using System.Text;
using System.Text.Json.Nodes;
using HarborBackup.Core.Domain;
using HarborBackup.OneDrive.Authentication;
using HarborBackup.Windows.Persistence;
using HarborBackup.OneDrive.Transport;
using HarborBackup.Windows.Integration;
using HarborBackup.Windows.Persistence.Configuration;
using static HarborBackup.Windows.Tests.CloudTransportTests;

namespace HarborBackup.Windows.Tests;

[TestClass]
[DoNotParallelize]
public sealed class AutomaticNetworkRecoveryTests
{
    private static (ProjectDefinition P, ConfigurationSnapshot C) Versioned(Fixture f, bool ready)
    {
        var device = f.Device("A");
        var p = device.P with { BindingVersion = 1, MappingVersion = Guid.NewGuid().ToString("N") };
        var c = device.C with { Projects = [p] };
        if (ready) CloudMapping.MarkReady(p, c);
        return (p, c);
    }
    private static CloudBackupRuntime Runtime(ConfigurationSnapshot c, NetworkDrive drive) => new(c, () => drive, fileConcurrency: 1);
    private static Task<BackupReport> Run(CloudBackupRuntime runtime, string project, bool retry = false) =>
        runtime.ExecuteAsync(SyncDirection.Backup, project, false, false, null, default, automatic: true, retryOnly: retry);
    private static long Bodies(Fixture f, string name) => f.Drive.BodyTransfers.Count(t => t.Direction == "Upload" && t.Name == name);
    private static void Interrupt(Fixture f, NetworkDrive network, string name)
    {
        f.Drive.BeforeUpload = (file, _) =>
        {
            if (file == name) { network.Offline = true; throw new HttpRequestException("Synthetic connection interrupted."); }
            return Task.CompletedTask;
        };
    }
    private static void Reconnect(Fixture f, NetworkDrive network)
    { network.Offline = false; f.Drive.BeforeUpload = null; f.Drive.AfterUpload = null; }

    [TestMethod]
    public async Task NetworkInterruptionIsPersistedAndNewRuntimeResumesOriginalUpload()
    {
        using var f = new Fixture(); var a = Versioned(f, true); var network = new NetworkDrive(f.Drive);
        File.WriteAllText(Path.Combine(a.P.Source, "a.txt"), "original intent");
        Interrupt(f, network, "a.txt");
        var failed = await Run(Runtime(a.C, network), a.P.ProjectId);
        Assert.AreEqual(RecoveryDisposition.AutomaticRetry, failed.Projects.Single().Recovery);
        Assert.HasCount(1, CloudAutomaticRecovery.ReadCandidates(a.C));
        Reconnect(f, network);
        var recovered = await Run(Runtime(a.C, network), a.P.ProjectId, retry: true);
        Assert.AreEqual(0, recovered.ExitCode, System.Text.Json.JsonSerializer.Serialize(recovered));
        Assert.AreEqual(1L, Bodies(f, "a.txt"));
        Assert.HasCount(0, CloudAutomaticRecovery.ReadCandidates(a.C));
    }

    [TestMethod]
    public async Task LostUploadReceiptIsVerifiedWithoutSendingBodyAgain()
    {
        using var f = new Fixture(); var a = Versioned(f, true); var network = new NetworkDrive(f.Drive);
        File.WriteAllText(Path.Combine(a.P.Source, "a.txt"), "acknowledgement lost");
        f.Drive.AfterUpload = item =>
        {
            if (item.Name == "a.txt") { network.Offline = true; throw new HttpRequestException("Synthetic lost upload response."); }
            return Task.CompletedTask;
        };
        await Run(Runtime(a.C, network), a.P.ProjectId);
        Assert.AreEqual(1L, Bodies(f, "a.txt")); Reconnect(f, network);
        var recovered = await Run(Runtime(a.C, network), a.P.ProjectId, retry: true);
        Assert.AreEqual(0, recovered.ExitCode, System.Text.Json.JsonSerializer.Serialize(recovered));
        Assert.AreEqual(1L, Bodies(f, "a.txt"));
    }

    [TestMethod]
    public async Task ArchivedOldVersionAndCloudGapResumeAfterFullNetworkOutage()
    {
        using var f = new Fixture(); var a = Versioned(f, true); var network = new NetworkDrive(f.Drive);
        var local = Path.Combine(a.P.Source, "a.txt"); File.WriteAllText(local, "old");
        Assert.AreEqual(0, (await Run(Runtime(a.C, network), a.P.ProjectId)).ExitCode);
        File.WriteAllText(local, "new version"); Interrupt(f, network, "a.txt");
        var failed = await Run(Runtime(a.C, network), a.P.ProjectId);
        Assert.AreEqual(RecoveryDisposition.AutomaticRetry, failed.Projects.Single().Recovery);
        Reconnect(f, network);
        var recovered = await Run(Runtime(a.C, network), a.P.ProjectId, retry: true);
        Assert.AreEqual(0, recovered.ExitCode, System.Text.Json.JsonSerializer.Serialize(recovered));
        Assert.AreEqual(2L, Bodies(f, "a.txt"));
        var root = (await f.Drive.FindAsync("drive-root", "Documents", default))!;
        var live = (await f.Drive.FindAsync(root.Id, "a.txt", default))!;
        Assert.AreEqual("new version", Encoding.UTF8.GetString(f.Drive.Items[live.Id].Bytes));
        Assert.IsTrue(f.Drive.Items.Values.Any(i => !i.Item.Folder && Encoding.UTF8.GetString(i.Bytes) == "old"));
    }

