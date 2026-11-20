using System.Text.Json;
using HarborBackup.Core.Domain;
using HarborBackup.Windows.FileSystem;
using HarborBackup.Windows.Integration;
using static HarborBackup.Windows.Persistence.JsonInput;
using HarborBackup.Windows.Persistence.Configuration;

namespace HarborBackup.Windows.Persistence;

public sealed record ConfigurationSnapshot(IReadOnlyList<ProjectDefinition> Projects, PreviewSettings Preview,
    int IntervalMinutes, int StartDelayMinutes, string ConfigurationRoot)
{
    public CloudAccountSettings? CloudAccount { get; init; }
    public IReadOnlyList<CloudMapping> RetiredMappings { get; init; } = [];
    public IReadOnlyList<DismissedCloudProject> DismissedCloudProjects { get; init; } = [];
    // Local execution policy is separate from persisted recovery mappings and their signatures.
    public IReadOnlyDictionary<string, AutomaticBackupMode> AutomaticBackupModes { get; init; } = new Dictionary<string, AutomaticBackupMode>(StringComparer.Ordinal);
    public AutomaticBackupMode GetAutomaticBackupMode(string projectId) => AutomaticBackupModes.GetValueOrDefault(projectId, AutomaticBackupMode.UploadOnly);
    public IReadOnlySet<string> PausedProjects { get; init; } = new HashSet<string>(StringComparer.Ordinal);
    public bool IsProjectPaused(string projectId) => PausedProjects.Contains(projectId);
    public string ArchiveDirectory { get; init; } = "";
    public string ConflictDirectory { get; init; } = "";
    public string LogDirectory { get; init; } = "";
    public string HistoryDirectory { get; init; } = "";
    public int LogRetentionDays { get; init; } = 30;
    public int HistoryRetentionDays { get; init; } = 90;
    public int ExecutionTimeLimitMinutes { get; init; } = 120;
    public AutomaticBackupBudget AutomaticBudget { get; init; } = new();
    public int UploadKiBPerSecond { get; init; }
    public int DownloadKiBPerSecond { get; init; }
    public LocalRetention ArchiveRetention { get; init; }
    public LocalRetention ConflictRetention { get; init; }
}

public static class ConfigurationReader
{
    public static ConfigurationSnapshot Read(string projectFile, string systemFile,
        string desktopRoot, string deviceId, Func<string, string?>? environment = null)
    {
        SafePath.Validate(projectFile);
        SafePath.Validate(systemFile);
        return Parse(File.ReadAllText(projectFile), File.ReadAllText(systemFile), projectFile, systemFile,
            desktopRoot, deviceId, environment);
    }

