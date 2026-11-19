using System.Text.Json.Nodes;
using System.Text.Json;
using HarborBackup.Core.Domain;
using HarborBackup.Windows.FileSystem;
using HarborBackup.Windows.Integration;

namespace HarborBackup.Windows.Persistence.Configuration;

public enum ConfigurationPart { Projects, System }

/// <summary>A detached, lossless JSON draft. No filesystem writes until Save.</summary>
public sealed class ConfigurationDraft
{
    public JsonObject Projects { get; }
    public JsonObject System { get; }
    internal string? ProjectSignature { get; }
    internal string? SystemSignature { get; }
    internal string? OriginalProjects { get; }
    internal string? OriginalSystem { get; }
    internal ConfigurationDraft(string? projects, string? system)
    {
        OriginalProjects = projects;
        OriginalSystem = system;
        ProjectSignature = AtomicJson.Signature(projects);
        SystemSignature = AtomicJson.Signature(system);
        Projects = Parse(projects ?? "{\"schemaVersion\":2,\"folders\":[],\"builtInExclusions\":[],\"excludedDirectories\":[],\"excludedFiles\":[]}");
        System = Parse(system ?? "{\"schemaVersion\":1,\"sync\":{\"enabled\":true},\"schedule\":{\"intervalMinutes\":30,\"startDelayMinutes\":2,\"executionTimeLimitMinutes\":120},\"transfer\":{\"uploadKiBPerSecond\":0,\"downloadKiBPerSecond\":0}}");
    }
    private static JsonObject Parse(string text)
    {
        using var validated = JsonInput.Parse(text);
        return JsonNode.Parse(text, new JsonNodeOptions { PropertyNameCaseInsensitive = true })!.AsObject();
    }
    public ConfigurationDraft Copy()
    {
        var copy = new ConfigurationDraft(OriginalProjects, OriginalSystem);
        copy.Projects.Clear();
        copy.System.Clear();
        foreach (var pair in Projects) copy.Projects.Add(pair.Key, pair.Value?.DeepClone());
        foreach (var pair in System) copy.System.Add(pair.Key, pair.Value?.DeepClone());
        return copy;
    }
}

public sealed class ConfigurationEditor(RuntimePaths paths, string? deviceId = null, string? desktopRoot = null)
{
    private string Device => deviceId ?? DeviceIdentity.CurrentDevice();
    public ConfigurationDraft Load() => new(AtomicJson.ReadText(paths.ProjectConfig), AtomicJson.ReadText(paths.SystemConfig));

    public void RemoveProject(ConfigurationDraft draft, string projectId)
    {
        if (!FileVersion.IsHash(projectId)) throw new FormatException("Invalid project ID.");
        var folders = draft.Projects["folders"]!.AsArray();
        var entry = folders.FirstOrDefault(p => p?["id"]?.GetValue<string>() == projectId);
        var account = new CloudAccountStore(paths.Data).Read();
        if (account.DriveId is not null && account.RepositoryId is not null && account.RootId is not null)
        {
            var dismissed = draft.Projects["dismissedCloudProjects"]?.Deserialize<List<DismissedCloudProject>>(AtomicJson.Options) ?? [];
            if (!dismissed.Any(p => p.Matches(account, projectId)))
                dismissed.Add(new(account.DriveId, account.RepositoryId, account.RootId, projectId));
            draft.Projects["dismissedCloudProjects"] = JsonSerializer.SerializeToNode(dismissed, AtomicJson.Options);
        }
        if (entry is not null) folders.Remove(entry);
    }

    public ConfigurationSnapshot Validate(ConfigurationDraft draft)
    {
        var config = Parse(draft.Projects.ToJsonString(), draft.System.ToJsonString());
        var errors = config.Projects.Where(p => p.ConfigurationError is not null).Select(p => p.Destination + ": " + p.ConfigurationError).ToArray();
        if (errors.Length != 0) throw new FormatException(string.Join(Environment.NewLine, errors));
        UpdateMappings(draft, config);
        return Parse(draft.Projects.ToJsonString(), draft.System.ToJsonString());
    }

