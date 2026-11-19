using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using HarborBackup.Core.Domain;
using HarborBackup.OneDrive.Transport;
using HarborBackup.Windows.Integration;
using HarborBackup.Windows.Persistence;
using static HarborBackup.Windows.Tests.CloudTransportTests;

namespace HarborBackup.Windows.Tests;

[TestClass]
[DoNotParallelize]
public sealed class PullRetryTests
{
    [TestMethod]
    public async Task NetworkLossKeepsPullEvidenceAndOriginalCloudContentUntilExplicitRetry()
    {
        using var f = new Fixture(); var a = f.Device("A"); var b = f.Device("B");
        File.WriteAllText(Path.Combine(a.P.Source, "a.txt"), "cloud content"); await Apply(a.S);
        var runtime = new CloudBackupRuntime(b.C, () => f.Drive);
        var preview = await runtime.PreviewAsync(SyncDirection.Pull, "Documents", false, default);
        f.Drive.BeforeDownload = (name, _) => name == "a.txt" ? Task.FromException(new CloudOfflineException()) : Task.CompletedTask;
        var failed = await runtime.ExecuteAsync(SyncDirection.Pull, "Documents", false, false, null, default, preview.PullSelection);
        Assert.AreEqual(1, failed.ExitCode); Assert.IsFalse(File.Exists(Path.Combine(b.P.Source, "a.txt")));
        Assert.AreEqual(FileFailureKind.Network, failed.Projects.Single().Files.Single().FailureKind);
        Assert.AreEqual(CloudNetworkAvailability.Offline, failed.Projects.Single().Files.Single().Error);
        Assert.IsTrue(failed.Projects.Single().Issues.Any(i => i.Code == "PendingOperationsRetained"));
        Assert.IsFalse(CloudRunStatus.IsOfflineInterruption(failed.Projects.Single()));
        var pending = CloudMapping.ScanPending(b.C).Files;
        Assert.HasCount(1, pending);
        Assert.AreEqual("cloud content", File.ReadAllText(Path.Combine(a.P.Source, "a.txt")));
        Assert.IsFalse(Directory.Exists(Path.Combine(b.C.Preview.StateDirectory, "devices", b.C.Preview.DeviceId, "cloud", CloudMapping.Bind(b.P, b.C), "baselines")));
        f.Drive.BeforeDownload = null;
        var retry = await runtime.PreviewAsync(SyncDirection.Pull, "Documents", false, default);
        Assert.AreEqual(0, (await runtime.ExecuteAsync(SyncDirection.Pull, "Documents", false, false, null, default, retry.PullSelection)).ExitCode);
        Assert.AreEqual("cloud content", File.ReadAllText(Path.Combine(b.P.Source, "a.txt")));
    }

    [TestMethod]
    [DataRow(2, false)]
    [DataRow(4, false)]
    [DataRow(5, false)]
    [DataRow(2, true)]
    [DataRow(4, true)]
    [DataRow(5, true)]
    public async Task EmptyLocalPullRetriesInPlaceAndPreservesOriginalEvidence(int schema, bool switchSource)
    {
        using var f = new Fixture(); var setup = await Prepared(f, schema);
        var config = setup.C;
        var project = setup.P;
        var temporary = Path.Combine(project.Source, ".harborbackup-partial.tmp");
        File.WriteAllText(temporary, "partial");
        if (switchSource)
        {
            project = project with { Source = Path.Combine(f.Root, "new-source"), MappingVersion = Guid.NewGuid().ToString("N") };
            config = config with { Projects = [project], RetiredMappings = [CloudMapping.Capture(setup.P, setup.C)] };
        }
        var before = File.ReadAllBytes(setup.Journal);
        var runtime = new CloudBackupRuntime(config, () => f.Drive);
        var preview = await runtime.PreviewAsync(SyncDirection.Pull, "Documents", false, default);
        Assert.AreEqual(0, preview.ExitCode);
        Assert.AreEqual("RetryInterruptedPull", preview.Projects.Single().Plans.Single().Decision.Reason);
        Assert.HasCount(1, preview.PullSelection!.Files);
        CollectionAssert.AreEqual(before, File.ReadAllBytes(setup.Journal));
        Assert.IsFalse(File.Exists(Path.Combine(project.Source, "sample.docx")));
        var emptySelection = preview.PullSelection with { Files = [] };
        await runtime.ExecuteAsync(SyncDirection.Pull, "Documents", false, false, null, default, emptySelection);
        CollectionAssert.AreEqual(before, File.ReadAllBytes(setup.Journal));
        var result = await runtime.ExecuteAsync(SyncDirection.Pull, "Documents", false, false, null, default, preview.PullSelection);
        Assert.AreEqual(0, result.ExitCode, JsonSerializer.Serialize(result));
        Assert.AreEqual("cloud document", File.ReadAllText(Path.Combine(project.Source, "sample.docx")));
        Assert.AreEqual("cloud document", Encoding.UTF8.GetString(f.Drive.Items[setup.Remote.Id].Bytes));
        Assert.AreEqual("partial", File.ReadAllText(temporary));
        if (switchSource) Assert.IsFalse(File.Exists(Path.Combine(setup.P.Source, "sample.docx")));
        Assert.IsFalse(File.Exists(setup.Journal));
        var retained = Path.Combine(CloudMapping.StateRoot(setup.P, setup.C), "retained-transactions", Path.GetFileName(setup.Journal));
        CollectionAssert.AreEqual(before, File.ReadAllBytes(retained));
        Assert.IsFalse(CloudMapping.NeedsFirstOperation(project, config));
    }