    public static ConfigurationSnapshot Parse(string projectJson, string systemJson, string projectFile, string systemFile,
        string desktopRoot, string deviceId, Func<string, string?>? environment = null, bool forExecution = true)
    {
        projectFile = PathRules.Absolute(projectFile);
        systemFile = PathRules.Absolute(systemFile);
        var account = new CloudAccountStore(DataPaths.FromConfig(systemFile)).Read();
        if (forExecution && (!account.Enabled || account.RequiresAuthorization)) throw new InvalidOperationException("Connect OneDrive and select a cloud repository in settings.");
        if (!FileVersion.IsHash(deviceId)) throw new FormatException("Invalid device ID.");
        SafePath.Validate(projectFile);
        SafePath.Validate(systemFile);
        using var projectDoc = JsonInput.Parse(projectJson);
        var values = BackupSettingsReader.Parse(systemJson);
        var data = DataPaths.FromConfig(systemFile);
        var root = data.Root;
        var state = data.State;
        var conflict = data.Conflicts;
        var archive = data.Archives;
        var internals = new[] { root, archive, conflict, ApplicationPaths.InstallRoot() };
        foreach (var path in internals) SafePath.Validate(path);
        var log = data.Logs;
        var config = projectDoc.RootElement;
        if (config.ValueKind != JsonValueKind.Object || Get(config, "backupFolder").ValueKind != JsonValueKind.Undefined)
            throw new FormatException("Expected project configuration without obsolete backupFolder.");
        if (Integer(Get(config, "schemaVersion"), -1, 2, 2) != 2) throw new FormatException("Unsupported project settings version. Create a new project configuration.");
        var ids = new HashSet<string>();
        var folders = Get(config, "folders");
        if (folders.ValueKind != JsonValueKind.Array) throw new FormatException("folders must be an array.");
        var projects = new List<ProjectDefinition>();
        var automaticBackupModes = new Dictionary<string, AutomaticBackupMode>(StringComparer.Ordinal);
        var pausedProjects = new HashSet<string>(StringComparer.Ordinal);
        var retired = Get(config, "retiredMappings").ValueKind == JsonValueKind.Undefined ? new List<CloudMapping>() :
            JsonSerializer.Deserialize<List<CloudMapping>>(Get(config, "retiredMappings"), AtomicJson.Options)
            ?? throw new FormatException("Invalid retired mappings.");
        if (retired.Any(m => m is null || m.Project is null)) throw new FormatException("Invalid retired mappings.");
        var legacyProjects = new List<ProjectDefinition>();
        var presetValue = Get(config, "builtInExclusions");
        if (presetValue.ValueKind != JsonValueKind.Undefined && presetValue.ValueKind != JsonValueKind.Array)
            throw new FormatException("builtInExclusions must be an array.");
        var presets = ExclusionPresets.Resolve(presetValue.ValueKind == JsonValueKind.Undefined ? [] :
            presetValue.EnumerateArray().Select(v => v.ValueKind == JsonValueKind.String
                ? v.GetString()! : throw new FormatException("Built-in exclusion IDs must be strings.")));
        var dismissed = Get(config, "dismissedCloudProjects").ValueKind == JsonValueKind.Undefined ? [] :
            JsonSerializer.Deserialize<DismissedCloudProject[]>(Get(config, "dismissedCloudProjects"), AtomicJson.Options)
            ?? throw new FormatException("Invalid dismissed cloud projects.");
        foreach (var item in dismissed) (item ?? throw new FormatException("Invalid dismissed cloud project.")).Validate();
        foreach (var entry in folders.EnumerateArray())
        {
            if (entry.ValueKind != JsonValueKind.Object || Get(entry, "backupName").ValueKind != JsonValueKind.Undefined)
                throw new FormatException("Invalid project or obsolete backupName.");
            var projectId = Text(Get(entry, "id"));
            if (!FileVersion.IsHash(projectId) || !ids.Add(projectId)) throw new FormatException("Project IDs must be valid and unique.");
            automaticBackupModes.Add(projectId, ReadAutomaticBackupMode(entry));
            var paused = Get(entry, "backupPaused");
            if (paused.ValueKind == JsonValueKind.True) pausedProjects.Add(projectId);
            else if (paused.ValueKind is not (JsonValueKind.Undefined or JsonValueKind.False))
                throw new FormatException("backupPaused must be a boolean.");
            var destination = PathRules.Relative(Text(Get(entry, "destination")), true);
            if (projects.Any(p => p.Destination.Equals(destination, StringComparison.OrdinalIgnoreCase) || p.Destination.StartsWith(destination + "/", StringComparison.OrdinalIgnoreCase) || destination.StartsWith(p.Destination + "/", StringComparison.OrdinalIgnoreCase))) throw new FormatException("Cloud project destinations overlap.");
            if (destination.Equals(account.RootName, StringComparison.OrdinalIgnoreCase) ||
                destination.StartsWith(account.RootName + "/", StringComparison.OrdinalIgnoreCase) ||
                account.RootName.StartsWith(destination + "/", StringComparison.OrdinalIgnoreCase))
                throw new FormatException("Cloud target overlaps HarborBackup management storage.");
            var target = "OneDrive/" + destination;
            var folderId = Get(entry, "cloudFolderId").ValueKind == JsonValueKind.String ? Text(Get(entry, "cloudFolderId")) : null;
            var mappingVersion = Get(entry, "mappingVersion").ValueKind == JsonValueKind.Undefined ? null : Text(Get(entry, "mappingVersion"));
            if (mappingVersion is not null && !Guid.TryParseExact(mappingVersion, "N", out _)) throw new FormatException("Invalid local mapping version.");
            var bindingVersion = Get(entry, "bindingVersion").ValueKind == JsonValueKind.Undefined ? 0 : Integer(Get(entry, "bindingVersion"), -1, 0, 1);
            if (bindingVersion is not (0 or 1)) throw new FormatException("Invalid mapping binding version.");
            try
            {
                var source = PathRules.Source(Text(Get(entry, "source")), desktopRoot, environment);
                if (internals.Any(p => PathRules.Overlaps(p, source)))
                    throw new FormatException("Source overlaps internal data directories.");
                var sources = new ExclusionRuleSources(presets.Select(p => p.Id).ToArray(),
                    Patterns(Get(config, "excludedDirectories")).OfType<string>().ToArray(),
                    Patterns(Get(config, "excludedFiles")).OfType<string>().ToArray(),
                    Patterns(Get(entry, "excludedDirectories")).OfType<string>().ToArray(),
                    Patterns(Get(entry, "excludedFiles")).OfType<string>().ToArray());
                var dirs = PathRules.Exclusions(source, sources.GlobalDirectories.Concat(sources.ProjectDirectories), true)
                    .Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
                var files = PathRules.Exclusions(source, presets.SelectMany(p => p.Files).Concat(sources.GlobalFiles).Concat(sources.ProjectFiles), false)
                    .Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
                var project = new ProjectDefinition(source, destination, target, dirs, files, projectId, DeviceIdentity.Fingerprint(dirs, files, target + "/" + folderId))
                    { CloudFolderId = folderId, MappingVersion = mappingVersion, BindingVersion = 1, ExclusionSources = sources };
                projects.Add(project);
                if (bindingVersion == 0) legacyProjects.Add(project with { BindingVersion = 0 });
            }
            catch (Exception ex) when (ex is FormatException or ArgumentException)
            {
                projects.Add(new("", destination, target, [], [], projectId, "", ex.Message));
            }
        }
        SafePath.Validate(log);
        if (projects.Any(p => (p.Source.Length != 0 && PathRules.Overlaps(log, p.Source))))
            throw new FormatException("Log directory overlaps business files.");
        var snapshot = new ConfigurationSnapshot(projects.AsReadOnly(), new(state, deviceId, values.Hash, values.ContinueOnError), values.IntervalMinutes, values.StartDelayMinutes, root)
        { CloudAccount = account, DismissedCloudProjects = dismissed, AutomaticBackupModes = automaticBackupModes, PausedProjects = pausedProjects,
          AutomaticBudget = values.AutomaticBudget,
          UploadKiBPerSecond = values.UploadKiBPerSecond, DownloadKiBPerSecond = values.DownloadKiBPerSecond,
          ArchiveRetention = account.ArchiveRetention, ConflictRetention = account.ConflictRetention, ArchiveDirectory = archive, ConflictDirectory = conflict, LogDirectory = log, HistoryDirectory = data.History, LogRetentionDays = values.LogRetentionDays, HistoryRetentionDays = values.HistoryRetentionDays, ExecutionTimeLimitMinutes = values.ExecutionLimitMinutes };
        // Keep the old scope addressable without copying, rewriting or deleting any state.
        foreach (var legacy in legacyProjects)
        {
            var mapping = CloudMapping.Capture(legacy, snapshot);
            if (!retired.Any(m => m.Binding == mapping.Binding)) retired.Add(mapping);
        }
        return snapshot with { RetiredMappings = retired.AsReadOnly() };
    }

