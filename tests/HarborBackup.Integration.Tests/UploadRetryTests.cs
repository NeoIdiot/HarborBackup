using System.Text;
using System.Text.Json.Nodes;
using HarborBackup.Core.Domain;
using HarborBackup.Windows.Integration;
using static HarborBackup.Windows.Tests.CloudTransportTests;

namespace HarborBackup.Windows.Tests;

[TestClass]
[DoNotParallelize]
public sealed class UploadRetryTests
{
    [TestMethod]
    public async Task AutomaticBackupAndPreviewKeepInterruptedUploadPending()
    {
        using var f = new Fixture(); var a = f.Device("A");
        var local = Path.Combine(a.P.Source, "1021.docx"); File.WriteAllText(local, "old intent");
        f.Drive.BeforeUpload = (name, _) => name == "1021.docx" ? throw new IOException("Offline") : Task.CompletedTask;
        await Assert.ThrowsExactlyAsync<IOException>(() => Apply(a.S));
        var journal = Journal(f); var original = File.ReadAllBytes(journal);
        File.WriteAllText(local, "current local"); f.Drive.BeforeUpload = null;
        var count = f.Drive.UploadIntents.Count(i => i.Name == "1021.docx");
        var runtime = new CloudBackupRuntime(a.C, () => f.Drive);
        var preview = await runtime.PreviewAsync(SyncDirection.Backup, "Documents", false, default, prepareUpload: true);
        Assert.AreEqual(FileAction.Deferred, preview.Projects.Single().Plans.Single().Decision.Action);
        await runtime.ExecuteAsync(SyncDirection.Backup, "Documents", false, false, null, default, automatic: true);
        Assert.IsTrue(a.S.Pending); CollectionAssert.AreEqual(original, File.ReadAllBytes(journal));
        Assert.AreEqual(count, f.Drive.UploadIntents.Count(i => i.Name == "1021.docx"));
        Assert.AreEqual("current local", File.ReadAllText(local));
    }

    [TestMethod]
    public async Task RetryRejectsChangedArchiveWithoutUploadingOrAdvancingBaseline()
    {
        using var f = new Fixture(); var a = f.Device("A");
        var local = Path.Combine(a.P.Source, "1021.docx"); File.WriteAllText(local, "original"); await Apply(a.S);
        File.WriteAllText(local, "interrupted"); f.Drive.FailCopies = true;
        f.Drive.BeforeUpload = (name, _) => name == "1021.docx" ? throw new IOException("Offline") : Task.CompletedTask;
        await Assert.ThrowsExactlyAsync<IOException>(() => Apply(a.S));
        var journal = Journal(f); var original = File.ReadAllBytes(journal);
        var id = JsonNode.Parse(File.ReadAllText(journal))!["archived"]!["id"]!.GetValue<string>();
        var saved = f.Drive.Items[id]; f.Drive.Items[id] = (saved.Item with { ETag = "changed archive" }, saved.Parent, saved.Bytes);
        File.WriteAllText(local, "current local"); f.Drive.BeforeUpload = null; f.Drive.FailCopies = false;
        var count = f.Drive.UploadIntents.Count(i => i.Name == "1021.docx");
        var report = await new CloudBackupRuntime(a.C, () => f.Drive).ExecuteAsync(SyncDirection.Backup, "Documents", false, true, null, default);
        Assert.AreNotEqual(0, report.ExitCode); Assert.IsTrue(a.S.Pending);
        CollectionAssert.AreEqual(original, File.ReadAllBytes(journal));
        Assert.AreEqual(count, f.Drive.UploadIntents.Count(i => i.Name == "1021.docx"));
        var read = await a.S.ReadAsync(true, default);
        Assert.AreNotEqual(read.Snapshot.Files.Single().Source.Version!.Sha256, read.Snapshot.Files.Single().Baseline!.LastCommonHash);
        Assert.AreEqual("current local", File.ReadAllText(local));
    }

    private static string Journal(Fixture f) => Directory.GetFiles(f.Root, "*.json", SearchOption.AllDirectories)
        .Single(p => Path.GetDirectoryName(p)!.EndsWith("cloud-transactions", StringComparison.Ordinal));