    [TestMethod]
    public async Task CompetingCloudVersionStopsWithoutOverwritingOrUploadingAnotherBody()
    {
        using var f = new Fixture(); var a = Versioned(f, true); var network = new NetworkDrive(f.Drive);
        File.WriteAllText(Path.Combine(a.P.Source, "a.txt"), "original intent"); Interrupt(f, network, "a.txt");
        await Run(Runtime(a.C, network), a.P.ProjectId); Reconnect(f, network);
        var root = (await f.Drive.FindAsync("drive-root", "Documents", default))!;
        var competing = f.Drive.Add(root.Id, "a.txt", Encoding.UTF8.GetBytes("another device"));
        var recovered = await Run(Runtime(a.C, network), a.P.ProjectId, retry: true);
        Assert.AreNotEqual(0, recovered.ExitCode);
        Assert.AreEqual(0L, Bodies(f, "a.txt"));
        Assert.AreEqual("another device", Encoding.UTF8.GetString(f.Drive.Items[competing.Id].Bytes));
        Assert.HasCount(0, CloudAutomaticRecovery.ReadCandidates(a.C));
        Assert.IsTrue(Directory.GetFiles(f.Root, "*.json", SearchOption.AllDirectories).Any(p => p.Contains("cloud-transactions")));
    }

    [TestMethod]
    public async Task FirstManualSelectionResumesOnlyChosenFileAndDoesNotActivateIncompleteMapping()
    {
        using var f = new Fixture(); var a = Versioned(f, false); var network = new NetworkDrive(f.Drive);
        File.WriteAllText(Path.Combine(a.P.Source, "a.txt"), "chosen");
        File.WriteAllText(Path.Combine(a.P.Source, "b.txt"), "not selected");
        var runtime = Runtime(a.C, network);
        var preview = await runtime.PreviewAsync(SyncDirection.Backup, a.P.ProjectId, false, default, prepareUpload: true);
        var original = preview.UploadSelection!;
        var selected = original with { Files = original.Files.Where(p => p.Path == "a.txt").ToArray() };
        Interrupt(f, network, "a.txt");
        var failed = await runtime.ExecuteAsync(SyncDirection.Backup, a.P.ProjectId, false, false, null, default, confirmedUpload: selected);
        Assert.AreEqual(RecoveryDisposition.AutomaticRetry, failed.Projects.Single().Recovery);
        File.WriteAllText(Path.Combine(a.P.Source, "c.txt"), "created after interruption"); Reconnect(f, network);
        await Run(Runtime(a.C, network), a.P.ProjectId, retry: true);
        Assert.AreEqual(1L, Bodies(f, "a.txt")); Assert.AreEqual(0L, Bodies(f, "b.txt")); Assert.AreEqual(0L, Bodies(f, "c.txt"));
        Assert.IsTrue(CloudMapping.NeedsFirstOperation(a.P, a.C));
    }

    [TestMethod]
    public async Task FirstManualTaskRemembersCompletedFilesAndContinuesUnstartedOriginalSelection()
    {
        using var f = new Fixture(); var a = Versioned(f, false); var network = new NetworkDrive(f.Drive);
        foreach (var name in new[] { "a.txt", "b.txt", "c.txt" }) File.WriteAllText(Path.Combine(a.P.Source, name), name);
        var runtime = Runtime(a.C, network);
        var preview = await runtime.PreviewAsync(SyncDirection.Backup, a.P.ProjectId, false, default, prepareUpload: true);
        Interrupt(f, network, "b.txt");
        await runtime.ExecuteAsync(SyncDirection.Backup, a.P.ProjectId, false, false, null, default, confirmedUpload: preview.UploadSelection);
        Assert.AreEqual(1L, Bodies(f, "a.txt")); Assert.AreEqual(0L, Bodies(f, "c.txt")); Reconnect(f, network);
        var recovered = await Run(Runtime(a.C, network), a.P.ProjectId, retry: true);
        Assert.AreEqual(0, recovered.ExitCode, System.Text.Json.JsonSerializer.Serialize(recovered));
        foreach (var name in new[] { "a.txt", "b.txt", "c.txt" }) Assert.AreEqual(1L, Bodies(f, name));
        Assert.IsFalse(CloudMapping.NeedsFirstOperation(a.P, a.C));
    }

    [TestMethod]
    public async Task AutomaticTaskFreezesScopeButContinuesItsOriginallyUnstartedFiles()
    {
        using var f = new Fixture(); var a = Versioned(f, true); var network = new NetworkDrive(f.Drive);
        foreach (var name in new[] { "a.txt", "b.txt" }) File.WriteAllText(Path.Combine(a.P.Source, name), name);
        Interrupt(f, network, "a.txt"); await Run(Runtime(a.C, network), a.P.ProjectId);
        File.WriteAllText(Path.Combine(a.P.Source, "new.txt"), "outside original scope"); Reconnect(f, network);
        await Run(Runtime(a.C, network), a.P.ProjectId, retry: true);
        Assert.AreEqual(1L, Bodies(f, "a.txt")); Assert.AreEqual(1L, Bodies(f, "b.txt")); Assert.AreEqual(0L, Bodies(f, "new.txt"));
    }

    [TestMethod]
    public async Task FailureBeforeFirstCloudReadIsAnOriginalAutomaticTaskAndCanRescan()
    {
        using var f = new Fixture(); var a = Versioned(f, true); var network = new NetworkDrive(f.Drive) { Offline = true };
        File.WriteAllText(Path.Combine(a.P.Source, "a.txt"), "content");
        var failed = await Run(Runtime(a.C, network), a.P.ProjectId);
        Assert.AreEqual(RecoveryDisposition.AutomaticRetry, failed.Projects.Single().Recovery);
        Assert.HasCount(1, CloudAutomaticRecovery.ReadCandidates(a.C)); network.Offline = false;
        var recovered = await Run(Runtime(a.C, network), a.P.ProjectId, retry: true);
        Assert.AreEqual(0, recovered.ExitCode); Assert.AreEqual(1L, Bodies(f, "a.txt"));
    }

