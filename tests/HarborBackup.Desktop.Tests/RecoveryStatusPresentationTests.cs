using System.Text.Json;
using HarborBackup.Core.Domain;
using HarborBackup.Desktop;
using HarborBackup.Desktop.Localization;
using HarborBackup.Desktop.Presentation;
using HarborBackup.Windows.Integration;
using HarborBackup.Windows.Persistence;
using HarborBackup.Windows.Persistence.Configuration;

namespace HarborBackup.Windows.Tests;

[TestClass]
[DoNotParallelize]
public sealed class RecoveryStatusPresentationTests
{
    private static readonly string ProjectId = new('a', 64);
    private static readonly CloudAccountSettings Connected = new(Enabled: true);
    private static FileResult NetworkFile() => new("draft.txt", FileAction.Deferred, false, "synthetic transport diagnostic")
        { FailureKind = FileFailureKind.Network, Recovery = RecoveryDisposition.AutomaticRetry };
    private static ProjectRun Run(FileResult[]? files = null, PreviewIssue[]? issues = null) =>
        new("PersonalProjects", files ?? [NetworkFile()], issues ?? [], new())
        { ProjectId = ProjectId, Recovery = RecoveryDisposition.AutomaticRetry };
    private static BackupReport Report(ProjectRun run)
    {
        var time = DateTimeOffset.UtcNow;
        return new("Backup", time, time, [run]);
    }
    private static DesktopState State(DetailRow[] rows) => new("等待自动重试", false, false, false,
        null, null, null, null, [], rows, [], null);

    [TestMethod]
    public void NetworkAndPendingEvidenceRemainAutomaticWithoutParsingDiagnosticText()
    {
        var pending = new PreviewIssue("PendingOperationsRetained", "original intent retained", false)
            { Recovery = RecoveryDisposition.AutomaticRetry };
        var run = Run(issues: [pending]);
        Assert.IsTrue(CloudRunStatus.IsAutomaticRetry(run));
        Assert.IsTrue(CloudRunStatus.IsAutomaticRetryOnly(run));
        Assert.AreEqual("WaitingForRetry", CloudRunStatus.Result(run));
        var rows = ReportPresentation.Details(Report(run));
        Assert.HasCount(2, rows);
        Assert.IsTrue(rows.All(r => r.IsAutomaticRetry && r.ProjectId == ProjectId));
        Assert.AreEqual("synthetic transport diagnostic", rows[0].Details);
        Assert.IsFalse(rows.Any(r => r.IsOfflineIssue), "Structured waiting rows stay visible rather than being removed as an old offline diagnostic.");
        Assert.IsTrue(rows.All(r => r.ActionProjectId is null), "A waiting transfer must not offer a version-choice action.");
    }

    [TestMethod]
    [DataRow("zh-CN")]
    [DataRow("en-US")]
    public void RecoveryCaptionsAndTrayAreLocalizedWithoutAnActionRequiredNotification(string language)
    {
        var previous = UiText.Language;
        try
        {
            UiText.SetLanguage(language);
            var rows = ReportPresentation.Details(Report(Run()));
            var state = State(rows) with { Status = "失败" };
            Assert.AreEqual(UiText.Text("等待自动重试"), rows.Single().DisplayAction);
            Assert.Contains(UiText.Text("等待自动重试"), rows.Single().DisplayDetails);
            var tray = TrayPresentation.Create(state, Connected);
            Assert.AreEqual(TrayBadge.Waiting, tray.Badge);
            Assert.Contains(UiText.Text("等待自动重试"), tray.ToolTip);
            Assert.IsLessThanOrEqualTo(63, tray.ToolTip.Length);
            Assert.IsFalse(new ProblemNotificationPolicy().ShouldNotify(state), "A retained network task should not be announced as requiring a person.");
            Assert.AreEqual(TrayBadge.Paused, TrayPresentation.Create(state with { Paused = true }, Connected).Badge);
        }
        finally { UiText.SetLanguage(previous); }
    }

    [TestMethod]
    public void MixedConflictKeepsTheHumanActionWhileOtherFilesCanRetry()
    {
        var conflict = new FileResult("other.txt", FileAction.ConflictUnresolved, false, "both changed");
        var run = Run([NetworkFile(), conflict]);
        Assert.IsTrue(CloudRunStatus.IsAutomaticRetry(run));
        Assert.IsFalse(CloudRunStatus.IsAutomaticRetryOnly(run));
        Assert.AreEqual(RecoveryDisposition.AwaitingConfirmation, CloudRunStatus.Recovery(run));
        Assert.AreEqual("AwaitingConfirmation", CloudRunStatus.Result(run));
        var rows = ReportPresentation.Details(Report(run));
        var choice = rows.Single(r => r.Path == "other.txt");
        Assert.AreEqual(RecoveryDisposition.AwaitingConfirmation, choice.Recovery);
        Assert.AreEqual(ProjectId, choice.ActionProjectId);
        var state = State(rows) with { NetworkOffline = true };
        Assert.AreEqual(TrayBadge.Attention, TrayPresentation.Create(state, Connected).Badge);
        Assert.IsTrue(new ProblemNotificationPolicy().ShouldNotify(state));
    }

