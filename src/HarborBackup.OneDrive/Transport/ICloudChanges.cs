namespace HarborBackup.OneDrive.Transport;

// A drive-wide delta cursor is only an inventory cache invalidator, never a lock or commit predicate.
public sealed record CloudChange(string Id, string? Name, string? ParentId, bool Folder, bool Deleted,
    string? ETag = null, long? Size = null);
public sealed record CloudChanges(string Cursor, bool Changed, IReadOnlyList<CloudChange>? Items = null, bool ResyncRequired = false);
public interface ICloudChanges
{
    Task<CloudChanges> ChangesAsync(string? cursor, CancellationToken token);
}
