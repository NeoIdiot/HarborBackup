using HarborBackup.OneDrive.Transport;
using HarborBackup.Windows.Integration;
using HarborBackup.Windows.Persistence.Configuration;

namespace HarborBackup.Windows.Tests;

internal sealed class FakeAccountSession(CloudAccountStore store) : ICloudAccountSession
{
    public string? LastBrowseId;
    public CloudStorageSelection? LastSelection;
    public bool FailApply;
    public bool FailBrowse;
    public bool Offline;
    public CloudAccountSettings Read() => store.Read();
    public Task<CloudAccountSettings> ConnectAsync(string? clientId, CancellationToken token) => throw new NotSupportedException();
    public Task<CloudAccountSettings> DisconnectAsync(CancellationToken token) => throw new NotSupportedException();
    public Task<CloudFolderListing> BrowseAsync(CloudAccountSettings account, string? folderId, CancellationToken token)
    {
        LastBrowseId = folderId;
        if (Offline) throw new CloudOfflineException();
        if (FailBrowse) throw new IOException("Synthetic offline failure");
        return Task.FromResult(new CloudFolderListing(
            new(folderId ?? "root", folderId is null or "root" ? "root" : account.RootName, "", 0, true, DisplayPath: folderId is null or "root" ? "" : account.RootName),
            [new("projects", "Projects", "", 0, true), new("archive", "Archive", "", 0, true)]));
    }
    public Task<CloudAccountSettings> ApplyAsync(CloudAccountSettings expected, CloudStorageSelection selection, CancellationToken token)
    {
        LastSelection = selection;
        if (FailApply) throw new IOException("Choose an empty folder or an existing account-mode repository.");
        var next = expected with { Enabled = true, RootId = selection.CreateNew ? "created-root" : selection.FolderId,
            RootName = selection.DisplayPath, RepositoryId = Guid.NewGuid().ToString("N"), Folders = selection.Folders,
            ArchiveRetention = selection.ArchiveRetention, ConflictRetention = selection.ConflictRetention };
        store.Save(next, expected);
        return Task.FromResult(next);
    }
}
