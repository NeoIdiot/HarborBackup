using HarborBackup.Core.Domain;
using HarborBackup.Windows.FileSystem;
using HarborBackup.Windows.Persistence;
using HarborBackup.Windows.Persistence.Configuration;

namespace HarborBackup.Windows.Integration;

public sealed record RuntimePaths(string ProjectConfig, string SystemConfig) { public DataPaths Data => DataPaths.FromConfig(SystemConfig); }

/// <summary>Synchronous host boundary keeps the named Mutex on its acquiring thread.</summary>
public sealed class BackupRuntime(RuntimePaths paths)
{
    public ConfigurationSnapshot Load(bool forExecution = true)
    {
        if (!forExecution) return ConfigurationReader.Parse(File.ReadAllText(paths.ProjectConfig), File.ReadAllText(paths.SystemConfig),
            paths.ProjectConfig, paths.SystemConfig, Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory), DeviceIdentity.CurrentDevice(), forExecution: false);
        return ConfigurationReader.Read(paths.ProjectConfig, paths.SystemConfig,
            Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory), DeviceIdentity.CurrentDevice());
    }
    public PreviewReport Preview(SyncDirection direction, string? destination = null, bool fullCheck = false, CancellationToken token = default, IProgress<BackupProgress>? structuredProgress = null, bool prepareUpload = false, bool verifyOnly = false)
    {
        using var runLock = new RunLock();
        var config = Load();
        var report = new CloudBackupRuntime(config).PreviewAsync(direction, destination, fullCheck || verifyOnly, token, structuredProgress, prepareUpload).GetAwaiter().GetResult();
        return verifyOnly ? report with { Mode = "Verify" } : report;
    }

    public BackupReport Execute(SyncDirection direction, string? destination = null, bool fullCheck = false,
        bool recoverOnly = false, CancellationToken token = default, IProgress<BackupProgress>? structuredProgress = null, PullSelection? confirmedPull = null,
        bool automatic = false, PullSelection? confirmedUpload = null, bool retryOnly = false)
    {
        if (direction == SyncDirection.Pull && string.IsNullOrWhiteSpace(destination) && !recoverOnly)
            throw new ArgumentException("Pull requires an explicit destination project.");
        using var runLock = new RunLock();
        var config = Load();
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
        deadline.CancelAfter(TimeSpan.FromMinutes(config.ExecutionTimeLimitMinutes));
        return new CloudBackupRuntime(config).ExecuteAsync(direction, destination, fullCheck, recoverOnly, structuredProgress, deadline.Token, confirmedPull, automatic, confirmedUpload, retryOnly).GetAwaiter().GetResult();
    }

    public static IReadOnlyList<ProjectDefinition> Select(ConfigurationSnapshot config, string? projectId)
    {
        if (projectId is null) return config.Projects;
        if (!FileVersion.IsHash(projectId)) throw new ArgumentException("Invalid project ID.");
        var selected = config.Projects.Where(p => p.ProjectId == projectId).ToArray();
        if (selected.Length != 1) throw new ArgumentException("Unknown or ambiguous project ID.");
        return selected;
    }

    // Human-readable selectors are resolved once at an application entry point. Every
    // subsequent project lookup, confirmation and business association uses the returned ID.
    public static string? ResolveProjectId(ConfigurationSnapshot config, string? selector) =>
        ResolveProjectId(config.Projects, selector);

    internal static string? ResolveProjectId(IEnumerable<ProjectDefinition> projects, string? selector)
    {
        if (selector is null) return null;
        var available = projects.ToArray();
        var identified = available.Where(p => p.ProjectId == selector).Select(p => p.ProjectId).Distinct().ToArray();
        if (identified.Length == 1) return identified[0];
        // An unknown ID must never accidentally select a folder whose name is that ID.
        if (FileVersion.IsHash(selector)) throw new ArgumentException("Unknown project ID.");
        var aliases = available.Where(p => p.Destination.Equals(selector, StringComparison.OrdinalIgnoreCase))
            .Select(p => p.ProjectId).Distinct().ToArray();
        if (aliases.Length != 1) throw new ArgumentException("Unknown or ambiguous project selector.");
        return aliases[0];
    }

    public RestoreCatalog ListRestore(string? destination = null, RestoreScope? scope = null, string? path = null, CancellationToken token = default)
    {
        using var runLock = new RunLock();
        return new CloudRestoreService(Load()).ListAsync(destination, scope, path, token).GetAwaiter().GetResult();
    }

    public RestoreReport Restore(IReadOnlyList<RestoreCandidate> candidates, string outputRoot, bool testOnly = false,
        long maxBytes = 0, int groupCount = 1, CancellationToken token = default)
    {
        using var runLock = new RunLock();
        return new CloudRestoreService(Load()).ExecuteAsync(candidates, outputRoot, testOnly, maxBytes, groupCount, token).GetAwaiter().GetResult();
    }
}
