using System.Text.Json.Nodes;
using HarborBackup.Windows.Integration;

namespace HarborBackup.Windows.Tests;

[TestClass]
[DoNotParallelize]
public sealed class InstallationDataTests
{
    private sealed class Lease : IDisposable
    {
        public string Name { get; } = "Local\\HarborInstallDataTest." + Guid.NewGuid().ToString("N");
        public string Token { get; }
        private readonly Mutex maintenance;
        private readonly Mutex owner;
        public Lease()
        {
            Token = Name + ".Initialize." + DeviceIdentity.Digest(Guid.NewGuid().ToString("N"));
            maintenance = new(false, Name + ".Maintenance"); owner = new(false, Token);
        }
        public InstallationDataResult Check(RuntimePaths paths) => FreshDataInitializer.PrepareInstallation(paths, Token, Name);
        public void Dispose() { owner.Dispose(); maintenance.Dispose(); }
    }

    [TestMethod]
    public void FreshInstallationCreatesReadyEmptyConfigurationAndRepeatDoesNothing()
    {
        using var ws = new TestWorkspace(); using var lease = new Lease();
        var root = Path.Combine(ws.Root, "new-install", "data");
        var paths = new RuntimePaths(Path.Combine(root, "config", "projects.json"), Path.Combine(root, "config", "backup-settings.json"));
        var first = lease.Check(paths);
        Assert.AreEqual("Created", first.Action); Assert.IsNull(first.RetainedData);
        Assert.HasCount(2, Directory.GetFiles(root, "*", SearchOption.AllDirectories));
        var before = Snapshot(root);
        Assert.AreEqual("Preserved", lease.Check(paths).Action);
        AssertSnapshot(root, before);
    }

    [TestMethod]
    [DataRow("old")]
    [DataRow("invalid-json")]
    [DataRow("invalid-id")]
    [DataRow("missing-system")]
    [DataRow("old-account")]
    [DataRow("old-status")]
    public void UnsupportedConfigurationIsAutomaticallyRebuiltOnceWithAllEvidenceRetained(string kind)
    {
        using var ws = new TestWorkspace(); using var lease = new Lease(); var paths = Paths(ws);
        if (kind == "old") File.WriteAllText(paths.ProjectConfig, "{\"schemaVersion\":1,\"folders\":[]}");
        if (kind == "invalid-json") File.WriteAllText(paths.ProjectConfig, "unreadable old configuration");
        if (kind == "invalid-id")
        {
            var document = JsonNode.Parse(File.ReadAllText(paths.ProjectConfig))!;
            document["folders"]![0]!["id"] = "old-name-id";
            File.WriteAllText(paths.ProjectConfig, document.ToJsonString());
        }
        if (kind == "missing-system") File.Delete(paths.SystemConfig);
        if (kind == "old-account") ws.Write(paths.Data.Config, "onedrive-account.json", "{\"schemaVersion\":1}");
        if (kind == "old-status") ws.Write(paths.Data.State, "devices/" + DeviceIdentity.CurrentDevice() + "/last-run.json", "{\"schemaVersion\":2}");
        ws.Write(ws.State, "cloud-transactions/keep.json", "recovery");
        ws.Write(paths.Data.Archives, "keep.bin", "archive");
        ws.Write(paths.Data.Conflicts, "keep.bin", "conflict");
        var source = ws.Write(ws.Source, "business.txt", "business");
        var before = Snapshot(paths.Data.Root);
        var result = lease.Check(paths);
        Assert.AreEqual("Rebuilt", result.Action); Assert.IsNotNull(result.RetainedData);
        AssertSnapshot(result.RetainedData, before);
        Assert.AreEqual("business", File.ReadAllText(source));
        Assert.HasCount(2, Directory.GetFiles(paths.Data.Root, "*", SearchOption.AllDirectories));
        Assert.AreEqual("Preserved", lease.Check(paths).Action);
        Assert.HasCount(1, Directory.GetDirectories(ws.Root, "data-retained-*"));
    }

    [TestMethod]
    public void CurrentConfigurationKeepsIdsCredentialsAndStateWithoutAccessingSource()
    {
        using var ws = new TestWorkspace(); using var lease = new Lease(); var paths = Paths(ws);
        var document = JsonNode.Parse(File.ReadAllText(paths.ProjectConfig))!;
        document["folders"]![0]!["source"] = @"\\unavailable.invalid\share\missing-source";
        File.WriteAllText(paths.ProjectConfig, document.ToJsonString());
        ws.Write(paths.Data.State, "keep.json", "state"); ws.Write(paths.Data.Config, "auth-cache.bin", "protected credentials");
        var before = Snapshot(paths.Data.Root);
        Assert.AreEqual("Preserved", lease.Check(paths).Action);
        AssertSnapshot(paths.Data.Root, before);
    }

    [TestMethod]
    public void LockedConfigurationFailsWithoutMovingOrRebuildingData()
    {
        using var ws = new TestWorkspace(); using var lease = new Lease(); var paths = Paths(ws);
        var before = Snapshot(paths.Data.Root);
        using (var locked = new FileStream(paths.ProjectConfig, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
            Assert.Throws<IOException>(() => lease.Check(paths));
        AssertSnapshot(paths.Data.Root, before);
        Assert.IsEmpty(Directory.GetDirectories(ws.Root, "data-retained-*"));
    }

    [TestMethod]
    public void MissingLeaseCannotRunDuringNormalApplicationLifetime()
    {
        using var ws = new TestWorkspace(); var paths = Paths(ws);
        var name = "Local\\HarborInstallDataTest." + Guid.NewGuid().ToString("N");
        var token = name + ".Initialize." + DeviceIdentity.Digest("missing");
        var before = Snapshot(paths.Data.Root);
        Assert.Throws<IOException>(() => FreshDataInitializer.PrepareInstallation(paths, token, name));
        AssertSnapshot(paths.Data.Root, before);
    }

    private static RuntimePaths Paths(TestWorkspace ws) => new(Path.Combine(ws.Config, "projects.json"), Path.Combine(ws.Config, "backup-settings.json"));
    private static Dictionary<string, byte[]> Snapshot(string root) => Directory.GetFiles(root, "*", SearchOption.AllDirectories)
        .ToDictionary(p => Path.GetRelativePath(root, p), File.ReadAllBytes);
    private static void AssertSnapshot(string root, Dictionary<string, byte[]> expected)
    {
        Assert.HasCount(expected.Count, Directory.GetFiles(root, "*", SearchOption.AllDirectories));
        foreach (var (relative, bytes) in expected) CollectionAssert.AreEqual(bytes, File.ReadAllBytes(Path.Combine(root, relative)));
    }
}
