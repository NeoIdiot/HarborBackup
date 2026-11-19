using HarborBackup.Core.Domain;
using HarborBackup.OneDrive.Transport;
using HarborBackup.Windows.FileSystem;
using HarborBackup.Windows.Persistence;

namespace HarborBackup.Windows.Integration;

public sealed partial class CloudBackupSession
{
    private void SnapshotUploadJournal(string path, string text, CloudJournal j, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        if (AtomicJson.ReadText(path) != text) throw new IOException("Recovery record changed; evidence retained.");
        var root = Path.Combine(StateRoot, "retained-transactions");
        SafePath.Validate(root, config.Preview.StateDirectory);
        Directory.CreateDirectory(root);
        var retained = Path.Combine(root, j.Id + ".json");
        if (File.Exists(retained) && AtomicJson.ReadText(retained) != text)
            retained = Path.Combine(root, j.Id + "-" + DeviceIdentity.Digest(text) + ".json");
        SafePath.Validate(retained, config.Preview.StateDirectory);
        if (File.Exists(retained))
        {
            if (AtomicJson.ReadText(retained) != text) throw new IOException("Retained recovery evidence differs.");
            return;
        }
        // Keep the active journal until reconciliation completes; a crash still has a replayable intent.
        using (var output = new FileStream(retained, FileMode.CreateNew, FileAccess.Write, FileShare.None))
        using (var input = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read))
        { input.CopyTo(output); output.Flush(true); }
        if (AtomicJson.ReadText(retained) != text) throw new IOException("Recovery record changed; evidence retained.");
    }

    private async Task<bool> RetryBackupJournalAsync(string path, string text, CloudJournal j, CancellationToken token)
    {
        if (j.Local is null || Excluded(j.Path, false)) throw new IOException("Current local file cannot be retried.");
        if (j.Expected is { } expected && (expected.Folder || expected.ParentId != j.Parent) ||
            j.Remote is { } remote && (remote.SchemaVersion != 1 || remote.Directory ||
                remote.Project != CloudRepository.ProjectKey(project) || remote.Path != j.Path ||
                remote.ItemId != j.Expected?.Id || remote.ETag != j.Expected?.ETag ||
                !FileVersion.IsHash(remote.Version.Sha256) || remote.Version.Length != j.Expected?.Size))
            throw new IOException("Recovery upload identity mismatch; evidence retained.");
        var local = await new FileObserver().ObserveAsync(LocalPath(j.Path), null, new(), true, new(), token);
        if (local.Kind != ObservationKind.File) throw new IOException(local.Error ?? "Current local file is unavailable for retry.");
        SnapshotUploadJournal(path, text, j, token);

        var live = await drive.FindAsync(j.Parent, Path.GetFileName(j.Path), token);
        if (live?.Folder == true) throw new IOException("FileDirectoryCollision");
        RemoteItem? archived = null;
        if (j.ArchiveParent is not null && j.ArchiveName is not null && j.Expected is not null && j.Publish)
        {
            archived = await drive.FindAsync(j.ArchiveParent, j.ArchiveName, token);
            if (j.Archived is not null) RequireExpected(j.Archived, archived);
            if (archived is not null)
            {
                if (archived.Id != j.Expected.Id || archived.ParentId != j.ArchiveParent || j.Remote is null)
                    throw new IOException("Recovery archive identity changed; evidence retained.");
                await Repository.VerifyAsync(archived, j.Remote.Version, j.Expected.QuickXorHash, false, token);
                // A new replacement may rotate ordinary history. Preserve the interrupted operation's
                // original independently in conflicts, with deterministic identity for lost responses.
                var revision = j.Remote with { Id = DeviceIdentity.Digest(j.Id + "/retry-original")[..32],
                    ItemId = archived.Id, ETag = archived.ETag };
                await Repository.ProtectAsync(project, revision, ProtectionKind.Conflict, token);
                var index = AsRevision(j, archived, j.Remote.Version) with
                { Id = DeviceIdentity.Digest(j.Id + "/retry-archive-index")[..32], ContentHashKnown = j.Remote.ContentHashKnown };
                await Protection.SaveAsync(project.Destination,
                    j.Protection == ProtectionKind.Conflict ? ProtectionKind.Conflict : ProtectionKind.Archive, index, archived, token);
            }
        }
        if (j.Uploaded is not null && !j.Publish)
        {
            var uploaded = await drive.GetAsync(j.Uploaded.Id, token);
            RequireExpected(j.Uploaded, uploaded);
            if (uploaded.ParentId != (j.Publish ? j.Parent : j.ArchiveParent))
                throw new IOException("Recovery upload location changed; evidence retained.");
            await Repository.VerifyAsync(uploaded, j.Local, j.QuickXor ?? j.Uploaded.QuickXorHash, false, token);
            await Protection.SaveAsync(project.Destination, ProtectionKind.Conflict, AsRevision(j, uploaded, j.Local), uploaded, token);
        }
        else if (j.Publish && live is not null && (j.Phase == "Uploading" || j.Uploaded?.Id == live.Id) &&
            !string.IsNullOrEmpty(j.QuickXor) && live.QuickXorHash == j.QuickXor && live.Size == j.Local.Length)
        {
            // Complete upload with a lost receipt: verify it, then let the fresh upload protect it.
            await Repository.VerifyAsync(live, j.Local, j.QuickXor, false, token);
        }

        if (live is null && j.Publish && j.Expected is not null)
        {
            if (archived is null || j.Remote is null)
                throw new IOException("Original cloud file is missing without a verified archive; evidence retained.");
            // Restore the gap through the existing journaled, create-only recovery path. A concurrent
            // live version is never overwritten by rollback. The original journal was snapshotted first.
            await RestoreAsync(j with { Archived = archived }, token);
            if (File.Exists(path)) throw new IOException("Cloud restoration is not complete; evidence retained.");
        }
        if (File.Exists(path))
        {
            token.ThrowIfCancellationRequested();
            if (AtomicJson.ReadText(path) != text) throw new IOException("Recovery record changed; evidence retained.");
            File.Delete(path); // Exact snapshot is durable; no pending cloud mutation remains under this intent.
        }

        // Read current evidence after reconciliation. Never require the source to equal the old intent.
        var read = await ReadAsync(true, token);
        var file = read.Snapshot.Files.SingleOrDefault(f => f.RelativePath.Equals(j.Path, StringComparison.OrdinalIgnoreCase))
            ?? throw new IOException("Current local file is unavailable for retry.");
        var heads = read.Heads.GetValueOrDefault(file.RelativePath) ?? [];
        await using var prepared = await PrepareBackupAsync(file, heads, token, force: true);
        file = prepared.File;
        var decision = Decide(file, heads, SyncDirection.Backup, establishBaseline: true);
        if (decision.Action is not (FileAction.Copy or FileAction.Equal))
            throw new IOException("Current upload could not be verified: " + decision.Reason);
        await ApplyAsync(file, heads, SyncDirection.Backup, decision, token);
        FlushLocalCache();
        return decision.Action == FileAction.Copy;
    }
}
