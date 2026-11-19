using HarborBackup.Core.Domain;
using HarborBackup.OneDrive.Transport;
using HarborBackup.Windows.FileSystem;
using HarborBackup.Windows.Persistence.Configuration;

namespace HarborBackup.Windows.Integration;

public sealed record CloudProtectionRecord(int SchemaVersion, string Repository, string Destination,
    ProtectionKind Kind, CloudRevision Revision, RemoteItem Item);

/// <summary>Readable protection files; their indexes live only in metadata.</summary>
internal sealed class CloudProtectionStore(CloudRepository repository, ICloudDrive drive, CloudAccountSettings account, DataPaths data)
{
    private static string Area(ProtectionKind kind) => kind switch
    {
        ProtectionKind.Archive => "archives", ProtectionKind.Conflict => "conflicts",
        _ => throw new ArgumentException("Invalid protection kind.")
    };
    private static string IndexArea(ProtectionKind kind, string projectId, string path) =>
        "metadata/protection/" + Area(kind) + "/" + projectId + "/" + DeviceIdentity.Digest(path.ToUpperInvariant());
    public async Task<RemoteItem> ParentAsync(ProtectionKind kind, string destination, string path, CancellationToken token)
    {
        var relative = PathRules.Relative(destination, true) + "/" + PathRules.Relative(path);
        var folder = relative.Contains('/') ? relative[..relative.LastIndexOf('/')] : "";
        return (await repository.FolderAsync(Area(kind) + "/" + folder, true, token))!;
    }
    public static string ConflictName(string path, string device, string id) =>
        Path.GetFileNameWithoutExtension(path) + "." + device[..Math.Min(8, device.Length)] + "." + id + Path.GetExtension(path);
    public async Task SaveAsync(string destination, ProtectionKind kind, CloudRevision revision, RemoteItem item, CancellationToken token)
    {
        var folder = (await repository.FolderAsync(IndexArea(kind, revision.Project, revision.Path), true, token))!;
        var record = new CloudProtectionRecord(3, account.RepositoryId!, PathRules.Relative(destination, true), kind,
            revision with { ItemId = item.Id, ETag = item.ETag }, item);
        await repository.JsonAsync(folder.Id, revision.Id + ".json", record, token);
        await ApplyLocalAsync(record, token);
    }
    public async Task<RemoteItem> CopyAsync(ProjectDefinition project, CloudRevision revision, ProtectionKind kind, CancellationToken token)
    {
        var parent = await ParentAsync(kind, project.Destination, revision.Path, token);
        var name = kind == ProtectionKind.Conflict ? ConflictName(revision.Path, revision.Device, revision.Id) :
            Path.GetFileName(revision.Path);
        var original = await drive.GetAsync(revision.ItemId, token);
        if (original.Size != revision.Version.Length || original.ETag != revision.ETag) throw new IOException("Cloud protection source changed.");
        var copy = await drive.CopyAsync(original, parent.Id, name, token);
        await repository.VerifyAsync(copy, revision.Version, original.QuickXorHash, false, token);
        await SaveAsync(project.Destination, kind, revision, copy, token);
        return copy;
    }
    public async Task<IReadOnlyList<(RemoteItem Index, CloudProtectionRecord Record)>> RecordsAsync(ProtectionKind kind, string projectId, string destination, string path, CancellationToken token)
    {
        var folder = await repository.FolderAsync(IndexArea(kind, projectId, path), false, token);
        var records = new List<(RemoteItem, CloudProtectionRecord)>();
        if (folder is null) return records;
        foreach (var item in await drive.ChildrenAsync(folder.Id, token))
        {
            var record = await repository.ReadJsonAsync<CloudProtectionRecord>(item, token);
            Validate(record);
            if (record.Revision.Project != projectId || record.Kind != kind || !record.Revision.Path.Equals(path, StringComparison.OrdinalIgnoreCase) ||
                item.Name != record.Revision.Id + ".json") throw new IOException("Protection index identity mismatch.");
            records.Add((item, record));
        }
        return records;
    }
    public void Validate(CloudProtectionRecord record)
    {
        if (record.SchemaVersion != 3 || record.Repository != account.RepositoryId ||
            record.Kind is not (ProtectionKind.Archive or ProtectionKind.Conflict) ||
            PathRules.Relative(record.Destination, true) != record.Destination ||
            PathRules.Relative(record.Revision.Path) != record.Revision.Path ||
            !FileVersion.IsHash(record.Revision.Project) ||
            !Guid.TryParseExact(record.Revision.Id, "N", out _) ||
            record.Item.Id != record.Revision.ItemId || record.Item.Size != record.Revision.Version.Length ||
            !FileVersion.IsHash(record.Revision.Version.Sha256))
            throw new IOException("Protection index identity mismatch.");
    }
    public async Task ApplyLocalAsync(CloudProtectionRecord record, CancellationToken token)
    {
        Validate(record);
        if (!record.Revision.ContentHashKnown) return;
        var relativeParent = Path.GetDirectoryName(record.Revision.Path) ?? "";
        var root = record.Kind == ProtectionKind.Archive ? data.Archives : data.Conflicts;
        var target = Path.Combine(root, record.Destination, relativeParent, record.Item.Name);
        SafePath.Validate(target, root);
        var mode = record.Kind == ProtectionKind.Archive ? account.ArchiveRetention : account.ConflictRetention;
        if (mode == LocalRetention.OnlineOnly && !File.Exists(target)) return;
        var current = await drive.GetAsync(record.Item.Id, token);
        if (current.ETag != record.Item.ETag || current.Size != record.Item.Size) throw new IOException("Cloud protection changed; retained for review.");
        var parent = await ParentAsync(record.Kind, record.Destination, record.Revision.Path, token);
        if (current.ParentId != parent.Id || current.Name != record.Item.Name) throw new IOException("Protection mapping changed.");
        if (mode == LocalRetention.KeepLocal) await repository.DownloadVerifiedAsync(current, record.Revision.Version, target, token);
        else if (File.Exists(target))
        {
            var local = await new FileObserver().ObserveAsync(target, null, new(), true, new(), token);
            if (local.Version?.Sha256 != record.Revision.Version.Sha256) throw new IOException("Local protection differs; refusing removal.");
            await using (var source = new FileStream(target, FileMode.Open, FileAccess.Read, FileShare.Read))
                await repository.VerifyAsync(current, record.Revision.Version, await QuickXor.ComputeAsync(source, token), false, token);
            var quarantine = target + "." + Guid.NewGuid().ToString("N") + ".verified-removal";
            File.Move(target, quarantine);
            var moved = await new FileObserver().ObserveAsync(quarantine, null, new(), true, new(), token);
            if (moved.Version?.Sha256 != record.Revision.Version.Sha256)
                throw new IOException("Concurrent local content retained for review: " + quarantine);
            File.Delete(quarantine);
        }
    }
    // Cleanup only the exact predecessor recorded before this transaction.
    public async Task RemovePreviousAsync(string projectId, string destination, string path, RemoteItem previous, CancellationToken token)
    {
        var parent = await ParentAsync(ProtectionKind.Archive, destination, path, token);
        var live = await drive.FindAsync(parent.Id, Path.GetFileName(path), token);
        if (live is not null)
        {
            if (live.Id != previous.Id || live.ETag != previous.ETag) throw new IOException("Previous history changed; cleanup stopped.");
            await drive.DeleteAsync(live, token);
        }
        foreach (var (index, record) in await RecordsAsync(ProtectionKind.Archive, projectId, destination, path, token))
        {
            if (record.Item.Id != previous.Id) continue;
            if (!record.Revision.Path.Equals(path, StringComparison.OrdinalIgnoreCase) || record.Item.ETag != previous.ETag)
                throw new IOException("Previous history index differs.");
            var local = Path.Combine(data.Archives, destination, path);
            SafePath.Validate(local, data.Archives);
            if (File.Exists(local))
            {
                var version = await new FileObserver().ObserveAsync(local, null, new(), true, new(), token);
                if (version.Version?.Sha256 == record.Revision.Version.Sha256) File.Delete(local);
                else throw new IOException("Local history differs; retained for review.");
            }
            await drive.DeleteAsync(index, token);
        }
    }
}
