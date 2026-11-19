using System.Text;
using System.Text.Json;
using HarborBackup.Core.Domain;
using HarborBackup.OneDrive.Transport;
using HarborBackup.Windows.Integration;
using HarborBackup.Windows.Persistence;
using static HarborBackup.Windows.Tests.CloudTransportTests;

namespace HarborBackup.Windows.Tests;

[TestClass]
[DoNotParallelize]
public sealed class CloudUploadFlowTests
{
    [TestMethod]
    [DataRow("Prepared", false)]
    [DataRow("Uploading", false)]
    [DataRow("Uploading", true)]
    public async Task ChangedSourceCanCloseUnpublishedUploadWithOriginalJournalRetained(string phase, bool withSession)
    {
        using var f = new Fixture(); var a = f.Device("A");
        var local = Path.Combine(a.P.Source, "1021.docx");
        File.WriteAllText(local, "old intended version");
        f.Drive.BeforeUpload = (name, _) => name == "1021.docx" ? throw new IOException("Interrupted upload") : Task.CompletedTask;
        await Assert.ThrowsExactlyAsync<IOException>(() => Apply(a.S));
        var journal = Journal(f);
        var node = System.Text.Json.Nodes.JsonNode.Parse(File.ReadAllText(journal))!;
        node["phase"] = phase;
        if (withSession) node["session"] = "retained-opaque-session";
        if (withSession)
        {
            var target = await FileAt(f, "drive-root", "Documents");
            f.Drive.Add(target.Id, ".harbor-upload-" + node["id"]!.GetValue<string>() + ".tmp", Encoding.UTF8.GetBytes("partial stage"));
        }
        File.WriteAllText(journal, node.ToJsonString());
        var original = File.ReadAllBytes(journal);
        File.WriteAllText(local, "current edited version");
        f.Drive.BeforeUpload = null;
        var count = f.Drive.Items.Count; var writes = f.Drive.UploadIntents.Count;
        var results = new List<FileResult>();
        await a.S.RecoverAsync(default, results.Add);
        Assert.AreEqual("ChangedSourceOperationClosed", results.Single().Error);
        Assert.IsFalse(a.S.Pending);
        Assert.HasCount(count, f.Drive.Items); Assert.HasCount(writes, f.Drive.UploadIntents);
        var retained = Directory.GetFiles(f.Root, "*.json", SearchOption.AllDirectories).Single(p => p.Contains("retained-transactions"));
        CollectionAssert.AreEqual(original, File.ReadAllBytes(retained));
        Assert.AreEqual("current edited version", File.ReadAllText(local));
        await Apply(a.S);
        var root = await FileAt(f, "drive-root", "Documents");
        Assert.AreEqual("current edited version", Contents(f, await FileAt(f, root.Id, "1021.docx")));
    }

    [TestMethod]
    public async Task ChangedPreparedReplacementRetainsJournalWithoutTouchingOriginalOrBaseline()
    {
        using var f = new Fixture(); var a = f.Device("A");
        var local = Path.Combine(a.P.Source, "1021.docx"); File.WriteAllText(local, "original");
        await Apply(a.S);
        var read = await a.S.ReadAsync(false, default);
        var remote = read.Heads["1021.docx"].Single();
        var expected = await f.Drive.GetAsync(remote.ItemId, default);
        File.WriteAllText(local, "old intent");
        var observed = await new HarborBackup.Windows.FileSystem.FileObserver().ObserveAsync(local, null, new(), true, new(), default);
        var id = Guid.NewGuid().ToString("N");
        var journal = new CloudJournal(5, id, CloudMapping.Bind(a.P, a.C), "1021.docx", SyncDirection.Backup,
            observed.Version, remote, expected, true, ProtectionKind.Archive, expected.ParentId!,
            CreatedUtc: DateTimeOffset.UtcNow, Mapping: CloudMapping.Capture(a.P, a.C));
        var root = CloudMapping.StateRoot(a.P, a.C);
        var path = Path.Combine(root, "cloud-transactions", id + ".json");
        File.WriteAllText(path, JsonSerializer.Serialize(journal, AtomicJson.Options));
        var bytes = File.ReadAllBytes(path);
        var baselines = Directory.GetFiles(root, "*.json", SearchOption.AllDirectories)
            .Where(p => p.Contains(Path.DirectorySeparatorChar + "baselines" + Path.DirectorySeparatorChar)).ToDictionary(p => p, File.ReadAllBytes);
        File.WriteAllText(local, "latest edit");
        var count = f.Drive.Items.Count;
        await a.S.RecoverAsync(default);
        Assert.IsFalse(a.S.Pending);
        Assert.HasCount(count, f.Drive.Items);
        Assert.AreEqual("original", Contents(f, await f.Drive.GetAsync(expected.Id, default)));
        Assert.AreEqual("latest edit", File.ReadAllText(local));
        CollectionAssert.AreEqual(bytes, File.ReadAllBytes(Path.Combine(root, "retained-transactions", id + ".json")));
        foreach (var baseline in baselines) CollectionAssert.AreEqual(baseline.Value, File.ReadAllBytes(baseline.Key));
        await Apply(a.S);
        Assert.AreEqual("latest edit", Contents(f, await FileAt(f, expected.ParentId!, "1021.docx")));
    }

