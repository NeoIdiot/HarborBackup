using HarborBackup.Core.Domain;
using HarborBackup.Windows.Integration;
using static HarborBackup.Windows.Tests.CloudTransportTests;

namespace HarborBackup.Windows.Tests;

[TestClass]
[DoNotParallelize]
public sealed class CloudUploadFastPathTests
{
    [TestMethod]
    [DataRow(SyncDirection.Backup)]
    [DataRow(SyncDirection.Pull)]
    public async Task EqualContentCannotEstablishBaselineAfterSameMetadataSourceChange(SyncDirection direction)
    {
        using var f = new Fixture(); var a = f.Device("A"); var b = f.Device("B");
        File.WriteAllText(Path.Combine(a.P.Source, "a.txt"), "base"); await Apply(a.S);
        var path = Path.Combine(b.P.Source, "a.txt"); File.WriteAllText(path, "base");
        var read = await b.S.ReadAsync(false, default); var file = read.Snapshot.Files.Single();
        Assert.IsNull(file.Baseline);
        var heads = read.Heads["a.txt"]; var decision = b.S.Decide(file, heads, direction);
        Assert.AreEqual(FileAction.Equal, decision.Action);
        var stamp = File.GetLastWriteTimeUtc(path); File.WriteAllText(path, "edit"); File.SetLastWriteTimeUtc(path, stamp);
        var writes = f.Drive.UploadIntents.Count;
        await Assert.ThrowsExactlyAsync<IOException>(() => b.S.ApplyAsync(file, heads, direction, decision, default));
        var after = await b.S.ReadAsync(true, default);
        Assert.IsNull(after.Snapshot.Files.Single().Baseline);
        Assert.AreEqual("edit", File.ReadAllText(path));
        Assert.HasCount(writes, f.Drive.UploadIntents);
    }