    private static AutomaticBackupMode ReadAutomaticBackupMode(JsonElement entry)
    {
        var mode = Get(entry, "automaticBackupMode");
        if (mode.ValueKind != JsonValueKind.Undefined)
            return mode.ValueKind == JsonValueKind.String ? mode.GetString() switch
            {
                "upload" => AutomaticBackupMode.UploadOnly,
                "bidirectional" => AutomaticBackupMode.Bidirectional,
                "download" => AutomaticBackupMode.DownloadOnly,
                _ => throw new FormatException("automaticBackupMode must be upload, bidirectional or download.")
            } : throw new FormatException("automaticBackupMode must be a string.");
        var legacy = Get(entry, "automaticBidirectional");
        return legacy.ValueKind switch
        {
            JsonValueKind.True => AutomaticBackupMode.Bidirectional,
            JsonValueKind.Undefined or JsonValueKind.False => AutomaticBackupMode.UploadOnly,
            _ => throw new FormatException("automaticBidirectional must be a boolean.")
        };
    }

    private static IEnumerable<string?> Patterns(JsonElement array)
    {
        if (!Has(array)) yield break;
        if (array.ValueKind != JsonValueKind.Array) throw new FormatException("Exclusions must be arrays.");
        foreach (var item in array.EnumerateArray())
        {
            if (item.ValueKind == JsonValueKind.Null) continue;
            if (item.ValueKind != JsonValueKind.String) throw new FormatException("Exclusion entries must be strings.");
            yield return item.GetString();
        }
    }
}