    [TestMethod]
    public async Task SourceChangeBeforeAnyCloudWriteRetainsOldIntentThenUsesOrdinaryAutomaticComparison()
    {
        using var f = new Fixture(); var a = Versioned(f, true); var network = new NetworkDrive(f.Drive);
        var local = Path.Combine(a.P.Source, "a.txt"); File.WriteAllText(local, "old intent");
        Interrupt(f, network, "a.txt"); await Run(Runtime(a.C, network), a.P.ProjectId);
        File.WriteAllText(local, "new local contents"); Reconnect(f, network);
        var recovered = await Run(Runtime(a.C, network), a.P.ProjectId, retry: true);
        Assert.AreEqual(0, recovered.ExitCode, System.Text.Json.JsonSerializer.Serialize(recovered));
        Assert.AreEqual(1L, Bodies(f, "a.txt"));
        Assert.IsTrue(Directory.GetFiles(f.Root, "*.json", SearchOption.AllDirectories).Any(p => p.Contains("retained-transactions")));
    }

    [TestMethod]
    public async Task UserCancellationIsStoppedAndNotAutomaticallyResumed()
    {
        using var f = new Fixture(); var a = Versioned(f, true); var network = new NetworkDrive(f.Drive);
        File.WriteAllText(Path.Combine(a.P.Source, "a.txt"), "content"); using var stop = new CancellationTokenSource();
        f.Drive.BeforeUpload = (name, _) => { if (name == "a.txt") stop.Cancel(); stop.Token.ThrowIfCancellationRequested(); return Task.CompletedTask; };
        var report = await Runtime(a.C, network).ExecuteAsync(SyncDirection.Backup, a.P.ProjectId, false, false, null, stop.Token, automatic: true);
        Assert.IsTrue(report.Cancelled); Assert.AreEqual(RecoveryDisposition.Stopped, report.Projects.Single().Recovery);
        Assert.HasCount(0, CloudAutomaticRecovery.ReadCandidates(a.C)); f.Drive.BeforeUpload = null;
        await Run(Runtime(a.C, network), a.P.ProjectId, retry: true); Assert.AreEqual(0L, Bodies(f, "a.txt"));
    }

    [TestMethod]
    public async Task DeviceObservationRetryOnlyDrainsOutboxAndNeverStartsAnotherFile()
    {
        using var f = new Fixture(); var a = Versioned(f, true); var network = new NetworkDrive(f.Drive) { FailObservations = true };
        File.WriteAllText(Path.Combine(a.P.Source, "a.txt"), "content");
        var failed = await Run(Runtime(a.C, network), a.P.ProjectId);
        Assert.AreEqual(RecoveryDisposition.AutomaticRetry, failed.Projects.Single().Recovery);
        Assert.AreEqual(1L, Bodies(f, "a.txt"));
        File.WriteAllText(Path.Combine(a.P.Source, "later.txt"), "later"); network.FailObservations = false;
        var recovered = await Run(Runtime(a.C, network), a.P.ProjectId, retry: true);
        Assert.AreEqual(0, recovered.ExitCode); Assert.AreEqual(1L, Bodies(f, "a.txt")); Assert.AreEqual(0L, Bodies(f, "later.txt"));
        Assert.HasCount(0, CloudAutomaticRecovery.ReadCandidates(a.C));
    }

    [TestMethod]
    public async Task PhysicalMappingChangeDoesNotAdoptAnotherMappingsSuspendedTask()
    {
        using var f = new Fixture(); var a = Versioned(f, true); var network = new NetworkDrive(f.Drive);
        File.WriteAllText(Path.Combine(a.P.Source, "a.txt"), "content"); Interrupt(f, network, "a.txt");
        await Run(Runtime(a.C, network), a.P.ProjectId); Reconnect(f, network);
        var moved = a.P with { Source = Path.Combine(f.Root, "different-source"), MappingVersion = Guid.NewGuid().ToString("N") };
        Directory.CreateDirectory(moved.Source); var config = a.C with { Projects = [moved] };
        Assert.HasCount(0, CloudAutomaticRecovery.ReadCandidates(config));
        await Run(Runtime(config, network), moved.ProjectId, retry: true); Assert.AreEqual(0L, Bodies(f, "a.txt"));
    }

    [TestMethod]
    public async Task RetryRetainsOriginalAutomaticBudgetInsteadOfAllocatingAnotherAllowance()
    {
        using var f = new Fixture(); var a = Versioned(f, true); var network = new NetworkDrive(f.Drive);
        var config = a.C with { AutomaticBudget = new(MaxBytes: 1) };
        File.WriteAllText(Path.Combine(a.P.Source, "a.txt"), "first oversized item");
        File.WriteAllText(Path.Combine(a.P.Source, "b.txt"), "second item");
        Interrupt(f, network, "a.txt"); await Run(Runtime(config, network), a.P.ProjectId); Reconnect(f, network);
        var recovered = await Run(Runtime(config, network), a.P.ProjectId, retry: true);
        Assert.AreEqual(1L, Bodies(f, "a.txt")); Assert.AreEqual(0L, Bodies(f, "b.txt"));
        Assert.AreEqual("AutomaticTransferBudgetReached", recovered.BudgetStopReason);
        Assert.HasCount(0, CloudAutomaticRecovery.ReadCandidates(config));
    }

    [TestMethod]
    public async Task OwnSuccessfulRollbackReceiptAllowsOriginalUploadToContinue()
    {
        using var f = new Fixture(); var a = Versioned(f, true); var network = new NetworkDrive(f.Drive);
        var local = Path.Combine(a.P.Source, "a.txt"); File.WriteAllText(local, "old cloud version");
        await Run(Runtime(a.C, network), a.P.ProjectId); File.WriteAllText(local, "new chosen version");
        var runtime = Runtime(a.C, network);
        var preview = await runtime.PreviewAsync(SyncDirection.Backup, a.P.ProjectId, false, default, prepareUpload: true);
        // Only the upload fails: rollback can reconnect independently and creates a new item ID.
        f.Drive.BeforeUpload = (name, _) => name == "a.txt" ? throw new HttpRequestException("Synthetic upload failure.") : Task.CompletedTask;
        var failed = await runtime.ExecuteAsync(SyncDirection.Backup, a.P.ProjectId, false, false, null, default, confirmedUpload: preview.UploadSelection);
        Assert.AreEqual(RecoveryDisposition.AutomaticRetry, failed.Projects.Single().Recovery);
        f.Drive.BeforeUpload = null;
        var recovered = await Run(Runtime(a.C, network), a.P.ProjectId, retry: true);
        Assert.AreEqual(0, recovered.ExitCode, System.Text.Json.JsonSerializer.Serialize(recovered));
        Assert.AreEqual(2L, Bodies(f, "a.txt"));
        var root = (await f.Drive.FindAsync("drive-root", "Documents", default))!;
        var live = (await f.Drive.FindAsync(root.Id, "a.txt", default))!;
        Assert.AreEqual("new chosen version", Encoding.UTF8.GetString(f.Drive.Items[live.Id].Bytes));
    }