    [TestMethod]
    public async Task LostUploadReceiptCanRecoverAfterSourceWasEdited()
    {
        using var f = new Fixture(); var a = f.Device("A");
        var local = Path.Combine(a.P.Source, "1021.docx");
        File.WriteAllText(local, "acknowledgment lost");
        f.Drive.AfterUpload = item => item.Name == "1021.docx" ? throw new IOException("Lost response") : Task.CompletedTask;
        await Assert.ThrowsExactlyAsync<IOException>(() => Apply(a.S));
        File.WriteAllText(local, "edited after upload");
        f.Drive.AfterUpload = null;
        await a.S.RecoverAsync(default);
        Assert.IsFalse(a.S.Pending);
        var root = await FileAt(f, "drive-root", "Documents");
        Assert.AreEqual("acknowledgment lost", Contents(f, await FileAt(f, root.Id, "1021.docx")));
        Assert.AreEqual("edited after upload", File.ReadAllText(local));
        // Metadata may be published, but the completed business file is never re-uploaded.
        Assert.AreEqual(1, f.Drive.UploadIntents.Count(i => i.Name == "1021.docx"));
        var runtime = new CloudBackupRuntime(a.C, () => f.Drive);
        var preview = await runtime.PreviewAsync(SyncDirection.Backup, "Documents", false, default, prepareUpload: true);
        Assert.HasCount(1, preview.UploadSelection!.Files);
        var result = await runtime.ExecuteAsync(SyncDirection.Backup, "Documents", false, false, null, default,
            confirmedUpload: preview.UploadSelection);
        Assert.AreEqual(0, result.ExitCode);
        Assert.AreEqual("edited after upload", Contents(f, await FileAt(f, root.Id, "1021.docx")));
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task ChangedUploadCannotBeRetiredWithCompetingTargetOrUnreadableSource(bool lockSource)
    {
        using var f = new Fixture(); var a = f.Device("A");
        var local = Path.Combine(a.P.Source, "1021.docx"); File.WriteAllText(local, "old");
        f.Drive.BeforeUpload = (name, _) => name == "1021.docx" ? throw new IOException("Interrupted upload") : Task.CompletedTask;
        await Assert.ThrowsExactlyAsync<IOException>(() => Apply(a.S));
        var journal = Journal(f); var before = File.ReadAllBytes(journal);
        File.WriteAllText(local, "edited"); f.Drive.BeforeUpload = null;
        var root = await FileAt(f, "drive-root", "Documents");
        if (!lockSource) f.Drive.Add(root.Id, "1021.docx", Encoding.UTF8.GetBytes("competing device"));
        using var held = lockSource ? new FileStream(local, FileMode.Open, FileAccess.Read, FileShare.None) : null;
        await Assert.ThrowsAsync<IOException>(() => a.S.RecoverAsync(default));
        Assert.IsTrue(a.S.Pending);
        CollectionAssert.AreEqual(before, File.ReadAllBytes(journal));
        if (!lockSource) Assert.AreEqual("competing device", Contents(f, await FileAt(f, root.Id, "1021.docx")));
    }

    private static string Journal(Fixture f) => Directory.GetFiles(f.Root, "*.json", SearchOption.AllDirectories)
        .Single(p => Path.GetDirectoryName(p)!.EndsWith("cloud-transactions", StringComparison.Ordinal));
    private static async Task<RemoteItem> FileAt(Fixture f, string parent, string name) =>
        (await f.Drive.FindAsync(parent, name, default))!;
    private static string Contents(Fixture f, RemoteItem item) => Encoding.UTF8.GetString(f.Drive.Items[item.Id].Bytes);
    [TestMethod]
    public async Task FirstUploadUsesOriginalTreeWithoutProtectionDirectories()
    {
        using var f = new Fixture(); var a = f.Device("A");
        Directory.CreateDirectory(Path.Combine(a.P.Source, "docs"));
        File.WriteAllText(Path.Combine(a.P.Source, "docs", "说明.txt"), "original");
        var report = await new CloudBackupRuntime(a.C, () => f.Drive).ExecuteAsync(SyncDirection.Backup, null, false, false, null, default);
        Assert.AreEqual(0, report.ExitCode);
        Assert.AreEqual(1L, report.Projects.Single().Metrics.CloudUploadedFiles);
        Assert.AreEqual(8L, report.Projects.Single().Metrics.CloudUploadedBytes);
        Assert.IsNull(await f.Drive.FindAsync("root", "archives", default));
        Assert.IsNull(await f.Drive.FindAsync("root", "conflicts", default));
        Assert.IsFalse(f.Drive.Items.Values.Any(v => v.Item.Name == "content" || v.Item.Name.StartsWith(".harborbackup-upload-")));
        Assert.IsFalse(a.S.Pending);
    }
    [TestMethod]
    public async Task HistoryMirrorsRelativePathAndKeepsOnlyThePreviousContent()
    {
        using var f = new Fixture(); var a = f.Device("A");
        Directory.CreateDirectory(Path.Combine(a.P.Source, "docs"));
        var path = Path.Combine(a.P.Source, "docs", "a.txt");
        foreach (var version in new[] { "first", "second", "third", "fourth" })
        { File.WriteAllText(path, version); await Apply(a.S); }
        var repo = new CloudRepository(f.Drive, f.Account, DataPaths.FromState(a.C.Preview.StateDirectory));
        var history = (await repo.FolderAsync("archives/Documents/docs", false, default))!;
        var files = await f.Drive.ChildrenAsync(history.Id, default);
        Assert.HasCount(1, files);
        Assert.AreEqual("a.txt", files.Single().Name);
        Assert.AreEqual("third", Contents(f, files.Single()));
        Assert.IsNull(await f.Drive.FindAsync("root", "conflicts", default));
        Assert.AreEqual(0, f.Drive.BusinessDownloads);
        Assert.IsFalse(a.S.Pending);
    }
    [TestMethod]
    public async Task FailedUploadRestoresOldFileWithoutDownloadingAndRetryCanUpdate()
    {
        using var f = new Fixture(); var a = f.Device("A");
        var path = Path.Combine(a.P.Source, "a.txt");
        File.WriteAllText(path, "old"); await Apply(a.S);
        var root = await FileAt(f, "drive-root", "Documents");
        File.WriteAllText(path, "new");
        f.Drive.BeforeUpload = (name, _) =>
        {
            if (name == "a.txt")
            {
                Assert.IsNull(f.Drive.FindAsync(root.Id, name, default).Result, "Old file must already be archived.");
                throw new IOException("Injected transfer failure.");
            }
            return Task.CompletedTask;
        };
        await Assert.ThrowsExactlyAsync<IOException>(() => Apply(a.S));
        Assert.AreEqual("old", Contents(f, await FileAt(f, root.Id, "a.txt")));
        Assert.IsFalse(a.S.Pending);
        Assert.AreEqual(0, f.Drive.BusinessDownloads);
        f.Drive.BeforeUpload = null;
        await Apply(a.S);
        Assert.AreEqual("new", Contents(f, await FileAt(f, root.Id, "a.txt")));
    }
    [TestMethod]
    public async Task LostUploadResponseIsVerifiedAndCommittedWithoutReupload()
    {
        using var f = new Fixture(); var a = f.Device("A");
        File.WriteAllText(Path.Combine(a.P.Source, "a.txt"), "payload");
        f.Drive.AfterUpload = item => item.Name == "a.txt" ? throw new IOException("Lost response.") : Task.CompletedTask;
        await Assert.ThrowsExactlyAsync<IOException>(() => Apply(a.S));
        Assert.IsTrue(a.S.Pending);
        var root = await FileAt(f, "drive-root", "Documents");
        var id = (await FileAt(f, root.Id, "a.txt")).Id;
        f.Drive.AfterUpload = null;
        await a.S.RecoverAsync(default);
        Assert.AreEqual(id, (await FileAt(f, root.Id, "a.txt")).Id);
        Assert.IsFalse(a.S.Pending);
    }
    [TestMethod]
    public async Task ConcurrentTargetAfterArchiveIsNeverReplacedOrRolledBackOver()
    {
        using var f = new Fixture(); var a = f.Device("A");
        var path = Path.Combine(a.P.Source, "a.txt");
        File.WriteAllText(path, "old"); await Apply(a.S);
        var root = await FileAt(f, "drive-root", "Documents");
        File.WriteAllText(path, "new");
        f.Drive.BeforeMove = (item, _, _) =>
        {
            if (item.Name == "a.txt" && item.ParentId == root.Id)
                f.Drive.BeforeUpload = (name, _) =>
                {
                    if (name == "a.txt") f.Drive.Add(root.Id, name, Encoding.UTF8.GetBytes("another device"));
                    return Task.CompletedTask;
                };
        };
        await Assert.ThrowsExactlyAsync<IOException>(() => Apply(a.S));
        Assert.AreEqual("another device", Contents(f, await FileAt(f, root.Id, "a.txt")));
        Assert.IsTrue(a.S.Pending);
        Assert.IsTrue(f.Drive.Items.Values.Any(v => Encoding.UTF8.GetString(v.Bytes) == "old"));
        Assert.AreEqual("new", File.ReadAllText(path));
    }
    [TestMethod]
    public async Task OldTransactionFormatIsRejectedWithoutChangingEvidence()
    {
        using var f = new Fixture(); var a = f.Device("A");
        File.WriteAllText(Path.Combine(a.P.Source, "a.txt"), "new");
        f.Drive.BeforeUpload = (name, _) => name == "a.txt" ? throw new IOException("Fail.") : Task.CompletedTask;
        await Assert.ThrowsExactlyAsync<IOException>(() => Apply(a.S));
        var path = Journal(f);
        var node = System.Text.Json.Nodes.JsonNode.Parse(File.ReadAllText(path))!;
        node["schemaVersion"] = 2;
        File.WriteAllText(path, node.ToJsonString());
        var bytes = File.ReadAllBytes(path);
        var count = f.Drive.Items.Count;
        await Assert.ThrowsExactlyAsync<IOException>(() => a.S.RecoverAsync(default));
        CollectionAssert.AreEqual(bytes, File.ReadAllBytes(path));
        Assert.HasCount(count, f.Drive.Items);
    }
    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task InterruptedArchiveRestoresOldFileWhenSourceChangesBeforeRecovery(bool lostArchiveReceipt)
    {
        using var f = new Fixture(); var a = f.Device("A");
        var path = Path.Combine(a.P.Source, "a.txt");
        File.WriteAllText(path, "old"); await Apply(a.S);
        File.WriteAllText(path, "new");
        f.Drive.FailCopies = true;
        f.Drive.BeforeUpload = (name, _) => name == "a.txt" ? throw new IOException("Network down.") : Task.CompletedTask;
        await Assert.ThrowsExactlyAsync<IOException>(() => Apply(a.S));
        Assert.IsTrue(a.S.Pending);
        if (lostArchiveReceipt)
        {
            var journal = Journal(f);
            var pending = System.Text.Json.Nodes.JsonNode.Parse(File.ReadAllText(journal))!;
            pending["phase"] = "Prepared"; pending["archived"] = null;
            File.WriteAllText(journal, pending.ToJsonString());
        }
        File.WriteAllText(path, "edited after interruption");
        f.Drive.FailCopies = false; f.Drive.BeforeUpload = null;
        if (lostArchiveReceipt) await Assert.ThrowsExactlyAsync<IOException>(() => a.S.RecoverAsync(default));
        else await a.S.RecoverAsync(default);
        var root = await FileAt(f, "drive-root", "Documents");
        Assert.AreEqual("old", Contents(f, await FileAt(f, root.Id, "a.txt")));
        Assert.AreEqual("edited after interruption", File.ReadAllText(path));
        Assert.IsFalse(a.S.Pending);
    }
    [TestMethod]
    public async Task KeepLocalHistoryUsesReadablePathAndReplacesOnlyVerifiedPredecessor()
    {
        using var f = new Fixture(); var a = f.Device("A");
        var account = f.Account with { ArchiveRetention = LocalRetention.KeepLocal };
        var session = new CloudBackupSession(a.P, a.C with { CloudAccount = account }, account, f.Drive);
        var path = Path.Combine(a.P.Source, "a.txt");
        foreach (var version in new[] { "first", "second", "third" })
        { File.WriteAllText(path, version); await Apply(session); }
        var data = DataPaths.FromState(a.C.Preview.StateDirectory);
        Assert.AreEqual("second", File.ReadAllText(Path.Combine(data.Archives, "Documents", "a.txt")));
        Assert.HasCount(1, Directory.GetFiles(data.Archives, "*", SearchOption.AllDirectories));
    }
    [TestMethod]
    public async Task PullingOldCommonBaselineDoesNotDowngradeSharedHistory()
    {
        using var f = new Fixture(); var a = f.Device("A"); var b = f.Device("B");
        var path = Path.Combine(a.P.Source, "a.txt");
        File.WriteAllText(path, "first"); await Apply(a.S); await Apply(b.S, SyncDirection.Pull);
        foreach (var version in new[] { "second", "third" }) { File.WriteAllText(path, version); await Apply(a.S); }
        await Apply(b.S, SyncDirection.Pull);
        var repo = new CloudRepository(f.Drive, f.Account, DataPaths.FromState(a.C.Preview.StateDirectory));
        var history = (await repo.FolderAsync("archives/Documents", false, default))!;
        Assert.AreEqual("second", Contents(f, await FileAt(f, history.Id, "a.txt")));
        Assert.AreEqual("third", File.ReadAllText(Path.Combine(b.P.Source, "a.txt")));
    }
    [TestMethod]
    public async Task LostConflictUploadResponseCanRecoverWithoutReplacingLiveFile()
    {
        using var f = new Fixture(); var a = f.Device("A"); var b = f.Device("B");
        File.WriteAllText(Path.Combine(a.P.Source, "a.txt"), "base");
        await Apply(a.S); await Apply(b.S, SyncDirection.Pull);
        File.WriteAllText(Path.Combine(b.P.Source, "a.txt"), "B edit");
        var old = await b.S.ReadAsync(false, default); var file = old.Snapshot.Files.Single();
        File.WriteAllText(Path.Combine(a.P.Source, "a.txt"), "A edit"); await Apply(a.S);
        f.Drive.AfterUpload = item => item.Name.StartsWith("a.", StringComparison.Ordinal) && item.Name.EndsWith(".txt", StringComparison.Ordinal)
            ? throw new IOException("Lost conflict response.") : Task.CompletedTask;
        await Assert.ThrowsExactlyAsync<IOException>(() => b.S.ApplyAsync(file, old.Heads["a.txt"], SyncDirection.Backup,
            b.S.Decide(file, old.Heads["a.txt"], SyncDirection.Backup), default));
        Assert.IsTrue(b.S.Pending);
        f.Drive.AfterUpload = null;
        await b.S.RecoverAsync(default);
        Assert.IsFalse(b.S.Pending);
        var root = await FileAt(f, "drive-root", "Documents");
        Assert.AreEqual("A edit", Contents(f, await FileAt(f, root.Id, "a.txt")));
        Assert.IsTrue(f.Drive.Items.Values.Any(v => Encoding.UTF8.GetString(v.Bytes) == "B edit"));
    }
    [TestMethod]
    public async Task WorkerRefillsWhileTheOtherUploadIsStillWaiting()
    {
        using var f = new Fixture(); var a = f.Device("A");
        foreach (var name in new[] { "a.txt", "b.txt", "c.txt" }) File.WriteAllText(Path.Combine(a.P.Source, name), name);
        var held = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var third = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        f.Drive.BeforeUpload = async (name, token) =>
        {
            if (name == "a.txt") await held.Task.WaitAsync(token);
            if (name == "c.txt") third.TrySetResult();
        };
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        var running = new CloudBackupRuntime(a.C, () => f.Drive).ExecuteAsync(SyncDirection.Backup, null, false, false, null, timeout.Token);
        try { await third.Task.WaitAsync(TimeSpan.FromSeconds(5)); Assert.IsFalse(held.Task.IsCompleted); }
        finally { held.TrySetResult(); }
        Assert.AreEqual(0, (await running).ExitCode);
    }
}
