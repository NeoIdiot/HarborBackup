namespace HarborBackup.OneDrive.Transport;

public sealed record RemoteItem(string Id, string Name, string ETag, long Size, bool Folder, string? WebUrl = null, string? QuickXorHash = null, string? DisplayPath = null, string? ParentId = null, DateTimeOffset? ModifiedUtc = null, string? ContentTag = null);
public sealed record TransferSession(string Url, DateTimeOffset ExpiresUtc, long Offset,
    string? StagingName = null, string? CompletedItemId = null, string? CompletedETag = null)
{
    public static bool IsTemporaryName(string name)
    {
        const string prefix = ".harbor-upload-";
        return name.StartsWith(prefix, StringComparison.Ordinal) && name.EndsWith(".tmp", StringComparison.Ordinal) &&
            Guid.TryParseExact(name[prefix.Length..^4], "N", out _);
    }
}
public interface ICloudDrive : IDisposable
{
    Task<RemoteItem> RootAsync(CancellationToken token);
    Task<RemoteItem?> FindAsync(string parent, string name, CancellationToken token);
    Task<RemoteItem> GetAsync(string id, CancellationToken token);
    Task<IReadOnlyList<RemoteItem>> ChildrenAsync(string parent, CancellationToken token);
    Task<RemoteItem> FolderAsync(string parent, string name, CancellationToken token);
    Task<RemoteItem> UploadAsync(string parent, string name, Stream source, TransferSession? session,
        Action<TransferSession?> checkpoint, CancellationToken token, bool reconcileExisting = true);
    Task DownloadAsync(RemoteItem item, Stream destination, CancellationToken token);
    Task DeleteAsync(RemoteItem item, CancellationToken token);
    Task<RemoteItem> MoveAsync(RemoteItem source, string parent, string name, DateTimeOffset? modifiedUtc, CancellationToken token);
    Task<RemoteItem> SetModifiedAsync(RemoteItem item, DateTimeOffset modified, CancellationToken token);
    Task<RemoteItem> CopyAsync(RemoteItem source, string parent, string name, CancellationToken token);
}