    [TestMethod]
    public async Task LostMetadataReceiptCompletesOriginalRevisionWithoutDuplicateBodyOrMetadata()
    {
        using var f = new Fixture(); var a = Versioned(f, true); var network = new NetworkDrive(f.Drive);
        File.WriteAllText(Path.Combine(a.P.Source, "a.txt"), "body already accepted"); string? metadataId = null;
        f.Drive.AfterUpload = item =>
        {
            if (item.Name.EndsWith(".json", StringComparison.Ordinal) && f.Drive.Items[item.ParentId!].Item.Name == "files")
            { metadataId = item.Id; network.Offline = true; throw new HttpRequestException("Synthetic metadata response lost."); }
            return Task.CompletedTask;
        };
        await Run(Runtime(a.C, network), a.P.ProjectId); Assert.IsNotNull(metadataId); Reconnect(f, network);
        var recovered = await Run(Runtime(a.C, network), a.P.ProjectId, retry: true);
        Assert.AreEqual(0, recovered.ExitCode, System.Text.Json.JsonSerializer.Serialize(recovered));
        Assert.AreEqual(1L, Bodies(f, "a.txt"));
        Assert.AreEqual(1, f.Drive.BodyTransfers.Count(t => t.Direction == "Upload" && t.ItemId == metadataId));
        var parent = f.Drive.Items[metadataId].Parent;
        Assert.AreEqual(1, f.Drive.Items.Values.Count(v => v.Parent == parent && !v.Item.Folder));
    }

    [TestMethod]
    public async Task PersistedServiceCooldownPreventsQuickAndOrdinaryAutomaticCloudRequests()
    {
        using var f = new Fixture(); var a = Versioned(f, true); var network = new NetworkDrive(f.Drive);
        File.WriteAllText(Path.Combine(a.P.Source, "a.txt"), "content");
        var due = DateTimeOffset.UtcNow.AddMinutes(10);
        f.Drive.BeforeUpload = (name, _) => name == "a.txt" ? throw new CloudCooldownException(due) : Task.CompletedTask;
        var failed = await Run(Runtime(a.C, network), a.P.ProjectId);
        Assert.AreEqual(due, failed.Projects.Single().RetryNotBeforeUtc);
        Assert.AreEqual(due, CloudAutomaticRecovery.ReadCandidates(a.C).Single().RetryNotBeforeUtc);
        f.Drive.BeforeUpload = null; var reads = network.Requests;
        await Run(Runtime(a.C, network), a.P.ProjectId, retry: true);
        await Run(Runtime(a.C, network), a.P.ProjectId);
        Assert.AreEqual(reads, network.Requests); Assert.AreEqual(0L, Bodies(f, "a.txt"));
    }

    [TestMethod]
    public async Task LegacyPendingJournalWithoutDurableNetworkAuthorizationRemainsManual()
    {
        using var f = new Fixture(); var a = Versioned(f, true); var network = new NetworkDrive(f.Drive);
        File.WriteAllText(Path.Combine(a.P.Source, "a.txt"), "legacy original intent");
        f.Drive.BeforeUpload = (name, _) => name == "a.txt" ? throw new HttpRequestException("Synthetic legacy interruption.") : Task.CompletedTask;
        await Assert.ThrowsExactlyAsync<HttpRequestException>(() => Apply(new(a.P, a.C, f.Account, f.Drive)));
        f.Drive.BeforeUpload = null;
        await Run(Runtime(a.C, network), a.P.ProjectId, retry: true);
        await Run(Runtime(a.C, network), a.P.ProjectId);
        Assert.AreEqual(0L, Bodies(f, "a.txt")); Assert.HasCount(0, CloudAutomaticRecovery.ReadCandidates(a.C));
    }

    [TestMethod]
    public async Task OrdinaryCycleCompletesFrozenTaskThenNextCycleIncludesNewFiles()
    {
        using var f = new Fixture(); var a = Versioned(f, true); var network = new NetworkDrive(f.Drive);
        File.WriteAllText(Path.Combine(a.P.Source, "a.txt"), "original scope");
        Interrupt(f, network, "a.txt"); await Run(Runtime(a.C, network), a.P.ProjectId);
        File.WriteAllText(Path.Combine(a.P.Source, "later.txt"), "later file"); Reconnect(f, network);
        var resumed = await Run(Runtime(a.C, network), a.P.ProjectId);
        Assert.AreEqual(0, resumed.ExitCode); Assert.AreEqual(RecoveryDisposition.None, resumed.Projects.Single().Recovery);
        Assert.AreEqual(1L, Bodies(f, "a.txt")); Assert.AreEqual(0L, Bodies(f, "later.txt"));
        var next = await Run(Runtime(a.C, network), a.P.ProjectId);
        Assert.AreEqual(0, next.ExitCode); Assert.AreEqual(1L, Bodies(f, "a.txt")); Assert.AreEqual(1L, Bodies(f, "later.txt"));
    }

