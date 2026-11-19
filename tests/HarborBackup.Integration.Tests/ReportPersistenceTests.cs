using System.Text.Json;
using HarborBackup.Core.Domain;
using HarborBackup.Windows.Integration;
using HarborBackup.Windows.Persistence;
using static HarborBackup.Windows.Tests.CloudTransportTests;

namespace HarborBackup.Windows.Tests;

[TestClass]
[DoNotParallelize]
public sealed class ReportPersistenceTests
{
    [TestMethod]
    public void MalformedDiagnosticListIsAnUnreadableHistoryRecord()
    {
        using var f = new Fixture();
        var history = Path.Combine(f.Root, "malformed-history"); Directory.CreateDirectory(history);
        File.WriteAllText(Path.Combine(history, "record.json"),
            "{\"mode\":\"Backup\",\"projects\":[],\"diagnostics\":null}");
        var record = RunHistory.Read(history).Single();
        Assert.IsNull(record.Report); Assert.AreEqual(1, record.ExitCode);
    }

    [TestMethod]
    [DataRow("locked-status")]
    [DataRow("history-path")]
    [DataRow("both")]
    public async Task DisplayPersistenceFailuresPreserveVerifiedTransfersAndBaselines(string failure)
    {
        using var f = new Fixture(); var a = f.Device("A");
        File.WriteAllText(Path.Combine(a.P.Source, "a.txt"), "verified payload");
        var status = RunStatusStore.PathFor(a.C.Preview);
        Directory.CreateDirectory(Path.GetDirectoryName(status)!);
        if (failure == "both") File.WriteAllText(status, "corrupt summary retained");
        else File.WriteAllText(status, "{}");
        var blockedHistory = Path.Combine(f.Root, "not-a-history-directory");
        File.WriteAllText(blockedHistory, "sentinel");
        var config = failure == "locked-status" ? a.C : a.C with { HistoryDirectory = blockedHistory };
        using var locked = failure == "locked-status" ? new FileStream(status, FileMode.Open, FileAccess.ReadWrite, FileShare.None) : null;
        // A valid initial status is absent in the history-only case.
        if (failure == "history-path") File.Delete(status);
        var report = await new CloudBackupRuntime(config, () => f.Drive).ExecuteAsync(SyncDirection.Backup, "Documents", false, false, null, default);
        Assert.AreEqual(2, report.ExitCode);
        Assert.AreEqual("CloudOperationsConfirmed", report.CloudUploadStatus);
        Assert.IsTrue(report.Projects.Single().Files.Single().Changed);
        Assert.AreEqual(0, report.Projects.Single().ExitCode);
        Assert.IsFalse(a.S.Pending);
        Assert.HasCount(failure == "both" ? 2 : 1, report.Diagnostics);
        if (failure != "history-path") Assert.IsTrue(report.Diagnostics.Any(d => d.Code == "RunStatusWriteFailed"));
        if (failure != "locked-status") Assert.IsTrue(report.Diagnostics.Any(d => d.Code == "RunHistoryWriteFailed"));
        var read = await a.S.ReadAsync(true, default);
        Assert.AreEqual(read.Snapshot.Files.Single().Source.Version!.Sha256, read.Snapshot.Files.Single().Baseline!.LastCommonHash);
        Assert.AreEqual("verified payload", File.ReadAllText(Path.Combine(a.P.Source, "a.txt")));
        if (failure == "both") Assert.AreEqual("corrupt summary retained", File.ReadAllText(status));
        if (failure == "locked-status")
        {
            var history = Directory.GetFiles(config.HistoryDirectory, "*.json").Single();
            var saved = JsonSerializer.Deserialize<BackupReport>(File.ReadAllText(history), AtomicJson.Options)!;
            Assert.AreEqual("RunStatusWriteFailed", saved.Diagnostics.Single().Code);
            Assert.AreEqual("CloudOperationsConfirmed", saved.CloudUploadStatus);
        }
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public void PersistenceWarningsDoNotMaskTransferFailureOrCancellation(bool cancelled)
    {
        var report = new BackupReport("Backup", DateTimeOffset.UtcNow, DateTimeOffset.UtcNow,
            [new("Demo", [], [new("TransferFailed", "failed")], new())], cancelled);
        var result = RunReportPersistence.Save(report, _ => throw new IOException("full"), _ => throw new IOException("denied"));
        Assert.AreEqual(cancelled ? 130 : 1, result.ExitCode);
        Assert.HasCount(2, result.Diagnostics);
    }
}
