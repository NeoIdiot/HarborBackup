using HarborBackup.Windows.FileSystem;
using HarborBackup.Windows.Persistence.Configuration;
using HarborBackup.Core.Domain;
using HarborBackup.Windows.Persistence;
using System.Text.Json;

namespace HarborBackup.Windows.Integration;

public sealed record FreshDataResult(string DataRoot, string? RetainedData);
public sealed record InstallationDataResult(string Action, string Reason, string DataRoot, string? RetainedData);

// Explicit fresh start. Existing data is retained intact and is never interpreted or migrated.
public static class FreshDataInitializer
{
    public static FreshDataResult Recreate(RuntimePaths paths, string? installationName = null)
    {
        installationName ??= InstallationGuard.Namespace;
        using var gate = new Mutex(false, installationName + ".Startup");
        var held = false;
        Mutex? maintenance = null;
        try
        {
            try { held = gate.WaitOne(TimeSpan.FromSeconds(5)); }
            catch (AbandonedMutexException) { held = true; }
            if (!held) throw new IOException("Installation is busy.");
            foreach (var suffix in new[] { ".Running", ".Maintenance" })
                if (Mutex.TryOpenExisting(installationName + suffix, out var active))
                { active.Dispose(); throw new IOException("Exit HarborBackup and other CLI operations before initializing fresh data."); }
            maintenance = new Mutex(false, installationName + ".Maintenance");
        }
        finally { if (held) gate.ReleaseMutex(); }
        using (maintenance)
        using (var runLock = new RunLock())
            return RecreateCore(paths);
    }

    public static InstallationDataResult PrepareInstallation(RuntimePaths paths, string lease, string? installationName = null)
    {
        installationName ??= InstallationGuard.Namespace;
        var prefix = installationName + ".Initialize.";
        if (!lease.StartsWith(prefix, StringComparison.Ordinal) || !FileVersion.IsHash(lease[prefix.Length..]))
            throw new IOException("Invalid installation lease.");
        using var gate = new Mutex(false, installationName + ".Startup");
        var held = false;
        try
        {
            try { held = gate.WaitOne(TimeSpan.FromSeconds(5)); }
            catch (AbandonedMutexException) { held = true; }
            if (!held) throw new IOException("Installation is busy.");
            using var owner = OpenMarker(lease);
            using var maintenance = OpenMarker(installationName + ".Maintenance");
            if (Mutex.TryOpenExisting(installationName + ".Running", out var running))
            { running.Dispose(); throw new IOException("An application is running during installation."); }
            using var runLock = new RunLock();
            var reason = RebuildReason(paths);
            if (reason is null) return new("Preserved", "CurrentFormat", paths.Data.Root, null);
            var existed = Directory.Exists(paths.Data.Root);
            var result = RecreateCore(paths);
            return new(existed ? "Rebuilt" : "Created", reason, result.DataRoot, result.RetainedData);
        }
        finally { if (held) gate.ReleaseMutex(); }
    }

    private static Mutex OpenMarker(string name) => Mutex.TryOpenExisting(name, out var marker) ? marker :
        throw new IOException("Installation lease is no longer active.");

    private static string? RebuildReason(RuntimePaths paths)
    {
        if (!Directory.Exists(paths.Data.Root)) return "FreshInstallation";
        foreach (var path in new[] { paths.ProjectConfig, paths.SystemConfig }) SafePath.Validate(path, paths.Data.Root);
        if (!File.Exists(paths.ProjectConfig) || !File.Exists(paths.SystemConfig)) return "MissingConfiguration";
        try
        {
            using var projects = JsonInput.Read(paths.ProjectConfig);
            var root = projects.RootElement;
            if (JsonInput.Integer(JsonInput.Get(root, "schemaVersion"), -1, -1, int.MaxValue) != 2)
                return "UnsupportedProjectFormat";
            var folders = JsonInput.Get(root, "folders");
            if (folders.ValueKind != JsonValueKind.Array) return "InvalidProjectConfiguration";
            var ids = new HashSet<string>(StringComparer.Ordinal);
            foreach (var folder in folders.EnumerateArray())
            {
                var id = JsonInput.Text(JsonInput.Get(folder, "id"));
                if (!FileVersion.IsHash(id) || !ids.Add(id)) return "InvalidProjectIdentity";
                _ = JsonInput.Text(JsonInput.Get(folder, "source"));
                _ = PathRules.Relative(JsonInput.Text(JsonInput.Get(folder, "destination")), true);
            }
            _ = BackupSettingsReader.Parse(File.ReadAllText(paths.SystemConfig));
            var accountPath = Path.Combine(paths.Data.Config, "onedrive-account.json");
            SafePath.Validate(accountPath, paths.Data.Root);
            if (File.Exists(accountPath))
            {
                using var account = JsonInput.Read(accountPath);
                if (JsonInput.Integer(JsonInput.Get(account.RootElement, "schemaVersion"), -1, -1, int.MaxValue) != 2)
                    return "UnsupportedAccountFormat";
            }
            _ = RunStatusStore.Read(new(paths.Data.State, DeviceIdentity.CurrentDevice(), new()));
            return null;
        }
        catch (Exception ex) when (ex is JsonException or FormatException)
        { return "InvalidConfiguration"; }
    }

    private static FreshDataResult RecreateCore(RuntimePaths paths)
        {
            var data = paths.Data;
            var root = Path.GetFullPath(data.Root);
            var parent = Directory.GetParent(root)?.FullName ?? throw new IOException("Invalid data root.");
            if (!Path.GetFileName(root).Equals("data", StringComparison.OrdinalIgnoreCase) ||
                !Path.GetFullPath(paths.ProjectConfig).Equals(data.Projects, StringComparison.OrdinalIgnoreCase) ||
                !Path.GetFullPath(paths.SystemConfig).Equals(data.BackupSettings, StringComparison.OrdinalIgnoreCase))
                throw new IOException("Fresh initialization requires the standard data/config layout.");
            var retained = Path.GetFullPath(Path.Combine(parent, "data-retained-" + Guid.NewGuid().ToString("N")));
            var staged = Path.GetFullPath(Path.Combine(parent, "data-new-" + Guid.NewGuid().ToString("N")));
            foreach (var target in new[] { root, retained, staged })
            {
                if (!target.StartsWith(parent + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                    throw new IOException("Data initialization escapes its parent directory.");
                SafePath.Validate(target, parent);
            }
            var fresh = new DataPaths(staged);
            var editor = new ConfigurationEditor(new(fresh.Projects, fresh.BackupSettings));
            editor.Save(editor.Load(), ConfigurationPart.Projects);
            var existed = Directory.Exists(root);
            if (existed) Directory.Move(root, retained);
            try { Directory.Move(staged, root); }
            catch { if (existed && !Directory.Exists(root)) Directory.Move(retained, root); throw; }
            return new(root, existed ? retained : null);
    }
}