    [TestMethod]
    public async Task LaterProjectsQuickRetryInheritsBytesAlreadyReservedByEarlierProject()
    {
        using var f = new Fixture(); var a = Versioned(f, true); var network = new NetworkDrive(f.Drive);
        var b = a.P with { Source = Path.Combine(f.Root, "B-project-source"), Destination = "OtherDocuments",
            Target = "OneDrive / OtherDocuments", TaskId = DeviceIdentity.Digest("second project"), MappingVersion = Guid.NewGuid().ToString("N") };
        Directory.CreateDirectory(b.Source);
        var config = a.C with { Projects = [a.P, b], AutomaticBudget = new(MaxBytes: 10) };
        CloudMapping.MarkReady(b, config);
        File.WriteAllText(Path.Combine(a.P.Source, "a.txt"), "123456");
        File.WriteAllText(Path.Combine(b.Source, "b1.txt"), "123456");
        File.WriteAllText(Path.Combine(b.Source, "b2.txt"), "x");
        Interrupt(f, network, "b1.txt");
        await Runtime(config, network).ExecuteAsync(SyncDirection.Backup, null, false, false, null, default, automatic: true);
        Assert.AreEqual(1L, Bodies(f, "a.txt")); Reconnect(f, network);
        var recovered = await Run(Runtime(config, network), b.ProjectId, retry: true);
        Assert.AreEqual(1L, Bodies(f, "b1.txt")); Assert.AreEqual(0L, Bodies(f, "b2.txt"));
        Assert.AreEqual("AutomaticTransferBudgetReached", recovered.BudgetStopReason);
    }

    [TestMethod]
    public async Task UnrelatedLegacyPendingFileDoesNotBlockAuthorizedNetworkRecovery()
    {
        using var f = new Fixture(); var a = Versioned(f, true); var network = new NetworkDrive(f.Drive);
        File.WriteAllText(Path.Combine(a.P.Source, "b.txt"), "manual recovery evidence");
        f.Drive.BeforeUpload = (name, _) => name == "b.txt" ? throw new IOException("Synthetic manual-only interruption.") : Task.CompletedTask;
        await Assert.ThrowsExactlyAsync<IOException>(() => Apply(new(a.P, a.C, f.Account, f.Drive)));
        var legacy = Directory.GetFiles(CloudMapping.StateRoot(a.P, a.C), "*.json", SearchOption.AllDirectories)
            .Single(p => Path.GetFileName(Path.GetDirectoryName(p)) == "cloud-transactions");
        var original = File.ReadAllBytes(legacy);
        File.WriteAllText(Path.Combine(a.P.Source, "a.txt"), "safe original upload"); Interrupt(f, network, "a.txt");
        var failed = await Run(Runtime(a.C, network), a.P.ProjectId);
        Assert.AreEqual(RecoveryDisposition.AwaitingConfirmation, failed.Projects.Single().Recovery);
        Assert.HasCount(1, CloudAutomaticRecovery.ReadCandidates(a.C)); Reconnect(f, network);
        var recovered = await Run(Runtime(a.C, network), a.P.ProjectId, retry: true);
        Assert.AreEqual(1L, Bodies(f, "a.txt")); Assert.AreEqual(0L, Bodies(f, "b.txt"));
        Assert.AreEqual(RecoveryDisposition.AwaitingConfirmation, recovered.Projects.Single().Recovery);
        CollectionAssert.AreEqual(original, File.ReadAllBytes(legacy));
        Assert.HasCount(0, CloudAutomaticRecovery.ReadCandidates(a.C));
    }

    [TestMethod]
    public async Task ManualRepairInOneInFlightFileDoesNotResumeItOrBlockAnotherNetworkFile()
    {
        using var f = new Fixture(); var a = Versioned(f, true); var network = new NetworkDrive(f.Drive);
        File.WriteAllText(Path.Combine(a.P.Source, "a.txt"), "network intent");
        File.WriteAllText(Path.Combine(a.P.Source, "b.txt"), "manual repair intent");
        var bStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        f.Drive.BeforeUpload = async (name, _) =>
        {
            if (name == "a.txt") { await bStarted.Task; network.Offline = true; throw new HttpRequestException("Synthetic network interruption."); }
            if (name == "b.txt") { bStarted.TrySetResult(); throw new IOException("Synthetic state/commit failure requiring review."); }
        };
        var failed = await new CloudBackupRuntime(a.C, () => network, fileConcurrency: 2)
            .ExecuteAsync(SyncDirection.Backup, a.P.ProjectId, false, false, null, default, automatic: true);
        Assert.AreEqual(RecoveryDisposition.RepairRequired, failed.Projects.Single().Recovery);
        Assert.HasCount(1, CloudAutomaticRecovery.ReadCandidates(a.C));
        var journal = Directory.GetFiles(CloudMapping.StateRoot(a.P, a.C), "*.json", SearchOption.AllDirectories)
            .Where(p => Path.GetFileName(Path.GetDirectoryName(p)) == "cloud-transactions")
            .Single(p => JsonNode.Parse(File.ReadAllText(p))!["path"]!.GetValue<string>() == "b.txt");
        var original = File.ReadAllBytes(journal); Reconnect(f, network);
        var recovered = await Run(Runtime(a.C, network), a.P.ProjectId, retry: true);
        Assert.AreEqual(1L, Bodies(f, "a.txt")); Assert.AreEqual(0L, Bodies(f, "b.txt"));
        Assert.AreEqual(RecoveryDisposition.RepairRequired, recovered.Projects.Single().Recovery);
        CollectionAssert.AreEqual(original, File.ReadAllBytes(journal));
        Assert.HasCount(0, CloudAutomaticRecovery.ReadCandidates(a.C));
    }

