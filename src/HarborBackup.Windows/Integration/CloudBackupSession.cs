using System.Security.Cryptography;
using System.Collections.Concurrent;
using System.Text.Json;
using HarborBackup.Core.Domain;
using HarborBackup.OneDrive.Transport;
using HarborBackup.Windows.FileSystem;
using HarborBackup.Windows.Persistence;
using HarborBackup.Windows.Persistence.Configuration;

namespace HarborBackup.Windows.Integration;

public sealed record CloudFileMetadata(int SchemaVersion, CloudRevision Revision, RemoteItem Item);
public sealed record CloudJournal(int SchemaVersion, string Id, string Binding, string Path, SyncDirection Direction,
    FileVersion? Local, CloudRevision? Remote, RemoteItem? Expected, bool Publish, ProtectionKind Protection,
    string Parent, string? Session = null, RemoteItem? Uploaded = null, RemoteItem? Archived = null,
    string? ArchiveParent = null, string? ArchiveName = null, RemoteItem? PreviousHistory = null,
    CloudFileMetadata? Committed = null, string Phase = "Prepared", string? QuickXor = null,
    DateTimeOffset CreatedUtc = default, FileVersion? Incoming = null, CloudMapping? Mapping = null, RemoteItem? RestoredItem = null,
    RemoteItem? HistoryFinishedItem = null);

