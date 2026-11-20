using System.Text.Json;
using System.Text.Json.Nodes;
using HarborBackup.Core.Domain;
using HarborBackup.Windows.Integration;
using HarborBackup.Windows.Persistence;
using static HarborBackup.Windows.Tests.CloudTransportTests;

namespace HarborBackup.Windows.Tests;

[TestClass]
[DoNotParallelize]
public sealed class CloudResumeTaskStoreTests
{
    private static (ProjectDefinition Project, ConfigurationSnapshot Config) Mapping(Fixture fixture)
    {
        var device = fixture.Device("A");
        var project = device.P with { BindingVersion = 1, MappingVersion = Guid.NewGuid().ToString("N") };
        var config = device.C with { Projects = [project] };
        CloudMapping.MarkReady(project, config);
        return (project, config);
    }
    private static string ControlPath(ProjectDefinition project, ConfigurationSnapshot config) =>
        Path.Combine(CloudMapping.StateRoot(project, config), "resume-task.json");
    private static CloudJournal Journal(ProjectDefinition project, ConfigurationSnapshot config, string relative) =>
        new(5, Guid.NewGuid().ToString("N"), CloudMapping.Bind(project, config), relative, SyncDirection.Backup,
            new(DeviceIdentity.Digest("payload"), 12, DateTimeOffset.UnixEpoch, DateTimeOffset.UnixEpoch), null, null,
            true, ProtectionKind.None, "drive-root", CreatedUtc: DateTimeOffset.UtcNow, Mapping: CloudMapping.Capture(project, config));

    [TestMethod]
    public void PerFileProgressDoesNotRewriteTheWholeAuthorizationOrControl()
    {
        using var fixture = new Fixture(); var (project, config) = Mapping(fixture);
        var selection = new PullSelection(project.Destination, true,
            Enumerable.Range(0, 128).Select(i => new PullFileSelection("file-" + i + ".json", DeviceIdentity.Digest("confirmation-" + i), true, 12)).ToArray())
            { ProjectId = project.ProjectId };
        var task = CloudResumeTaskStore.Create(project, config, SyncDirection.Backup, false, selection);
        var control = ControlPath(project, config);
        var root = Path.Combine(Path.GetDirectoryName(control)!, "resume-tasks", task.Value.Id);
        var authorization = File.ReadAllText(Path.Combine(root, "authorization.json"));
        var scope = File.ReadAllText(Path.Combine(root, "scope.json"));
        var before = File.ReadAllText(control);
        for (var i = 0; i < 32; i++)
        {
            var relative = selection.Files[i].Path;
            task.Reserve(relative, 12);
            task.Track(Journal(project, config, relative));
            task.Complete(relative, DeviceIdentity.Digest("payload"));
        }
        Assert.AreEqual(before, File.ReadAllText(control));
        Assert.AreEqual(authorization, File.ReadAllText(Path.Combine(root, "authorization.json")));
        Assert.AreEqual(scope, File.ReadAllText(Path.Combine(root, "scope.json")));
        var receipts = Directory.GetFiles(Path.Combine(root, "files"), "*.json");
        Assert.HasCount(32, receipts);
        Assert.IsTrue(receipts.All(p => new FileInfo(p).Length < authorization.Length), "A hot receipt must not contain all project selections.");
        var reloaded = CloudResumeTaskStore.Read(project, config)!;
        Assert.AreEqual(384L, reloaded.Value.ReservedBytes);
        Assert.HasCount(32, reloaded.Value.Completed);
        task.AddActiveTime(1700); task.SetState(CloudResumeState.NetworkWait);
        Assert.IsLessThan(2048L, new FileInfo(control).Length, "Control remains bounded regardless of project size.");
    }

    [TestMethod]
    public void NewJournalForTheSamePathClearsTheOldCompletion()
    {
        using var fixture = new Fixture(); var (project, config) = Mapping(fixture);
        var task = CloudResumeTaskStore.Create(project, config, SyncDirection.Backup, true, null);
        task.SetScope(["a.txt"]);
        var old = Journal(project, config, "a.txt"); task.Track(old);
        task.Complete("a.txt", old.Local!.Sha256);
        task.Track(old with { Phase = "Uploaded" });
        Assert.IsTrue(task.HasCompleted("a.txt"));
        task.Track(Journal(project, config, "a.txt"));
        Assert.IsFalse(task.HasCompleted("a.txt"));
        Assert.IsFalse(CloudResumeTaskStore.Read(project, config)!.HasCompleted("a.txt"));
    }