    [TestMethod]
    [DataRow("New", false)]
    [DataRow("New", true)]
    [DataRow("SameSource", true)]
    [DataRow("Prepared", false)]
    [DataRow("Archived", false)]
    [DataRow("Archived", true)]
    [DataRow("LostArchive", true)]
    [DataRow("LostUpload", false)]
    [DataRow("Competing", false)]
    [DataRow("Competing", true)]
    [DataRow("Session", true)]
    public async Task ManualRetryReconcilesOldIntentAndUploadsCurrentSource(string phase, bool recoverOnly)
    {
        using var f = new Fixture(); var a = f.Device("A");
        var local = Path.Combine(a.P.Source, "1021.docx");
        File.WriteAllText(local, "original cloud");
        if (phase is not ("New" or "SameSource" or "Session")) await Apply(a.S);
        File.WriteAllText(local, "interrupted version");
        f.Drive.FailCopies = true;
        if (phase == "Prepared") f.Drive.BeforeMove = (_, _, _) => throw new IOException("Interrupted before archive");
        else if (phase == "LostUpload") f.Drive.AfterUpload = i => i.Name == "1021.docx" ? throw new IOException("Lost upload receipt") : Task.CompletedTask;
        else f.Drive.BeforeUpload = (name, _) => name == "1021.docx" ? throw new IOException("Interrupted upload") : Task.CompletedTask;
        await Assert.ThrowsExactlyAsync<IOException>(() => Apply(a.S));
        var root = (await f.Drive.FindAsync("drive-root", "Documents", default))!;
        var journal = Journal(f);
        if (phase is "LostArchive" or "Session")
        {
            var node = JsonNode.Parse(File.ReadAllText(journal))!;
            if (phase == "LostArchive") { node["phase"] = "Prepared"; node["archived"] = null; }
            else
            {
                node["session"] = "opaque-session-kept-with-old-intent";
                f.Drive.Add(root.Id, ".harbor-upload-" + node["id"]!.GetValue<string>() + ".tmp", Encoding.UTF8.GetBytes("partial stage"));
            }
            File.WriteAllText(journal, node.ToJsonString(), new UTF8Encoding(phase == "Session"));
        }
        if (phase == "Competing") f.Drive.Add(root.Id, "1021.docx", Encoding.UTF8.GetBytes("another device"));
        var originalJournal = File.ReadAllBytes(journal);
        var originals = f.Drive.Items.Values.Where(v => !v.Item.Folder &&
            (Encoding.UTF8.GetString(v.Bytes) == "original cloud" || Encoding.UTF8.GetString(v.Bytes) == "another device"))
            .Select(v => v.Bytes).ToArray();
        if (phase != "SameSource") File.WriteAllText(local, "current local version");
        f.Drive.BeforeMove = null; f.Drive.BeforeUpload = null; f.Drive.AfterUpload = null; f.Drive.FailCopies = false;
        var report = await new CloudBackupRuntime(a.C, () => f.Drive).ExecuteAsync(SyncDirection.Backup, "Documents", false, recoverOnly, null, default);
        Assert.AreEqual(0, report.ExitCode, System.Text.Json.JsonSerializer.Serialize(report));
        var expected = phase == "SameSource" ? "interrupted version" : "current local version";
        var live = (await f.Drive.FindAsync(root.Id, "1021.docx", default))!;
        Assert.AreEqual(expected, Encoding.UTF8.GetString(f.Drive.Items[live.Id].Bytes));
        Assert.AreEqual(expected, File.ReadAllText(local));
        Assert.IsFalse(a.S.Pending);
        var retained = Directory.GetFiles(f.Root, "*.json", SearchOption.AllDirectories).Single(p => p.Contains("retained-transactions"));
        CollectionAssert.AreEqual(originalJournal, File.ReadAllBytes(retained));
        foreach (var original in originals)
            Assert.IsTrue(f.Drive.Items.Values.Any(v => v.Bytes.SequenceEqual(original)), "Every replaced cloud version must remain protected.");
        var read = await a.S.ReadAsync(true, default);
        var file = read.Snapshot.Files.Single(p => p.RelativePath == "1021.docx");
        Assert.AreEqual(file.Source.Version!.Sha256, file.Baseline!.LastCommonHash);
        Assert.AreEqual(file.Source.Version.Sha256, read.Heads["1021.docx"].Single().Version.Sha256);
    }

    [TestMethod]
    [DataRow("Upload")]
    [DataRow("Metadata")]
    public async Task FailedFreshUploadKeepsNewJournalAndDoesNotAdvanceBaseline(string failure)
    {
        using var f = new Fixture(); var a = f.Device("A");
        var local = Path.Combine(a.P.Source, "1021.docx"); File.WriteAllText(local, "old intent");
        f.Drive.BeforeUpload = (name, _) => name == "1021.docx" ? throw new IOException("Offline") : Task.CompletedTask;
        await Assert.ThrowsExactlyAsync<IOException>(() => Apply(a.S));
        var oldPath = Journal(f); var old = File.ReadAllBytes(oldPath);
        File.WriteAllText(local, "current local");
        if (failure == "Metadata") { f.Drive.BeforeUpload = null; f.Drive.FailAfterCommit = true; }
        var runtime = new CloudBackupRuntime(a.C, () => f.Drive);
        var failed = await runtime.ExecuteAsync(SyncDirection.Backup, "Documents", false, true, null, default);
        Assert.AreNotEqual(0, failed.ExitCode);
        Assert.IsTrue(a.S.Pending); Assert.AreNotEqual(oldPath, Journal(f));
        var read = await a.S.ReadAsync(true, default);
        Assert.IsNull(read.Snapshot.Files.Single().Baseline);
        var retained = Directory.GetFiles(f.Root, "*.json", SearchOption.AllDirectories).Single(p => p.Contains("retained-transactions"));
        CollectionAssert.AreEqual(old, File.ReadAllBytes(retained));
        f.Drive.BeforeUpload = null;
        Assert.AreEqual(0, (await runtime.ExecuteAsync(SyncDirection.Backup, "Documents", false, true, null, default)).ExitCode);
        Assert.AreEqual("current local", File.ReadAllText(local)); Assert.IsFalse(a.S.Pending);
    }
}
