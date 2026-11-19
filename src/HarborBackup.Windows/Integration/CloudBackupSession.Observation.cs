using System.Text.Json;
using HarborBackup.Core.Domain;
using HarborBackup.OneDrive.Transport;
using HarborBackup.Windows.FileSystem;
using HarborBackup.Windows.Persistence;

namespace HarborBackup.Windows.Integration;

public sealed partial class CloudBackupSession
{
    private bool Excluded(string relative, bool directory)
    {
        if (PathRules.Excluded(relative, directory ? project.ExcludedDirectories : project.ExcludedFiles, project.Source)) return true;
        var parts = relative.Split('/');
        for (var i = 1; i < parts.Length; i++)
            if (PathRules.Excluded(string.Join('/', parts.Take(i)), project.ExcludedDirectories, project.Source)) return true;
        return false;
    }
    private IEnumerable<string> Scan(CancellationToken token, ReadMetrics metrics, bool allowMissingRoot)
    {
        SafePath.Validate(project.Source);
        if (!Directory.Exists(project.Source))
        {
            // Only absence is an empty pull destination; access/network failures must remain failures.
            try { _ = File.GetAttributes(project.Source); }
            catch (DirectoryNotFoundException) when (allowMissingRoot) { yield break; }
            catch (FileNotFoundException) when (allowMissingRoot) { yield break; }
            throw new IOException("Local project root is unavailable.");
        }
        var queue = new Queue<string>(); queue.Enqueue(project.Source);
        while (queue.TryDequeue(out var directory))
        {
            foreach (var path in Directory.EnumerateFileSystemEntries(directory))
            {
                token.ThrowIfCancellationRequested();
                var relative = PathRules.Relative(Path.GetRelativePath(project.Source, path));
                if (Path.GetFileName(path).StartsWith(".harborbackup-", StringComparison.OrdinalIgnoreCase)) continue;
                var attributes = File.GetAttributes(path);
                var isDirectory = attributes.HasFlag(FileAttributes.Directory);
                if (Excluded(relative, isDirectory)) { metrics.ExcludedLocalEntries++; continue; }
                yield return relative;
                if (isDirectory && !attributes.HasFlag(FileAttributes.ReparsePoint)) queue.Enqueue(path);
            }
        }
    }
    private async Task<RemoteItem?> TargetAsync(bool create, CancellationToken token)
    {
        await targetGate.WaitAsync(token);
        try { return await TargetCoreAsync(create, token); }
        finally { targetGate.Release(); }
    }
    private async Task<RemoteItem?> TargetCoreAsync(bool create, CancellationToken token)
    {
        if (targetResolved && (targetRoot is not null || !create))
        { if (create) await RegisterTargetAsync(token); return targetRoot; }
        if (project.CloudFolderId is { Length: > 0 } id)
        {
            targetRoot = await drive.GetAsync(id, token);
            await Repository.ValidateTargetAsync(targetRoot, token);
        }
        else targetRoot = await Repository.DirectFolderAsync(project.Destination, create, token);
        targetResolved = true;
        await Registry.ValidateAsync(project, targetRoot, token);
        if (create) await RegisterTargetAsync(token);
        return targetRoot;
    }
    private async Task RegisterTargetAsync(CancellationToken token)
    {
        if (targetRegistered || targetRoot is null) return;
        await Registry.RegisterAsync(project, targetRoot, token);
        targetRegistered = true;
    }
    public async Task CompleteMappingAsync(SyncDirection direction, CancellationToken token)
    {
        await targetGate.WaitAsync(token);
        try
        {
            await TargetCoreAsync(direction == SyncDirection.Backup, token);
            if (targetRoot is null) throw new IOException("Cloud target directory is unavailable.");
            await RegisterTargetAsync(token);
        }
        finally { targetGate.Release(); }
    }
    private async Task<RemoteItem?> ParentAsync(string path, bool create, CancellationToken token)
    {
        var current = await TargetAsync(create, token);
        if (current is null) return null;
        var parts = PathRules.Relative(path).Split('/');
        var prefix = "";
        await directoryGate.WaitAsync(token);
        try
        {
            foreach (var part in parts.SkipLast(1))
            {
                prefix = prefix.Length == 0 ? part : prefix + "/" + part;
                if (directories.TryGetValue(prefix, out var cached)) { current = cached; continue; }
                current = create ? await drive.FolderAsync(current.Id, part, token) : await drive.FindAsync(current.Id, part, token);
                if (current is null) return null;
                if (!current.Folder || current.Id == account.RootId) throw new IOException("Invalid cloud target directory.");
                directories[prefix] = current;
            }
            return current;
        }
        finally { directoryGate.Release(); }
    }
    private async Task LoadMetadataAsync(RemoteItem[] metadataItems, CancellationToken token, IProgress<BackupProgress>? progress, ReadMetrics metrics, bool full)
    {
        known.Clear();
        metadataSources.Clear();
        foreach (var item in metadataItems) metadataSources[item.Id] = item;
        metrics.CloudMetadataCatalogEntries = metadataItems.Length;
        var hit = full ? null : await Checkpoints.ReadAsync(metadataItems, token);
        if (hit is not null)
        {
            try { foreach (var value in hit.Heads) ValidateMetadata(value, value.Revision.Id + ".json"); }
            catch (Exception ex) when (ex is IOException or ArgumentException or NullReferenceException) { hit = null; }
        }
        async Task<CloudFileMetadata[]> LoadAsync(RemoteItem[] items)
        {
        var candidates = new CloudFileMetadata[items.Length];
        var loaded = 0;
        // Bound warm-up groups: the repository's small-batch queue must not repeatedly shift
        // a project-sized list when a cold catalog contains many historical records.
        for (var start = 0; start < items.Length; start += 256)
        {
        var count = Math.Min(256, items.Length - start);
        var warmed = await Repository.WarmMetadataAsync<CloudFileMetadata>(items.AsSpan(start, count).ToArray(), token);
        metrics.CloudMetadataBatchFiles += warmed.Count;
        await Parallel.ForEachAsync(Enumerable.Range(start, count),
            new ParallelOptions { MaxDegreeOfParallelism = 4, CancellationToken = token }, async (index, cancellation) =>
        {
            var item = items[index];
            cancellation.ThrowIfCancellationRequested();
            if (item.Folder || !item.Name.EndsWith(".json", StringComparison.Ordinal)) throw new IOException("Unexpected file metadata entry.");
            lock (metrics) metrics.CloudMetadataRecordsRead++;
            var m = await Repository.ReadJsonAsync<CloudFileMetadata>(item, cancellation, warmed.Contains(item.Id) ? null : metrics);
            ValidateMetadata(m, item.Name);
            candidates[index] = m;
            progress?.Report(new(BackupPhase.ReadingMetadata, project.Destination, item.Name, Interlocked.Increment(ref loaded), items.Length));
        });
        }
        return candidates;
        }
        var tail = await LoadAsync(hit?.Remaining ?? metadataItems);
        var candidates = hit is null ? tail : hit.Heads.Concat(tail).ToArray();
        bool Resolvable()
        {
            var indexed = candidates.ToLookup(m => m.Revision.Path, StringComparer.OrdinalIgnoreCase);
            var unknown = hit!.Payload.Unknown.ToLookup(u => u.Path, StringComparer.OrdinalIgnoreCase);
            return observed.All(pair => pair.Value.Folder || indexed[pair.Key].Any(m => Matches(m, pair.Value)) ||
                unknown[pair.Key].Any(u => u.Id == pair.Value.Id && u.ETag == pair.Value.ETag && u.Size == pair.Value.Size));
        }
        if (hit is not null && !Resolvable()) { candidates = await LoadAsync(metadataItems); hit = null; }
        if (hit is not null)
        {
            metrics.CloudMetadataCheckpointHits++;
            metrics.CloudMetadataHistoricalRecordsSkipped += metadataItems.Length - hit.Remaining.Length;
        }
        var byPath = candidates.GroupBy(m => m.Revision.Path, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.ToArray(), StringComparer.OrdinalIgnoreCase);
        var catalogOrder = metadataItems.Select((item, index) => (item.Name, index)).ToDictionary(p => p.Name, p => p.index, StringComparer.Ordinal);
        // Match the current cloud item, not whichever device happened to publish last.
        foreach (var pair in observed)
        {
            var found = (byPath.GetValueOrDefault(pair.Key) ?? []).Where(m => Matches(m, pair.Value))
                .OrderByDescending(m => m.Revision.CreatedUtc).ThenBy(m => catalogOrder[m.Revision.Id + ".json"]).FirstOrDefault();
            if (found is not null) known[pair.Key] = found;
        }
        Checkpoints.Save(CloudMetadataCheckpoint.Create(project.ProjectId, metadataItems, known.Values, observed, CloudMetadataCheckpoint.Scope(account)));
    }
    private void ValidateMetadata(CloudFileMetadata m, string name)
    {
            if (m is null || m.Revision is null || m.Item is null || m.Revision.Version is null || m.SchemaVersion != 2 || m.Revision.SchemaVersion != 1 || m.Item.Folder || !m.Revision.ContentHashKnown || m.Revision.Version.Length < 0 ||
                m.Item.Name != Path.GetFileName(m.Revision.Path) || m.Revision.ETag != m.Item.ETag || m.Revision.Project != CloudRepository.ProjectKey(project) ||
                !Guid.TryParseExact(m.Revision.Id, "N", out _) || name != m.Revision.Id + ".json" ||
                PathRules.Relative(m.Revision.Path) != m.Revision.Path || !FileVersion.IsHash(m.Revision.Version.Sha256) ||
                m.Item.Id != m.Revision.ItemId || m.Item.Size != m.Revision.Version.Length)
                throw new IOException("Invalid cloud file metadata.");
    }
    private static bool Matches(CloudFileMetadata metadata, RemoteItem current) =>
        metadata.Item.Id == current.Id && !current.Folder && metadata.Item.Size == current.Size &&
        (metadata.Item.ETag == current.ETag || (!string.IsNullOrEmpty(current.ContentTag) && current.ContentTag == metadata.Item.ContentTag)) &&
        (string.IsNullOrEmpty(metadata.Item.QuickXorHash) || metadata.Item.QuickXorHash == current.QuickXorHash);
    private CloudRevision Revision(string path, RemoteItem item)
    {
        var version = item.Folder ? new FileVersion(new string('0', 64), 0, DateTimeOffset.UnixEpoch, DateTimeOffset.UnixEpoch) :
            known.TryGetValue(path, out var m) && Matches(m, item) ? m.Revision.Version :
            new FileVersion(new string('0', 64), item.Size, item.ModifiedUtc ?? DateTimeOffset.UnixEpoch, DateTimeOffset.UnixEpoch);
        return new(1, DeviceIdentity.Digest(item.Id + "/" + (item.Folder ? "folder" : item.ETag))[..32],
            CloudRepository.ProjectKey(project), path, config.Preview.DeviceId, [], item.Id, item.ETag, version,
            DateTimeOffset.UtcNow, item.Folder, item.Folder || known.TryGetValue(path, out var record) && Matches(record, item));
    }
    public async Task<(ProjectSnapshot Snapshot, Dictionary<string, CloudRevision[]> Heads)> ReadAsync(bool full,
        CancellationToken token, IProgress<BackupProgress>? progress = null, ReadMetrics? measured = null, bool deferContentReads = false, bool allowMissingRoot = false)
    {
        var metrics = measured ?? new ReadMetrics();
        var elapsed = System.Diagnostics.Stopwatch.StartNew();
        var scanning = false;
        try
        {
            progress?.Report(new(BackupPhase.ReadingMetadata, project.Destination));
            Repository.ResetObservation();
            await Repository.ValidateAsync(token);
            targetResolved = false; targetRegistered = false; observed.Clear(); directories.Clear();
            var root = await TargetAsync(false, token);
            var inventoryBinding = DeviceIdentity.Digest(JsonSerializer.Serialize(new { Binding, Root = root?.Id,
                project.Destination, project.ExcludedDirectories, project.ExcludedFiles }));
            var inventory = new CloudInventoryCache(Path.Combine(data.Cache, "cloud-inventory", Binding + ".json"), inventoryBinding, drive);
            async Task<CloudInventorySnapshot> EnumerateAsync()
            {
                var entries = new List<CloudInventoryEntry>(); long excluded = 0;
                var queue = new Queue<(RemoteItem Folder, string Path)>();
                if (root is not null) queue.Enqueue((root, ""));
                while (queue.TryDequeue(out var current))
                {
                    token.ThrowIfCancellationRequested();
                    progress?.Report(new(BackupPhase.ReadingMetadata, project.Destination, current.Path, entries.Count));
                    metrics.CloudDirectoriesListed++;
                    foreach (var item in await drive.ChildrenAsync(current.Folder.Id, token))
                    {
                        var path = PathRules.Relative(current.Path + item.Name);
                        if (!item.Folder && TransferSession.IsTemporaryName(item.Name)) continue;
                        if (Excluded(path, item.Folder)) { excluded++; continue; }
                        if (item.Id == account.RootId) throw new IOException("Cloud target includes management storage.");
                        entries.Add(new(path, item));
                        if (item.Folder) queue.Enqueue((item, path + "/"));
                    }
                }
                var metadataFolder = await Repository.FolderAsync(MetadataArea, false, token);
                RemoteItem[] metadata = [];
                if (metadataFolder is not null)
                {
                    metrics.CloudDirectoriesListed++;
                    metadata = (await drive.ChildrenAsync(metadataFolder.Id, token)).ToArray();
                }
                return new(entries.ToArray(), metadata, excluded, root?.Id, metadataFolder?.Id, Repository.ObservationGuards);
            }
            var snapshot = await inventory.ReadAsync(full, EnumerateAsync, metrics, token);
            foreach (var entry in snapshot.Entries)
            {
                if (entry.Item.Id == account.RootId) throw new IOException("Cloud target includes management storage.");
                if (!entry.Item.Folder && TransferSession.IsTemporaryName(entry.Item.Name)) continue;
                observed.Add(entry.Path, entry.Item);
                if (entry.Item.Folder) directories[entry.Path] = entry.Item;
            }
            metrics.ExcludedCloudEntries += snapshot.Excluded;
            await LoadMetadataAsync(snapshot.Metadata, token, progress, metrics, full);
            metrics.CloudReadMilliseconds += elapsed.ElapsedMilliseconds;
            elapsed.Restart(); scanning = true;
            try
            {
                var saved = JsonSerializer.Deserialize<HashCache>(AtomicJson.ReadText(CachePath) ?? "null", AtomicJson.Options);
                localCache = new(saved is { SchemaVersion: 1, Files: not null } && saved.Rules == RulesFingerprint ? saved.Files : [], StringComparer.OrdinalIgnoreCase);
                if (localCache.Any(p => p.Value is null || !FileVersion.IsHash(p.Value.Sha256))) localCache = new(StringComparer.OrdinalIgnoreCase);
                localCache = new(localCache, StringComparer.OrdinalIgnoreCase);
            }
            catch (Exception ex) when (ex is JsonException or ArgumentException or IOException or UnauthorizedAccessException)
            { localCache = new(StringComparer.OrdinalIgnoreCase); }
            var files = new List<PreviewFile>();
            progress?.Report(new(BackupPhase.Scanning, project.Destination));
            var localPaths = new List<string>();
            foreach (var path in Scan(token, metrics, allowMissingRoot))
            {
                localPaths.Add(path);
                if (localPaths.Count % 100 == 0) progress?.Report(new(BackupPhase.Scanning, project.Destination, path, localPaths.Count));
            }
            var paths = localPaths.Concat(observed.Keys).Distinct(StringComparer.OrdinalIgnoreCase).Order(StringComparer.OrdinalIgnoreCase).ToArray();
            var heads = new Dictionary<string, CloudRevision[]>(StringComparer.OrdinalIgnoreCase);
            var observer = new FileObserver();
            foreach (var path in paths)
            {
                token.ThrowIfCancellationRequested();
                progress?.Report(new(BackupPhase.Scanning, project.Destination, path, files.Count, paths.Length, metrics.HashBytes));
                var local = await observer.ObserveAsync(LocalPath(path), localCache.GetValueOrDefault(path), config.Preview.Hash, full, metrics, token, deferHash: deferContentReads);
                if (local.Version is { } v) localCache[path] = v;
                var remote = FileObservation.Missing;
                if (observed.TryGetValue(path, out var item))
                {
                    var revision = Revision(path, item); heads[path] = [revision];
                    remote = item.Folder ? FileObservation.Directory : FileObservation.Present(revision.Version, false);
                }
                var common = BaselineHash(path);
                var basis = common is null ? null : new FileVersion(common, 0, DateTimeOffset.UnixEpoch, DateTimeOffset.UnixEpoch);
                files.Add(new(path, local, remote, basis is null ? null : new(basis, basis, common!)));
                metrics.ScannedEntries++;
            }
            if (!deferContentReads) FlushLocalCache();
            return (new(files, [], metrics), heads);
        }
        finally
        {
            if (scanning) metrics.ScanMilliseconds += elapsed.ElapsedMilliseconds;
            else metrics.CloudReadMilliseconds += elapsed.ElapsedMilliseconds;
        }
    }
    public FileDecision Decide(PreviewFile file, CloudRevision[] heads, SyncDirection direction, bool establishBaseline = false)
    {
        var remote = heads.FirstOrDefault();
        var unknown = remote is { Directory: false, ContentHashKnown: false };
        var decision = CloudRevisions.Decide(direction, file.Source, heads, file.Baseline?.LastCommonHash);
        if (establishBaseline && direction == SyncDirection.Pull && remote is { Directory: false } &&
            file.Source.Kind is ObservationKind.File or ObservationKind.Missing &&
            (unknown || decision.Action is FileAction.ConflictUnresolved or FileAction.NeedsPush))
            return new(FileAction.Copy, true, FileSide.Source, FileSide.Both,
                file.Source.Version is null ? ProtectionKind.None : ProtectionKind.Archive, "ExplicitBaselinePull");
        if (establishBaseline && direction == SyncDirection.Backup && remote is { Directory: false } &&
            file.Source.Kind == ObservationKind.File && (unknown || decision.Action is FileAction.ConflictUnresolved or FileAction.NeedsPull))
            return new(FileAction.Copy, true, FileSide.Target, FileSide.Target, ProtectionKind.Archive, "ExplicitLocalUpload");
        return decision;
    }
    public PullFileSelection ConfirmPull(PreviewFile file, CloudRevision[] heads, FileDecision decision, bool establishBaseline, SyncDirection direction = SyncDirection.Pull)
    {
        var evidence = new { Binding, file.RelativePath, file.Source.Kind, LocalHash = file.Source.Version?.Sha256,
            CommonHash = file.Baseline?.LastCommonHash, Remote = heads.Select(h => new { h.ItemId,
                ETag = h.Directory ? null : h.ETag, h.Directory, h.ContentHashKnown, Hash = h.Version.Sha256 }),
            decision.Action, decision.Reason, establishBaseline, direction };
        return new(file.RelativePath, DeviceIdentity.Digest(JsonSerializer.Serialize(evidence)),
            direction == SyncDirection.Pull ? file.Source.Kind == ObservationKind.Missing : file.Target.Kind == ObservationKind.Missing,
            direction == SyncDirection.Pull ? heads.FirstOrDefault() is { Directory: false } remote ? remote.Version.Length : 0 : file.Source.Version?.Length ?? 0);
    }
    public async Task<PreviewFile> RefreshPullLocalAsync(PreviewFile file, CancellationToken token) =>
        file with { Source = await new FileObserver().ObserveAsync(LocalPath(file.RelativePath), null, new(), true, new(), token) };
}