/// <summary>Ordinary cloud tree; append-only metadata is evidence, never a replacement for the live directory.</summary>
public sealed partial class CloudBackupSession(ProjectDefinition project, ConfigurationSnapshot config,
    CloudAccountSettings account, ICloudDrive drive)
{
    private readonly DataPaths data = DataPaths.FromState(config.Preview.StateDirectory);
    private readonly CloudRepository repository = new(drive, account, DataPaths.FromState(config.Preview.StateDirectory));
    internal CloudRepository Repository => repository;
    private readonly SemaphoreSlim targetGate = new(1, 1);
    private readonly SemaphoreSlim directoryGate = new(1, 1);
    private readonly ConcurrentDictionary<string, RemoteItem> directories = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, PreparedUpload> preparedSources = new(StringComparer.OrdinalIgnoreCase);
    public ReadMetrics? Metrics { get; set; }
    internal CloudResumeTaskStore? ResumeTask { get; set; }
    private CloudProtectionStore Protection => new(Repository, drive, account, data);
    private string Binding => CloudMapping.Bind(project, config);
    private string StateRoot => Path.Combine(data.State, "devices", config.Preview.DeviceId, "cloud", Binding);
    private string JournalRoot => Path.Combine(StateRoot, "cloud-transactions");
    private string CachePath => Path.Combine(data.Cache, "hashes", Binding + ".json");
    private string MetadataArea => "metadata/projects/" + CloudRepository.ProjectKey(project) + "/files";
    private IReadOnlyList<CloudMapping>? previousEvidence;
    private IReadOnlyList<CloudMapping> PreviousEvidence => previousEvidence ??= CloudMapping.PreviousEvidence(project, config).ToArray();
    private CloudProjectRegistry Registry => new(Repository, drive, account.DriveId!, PreviousEvidence.Select(m => m.Project));
    private readonly ConcurrentDictionary<string, CloudFileMetadata> known = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, RemoteItem> metadataSources = new(StringComparer.Ordinal);
    private CloudMetadataCheckpoint Checkpoints => new(Repository, drive, data, project.ProjectId, CloudMetadataCheckpoint.Scope(account));
    internal Task PublishCheckpointAsync(CancellationToken token) => Checkpoints.PublishAsync(
        CloudMetadataCheckpoint.Create(project.ProjectId, metadataSources.Values.ToArray(), known.Values, observed, CloudMetadataCheckpoint.Scope(account)), token);
    private readonly Dictionary<string, RemoteItem> observed = new(StringComparer.OrdinalIgnoreCase);
    private RemoteItem? targetRoot;
    private bool targetResolved;
    private bool targetRegistered;
    private sealed record Baseline(string Hash);
    private sealed record HashCache(int SchemaVersion, string Rules, Dictionary<string, FileVersion> Files);
    private string RulesFingerprint => DeviceIdentity.Digest(JsonSerializer.Serialize(new { project.ExcludedDirectories, project.ExcludedFiles }));
    private ConcurrentDictionary<string, FileVersion> localCache = new(StringComparer.OrdinalIgnoreCase);
    public Action<BackupPhase, string>? Progress { get; set; }
    private void Report(BackupPhase phase, string path) => Progress?.Invoke(phase, path);
    private string BaselinePath(string path) => BaselinePath(StateRoot, path);
    private static string BaselinePath(string root, string path) => Path.Combine(root, "baselines", DeviceIdentity.Digest(path.ToUpperInvariant()) + ".json");
    private string? BaselineHash(string path)
    {
        foreach (var root in new[] { StateRoot }.Concat(PreviousEvidence.Select(m => CloudMapping.StateRoot(m.Project, config))))
        {
            var name = BaselinePath(root, path);
            SafePath.Validate(name, data.State);
            var text = AtomicJson.ReadText(name);
            if (text is null) continue;
            var value = JsonSerializer.Deserialize<Baseline>(text, AtomicJson.Options);
            if (value is null || !FileVersion.IsHash(value.Hash)) throw new IOException("Invalid baseline; recovery evidence retained.");
            return value.Hash;
        }
        return null;
    }
    private void Record(string path, string hash)
    {
        var name = BaselinePath(path);
        var existing = AtomicJson.ReadText(name);
        if (existing is not null && JsonSerializer.Deserialize<Baseline>(existing, AtomicJson.Options)?.Hash == hash) return;
        AtomicJson.Write(name, new Baseline(hash), AtomicJson.Signature(existing));
    }
    private string JournalPath(string id) => Path.Combine(JournalRoot, id + ".json");
    private void Save(CloudJournal value)
    {
        // Authorization is durable before the transaction can cause a business side effect.
        ResumeTask?.Track(value);
        AtomicJson.Write(JournalPath(value.Id), value, AtomicJson.Signature(AtomicJson.ReadText(JournalPath(value.Id))));
        if (Metrics is { } metrics) lock (metrics) metrics.LocalJournalWrites++;
    }
    public bool Pending { get { SafePath.Validate(JournalRoot, data.Root); return Directory.Exists(JournalRoot) && Directory.EnumerateFileSystemEntries(JournalRoot).Any(); } }
    private static string Seal(TransferSession value) => Convert.ToBase64String(ProtectedData.Protect(JsonSerializer.SerializeToUtf8Bytes(value), null, DataProtectionScope.CurrentUser));
    private static TransferSession? Unseal(string? value) => value is null ? null : JsonSerializer.Deserialize<TransferSession>(ProtectedData.Unprotect(Convert.FromBase64String(value), null, DataProtectionScope.CurrentUser));
    private string LocalPath(string relative)
    {
        var path = Path.Combine(project.Source, PathRules.Relative(relative));
        SafePath.Validate(path, project.Source); return path;
    }
    internal async Task<PreparedUpload> PrepareBackupAsync(PreviewFile file, CloudRevision[] heads, CancellationToken token, bool force = false)
    {
        var pending = file.Source.Error == "ContentReadPending";
        var decision = pending ? null : Decide(file, heads, SyncDirection.Backup);
        if (!pending && (file.Source.Kind != ObservationKind.File ||
            !force && decision!.Action != FileAction.Copy && decision.Protection == ProtectionKind.None))
            return new(file);
        var path = LocalPath(file.RelativePath);
        var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read,
            131072, FileOptions.Asynchronous | FileOptions.SequentialScan);
        try
        {
            var modified = File.GetLastWriteTimeUtc(path);
            if (modified > DateTime.UtcNow.AddMinutes(5)) throw new IOException("FutureModificationTime");
            var hashes = await QuickXor.ComputeTransferHashesAsync(stream, token);
            if (stream.Length != hashes.Length || File.GetLastWriteTimeUtc(path) != modified)
                throw new IOException("Source changed while preparing upload.");
            stream.Position = 0;
            var version = new FileVersion(hashes.Sha256, hashes.Length, modified, DateTimeOffset.UtcNow);
            file = file with { Source = FileObservation.Present(version, true) };
            localCache[file.RelativePath] = version;
            if (Metrics is { } metrics) lock (metrics)
            {
                metrics.HashedFiles++; metrics.HashBytes += hashes.Length;
                metrics.UploadHashPasses++; metrics.UploadHashBytes += hashes.Length;
            }
            var key = file.RelativePath;
            var prepared = new PreparedUpload(file, stream, hashes, () => preparedSources.TryRemove(key, out _));
            if (!preparedSources.TryAdd(key, prepared)) throw new IOException("Duplicate source preparation.");
            return prepared;
        }
        catch { await stream.DisposeAsync(); throw; }
    }
    internal void FlushLocalCache()
    {
        try { AtomicJson.Write(CachePath, new HashCache(1, RulesFingerprint, new(localCache)), AtomicJson.Signature(AtomicJson.ReadText(CachePath))); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { /* Disposable optimization. */ }
    }
    private static void RequireExpected(RemoteItem? expected, RemoteItem? actual)
    {
        if (expected is null ? actual is not null : actual is null || expected.Id != actual.Id || expected.Folder != actual.Folder ||
            (!actual.Folder && (expected.ETag != actual.ETag || expected.Size != actual.Size)))
            throw new IOException("Cloud target changed after observation. No replacement performed; review or recover.");
    }
    public async Task ApplyAsync(PreviewFile file, CloudRevision[] heads, SyncDirection direction, FileDecision decision, CancellationToken token)
    {
        if (decision.Action == FileAction.Equal && file.Source.Version is { } equal)
        {
            // Existing common evidence needs no new commit. The inventory and local observation
            // already agreed; do not turn an unchanged file into another remote read/state write.
            if (file.Baseline?.LastCommonHash == equal.Sha256) return;
            var parent = await ParentAsync(file.RelativePath, false, token);
            var current = parent is null ? null : await drive.FindAsync(parent.Id, Path.GetFileName(file.RelativePath), token);
            RequireExpected(observed.GetValueOrDefault(file.RelativePath), current);
            // A new common baseline needs current bytes, even if metadata stayed unchanged.
            var verification = new ReadMetrics();
            var local = await new FileObserver().ObserveAsync(LocalPath(file.RelativePath), null, config.Preview.Hash, true, verification, token);
            if (Metrics is { } metrics) lock (metrics)
            { metrics.HashedFiles += verification.HashedFiles; metrics.HashBytes += verification.HashBytes; }
            if (local.Version?.Sha256 != equal.Sha256) throw new IOException("Local file changed before baseline commit.");
            Record(file.RelativePath, equal.Sha256); return;
        }
        if (decision.Action == FileAction.CreateDirectory)
        {
            if (direction == SyncDirection.Pull) Directory.CreateDirectory(LocalPath(file.RelativePath));
            else
            {
                var parent = (await ParentAsync(file.RelativePath, true, token))!;
                directories[file.RelativePath] = await drive.FolderAsync(parent.Id, Path.GetFileName(file.RelativePath), token);
            }
            return;
        }
        if (decision.Action != FileAction.Copy && decision.Protection == ProtectionKind.None) return;
        var target = await ParentAsync(file.RelativePath, direction == SyncDirection.Backup, token);
        if (target is null) throw new IOException("Cloud target directory is unavailable.");
        var expected = observed.GetValueOrDefault(file.RelativePath);
        var journal = new CloudJournal(5, Guid.NewGuid().ToString("N"), Binding, file.RelativePath, direction,
            file.Source.Version, heads.FirstOrDefault(), expected, decision.Action == FileAction.Copy, decision.Protection, target.Id,
            QuickXor: direction == SyncDirection.Backup ? preparedSources.GetValueOrDefault(file.RelativePath)?.Hashes?.QuickXor : null,
            CreatedUtc: DateTimeOffset.UtcNow, Mapping: CloudMapping.Capture(project, config));
        Save(journal);
        await ContinueAsync(journal, token);
    }
    public async Task RecoverAsync(CancellationToken token, Action<FileResult>? outcome = null,
        bool retryUploads = false, bool backupOnly = false)
    {
        Repository.ResetObservation();
        await Repository.ValidateAsync(token);
        if (!Pending) return;
        foreach (var path in Directory.EnumerateFiles(JournalRoot).ToArray())
        {
            var relative = Path.GetFileNameWithoutExtension(path);
            try
            {
                var text = AtomicJson.ReadText(path)!;
                var journal = JsonSerializer.Deserialize<CloudJournal>(text, AtomicJson.Options) ?? throw new IOException("Invalid transaction.");
                if (backupOnly && (journal.Direction != SyncDirection.Backup || journal.SchemaVersion is not (4 or 5))) continue;
                if (backupOnly && Excluded(journal.Path, false)) continue;
                relative = journal.Path;
                var retried = false;
                var message = await RecoverJournalAsync(path, text, journal, token, retryUploads,
                    changed => retried = changed);
                outcome?.Invoke(new(relative, retried ? FileAction.Copy : FileAction.Equal, retried, message));
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex) when (outcome is not null)
            { outcome(new(relative, FileAction.Deferred, false, ex.Message)); }
        }
    }
    private async Task<string?> RecoverJournalAsync(string path, string text, CloudJournal j, CancellationToken token,
        bool retryUpload, Action<bool> retried)
    {
            if (j.SchemaVersion == 2) { await ClosePreviousJournalAsync(path, j, token); return "PreviousOperationClosed"; }
            if (j.SchemaVersion is not (4 or 5))
                throw new IOException($"恢复记录版本 {j.SchemaVersion} 尚无法安全解释：{path}。此记录已保留，其他文件可继续操作。请提供版本和阶段以核对恢复方式。");
            if (j.SchemaVersion == 5)
            {
                if (j.Mapping is null) throw new IOException("Missing recovery mapping.");
                j.Mapping.Validate(config);
                if (j.Mapping.Binding != Binding) throw new IOException("Recovery mapping changed.");
            }
            if (j.Binding != Binding || !Guid.TryParseExact(j.Id, "N", out _) || Path.GetFileName(path) != j.Id + ".json" ||
                PathRules.Relative(j.Path) != j.Path || !Enum.IsDefined(j.Direction) || !Enum.IsDefined(j.Protection) ||
                j.Phase is not ("Prepared" or "Archived" or "Uploading" or "Uploaded" or "Published" or "Restoring" or "Restored") ||
                j.Phase is "Restoring" or "Restored" && (j.Archived is null || j.Remote is null || j.Expected is null) ||
                j.Local is { } local && (!FileVersion.IsHash(local.Sha256) || local.Length < 0))
                throw new IOException("Recovery identity mismatch. Evidence retained.");
            var parent = await ParentAsync(j.Path, false, token);
            if (parent?.Id != j.Parent || j.Expected is { } expected && expected.Name != Path.GetFileName(j.Path) ||
                j.Uploaded is { } uploaded && (uploaded.Size != j.Local?.Length ||
                    uploaded.Name != (j.Publish && j.Direction == SyncDirection.Backup ? Path.GetFileName(j.Path) : j.ArchiveName)) ||
                j.Direction == SyncDirection.Backup && j.Archived is { } archived && archived.Id != j.Expected?.Id ||
                j.ArchiveName is { } name && name != Path.GetFileName(j.Path) && name != HistoryPendingName(j) &&
                    name != CloudProtectionStore.ConflictName(j.Path, config.Preview.DeviceId, j.Id) ||
                j.Committed is { } committed && (committed.Revision.Path != j.Path ||
                    committed.Revision.Project != CloudRepository.ProjectKey(project) ||
                    committed.Item.Id != (j.Direction == SyncDirection.Backup ? j.Uploaded?.Id : j.Expected?.Id) ||
                    committed.Revision.ItemId != committed.Item.Id || committed.Revision.ETag != committed.Item.ETag ||
                    committed.Revision.Version.Sha256 != (j.Direction == SyncDirection.Backup ? j.Local?.Sha256 : j.Incoming?.Sha256)))
                throw new IOException("Recovery mapping or version mismatch. Evidence retained.");
            if (j.ArchiveParent is not null)
            {
                var kind = j.Publish && j.Protection != ProtectionKind.Conflict ? ProtectionKind.Archive : ProtectionKind.Conflict;
                if ((await Protection.ParentAsync(kind, project.Destination, j.Path, token)).Id != j.ArchiveParent)
                    throw new IOException("Recovery protection mapping mismatch.");
            }
            // Explicit recovery may finish an excluded file's original intent, but must not
            // turn that into a fresh upload selected under today's exclusion policy.
            if (retryUpload && j.Direction == SyncDirection.Backup && !Excluded(j.Path, false))
            {
                retried(await RetryBackupJournalAsync(path, text, j, token));
                return null;
            }
            if (await RetainChangedUploadAsync(path, text, j, token)) return "ChangedSourceOperationClosed";
            await ContinueAsync(j, token, recovery: true);
            return null;
    }
}