    [TestMethod]
    public async Task NewFileUsesOneHashPassAndNoPerFileLookup()
    {
        using var f = new Fixture(); var a = f.Device("A");
        Directory.CreateDirectory(Path.Combine(a.P.Source, "nested", "sub"));
        File.WriteAllText(Path.Combine(a.P.Source, "nested", "sub", "a.txt"), "payload");
        var report = await new CloudBackupRuntime(a.C, () => f.Drive).ExecuteAsync(SyncDirection.Backup, null, false, false, null, default);
        Assert.AreEqual(0, report.ExitCode, string.Join("\n", report.Projects.Single().Files.Select(v => v.Error)));
        var metrics = report.Projects.Single().Metrics;
        Assert.AreEqual(1L, metrics.HashedFiles);
        Assert.AreEqual(7L, metrics.HashBytes);
        Assert.AreEqual(1L, metrics.UploadHashPasses);
        Assert.AreEqual(7L, metrics.UploadHashBytes);
        Assert.AreEqual(4L, metrics.LocalJournalWrites, "Prepared checksums must not require a separate journal rewrite.");
        Assert.IsFalse(f.Drive.Lookups.Any(v => v.Name == "a.txt"), "Fresh file creation must rely on conditional write, not lookup.");
        Assert.IsTrue(f.Drive.UploadIntents.Any(v => v.Name == "a.txt" && !v.Reconcile));
        Assert.IsFalse(a.S.Pending);
    }
    [TestMethod]
    public async Task ScannedDirectoriesAreReusedAndUnchangedFilesStayCached()
    {
        using var f = new Fixture(); var a = f.Device("A");
        Directory.CreateDirectory(Path.Combine(a.P.Source, "nested", "sub"));
        File.WriteAllText(Path.Combine(a.P.Source, "nested", "sub", "a.txt"), "same");
        var runtime = new CloudBackupRuntime(a.C, () => f.Drive);
        Assert.AreEqual(0, (await runtime.ExecuteAsync(SyncDirection.Backup, null, false, false, null, default)).ExitCode);
        File.WriteAllText(Path.Combine(a.P.Source, "nested", "sub", "b.txt"), "new");
        f.Drive.Lookups.Clear();
        var result = await runtime.ExecuteAsync(SyncDirection.Backup, null, false, false, null, default);
        Assert.AreEqual(0, result.ExitCode);
        Assert.IsFalse(f.Drive.Lookups.Any(v => v.Name is "nested" or "sub" or "b.txt"));
        Assert.AreEqual(1L, result.Projects.Single().Metrics.CachedFiles);
        Assert.AreEqual(1L, result.Projects.Single().Metrics.UploadHashPasses);
        Assert.AreEqual(3L, result.Projects.Single().Metrics.HashBytes);
        var unchanged = await runtime.ExecuteAsync(SyncDirection.Backup, null, false, false, null, default);
        Assert.AreEqual(0L, unchanged.Projects.Single().Metrics.UploadHashPasses);
        Assert.AreEqual(0L, unchanged.Projects.Single().Metrics.HashBytes);
    }
    [TestMethod]
    public async Task ChangedTimestampWithEqualContentDoesNotUploadOrArchive()
    {
        using var f = new Fixture(); var a = f.Device("A");
        var path = Path.Combine(a.P.Source, "a.txt"); File.WriteAllText(path, "same");
        var runtime = new CloudBackupRuntime(a.C, () => f.Drive);
        Assert.AreEqual(0, (await runtime.ExecuteAsync(SyncDirection.Backup, null, false, false, null, default)).ExitCode);
        File.SetLastWriteTimeUtc(path, DateTime.UtcNow.AddMinutes(-1));
        var before = f.Drive.UploadIntents.Count;
        var report = await runtime.ExecuteAsync(SyncDirection.Backup, null, false, false, null, default);
        Assert.AreEqual(0, report.ExitCode);
        Assert.AreEqual(FileAction.Equal, report.Projects.Single().Files.Single().Action);
        Assert.AreEqual(1L, report.Projects.Single().Metrics.UploadHashPasses);
        Assert.IsFalse(f.Drive.UploadIntents.Skip(before).Any(v => v.Name == "a.txt"));
        Assert.IsNull(await f.Drive.FindAsync("root", "archives", default));
    }
    [TestMethod]
    public async Task MetadataLostResponseRecoversWithoutAnotherBodyUpload()
    {
        using var f = new Fixture(); var a = f.Device("A");
        File.WriteAllText(Path.Combine(a.P.Source, "a.txt"), "payload");
        f.Drive.FailAfterCommit = true;
        var runtime = new CloudBackupRuntime(a.C, () => f.Drive);
        Assert.AreNotEqual(0, (await runtime.ExecuteAsync(SyncDirection.Backup, null, false, false, null, default)).ExitCode);
        Assert.IsTrue(a.S.Pending);
        var first = f.Drive.UploadIntents.ToArray();
        Assert.HasCount(1, first.Where(v => v.Name == "a.txt"));
        Assert.AreEqual(0, (await runtime.ExecuteAsync(SyncDirection.Backup, null, false, true, null, default)).ExitCode);
        Assert.HasCount(1, f.Drive.UploadIntents.Where(v => v.Name == "a.txt"));
        Assert.IsTrue(f.Drive.UploadIntents.Skip(first.Length).Any(v => v.Reconcile));
        Assert.IsFalse(a.S.Pending);
    }
    [TestMethod]
    public async Task InterruptedCompletionStillChecksLiveCloudBeforeRecordingBaseline()
    {
        using var f = new Fixture(); var a = f.Device("A");
        File.WriteAllText(Path.Combine(a.P.Source, "a.txt"), "payload");
        f.Drive.BeforeUpload = (name, token) => Guid.TryParseExact(Path.GetFileNameWithoutExtension(name), "N", out _)
            ? throw new IOException("Metadata unavailable.") : Task.CompletedTask;
        var runtime = new CloudBackupRuntime(a.C, () => f.Drive);
        Assert.AreNotEqual(0, (await runtime.ExecuteAsync(SyncDirection.Backup, null, false, false, null, default)).ExitCode);
        var path = Directory.GetFiles(f.Root, "*.json", SearchOption.AllDirectories)
            .Single(p => Path.GetFileName(Path.GetDirectoryName(p)) == "cloud-transactions");
        var journal = System.Text.Json.Nodes.JsonNode.Parse(File.ReadAllText(path))!;
        journal["committed"] = null; journal["phase"] = "Uploaded";
        File.WriteAllText(path, journal.ToJsonString());
        var parent = (await f.Drive.FindAsync("drive-root", "Documents", default))!;
        var old = (await f.Drive.FindAsync(parent.Id, "a.txt", default))!;
        await f.Drive.DeleteAsync(old, default);
        f.Drive.Add(parent.Id, "a.txt", System.Text.Encoding.UTF8.GetBytes("other"));
        f.Drive.BeforeUpload = null;
        await Assert.ThrowsExactlyAsync<IOException>(() => a.S.RecoverAsync(default));
        Assert.IsTrue(a.S.Pending);
        Assert.IsFalse(Directory.GetDirectories(f.Root, "baselines", SearchOption.AllDirectories).Any());
        Assert.AreEqual("payload", File.ReadAllText(Path.Combine(a.P.Source, "a.txt")));
    }

    [TestMethod]
    public async Task DeletedScannedParentStopsUploadAndReleasesSource()
    {
        using var f = new Fixture(); var a = f.Device("A");
        Directory.CreateDirectory(Path.Combine(a.P.Source, "nested"));
        await Apply(a.S);
        File.WriteAllText(Path.Combine(a.P.Source, "nested", "a.txt"), "payload");
        var root = (await f.Drive.FindAsync("drive-root", "Documents", default))!;
        var folder = (await f.Drive.FindAsync(root.Id, "nested", default))!;
        f.Drive.BeforeUpload = async (name, token) =>
        { if (name == "a.txt") await f.Drive.DeleteAsync(folder, token); };
        var report = await new CloudBackupRuntime(a.C, () => f.Drive).ExecuteAsync(SyncDirection.Backup, null, false, false, null, default);
        Assert.AreNotEqual(0, report.ExitCode);
        Assert.IsFalse(f.Drive.Items.Values.Any(v => v.Item.Name == "a.txt"));
        using var writer = File.OpenWrite(Path.Combine(a.P.Source, "nested", "a.txt"));
        Assert.IsTrue(writer.CanWrite);
    }
}
