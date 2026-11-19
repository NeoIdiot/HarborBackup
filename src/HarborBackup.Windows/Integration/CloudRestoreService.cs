using System.Globalization;
using System.Text.Json;
using HarborBackup.Core.Domain;
using HarborBackup.OneDrive.Transport;
using HarborBackup.Windows.FileSystem;
using HarborBackup.Windows.Persistence;
using HarborBackup.Windows.Persistence.Configuration;

namespace HarborBackup.Windows.Integration;

public sealed record RestoreVerificationState(int SchemaVersion, string DeviceId, string? AccountId,
    string? DriveId, string? RepositoryId, string? RootId, string Binding, string ProjectId,
    DateTimeOffset VerifiedUtc, int VerifiedFiles, long VerifiedBytes, RestoreScope Scope);

/// <summary>Explicit independent restores. Never writes cloud content, common baselines or readiness.</summary>
public sealed class CloudRestoreService(ConfigurationSnapshot config, Func<ICloudDrive>? driveFactory = null)
{
    private CloudAccountSettings Account => config.CloudAccount ?? throw new InvalidOperationException("Missing account binding.");
    private DataPaths Data => DataPaths.FromState(config.Preview.StateDirectory);
    private ICloudDrive Open() => driveFactory?.Invoke() ?? new CloudAccountStore(Data).Open(Account);
    private sealed record Cursor(int SchemaVersion, int GroupCount, int NextGroup, Dictionary<int, string> LastKeys);

    public static string VerificationPath(ConfigurationSnapshot config, ProjectDefinition project) => Path.Combine(
        DataPaths.FromState(config.Preview.StateDirectory).State, "devices", config.Preview.DeviceId,
        "restore-verification", CloudMapping.Bind(project, config) + ".json");

