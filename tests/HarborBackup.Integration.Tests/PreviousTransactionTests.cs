using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using HarborBackup.Core.Domain;
using HarborBackup.OneDrive.Transport;
using HarborBackup.Windows.Integration;
using HarborBackup.Windows.Persistence;
using static HarborBackup.Windows.Tests.CloudTransportTests;

namespace HarborBackup.Windows.Tests;

[TestClass]
[DoNotParallelize]
public sealed class PreviousTransactionTests
{
    [TestMethod]
    [DataRow("Prepared")]
    [DataRow("Staged")]
    [DataRow("Archived")]
    [DataRow("Published")]
    [DataRow("LostArchiveResponse")]
    [DataRow("LostRestoreResponse")]
    public async Task PreviousUploadRetainsEvidenceAndReleasesOnlyVerifiedFiles(string phase)
    {
        using var f = new Fixture(); var a = f.Device("A");
        File.WriteAllText(Path.Combine(a.P.Source, "a.txt"), "original");
        await Apply(a.S);
        var read = await a.S.ReadAsync(false, default);
        var remote = read.Heads["a.txt"].Single();
        var expected = await f.Drive.GetAsync(remote.ItemId, default);
        var parent = expected.ParentId!;
        File.WriteAllText(Path.Combine(a.P.Source, "a.txt"), "updated");
        var local = new FileVersion(DeviceIdentity.Digest("updated"), 7, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow);
        var id = Guid.NewGuid().ToString("N");
        var repo = new CloudRepository(f.Drive, f.Account, DataPaths.FromState(a.C.Preview.StateDirectory));
        RemoteItem? staged = null, archived = null, archive = null;
        if (phase != "Prepared")
        {
            var folder = (await repo.FolderAsync("conflicts/" + remote.Project + "/" + id + "/content", true, default))!;
            staged = f.Drive.Add(folder.Id, "a.txt", Encoding.UTF8.GetBytes("updated"));
        }
        if (phase is "Archived" or "Published" or "LostArchiveResponse" or "LostRestoreResponse")
        {
            archive = (await repo.FolderAsync("archives/" + remote.Project + "/" + id, true, default))!;
            var content = await f.Drive.FolderAsync(archive.Id, "content", default);
            archived = await f.Drive.MoveAsync(expected, content.Id, "a.txt", null, default);
        }
        if (phase == "Published") await f.Drive.MoveAsync(staged!, parent, "a.txt", null, default);
        var path = WriteJournal(a.P, a.C, new
        {
            SchemaVersion = 2, Id = id, Binding = CloudMapping.Bind(a.P, a.C), Path = "a.txt", Direction = SyncDirection.Backup,
            Local = local, Remote = remote, Expected = expected, Publish = true, Protection = ProtectionKind.Archive, Parent = parent,
            Staged = staged, Archived = phase == "LostArchiveResponse" ? null : archived,
            ArchiveFolder = phase == "LostArchiveResponse" ? null : archive?.Id,
            Phase = phase.StartsWith("Lost") ? "Staged" : phase, QuickXor = staged?.QuickXorHash, CreatedUtc = DateTimeOffset.UtcNow
        }, id);
        var before = File.ReadAllBytes(path);
        var runtime = new CloudBackupRuntime(a.C, () => f.Drive);
        var preview = await runtime.PreviewAsync(SyncDirection.Backup, "Documents", false, default, prepareUpload: true);
        Assert.AreEqual("PendingFileOperation", preview.Projects.Single().Plans.Single().Decision.Reason);
        CollectionAssert.AreEqual(before, File.ReadAllBytes(path));
        if (phase == "LostRestoreResponse")
        {
            f.Drive.LoseNextMoveResponse = true;
            Assert.AreNotEqual(0, (await runtime.ExecuteAsync(SyncDirection.Backup, null, false, true, null, default)).ExitCode);
            CollectionAssert.AreEqual(before, File.ReadAllBytes(path));
        }
        var result = await runtime.ExecuteAsync(SyncDirection.Backup, null, false, true, null, default);
        Assert.AreEqual("PreviousOperationClosed", result.Projects.Single().Files.Single().Error, JsonSerializer.Serialize(result));
        Assert.IsFalse(File.Exists(path));
        CollectionAssert.AreEqual(before, File.ReadAllBytes(Path.Combine(CloudMapping.StateRoot(a.P, a.C), "retained-transactions", id + ".json")));
        var live = (await f.Drive.FindAsync(parent, "a.txt", default))!;
        Assert.AreEqual(phase == "Published" ? "updated" : "original", Encoding.UTF8.GetString(f.Drive.Items[live.Id].Bytes));
        Assert.AreEqual("updated", File.ReadAllText(Path.Combine(a.P.Source, "a.txt")));
        if (staged is not null) Assert.IsTrue(f.Drive.Items.ContainsKey(staged.Id));
        var next = await runtime.PreviewAsync(SyncDirection.Backup, "Documents", false, default, prepareUpload: true);
        Assert.HasCount(1, next.UploadSelection!.Files);
        Assert.AreEqual(0, (await runtime.ExecuteAsync(SyncDirection.Backup, "Documents", false, false, null, default, confirmedUpload: next.UploadSelection)).ExitCode);
    }

