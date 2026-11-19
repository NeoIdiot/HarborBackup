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
        bool automatic = false, PullSelection? confirmedUpload = null)
    {
        if (direction == SyncDirection.Pull && string.IsNullOrWhiteSpace(destination) && !recoverOnly)
            throw new ArgumentException("Pull requires an explicit destination project.");
        using var runLock = new RunLock();
        var config = Load();
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
        deadline.CancelAfter(TimeSpan.FromMinutes(config.ExecutionTimeLimitMinutes));
        return new CloudBackupRuntime(config).ExecuteAsync(direction, destination, fullCheck, recoverOnly, structuredProgress, deadline.Token, confirmedPull, automatic, confirmedUpload).GetAwaiter().GetResult();
    }

    public static IReadOnlyList<ProjectDefinition> Select(ConfigurationSnapshot config, string? destination)
    {
        if (destination is null) return config.Projects;
        var selected = config.Projects.Where(p => p.ProjectId == destination).ToArray();
        // Destination is a human-facing selector for CLI calls, never a storage identity.
        if (selected.Length == 0) selected = config.Projects.Where(p => p.Destination.Equals(destination, StringComparison.OrdinalIgnoreCase)).ToArray();
        if (selected.Length == 0) throw new ArgumentException("Unknown destination.");
        return selected;
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