    [TestMethod]
    public async Task UnknownOverlappingJournalBlocksOnlyThatAutomaticRecoveryIntent()
    {
        using var f = new Fixture(); var a = Versioned(f, true); var network = new NetworkDrive(f.Drive);
        File.WriteAllText(Path.Combine(a.P.Source, "a.txt"), "original intent");
        Interrupt(f, network, "a.txt"); await Run(Runtime(a.C, network), a.P.ProjectId); Reconnect(f, network);
        var root = Path.Combine(CloudMapping.StateRoot(a.P, a.C), "cloud-transactions");
        var original = Directory.GetFiles(root, "*.json").Single();
        var node = JsonNode.Parse(File.ReadAllText(original))!; var id = Guid.NewGuid().ToString("N"); node["id"] = id;
        var overlapping = Path.Combine(root, id + ".json"); File.WriteAllText(overlapping, node.ToJsonString());
        var first = File.ReadAllBytes(original); var second = File.ReadAllBytes(overlapping);
        var recovered = await Run(Runtime(a.C, network), a.P.ProjectId, retry: true);
        Assert.AreEqual(RecoveryDisposition.AwaitingConfirmation, recovered.Projects.Single().Recovery);
        Assert.AreEqual(0L, Bodies(f, "a.txt"));
        CollectionAssert.AreEqual(first, File.ReadAllBytes(original)); CollectionAssert.AreEqual(second, File.ReadAllBytes(overlapping));
        Assert.HasCount(0, CloudAutomaticRecovery.ReadCandidates(a.C));
    }

    [TestMethod]
    public async Task IndependentQueuedSelectionsDoNotTurnEachOthersPendingFilesIntoManualProblems()
    {
        using var f = new Fixture(); var a = Versioned(f, true); var network = new NetworkDrive(f.Drive);
        var runtime = Runtime(a.C, network);
        var shared = Path.Combine(a.P.Source, "Shared"); Directory.CreateDirectory(shared);
        File.WriteAllText(Path.Combine(shared, "a.txt"), "first authorization");
        var first = (await runtime.PreviewAsync(SyncDirection.Backup, a.P.ProjectId, false, default, prepareUpload: true)).UploadSelection!;
        Interrupt(f, network, "a.txt");
        await runtime.ExecuteAsync(SyncDirection.Backup, a.P.ProjectId, false, false, null, default, confirmedUpload: first);
        Assert.IsTrue(CloudResumeTaskStore.ReadActive(a.P, a.C)!.Value.Completed.Any(c => c.Path == "Shared" && c.Directory));
        Reconnect(f, network); File.WriteAllText(Path.Combine(shared, "b.txt"), "independent authorization");
        var preview = (await runtime.PreviewAsync(SyncDirection.Backup, a.P.ProjectId, false, default, prepareUpload: true)).UploadSelection!;
        var second = preview with { Files = preview.Files.Where(p => p.Path == "Shared/b.txt").ToArray() };
        Interrupt(f, network, "b.txt");
        var secondFailure = await runtime.ExecuteAsync(SyncDirection.Backup, a.P.ProjectId, false, false, null, default, confirmedUpload: second);
        Assert.AreEqual(RecoveryDisposition.AutomaticRetry, secondFailure.Projects.Single().Recovery);
        Reconnect(f, network);
        var resumedFirst = await Run(Runtime(a.C, network), a.P.ProjectId, retry: true);
        Assert.AreEqual(RecoveryDisposition.None, resumedFirst.Projects.Single().Recovery);
        Assert.IsFalse(resumedFirst.Projects.Single().Issues.Any(i => i.Code == "PreviousMappingPending"));
        Assert.AreEqual(1L, Bodies(f, "a.txt")); Assert.AreEqual(0L, Bodies(f, "b.txt"));
        Assert.HasCount(1, CloudAutomaticRecovery.ReadCandidates(a.C));
        var resumedSecond = await Run(Runtime(a.C, network), a.P.ProjectId, retry: true);
        Assert.AreEqual(RecoveryDisposition.None, resumedSecond.Projects.Single().Recovery);
        Assert.AreEqual(1L, Bodies(f, "a.txt")); Assert.AreEqual(1L, Bodies(f, "b.txt"));
        Assert.HasCount(0, CloudAutomaticRecovery.ReadCandidates(a.C));
    }

    [TestMethod]
    public async Task PolicyChangeOfQueuedAutomaticTaskFinalizesOnceAndKeepsOriginalJournal()
    {
        using var f = new Fixture(); var a = Versioned(f, true); var network = new NetworkDrive(f.Drive);
        var runtime = Runtime(a.C, network);
        File.WriteAllText(Path.Combine(a.P.Source, "a.txt"), "automatic intent");
        Interrupt(f, network, "a.txt"); await Run(runtime, a.P.ProjectId); Reconnect(f, network);
        var journal = Directory.GetFiles(Path.Combine(CloudMapping.StateRoot(a.P, a.C), "cloud-transactions"), "*.json").Single();
        var original = File.ReadAllBytes(journal);
        File.WriteAllText(Path.Combine(a.P.Source, "b.txt"), "manual selection");
        var preview = (await runtime.PreviewAsync(SyncDirection.Backup, a.P.ProjectId, false, default, prepareUpload: true)).UploadSelection!;
        var selection = preview with { Files = preview.Files.Where(p => p.Path == "b.txt").ToArray() };
        var uploaded = await runtime.ExecuteAsync(SyncDirection.Backup, a.P.ProjectId, false, false, null, default, confirmedUpload: selection);
        Assert.AreEqual(RecoveryDisposition.None, uploaded.Projects.Single().Recovery);
        Assert.HasCount(1, CloudAutomaticRecovery.ReadCandidates(a.C));
        var changed = a.C with { AutomaticBackupModes = new Dictionary<string, AutomaticBackupMode> { [a.P.ProjectId] = AutomaticBackupMode.Bidirectional } };
        var report = await Run(Runtime(changed, network), a.P.ProjectId, retry: true);
        Assert.AreEqual(RecoveryDisposition.AwaitingConfirmation, report.Projects.Single().Recovery);
        Assert.AreEqual(0L, Bodies(f, "a.txt")); Assert.AreEqual(1L, Bodies(f, "b.txt"));
        CollectionAssert.AreEqual(original, File.ReadAllBytes(journal));
        Assert.HasCount(0, CloudAutomaticRecovery.ReadCandidates(changed));
    }

