using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using HarborBackup.Core.Domain;
using HarborBackup.Windows.Integration;
using HarborBackup.Windows.Persistence;
using static HarborBackup.Windows.Tests.CloudTransportTests;

namespace HarborBackup.Windows.Tests;

[TestClass]
[DoNotParallelize]
public sealed class CloudMappingTests
{
    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task PendingFileDoesNotBlockPreviewOrOtherFilesInSameProject(bool switchSource)
    {
        using var f = new Fixture(); var a = f.Device("A");
        File.WriteAllText(Path.Combine(a.P.Source, "pending.txt"), "retain");
        f.Drive.FailAfterCommit = true;
        await new CloudBackupRuntime(a.C, () => f.Drive).ExecuteAsync(SyncDirection.Backup, null, false, false, null, default);
        var journal = Directory.GetFiles(f.Root, "*.json", SearchOption.AllDirectories).Single(p => Path.GetFileName(Path.GetDirectoryName(p)) == "cloud-transactions");
        var before = File.ReadAllBytes(journal);
        var next = switchSource ? Switch(a.P, a.C, Path.Combine(f.Root, "new-source")) : (a.P, a.C);
        Directory.CreateDirectory(next.Item1.Source);
        var root = (await f.Drive.FindAsync("drive-root", "Documents", default))!;
        for (var i = 0; i < 5; i++) f.Drive.Add(root.Id, $"available-{i}.txt", Encoding.UTF8.GetBytes("cloud"));
        var runtime = new CloudBackupRuntime(next.Item2, () => f.Drive);
        var preview = await runtime.PreviewAsync(SyncDirection.Pull, "Documents", false, default);
        Assert.HasCount(6, preview.Projects.Single().Plans);
        Assert.AreEqual("PendingFileOperation", preview.Projects.Single().Plans.Single(p => p.Path == "pending.txt").Decision.Reason);
        Assert.IsNotNull(preview.PullSelection); Assert.HasCount(5, preview.PullSelection.Files);
        CollectionAssert.AreEqual(before, File.ReadAllBytes(journal));
        var result = await runtime.ExecuteAsync(SyncDirection.Pull, "Documents", false, false, null, default, preview.PullSelection);
        Assert.AreEqual(2, result.ExitCode); // retained operation is still reported, other files completed
        Assert.HasCount(5, result.Projects.Single().Files.Where(p => p.Changed));
        for (var i = 0; i < 5; i++) Assert.AreEqual("cloud", File.ReadAllText(Path.Combine(next.Item1.Source, $"available-{i}.txt")));
        CollectionAssert.AreEqual(before, File.ReadAllBytes(journal));
        Assert.AreEqual("retain", File.ReadAllText(Path.Combine(a.P.Source, "pending.txt")));
    }

    [TestMethod]
    public async Task DamagedRecoveryEvidenceAllowsObservationButCannotAuthorizeWrites()
    {
        using var f = new Fixture(); var a = f.Device("A");
        File.WriteAllText(Path.Combine(a.P.Source, "a.txt"), "local");
        var directory = Path.Combine(CloudMapping.StateRoot(a.P, a.C), "cloud-transactions");
        Directory.CreateDirectory(directory); var path = Path.Combine(directory, "damaged.json"); File.WriteAllText(path, "broken");
        var preview = await new CloudBackupRuntime(a.C, () => f.Drive).PreviewAsync(SyncDirection.Backup, "Documents", false, default, prepareUpload: true);
        Assert.HasCount(1, preview.Projects.Single().Plans);
        Assert.AreEqual("PendingOperationsUnverified", preview.Projects.Single().Issues.Single().Code);
        Assert.IsNotNull(preview.UploadSelection); Assert.IsEmpty(preview.UploadSelection.Files);
        Assert.AreEqual("broken", File.ReadAllText(path));
    }

    private static (ProjectDefinition P, ConfigurationSnapshot C) Switch(ProjectDefinition project, ConfigurationSnapshot config, string source)
    {
        var next = project with { Source = source, MappingVersion = Guid.NewGuid().ToString("N") };
        return (next, config with { Projects = [next], RetiredMappings = [CloudMapping.Capture(project, config)] });
    }

