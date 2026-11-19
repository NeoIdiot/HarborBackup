using HarborBackup.Core.Domain;
using HarborBackup.Desktop.Controllers;
using HarborBackup.Desktop.Presentation;
using HarborBackup.OneDrive.Transport;
using HarborBackup.Windows.Integration;
using HarborBackup.Windows.Persistence;

namespace HarborBackup.Windows.Tests;

[TestClass]
public sealed class NetworkStatusTests
{
    [TestMethod]
    [DataRow(false, false)]
    [DataRow(false, true)]
    [DataRow(true, false)]
    public async Task ReconnectClearsLiveIndicatorAndLeavesRunEvidence(bool legacySummary, bool otherIssue)
    {
        using var ws = new TestWorkspace();
        var paths = new RuntimePaths(Path.Combine(ws.Config, "projects.json"), Path.Combine(ws.Config, "backup-settings.json"));
        var config = new BackupRuntime(paths).Load(forExecution: false);
        var project = config.Projects.Single();
        var now = DateTimeOffset.UtcNow;
        var offline = new PreviewIssue("CloudOperationFailed", CloudNetworkAvailability.Offline);
        var pendingPublication = new PreviewIssue("DeviceStatusPending", "设备状态暂未上报。" + Environment.NewLine + CloudNetworkAvailability.Offline, false);
        var report = new BackupReport("Backup", now, now, [new(project.Destination,
            otherIssue ? [new("pending.txt", FileAction.Deferred, false, "PendingFileOperation")] : [],
            [offline, pendingPublication], new()) { ProjectId = project.ProjectId }]);
        RunStatusStore.Write(config, report);
        RunStatusStore.Log(config, report);
        var statusPath = RunStatusStore.PathFor(config.Preview);
        if (legacySummary)
        {
            var saved = RunStatusStore.Read(config.Preview)!;
            saved["projects"]![0]!["lastResult"] = "Failed";
            File.WriteAllText(statusPath, saved.ToJsonString());
        }
        var savedStatus = File.ReadAllText(statusPath);
        var online = false;
        var controller = new DesktopController(paths, networkAvailable: () => online);
        await controller.RefreshAsync(includeHistory: true);
        await controller.TickAsync();
        Assert.IsTrue(controller.State.NetworkOffline);
        Assert.AreEqual("等待网络", controller.State.Status);
        Assert.AreEqual(TrayBadge.Waiting, TrayPresentation.Create(controller.State, controller.CloudAccount).Badge);

        online = true;
        await controller.TickAsync();

        Assert.IsFalse(controller.State.NetworkOffline);
        Assert.AreEqual("空闲", controller.State.Status);
        Assert.IsNull(controller.State.Error);
        Assert.AreEqual(otherIssue ? TrayBadge.Failed : TrayBadge.None, TrayPresentation.Create(controller.State, controller.CloudAccount).Badge);
        Assert.AreEqual(otherIssue ? "失败" : "上次运行因断网中断", controller.State.Projects.Single().Result);
        Assert.AreEqual(otherIssue, controller.State.Details.Any(row => row.Path == "pending.txt" && row.Attention));
        Assert.IsFalse(controller.State.Details.Any(row => row.IsOfflineIssue));
        CollectionAssert.AreEqual(new[] { offline, pendingPublication }, controller.State.History.Single().Report!.Projects.Single().Issues.ToArray());
        Assert.AreEqual(savedStatus, File.ReadAllText(statusPath));
    }
}