    [TestMethod]
    [DataRow(FileFailureKind.Authorization)]
    [DataRow(FileFailureKind.RecoveryRequired)]
    public void RepairFailuresCannotBeHiddenByAnAutomaticHint(FileFailureKind failure)
    {
        var file = NetworkFile() with { FailureKind = failure };
        var run = Run([file]);
        Assert.AreEqual(RecoveryDisposition.RepairRequired, CloudRunStatus.Recovery(run));
        Assert.IsFalse(CloudRunStatus.IsAutomaticRetry(run));
        Assert.IsFalse(CloudRunStatus.IsAutomaticRetry(file));
        Assert.IsFalse(CloudRunStatus.IsAutomaticRetryOnly(run));
        var rows = ReportPresentation.Details(Report(run));
        Assert.AreEqual(RecoveryDisposition.RepairRequired, rows.Single().Recovery);
        Assert.IsNull(rows.Single().ActionProjectId);
        Assert.AreEqual(TrayBadge.Failed, TrayPresentation.Create(State(rows), Connected).Badge);
        Assert.IsTrue(new ProblemNotificationPolicy().ShouldNotify(State(rows)));
    }

    [TestMethod]
    public void StoppedTaskDoesNotBecomeAnAutomaticRetryFromAnEarlierNetworkFailure()
    {
        var run = Run([NetworkFile() with { FailureKind = FileFailureKind.Cancelled, Recovery = RecoveryDisposition.Stopped }])
            with { Recovery = RecoveryDisposition.Stopped };
        Assert.AreEqual("Stopped", CloudRunStatus.Result(run));
        Assert.IsFalse(CloudRunStatus.IsAutomaticRetry(run));
        var rows = ReportPresentation.Details(Report(run));
        Assert.AreEqual(RecoveryDisposition.Stopped, rows.Single().Recovery);
        Assert.IsFalse(new ProblemNotificationPolicy().ShouldNotify(State(rows) with { Status = "已安全停止" }));
        Assert.AreEqual(TrayBadge.None, TrayPresentation.Create(State(rows), Connected).Badge);
    }

    [TestMethod]
    public void LegacyNetworkAndUnknownPendingRecordsDoNotAcquireResumePermission()
    {
        var run = Run([NetworkFile() with { Recovery = RecoveryDisposition.None }],
            [new("PendingOperationsRetained", "unclassified evidence", false)]) with { Recovery = RecoveryDisposition.None };
        Assert.IsFalse(CloudRunStatus.IsAutomaticRetry(run));
        Assert.IsFalse(CloudRunStatus.IsAutomaticRetryOnly(run));
        Assert.IsFalse(CloudRunStatus.IsOfflineInterruption(run));
        Assert.AreEqual("Failed", CloudRunStatus.Result(run));
    }

    [TestMethod]
    public void TaskOnlyWaitingProducesAnIdentifiedVisibleSummary()
    {
        var rows = ReportPresentation.Details(Report(Run([], [])));
        Assert.HasCount(1, rows);
        Assert.AreEqual(ProjectId, rows[0].ProjectId);
        Assert.AreEqual(RecoveryDisposition.AutomaticRetry, rows[0].Recovery);
        Assert.IsTrue(ReportPresentation.IsAutomaticRetryOnly(rows));
        Assert.IsFalse(new ProblemNotificationPolicy().ShouldNotify(State(rows)));
    }

    [TestMethod]
    public void StructuredDispositionAndRetryDeadlineSurviveReportSerialization()
    {
        var deadline = DateTimeOffset.UtcNow.AddMinutes(2);
        var run = Run(issues: [new("NetworkWaiting", "retained original diagnostic", false)
            { FailureKind = FileFailureKind.Network, Recovery = RecoveryDisposition.AutomaticRetry }])
            with { RetryNotBeforeUtc = deadline };
        var value = JsonSerializer.Deserialize<BackupReport>(JsonSerializer.Serialize(Report(run), AtomicJson.Options), AtomicJson.Options)!;
        Assert.AreEqual(RecoveryDisposition.AutomaticRetry, value.Projects.Single().Recovery);
        Assert.AreEqual(deadline, value.Projects.Single().RetryNotBeforeUtc);
        Assert.AreEqual(RecoveryDisposition.AutomaticRetry, value.Projects.Single().Files.Single().Recovery);
        Assert.AreEqual(FileFailureKind.Network, value.Projects.Single().Issues.Single().FailureKind);
        Assert.AreEqual(RecoveryDisposition.AutomaticRetry, value.Projects.Single().Issues.Single().Recovery);
        var old = JsonSerializer.Serialize(new FileResult("unchanged.txt", FileAction.Equal, false), AtomicJson.Options);
        Assert.DoesNotContain("recovery", old.ToLowerInvariant(), "Defaults must not fabricate a recovery policy for old records.");
    }
}