    [TestMethod]
    public void ChangedIntentPreservesOriginalAuthorizationAndRequiresConfirmation()
    {
        using var fixture = new Fixture(); var (project, config) = Mapping(fixture);
        var task = CloudResumeTaskStore.Create(project, config, SyncDirection.Backup, false, null);
        task.SetScope(["a.txt"]);
        var original = Journal(project, config, "a.txt"); task.Track(original);
        task.Track(original with { Publish = false, Protection = ProtectionKind.Conflict });
        var loaded = CloudResumeTaskStore.Read(project, config)!;
        Assert.AreEqual(CloudResumeState.AwaitingConfirmation, loaded.Value.State);
        Assert.IsTrue(loaded.Value.Journals.Single().Publish);
        Assert.IsFalse(loaded.LatestJournal(original.Id).Publish);
        Assert.IsEmpty(CloudAutomaticRecovery.ReadCandidates(config));
    }

    [TestMethod]
    public void BudgetEpochIncludesEarlierProjectsAndResetsOnlyForANormalCycle()
    {
        using var fixture = new Fixture(); var (project, config) = Mapping(fixture);
        var task = CloudResumeTaskStore.Create(project, config, SyncDirection.Backup, true, null);
        task.SetScope(["a.txt"]);
        task.ResetBudget(16, 400);
        task.Reserve("a.txt", 2); task.AddActiveTime(100);
        var restarted = CloudResumeTaskStore.Read(project, config)!;
        Assert.AreEqual(18L, restarted.Value.ReservedBytes);
        Assert.AreEqual(500L, restarted.Value.ActiveMilliseconds);
        Assert.IsTrue(restarted.WasReserved("a.txt"));
        restarted.ResetBudget(3, 100);
        Assert.IsFalse(restarted.WasReserved("a.txt"));
        restarted.Reserve("a.txt", 4);
        Assert.AreEqual(7L, CloudResumeTaskStore.Read(project, config)!.Value.ReservedBytes);
    }

    [TestMethod]
    public void AReplacementIntentAddsItsBytesAndRemainsIdempotentAcrossRestart()
    {
        using var fixture = new Fixture(); var (project, config) = Mapping(fixture);
        var task = CloudResumeTaskStore.Create(project, config, SyncDirection.Backup, true, null);
        task.SetScope(["a.txt"]); task.ResetBudget(5, 0);
        task.Reserve("a.txt", 2);
        task.Complete("a.txt", null); // The original no-write intent was safely retired.
        var restarted = CloudResumeTaskStore.Read(project, config)!;
        Assert.IsTrue(restarted.HasCompleted("a.txt"));
        restarted.Reserve("a.txt", 100);
        Assert.AreEqual(107L, restarted.Value.ReservedBytes, "The new source version does not inherit free bytes from the old path reservation.");
        Assert.IsFalse(restarted.HasCompleted("a.txt"));
        restarted.Reserve("a.txt", 100);
        Assert.AreEqual(107L, restarted.Value.ReservedBytes, "Resuming the same new intent must not reserve it twice.");
        var reloaded = CloudResumeTaskStore.Read(project, config)!;
        Assert.AreEqual(107L, reloaded.Value.ReservedBytes);
        reloaded.Reserve("a.txt", 100);
        Assert.AreEqual(107L, reloaded.Value.ReservedBytes);
    }

    [TestMethod]
    public void ANormalBudgetEpochCountsOnlyTheCurrentIntentAfterEarlierVersions()
    {
        using var fixture = new Fixture(); var (project, config) = Mapping(fixture);
        var task = CloudResumeTaskStore.Create(project, config, SyncDirection.Backup, true, null);
        task.SetScope(["a.txt"]); task.Reserve("a.txt", 2);
        task.Complete("a.txt", null); task.Reserve("a.txt", 100);
        Assert.AreEqual(102L, task.Value.ReservedBytes);
        task.ResetBudget(3, 10);
        Assert.IsFalse(task.WasReserved("a.txt"));
        task.Reserve("a.txt", 100); task.Reserve("a.txt", 100);
        var reloaded = CloudResumeTaskStore.Read(project, config)!;
        Assert.AreEqual(103L, reloaded.Value.ReservedBytes, "A normal cycle inherits only its new shared allowance, not earlier intent reservations.");
        Assert.AreEqual(10L, reloaded.Value.ActiveMilliseconds);
        Assert.IsTrue(reloaded.WasReserved("a.txt"));
    }