    [TestMethod]
    public async Task MissingNewRootIsNotCreatedUntilConfirmedPullAndAutomaticBackupWaits()
    {
        using var f = new Fixture(); var a = f.Device("A");
        File.WriteAllText(Path.Combine(a.P.Source, "a.txt"), "original"); await Apply(a.S);
        var next = Switch(a.P, a.C, Path.Combine(f.Root, "new-source"));
        var runtime = new CloudBackupRuntime(next.C, () => f.Drive);
        var writes = f.Drive.UploadIntents.Count;
        var automatic = await runtime.ExecuteAsync(SyncDirection.Backup, null, false, false, null, default, automatic: true);
        Assert.AreEqual("MappingAwaitingOperation", automatic.Projects.Single().Issues.Single().Code);
        Assert.HasCount(writes, f.Drive.UploadIntents);
        Assert.IsFalse(Directory.Exists(next.P.Source));
        var failedUpload = await runtime.ExecuteAsync(SyncDirection.Backup, null, false, false, null, default);
        Assert.AreNotEqual(0, failedUpload.ExitCode);
        var preview = await runtime.PreviewAsync(SyncDirection.Pull, "Documents", false, default);
        Assert.AreEqual(0, preview.ExitCode);
        Assert.IsFalse(Directory.Exists(next.P.Source));
        Assert.IsTrue(CloudMapping.NeedsFirstOperation(next.P, next.C));
        var result = await runtime.ExecuteAsync(SyncDirection.Pull, "Documents", false, false, null, default, preview.PullSelection);
        Assert.AreEqual(0, result.ExitCode);
        Assert.AreEqual("original", File.ReadAllText(Path.Combine(next.P.Source, "a.txt")));
        Assert.AreEqual("original", File.ReadAllText(Path.Combine(a.P.Source, "a.txt")));
        Assert.IsFalse(CloudMapping.NeedsFirstOperation(next.P, next.C));
        Assert.AreEqual(0, (await runtime.ExecuteAsync(SyncDirection.Backup, null, false, false, null, default, automatic: true)).ExitCode);
    }

