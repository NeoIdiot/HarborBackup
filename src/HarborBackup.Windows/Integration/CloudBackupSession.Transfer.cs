using System.Text.Json;
using HarborBackup.Core.Domain;
using HarborBackup.OneDrive.Transport;
using HarborBackup.Windows.FileSystem;
using HarborBackup.Windows.Persistence;

namespace HarborBackup.Windows.Integration;

public sealed partial class CloudBackupSession
{
    private static string HistoryPendingName(CloudJournal j) => ".harbor-history-" + j.Id + ".tmp";
    private CloudRevision AsRevision(CloudJournal j, RemoteItem item, FileVersion version) => new(1, j.Id,
        CloudRepository.ProjectKey(project), j.Path, config.Preview.DeviceId, [], item.Id, item.ETag, version, j.CreatedUtc);
    private async Task CommitAsync(CloudFileMetadata metadata, CancellationToken token, bool reconcileExisting = false)
    {
        await targetGate.WaitAsync(token);
        try { await TargetCoreAsync(false, token); await RegisterTargetAsync(token); }
        finally { targetGate.Release(); }
        var folder = (await Repository.FolderAsync(MetadataArea, true, token))!;
        var evidence = await Repository.JsonAsync(folder.Id, metadata.Revision.Id + ".json", metadata, token, reconcileExisting);
        metadataSources[evidence.Id] = evidence;
        known[metadata.Revision.Path] = metadata;
    }
    private async Task<CloudJournal> PrepareProtectionAsync(CloudJournal j, ProtectionKind kind, CancellationToken token)
    {
        if (j.ArchiveParent is not null) return j;
        var parent = await Protection.ParentAsync(kind, project.Destination, j.Path, token);
        var previous = kind == ProtectionKind.Archive ? await drive.FindAsync(parent.Id, Path.GetFileName(j.Path), token) : null;
        if (previous?.Folder == true) throw new IOException("History path is a directory; no replacement performed.");
        if (previous is not null)
        {
            var records = await Protection.RecordsAsync(kind, project.ProjectId, project.Destination, j.Path, token);
            if (!records.Any(r => r.Record.Item.Id == previous.Id && r.Record.Item.ETag == previous.ETag &&
                r.Record.Revision.Path.Equals(j.Path, StringComparison.OrdinalIgnoreCase)))
                throw new IOException("History file has no matching metadata; retained for review.");
        }
        j = j with { ArchiveParent = parent.Id, PreviousHistory = previous, ArchiveName = kind == ProtectionKind.Conflict
            ? CloudProtectionStore.ConflictName(j.Path, config.Preview.DeviceId, j.Id)
            : previous is null ? Path.GetFileName(j.Path) : HistoryPendingName(j) };
        Save(j); return j;
    }
    private async Task<CloudJournal> ArchiveCloudAsync(CloudJournal j, CancellationToken token)
    {
        if (j.Expected is null || j.Archived is not null) return j;
        if (j.Remote is { ContentHashKnown: false })
            await Repository.VerifyAsync(j.Expected, j.Remote.Version, j.Expected.QuickXorHash, false, token);
        var reconcileArchive = j.ArchiveParent is not null;
        j = await PrepareProtectionAsync(j, j.Protection == ProtectionKind.Conflict ? ProtectionKind.Conflict : ProtectionKind.Archive, token);
        Report(BackupPhase.Protecting, j.Path);
        var archived = reconcileArchive ? await drive.FindAsync(j.ArchiveParent!, j.ArchiveName!, token) : null;
        if (archived is null)
        {
            try
            {
                // The conditional move checks the scanned version at the actual write.
                archived = await drive.MoveAsync(j.Expected!, j.ArchiveParent!, j.ArchiveName!, null, token);
            }
            catch (IOException)
            {
                var moved = await drive.FindAsync(j.ArchiveParent!, j.ArchiveName!, token);
                if (moved?.Id != j.Expected!.Id)
                {
                    var latest = await drive.FindAsync(j.Parent, Path.GetFileName(j.Path), token);
                    try { RequireExpected(j.Expected, latest); }
                    catch (IOException) { await PreserveLosingLocalAsync(j, token); throw; }
                }
                throw; // Lost responses and genuine I/O failures retain the original recovery path.
            }
        }
        if (archived.Id != j.Expected!.Id) throw new IOException("History destination changed; source preserved.");
        await Repository.VerifyAsync(archived, j.Remote!.Version, j.Expected.QuickXorHash, false, token);
        j = j with { Archived = archived, Phase = "Archived" }; Save(j);
        return j;
    }
    private async Task<CloudJournal> UploadLocalAsync(CloudJournal j, bool liveTarget, CancellationToken token)
    {
        if (j.Uploaded is not null) return j;
        if (j.Local is null) throw new IOException("No local content to upload.");
        // A lost upload response can outlive subsequent source edits. Reconcile the complete
        // remote version before requiring the original source bytes to still exist locally.
        if (j.Phase == "Uploading")
        {
            var completed = await drive.FindAsync(liveTarget ? j.Parent : j.ArchiveParent!,
                liveTarget ? Path.GetFileName(j.Path) : j.ArchiveName!, token);
            if (completed is not null && completed.Size == j.Local.Length && !string.IsNullOrEmpty(j.QuickXor) &&
                completed.QuickXorHash == j.QuickXor)
            {
                await Repository.VerifyAsync(completed, j.Local, j.QuickXor, false, token);
                j = j with { Uploaded = completed, Session = null, Phase = "Uploaded" }; Save(j);
                return j;
            }
        }
        var path = LocalPath(j.Path);
        var prepared = preparedSources.GetValueOrDefault(j.Path);
        await using var owned = prepared is null
            ? new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 131072, true) : null;
        var stream = prepared?.Stream ?? owned!;
        var modified = File.GetLastWriteTimeUtc(path);
        if (modified > DateTime.UtcNow.AddMinutes(5)) throw new IOException("FutureModificationTime");
        var hashes = prepared?.Hashes ?? await QuickXor.ComputeTransferHashesAsync(stream, token);
        if (hashes.Sha256 != j.Local.Sha256 || hashes.Length != j.Local.Length ||
            File.GetLastWriteTimeUtc(path) != modified)
        {
            if (liveTarget && j.Expected is not null && j.ArchiveParent is not null) await RestoreAsync(j, token);
            throw new IOException("Source changed before upload; evidence retained.");
        }
        stream.Position = 0;
        // Normal prepared uploads already persisted both checksums before any remote write.
        if (j.QuickXor != hashes.QuickXor) { j = j with { QuickXor = hashes.QuickXor }; Save(j); }
        try
        {
            if (liveTarget) j = await ArchiveCloudAsync(j, token);
            else j = await PrepareProtectionAsync(j, j.Publish && j.Protection != ProtectionKind.Conflict ? ProtectionKind.Archive : ProtectionKind.Conflict, token);
            var parent = liveTarget ? j.Parent : j.ArchiveParent!;
            var name = liveTarget ? Path.GetFileName(j.Path) : j.ArchiveName!;
            var recoveringUpload = j.Phase == "Uploading";
            var existing = recoveringUpload ? await drive.FindAsync(parent, name, token) : null;
            if (existing is not null)
            {
                if (existing.Size != j.Local!.Length || !string.IsNullOrEmpty(existing.QuickXorHash) && existing.QuickXorHash != j.QuickXor)
                    await PreserveLosingLocalAsync(j, token);
                // A lost response is accepted only when the complete content is the expected version.
                await Repository.VerifyAsync(existing, j.Local!, j.QuickXor, false, token);
                j = j with { Uploaded = existing, Session = null, Phase = "Uploaded" }; Save(j);
                return j;
            }
            j = j with { Phase = "Uploading" }; Save(j);
            Report(BackupPhase.Uploading, j.Path);
            var elapsed = System.Diagnostics.Stopwatch.StartNew();
            RemoteItem item;
            try
            {
                item = await drive.UploadAsync(parent, name, stream, Unseal(j.Session), value =>
                { j = j with { Session = value is null ? null : Seal(value) }; Save(j); }, token, reconcileExisting: false);
            }
            finally { if (Metrics is { } metrics) lock (metrics) metrics.CloudUploadMilliseconds += elapsed.ElapsedMilliseconds; }
            Report(BackupPhase.Verifying, j.Path);
            await Repository.VerifyAsync(item, j.Local!, j.QuickXor, false, token);
            if (liveTarget)
                item = await drive.SetModifiedAsync(item, j.Local!.LastWriteTimeUtc, token);
            j = j with { Uploaded = item, Session = null, Phase = "Uploaded" }; Save(j);
            if (Metrics is { } completed) lock (completed) { completed.CloudUploadedFiles++; completed.CloudUploadedBytes += item.Size; }
            return j;
        }
        catch (CloudRequestException ex) when (liveTarget && (int)ex.StatusCode is 409 or 412)
        { await PreserveLosingLocalAsync(j, token); throw; }
        catch
        {
            if (liveTarget && j.Expected is not null && File.Exists(JournalPath(j.Id)))
            {
                // Reconcile a lost archive response before attempting rollback.
                using var rollback = new CancellationTokenSource(TimeSpan.FromSeconds(15));
                try
                {
                    var saved = JsonSerializer.Deserialize<CloudJournal>(AtomicJson.ReadText(JournalPath(j.Id))!, AtomicJson.Options)!;
                    await RestoreAsync(saved, rollback.Token);
                }
                catch (Exception ex) when (ex is IOException or HttpRequestException or OperationCanceledException or DeferredOperationException)
                { /* Journal and cloud history remain authoritative for recovery. */ }
            }
            throw;
        }
    }
    private async Task PreserveLosingLocalAsync(CloudJournal j, CancellationToken token)
    {
        if (j.Archived is not null) await FinishHistoryAsync(j, j.Archived, j.Remote!.Version, token);
        // The live competing file stays in place; only the losing local content needs a copy.
        var competing = await drive.FindAsync(j.Parent, Path.GetFileName(j.Path), token);
        j = j with { Publish = false, Protection = ProtectionKind.Conflict, Expected = competing, Remote = null,
            Archived = null, Uploaded = null, ArchiveParent = null, ArchiveName = null, PreviousHistory = null,
            Phase = "Prepared", Session = null };
        Save(j);
        j = await UploadLocalAsync(j, false, token);
        await FinishHistoryAsync(j, j.Uploaded!, j.Local!, token);
        File.Delete(JournalPath(j.Id));
        throw new DeferredOperationException("Cloud target changed concurrently. Local content was preserved in conflicts; no replacement performed.");
    }
    private async Task RestoreAsync(CloudJournal j, CancellationToken token)
    {
        if (j.Archived is null && j.ArchiveParent is not null && j.ArchiveName is not null)
        {
            var candidate = await drive.FindAsync(j.ArchiveParent, j.ArchiveName, token);
            if (candidate?.Id == j.Expected?.Id) { j = j with { Archived = candidate }; Save(j); }
        }
        if (j.Archived is null) return;
        var current = await drive.FindAsync(j.Parent, Path.GetFileName(j.Path), token);
        if (current is not null)
        {
            if (j.Phase is "Restoring" or "Restored") await CompleteRestoreAsync(j, current, token);
            return; // Never replace another device's version, including a completed lost upload.
        }
        j = j with { Phase = "Restoring" }; Save(j);
        var archived = await drive.GetAsync(j.Archived.Id, token);
        RequireExpected(j.Archived, archived);
        var restored = await drive.CopyAsync(archived, j.Parent, Path.GetFileName(j.Path), token);
        await Repository.VerifyAsync(restored, j.Remote!.Version, archived.QuickXorHash, false, token);
        j = j with { Phase = "Restored", RestoredItem = restored }; Save(j);
        await CompleteRestoreAsync(j, restored, token);
    }
    private async Task CompleteRestoreAsync(CloudJournal j, RemoteItem restored, CancellationToken token)
    {
        await Repository.VerifyAsync(restored, j.Remote!.Version, j.Archived!.QuickXorHash, false, token);
        var revision = j.Remote with { Id = DeviceIdentity.Digest(j.Id + "/restore")[..32],
            ItemId = restored.Id, ETag = restored.ETag, CreatedUtc = j.CreatedUtc };
        if (revision.ContentHashKnown) await CommitAsync(new(2, revision, restored), token, reconcileExisting: true);
        // The restored old version remains available in history as well.
        await FinishHistoryAsync(j, j.Archived, j.Remote.Version, token);
        File.Delete(JournalPath(j.Id));
    }
    private async Task<RemoteItem> FinishHistoryAsync(CloudJournal j, RemoteItem item, FileVersion version, CancellationToken token)
    {
        var kind = j.Publish && j.Protection != ProtectionKind.Conflict ? ProtectionKind.Archive : ProtectionKind.Conflict;
        var current = await drive.GetAsync(item.Id, token);
        if (current.ParentId != j.ArchiveParent || current.Size != version.Length)
            throw new IOException("Protection file changed; retained for review.");
        await Repository.VerifyAsync(current, version, item.QuickXorHash, false, token);
        if (kind == ProtectionKind.Archive && current.Name != Path.GetFileName(j.Path))
        {
            if (current.Name != HistoryPendingName(j)) throw new IOException("Unexpected history filename.");
            if (j.PreviousHistory is not null) await Protection.RemovePreviousAsync(project.ProjectId, project.Destination, j.Path, j.PreviousHistory, token);
            current = await drive.MoveAsync(current, j.ArchiveParent!, Path.GetFileName(j.Path), null, token);
            // Preserve the acknowledged rename before publishing its metadata. A failed metadata
            // response must not make our own history rotation look like an external archive edit.
            Save(j with { HistoryFinishedItem = current });
        }
        var revision = AsRevision(j, current, version);
        if (j.Remote is { ContentHashKnown: false } && item.Id == j.Archived?.Id)
            revision = revision with { ContentHashKnown = false };
        await Protection.SaveAsync(project.Destination, kind, revision, current, token);
        return current;
    }
    private async Task ContinueAsync(CloudJournal j, CancellationToken token, bool recovery = false)
    {
        Report(BackupPhase.Recovery, j.Path);
        if (j.Phase is "Restoring" or "Restored")
        { await RestoreAsync(j, token); return; }
        if (j.Committed is not null)
        { await CommitAsync(j.Committed, token, reconcileExisting: true); await FinishAsync(j, token); return; }
        var current = j.Direction == SyncDirection.Pull || !j.Publish
            ? await drive.FindAsync(j.Parent, Path.GetFileName(j.Path), token) : null;
        if (j.Direction == SyncDirection.Pull && j.Publish)
        {
            RequireExpected(j.Expected, current);
            // A local common-baseline version has no unuploaded edits. The verified local
            // replacement keeps rollback bytes until completion; re-uploading it would regress shared history.
            if (j.Local is not null && BaselineHash(j.Path) != j.Local.Sha256)
                j = await UploadLocalAsync(j, false, token);
            var target = LocalPath(j.Path);
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            var temp = Path.Combine(Path.GetDirectoryName(target)!, ".harborbackup-" + j.Id + ".tmp");
            var rollback = temp + ".previous";
            SafePath.Validate(temp, project.Source); SafePath.Validate(rollback, project.Source);
            var local = await new FileObserver().ObserveAsync(target, null, new(), true, new(), token);
            if (local.Version?.Sha256 != j.Local?.Sha256 && !(j.Remote!.ContentHashKnown && local.Version?.Sha256 == j.Remote.Version.Sha256) && local.Version?.Sha256 != j.Incoming?.Sha256)
                throw new IOException("Local file changed during pull; replacement stopped.");
            Report(BackupPhase.Processing, j.Path);
            FileVersion incoming;
            if (File.Exists(temp)) File.Delete(temp); // Owned, reproducible transfer output; never a rollback.
            await using (var output = new FileStream(temp, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None, 131072, true))
            {
                await drive.DownloadAsync(current!, output, token);
                output.Position = 0;
                var hashes = await QuickXor.ComputeTransferHashesAsync(output, token);
                var hash = hashes.Sha256; var quick = hashes.QuickXor;
                if (output.Length != current!.Size || j.Remote!.ContentHashKnown && hash != j.Remote.Version.Sha256 ||
                    !string.IsNullOrEmpty(current.QuickXorHash) && quick != current.QuickXorHash)
                    throw new IOException("Downloaded content mismatch; source preserved.");
                incoming = new(hash, output.Length, current.ModifiedUtc ?? DateTimeOffset.UtcNow, DateTimeOffset.UtcNow);
                output.Flush(true);
            }
            j = j with { Incoming = incoming }; Save(j);
            RequireExpected(j.Expected, await drive.FindAsync(j.Parent, Path.GetFileName(j.Path), token));
            local = await new FileObserver().ObserveAsync(target, null, new(), true, new(), token);
            if (local.Version?.Sha256 != incoming.Sha256)
            {
                if (local.Version?.Sha256 != j.Local?.Sha256 || File.Exists(rollback)) throw new IOException("Concurrent local content or rollback retained.");
                File.SetLastWriteTimeUtc(temp, incoming.LastWriteTimeUtc.UtcDateTime);
                if (j.Local is null) File.Move(temp, target); else File.Replace(temp, target, rollback);
            }
            if (File.Exists(rollback))
            {
                var old = await new FileObserver().ObserveAsync(rollback, null, new(), true, new(), token);
                if (old.Version?.Sha256 != j.Local?.Sha256) throw new IOException("Concurrent local edit retained in rollback.");
                File.Delete(rollback);
            }
            if (File.Exists(temp)) File.Delete(temp);
            j = j with { Committed = new(2, AsRevision(j, current!, incoming), current!), Phase = "Published" };

        }
        else if (!j.Publish)
        {
            RequireExpected(j.Expected, current);
            if (j.Remote is { Directory: false } remote)
                await Repository.ProtectAsync(project, remote, ProtectionKind.Conflict, token);
            if (j.Local is not null)
            {
                j = await UploadLocalAsync(j, false, token);
                await FinishHistoryAsync(j, j.Uploaded!, j.Local!, token);
            }
            File.Delete(JournalPath(j.Id)); return;
        }
        else
        {
            j = await UploadLocalAsync(j, true, token);
            if (j.Archived is not null) j = j with { HistoryFinishedItem = await FinishHistoryAsync(j, j.Archived, j.Remote!.Version, token) };
            j = j with { Committed = new(2, AsRevision(j, j.Uploaded!, j.Local!), j.Uploaded!), Phase = "Published" };
        }
        if (j.Direction == SyncDirection.Pull && j.Uploaded is not null)
            j = j with { HistoryFinishedItem = await FinishHistoryAsync(j, j.Uploaded, j.Local!, token) };
        Save(j); await CommitAsync(j.Committed!, token); await FinishAsync(j, token, verifyPublished: recovery || j.Direction != SyncDirection.Backup);
    }
    private async Task FinishAsync(CloudJournal j, CancellationToken token, bool verifyPublished = true)
    {
        if (!verifyPublished)
        {
            // The locked source and successful conditional upload establish this acknowledged version.
            // A later local/cloud edit belongs to the next observation, not another post-upload hash/read.
            Record(j.Path, j.Committed!.Revision.Version.Sha256);
            File.Delete(JournalPath(j.Id));
            return;
        }
        var live = await drive.FindAsync(j.Parent, Path.GetFileName(j.Path), token);
        if (live?.Id == j.Committed!.Item.Id && Matches(j.Committed, live))
        {
            var local = await new FileObserver().ObserveAsync(LocalPath(j.Path), null, new(), true, new(), token);
            if (local.Version?.Sha256 == j.Committed.Revision.Version.Sha256) Record(j.Path, local.Version.Sha256);
        }
        else throw new IOException("Published cloud version changed before completion; evidence retained.");
        File.Delete(JournalPath(j.Id));
    }
}
