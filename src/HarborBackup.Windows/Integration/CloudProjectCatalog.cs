using System.Text.Json;
using HarborBackup.OneDrive.Transport;
using HarborBackup.Windows.Persistence.Configuration;

namespace HarborBackup.Windows.Integration;

public sealed record CloudProjectTarget(string Destination, string FolderId, string ProjectId)
{
    public override string ToString() => Destination;
}
public static class CloudProjectCatalog
{
    public static async Task<IReadOnlyList<CloudProjectTarget>> ReadAsync(DataPaths data, CloudAccountSettings account,
        ICloudDrive drive, CancellationToken token)
    {
        var repository = new CloudRepository(drive, account, data);
        await repository.ValidateAsync(token);
        var root = await repository.FolderAsync("metadata/projects", false, token);
        var result = new Dictionary<string, CloudProjectTarget>(StringComparer.Ordinal);
        if (root is null) return [];
        var driveRoot = await drive.RootAsync(token);
        var parents = new Dictionary<string, RemoteItem>(StringComparer.Ordinal) { [driveRoot.Id] = driveRoot };
        async Task<RemoteItem> GetAsync(string id)
        {
            if (!parents.TryGetValue(id, out var value)) parents[id] = value = await drive.GetAsync(id, token);
            return value;
        }
        foreach (var folder in await drive.ChildrenAsync(root.Id, token))
        {
            if (!folder.Folder) continue;
            var location = CloudProjectRegistry.Latest(await new CloudProjectRegistry(repository, drive, account.DriveId!).ReadAsync(folder.Name, token));
            if (location is null) continue;
            var id = location.FolderId;
            try
            {
                var current = await GetAsync(id);
                var parts = new List<string>();
                var visited = new HashSet<string>(StringComparer.Ordinal);
                while (current.Id != driveRoot.Id)
                {
                    if (!current.Folder || current.Id == account.RootId || !visited.Add(current.Id) || current.ParentId is null)
                        throw new IOException("Invalid cloud project folder ancestry.");
                    parts.Add(current.Name);
                    current = await GetAsync(current.ParentId);
                }
                if (parts.Count == 0) throw new IOException("Invalid cloud project folder ancestry.");
                parts.Reverse();
                var destination = HarborBackup.Windows.FileSystem.PathRules.Relative(string.Join("/", parts), true);
                result[location.ProjectId] = new(destination, id, location.ProjectId);
            }
            catch (CloudRequestException ex) when (ex.StatusCode == System.Net.HttpStatusCode.NotFound)
            { /* A deleted target is not a new project. Its registration and recovery materials are retained. */ }
        }
        return result.Values.OrderBy(p => p.Destination, StringComparer.OrdinalIgnoreCase).ToArray();
    }
}