    [TestMethod]
    [DataRow(SyncDirection.Backup)]
    [DataRow(SyncDirection.Pull)]
    public async Task NewMappingUsesExplicitDirectionAndProtectsReplacedContent(SyncDirection direction)
    {
        using var f = new Fixture(); var a = f.Device("A");
        File.WriteAllText(Path.Combine(a.P.Source, "a.txt"), "cloud"); await Apply(a.S);
        var next = Switch(a.P, a.C, Path.Combine(f.Root, "new-source"));
        Directory.CreateDirectory(next.P.Source);
        File.WriteAllText(Path.Combine(next.P.Source, "a.txt"), "local");
        var runtime = new CloudBackupRuntime(next.C, () => f.Drive);
        var read = await new CloudBackupSession(next.P, next.C, f.Account, f.Drive).ReadAsync(false, default);
        Assert.IsNull(read.Snapshot.Files.Single().Baseline);
        var preview = await runtime.PreviewAsync(direction, "Documents", false, default, prepareUpload: direction == SyncDirection.Backup);
        var result = await runtime.ExecuteAsync(direction, "Documents", false, false, null, default,
            confirmedPull: preview.PullSelection, confirmedUpload: preview.UploadSelection);
        Assert.AreEqual(0, result.ExitCode, JsonSerializer.Serialize(result));
        var parent = (await f.Drive.FindAsync("drive-root", "Documents", default))!;
        var live = (await f.Drive.FindAsync(parent.Id, "a.txt", default))!;
        var expected = direction == SyncDirection.Backup ? "local" : "cloud";
        Assert.AreEqual(expected, Encoding.UTF8.GetString(f.Drive.Items[live.Id].Bytes));
        Assert.AreEqual(expected, File.ReadAllText(Path.Combine(next.P.Source, "a.txt")));
        Assert.IsTrue(f.Drive.Items.Values.Any(v => v.Item.Id != live.Id && Encoding.UTF8.GetString(v.Bytes) == (direction == SyncDirection.Backup ? "cloud" : "local")));
        Assert.IsFalse(CloudMapping.NeedsFirstOperation(next.P, next.C));
        Assert.AreEqual("cloud", File.ReadAllText(Path.Combine(a.P.Source, "a.txt")));
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task RecoveryAfterSwitchUsesOriginalMappingAndDoesNotBlockOtherProjects(bool legacy)
    {
        using var f = new Fixture(); var a = f.Device("A");
        File.WriteAllText(Path.Combine(a.P.Source, "a.txt"), "old-source");
        f.Drive.FailAfterCommit = true;
        Assert.AreNotEqual(0, (await new CloudBackupRuntime(a.C, () => f.Drive).ExecuteAsync(SyncDirection.Backup, null, false, false, null, default)).ExitCode);
        var journal = Directory.GetFiles(f.Root, "*.json", SearchOption.AllDirectories).Single(p => Path.GetFileName(Path.GetDirectoryName(p)) == "cloud-transactions");
        if (legacy)
        {
            var node = JsonNode.Parse(File.ReadAllText(journal))!; node["schemaVersion"] = 4; node.AsObject().Remove("mapping");
            File.WriteAllText(journal, node.ToJsonString());
        }
        var next = Switch(a.P, a.C, Path.Combine(f.Root, "new-source"));
        Directory.CreateDirectory(next.P.Source); File.WriteAllText(Path.Combine(next.P.Source, "a.txt"), "new-source");
        var other = a.P with { Source = Path.Combine(f.Root, "other"), Destination = "Other", TaskId = DeviceIdentity.Digest("other") };
        Directory.CreateDirectory(other.Source); File.WriteAllText(Path.Combine(other.Source, "b.txt"), "other");
        var config = next.C with { Projects = [next.P, other] };
        var runtime = new CloudBackupRuntime(config, () => f.Drive);
        var blocked = await runtime.ExecuteAsync(SyncDirection.Backup, null, false, false, null, default);
        Assert.AreEqual("PreviousMappingPending", blocked.Projects[0].Issues.Single().Code);
        Assert.AreEqual(0, blocked.Projects[1].ExitCode);
        Assert.IsTrue(File.Exists(journal));
        var recovered = await runtime.ExecuteAsync(SyncDirection.Backup, "Documents", false, true, null, default);
        Assert.AreEqual(0, recovered.ExitCode, JsonSerializer.Serialize(recovered));
        Assert.IsFalse(File.Exists(journal));
        Assert.AreEqual("new-source", File.ReadAllText(Path.Combine(next.P.Source, "a.txt")));
        Assert.IsTrue(CloudMapping.NeedsFirstOperation(next.P, config));
        Assert.IsNull((await new CloudBackupSession(next.P, config, f.Account, f.Drive).ReadAsync(false, default)).Snapshot.Files.Single().Baseline);
    }

    [TestMethod]
    public async Task RecoveryRejectsAlteredSourceSnapshot()
    {
        using var f = new Fixture(); var a = f.Device("A");
        File.WriteAllText(Path.Combine(a.P.Source, "a.txt"), "payload"); f.Drive.FailAfterCommit = true;
        var runtime = new CloudBackupRuntime(a.C, () => f.Drive);
        await runtime.ExecuteAsync(SyncDirection.Backup, null, false, false, null, default);
        var path = Directory.GetFiles(f.Root, "*.json", SearchOption.AllDirectories).Single(p => Path.GetFileName(Path.GetDirectoryName(p)) == "cloud-transactions");
        var node = JsonNode.Parse(File.ReadAllText(path))!;
        node["mapping"]!["project"]!["source"] = Path.Combine(f.Root, "wrong-source");
        File.WriteAllText(path, node.ToJsonString());
        var before = File.ReadAllBytes(path);
        var failed = await runtime.ExecuteAsync(SyncDirection.Backup, null, false, true, null, default);
        Assert.AreEqual(1, failed.ExitCode);
        StringAssert.Contains(failed.Projects.Single().Issues.Single().Message, "Recovery mapping identity mismatch");
        CollectionAssert.AreEqual(before, File.ReadAllBytes(path));
        Assert.IsTrue(File.Exists(path)); Assert.IsFalse(Directory.Exists(Path.Combine(f.Root, "wrong-source")));
    }

    [TestMethod]
    public async Task ConfirmedUploadRehashesSourceAndRejectsSameSizeTimestampChange()
    {
        using var f = new Fixture(); var a = f.Device("A");
        File.WriteAllText(Path.Combine(a.P.Source, "a.txt"), "cloud"); await Apply(a.S);
        var next = Switch(a.P, a.C, Path.Combine(f.Root, "new-source")); Directory.CreateDirectory(next.P.Source);
        var path = Path.Combine(next.P.Source, "a.txt"); File.WriteAllText(path, "local");
        var runtime = new CloudBackupRuntime(next.C, () => f.Drive);
        var preview = await runtime.PreviewAsync(SyncDirection.Backup, "Documents", false, default, prepareUpload: true);
        var stamp = File.GetLastWriteTimeUtc(path); File.WriteAllText(path, "other"); File.SetLastWriteTimeUtc(path, stamp);
        var result = await runtime.ExecuteAsync(SyncDirection.Backup, "Documents", false, false, null, default, confirmedUpload: preview.UploadSelection);
        Assert.AreNotEqual(0, result.ExitCode);
        Assert.IsTrue(CloudMapping.NeedsFirstOperation(next.P, next.C));
        var root = (await f.Drive.FindAsync("drive-root", "Documents", default))!;
        var live = (await f.Drive.FindAsync(root.Id, "a.txt", default))!;
        Assert.AreEqual("cloud", Encoding.UTF8.GetString(f.Drive.Items[live.Id].Bytes));
        Assert.IsFalse(Directory.Exists(Path.Combine(CloudMapping.StateRoot(next.P, next.C), "baselines")));
    }

    [TestMethod]
    public async Task PartialConfirmationLeavesAutomaticBackupWaiting()
    {
        using var f = new Fixture(); var a = f.Device("A");
        File.WriteAllText(Path.Combine(a.P.Source, "a.txt"), "a"); File.WriteAllText(Path.Combine(a.P.Source, "b.txt"), "b"); await Apply(a.S);
        var next = Switch(a.P, a.C, Path.Combine(f.Root, "new-source"));
        var runtime = new CloudBackupRuntime(next.C, () => f.Drive);
        var preview = await runtime.PreviewAsync(SyncDirection.Pull, "Documents", false, default);
        var selection = preview.PullSelection! with { Files = preview.PullSelection!.Files.Take(1).ToArray() };
        Assert.AreEqual(0, (await runtime.ExecuteAsync(SyncDirection.Pull, "Documents", false, false, null, default, selection)).ExitCode);
        Assert.IsTrue(CloudMapping.NeedsFirstOperation(next.P, next.C));
        var fresh = await runtime.PreviewAsync(SyncDirection.Pull, "Documents", false, default);
        Assert.AreEqual(0, (await runtime.ExecuteAsync(SyncDirection.Pull, "Documents", false, false, null, default, fresh.PullSelection)).ExitCode);
        Assert.IsFalse(CloudMapping.NeedsFirstOperation(next.P, next.C));
    }

    [TestMethod]
    public async Task ExplicitUploadProtectsUnknownCloudContentWithoutInventingHash()
    {
        using var f = new Fixture(); var a = f.Device("A");
        var root = await f.Drive.FolderAsync("drive-root", "Documents", default);
        f.Drive.Add(root.Id, "a.txt", Encoding.UTF8.GetBytes("unknown-cloud"));
        File.WriteAllText(Path.Combine(a.P.Source, "a.txt"), "local");
        var runtime = new CloudBackupRuntime(a.C, () => f.Drive);
        var preview = await runtime.PreviewAsync(SyncDirection.Backup, "Documents", false, default, prepareUpload: true);
        var result = await runtime.ExecuteAsync(SyncDirection.Backup, "Documents", false, false, null, default, confirmedUpload: preview.UploadSelection);
        Assert.AreEqual(0, result.ExitCode, JsonSerializer.Serialize(result));
        Assert.AreEqual(0, f.Drive.BusinessDownloads);
        Assert.IsTrue(f.Drive.Items.Values.Any(v => Encoding.UTF8.GetString(v.Bytes) == "unknown-cloud"));
        var protection = f.Drive.Items.Values.Where(v => v.Item.Name.EndsWith(".json") && v.Bytes.Length > 0)
            .Select(v => JsonNode.Parse(v.Bytes)).First(n => n?["kind"] is not null);
        Assert.IsFalse(protection!["revision"]!["contentHashKnown"]!.GetValue<bool>());
        Assert.IsTrue((await a.S.ReadAsync(false, default)).Heads["a.txt"].Single().ContentHashKnown);
    }

    [TestMethod]
    public async Task FailedExplicitUploadRestoresUnknownCloudContentWithoutPublishingFalseMetadata()
    {
        using var f = new Fixture(); var a = f.Device("A");
        var root = await f.Drive.FolderAsync("drive-root", "Documents", default);
        f.Drive.Add(root.Id, "a.txt", Encoding.UTF8.GetBytes("unknown-cloud"));
        File.WriteAllText(Path.Combine(a.P.Source, "a.txt"), "local");
        var runtime = new CloudBackupRuntime(a.C, () => f.Drive);
        var preview = await runtime.PreviewAsync(SyncDirection.Backup, "Documents", false, default, prepareUpload: true);
        f.Drive.BeforeUpload = (name, _) => name == "a.txt" ? throw new IOException("Injected body failure") : Task.CompletedTask;
        var result = await runtime.ExecuteAsync(SyncDirection.Backup, "Documents", false, false, null, default, confirmedUpload: preview.UploadSelection);
        Assert.AreNotEqual(0, result.ExitCode);
        var live = (await f.Drive.FindAsync(root.Id, "a.txt", default))!;
        Assert.AreEqual("unknown-cloud", Encoding.UTF8.GetString(f.Drive.Items[live.Id].Bytes));
        Assert.IsFalse((await a.S.ReadAsync(true, default)).Heads["a.txt"].Single().ContentHashKnown);
        Assert.AreEqual("local", File.ReadAllText(Path.Combine(a.P.Source, "a.txt")));
        Assert.IsFalse(a.S.Pending);
    }
}