    [TestMethod]
    [DataRow(2)]
    [DataRow(3)]
    public async Task PreviousFormatsDoNotBlockUnrelatedFilesOrProjects(int schema)
    {
        using var f = new Fixture(); var a = f.Device("A");
        var parent = await f.Drive.FolderAsync("drive-root", "Documents", default);
        var id = Guid.NewGuid().ToString("N");
        var path = WriteJournal(a.P, a.C, new { SchemaVersion = schema, Id = id, Binding = CloudMapping.Bind(a.P, a.C),
            Path = "pending.txt", Direction = SyncDirection.Backup, Publish = true, Parent = parent.Id, Phase = "Prepared" }, id);
        var before = File.ReadAllBytes(path);
        File.WriteAllText(Path.Combine(a.P.Source, "pending.txt"), "pending");
        File.WriteAllText(Path.Combine(a.P.Source, "available.txt"), "available");
        var other = a.P with { Destination = "Other", Source = Path.Combine(f.Root, "other"), TaskId = DeviceIdentity.Digest("other") };
        Directory.CreateDirectory(other.Source); File.WriteAllText(Path.Combine(other.Source, "other.txt"), "other");
        var config = a.C with { Projects = [a.P, other] };
        var result = await new CloudBackupRuntime(config, () => f.Drive).ExecuteAsync(SyncDirection.Backup, null, false, false, null, default);
        Assert.HasCount(2, result.Projects);
        Assert.IsTrue(result.Projects[0].Files.Single(r => r.Path == "available.txt").Changed);
        Assert.IsTrue(result.Projects[1].Files.Single().Changed);
        Assert.AreEqual("PendingFileOperation", result.Projects[0].Files.Single(r => r.Path == "pending.txt").Error);
        CollectionAssert.AreEqual(before, File.ReadAllBytes(path));
    }

    [TestMethod]
    [DataRow("invalid-json")]
    [DataRow("unknown-format")]
    [DataRow("missing-mapping")]
    public async Task UnverifiableStateProducesRecordedFailureWithDetails(string kind)
    {
        using var f = new Fixture(); var a = f.Device("A"); var id = Guid.NewGuid().ToString("N");
        var path = WriteJournal(a.P, a.C, new { SchemaVersion = kind == "unknown-format" ? 99 : 2, Id = id,
            Binding = kind == "missing-mapping" ? new string('0', 64) : CloudMapping.Bind(a.P, a.C), Path = "a.txt" }, id);
        if (kind == "invalid-json") File.WriteAllText(path, "broken");
        var before = File.ReadAllBytes(path);
        var result = await new CloudBackupRuntime(a.C, () => f.Drive).ExecuteAsync(SyncDirection.Backup, null, false, false, null, default);
        Assert.AreEqual(1, result.ExitCode);
        StringAssert.Contains(result.Projects.Single().Issues.Single().Message, "恢复记录未通过核验");
        Assert.IsNotNull(RunStatusStore.Read(a.C.Preview)?["lastRun"]);
        Assert.HasCount(1, RunHistory.Read(a.C.HistoryDirectory));
        Assert.IsEmpty(f.Drive.UploadIntents);
        CollectionAssert.AreEqual(before, File.ReadAllBytes(path));
    }