    [TestMethod]
    [DataRow("journal")]
    [DataRow("local-file")]
    [DataRow("local-directory")]
    [DataRow("cloud")]
    public async Task PreviewChangesNeverAuthorizeDiscardOrOverwrite(string change)
    {
        using var f = new Fixture(); var setup = await Prepared(f, 2);
        var runtime = new CloudBackupRuntime(setup.C, () => f.Drive);
        var preview = await runtime.PreviewAsync(SyncDirection.Pull, "Documents", false, default);
        var local = Path.Combine(setup.P.Source, "sample.docx");
        if (change == "journal")
        {
            var node = JsonNode.Parse(File.ReadAllText(setup.Journal))!;
            node["createdUtc"] = DateTimeOffset.UtcNow.AddHours(1); File.WriteAllText(setup.Journal, node.ToJsonString());
        }
        else if (change == "local-file") File.WriteAllText(local, "new local edit");
        else if (change == "local-directory") Directory.CreateDirectory(local);
        else
        {
            var old = f.Drive.Items[setup.Remote.Id];
            f.Drive.Items[setup.Remote.Id] = (old.Item with { ETag = "changed" }, old.Parent, old.Bytes);
        }
        var before = File.ReadAllBytes(setup.Journal);
        var result = await runtime.ExecuteAsync(SyncDirection.Pull, "Documents", false, false, null, default, preview.PullSelection);
        Assert.AreNotEqual(0, result.ExitCode);
        Assert.IsFalse(result.Projects.Single().Files.Any(file => file.Changed));
        CollectionAssert.AreEqual(before, File.ReadAllBytes(setup.Journal));
        if (change == "local-file") Assert.AreEqual("new local edit", File.ReadAllText(local));
        else if (change == "local-directory") Assert.IsTrue(Directory.Exists(local));
        else Assert.IsFalse(File.Exists(local));
    }

    [TestMethod]
    [DataRow("phase")]
    [DataRow("incoming")]
    [DataRow("local")]
    [DataRow("protection")]
    [DataRow("direction")]
    [DataRow("schema")]
    [DataRow("staged")]
    [DataRow("parent")]
    public async Task OnlyUnwrittenPullIntentsCanBeRestarted(string change)
    {
        using var f = new Fixture(); var setup = await Prepared(f, 2);
        var node = JsonNode.Parse(File.ReadAllText(setup.Journal))!;
        switch (change)
        {
            case "phase": node["phase"] = "Published"; break;
            case "incoming": node["incoming"] = node["remote"]!["version"]!.DeepClone(); break;
            case "local": node["local"] = node["remote"]!["version"]!.DeepClone(); break;
            case "protection": node["protection"] = "Archive"; break;
            case "direction": node["direction"] = "Backup"; break;
            case "schema": node["schemaVersion"] = 3; break;
            case "staged": node["staged"] = node["expected"]!.DeepClone(); break;
            case "parent": node["parent"] = "another-parent"; break;
        }
        File.WriteAllText(setup.Journal, node.ToJsonString()); var before = File.ReadAllBytes(setup.Journal);
        var preview = await new CloudBackupRuntime(setup.C, () => f.Drive).PreviewAsync(SyncDirection.Pull, "Documents", false, default);
        Assert.AreEqual("PendingFileOperation", preview.Projects.Single().Plans.Single().Decision.Reason);
        Assert.IsEmpty(preview.PullSelection!.Files);
        CollectionAssert.AreEqual(before, File.ReadAllBytes(setup.Journal));
    }

