using HarborBackup.Core.Domain;
using HarborBackup.OneDrive.Transport;
using HarborBackup.Windows.Persistence.Configuration;

namespace HarborBackup.Windows.Integration;

public static class CloudProtectionMaintenance
{
    public static async Task<int> RunAsync(DataPaths data, CloudAccountSettings account, ICloudDrive drive, CancellationToken token)
    {
        CloudAccountStore.EnsureNoPending(data);
        var repository = new CloudRepository(drive, account, data);
        await repository.ValidateAsync(token);
        var store = new CloudProtectionStore(repository, drive, account, data);
        var count = 0;
        foreach (var kind in new[] { ProtectionKind.Archive, ProtectionKind.Conflict })
        {
            var area = kind == ProtectionKind.Archive ? "archives" : "conflicts";
            var root = await repository.FolderAsync("metadata/protection/" + area, false, token);
            if (root is null) continue;
            foreach (var project in await drive.ChildrenAsync(root.Id, token))
            {
                if (!project.Folder || !FileVersion.IsHash(project.Name)) throw new IOException("Invalid protection project index.");
                foreach (var file in await drive.ChildrenAsync(project.Id, token))
                {
                    if (!file.Folder || !FileVersion.IsHash(file.Name)) throw new IOException("Invalid protection file index.");
                    foreach (var item in await drive.ChildrenAsync(file.Id, token))
                    {
                        token.ThrowIfCancellationRequested();
                        var record = await repository.ReadJsonAsync<CloudProtectionRecord>(item, token);
                        store.Validate(record);
                        if (record.Kind != kind || record.Revision.Project != project.Name || DeviceIdentity.Digest(record.Revision.Path.ToUpperInvariant()) != file.Name || item.Name != record.Revision.Id + ".json")
                            throw new IOException("Protection index identity mismatch.");
                        await store.ApplyLocalAsync(record, token);
                        count++;
                    }
                }
            }
        }
        return count;
    }
}
