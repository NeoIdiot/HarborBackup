using HarborBackup.Core.Domain;
using HarborBackup.OneDrive.Authentication;
using HarborBackup.OneDrive.Transport;
using HarborBackup.Windows.Persistence.Configuration;

namespace HarborBackup.Windows.Integration;

public sealed record CloudFolderListing(RemoteItem Current, IReadOnlyList<RemoteItem> Folders);
public sealed record CloudStorageSelection(bool CreateNew, string FolderId, string Name, string DisplayPath,
    CloudFolders Folders, LocalRetention ArchiveRetention, LocalRetention ConflictRetention)
{
    public bool RetentionOnly { get; init; }
}

public interface ICloudAccountSession
{
    CloudAccountSettings Read();
    Task<CloudAccountSettings> ConnectAsync(string? clientId, CancellationToken token);
    Task<CloudAccountSettings> DisconnectAsync(CancellationToken token);
    Task<CloudFolderListing> BrowseAsync(CloudAccountSettings account, string? folderId, CancellationToken token);
    Task<CloudAccountSettings> ApplyAsync(CloudAccountSettings expected, CloudStorageSelection selection, CancellationToken token);
}

public sealed class CloudAccountSession(DataPaths data, Func<CloudAccountSettings, ICloudDrive>? driveFactory = null) : ICloudAccountSession
{
    private CloudAccountStore Store => new(data);
    private ICloudDrive Open(CloudAccountSettings account) => driveFactory?.Invoke(account) ??
        new GraphDrive(Store.Tokens(account), account.DriveId ?? "", options: Store.TransferOptions());
    public CloudAccountSettings Read() => Store.Read();
    public Task<CloudAccountSettings> ConnectAsync(string? clientId, CancellationToken token) => CloudConnection.ConnectAsync(data, clientId, token);
    public Task<CloudAccountSettings> DisconnectAsync(CancellationToken token) => Task.Run(() =>
    {
        using var guard = new RunLock();
        var old = Read();
        token.ThrowIfCancellationRequested();
        Store.Tokens(old).DisconnectAsync().GetAwaiter().GetResult();
        var next = old with { RequiresAuthorization = true };
        Store.Save(next, old);
        return next;
    }, token);
    public async Task<CloudFolderListing> BrowseAsync(CloudAccountSettings account, string? folderId, CancellationToken token)
    {
        using var drive = Open(account);
        try
        {
            var current = folderId is null ? await drive.RootAsync(token) : await drive.GetAsync(folderId, token);
            if (!current.Folder) throw new IOException("Cloud repository root is not a folder.");
            return new(current, (await drive.ChildrenAsync(current.Id, token)).Where(i => i.Folder).ToArray());
        }
        catch (AccountAuthorizationRequiredException)
        {
            if (Read() == account) Store.Save(account with { RequiresAuthorization = true }, account);
            throw;
        }
    }
    public Task<CloudAccountSettings> ApplyAsync(CloudAccountSettings expected, CloudStorageSelection selection, CancellationToken token) => Task.Run(() =>
    {
        using var guard = new RunLock();
        // Reject stale account/drive selections before any cloud mutation.
        if (Read() != expected) throw new IOException("OneDrive settings changed. Reload before saving.");
        if (!Enum.IsDefined(selection.ArchiveRetention) || !Enum.IsDefined(selection.ConflictRetention))
            throw new FormatException("Invalid retention policy.");
        selection.Folders.Validate();
        if (selection.RetentionOnly)
        {
            if (!expected.Enabled || selection.CreateNew || selection.FolderId != expected.RootId ||
                selection.Folders != (expected.Folders ?? new CloudFolders()))
                throw new InvalidOperationException("Retention-only saves cannot change the repository binding or layout.");
            // A retention preference changes local configuration only; it does not rebind
            // the repository or require a live token/listing or completed transactions.
            token.ThrowIfCancellationRequested();
            var policy = expected with { ArchiveRetention = selection.ArchiveRetention, ConflictRetention = selection.ConflictRetention };
            Store.Save(policy, expected);
            return policy;
        }
        if (expected.AccountId is null || expected.DriveId is null || expected.RequiresAuthorization)
            throw new InvalidOperationException("Connect an account first.");
        CloudAccountStore.EnsureNoPending(data);
        using var drive = Open(expected);
        async Task<CloudAccountSettings> PrepareAsync()
        {
        RemoteItem root;
        if (selection.CreateNew)
        {
            var name = FileSystem.PathRules.Relative(selection.Name);
            if (name.Contains('/')) throw new FormatException("Enter a single repository folder name.");
            // Existing contents are still validated below; retry after a lost response is safe.
            root = await drive.FolderAsync(selection.FolderId, name, token);
        }
        else root = await drive.GetAsync(selection.FolderId, token);
        return await CloudConnection.PrepareBindingAsync(data, expected, drive, root, selection.DisplayPath,
            selection.Folders, selection.ArchiveRetention, selection.ConflictRetention, token);
        }
        // Wait on the worker, then commit on its mutex-owning thread. No UI blocking.
        var next = PrepareAsync().GetAwaiter().GetResult();
        Store.Save(next, expected);
        return next;
    }, token);
}