    [TestMethod]
    public void CorruptReceiptNeverBecomesAnAutomaticRetryCandidate()
    {
        using var fixture = new Fixture(); var (project, config) = Mapping(fixture);
        var task = CloudResumeTaskStore.Create(project, config, SyncDirection.Backup, true, null);
        task.SetScope(["a.txt"]); task.Track(Journal(project, config, "a.txt"));
        task.SetRecovery("a.txt", RecoveryDisposition.AutomaticRetry);
        task.SetState(CloudResumeState.NetworkWait);
        Assert.HasCount(1, CloudAutomaticRecovery.ReadCandidates(config));
        var receipt = Directory.GetFiles(Path.Combine(CloudMapping.StateRoot(project, config), "resume-tasks", task.Value.Id, "files"), "*.json").Single();
        var record = JsonNode.Parse(File.ReadAllText(receipt))!;
        record["path"] = "other.txt"; File.WriteAllText(receipt, record.ToJsonString());
        Assert.ThrowsExactly<IOException>(() => CloudResumeTaskStore.Read(project, config));
        Assert.IsEmpty(CloudAutomaticRecovery.ReadCandidates(config));
        Assert.IsTrue(File.Exists(receipt));
    }

    [TestMethod]
    [DataRow("Stopped")]
    [DataRow("BudgetWait")]
    [DataRow("AwaitingConfirmation")]
    public void WaitingStatesDoNotAcquireFastRetryAuthorization(string state)
    {
        using var fixture = new Fixture(); var (project, config) = Mapping(fixture);
        var task = CloudResumeTaskStore.Create(project, config, SyncDirection.Backup, true, null);
        task.SetScope(["a.txt"]); task.SetState(Enum.Parse<CloudResumeState>(state));
        Assert.IsEmpty(CloudAutomaticRecovery.ReadCandidates(config));
        var changed = project with { Source = Path.Combine(fixture.Root, "another-source"), MappingVersion = Guid.NewGuid().ToString("N") };
        Assert.IsNull(CloudResumeTaskStore.Read(changed, config with { Projects = [changed] }));
        Assert.IsTrue(File.Exists(ControlPath(project, config)));
    }

    private static CloudResumeTaskStore WaitingTask(ProjectDefinition project, ConfigurationSnapshot config,
        string relative, DateTimeOffset? deadline = null)
    {
        var selection = new PullSelection(project.Destination, true,
            [new(relative, DeviceIdentity.Digest("confirmation-" + relative), true, 12)]) { ProjectId = project.ProjectId };
        var task = CloudResumeTaskStore.Create(project, config, SyncDirection.Backup, false, selection);
        task.Track(Journal(project, config, relative));
        task.SetRecovery(relative, RecoveryDisposition.AutomaticRetry);
        task.SetState(CloudResumeState.NetworkWait, deadline);
        return task;
    }
    private static string QueuedPath(ProjectDefinition project, ConfigurationSnapshot config, string id) =>
        Path.Combine(CloudMapping.StateRoot(project, config), "active-tasks", id + ".json");

    [TestMethod]
    public void AnotherTaskKeepsTheOriginalIndependentAuthorizationQueued()
    {
        using var fixture = new Fixture(); var (project, config) = Mapping(fixture);
        var first = WaitingTask(project, config, "a.txt");
        var original = File.ReadAllText(ControlPath(project, config));
        var second = CloudResumeTaskStore.Create(project, config, SyncDirection.Backup, false,
            new(project.Destination, true, [new("b.txt", DeviceIdentity.Digest("b-confirmation"), true, 12)]) { ProjectId = project.ProjectId });
        Assert.AreEqual(original, File.ReadAllText(QueuedPath(project, config, first.Value.Id)));
        Assert.AreEqual(second.Value.Id, CloudResumeTaskStore.Read(project, config)!.Value.Id);
        var active = CloudResumeTaskStore.ReadActive(project, config)!;
        Assert.AreEqual(first.Value.Id, active.Value.Id);
        CollectionAssert.AreEqual(new[] { "a.txt" }, active.Value.Scope!);
        Assert.IsTrue(active.CanRetry("a.txt")); Assert.IsFalse(active.CanRetry("b.txt"));
        Assert.HasCount(1, CloudAutomaticRecovery.ReadCandidates(config));
        active.Complete("a.txt", DeviceIdentity.Digest("payload"));
        active.SetState(CloudResumeState.Completed);
        Assert.IsFalse(File.Exists(QueuedPath(project, config, first.Value.Id)));
        Assert.AreEqual(second.Value.Id, CloudResumeTaskStore.ReadActive(project, config)!.Value.Id);
        Assert.IsEmpty(CloudAutomaticRecovery.ReadCandidates(config));
        var retained = Directory.GetFiles(Path.Combine(CloudMapping.StateRoot(project, config), "retained-tasks"), "*.json");
        Assert.HasCount(1, retained);
        Assert.AreEqual("Completed", JsonNode.Parse(File.ReadAllText(retained.Single()))!["state"]!.GetValue<string>());
        Assert.ThrowsExactly<IOException>(() => active.SetState(CloudResumeState.NetworkWait), "Retained controls cannot acquire authority again.");
    }