    [TestMethod]
    public async Task RecoveredAuthorizationFailureRemainsRepairRequiredInLaterMixedRetries()
    {
        using var f = new Fixture(); var a = Versioned(f, true); var network = new NetworkDrive(f.Drive);
        foreach (var name in new[] { "a.txt", "b.txt" }) File.WriteAllText(Path.Combine(a.P.Source, name), name);
        var bothStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously); var started = 0;
        f.Drive.BeforeUpload = async (name, _) =>
        {
            if (name is not ("a.txt" or "b.txt")) return;
            if (Interlocked.Increment(ref started) == 2) bothStarted.TrySetResult();
            await bothStarted.Task; throw new HttpRequestException("Synthetic interrupted upload.");
        };
        await new CloudBackupRuntime(a.C, () => network, fileConcurrency: 2)
            .ExecuteAsync(SyncDirection.Backup, a.P.ProjectId, false, false, null, default, automatic: true);
        f.Drive.BeforeUpload = (name, _) => name switch
        {
            "a.txt" => throw new AccountAuthorizationRequiredException("Synthetic sign-in repair required."),
            "b.txt" => throw new HttpRequestException("Synthetic remaining network failure."),
            _ => Task.CompletedTask
        };
        var failed = await Run(Runtime(a.C, network), a.P.ProjectId, retry: true);
        Assert.AreEqual(RecoveryDisposition.RepairRequired, failed.Projects.Single().Recovery);
        Assert.AreEqual(RecoveryDisposition.RepairRequired, CloudResumeTaskStore.ReadActive(a.P, a.C)!.GetRecovery("a.txt"));
        Assert.HasCount(1, CloudAutomaticRecovery.ReadCandidates(a.C)); f.Drive.BeforeUpload = null;
        var retried = await Run(Runtime(a.C, network), a.P.ProjectId, retry: true);
        Assert.AreEqual(RecoveryDisposition.RepairRequired, retried.Projects.Single().Recovery);
        Assert.AreEqual(RecoveryDisposition.RepairRequired, retried.Projects.Single().Files.Single(p => p.Path == "a.txt").Recovery);
        Assert.AreEqual(0L, Bodies(f, "a.txt")); Assert.AreEqual(1L, Bodies(f, "b.txt"));
        Assert.HasCount(0, CloudAutomaticRecovery.ReadCandidates(a.C));
    }

    [TestMethod]
    public async Task SuccessfulWholeProjectManualRetrySupersedesOldNetworkAuthorization()
    {
        using var f = new Fixture(); var a = Versioned(f, true); var network = new NetworkDrive(f.Drive);
        File.WriteAllText(Path.Combine(a.P.Source, "a.txt"), "original intent"); Interrupt(f, network, "a.txt");
        await Run(Runtime(a.C, network), a.P.ProjectId); var oldId = CloudResumeTaskStore.ReadActive(a.P, a.C)!.Value.Id;
        Reconnect(f, network);
        var manual = await Runtime(a.C, network).ExecuteAsync(SyncDirection.Backup, a.P.ProjectId, false, false, null, default);
        Assert.AreEqual(0, manual.ExitCode); Assert.AreEqual(1L, Bodies(f, "a.txt"));
        Assert.HasCount(0, CloudAutomaticRecovery.ReadCandidates(a.C)); Assert.IsNull(CloudResumeTaskStore.ReadActive(a.P, a.C));
        var retained = Path.Combine(CloudMapping.StateRoot(a.P, a.C), "retained-tasks");
        Assert.IsTrue(Directory.GetFiles(retained, "*.json").Any(p => JsonNode.Parse(File.ReadAllText(p))!["id"]!.GetValue<string>() == oldId));
        var quick = await Run(Runtime(a.C, network), a.P.ProjectId, retry: true);
        Assert.AreEqual(0, quick.ExitCode); Assert.AreEqual(1L, Bodies(f, "a.txt"));
    }

    [TestMethod]
    public async Task ManualRetryFailureBeforeRecoveryEffectsKeepsOriginalNetworkAuthorization()
    {
        using var f = new Fixture(); var a = Versioned(f, true); var network = new NetworkDrive(f.Drive);
        File.WriteAllText(Path.Combine(a.P.Source, "a.txt"), "original intent"); Interrupt(f, network, "a.txt");
        await Run(Runtime(a.C, network), a.P.ProjectId); var oldId = CloudResumeTaskStore.ReadActive(a.P, a.C)!.Value.Id;
        var manual = await Runtime(a.C, network).ExecuteAsync(SyncDirection.Backup, a.P.ProjectId, false, false, null, default);
        Assert.AreNotEqual(0, manual.ExitCode);
        Assert.AreEqual(oldId, CloudResumeTaskStore.ReadActive(a.P, a.C)!.Value.Id);
        Assert.HasCount(1, CloudAutomaticRecovery.ReadCandidates(a.C)); Reconnect(f, network);
        var recovered = await Run(Runtime(a.C, network), a.P.ProjectId, retry: true);
        Assert.AreEqual(0, recovered.ExitCode); Assert.AreEqual(1L, Bodies(f, "a.txt"));
    }

    [TestMethod]
    public async Task FirstPullWithoutSharedHashesRemembersActualDownloadedContentsAcrossRetry()
    {
        using var f = new Fixture(); var a = Versioned(f, false); var network = new NetworkDrive(f.Drive);
        var cloud = await f.Drive.FolderAsync("drive-root", "Documents", default);
        foreach (var name in new[] { "a.txt", "b.txt", "c.txt" }) f.Drive.Add(cloud.Id, name, Encoding.UTF8.GetBytes("external " + name));
        var runtime = Runtime(a.C, network);
        var preview = await runtime.PreviewAsync(SyncDirection.Pull, a.P.ProjectId, false, default);
        f.Drive.BeforeDownload = (name, _) =>
        { if (name == "b.txt") { network.Offline = true; throw new HttpRequestException("Synthetic pull interruption."); } return Task.CompletedTask; };
        await runtime.ExecuteAsync(SyncDirection.Pull, a.P.ProjectId, false, false, null, default, confirmedPull: preview.PullSelection);
        Assert.AreEqual(1, f.Drive.BodyTransfers.Count(t => t.Direction == "Download" && t.Name == "a.txt"));
        Reconnect(f, network); f.Drive.BeforeDownload = null;
        var recovered = await Run(Runtime(a.C, network), a.P.ProjectId, retry: true);
        Assert.AreEqual(0, recovered.ExitCode, System.Text.Json.JsonSerializer.Serialize(recovered));
        Assert.IsFalse(CloudMapping.NeedsFirstOperation(a.P, a.C));
        foreach (var name in new[] { "a.txt", "b.txt", "c.txt" })
        {
            Assert.AreEqual("external " + name, File.ReadAllText(Path.Combine(a.P.Source, name)));
            Assert.AreEqual(1, f.Drive.BodyTransfers.Count(t => t.Direction == "Download" && t.Name == name));
        }
    }

    [TestMethod]
    public async Task ChangedSourceNewIntentPaysItsFullReservationAcrossRestart()
    {
        using var f = new Fixture(); var a = Versioned(f, true); var network = new NetworkDrive(f.Drive);
        var config = a.C with { AutomaticBudget = new(MaxBytes: 10) };
        var local = Path.Combine(a.P.Source, "a.txt"); File.WriteAllText(local, "x"); File.WriteAllText(Path.Combine(a.P.Source, "b.txt"), "y");
        Interrupt(f, network, "a.txt"); await Run(Runtime(config, network), a.P.ProjectId);
        File.WriteAllText(local, new string('n', 100)); Reconnect(f, network); Interrupt(f, network, "a.txt");
        await Run(Runtime(config, network), a.P.ProjectId, retry: true);
        Assert.AreEqual(101L, CloudResumeTaskStore.ReadActive(a.P, config)!.Value.ReservedBytes);
        Assert.AreEqual(0L, Bodies(f, "a.txt")); Reconnect(f, network);
        var recovered = await Run(Runtime(config, network), a.P.ProjectId, retry: true);
        Assert.AreEqual(1L, Bodies(f, "a.txt")); Assert.AreEqual(0L, Bodies(f, "b.txt"));
        Assert.AreEqual("AutomaticTransferBudgetReached", recovered.BudgetStopReason);
        Assert.AreEqual(101L, CloudResumeTaskStore.ReadActive(a.P, config)!.Value.ReservedBytes);
    }

    [TestMethod]
    public async Task ClosedChangedSourceIntentDoesNotBecomeFalseCompletionOnNextBudgetCycle()
    {
        using var f = new Fixture(); var a = Versioned(f, true); var network = new NetworkDrive(f.Drive);
        var config = a.C with { AutomaticBudget = new(MaxBytes: 1) };
        var local = Path.Combine(a.P.Source, "a.txt"); File.WriteAllText(local, "x");
        Interrupt(f, network, "a.txt"); await Run(Runtime(config, network), a.P.ProjectId);
        File.WriteAllText(local, new string('n', 100)); Reconnect(f, network);
        var quick = await Run(Runtime(config, network), a.P.ProjectId, retry: true);
        Assert.AreEqual("AutomaticTransferBudgetReached", quick.BudgetStopReason); Assert.AreEqual(0L, Bodies(f, "a.txt"));
        var next = await Run(Runtime(config, network), a.P.ProjectId);
        Assert.AreEqual(0, next.ExitCode, System.Text.Json.JsonSerializer.Serialize(next));
        Assert.AreEqual(RecoveryDisposition.None, next.Projects.Single().Recovery); Assert.AreEqual(1L, Bodies(f, "a.txt"));
    }

    private sealed class NetworkDrive(FakeDrive inner) : ICloudDrive
    {
        public bool Offline;
        public bool FailObservations;
        public int Requests;
        private void Check() { Interlocked.Increment(ref Requests); if (Offline) throw new HttpRequestException("Synthetic OneDrive unreachable."); }
        private bool Observation(string parent)
        {
            while (inner.Items.TryGetValue(parent, out var node)) { if (node.Item.Name == "devices") return true; parent = node.Parent; }
            return false;
        }
        public void Dispose() { }
        public Task<RemoteItem> RootAsync(CancellationToken token) { Check(); return inner.RootAsync(token); }
        public Task<RemoteItem?> FindAsync(string parent, string name, CancellationToken token) { Check(); return inner.FindAsync(parent, name, token); }
        public Task<RemoteItem> GetAsync(string id, CancellationToken token) { Check(); return inner.GetAsync(id, token); }
        public Task<IReadOnlyList<RemoteItem>> ChildrenAsync(string parent, CancellationToken token) { Check(); return inner.ChildrenAsync(parent, token); }
        public Task<RemoteItem> FolderAsync(string parent, string name, CancellationToken token) { Check(); return inner.FolderAsync(parent, name, token); }
        public Task<RemoteItem> UploadAsync(string parent, string name, Stream source, TransferSession? session, Action<TransferSession?> checkpoint, CancellationToken token, bool reconcileExisting = true)
        { Check(); if (FailObservations && Observation(parent)) throw new HttpRequestException("Synthetic observation upload interruption."); return inner.UploadAsync(parent, name, source, session, checkpoint, token, reconcileExisting); }
        public Task DownloadAsync(RemoteItem item, Stream destination, CancellationToken token) { Check(); return inner.DownloadAsync(item, destination, token); }
        public Task DeleteAsync(RemoteItem item, CancellationToken token) { Check(); return inner.DeleteAsync(item, token); }
        public Task<RemoteItem> MoveAsync(RemoteItem source, string parent, string name, DateTimeOffset? modifiedUtc, CancellationToken token) { Check(); return inner.MoveAsync(source, parent, name, modifiedUtc, token); }
        public Task<RemoteItem> SetModifiedAsync(RemoteItem item, DateTimeOffset modified, CancellationToken token) { Check(); return inner.SetModifiedAsync(item, modified, token); }
        public Task<RemoteItem> CopyAsync(RemoteItem source, string parent, string name, CancellationToken token) { Check(); return inner.CopyAsync(source, parent, name, token); }
    }
}
