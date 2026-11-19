using HarborBackup.Core.Domain;

namespace HarborBackup.Core.Tests;

[TestClass]
public sealed class InspectionTests
{
    private static FileObservation File(char hash) => FileObservation.Present(new(new string(hash, 64), 4, DateTimeOffset.UnixEpoch, DateTimeOffset.UtcNow), true);
    [TestMethod]
    [DataRow("match", VerificationCategory.Match)]
    [DataRow("different", VerificationCategory.Different)]
    [DataRow("unknown", VerificationCategory.Unverifiable)]
    [DataRow("local-missing", VerificationCategory.MissingLocal)]
    [DataRow("cloud-missing", VerificationCategory.MissingCloud)]
    [DataRow("unstable", VerificationCategory.Unverifiable)]
    [DataRow("type", VerificationCategory.TypeConflict)]
    public void VerificationReportsEvidenceRatherThanTransferDirection(string kind, VerificationCategory expected)
    {
        var local = kind == "local-missing" ? FileObservation.Missing : kind == "unstable"
            ? FileObservation.Problem(ObservationKind.Unstable, "Changing") : File('a');
        var cloud = kind == "cloud-missing" ? FileObservation.Missing : kind == "type" ? FileObservation.Directory : File(kind == "different" ? 'b' : 'a');
        Assert.AreEqual(expected, VerificationSummary.Classify(local, cloud, kind != "unknown"));
    }
    [TestMethod]
    public void BudgetCompletionCannotHideOtherFileFailuresOrCancellation()
    {
        var budget = new FileResult("later", FileAction.Deferred, false, "Budget") { FailureKind = FileFailureKind.Budget };
        var localError = new FileResult("locked", FileAction.Deferred, false, "Locked") { FailureKind = FileFailureKind.FileUnavailable };
        var report = new BackupReport("Backup", DateTimeOffset.UtcNow, DateTimeOffset.UtcNow,
            [new("p", [budget], [], new())]) { BudgetStopReason = "AutomaticTransferBudgetReached" };
        Assert.AreEqual("BudgetLimited", report.Completion);
        Assert.AreEqual("NeedsAttention", (report with { Projects = [new("p", [budget, localError], [], new())] }).Completion);
        Assert.AreEqual("NeedsAttention", (report with { Diagnostics = [new("StatusFailed", "Status failed", false)] }).Completion);
        Assert.AreEqual("NeedsAttention", (report with { Projects = [new("p", [budget], [new("MappingAwaitingOperation", "Choose", false)], new())] }).Completion);
        Assert.AreEqual("Cancelled", (report with { Cancelled = true }).Completion);
    }
    [TestMethod]
    public void VerificationDifferencesAreNotReportedAsSuccessfulEquality()
    {
        var plan = new PreviewPlan("a", new(FileAction.Copy, false, FileSide.Target, FileSide.None, ProtectionKind.None, "SourceOnly"), true, false)
        { Verification = VerificationCategory.MissingCloud, TransferBytes = 4 };
        var preview = new PreviewReport([new("project", [plan], [], new())]);
        Assert.AreEqual(0, preview.ExitCode);
        Assert.AreEqual(2, (preview with { Mode = "Verify" }).ExitCode);
        Assert.AreEqual(1, preview.Summary.Transfers); Assert.AreEqual(4L, preview.Summary.TransferBytes);
    }
}
