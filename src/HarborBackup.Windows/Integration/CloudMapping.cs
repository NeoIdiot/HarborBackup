using System.Text.Json;
using HarborBackup.Core.Domain;
using HarborBackup.Windows.FileSystem;
using HarborBackup.Windows.Persistence;

namespace HarborBackup.Windows.Integration;

public sealed record PendingCloudFile(CloudMapping Mapping, string Path)
{
    internal string? JournalId { get; init; }
    internal PreparedPullRestart? Restart { get; init; }

    public bool CanRestartPull(ProjectDefinition project, PreviewFile file, SyncDirection direction) =>
        direction == SyncDirection.Pull && Restart is not null && Mapping.Project.TaskId == project.TaskId &&
        Mapping.Project.Destination.Equals(project.Destination, StringComparison.OrdinalIgnoreCase) &&
        Path.Equals(file.RelativePath, StringComparison.OrdinalIgnoreCase) &&
        file.Source.Kind == ObservationKind.Missing && file.Target.Kind == ObservationKind.File;

    public bool Blocks(ProjectDefinition project, PreviewFile file, SyncDirection direction) =>
        Affects(project, file.RelativePath) && !CanRestartPull(project, file, direction) &&
        !(direction == SyncDirection.Pull && Restart is not null && Mapping.Project.TaskId == project.TaskId &&
          Mapping.Project.Destination.Equals(project.Destination, StringComparison.OrdinalIgnoreCase) &&
          Path.StartsWith(file.RelativePath + "/", StringComparison.OrdinalIgnoreCase) &&
          file.Source.Kind is ObservationKind.Missing or ObservationKind.Directory && file.Target.Kind == ObservationKind.Directory);

    internal static FileDecision Describe(FileDecision decision, IReadOnlyList<PendingCloudFile> restarts) =>
        restarts.Count > 0 && decision.Action == FileAction.Copy
            ? decision with { Reason = "RetryInterruptedPull" } : decision;

    internal static PullFileSelection Confirm(PullFileSelection selection, IReadOnlyList<PendingCloudFile> restarts) =>
        restarts.Count == 0 ? selection : selection with
        {
            Confirmation = DeviceIdentity.Digest(selection.Confirmation + "/" + string.Join("/", restarts
                .Select(p => p.Mapping.Binding + "/" + p.Restart!.Id + "/" + p.Restart.Signature).Order(StringComparer.Ordinal)))
        };
    private static bool CloudOverlap(string a, string b) => a.Equals(b, StringComparison.OrdinalIgnoreCase) ||
        a.StartsWith(b + "/", StringComparison.OrdinalIgnoreCase) || b.StartsWith(a + "/", StringComparison.OrdinalIgnoreCase);

    public bool Affects(ProjectDefinition project, string? relativePath = null)
    {
        var cloud = project.Destination + (relativePath is null ? "" : "/" + relativePath);
        var local = relativePath is null ? project.Source : System.IO.Path.Combine(project.Source, relativePath);
        return CloudOverlap(Mapping.Project.Destination + (Path.Length == 0 ? "" : "/" + Path), cloud) ||
            PathRules.Overlaps(System.IO.Path.Combine(Mapping.Project.Source, Path), local) ||
            Mapping.Project.CloudFolderId is not null && Mapping.Project.CloudFolderId == project.CloudFolderId &&
            (relativePath is null || CloudOverlap(Path, relativePath));
    }
}