    [TestMethod]
    [DataRow("stage-changed")]
    [DataRow("cloud-changed")]
    [DataRow("wrong-parent")]
    [DataRow("unsafe-path")]
    public async Task UnsafePreviousRecoveryRetainsAllEvidence(string change)
    {
        using var f = new Fixture(); var a = f.Device("A");
        var parent = await f.Drive.FolderAsync("drive-root", "Documents", default);
        var id = Guid.NewGuid().ToString("N");
        var repo = new CloudRepository(f.Drive, f.Account, DataPaths.FromState(a.C.Preview.StateDirectory));
        var stageFolder = (await repo.FolderAsync("conflicts/" + a.P.ProjectId + "/" + id + "/content", true, default))!;
        var staged = f.Drive.Add(stageFolder.Id, "a.txt", Encoding.UTF8.GetBytes("old"));
        var local = new FileVersion(DeviceIdentity.Digest("old"), 3, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow);
        var path = WriteJournal(a.P, a.C, new { SchemaVersion = 2, Id = id, Binding = CloudMapping.Bind(a.P, a.C),
            Path = change == "unsafe-path" ? "../outside.txt" : "a.txt", Direction = SyncDirection.Backup, Local = local,
            Staged = staged, QuickXor = staged.QuickXorHash, Publish = true, Parent = change == "wrong-parent" ? "root" : parent.Id, Phase = "Staged" }, id);
        if (change == "stage-changed")
        {
            var stored = f.Drive.Items[staged.Id];
            f.Drive.Items[staged.Id] = (staged with { QuickXorHash = "changed" }, stored.Parent, Encoding.UTF8.GetBytes("new"));
        }
        if (change == "cloud-changed") f.Drive.Add(parent.Id, "a.txt", Encoding.UTF8.GetBytes("concurrent"));
        var before = File.ReadAllBytes(path);
        var itemCount = f.Drive.Items.Count;
        await Assert.ThrowsAsync<Exception>(() => a.S.RecoverAsync(default));
        CollectionAssert.AreEqual(before, File.ReadAllBytes(path));
        Assert.HasCount(itemCount, f.Drive.Items);
        Assert.IsEmpty(f.Drive.UploadIntents);
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task PreviousPullClosesWithoutWritingEitherBusinessSide(bool replaced)
    {
        using var f = new Fixture(); var a = f.Device("A");
        File.WriteAllText(Path.Combine(a.P.Source, "a.txt"), "cloud"); await Apply(a.S);
        var read = await a.S.ReadAsync(false, default); var remote = read.Heads["a.txt"].Single();
        var expected = await f.Drive.GetAsync(remote.ItemId, default);
        var id = Guid.NewGuid().ToString("N");
        var repo = new CloudRepository(f.Drive, f.Account, DataPaths.FromState(a.C.Preview.StateDirectory));
        var archive = (await repo.FolderAsync("archives/" + remote.Project + "/" + id, true, default))!;
        var content = await f.Drive.FolderAsync(archive.Id, "content", default);
        var staged = f.Drive.Add(content.Id, "a.txt", Encoding.UTF8.GetBytes("local"));
        File.WriteAllText(Path.Combine(a.P.Source, "a.txt"), replaced ? "cloud" : "local");
        var rollback = Path.Combine(a.P.Source, ".harborbackup-" + id + ".tmp.previous");
        File.WriteAllText(rollback, "local");
        var path = WriteJournal(a.P, a.C, new { SchemaVersion = 2, Id = id, Binding = CloudMapping.Bind(a.P, a.C), Path = "a.txt",
            Direction = SyncDirection.Pull, Local = new FileVersion(DeviceIdentity.Digest("local"), 5, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow),
            Remote = remote, Expected = expected, Publish = true, Protection = ProtectionKind.Archive, Parent = expected.ParentId,
            Staged = staged, ArchiveFolder = archive.Id, Phase = "Staged", QuickXor = staged.QuickXorHash }, id);
        var cloud = f.Drive.Items.ToDictionary(p => p.Key, p => p.Value);
        await a.S.RecoverAsync(default);
        Assert.IsFalse(File.Exists(path));
        CollectionAssert.AreEquivalent(cloud.ToArray(), f.Drive.Items.ToArray());
        Assert.AreEqual(replaced ? "cloud" : "local", File.ReadAllText(Path.Combine(a.P.Source, "a.txt")));
        Assert.AreEqual("local", File.ReadAllText(rollback));
    }

    [TestMethod]
    public async Task PreviousRecoveryUsesRetiredMappingAndOtherDeviceEvidenceIsUntouched()
    {
        using var f = new Fixture(); var a = f.Device("A");
        var parent = await f.Drive.FolderAsync("drive-root", "Documents", default);
        var id = Guid.NewGuid().ToString("N");
        var path = WriteJournal(a.P, a.C, new { SchemaVersion = 2, Id = id, Binding = CloudMapping.Bind(a.P, a.C), Path = "a.txt",
            Direction = SyncDirection.Backup, Publish = true, Parent = parent.Id, Phase = "Prepared" }, id);
        var next = a.P with { Source = Path.Combine(f.Root, "new-source"), MappingVersion = Guid.NewGuid().ToString("N") };
        var config = a.C with { Projects = [next], RetiredMappings = [CloudMapping.Capture(a.P, a.C)] };
        var foreign = Path.Combine(a.C.Preview.StateDirectory, "devices", DeviceIdentity.Digest("B"), "cloud", "foreign", "cloud-transactions", "old.json");
        Directory.CreateDirectory(Path.GetDirectoryName(foreign)!); File.WriteAllText(foreign, "{\"schemaVersion\":1}");
        var report = await new CloudBackupRuntime(config, () => f.Drive).ExecuteAsync(SyncDirection.Backup, null, false, true, null, default);
        Assert.IsFalse(File.Exists(path), JsonSerializer.Serialize(report));
        Assert.AreEqual("{\"schemaVersion\":1}", File.ReadAllText(foreign));
        Assert.IsFalse(Directory.Exists(next.Source));
        Assert.IsTrue(CloudMapping.NeedsFirstOperation(next, config));
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task PreparedPullWithNoLocalOrIncomingRecordCanBeClosedAndPulledAgain(bool partialDownload)
    {
        // Match the reported v2 field shape, using synthetic identities and file contents.
        using var f = new Fixture(); var a = f.Device("A"); var b = f.Device("B");
        File.WriteAllText(Path.Combine(a.P.Source, "sample.docx"), "cloud document"); await Apply(a.S);
        var read = await a.S.ReadAsync(false, default);
        var remote = read.Heads["sample.docx"].Single();
        var expected = await f.Drive.GetAsync(remote.ItemId, default);
        var id = Guid.NewGuid().ToString("N");
        var path = WriteJournal(b.P, b.C, new
        {
            SchemaVersion = 2, Id = id, Binding = CloudMapping.Bind(b.P, b.C), Path = "sample.docx", Direction = SyncDirection.Pull,
            Local = (FileVersion?)null, Remote = remote, Expected = expected, Publish = true, Protection = ProtectionKind.None,
            Parent = expected.ParentId, Session = (string?)null, Staged = (RemoteItem?)null, Archived = (RemoteItem?)null,
            ArchiveFolder = (string?)null, PreviousArchive = (string?)null, Committed = (CloudFileMetadata?)null,
            Phase = "Prepared", QuickXor = (string?)null, CreatedUtc = DateTimeOffset.UtcNow, Incoming = (FileVersion?)null,
            PreviousArchives = Array.Empty<string>()
        }, id);
        var temporary = Path.Combine(b.P.Source, ".harborbackup-" + id + ".tmp");
        if (partialDownload) File.WriteAllText(temporary, "partial bytes");
        var before = File.ReadAllBytes(path);
        var cloudBefore = f.Drive.Items.ToDictionary(p => p.Key, p => p.Value);
        var downloadsBefore = f.Drive.BusinessDownloads;
        await b.S.RecoverAsync(default);
        CollectionAssert.AreEquivalent(cloudBefore.ToArray(), f.Drive.Items.ToArray());
        Assert.AreEqual(downloadsBefore, f.Drive.BusinessDownloads);
        Assert.IsFalse(File.Exists(Path.Combine(b.P.Source, "sample.docx")));
        Assert.IsFalse(File.Exists(path));
        CollectionAssert.AreEqual(before, File.ReadAllBytes(Path.Combine(CloudMapping.StateRoot(b.P, b.C), "retained-transactions", id + ".json")));
        if (partialDownload) Assert.AreEqual("partial bytes", File.ReadAllText(temporary));
        var runtime = new CloudBackupRuntime(b.C, () => f.Drive);
        var preview = await runtime.PreviewAsync(SyncDirection.Pull, "Documents", false, default);
        var selection = preview.PullSelection! with { Files = [preview.PullSelection!.Files.Single(p => p.Path == "sample.docx")] };
        var result = await runtime.ExecuteAsync(SyncDirection.Pull, "Documents", false, false, null, default, selection);
        Assert.AreEqual(0, result.ExitCode, JsonSerializer.Serialize(result));
        Assert.AreEqual("cloud document", File.ReadAllText(Path.Combine(b.P.Source, "sample.docx")));
        Assert.AreEqual("cloud document", Encoding.UTF8.GetString(f.Drive.Items[expected.Id].Bytes));
        if (partialDownload) Assert.AreEqual("partial bytes", File.ReadAllText(temporary));
    }

    private static string WriteJournal(ProjectDefinition p, ConfigurationSnapshot c, object value, string id)
    {
        var path = Path.Combine(CloudMapping.StateRoot(p, c), "cloud-transactions", id + ".json");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, JsonSerializer.Serialize(value, AtomicJson.Options));
        return path;
    }
}