    [TestMethod]
    public async Task FailedRetryCanBeRetriedAgainWithoutSeparateRecovery()
    {
        using var f = new Fixture(); var setup = await Prepared(f, 2);
        var runtime = new CloudBackupRuntime(setup.C, () => f.Drive);
        var preview = await runtime.PreviewAsync(SyncDirection.Pull, "Documents", false, default);
        f.Drive.BeforeDownload = (name, _) => name == "sample.docx" ? throw new IOException("download interrupted") : Task.CompletedTask;
        var failed = await runtime.ExecuteAsync(SyncDirection.Pull, "Documents", false, false, null, default, preview.PullSelection);
        Assert.AreNotEqual(0, failed.ExitCode);
        Assert.IsFalse(File.Exists(Path.Combine(setup.P.Source, "sample.docx")));
        f.Drive.BeforeDownload = null;
        var next = await runtime.PreviewAsync(SyncDirection.Pull, "Documents", false, default);
        Assert.AreEqual("RetryInterruptedPull", next.Projects.Single().Plans.Single().Decision.Reason);
        Assert.AreEqual(0, (await runtime.ExecuteAsync(SyncDirection.Pull, "Documents", false, false, null, default, next.PullSelection)).ExitCode);
        Assert.AreEqual("cloud document", File.ReadAllText(Path.Combine(setup.P.Source, "sample.docx")));
    }

    [TestMethod]
    public async Task NewCloudVersionCanBeChosenWithoutReplayingOldIntent()
    {
        using var f = new Fixture(); var setup = await Prepared(f, 2);
        await f.Drive.DeleteAsync(setup.Remote, default);
        f.Drive.Add(setup.Remote.ParentId!, "sample.docx", Encoding.UTF8.GetBytes("new cloud version"));
        var runtime = new CloudBackupRuntime(setup.C, () => f.Drive);
        var preview = await runtime.PreviewAsync(SyncDirection.Pull, "Documents", false, default);
        Assert.HasCount(1, preview.PullSelection!.Files);
        var result = await runtime.ExecuteAsync(SyncDirection.Pull, "Documents", false, false, null, default, preview.PullSelection);
        Assert.AreEqual(0, result.ExitCode, JsonSerializer.Serialize(result));
        Assert.AreEqual("new cloud version", File.ReadAllText(Path.Combine(setup.P.Source, "sample.docx")));
    }

    [TestMethod]
    public async Task RetryAlsoAllowsCreatingMissingParentDirectories()
    {
        using var f = new Fixture(); var setup = await Prepared(f, 5, "nested/child/sample.docx");
        var runtime = new CloudBackupRuntime(setup.C, () => f.Drive);
        var preview = await runtime.PreviewAsync(SyncDirection.Pull, "Documents", false, default);
        Assert.AreEqual(0, preview.ExitCode, JsonSerializer.Serialize(preview));
        Assert.HasCount(3, preview.PullSelection!.Files);
        Assert.IsFalse(Directory.Exists(Path.Combine(setup.P.Source, "nested")));
        var result = await runtime.ExecuteAsync(SyncDirection.Pull, "Documents", false, false, null, default, preview.PullSelection);
        Assert.AreEqual(0, result.ExitCode, JsonSerializer.Serialize(result));
        Assert.AreEqual("cloud document", File.ReadAllText(Path.Combine(setup.P.Source, "nested/child/sample.docx")));
    }

    private static async Task<(ProjectDefinition P, ConfigurationSnapshot C, string Journal, RemoteItem Remote)> Prepared(Fixture f, int schema, string relative = "sample.docx")
    {
        var a = f.Device("A"); var b = f.Device("B");
        var source = Path.Combine(a.P.Source, relative); Directory.CreateDirectory(Path.GetDirectoryName(source)!);
        File.WriteAllText(source, "cloud document"); await Apply(a.S);
        var read = await a.S.ReadAsync(false, default); var remote = read.Heads[relative].Single();
        var expected = await f.Drive.GetAsync(remote.ItemId, default);
        var id = Guid.NewGuid().ToString("N");
        var journal = new CloudJournal(schema, id, CloudMapping.Bind(b.P, b.C), relative, SyncDirection.Pull,
            null, remote, expected, true, ProtectionKind.None, expected.ParentId!, CreatedUtc: DateTimeOffset.UtcNow,
            Mapping: schema == 5 ? CloudMapping.Capture(b.P, b.C) : null);
        var path = Path.Combine(CloudMapping.StateRoot(b.P, b.C), "cloud-transactions", id + ".json");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, JsonSerializer.Serialize(journal, AtomicJson.Options));
        return (b.P, b.C, path, expected);
    }
}
