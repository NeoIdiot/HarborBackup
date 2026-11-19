using HarborBackup.Core.Domain;
using HarborBackup.OneDrive.Transport;
using HarborBackup.OneDrive.Authentication;
using HarborBackup.Windows.Persistence.Configuration;
namespace HarborBackup.Windows.Integration;

public static class CloudConnection
{
    public static async Task<CloudAccountSettings> ConnectAsync(DataPaths data, string? clientId, CancellationToken token)
    {
        var resolvedClientId = OneDriveApplication.Resolve(clientId);
        var store = new CloudAccountStore(data); var old = store.Read();
        if (!string.Equals(old.ClientId, resolvedClientId, StringComparison.OrdinalIgnoreCase))
            CloudAccountStore.EnsureNoPending(data);
        var draft = old with { ClientId = resolvedClientId };
        var account = await store.Tokens(draft).ConnectAsync(token);
        draft = draft with { AccountId = account.Id, AccountName = account.Name, RequiresAuthorization = false };
        using var drive = new GraphDrive(store.Tokens(draft), "");
        var id = await drive.DriveIdAsync(token);
        // Changing account never inherits another account's root or repository identity.
        draft = draft with { DriveId = id, Enabled = old.Enabled && old.DriveId == id && old.AccountId == account.Id,
            RootId = old.DriveId == id ? old.RootId : null, RepositoryId = old.DriveId == id ? old.RepositoryId : null };
        store.Save(draft, old); return draft;
    }
    public static Task<CloudAccountSettings> BindAsync(DataPaths data, string parentId, string name, CancellationToken token, CloudFolders? folders = null)
    {
        var session = new CloudAccountSession(data);
        var old = session.Read();
        return session.ApplyAsync(old, new(true, parentId, name, name, folders ?? new(),
            old.ArchiveRetention, old.ConflictRetention), token);
    }

    internal static async Task<CloudAccountSettings> PrepareBindingAsync(DataPaths data, CloudAccountSettings old,
        ICloudDrive drive, RemoteItem root, string displayPath, CloudFolders folders,
        LocalRetention archiveRetention, LocalRetention conflictRetention, CancellationToken token)
    {
        if (!root.Folder) throw new IOException("Cloud repository root is not a folder.");
        var draft = old with { RootId = root.Id, RootName = root.DisplayPath?.Trim('/') ?? displayPath.Trim('/') };
        var repository = new CloudRepository(drive, draft, data);
        var existing = await drive.FindAsync(root.Id, "repository.json", token);
        const string legacyName = ".harborbackup-repository-v1.json";
        var legacy = await drive.FindAsync(root.Id, legacyName, token);
        CloudRepository.RepositoryIdentity identity;
        if (existing is null && legacy is null)
        {
            if (old.Enabled && old.RootId == root.Id) throw new IOException("Missing cloud repository identity.");
            if ((await drive.ChildrenAsync(root.Id, token)).Count != 0) throw new IOException("Choose an empty folder or an existing account-mode repository.");
            identity = new(2, Guid.NewGuid().ToString("N"), folders);
            var marker = await repository.JsonAsync(root.Id, "repository.json", identity, token);
            identity = await repository.ReadIdentityAsync(marker, token);
        }
        else
        {
            identity = await repository.ReadIdentityAsync(existing ?? legacy!, token);
            if (old.Enabled && old.RootId == root.Id &&
                (identity.RepositoryId != old.RepositoryId || (identity.Folders ?? new CloudFolders()) != (old.Folders ?? new CloudFolders())))
                throw new IOException("Cloud repository identity mismatch. No write performed.");
            if (existing is null && identity.SchemaVersion != 1) throw new IOException("Invalid repository upgrade evidence.");
            if (identity.SchemaVersion == 1)
            {
                // Conditional move preserves the exact old marker. A missing marker after a
                // lost response is resumed from this evidence on the next explicit Apply.
                if (existing is not null)
                {
                    if (legacy is not null) throw new IOException("Repository upgrade backup already exists; retained for review.");
                    legacy = await drive.MoveAsync(existing, root.Id, legacyName, null, token);
                    var preserved = await repository.ReadIdentityAsync(legacy, token);
                    if (preserved != identity) throw new IOException("Repository identity changed during upgrade.");
                }
                identity = identity with { SchemaVersion = 2, Folders = identity.Folders ?? new CloudFolders() };
                var marker = await repository.JsonAsync(root.Id, "repository.json", identity, token);
                var confirmed = await repository.ReadIdentityAsync(marker, token);
                if (confirmed != identity) throw new IOException("Repository upgrade verification failed.");
            }
        }
        if (identity.SchemaVersion != 2) throw new IOException("Unsupported repository identity.");
        draft = draft with { RepositoryId = identity.RepositoryId, Folders = identity.Folders ?? new CloudFolders(), Enabled = true,
            ArchiveRetention = archiveRetention, ConflictRetention = conflictRetention, RequiresAuthorization = false };
        return draft;
    }
}
