using System.Text.Json;
using HarborBackup.Core.Domain;
using HarborBackup.OneDrive.Transport;
using HarborBackup.Windows.FileSystem;
using HarborBackup.Windows.Persistence;

namespace HarborBackup.Windows.Integration;

public sealed partial class CloudBackupSession
{
    internal DateTimeOffset? AutomaticRetryNotBeforeUtc { get; private set; }
    internal async Task RecoverAutomaticAsync(CloudResumeTaskStore task, AutomaticRunBudget budget,
        bool retryOnly, CancellationToken token, Action<FileResult> outcome)
    {
        Repository.ResetObservation();
        await Repository.ValidateAsync(token);
        var authorization = task.Value;
        var externalPending = CloudMapping.ScanPending(config);
        if (externalPending.Problems.Any(p => p.Affects(project)))
            throw new IOException("Recovery evidence cannot be verified. Retained operations require review.");
        var active = new List<CloudJournal>();
        if (Pending)
            foreach (var path in Directory.EnumerateFiles(JournalRoot))
            {
                var text = AtomicJson.ReadText(path)!;
                var journal = JsonSerializer.Deserialize<CloudJournal>(text, AtomicJson.Options) ?? throw new IOException("Invalid pending transaction.");
                active.Add(journal);
            }
        var completedPaths = authorization.Completed.Select(c => c.Path).ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var saved in authorization.Journals.GroupBy(j => j.Path, StringComparer.OrdinalIgnoreCase).Select(g => g.OrderBy(j => j.CreatedUtc).Last()))
        {
            token.ThrowIfCancellationRequested();
            if (completedPaths.Contains(saved.Path)) continue;
            if (!task.CanRetry(saved.Path))
            {
                var waiting = task.GetRecovery(saved.Path);
                outcome(new(saved.Path, FileAction.Deferred, false, "PendingFileOperation")
                { Recovery = waiting == RecoveryDisposition.None ? RecoveryDisposition.AwaitingConfirmation : waiting });
                continue;
            }
            try
            {
            if (externalPending.Files.Any(p => p.Mapping.Binding != Binding && p.Affects(project, saved.Path)))
                throw new DeferredOperationException("An unfinished operation in another mapping overlaps this file. Review the retained operations before continuing.");
            if (active.Any(other => other.Id != saved.Id && new PendingCloudFile(other.Mapping ?? authorization.Mapping, other.Path).Affects(project, saved.Path)))
                throw new DeferredOperationException("Another unfinished operation overlaps this file. Review the retained operations before continuing.");
            var path = JournalPath(saved.Id);
            var text = AtomicJson.ReadText(path);
            var j = text is null ? task.LatestJournal(saved.Id) : JsonSerializer.Deserialize<CloudJournal>(text, AtomicJson.Options)
                ?? throw new IOException("Invalid recovery transaction.");
            if (CloudResumeTaskStore.Intent(j) != CloudResumeTaskStore.Intent(saved) ||
                text is null && j.Committed is null && j.Phase is not ("Restored" or "Restoring"))
                throw new IOException("Recovery transaction no longer matches the original task. Evidence retained.");
            ValidateAutomaticJournal(j, path);
            if (!authorization.Automatic && j.Direction != authorization.Direction)
                throw new IOException("Recovery direction differs from the original authorization.");
            if (Excluded(j.Path, false)) throw new DeferredOperationException("Recovery path is excluded by the current project rules.");
            if (!budget.TryStart(retryOnly && task.WasReserved(j.Path) ? 0 : j.Direction == SyncDirection.Backup ? j.Local?.Length ?? 0 : j.Remote?.Version.Length ?? 0,
                !retryOnly || !task.WasReserved(j.Path)))
            {
                outcome(new(j.Path, FileAction.Deferred, false, budget.StopReason) { FailureKind = FileFailureKind.Budget });
                return;
            }
            task.Reserve(j.Path, j.Direction == SyncDirection.Backup ? j.Local?.Length ?? 0 : j.Remote?.Version.Length ?? 0);
            var verification = new ReadMetrics();
            var local = await new FileObserver().ObserveAsync(LocalPath(j.Path), null, new(), true, verification, token);
            if (Metrics is { } metrics) lock (metrics)
            { metrics.HashedFiles += verification.HashedFiles; metrics.HashBytes += verification.HashBytes; }
            if (j.Direction == SyncDirection.Backup && local.Version?.Sha256 != j.Local?.Sha256)
            {
                // Retiring a proven no-write intent is safe. It never grants manual local preference.
                if (text is not null && await RetainChangedUploadAsync(path, text, j, token))
                {
                    task.Complete(j.Path, null);
                    outcome(new(j.Path, FileAction.Deferred, false, "ChangedSourceOperationClosed")
                    { Recovery = RecoveryDisposition.AwaitingConfirmation });
                    continue;
                }
                throw new DeferredOperationException("Source changed since the interrupted operation. Review the retained versions before continuing.");
            }
            await ValidateAutomaticTargetAsync(j, token);
            if (!budget.TryStart(0, false))
            {
                outcome(new(j.Path, FileAction.Deferred, false, budget.StopReason) { FailureKind = FileFailureKind.Budget });
                return;
            }
            // The normal validator checks schema, device/account/mapping, exact paths and archive
            // destinations. retryUpload:false retains the original intent rather than manual preference.
            await RecoverJournalAsync(path, text ?? JsonSerializer.Serialize(j, AtomicJson.Options), j, token,
                retryUpload: false, _ => { });
            if (File.Exists(path)) throw new IOException("Recovery transaction did not finish. Evidence retained.");
            var restored = task.LatestJournal(j.Id).RestoredItem is not null;
            var completed = task.LatestJournal(j.Id);
            if (!restored) task.Complete(j.Path, j.Direction == SyncDirection.Backup ? j.Local?.Sha256 :
                completed.Committed?.Revision.Version.Sha256 ?? completed.Incoming?.Sha256 ?? j.Remote?.Version.Sha256);
            if (!j.Publish)
            {
                task.SetRecovery(j.Path, RecoveryDisposition.AwaitingConfirmation);
                outcome(new(j.Path, FileAction.ConflictUnresolved, false) { Recovery = RecoveryDisposition.AwaitingConfirmation, Direction = j.Direction });
            }
            else outcome(restored ? new(j.Path, FileAction.Deferred, false, "OriginalVersionRestored") : new(j.Path, FileAction.Copy, true) { Direction = j.Direction });
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                var kind = FileFailurePolicy.Classify(ex, false);
                var disposition = FileFailurePolicy.Recovery(kind, ex);
                task.SetRecovery(saved.Path, disposition);
                if (disposition == RecoveryDisposition.AutomaticRetry && !task.CanRetry(saved.Path)) disposition = RecoveryDisposition.AwaitingConfirmation;
                var hint = ex is CloudRequestException request ? request.RetryNotBeforeUtc : ex is CloudCooldownException cooldown ? cooldown.RetryNotBeforeUtc : null;
                if (hint is not null && (AutomaticRetryNotBeforeUtc is null || hint > AutomaticRetryNotBeforeUtc)) AutomaticRetryNotBeforeUtc = hint;
                outcome(new(saved.Path, FileAction.Deferred, false, ex.Message) { FailureKind = kind, Recovery = disposition, Direction = saved.Direction });
            }
        }
    }

    private void ValidateAutomaticJournal(CloudJournal j, string path)
    {
        if (j.SchemaVersion != 5 || j.Mapping?.Project is null || j.Binding != Binding ||
            !Guid.TryParseExact(j.Id, "N", out _) || Path.GetFileName(path) != j.Id + ".json" ||
            PathRules.Relative(j.Path) != j.Path || !Enum.IsDefined(j.Direction) || !Enum.IsDefined(j.Protection) ||
            j.Phase is not ("Prepared" or "Archived" or "Uploading" or "Uploaded" or "Published" or "Restoring" or "Restored") ||
            j.Local is { } local && (!FileVersion.IsHash(local.Sha256) || local.Length < 0) ||
            j.Direction == SyncDirection.Backup && j.Publish && j.Local is null ||
            j.Remote is { } remote && (remote.Version is null || remote.Project != project.ProjectId || remote.Path != j.Path ||
                !FileVersion.IsHash(remote.Version.Sha256) || remote.Version.Length < 0))
            throw new IOException("Invalid original recovery intent. Evidence retained.");
        j.Mapping.Validate(config);
        if (j.Mapping.Binding != Binding || j.Mapping.Project.ProjectId != project.ProjectId)
            throw new IOException("Recovery project or physical mapping changed.");
    }

    private async Task ValidateAutomaticTargetAsync(CloudJournal j, CancellationToken token)
    {
        if (j.SchemaVersion != 5 || j.Mapping is null) throw new IOException("Unattended recovery requires a verified current transaction format.");
        j.Mapping.Validate(config);
        if (j.Mapping.Project.ProjectId != project.ProjectId || j.Mapping.Binding != Binding || j.Binding != Binding)
            throw new IOException("Recovery project or physical mapping changed.");
        if (j.RestoredItem is { } restored)
            RequireExpected(restored, await drive.FindAsync(j.Parent, Path.GetFileName(j.Path), token));
        if (j.HistoryFinishedItem is { } finished)
        {
            RequireExpected(finished, await drive.GetAsync(finished.Id, token));
            if (finished.ParentId != j.ArchiveParent) throw new IOException("Recovery history mapping changed.");
            var version = j.Direction == SyncDirection.Backup ? j.Remote?.Version : j.Local;
            await Repository.VerifyAsync(finished, version ?? throw new IOException("Missing history version."), finished.QuickXorHash, false, token);
        }
        else if (j.Archived is { } archive)
        {
            var actual = await drive.GetAsync(archive.Id, token);
            var rotated = archive.Name == HistoryPendingName(j) && actual.Name == Path.GetFileName(j.Path) &&
                archive.Id == actual.Id && archive.Size == actual.Size && !string.IsNullOrEmpty(archive.ContentTag) &&
                archive.ContentTag == actual.ContentTag && !string.IsNullOrEmpty(archive.QuickXorHash) && archive.QuickXorHash == actual.QuickXorHash;
            if (!rotated) RequireExpected(archive, actual);
            if (actual.ParentId != j.ArchiveParent) throw new IOException("Recovery archive moved. Evidence retained.");
            await Repository.VerifyAsync(actual, j.Remote?.Version ?? throw new IOException("Missing archive version."), archive.QuickXorHash, false, token);
        }
        else if (j.Direction == SyncDirection.Backup && j.ArchiveParent is not null && j.ArchiveName is not null)
        {
            var candidate = await drive.FindAsync(j.ArchiveParent, j.ArchiveName, token);
            if (candidate is not null)
            {
                if (candidate.Id != j.Expected?.Id) throw new IOException("Recovery archive destination changed.");
                await Repository.VerifyAsync(candidate, j.Remote?.Version ?? throw new IOException("Missing archive version."), j.Expected!.QuickXorHash, false, token);
            }
        }
        if (j.Direction != SyncDirection.Backup || !j.Publish || j.Phase is "Restoring" or "Restored") return;
        var live = await drive.FindAsync(j.Parent, Path.GetFileName(j.Path), token);
        if (j.Uploaded is { } uploaded) { RequireExpected(uploaded, live); return; }
        if (live is null)
        {
            if (j.Expected is not null && j.ArchiveParent is null) RequireExpected(j.Expected, live);
            return;
        }
        if (j.Phase == "Uploading" && j.Local is not null && live.Size == j.Local.Length &&
            !string.IsNullOrEmpty(j.QuickXor) && live.QuickXorHash == j.QuickXor)
        { await Repository.VerifyAsync(live, j.Local, j.QuickXor, false, token); return; }
        // Reject a competing live version before ContinueAsync could create conflict material or
        // change the original journal's intention. Resolution remains an explicit user action.
        try { RequireExpected(j.Expected, live); }
        catch (IOException) { throw new DeferredOperationException("Cloud target changed after interruption. Review the competing versions before continuing."); }
    }
}