    [TestMethod]
    public void CandidateIdentityIsMergedAndCooldownIncludesQueuedTasks()
    {
        using var fixture = new Fixture(); var (project, config) = Mapping(fixture);
        var firstDeadline = DateTimeOffset.UtcNow.AddMinutes(5);
        var secondDeadline = firstDeadline.AddMinutes(5);
        var first = WaitingTask(project, config, "a.txt", secondDeadline);
        WaitingTask(project, config, "b.txt", firstDeadline);
        Assert.AreEqual(first.Value.Id, CloudResumeTaskStore.ReadActive(project, config)!.Value.Id);
        Assert.IsTrue(CloudResumeTaskStore.IsWaiting(project, config));
        var candidate = CloudAutomaticRecovery.ReadCandidates(config).Single();
        Assert.AreEqual(project.ProjectId, candidate.ProjectId);
        Assert.AreEqual(secondDeadline, candidate.RetryNotBeforeUtc);
        Assert.AreEqual(secondDeadline, CloudAutomaticRecovery.RetryNotBefore(config));
        var otherAccount = config with { CloudAccount = fixture.Account with { AccountId = "other-account" } };
        Assert.IsNull(CloudAutomaticRecovery.RetryNotBefore(otherAccount));
    }

    [TestMethod]
    public void NetworkTasksPrecedeBudgetTasksAndBudgetTasksSurviveNewWork()
    {
        using var fixture = new Fixture(); var (project, config) = Mapping(fixture);
        var first = CloudResumeTaskStore.Create(project, config, SyncDirection.Backup, true, null);
        first.SetScope(["a.txt"]); first.SetState(CloudResumeState.BudgetWait);
        var second = CloudResumeTaskStore.Create(project, config, SyncDirection.Backup, true, null);
        second.SetScope(["b.txt"]);
        Assert.AreEqual(first.Value.Id, CloudResumeTaskStore.ReadActive(project, config)!.Value.Id);
        second.SetState(CloudResumeState.NetworkWait);
        Assert.AreEqual(second.Value.Id, CloudResumeTaskStore.ReadActive(project, config)!.Value.Id);
        Assert.IsTrue(File.Exists(QueuedPath(project, config, first.Value.Id)));
    }

    [TestMethod]
    public void StaleDuplicateCannotReviveAStoppedCurrentTask()
    {
        using var fixture = new Fixture(); var (project, config) = Mapping(fixture);
        var task = WaitingTask(project, config, "a.txt");
        var duplicate = QueuedPath(project, config, task.Value.Id);
        Directory.CreateDirectory(Path.GetDirectoryName(duplicate)!);
        File.Copy(ControlPath(project, config), duplicate);
        task.SetState(CloudResumeState.Stopped);
        Assert.IsFalse(CloudResumeTaskStore.IsWaiting(project, config));
        Assert.IsEmpty(CloudAutomaticRecovery.ReadCandidates(config));
        Assert.IsNull(CloudAutomaticRecovery.RetryNotBefore(config));
        CloudResumeTaskStore.Create(project, config, SyncDirection.Backup, true, null);
        Assert.IsFalse(File.Exists(duplicate), "The current terminal marker must supersede its earlier copied network state.");
        Assert.IsEmpty(CloudAutomaticRecovery.ReadCandidates(config));
        var retained = Directory.GetFiles(Path.Combine(CloudMapping.StateRoot(project, config), "retained-tasks"), "*.json");
        Assert.HasCount(2, retained, "Both the stale duplicate and authoritative stopped marker remain as evidence.");
    }