    public static string ValidateOutputRoot(ConfigurationSnapshot config, string outputRoot)
    {
        var root = PathRules.Absolute(outputRoot);
        var data = DataPaths.FromState(config.Preview.StateDirectory);
        var sourceRoots = config.Projects.Select(p => p.Source).Concat(config.RetiredMappings.Select(m => m.Project.Source));
        var protectedRoots = new[] { data.Root, config.ArchiveDirectory, config.ConflictDirectory, config.HistoryDirectory, config.LogDirectory }
            .Where(p => !string.IsNullOrWhiteSpace(p));
        if (sourceRoots.Any(p => PathRules.Overlaps(root, p)) || protectedRoots.Any(p => PathRules.Overlaps(root, p)) ||
            PathRules.Overlaps(root, AppContext.BaseDirectory))
            throw new UnsafePathException("Restore output must be separate from every project source, application and data directory.");
        ValidateNoLinks(root);
        if (File.Exists(root)) throw new IOException("Restore output is a file.");
        return root;
    }
    private static void ValidateNoLinks(string path)
    {
        SafePath.Validate(path);
        for (string? current = PathRules.Normalize(path); current is not null; current = Path.GetDirectoryName(current))
        {
            try { if ((File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0) throw new UnsafePathException("Restore output cannot contain links or reparse points."); }
            catch (FileNotFoundException) { }
            catch (DirectoryNotFoundException) { }
        }
    }
    private static bool Matches(RemoteItem expected, RemoteItem actual) => !actual.Folder && actual.Id == expected.Id &&
        actual.ETag == expected.ETag && actual.Size == expected.Size && actual.ParentId == expected.ParentId && actual.Name == expected.Name;
    private static string Key(RestoreCandidate c) => c.ProjectId + "/" + c.Scope + "/" + c.Path + "/" + c.Id;
    private static bool Filter(string path, string? filter) => filter is null || path.Equals(filter, StringComparison.OrdinalIgnoreCase) || path.StartsWith(filter + "/", StringComparison.OrdinalIgnoreCase);
    private static RestoreCandidate Candidate(ProjectDefinition project, ConfigurationSnapshot config, string path,
        RestoreScope scope, RemoteItem item, string? hash) => new(
            DeviceIdentity.Digest(project.ProjectId + "/" + scope + "/" + path + "/" + item.Id + "/" + item.ETag),
            project.ProjectId, project.Destination, CloudMapping.Bind(project, config), path, scope, item.Id, item.ETag,
            item.ParentId, item.Name, item.Size, hash, item.QuickXorHash, item.ModifiedUtc);

    public async Task<RestoreCatalog> ListAsync(string? destination, RestoreScope? scope, string? pathFilter, CancellationToken token)
    {
        if (pathFilter is not null) pathFilter = PathRules.Relative(pathFilter, true);
        using var drive = Open(); var repository = new CloudRepository(drive, Account, Data);
        await repository.ValidateAsync(token);
        var result = new List<RestoreCandidate>(); var diagnostics = new List<string>();
        foreach (var project in BackupRuntime.Select(config, destination))
        {
            token.ThrowIfCancellationRequested();
            if (project.ConfigurationError is not null) { diagnostics.Add(project.ProjectId + ": " + project.ConfigurationError); continue; }
            if (scope is null or RestoreScope.Current)
            {
                var metadata = new List<CloudFileMetadata>();
                var metadataFolder = await repository.FolderAsync("metadata/projects/" + project.ProjectId + "/files", false, token);
                if (metadataFolder is not null)
                    foreach (var item in await drive.ChildrenAsync(metadataFolder.Id, token))
                    {
                        var record = await repository.ReadJsonAsync<CloudFileMetadata>(item, token);
                        if (record.SchemaVersion != 2 || record.Revision.SchemaVersion != 1 || record.Revision.Project != project.ProjectId ||
                            record.Item.Folder || !record.Revision.ContentHashKnown || !FileVersion.IsHash(record.Revision.Version.Sha256) ||
                            record.Item.Id != record.Revision.ItemId || record.Item.ETag != record.Revision.ETag ||
                            record.Item.Size != record.Revision.Version.Length || record.Revision.Version.Length < 0 ||
                            !Guid.TryParseExact(record.Revision.Id, "N", out _) || item.Name != record.Revision.Id + ".json" ||
                            PathRules.Relative(record.Revision.Path) != record.Revision.Path || record.Item.Name != Path.GetFileName(record.Revision.Path))
                            throw new IOException("Invalid restore file metadata.");
                        metadata.Add(record);
                    }
                var byItem = metadata.GroupBy(m => m.Item.Id).ToDictionary(g => g.Key, g => g.ToArray());
                var root = project.CloudFolderId is { Length: > 0 } folderId
                    ? await drive.GetAsync(folderId, token) : await repository.DirectFolderAsync(project.Destination, false, token);
                if (root is not null)
                {
                    await repository.ValidateTargetAsync(root, token);
                    var queue = new Queue<(RemoteItem Folder, string Prefix)>(); queue.Enqueue((root, ""));
                    var seen = new HashSet<string>();
                    while (queue.TryDequeue(out var directory))
                    {
                        if (!seen.Add(directory.Folder.Id)) throw new IOException("Cloud directory cycle during restore observation.");
                        foreach (var item in await drive.ChildrenAsync(directory.Folder.Id, token))
                        {
                            var path = PathRules.Relative(directory.Prefix + item.Name);
                            if (item.Id == Account.RootId) throw new IOException("Restore project overlaps management storage.");
                            if (item.Folder) { queue.Enqueue((item, path + "/")); continue; }
                            if (TransferSession.IsTemporaryName(item.Name) || !Filter(path, pathFilter)) continue;
                            var known = (byItem.GetValueOrDefault(item.Id) ?? []).FirstOrDefault(m =>
                                m.Revision.Path.Equals(path, StringComparison.OrdinalIgnoreCase) && m.Item.Size == item.Size &&
                                (m.Item.ETag == item.ETag || !string.IsNullOrEmpty(item.ContentTag) && m.Item.ContentTag == item.ContentTag) &&
                                (string.IsNullOrEmpty(m.Item.QuickXorHash) || m.Item.QuickXorHash == item.QuickXorHash));
                            result.Add(Candidate(project, config, path, RestoreScope.Current, item, known?.Revision.Version.Sha256));
                        }
                    }
                }
            }
            if (scope is null or RestoreScope.Archive)
            {
                var folder = await repository.FolderAsync("metadata/protection/archives/" + project.ProjectId, false, token);
                if (folder is not null)
                    foreach (var pathFolder in await drive.ChildrenAsync(folder.Id, token))
                    {
                        if (!pathFolder.Folder || !FileVersion.IsHash(pathFolder.Name)) throw new IOException("Invalid archive index directory.");
                        foreach (var item in await drive.ChildrenAsync(pathFolder.Id, token))
                        {
                            var record = await repository.ReadJsonAsync<CloudProtectionRecord>(item, token);
                            new CloudProtectionStore(repository, drive, Account, Data).Validate(record);
                            if (record.Kind != ProtectionKind.Archive || record.Revision.Project != project.ProjectId ||
                                DeviceIdentity.Digest(record.Revision.Path.ToUpperInvariant()) != pathFolder.Name || item.Name != record.Revision.Id + ".json")
                                throw new IOException("Archive restore index identity mismatch.");
                            if (!record.Destination.Equals(project.Destination, StringComparison.OrdinalIgnoreCase) || !Filter(record.Revision.Path, pathFilter)) continue;
                            var relativeParent = record.Revision.Path.Contains('/') ? record.Revision.Path[..record.Revision.Path.LastIndexOf('/')] : "";
                            var contentParent = await repository.FolderAsync("archives/" + record.Destination + (relativeParent.Length == 0 ? "" : "/" + relativeParent), false, token);
                            if (contentParent is null || record.Item.ParentId != contentParent.Id || record.Item.Name != Path.GetFileName(record.Revision.Path))
                                throw new IOException("Archive content is outside its recorded protection directory.");
                            result.Add(Candidate(project, config, record.Revision.Path, RestoreScope.Archive, record.Item,
                                record.Revision.ContentHashKnown ? record.Revision.Version.Sha256 : null));
                        }
                    }
            }
        }
        return new(result.OrderBy(Key, StringComparer.Ordinal).ToArray(), diagnostics);
    }

    public async Task<RestoreFileResult> ExportAsync(RestoreCandidate candidate, string outputRoot, CancellationToken token)
    {
        var root = ValidateOutputRoot(config, outputRoot);
        using var drive = Open(); var repository = new CloudRepository(drive, Account, Data);
        await repository.ValidateAsync(token);
        return await ExportCoreAsync(candidate, root, drive, token);
    }

    private async Task<RestoreFileResult> ExportCoreAsync(RestoreCandidate candidate, string root, ICloudDrive drive, CancellationToken token)
    {
        ValidateOutputRoot(config, root);
        var project = config.Projects.SingleOrDefault(p => p.ProjectId == candidate.ProjectId) ?? throw new IOException("Restore project is no longer configured.");
        if (candidate.Binding != CloudMapping.Bind(project, config) || candidate.Length < 0 || !Enum.IsDefined(candidate.Scope) ||
            string.IsNullOrWhiteSpace(candidate.ItemId) || string.IsNullOrWhiteSpace(candidate.ETag)) throw new IOException("Restore mapping or candidate changed.");
        var relative = PathRules.Relative(candidate.Path);
        if (!FileVersion.IsHash(candidate.Sha256 ?? ""))
            return new(candidate.Id, project.ProjectId, relative, candidate.Scope, RestoreFileStatus.EvidenceInsufficient, 0,
                Error: "Trusted SHA-256 evidence is unavailable; no content was restored.");
        var expected = new RemoteItem(candidate.ItemId, candidate.ItemName, candidate.ETag, candidate.Length, false,
            QuickXorHash: candidate.QuickXorHash, ParentId: candidate.ParentId);
        var current = await drive.GetAsync(candidate.ItemId, token);
        if (!Matches(expected, current)) throw new IOException("Cloud restore version changed; list candidates again.");
        var target = Path.Combine(root, candidate.Scope.ToString().ToLowerInvariant(), candidate.ProjectId, relative.Replace('/', Path.DirectorySeparatorChar));
        SafePath.Validate(target, root); ValidateNoLinks(target);
        if (File.Exists(target) || Directory.Exists(target)) throw new IOException("Restore never overwrites an existing path.");
        Directory.CreateDirectory(Path.GetDirectoryName(target)!); ValidateNoLinks(target);
        var temp = target + "." + Guid.NewGuid().ToString("N") + ".restore-part";
        try
        {
            await using (var output = new FileStream(temp, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None, 131072, true))
            {
                await drive.DownloadAsync(current, output, token);
                if (output.Length != candidate.Length) throw new IOException("Restored content length differs from evidence.");
                output.Position = 0; var hashes = await QuickXor.ComputeTransferHashesAsync(output, token);
                if (hashes.Sha256 != candidate.Sha256 || !string.IsNullOrEmpty(current.QuickXorHash) && hashes.QuickXor != current.QuickXorHash)
                    throw new IOException("Restored content checksum differs from evidence.");
                output.Flush(true);
            }
            if (!Matches(current, await drive.GetAsync(current.Id, token))) throw new IOException("Cloud restore version changed during download.");
            ValidateNoLinks(target); SafePath.Validate(target, root);
            if (candidate.ModifiedUtc is { } modified) File.SetLastWriteTimeUtc(temp, modified.UtcDateTime);
            File.Move(temp, target);
            return new(candidate.Id, project.ProjectId, relative, candidate.Scope, RestoreFileStatus.Verified, candidate.Length, target);
        }
        finally { if (File.Exists(temp)) { ValidateNoLinks(temp); File.Delete(temp); } }
    }

    public async Task<RestoreReport> ExecuteAsync(IReadOnlyList<RestoreCandidate> candidates, string outputRoot,
        bool testOnly, long maxBytes, int groupCount, CancellationToken token)
    {
        if (maxBytes < 0 || groupCount is < 1 or > 256) throw new ArgumentOutOfRangeException(nameof(maxBytes));
        if (!testOnly && (maxBytes != 0 || groupCount != 1)) throw new ArgumentException("Budgets and rotation groups apply to restore-test only.");
        if (candidates.Select(c => c.Id).Distinct(StringComparer.Ordinal).Count() != candidates.Count) throw new ArgumentException("Duplicate restore candidates.");
        var root = ValidateOutputRoot(config, outputRoot); var started = DateTimeOffset.UtcNow;
        if (testOnly) root = ValidateOutputRoot(config, Path.Combine(root, "restore-test-" + Guid.NewGuid().ToString("N")));
        var diagnostics = new List<string>(); var files = new List<RestoreFileResult>();
        using var drive = Open(); var repository = new CloudRepository(drive, Account, Data);
        await repository.ValidateAsync(token);
        var cursorPath = Path.Combine(Data.Cache, "restore-test", DeviceIdentity.Digest(string.Join("/", candidates.Select(c => c.Binding).Distinct().Order(StringComparer.Ordinal))) + ".json");
        var cursor = new Cursor(1, groupCount, 0, []);
        if (testOnly)
        {
            try { if (JsonSerializer.Deserialize<Cursor>(AtomicJson.ReadText(cursorPath) ?? "null", AtomicJson.Options) is { SchemaVersion: 1, LastKeys: not null } saved && saved.GroupCount == groupCount && saved.NextGroup >= 0 && saved.NextGroup < groupCount) cursor = saved; }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException) { }
        }
        var group = testOnly ? cursor.NextGroup : 0;
        var ordered = candidates.Where(c => !testOnly || uint.Parse(DeviceIdentity.Digest(Key(c))[..8], NumberStyles.HexNumber, CultureInfo.InvariantCulture) % groupCount == group)
            .OrderBy(Key, StringComparer.Ordinal).ToArray();
        if (testOnly && cursor.LastKeys.TryGetValue(group, out var last)) ordered = ordered.Where(c => string.CompareOrdinal(Key(c), last) > 0).Concat(ordered.Where(c => string.CompareOrdinal(Key(c), last) <= 0)).ToArray();
        long used = 0; string? deferredStart = null;
        foreach (var candidate in ordered)
        {
            if (token.IsCancellationRequested) { files.Add(new(candidate.Id, candidate.ProjectId, candidate.Path, candidate.Scope, RestoreFileStatus.Cancelled, 0)); break; }
            if (!FileVersion.IsHash(candidate.Sha256 ?? ""))
            {
                files.Add(new(candidate.Id, candidate.ProjectId, candidate.Path, candidate.Scope, RestoreFileStatus.EvidenceInsufficient, 0,
                    Error: "Trusted SHA-256 evidence is unavailable; no content was restored."));
                cursor.LastKeys[group] = Key(candidate); continue;
            }
            if (maxBytes != 0 && candidate.Length > maxBytes - used)
            {
                files.Add(new(candidate.Id, candidate.ProjectId, candidate.Path, candidate.Scope, RestoreFileStatus.BudgetSkipped, 0, Error: "Restore-test byte budget reached."));
                // A later small/oversized/unknown entry must not repeatedly jump past
                // a file that would fit at the beginning of the next run.
                if (candidate.Length <= maxBytes) deferredStart ??= Key(candidate);
                continue;
            }
            used += candidate.Length;
            try { var result = await ExportCoreAsync(candidate, root, drive, token); files.Add(result); }
            catch (OperationCanceledException) { files.Add(new(candidate.Id, candidate.ProjectId, candidate.Path, candidate.Scope, RestoreFileStatus.Cancelled, 0)); break; }
            catch (Exception ex) when (ex is IOException or HttpRequestException or UnauthorizedAccessException or FormatException or ArgumentException)
            { files.Add(new(candidate.Id, candidate.ProjectId, candidate.Path, candidate.Scope, RestoreFileStatus.Failed, 0, Error: ex.Message)); }
            cursor.LastKeys[group] = Key(candidate);
        }
        if (deferredStart is not null)
        {
            var keys = ordered.Select(Key).Order(StringComparer.Ordinal).ToArray();
            cursor.LastKeys[group] = keys.LastOrDefault(k => string.CompareOrdinal(k, deferredStart) < 0) ?? keys[^1];
        }
        foreach (var verified in files.Where(f => f.Status == RestoreFileStatus.Verified).GroupBy(f => (f.ProjectId, f.Scope)))
        {
            var project = config.Projects.Single(p => p.ProjectId == verified.Key.ProjectId);
            var path = VerificationPath(config, project);
            try { AtomicJson.Write(path, new RestoreVerificationState(1, config.Preview.DeviceId, Account.AccountId, Account.DriveId,
                Account.RepositoryId, Account.RootId, CloudMapping.Bind(project, config), project.ProjectId, DateTimeOffset.UtcNow,
                verified.Count(), verified.Sum(f => f.Bytes), verified.Key.Scope), AtomicJson.Signature(AtomicJson.ReadText(path))); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { diagnostics.Add("Restore verification status could not be saved: " + ex.Message); }
        }
        if (testOnly)
            try { AtomicJson.Write(cursorPath, cursor with { NextGroup = (group + 1) % groupCount }, AtomicJson.Signature(AtomicJson.ReadText(cursorPath))); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { diagnostics.Add("Rotation cursor was not saved: " + ex.Message); }
        Directory.CreateDirectory(root); ValidateNoLinks(root);
        var report = new RestoreReport(testOnly ? "RestoreTest" : "Restore", started, DateTimeOffset.UtcNow, root, files, diagnostics, group, groupCount);
        var reportPath = Path.Combine(root, "restore-report-" + Guid.NewGuid().ToString("N") + ".json");
        AtomicJson.Write(reportPath, report, null);
        return report;
    }
}
