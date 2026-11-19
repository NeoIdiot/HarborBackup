using System.Text;
using System.Text.Json;
using HarborBackup.Core.Domain;
using HarborBackup.Windows.Integration;
using HarborBackup.Windows.Persistence;
using static HarborBackup.Windows.Tests.CloudTransportTests;

namespace HarborBackup.Windows.Tests;

[TestClass]
[DoNotParallelize]
public sealed class AutomaticBidirectionalTests
{
    private sealed class Clock : TimeProvider
    {
        public long Ticks;
        public override long GetTimestamp() => Ticks;
        public override long TimestampFrequency => TimeSpan.TicksPerSecond;
    }
    private static ConfigurationSnapshot Enable(ConfigurationSnapshot config, params string[] projects) =>
        config with { AutomaticBidirectionalProjects = new HashSet<string>(projects) };

    private static Task<BackupReport> Run(Fixture f, ConfigurationSnapshot config, bool automatic = true,
        CancellationToken token = default) => new CloudBackupRuntime(config, () => f.Drive, 1)
            .ExecuteAsync(SyncDirection.Backup, null, false, false, null, token, automatic: automatic);

    private static void Write(ProjectDefinition project, string name, string content)
    {
        var path = Path.Combine(project.Source, name);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content);
        File.SetLastWriteTimeUtc(path, DateTime.UtcNow.AddSeconds(-1));
    }

    private static async Task<string> CloudText(Fixture f, string destination, string name)
    {
        var parent = (await f.Drive.FindAsync("drive-root", destination, default))!;
        var file = (await f.Drive.FindAsync(parent.Id, name, default))!;
        return Encoding.UTF8.GetString(f.Drive.Items[file.Id].Bytes);
    }

    [TestMethod]
    public async Task DefaultAndManualBackupKeepUploadOnlyWhileEnabledAutomaticTransfersBothDirections()
    {
        using var f = new Fixture(); var a = f.Device("A"); var b = f.Device("B");
        Write(a.P, "remote.txt", "base"); await Apply(a.S); await Apply(b.S, SyncDirection.Pull);
        Write(b.P, "remote.txt", "cloud edit"); await Apply(b.S);
        var enabled = Enable(a.C, a.P.ProjectId);
        foreach (var config in new[] { a.C, enabled })
        {
            var result = await Run(f, config, automatic: config == a.C);
            Assert.AreEqual(FileAction.NeedsPull, result.Projects.Single().Files.Single().Action);
            Assert.AreEqual("base", File.ReadAllText(Path.Combine(a.P.Source, "remote.txt")));
        }
        Assert.AreEqual(1, f.Drive.BusinessDownloads); // B's initial manual pull only.
        Write(a.P, "local.txt", "local new file");
        var automatic = await Run(f, enabled);
        Assert.AreEqual(0, automatic.ExitCode, JsonSerializer.Serialize(automatic));
        Assert.AreEqual("AutomaticBidirectional", automatic.Mode);
        Assert.AreEqual("cloud edit", File.ReadAllText(Path.Combine(a.P.Source, "remote.txt")));
        Assert.AreEqual("local new file", await CloudText(f, "Documents", "local.txt"));
        Assert.AreEqual(SyncDirection.Pull, automatic.Projects.Single().Files.Single(p => p.Path == "remote.txt").Direction);
        Assert.AreEqual(SyncDirection.Backup, automatic.Projects.Single().Files.Single(p => p.Path == "local.txt").Direction);
        Assert.IsTrue(automatic.Projects.Single().CoverageComplete);
        Assert.IsFalse(a.S.Pending);
        var next = await Run(f, enabled);
        Assert.AreEqual(0, next.ExitCode);
        Assert.IsTrue(next.Projects.Single().Files.All(p => p.Action == FileAction.Equal && !p.Changed));
    }

    [TestMethod]
    public async Task MixedProjectsOnlyDownloadForTheEnabledProject()
    {
        using var f = new Fixture(); var a = f.Device("A"); var b = f.Device("B");
        var second = a.P with { Source = Path.Combine(f.Root, "A", "second"), Destination = "Other", TaskId = DeviceIdentity.Digest("second") };
        var peerSecond = second with { Source = Path.Combine(f.Root, "B", "second") };
        Directory.CreateDirectory(peerSecond.Source);
        var local = a.C with { Projects = [a.P, second] };
        var peer = b.C with { Projects = [b.P, peerSecond] };
        Write(a.P, "a.txt", "base"); Write(second, "a.txt", "base");
        Assert.AreEqual(0, (await Run(f, local, false)).ExitCode);
        foreach (var p in peer.Projects)
            Assert.AreEqual(0, (await new CloudBackupRuntime(peer, () => f.Drive)
                .ExecuteAsync(SyncDirection.Pull, p.ProjectId, false, false, null, default)).ExitCode);
        Write(b.P, "a.txt", "changed"); Write(peerSecond, "a.txt", "changed");
        Assert.AreEqual(0, (await Run(f, peer, false)).ExitCode);
        var result = await Run(f, Enable(local, a.P.ProjectId));
        Assert.AreEqual("changed", File.ReadAllText(Path.Combine(a.P.Source, "a.txt")));
        Assert.AreEqual("base", File.ReadAllText(Path.Combine(second.Source, "a.txt")));
        Assert.AreEqual(FileAction.NeedsPull, result.Projects.Single(p => p.ProjectId == second.ProjectId).Files.Single().Action);
        Assert.IsTrue(result.Projects.Single(p => p.ProjectId == a.P.ProjectId).CoverageComplete);
        Assert.IsFalse(result.Projects.Single(p => p.ProjectId == second.ProjectId).CoverageComplete);
    }

    [TestMethod]
    public async Task SingleSidedMissingFilesAndEmptyDirectoriesAreRecreatedWithoutDeletingTheSurvivingSide()
    {
        using var f = new Fixture(); var a = f.Device("A");
        Write(a.P, "missing-local.txt", "retained cloud"); Write(a.P, "missing-cloud.txt", "retained local");
        Directory.CreateDirectory(Path.Combine(a.P.Source, "empty-local"));
        Directory.CreateDirectory(Path.Combine(a.P.Source, "empty-cloud"));
        Assert.AreEqual(0, (await Run(f, a.C, false)).ExitCode);
        var root = (await f.Drive.FindAsync("drive-root", "Documents", default))!;
        await f.Drive.DeleteAsync((await f.Drive.FindAsync(root.Id, "missing-cloud.txt", default))!, default);
        await f.Drive.DeleteAsync((await f.Drive.FindAsync(root.Id, "empty-cloud", default))!, default);
        File.Delete(Path.Combine(a.P.Source, "missing-local.txt"));
        Directory.Delete(Path.Combine(a.P.Source, "empty-local"));
        var result = await Run(f, Enable(a.C, a.P.ProjectId));
        Assert.AreEqual(0, result.ExitCode, JsonSerializer.Serialize(result));
        Assert.AreEqual("retained cloud", File.ReadAllText(Path.Combine(a.P.Source, "missing-local.txt")));
        Assert.AreEqual("retained local", await CloudText(f, "Documents", "missing-cloud.txt"));
        Assert.IsTrue(Directory.Exists(Path.Combine(a.P.Source, "empty-local")));
        Assert.IsNotNull(await f.Drive.FindAsync(root.Id, "empty-cloud", default));
        Assert.IsFalse(a.S.Pending);
    }

    [TestMethod]
    public async Task DivergentAndUnbasedChangesAreProtectedAndUnknownCloudFilesAreNotDownloaded()
    {
        using var f = new Fixture(); var a = f.Device("A"); var b = f.Device("B");
        Write(a.P, "conflict.txt", "base"); await Apply(a.S); await Apply(b.S, SyncDirection.Pull);
        Write(b.P, "conflict.txt", "peer edit"); Write(b.P, "unbased.txt", "peer new"); await Apply(b.S);
        Write(a.P, "conflict.txt", "local edit"); Write(a.P, "unbased.txt", "local new");
        var root = (await f.Drive.FindAsync("drive-root", "Documents", default))!;
        f.Drive.Add(root.Id, "unknown.txt", Encoding.UTF8.GetBytes("unmanaged"));
        var downloads = f.Drive.BusinessDownloads;
        var result = await Run(f, Enable(a.C, a.P.ProjectId));
        Assert.AreEqual(2, result.ExitCode, JsonSerializer.Serialize(result));
        foreach (var path in new[] { "conflict.txt", "unbased.txt" })
            Assert.AreEqual(FileAction.ConflictUnresolved, result.Projects.Single().Files.Single(p => p.Path == path).Action);
        Assert.AreEqual("CloudMetadataRequired", result.Projects.Single().Files.Single(p => p.Path == "unknown.txt").Error);
        Assert.AreEqual("local edit", File.ReadAllText(Path.Combine(a.P.Source, "conflict.txt")));
        Assert.AreEqual("local new", File.ReadAllText(Path.Combine(a.P.Source, "unbased.txt")));
        Assert.AreEqual("peer edit", await CloudText(f, "Documents", "conflict.txt"));
        Assert.AreEqual("peer new", await CloudText(f, "Documents", "unbased.txt"));
        Assert.IsFalse(File.Exists(Path.Combine(a.P.Source, "unknown.txt")));
        Assert.AreEqual(downloads, f.Drive.BusinessDownloads);
        var conflicts = (await f.Drive.FindAsync("root", "conflicts", default))!;
        var conflictProject = (await f.Drive.FindAsync(conflicts.Id, "Documents", default))!;
        var protectedContents = (await f.Drive.ChildrenAsync(conflictProject.Id, default))
            .Select(item => Encoding.UTF8.GetString(f.Drive.Items[item.Id].Bytes)).ToArray();
        CollectionAssert.AreEquivalent(new[] { "peer edit", "local edit", "peer new", "local new" }, protectedContents);
        Assert.IsFalse(a.S.Pending);
    }

    [TestMethod]
    public async Task FreshLocalHashPreventsDownloadOverAnEditHiddenByUnchangedSizeAndTime()
    {
        using var f = new Fixture(); var a = f.Device("A"); var b = f.Device("B");
        Write(a.P, "a.txt", "base"); await Apply(a.S); await Apply(b.S, SyncDirection.Pull);
        var path = Path.Combine(a.P.Source, "a.txt"); var stamp = File.GetLastWriteTimeUtc(path);
        Write(b.P, "a.txt", "cloud changed"); await Apply(b.S);
        File.WriteAllText(path, "edit"); File.SetLastWriteTimeUtc(path, stamp);
        var downloads = f.Drive.BusinessDownloads;
        var result = await Run(f, Enable(a.C, a.P.ProjectId));
        Assert.AreEqual(FileAction.ConflictUnresolved, result.Projects.Single().Files.Single().Action);
        Assert.AreEqual("edit", File.ReadAllText(path));
        Assert.AreEqual("cloud changed", await CloudText(f, "Documents", "a.txt"));
        Assert.AreEqual(downloads, f.Drive.BusinessDownloads);
        Assert.IsFalse(a.S.Pending);
    }

    [TestMethod]
    public async Task UploadsAndCloudSizedDownloadsShareTheSameBudget()
    {
        using var f = new Fixture(); var a = f.Device("A"); var b = f.Device("B");
        foreach (var name in new[] { "b-download.txt", "c-download.txt" }) Write(a.P, name, "x");
        await Apply(a.S); await Apply(b.S, SyncDirection.Pull);
        foreach (var name in new[] { "b-download.txt", "c-download.txt" }) Write(b.P, name, new string('y', 20));
        await Apply(b.S); Write(a.P, "a-upload.txt", "123");
        var config = Enable(a.C, a.P.ProjectId) with { AutomaticBudget = new(4) };
        var result = await Run(f, config);
        Assert.AreEqual("BudgetLimited", result.Completion, JsonSerializer.Serialize(result));
        Assert.AreEqual("AutomaticTransferBudgetReached", result.BudgetStopReason);
        Assert.AreEqual("123", await CloudText(f, "Documents", "a-upload.txt"));
        Assert.AreEqual(new string('y', 20), File.ReadAllText(Path.Combine(a.P.Source, "b-download.txt")));
        Assert.AreEqual("x", File.ReadAllText(Path.Combine(a.P.Source, "c-download.txt")));
        Assert.IsFalse(a.S.Pending);
        var next = await Run(f, config);
        Assert.IsTrue(next.Projects.Single().Files.Single(p => p.Path == "c-download.txt").Changed);
        Assert.AreEqual(new string('y', 20), File.ReadAllText(Path.Combine(a.P.Source, "c-download.txt")));
    }

    [TestMethod]
    public async Task CancellationRetainsPullJournalAndNextAutomaticRunSkipsItWhileOtherFilesContinue()
    {
        using var f = new Fixture(); var a = f.Device("A"); var b = f.Device("B");
        Write(b.P, "a-cloud.txt", "cloud body"); await Apply(b.S);
        using var cancellation = new CancellationTokenSource();
        f.Drive.BeforeDownload = (name, token) =>
        {
            if (name == "a-cloud.txt") { cancellation.Cancel(); token.ThrowIfCancellationRequested(); }
            return Task.CompletedTask;
        };
        var config = Enable(a.C, a.P.ProjectId);
        var cancelled = await Run(f, config, token: cancellation.Token);
        Assert.AreEqual(130, cancelled.ExitCode); Assert.IsTrue(cancelled.Cancelled);
        var journal = Directory.GetFiles(CloudMapping.StateRoot(a.P, a.C), "*.json", SearchOption.AllDirectories)
            .Single(p => Path.GetFileName(Path.GetDirectoryName(p)) == "cloud-transactions");
        var bytes = File.ReadAllBytes(journal);
        f.Drive.BeforeDownload = null; Write(a.P, "b-local.txt", "other file");
        var next = await Run(f, config);
        Assert.AreEqual("PendingFileOperation", next.Projects.Single().Files.Single(p => p.Path == "a-cloud.txt").Error);
        Assert.IsTrue(next.Projects.Single().Files.Single(p => p.Path == "b-local.txt").Changed);
        Assert.AreEqual("other file", await CloudText(f, "Documents", "b-local.txt"));
        Assert.IsFalse(File.Exists(Path.Combine(a.P.Source, "a-cloud.txt")));
        CollectionAssert.AreEqual(bytes, File.ReadAllBytes(journal));
    }

    [TestMethod]
    public async Task NewMappingsWaitForManualInitializationAndMissingExistingRootsAreNotRecreated()
    {
        using var f = new Fixture(); var a = f.Device("A"); var b = f.Device("B");
        Write(b.P, "a.txt", "cloud"); await Apply(b.S);
        var project = a.P with { MappingVersion = Guid.NewGuid().ToString("N") };
        var config = Enable(a.C with { Projects = [project] }, project.ProjectId);
        var awaiting = await Run(f, config);
        Assert.AreEqual("MappingAwaitingOperation", awaiting.Projects.Single().Issues.Single().Code);
        Assert.IsFalse(File.Exists(Path.Combine(a.P.Source, "a.txt")));
        var initialized = await new CloudBackupRuntime(config, () => f.Drive)
            .ExecuteAsync(SyncDirection.Pull, project.ProjectId, false, false, null, default);
        Assert.AreEqual(0, initialized.ExitCode);
        Assert.IsFalse(CloudMapping.NeedsFirstOperation(project, config));
        Directory.Delete(project.Source, true);
        var missing = await Run(f, config);
        Assert.AreNotEqual(0, missing.ExitCode);
        Assert.IsFalse(Directory.Exists(project.Source));
        Assert.AreEqual("cloud", await CloudText(f, "Documents", "a.txt"));
    }

    [TestMethod]
    public async Task PersistedPauseSkipsBothDirectionsWithoutOpeningCloudOrReplacingLastRun()
    {
        using var f = new Fixture(); var a = f.Device("A"); var b = f.Device("B");
        Write(a.P, "a.txt", "base"); await Run(f, a.C, false); await Apply(b.S, SyncDirection.Pull);
        Write(b.P, "a.txt", "cloud changed"); await Apply(b.S); Write(a.P, "local.txt", "local new");
        var previous = RunStatusStore.Read(a.C.Preview)!.ToJsonString();
        var config = Enable(a.C, a.P.ProjectId);
        var control = new DeviceControlStore(config.Preview); control.SetPaused(true);
        var paused = await new CloudBackupRuntime(config, () => throw new AssertFailedException("Paused work must not open the cloud."))
            .ExecuteAsync(SyncDirection.Backup, null, false, false, null, default, automatic: true);
        Assert.AreEqual("Paused", paused.CloudUploadStatus);
        Assert.IsEmpty(paused.Projects.Single().Files);
        Assert.AreEqual(previous, RunStatusStore.Read(a.C.Preview)!.ToJsonString());
        Assert.AreEqual("base", File.ReadAllText(Path.Combine(a.P.Source, "a.txt")));
        control.SetPaused(false);
        Assert.AreEqual(0, (await Run(f, config)).ExitCode);
        Assert.AreEqual("cloud changed", File.ReadAllText(Path.Combine(a.P.Source, "a.txt")));
        Assert.AreEqual("local new", await CloudText(f, "Documents", "local.txt"));
    }

    [TestMethod]
    public async Task UnreadablePauseRecordDoesNotAuthorizeAutomaticCloudAccess()
    {
        using var f = new Fixture(); var a = f.Device("A");
        var control = new DeviceControlStore(a.C.Preview); control.SetPaused(false);
        var path = Path.Combine(a.C.Preview.StateDirectory, "devices", a.C.Preview.DeviceId, "scheduler.json");
        File.WriteAllText(path, "broken");
        await Assert.ThrowsAsync<JsonException>(() => new CloudBackupRuntime(Enable(a.C, a.P.ProjectId),
            () => throw new AssertFailedException("Unreadable pause state must prevent cloud access."))
            .ExecuteAsync(SyncDirection.Backup, null, false, false, null, default, automatic: true));
        Assert.AreEqual("broken", File.ReadAllText(path));
    }

    [TestMethod]
    public async Task DownloadFinishesWhenTimeBudgetExpiresAndNextFileWaits()
    {
        using var f = new Fixture(); var a = f.Device("A"); var b = f.Device("B"); var clock = new Clock();
        Write(b.P, "a.txt", "first"); Write(b.P, "b.txt", "second"); await Apply(b.S);
        f.Drive.BeforeDownload = (name, _) =>
        {
            if (name == "a.txt") clock.Ticks = TimeSpan.FromMinutes(2).Ticks;
            return Task.CompletedTask;
        };
        var config = Enable(a.C, a.P.ProjectId) with { AutomaticBudget = new(0, 1) };
        var report = await new CloudBackupRuntime(config, () => f.Drive, 1, clock)
            .ExecuteAsync(SyncDirection.Backup, null, false, false, null, default, automatic: true);
        Assert.AreEqual("BudgetLimited", report.Completion, JsonSerializer.Serialize(report));
        Assert.AreEqual("AutomaticTimeBudgetReached", report.BudgetStopReason);
        Assert.AreEqual("first", File.ReadAllText(Path.Combine(a.P.Source, "a.txt")));
        Assert.IsFalse(File.Exists(Path.Combine(a.P.Source, "b.txt")));
        Assert.IsFalse(a.S.Pending);
    }
}