    [TestMethod]
    public void NewCurrentSnapshotSupersedesAnEarlierDuplicateBeforeItIsQueued()
    {
        using var fixture = new Fixture(); var (project, config) = Mapping(fixture);
        var task = WaitingTask(project, config, "a.txt");
        var duplicate = QueuedPath(project, config, task.Value.Id);
        Directory.CreateDirectory(Path.GetDirectoryName(duplicate)!);
        File.Copy(ControlPath(project, config), duplicate);
        var deadline = DateTimeOffset.UtcNow.AddMinutes(20);
        task.SetState(CloudResumeState.NetworkWait, deadline);
        var authoritative = File.ReadAllText(ControlPath(project, config));
        CloudResumeTaskStore.Create(project, config, SyncDirection.Backup, true, null);
        Assert.AreEqual(authoritative, File.ReadAllText(duplicate));
        Assert.AreEqual(deadline, CloudResumeTaskStore.ReadActive(project, config)!.Value.RetryNotBeforeUtc);
    }

    [TestMethod]
    public void ExternallyChangedQueuedControlCannotBeUpdatedOrMovedAsIfTrusted()
    {
        using var fixture = new Fixture(); var (project, config) = Mapping(fixture);
        var task = WaitingTask(project, config, "a.txt");
        CloudResumeTaskStore.Create(project, config, SyncDirection.Backup, true, null);
        var queued = CloudResumeTaskStore.ReadActive(project, config)!;
        var path = QueuedPath(project, config, task.Value.Id);
        var changed = JsonNode.Parse(File.ReadAllText(path))!;
        changed["activeMilliseconds"] = 99;
        File.WriteAllText(path, changed.ToJsonString());
        var evidence = File.ReadAllText(path);
        Assert.ThrowsExactly<IOException>(() => queued.SetState(CloudResumeState.Completed));
        Assert.AreEqual(evidence, File.ReadAllText(path));
        Assert.IsTrue(File.Exists(path));
    }

    [TestMethod]
    public void CompletedHotPathDoesNotReadOldAuthorizationOrReceiptBodies()
    {
        using var fixture = new Fixture(); var (project, config) = Mapping(fixture);
        var task = CloudResumeTaskStore.Create(project, config, SyncDirection.Backup, true, null);
        task.SetScope(["a.txt"]); task.Track(Journal(project, config, "a.txt"));
        task.SetState(CloudResumeState.Completed);
        var taskRoot = Path.Combine(CloudMapping.StateRoot(project, config), "resume-tasks", task.Value.Id);
        File.WriteAllText(Path.Combine(taskRoot, "authorization.json"), "unreadable old authorization");
        File.WriteAllText(Directory.GetFiles(Path.Combine(taskRoot, "files"), "*.json").Single(), "unreadable old receipt");
        Assert.IsFalse(CloudResumeTaskStore.IsWaiting(project, config));
        Assert.IsEmpty(CloudAutomaticRecovery.ReadCandidates(config));
        Assert.IsNull(CloudAutomaticRecovery.RetryNotBefore(config));
        Assert.IsNull(CloudResumeTaskStore.ReadActive(project, config));
        Assert.ThrowsExactly<JsonException>(() => CloudResumeTaskStore.Read(project, config), "Explicit audit reads still validate historical ledgers.");
    }

    [TestMethod]
    public void MixedNetworkStateCannotRetryTheFileWhoseIntentChanged()
    {
        using var fixture = new Fixture(); var (project, config) = Mapping(fixture);
        var task = CloudResumeTaskStore.Create(project, config, SyncDirection.Backup, true, null);
        task.SetScope(["a.txt", "b.txt"]);
        var original = Journal(project, config, "a.txt");
        task.Track(original); task.SetRecovery("a.txt", RecoveryDisposition.AutomaticRetry);
        task.Track(original with { Publish = false, Protection = ProtectionKind.Conflict });
        task.SetRecovery("a.txt", RecoveryDisposition.AutomaticRetry);
        task.Track(Journal(project, config, "b.txt")); task.SetRecovery("b.txt", RecoveryDisposition.AutomaticRetry);
        task.SetState(CloudResumeState.NetworkWait);
        var restarted = CloudResumeTaskStore.Read(project, config)!;
        Assert.AreEqual(RecoveryDisposition.AwaitingConfirmation, restarted.GetRecovery("a.txt"));
        Assert.IsFalse(restarted.CanRetry("a.txt")); Assert.IsTrue(restarted.CanRetry("b.txt"));
        Assert.HasCount(1, CloudAutomaticRecovery.ReadCandidates(config));
    }
}
