using HarborBackup.Core.Domain;
using HarborBackup.OneDrive.Authentication;
using HarborBackup.Windows.Integration;
using HarborBackup.Windows.Persistence;
using static HarborBackup.Windows.Tests.CloudTransportTests;

namespace HarborBackup.Windows.Tests;

[TestClass]
[DoNotParallelize]
public sealed class RunPolicyTests
{
    private sealed class Clock : TimeProvider
    {
        public long Ticks;
        public override long GetTimestamp() => Ticks;
        public override long TimestampFrequency => TimeSpan.TicksPerSecond;
    }
    [TestMethod]
    [DataRow(true, FileFailureKind.FileUnavailable)]
    [DataRow(false, FileFailureKind.RecoveryRequired)]
    public void IdenticalPermissionErrorsHaveDifferentScopeBeforeAndAfterWrites(bool preparing, FileFailureKind expected)
    {
        Assert.AreEqual(expected, FileFailurePolicy.Classify(new UnauthorizedAccessException("Denied"), preparing));
        Assert.AreEqual(FileFailureKind.RecoveryRequired, FileFailurePolicy.Classify(new HarborBackup.Windows.FileSystem.UnsafePathException("Link"), preparing));
    }
    [TestMethod]
    public async Task LockedSourceOnlyDefersItsFileAndOtherFilesContinue()
    {
        using var f = new Fixture(); var a = f.Device("A");
        var path = Path.Combine(a.P.Source, "a-locked.txt"); File.WriteAllText(path, "locked");
        File.WriteAllText(Path.Combine(a.P.Source, "b-ready.txt"), "ready");
        using (File.Open(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
        {
            var report = await new CloudBackupRuntime(a.C, () => f.Drive, 1).ExecuteAsync(SyncDirection.Backup, null, false, false, null, default);
            Assert.AreEqual(2, report.ExitCode);
            Assert.AreEqual(FileFailureKind.FileUnavailable, report.Projects.Single().Files.Single(p => p.Path == "a-locked.txt").FailureKind);
            Assert.IsTrue(report.Projects.Single().Files.Single(p => p.Path == "b-ready.txt").Changed);
            Assert.IsFalse(a.S.Pending);
        }
    }
    [TestMethod]
    public async Task AuthorizationFailureStopsDispatchAndIsClassified()
    {
        using var f = new Fixture(); var a = f.Device("A");
        foreach (var name in new[] { "a.txt", "b.txt" }) File.WriteAllText(Path.Combine(a.P.Source, name), "body");
        f.Drive.BeforeUpload = (name, _) => name == "a.txt" ? throw new AccountAuthorizationRequiredException("Connect again") : Task.CompletedTask;
        var report = await new CloudBackupRuntime(a.C, () => f.Drive, 1).ExecuteAsync(SyncDirection.Backup, null, false, false, null, default);
        Assert.AreEqual(FileFailureKind.Authorization, report.Projects.Single().Files.Single().FailureKind);
        Assert.AreEqual(1, report.ExitCode); Assert.AreEqual("Failed", report.Completion);
        Assert.IsFalse(f.Drive.UploadIntents.Any(p => p.Name == "b.txt"));
        Assert.IsTrue(a.S.Pending);
    }
    [TestMethod]
    [DataRow(true)]
    [DataRow(false)]
    public async Task SizeBudgetOnlyLimitsAutomaticBackupAndDoesNotLeaveTransactions(bool automatic)
    {
        using var f = new Fixture(); var a = f.Device("A");
        foreach (var name in new[] { "a.txt", "b.txt", "c.txt" }) File.WriteAllText(Path.Combine(a.P.Source, name), "1234");
        var c = a.C with { AutomaticBudget = new(1) };
        var report = await new CloudBackupRuntime(c, () => f.Drive, 1).ExecuteAsync(SyncDirection.Backup, null, false, false, null, default, automatic: automatic);
        Assert.AreEqual(automatic ? "BudgetLimited" : "Completed", report.Completion);
        Assert.AreEqual(automatic ? 1 : 3, report.Projects.Single().Files.Count(p => p.Changed));
        Assert.IsFalse(report.Cancelled); Assert.IsFalse(a.S.Pending);
        if (automatic)
        {
            Assert.AreEqual("AutomaticTransferBudgetReached", report.BudgetStopReason);
            Assert.AreEqual(2, report.ExitCode);
            Assert.AreEqual("BudgetLimited", HarborBackup.Windows.Persistence.RunStatusStore.Read(c.Preview)!["projects"]![0]!["lastResult"]!.GetValue<string>());
            var next = await new CloudBackupRuntime(c, () => f.Drive, 1).ExecuteAsync(SyncDirection.Backup, null, false, false, null, default, automatic: true);
            Assert.IsTrue(next.Projects.Single().Files.Any(p => p.Path == "b.txt" && p.Changed));
            Assert.IsFalse(next.Projects.Single().Files.Any(p => p.Path == "a.txt" && p.Changed));
        }
    }
    [TestMethod]
    public async Task BudgetCutoffDoesNotHideAnUnavailableFile()
    {
        using var f = new Fixture(); var a = f.Device("A");
        var locked = Path.Combine(a.P.Source, "a.txt"); File.WriteAllText(locked, "locked");
        foreach (var name in new[] { "b.txt", "c.txt" }) File.WriteAllText(Path.Combine(a.P.Source, name), "body");
        using (File.Open(locked, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
        {
            var report = await new CloudBackupRuntime(a.C with { AutomaticBudget = new(1) }, () => f.Drive, 1)
                .ExecuteAsync(SyncDirection.Backup, null, false, false, null, default, automatic: true);
            Assert.IsNotNull(report.BudgetStopReason); Assert.AreEqual("NeedsAttention", report.Completion);
            Assert.AreEqual("NeedsAttention", report.CloudUploadStatus);
            Assert.IsTrue(report.Projects.Single().Files.Any(p => p.FailureKind == FileFailureKind.FileUnavailable));
        }
    }
    [TestMethod]
    public async Task TimeBudgetLetsStartedFileFinishAndDefersTheNext()
    {
        using var f = new Fixture(); var a = f.Device("A"); var clock = new Clock();
        foreach (var name in new[] { "a.txt", "b.txt" }) File.WriteAllText(Path.Combine(a.P.Source, name), "body");
        f.Drive.BeforeUpload = (name, _) => { if (name == "a.txt") clock.Ticks = TimeSpan.FromMinutes(2).Ticks; return Task.CompletedTask; };
        var c = a.C with { AutomaticBudget = new(0, 1) };
        var report = await new CloudBackupRuntime(c, () => f.Drive, 1, clock).ExecuteAsync(SyncDirection.Backup, null, false, false, null, default, automatic: true);
        Assert.AreEqual("AutomaticTimeBudgetReached", report.BudgetStopReason); Assert.AreEqual("BudgetLimited", report.Completion);
        Assert.IsTrue(report.Projects.Single().Files.Single(p => p.Path == "a.txt").Changed);
        Assert.IsFalse(f.Drive.UploadIntents.Any(p => p.Name == "b.txt")); Assert.IsFalse(a.S.Pending);
    }
    [TestMethod]
    public async Task VerificationSeparatesMissingDifferentAndUnknownWithoutBusinessDownloads()
    {
        using var f = new Fixture(); var a = f.Device("A");
        foreach (var name in new[] { "same.txt", "different.txt", "cloud-only.txt" }) File.WriteAllText(Path.Combine(a.P.Source, name), "base");
        await Apply(a.S);
        File.Delete(Path.Combine(a.P.Source, "cloud-only.txt"));
        File.WriteAllText(Path.Combine(a.P.Source, "different.txt"), "edit");
        File.WriteAllText(Path.Combine(a.P.Source, "local-only.txt"), "new");
        var root = (await f.Drive.FindAsync("drive-root", "Documents", default))!;
        f.Drive.Add(root.Id, "unknown.txt", [1, 2]);
        File.WriteAllText(Path.Combine(a.P.Source, "unknown.txt"), "unknown");
        var writes = f.Drive.UploadIntents.Count;
        var report = (await new CloudBackupRuntime(a.C, () => f.Drive).PreviewAsync(SyncDirection.Backup, null, true, default)) with { Mode = "Verify" };
        Assert.AreEqual(1, report.Verification.Match); Assert.AreEqual(1, report.Verification.Different);
        Assert.AreEqual(1, report.Verification.MissingLocal); Assert.AreEqual(1, report.Verification.MissingCloud);
        Assert.AreEqual(1, report.Verification.Unverifiable);
        Assert.AreEqual(2, report.ExitCode); Assert.AreEqual(0, f.Drive.BusinessDownloads);
        Assert.HasCount(writes, f.Drive.UploadIntents);
        Assert.AreEqual("LocalSha256AndCloudMetadata", report.VerificationEvidence);
    }
}