// Local recovery evidence. Absolute source paths are never published to the cloud.
public sealed record CloudMapping(int SchemaVersion, string DeviceId, string? AccountId, string? DriveId,
    string? RepositoryId, string? RootId, ProjectDefinition Project, string Binding, string Signature)
{
    public static string Bind(ProjectDefinition project, ConfigurationSnapshot config)
    {
        var account = config.CloudAccount!;
        if (project.BindingVersion == 1)
            return DeviceIdentity.Digest("direct-v3/" + JsonSerializer.Serialize(new
            {
                config.Preview.DeviceId, account.AccountId, account.DriveId, account.RepositoryId, account.RootId,
                project.TaskId, Source = project.Source.ToUpperInvariant(), Destination = project.Destination.ToUpperInvariant(),
                Target = project.Target.ToUpperInvariant(), project.CloudFolderId, project.MappingVersion
            }));
        var legacy = "direct-v2/" + config.Preview.DeviceId + "/" + account.AccountId + "/" + account.DriveId + "/" +
            account.RepositoryId + "/" + account.RootId + "/" + project.TaskId + "/" + project.Source + "/" + project.Fingerprint;
        return DeviceIdentity.Digest(project.MappingVersion is null ? legacy : legacy + "/" + JsonSerializer.Serialize(project));
    }

    public static CloudMapping Capture(ProjectDefinition project, ConfigurationSnapshot config)
    {
        var account = config.CloudAccount!;
        var value = new CloudMapping(1, config.Preview.DeviceId, account.AccountId, account.DriveId, account.RepositoryId,
            account.RootId, project, Bind(project, config), "");
        return value with { Signature = value.Checksum() };
    }

    private string Checksum() => DeviceIdentity.Digest(JsonSerializer.Serialize(this with { Signature = "" }));
    public bool SameAccount(ConfigurationSnapshot config) => DeviceId == config.Preview.DeviceId &&
        AccountId == config.CloudAccount?.AccountId && DriveId == config.CloudAccount?.DriveId &&
        RepositoryId == config.CloudAccount?.RepositoryId && RootId == config.CloudAccount?.RootId;

    public void Validate(ConfigurationSnapshot config)
    {
        if (SchemaVersion != 1 || Project is null || Project.BindingVersion is not (0 or 1) || !SameAccount(config) || Signature != Checksum() ||
            Binding != Bind(Project, config) || Project.ConfigurationError is not null ||
            !FileVersion.IsHash(Project.TaskId) || !FileVersion.IsHash(Project.Fingerprint) ||
            Project.MappingVersion is not null && !Guid.TryParseExact(Project.MappingVersion, "N", out _))
            throw new IOException("Recovery mapping identity mismatch. Evidence retained.");
        PathRules.Absolute(Project.Source);
        PathRules.Relative(Project.Destination);
        if (PathRules.Overlaps(Project.Source, DataPaths.FromState(config.Preview.StateDirectory).Root) ||
            PathRules.Overlaps(Project.Source, ApplicationPaths.InstallRoot()))
            throw new IOException("Recovery source overlaps internal storage.");
    }

    public static string StateRoot(ProjectDefinition project, ConfigurationSnapshot config) =>
        Path.Combine(config.Preview.StateDirectory, "devices", config.Preview.DeviceId, "cloud", Bind(project, config));

    public static bool SameLocation(ProjectDefinition a, ProjectDefinition b) => a.TaskId == b.TaskId &&
        a.Source.Equals(b.Source, StringComparison.OrdinalIgnoreCase) &&
        a.Destination.Equals(b.Destination, StringComparison.OrdinalIgnoreCase) &&
        a.Target.Equals(b.Target, StringComparison.OrdinalIgnoreCase) && a.CloudFolderId == b.CloudFolderId;

    // Read-only continuity, newest first. A real relocation is a barrier, even after switching back.
    // Old rule edits changed MappingVersion; only those legacy transitions may cross versions.
    public static IEnumerable<CloudMapping> PreviousEvidence(ProjectDefinition project, ConfigurationSnapshot config)
    {
        if (project.BindingVersion != 1) yield break;
        var previous = project;
        var legacyContinuation = false;
        var seen = new HashSet<string> { Bind(project, config) };
        foreach (var mapping in config.RetiredMappings.Reverse().Where(m => m.Project?.TaskId == project.TaskId))
        {
            if (!mapping.SameAccount(config) || !SameLocation(project, mapping.Project)) yield break;
            mapping.Validate(config);
            if (previous.MappingVersion != mapping.Project.MappingVersion &&
                (!legacyContinuation || mapping.Project.BindingVersion != 0 || previous.Fingerprint == mapping.Project.Fingerprint)) yield break;
            // Rule-induced legacy resets have a retained snapshot of the active version.
            // A newly joined mapping must never infer continuity solely from changed rules.
            if (mapping.Project.BindingVersion == 0 && mapping.Project.MappingVersion == project.MappingVersion)
                legacyContinuation = true;
            if (seen.Add(mapping.Binding)) yield return mapping;
            previous = mapping.Project;
        }
    }

    private static string ReadyPath(ProjectDefinition project, ConfigurationSnapshot config) =>
        Path.Combine(StateRoot(project, config), "mapping-ready.json");

    public static bool NeedsFirstOperation(ProjectDefinition project, ConfigurationSnapshot config)
    {
        if (project.MappingVersion is null) return false;
        try
        {
            foreach (var candidate in new[] { project }.Concat(PreviousEvidence(project, config).Select(m => m.Project)))
            {
                var path = ReadyPath(candidate, config);
                SafePath.Validate(path, config.Preview.StateDirectory);
                var text = AtomicJson.ReadText(path);
                if (text is not null) return JsonSerializer.Deserialize<string>(text, AtomicJson.Options) != Bind(candidate, config);
                // Unversioned mappings predate the explicit opt-in marker. Require retained state
                // before using such a predecessor to activate a versioned mapping.
                if (candidate.MappingVersion is null && Directory.Exists(StateRoot(candidate, config))) return false;
            }
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException or FormatException or ArgumentException)
        { return true; } // Damaged proof never authorizes automatic writes; observation reports the evidence failure.
        return true;
    }

    public static void MarkReady(ProjectDefinition project, ConfigurationSnapshot config)
    {
        if (project.MappingVersion is null) return;
        var path = ReadyPath(project, config);
        SafePath.Validate(path, config.Preview.StateDirectory);
        AtomicJson.Write(path, Bind(project, config), AtomicJson.Signature(AtomicJson.ReadText(path)));
    }

    public static bool Overlaps(ProjectDefinition a, ProjectDefinition b) => a.TaskId == b.TaskId ||
        a.CloudFolderId is not null && a.CloudFolderId == b.CloudFolderId ||
        a.Destination.Equals(b.Destination, StringComparison.OrdinalIgnoreCase) ||
        a.Destination.StartsWith(b.Destination + "/", StringComparison.OrdinalIgnoreCase) ||
        b.Destination.StartsWith(a.Destination + "/", StringComparison.OrdinalIgnoreCase) || PathRules.Overlaps(a.Source, b.Source);

    public static IReadOnlyList<CloudMapping> Pending(ConfigurationSnapshot config) =>
        PendingFiles(config).Select(p => p.Mapping).DistinctBy(m => m.Binding).ToArray();

    public static IReadOnlyList<PendingCloudFile> PendingFiles(ConfigurationSnapshot config)
    {
        var scan = ScanPending(config);
        if (scan.Problems.Count > 0) throw new IOException(string.Join(Environment.NewLine, scan.Problems.Select(p => p.Description)));
        return scan.Files;
    }

    public static CloudPendingScan ScanPending(ConfigurationSnapshot config)
    {
        var root = Path.Combine(config.Preview.StateDirectory, "devices", config.Preview.DeviceId, "cloud");
        var result = new List<PendingCloudFile>();
        var problems = new List<CloudRecoveryProblem>();
        static bool Invalid(Exception ex) => ex is IOException or UnauthorizedAccessException or JsonException or FormatException or ArgumentException;
        try
        {
            SafePath.Validate(root, config.Preview.StateDirectory);
            if (!Directory.Exists(root)) return new(result, problems);
            var known = config.Projects.Where(p => p.ConfigurationError is null).Select(p => Capture(p, config))
                .Concat(config.RetiredMappings).ToArray();
            foreach (var directory in Directory.EnumerateDirectories(root))
            {
                CloudMapping? scope = null;
                try
                {
                    var candidate = known.FirstOrDefault(m => m.Binding == Path.GetFileName(directory) && m.SameAccount(config));
                    if (candidate is not null) { candidate.Validate(config); scope = candidate; }
                    SafePath.Validate(directory, root);
                    var journals = Path.Combine(directory, "cloud-transactions");
                    SafePath.Validate(journals, root);
                    if (!Directory.Exists(journals)) continue;
                    foreach (var path in Directory.EnumerateFileSystemEntries(journals))
                    {
                        var mapping = scope;
                        try
                        {
                            SafePath.Validate(path, journals);
                            var text = AtomicJson.ReadText(path)!;
                            var journal = JsonSerializer.Deserialize<CloudJournal>(text, AtomicJson.Options)
                                ?? throw new IOException("Invalid pending transaction. Evidence retained.");
                            if (journal.Binding != Path.GetFileName(directory))
                            {
                                mapping = null;
                                throw new IOException("Recovery journal location mismatch. Evidence retained.");
                            }
                            if (journal.SchemaVersion is not (2 or 3 or 4 or 5))
                                throw new IOException($"无法识别恢复记录格式（版本 {journal.SchemaVersion}）。记录已保留，请勿删除恢复材料。");
                            var original = journal.SchemaVersion == 5 ? journal.Mapping : known
                                .FirstOrDefault(m => m.Binding == journal.Binding && m.SameAccount(config));
                            if (original is null) throw new IOException("恢复记录缺少原目录映射。请补回原项目配置后恢复；保存新配置不受影响。");
                            original.Validate(config);
                            if (original.Binding != journal.Binding || Path.GetFileName(directory) != journal.Binding)
                            {
                                mapping = null; // Contradicting valid identities do not establish a reliable scope.
                                throw new IOException("Recovery journal location mismatch. Evidence retained.");
                            }
                            mapping = original;
                            if (!Guid.TryParseExact(journal.Id, "N", out _) || Path.GetFileName(path) != journal.Id + ".json" ||
                                PathRules.Relative(journal.Path) != journal.Path)
                                throw new IOException("Recovery journal location mismatch. Evidence retained.");
                            var restart = journal.Remote?.Project == mapping.Project.ProjectId
                                ? PreparedPullRestart.Read(path, text, journal) : null;
                            result.Add(new(mapping, journal.Path) { JournalId = journal.Id, Restart = restart });
                        }
                        catch (Exception ex) when (Invalid(ex)) { problems.Add(new(mapping, path, ex.Message)); }
                    }
                }
                catch (Exception ex) when (Invalid(ex)) { problems.Add(new(scope, directory, ex.Message)); }
            }
        }
        catch (Exception ex) when (Invalid(ex)) { problems.Add(new(null, root, ex.Message)); }
        return new(result, problems);
    }
}
