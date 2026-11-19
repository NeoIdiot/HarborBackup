using System.Text.Json;
using HarborBackup.Windows.Persistence;

[assembly: Parallelize(Scope = ExecutionScope.MethodLevel)]

namespace HarborBackup.Windows.Tests;

internal sealed class TestWorkspace : IDisposable
{
    private readonly string container = Path.Combine(Path.GetTempPath(), "HarborBackup.Tests");
    public string Root { get; }
    public string Source => Path.Combine(Root, "source");
    public string Cloud => Path.Combine(Root, "cloud");
    public string Config => Path.Combine(Root, "data", "config");
    public string State => Path.Combine(Root, "data", "state");
    public string Target => Path.Combine(Cloud, "Projects", "Demo");
    public string Device { get; } = new('d', 64);

    public TestWorkspace()
    {
        Root = Path.Combine(container, Guid.NewGuid().ToString("N"));
        foreach (var path in new[] { Source, Cloud, Config }) Directory.CreateDirectory(path);
        WriteAccountConfig();
        WriteProjectConfig(new { folders = new[] { new { id = HarborBackup.Windows.Integration.DeviceIdentity.Digest("demo"), source = Source, destination = "Projects/Demo" } } });
        WriteSystemConfig(new { sync = new { enabled = true }, schedule = new { intervalMinutes = 30 } });
    }

    public void WriteAccountConfig(string? config = null)
    {
        config ??= Config;
        Directory.CreateDirectory(config);
        File.WriteAllText(Path.Combine(config, "onedrive-account.json"), JsonSerializer.Serialize(
            new HarborBackup.Windows.Persistence.Configuration.CloudAccountSettings(Enabled: true,
                ClientId: Guid.NewGuid().ToString(), AccountId: "synthetic-account", DriveId: "test-drive",
                RootId: "test-root", RepositoryId: Guid.NewGuid().ToString("N")), AtomicJson.Options));
    }
    public void WriteProjectConfig(object value) => File.WriteAllText(Path.Combine(Config, "projects.json"), SerializeConfig(value));
    public void WriteSystemConfig(object value) => File.WriteAllText(Path.Combine(Config, "backup-settings.json"), SerializeConfig(value));
    private static string SerializeConfig(object value)
    {
        var node = System.Text.Json.Nodes.JsonNode.Parse(JsonSerializer.Serialize(value))!.AsObject(); node["schemaVersion"] = node["folders"] is System.Text.Json.Nodes.JsonArray ? 2 : 1; if (node["folders"] is System.Text.Json.Nodes.JsonArray folders) foreach (var folder in folders) if (folder is not null && folder["id"] is null) folder["id"] = HarborBackup.Windows.Integration.DeviceIdentity.Digest(Guid.NewGuid().ToString("N"));
        return node.ToJsonString();
    }
    public ConfigurationSnapshot Load() => ConfigurationReader.Read(Path.Combine(Config, "projects.json"),
        Path.Combine(Config, "backup-settings.json"), Path.Combine(Root, "desktop"), Device);
    public string Write(string root, string relative, string content)
    {
        var path = Path.Combine(root, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content);
        File.SetLastWriteTimeUtc(path, new DateTime(2026, 10, 1, 0, 0, 0, DateTimeKind.Utc));
        return path;
    }

    public void Dispose()
    {
        var resolved = Path.GetFullPath(Root);
        if (!resolved.StartsWith(Path.GetFullPath(container) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Refusing cleanup outside test workspace.");
        if (Directory.Exists(resolved)) Directory.Delete(resolved, true);
    }
}
