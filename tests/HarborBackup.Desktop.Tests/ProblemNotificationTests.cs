using System.Xml.Linq;
using HarborBackup.Core.Domain;
using HarborBackup.Desktop;
using HarborBackup.Desktop.Lifecycle;
using HarborBackup.Desktop.Presentation;
using HarborBackup.OneDrive.Transport;

namespace HarborBackup.Windows.Tests;

[TestClass]
public sealed class ProblemNotificationTests
{
    private static DesktopState Idle => new("空闲", false, false, false, null, null, null, null, [], [], [], null);

    [TestMethod]
    public void NotificationIgnoresOfflineBudgetAndCancellationButKeepsFileProblems()
    {
        var policy = new ProblemNotificationPolicy();
        var offline = new DetailRow("Project", "", "CloudOperationFailed", CloudNetworkAvailability.Offline, true);
        var budget = new DetailRow("Project", "", "AutomaticTransferBudgetReached", "AutomaticTransferBudgetReached", true);
        var cancelled = new DetailRow("Project", "draft.txt", "延期处理", "Cancelled", true) { FailureKind = FileFailureKind.Cancelled };
        var state = Idle with { NetworkOffline = true, Details = [offline, budget, cancelled] };
        Assert.IsFalse(policy.ShouldNotify(state));

        var conflict = new DetailRow("Project", "draft.txt", "内容冲突", "BothChanged", true);
        Assert.IsTrue(policy.ShouldNotify(state with { Details = [offline, budget, cancelled, conflict] }));
    }

    [TestMethod]
    public void SameProblemDoesNotNotifyEachPollOrBackupAndCanRecurAfterClearing()
    {
        var policy = new ProblemNotificationPolicy();
        var conflict = new DetailRow("Project", "draft.txt", "内容冲突", "BothChanged", true);
        var issue = Idle with { Details = [conflict] };
        Assert.IsFalse(policy.ShouldNotify(issue with { Busy = true }));
        Assert.IsTrue(policy.ShouldNotify(issue));
        Assert.IsFalse(policy.ShouldNotify(issue with { Busy = true }));
        Assert.IsFalse(policy.ShouldNotify(issue with { Details = [conflict with { }], LastCompleted = DateTimeOffset.UtcNow }));
        Assert.IsFalse(policy.ShouldNotify(Idle));
        Assert.IsTrue(policy.ShouldNotify(issue));
        Assert.IsTrue(policy.ShouldNotify(issue with { Details = [conflict with { Path = "other.txt" }] }));
    }

    [TestMethod]
    public void NotificationContentEscapesTextAndUsesFixedReadOnlyActivation()
    {
        const string details = "文件 <a&b>.txt ⚓";
        var xml = WindowsNotification.CreateContent("HarborBackup", details + "\0");
        var toast = XElement.Parse(xml);
        Assert.AreEqual("protocol", (string?)toast.Attribute("activationType"));
        Assert.AreEqual("harborbackup-alert:issues", (string?)toast.Attribute("launch"));
        CollectionAssert.AreEqual(new[] { "HarborBackup", details }, toast.Descendants("text").Select(node => node.Value).ToArray());
    }

    [TestMethod]
    public async Task NotificationActivatesIssuesOnExistingInstance()
    {
        var channel = "HarborBackup.Test-" + Guid.NewGuid().ToString("N");
        var requested = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var activation = new InstanceActivation(channel, () => requested.TrySetResult("Window"),
            () => requested.TrySetResult("Shutdown"), () => requested.TrySetResult("Issues"));
        Assert.IsTrue(await InstanceActivation.ShowIssuesAsync(channel));
        Assert.AreEqual("Issues", await requested.Task.WaitAsync(TimeSpan.FromSeconds(5)));
    }
}
