using HarborBackup.Core.Domain;
using HarborBackup.Windows.Integration;
using HarborBackup.Windows.Persistence;
using static HarborBackup.Windows.Tests.CloudTransportTests;

namespace HarborBackup.Windows.Tests;

[TestClass]
[DoNotParallelize]
public sealed class ProjectCoverageTests
{
    [TestMethod]
    public async Task PartialSelectionCannotRefreshCompleteSuccessAndFullRunClearsBacklog()
    {
        using var f = new Fixture(); var a = f.Device("A");
        File.WriteAllText(Path.Combine(a.P.Source, "a.txt"), "a");
        File.WriteAllText(Path.Combine(a.P.Source, "b.txt"), "b");
        var runtime = new CloudBackupRuntime(a.C, () => f.Drive);
        var full = await runtime.ExecuteAsync(SyncDirection.Backup, null, false, false, null, default);
        Assert.IsTrue(full.Projects.Single().CoverageComplete);
        var first = ProjectHealthStore.Read(a.C, a.P, RunStatusStore.Read(a.C.Preview));
        Assert.IsNotNull(first.LastFullSuccessUtc);
        File.WriteAllText(Path.Combine(a.P.Source, "a.txt"), "updated a");
        File.WriteAllText(Path.Combine(a.P.Source, "b.txt"), "updated b");
        var preview = await runtime.PreviewAsync(SyncDirection.Backup, a.P.ProjectId, false, default, prepareUpload: true);
        var selection = preview.UploadSelection! with { Files = preview.UploadSelection!.Files.Take(1).ToArray() };
        var partial = await runtime.ExecuteAsync(SyncDirection.Backup, a.P.ProjectId, false, false, null, default, confirmedUpload: selection);
        Assert.AreEqual(0, partial.ExitCode);
        Assert.IsFalse(partial.Projects.Single().CoverageComplete);
        var after = ProjectHealthStore.Read(a.C, a.P, RunStatusStore.Read(a.C.Preview));
        Assert.AreEqual(first.LastFullSuccessUtc, after.LastFullSuccessUtc);
        Assert.IsTrue(after.PendingCountIncomplete);
        Assert.IsNotNull(after.OldestPendingUtc);
        var finished = await runtime.ExecuteAsync(SyncDirection.Backup, null, false, false, null, default);
        Assert.IsTrue(finished.Projects.Single().CoverageComplete);
        var final = ProjectHealthStore.Read(a.C, a.P, RunStatusStore.Read(a.C.Preview));
        Assert.IsFalse(final.PendingCountIncomplete);
        Assert.IsNull(final.OldestPendingUtc);
    }
}