    private ConfigurationSnapshot Parse(string projects, string system) => ConfigurationReader.Parse(projects, system,
        paths.ProjectConfig, paths.SystemConfig, desktopRoot ?? Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory), Device, forExecution: false);

    private void UpdateMappings(ConfigurationDraft draft, ConfigurationSnapshot candidate)
    {
        var originalProjects = draft.OriginalProjects ?? "{\"schemaVersion\":2,\"folders\":[]}";
        var originalSystem = draft.OriginalSystem ?? new ConfigurationDraft(null, null).System.ToJsonString();
        ConfigurationSnapshot original;
        try { original = Parse(originalProjects, originalSystem); }
        catch (Exception ex) when (ex is FormatException or ArgumentException)
        {
            // Repairing one invalid entry must remain possible. Preserve every independently valid
            // original mapping; unrecoverable evidence still blocks execution, never configuration repair.
            var document = JsonNode.Parse(originalProjects)!.AsObject();
            var valid = new List<ProjectDefinition>();
            if (document["folders"] is JsonArray folders)
                foreach (var entry in folders)
                {
                    var isolated = document.DeepClone().AsObject();
                    isolated["folders"] = new JsonArray(entry?.DeepClone());
                    try { valid.AddRange(Parse(isolated.ToJsonString(), originalSystem).Projects.Where(p => p.ConfigurationError is null)); }
                    catch (Exception error) when (error is FormatException or ArgumentException) { }
                }
            original = candidate with { Projects = valid };
        }
        var history = original.RetiredMappings.ToList();
        foreach (var old in original.Projects.Where(p => p.ConfigurationError is null))
        {
            var next = candidate.Projects.FirstOrDefault(p => p.TaskId == old.TaskId);
            if (next is not null && SameMapping(old, next)) continue;
            var snapshot = CloudMapping.Capture(old, original);
            if (!history.Any(m => m.Binding == snapshot.Binding)) history.Add(snapshot);
        }
        if (history.Count > 0) draft.Projects["retiredMappings"] = JsonSerializer.SerializeToNode(history, AtomicJson.Options);
        foreach (var next in candidate.Projects)
        {
            var old = original.Projects.FirstOrDefault(p => p.TaskId == next.TaskId);
            var entry = draft.Projects["folders"]!.AsArray().Single(p => p!["id"]!.GetValue<string>() == next.ProjectId)!;
            entry["bindingVersion"] = 1;
            if (old is not null && SameMapping(old, next))
            {
                if (old.MappingVersion is null) entry.AsObject().Remove("mappingVersion");
                else entry["mappingVersion"] = old.MappingVersion;
            }
            else if (next.MappingVersion is null || next.MappingVersion == old?.MappingVersion)
                entry["mappingVersion"] = Guid.NewGuid().ToString("N");
        }
    }

    private static bool SameMapping(ProjectDefinition a, ProjectDefinition b) =>
        CloudMapping.SameLocation(a, b);

    public PreviewReport Preview(ConfigurationDraft draft, string? destination = null, SyncDirection direction = SyncDirection.Backup,
        CancellationToken token = default, IProgress<HarborBackup.Core.Domain.BackupProgress>? progress = null,
        Func<HarborBackup.OneDrive.Transport.ICloudDrive>? driveFactory = null)
    {
        token.ThrowIfCancellationRequested();
        using var runLock = new RunLock();
        var snapshot = Validate(draft);
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
        deadline.CancelAfter(TimeSpan.FromMinutes(snapshot.ExecutionTimeLimitMinutes));
        try
        {
            var report = new CloudBackupRuntime(snapshot, driveFactory).PreviewAsync(direction, destination, false, deadline.Token, progress: progress).GetAwaiter().GetResult();
            ConfigurationSnapshot? original = null;
            try { original = Parse(draft.OriginalProjects ?? "{\"schemaVersion\":2,\"folders\":[]}", draft.OriginalSystem ?? new ConfigurationDraft(null, null).System.ToJsonString()); }
            catch (Exception ex) when (ex is FormatException or ArgumentException) { /* An invalid original cannot provide comparable counts. */ }
            return ConfigurationImpactReader.ReadAsync(original, snapshot, report, direction, driveFactory, deadline.Token, progress).GetAwaiter().GetResult();
        }
        catch (OperationCanceledException) when (!token.IsCancellationRequested)
        { throw new TimeoutException("PreviewTimeLimitReached"); }
    }

    public void Save(ConfigurationDraft draft, ConfigurationPart part)
    {
        using var runLock = new RunLock();
        // Each page saves one document. The other document is the actual disk version, not an unrelated draft.
        var currentProjects = AtomicJson.ReadText(paths.ProjectConfig);
        var currentSystem = AtomicJson.ReadText(paths.SystemConfig);
        if (AtomicJson.Signature(currentProjects) != draft.ProjectSignature || AtomicJson.Signature(currentSystem) != draft.SystemSignature)
            throw new IOException("配置已被其他程序修改。草稿已保留，请重新加载并比较后再保存。");
        var candidate = new ConfigurationDraft(currentProjects, currentSystem);
        var selected = part == ConfigurationPart.Projects ? candidate.Projects : candidate.System;
        selected.Clear();
        foreach (var pair in part == ConfigurationPart.Projects ? draft.Projects : draft.System) selected.Add(pair.Key, pair.Value?.DeepClone());
        if (part == ConfigurationPart.System) BackupSettingsReader.Parse(candidate.System.ToJsonString());
        else Validate(candidate);
        if (AtomicJson.Signature(AtomicJson.ReadText(paths.ProjectConfig)) != draft.ProjectSignature ||
            AtomicJson.Signature(AtomicJson.ReadText(paths.SystemConfig)) != draft.SystemSignature)
            throw new IOException("校验期间配置发生变化，请重新加载后再保存。");
        // A missing companion config is initialized first. Partial initialization contains no runnable projects.
        if (part == ConfigurationPart.Projects && currentSystem is null)
            AtomicJson.Write(paths.SystemConfig, new ConfigurationDraft(null, null).System, null);
        if (part == ConfigurationPart.System && currentProjects is null)
            AtomicJson.Write(paths.ProjectConfig, new ConfigurationDraft(null, null).Projects, null);
        AtomicJson.Write(part == ConfigurationPart.Projects ? paths.ProjectConfig : paths.SystemConfig, selected,
            part == ConfigurationPart.Projects ? draft.ProjectSignature : draft.SystemSignature);
    }

}
