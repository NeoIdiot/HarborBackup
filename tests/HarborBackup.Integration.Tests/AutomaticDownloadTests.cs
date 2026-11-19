using System.Text;
using System.Text.Json;
using HarborBackup.Core.Domain;
using HarborBackup.Windows.Integration;
using HarborBackup.Windows.Persistence;
using HarborBackup.Windows.Persistence.Configuration;
using static HarborBackup.Windows.Tests.CloudTransportTests;

namespace HarborBackup.Windows.Tests;

[TestClass]
[DoNotParallelize]
public sealed class AutomaticDownloadTests
{
    private static ConfigurationSnapshot Download(ConfigurationSnapshot config, string id) => config with
    {
        AutomaticBackupModes = new Dictionary<string, AutomaticBackupMode> { [id] = AutomaticBackupMode.DownloadOnly }
    };

    private static Task<BackupReport> Run(Fixture f, ConfigurationSnapshot config, CancellationToken token = default) =>
        new CloudBackupRuntime(config, () => f.Drive, 1).ExecuteAsync(SyncDirection.Backup, null, false, false, null, token, automatic: true);

    private static void Write(ProjectDefinition project, string name, string content)
    {
        var path = Path.Combine(project.Source, name);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content);
        File.SetLastWriteTimeUtc(path, DateTime.UtcNow.AddSeconds(-1));
    }

    [TestMethod]
    public async Task DownloadUpdatesAndRestoresLocalContentsWithoutUploadingOrDeletingBusinessContents()
    {
        using var f = new Fixture(); var a = f.Device("A"); var b = f.Device("B");
        foreach (var name in new[] { "remote.txt", "local-edit.txt", "missing-local.txt", "missing-cloud.txt" }) Write(a.P, name, "base");
        await Apply(a.S); await Apply(b.S, SyncDirection.Pull);
        Write(b.P, "remote.txt", "cloud update"); Write(b.P, "new-cloud.txt", "cloud new");
        Directory.CreateDirectory(Path.Combine(b.P.Source, "cloud-empty"));
        await Apply(b.S);
        var root = (await f.Drive.FindAsync("drive-root", "Documents", default))!;
        await f.Drive.DeleteAsync((await f.Drive.FindAsync(root.Id, "missing-cloud.txt", default))!, default);
        File.Delete(Path.Combine(a.P.Source, "missing-local.txt"));
        Write(a.P, "local-edit.txt", "local update"); Write(a.P, "local-only.txt", "local new");
        Directory.CreateDirectory(Path.Combine(a.P.Source, "local-empty"));
        var cloudBefore = (await f.Drive.ChildrenAsync(root.Id, default)).ToDictionary(i => i.Id, i => f.Drive.Items[i.Id]);
        var uploadsBefore = f.Drive.UploadIntents.Count;
        var report = await Run(f, Download(a.C, a.P.ProjectId));
        Assert.AreEqual("AutomaticDownload", report.Mode);
        Assert.AreEqual(2, report.ExitCode, JsonSerializer.Serialize(report)); // The local edit remains pending upload.
        Assert.IsTrue(report.Projects.Single().Files.All(p => p.Direction == SyncDirection.Pull));
        Assert.AreEqual("cloud update", File.ReadAllText(Path.Combine(a.P.Source, "remote.txt")));
        Assert.AreEqual("cloud new", File.ReadAllText(Path.Combine(a.P.Source, "new-cloud.txt")));
        Assert.AreEqual("base", File.ReadAllText(Path.Combine(a.P.Source, "missing-local.txt")));
        Assert.AreEqual("base", File.ReadAllText(Path.Combine(a.P.Source, "missing-cloud.txt")));
        Assert.AreEqual("local update", File.ReadAllText(Path.Combine(a.P.Source, "local-edit.txt")));
        Assert.AreEqual("local new", File.ReadAllText(Path.Combine(a.P.Source, "local-only.txt")));
        Assert.IsTrue(Directory.Exists(Path.Combine(a.P.Source, "cloud-empty")));
        Assert.IsTrue(Directory.Exists(Path.Combine(a.P.Source, "local-empty")));
        Assert.AreEqual(FileAction.NeedsPush, report.Projects.Single().Files.Single(p => p.Path == "local-edit.txt").Action);
        CollectionAssert.AreEquivalent(cloudBefore.Keys.ToArray(), (await f.Drive.ChildrenAsync(root.Id, default)).Select(i => i.Id).ToArray());
        foreach (var entry in cloudBefore)
        {
            Assert.AreEqual(entry.Value.Item, f.Drive.Items[entry.Key].Item);
            CollectionAssert.AreEqual(entry.Value.Bytes, f.Drive.Items[entry.Key].Bytes);
        }
        // Management uploads are required for protecting the replaced local version and publishing the observation.
        Assert.IsGreaterThan(uploadsBefore, f.Drive.UploadIntents.Count);
        var archives = (await f.Drive.FindAsync("root", "archives", default))!;
        var projectArchive = (await f.Drive.FindAsync(archives.Id, "Documents", default))!;
        var archive = (await f.Drive.FindAsync(projectArchive.Id, "remote.txt", default))!;
        Assert.AreEqual("base", Encoding.UTF8.GetString(f.Drive.Items[archive.Id].Bytes));
        Assert.IsFalse(a.S.Pending);
    }

    [TestMethod]
    public async Task ManualDirectionsRemainExplicitAndAutomaticMixedReportTracksEachProject()
    {
        using var f = new Fixture(); var a = f.Device("A"); var b = f.Device("B");
        var other = a.P with { Source = Path.Combine(f.Root, "A", "other"), Destination = "Other", TaskId = DeviceIdentity.Digest("other-project") };
        var config = Download(a.C with { Projects = [a.P, other] }, a.P.ProjectId);
        Write(a.P, "manual.txt", "explicit upload"); Write(other, "initial.txt", "base");
        var runtime = new CloudBackupRuntime(config, () => f.Drive, 1);
        var manual = await runtime.ExecuteAsync(SyncDirection.Backup, null, false, false, null, default);
        Assert.AreEqual("Backup", manual.Mode); Assert.AreEqual(0, manual.ExitCode, JsonSerializer.Serialize(manual));
        await Apply(b.S, SyncDirection.Pull);
        Write(b.P, "manual.txt", "cloud update"); await Apply(b.S);
        Write(a.P, "local-only.txt", "must remain local"); Write(other, "upload.txt", "automatic upload");
        var automatic = await Run(f, config);
        Assert.AreEqual("AutomaticMixed", automatic.Mode); Assert.AreEqual(0, automatic.ExitCode, JsonSerializer.Serialize(automatic));
        Assert.IsTrue(automatic.Projects.Single(p => p.ProjectId == a.P.ProjectId).Files.All(p => p.Direction == SyncDirection.Pull));
        Assert.IsTrue(automatic.Projects.Single(p => p.ProjectId == other.ProjectId).Files.All(p => p.Direction == SyncDirection.Backup));
        Assert.AreEqual("cloud update", File.ReadAllText(Path.Combine(a.P.Source, "manual.txt")));
        var root = (await f.Drive.FindAsync("drive-root", "Documents", default))!;
        Assert.IsNull(await f.Drive.FindAsync(root.Id, "local-only.txt", default));
        var otherRoot = (await f.Drive.FindAsync("drive-root", "Other", default))!;
        var uploaded = (await f.Drive.FindAsync(otherRoot.Id, "upload.txt", default))!;
        Assert.AreEqual("automatic upload", Encoding.UTF8.GetString(f.Drive.Items[uploaded.Id].Bytes));
    }

    [TestMethod]
    public async Task DownloadByteBudgetUsesCloudSizeAndLeavesNextFileForTheNextRun()
    {
        using var f = new Fixture(); var a = f.Device("A"); var b = f.Device("B");
        Write(b.P, "a.txt", new string('a', 20)); Write(b.P, "b.txt", new string('b', 20)); await Apply(b.S);
        var config = Download(a.C, a.P.ProjectId) with { AutomaticBudget = new(4) };
        var first = await Run(f, config);
        Assert.AreEqual("BudgetLimited", first.Completion, JsonSerializer.Serialize(first));
        Assert.AreEqual("AutomaticTransferBudgetReached", first.BudgetStopReason);
        Assert.AreEqual(new string('a', 20), File.ReadAllText(Path.Combine(a.P.Source, "a.txt")));
        Assert.IsFalse(File.Exists(Path.Combine(a.P.Source, "b.txt")));
        Assert.IsTrue(first.Projects.Single().Files.All(p => p.Direction == SyncDirection.Pull));
        var next = await Run(f, config);
        Assert.AreEqual(0, next.ExitCode, JsonSerializer.Serialize(next));
        Assert.AreEqual(new string('b', 20), File.ReadAllText(Path.Combine(a.P.Source, "b.txt")));
        Assert.IsFalse(a.S.Pending);
    }

    [TestMethod]
    public async Task CancelledDownloadIsRetainedWhileAnotherCloudFileContinuesWithoutUploadingLocalFiles()
    {
        using var f = new Fixture(); var a = f.Device("A"); var b = f.Device("B");
        Write(b.P, "a-cloud.txt", "cloud body"); await Apply(b.S);
        using var cancellation = new CancellationTokenSource();
        f.Drive.BeforeDownload = (name, token) =>
        {
            if (name == "a-cloud.txt") { cancellation.Cancel(); token.ThrowIfCancellationRequested(); }
            return Task.CompletedTask;
        };
        var config = Download(a.C, a.P.ProjectId);
        var cancelled = await Run(f, config, cancellation.Token);
        Assert.AreEqual(130, cancelled.ExitCode, JsonSerializer.Serialize(cancelled));
        var journal = Directory.GetFiles(CloudMapping.StateRoot(a.P, a.C), "*.json", SearchOption.AllDirectories)
            .Single(p => Path.GetFileName(Path.GetDirectoryName(p)) == "cloud-transactions");
        var original = File.ReadAllBytes(journal);
        f.Drive.BeforeDownload = null;
        Write(b.P, "b-cloud.txt", "other cloud file"); await Apply(b.S); Write(a.P, "local.txt", "never upload");
        var next = await Run(f, config);
        Assert.AreEqual("PendingFileOperation", next.Projects.Single().Files.Single(p => p.Path == "a-cloud.txt").Error);
        Assert.AreEqual("other cloud file", File.ReadAllText(Path.Combine(a.P.Source, "b-cloud.txt")));
        var root = (await f.Drive.FindAsync("drive-root", "Documents", default))!;
        Assert.IsNull(await f.Drive.FindAsync(root.Id, "local.txt", default));
        Assert.IsFalse(File.Exists(Path.Combine(a.P.Source, "a-cloud.txt")));
        CollectionAssert.AreEqual(original, File.ReadAllBytes(journal));
    }
}
