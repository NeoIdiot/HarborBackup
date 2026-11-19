using System.Text;
using System.Text.Json;
using HarborBackup.Core.Domain;
using HarborBackup.OneDrive.Transport;
using HarborBackup.Windows.FileSystem;
using HarborBackup.Windows.Integration;
using HarborBackup.Windows.Persistence;
using static HarborBackup.Windows.Tests.CloudTransportTests;

namespace HarborBackup.Windows.Tests;

[TestClass]
[DoNotParallelize]
public sealed class CloudRestoreTests
{
    private sealed class ChangedAfterDownloadDrive : FakeDrive, ICloudDrive
    {
        public bool ChangeBusinessVersion;
        async Task ICloudDrive.DownloadAsync(RemoteItem item, Stream destination, CancellationToken token)
        {
            await base.DownloadAsync(item, destination, token);
            if (ChangeBusinessVersion && item.Name == "a.txt")
            {
                var original = Items[item.Id];
                Items[item.Id] = (original.Item with { ETag = "changed-after-download" }, original.Parent, original.Bytes);
            }
        }
    }
    [TestMethod]
    public async Task CurrentAndPreviousArchiveRestoreIntoSeparateDirectoryWithoutChangingBusinessOrBaseline()
    {
        using var f = new Fixture(); var a = f.Device("A");
        var source = Path.Combine(a.P.Source, "nested", "a.txt"); Directory.CreateDirectory(Path.GetDirectoryName(source)!);
        File.WriteAllText(source, "first"); await Apply(a.S);
        File.WriteAllText(source, "second"); await Apply(a.S);
        var cloud = f.Drive.Items.ToDictionary(p => p.Key, p => (p.Value.Item, Convert.ToHexString(p.Value.Bytes)));
        var state = StateFiles(a.C.Preview.StateDirectory);
        var service = new CloudRestoreService(a.C, () => f.Drive);
        var catalog = await service.ListAsync(a.P.ProjectId, null, "nested", default);
        Assert.HasCount(2, catalog.Candidates);
        var report = await service.ExecuteAsync(catalog.Candidates, Path.Combine(f.Root, "restore"), false, 0, 1, default);
        Assert.AreEqual(0, report.ExitCode); Assert.AreEqual(2, report.VerifiedFiles);
        Assert.AreEqual("second", File.ReadAllText(report.Files.Single(r => r.Scope == RestoreScope.Current).OutputPath!));
        Assert.AreEqual("first", File.ReadAllText(report.Files.Single(r => r.Scope == RestoreScope.Archive).OutputPath!));
        Assert.AreEqual("second", File.ReadAllText(source));
        CollectionAssert.AreEquivalent(cloud.Keys.ToArray(), f.Drive.Items.Keys.ToArray());
        foreach (var pair in cloud) { Assert.AreEqual(pair.Value.Item, f.Drive.Items[pair.Key].Item); Assert.AreEqual(pair.Value.Item2, Convert.ToHexString(f.Drive.Items[pair.Key].Bytes)); }
        foreach (var pair in state) Assert.AreEqual(pair.Value, Convert.ToHexString(File.ReadAllBytes(pair.Key)));
        var verified = JsonSerializer.Deserialize<RestoreVerificationState>(File.ReadAllText(CloudRestoreService.VerificationPath(a.C, a.P)), AtomicJson.Options)!;
        Assert.AreEqual(CloudMapping.Bind(a.P, a.C), verified.Binding); Assert.AreEqual(a.P.ProjectId, verified.ProjectId);
        Assert.IsTrue(Directory.EnumerateFiles(report.OutputRoot, "restore-report-*.json").Any());
    }

    [TestMethod]
    public async Task UnknownCloudContentIsExplicitlyUnverifiableAndNotDownloaded()
    {
        using var f = new Fixture(); var a = f.Device("A");
        var root = await f.Drive.FolderAsync("drive-root", "Documents", default);
        f.Drive.Add(root.Id, "unknown.txt", Encoding.UTF8.GetBytes("unknown"));
        var service = new CloudRestoreService(a.C, () => f.Drive);
        var catalog = await service.ListAsync(a.P.ProjectId, RestoreScope.Current, null, default);
        Assert.IsNull(catalog.Candidates.Single().Sha256);
        var report = await service.ExecuteAsync(catalog.Candidates, Path.Combine(f.Root, "restore"), false, 0, 1, default);
        Assert.AreEqual(2, report.ExitCode); Assert.AreEqual(RestoreFileStatus.EvidenceInsufficient, report.Files.Single().Status);
        Assert.AreEqual(0, f.Drive.BusinessDownloads); Assert.IsFalse(File.Exists(CloudRestoreService.VerificationPath(a.C, a.P)));
    }

    [TestMethod]
    public async Task StaleVersionExistingOutputAndChecksumCorruptionNeverPublishRestoreFile()
    {
        using var f = new Fixture(); var a = f.Device("A");
        File.WriteAllText(Path.Combine(a.P.Source, "a.txt"), "payload"); await Apply(a.S);
        var service = new CloudRestoreService(a.C, () => f.Drive);
        var candidate = (await service.ListAsync(a.P.ProjectId, RestoreScope.Current, null, default)).Candidates.Single();
        var original = f.Drive.Items[candidate.ItemId];
        f.Drive.Items[candidate.ItemId] = (original.Item with { ETag = "changed" }, original.Parent, original.Bytes);
        var stale = await service.ExecuteAsync([candidate], Path.Combine(f.Root, "stale"), false, 0, 1, default);
        Assert.AreEqual(RestoreFileStatus.Failed, stale.Files.Single().Status);
        Assert.AreEqual(0, f.Drive.BusinessDownloads);
        f.Drive.Items[candidate.ItemId] = original;
        var output = Path.Combine(f.Root, "existing");
        var target = Path.Combine(output, "current", a.P.ProjectId, "a.txt"); Directory.CreateDirectory(Path.GetDirectoryName(target)!); File.WriteAllText(target, "keep");
        var existing = await service.ExecuteAsync([candidate], output, false, 0, 1, default);
        Assert.AreEqual(RestoreFileStatus.Failed, existing.Files.Single().Status); Assert.AreEqual("keep", File.ReadAllText(target));
        f.Drive.Items[candidate.ItemId] = (original.Item, original.Parent, Encoding.UTF8.GetBytes("corrupt"));
        var corrupt = await service.ExecuteAsync([candidate], Path.Combine(f.Root, "corrupt"), false, 0, 1, default);
        Assert.AreEqual(RestoreFileStatus.Failed, corrupt.Files.Single().Status);
        Assert.IsFalse(Directory.EnumerateFiles(corrupt.OutputRoot, "a.txt", SearchOption.AllDirectories).Any());
        Assert.IsFalse(Directory.EnumerateFiles(corrupt.OutputRoot, "*.restore-part", SearchOption.AllDirectories).Any());
        Assert.IsFalse(File.Exists(CloudRestoreService.VerificationPath(a.C, a.P)));
    }

    [TestMethod]
    public async Task OutputRejectsEverySourceDataApplicationOverlapAndTraversal()
    {
        using var f = new Fixture(); var a = f.Device("A");
        File.WriteAllText(Path.Combine(a.P.Source, "a.txt"), "payload"); await Apply(a.S);
        var service = new CloudRestoreService(a.C, () => f.Drive);
        var candidate = (await service.ListAsync(a.P.ProjectId, RestoreScope.Current, null, default)).Candidates.Single();
        foreach (var output in new[] { a.P.Source, Path.GetDirectoryName(a.P.Source)!, Path.Combine(a.P.Source, "restore"),
            DataPaths.FromState(a.C.Preview.StateDirectory).Root, AppContext.BaseDirectory })
            Assert.ThrowsExactly<UnsafePathException>(() => CloudRestoreService.ValidateOutputRoot(a.C, output));
        await Assert.ThrowsExactlyAsync<FormatException>(() => service.ExportAsync(candidate with { Path = "../escape.txt" }, Path.Combine(f.Root, "restore"), default));
    }

    [TestMethod]
    public async Task ByteBudgetRotatesCandidatesAndEveryTestUsesFreshDirectory()
    {
        using var f = new Fixture(); var a = f.Device("A");
        File.WriteAllText(Path.Combine(a.P.Source, "a.txt"), "aaa"); File.WriteAllText(Path.Combine(a.P.Source, "b.txt"), "bbb"); await Apply(a.S);
        var service = new CloudRestoreService(a.C, () => f.Drive);
        var candidates = (await service.ListAsync(a.P.ProjectId, RestoreScope.Current, null, default)).Candidates;
        var output = Path.Combine(f.Root, "tests");
        var first = await service.ExecuteAsync(candidates, output, true, 3, 1, default);
        var second = await service.ExecuteAsync(candidates, output, true, 3, 1, default);
        Assert.AreEqual(3L, first.VerifiedBytes); Assert.AreEqual(3L, second.VerifiedBytes);
        Assert.AreNotEqual(first.Files.Single(r => r.Status == RestoreFileStatus.Verified).Path, second.Files.Single(r => r.Status == RestoreFileStatus.Verified).Path);
        Assert.AreNotEqual(first.OutputRoot, second.OutputRoot);
        Assert.IsTrue(first.Files.Any(r => r.Status == RestoreFileStatus.BudgetSkipped));
        var group0 = await service.ExecuteAsync(candidates, output, true, 0, 2, default);
        var group1 = await service.ExecuteAsync(candidates, output, true, 0, 2, default);
        Assert.AreEqual(0, group0.Group); Assert.AreEqual(1, group1.Group);
        Assert.AreEqual(2, group0.VerifiedFiles + group1.VerifiedFiles);
    }

    [TestMethod]
    public async Task VersionChangeAfterDownloadAndCancellationLeaveNoPublishedFileOrVerificationStatus()
    {
        using var f = new Fixture(); var drive = new ChangedAfterDownloadDrive();
        foreach (var pair in f.Drive.Items) drive.Items[pair.Key] = pair.Value;
        f.Drive = drive;
        var a = f.Device("A"); File.WriteAllText(Path.Combine(a.P.Source, "a.txt"), "payload"); await Apply(a.S);
        var service = new CloudRestoreService(a.C, () => drive);
        var candidate = (await service.ListAsync(a.P.ProjectId, RestoreScope.Current, null, default)).Candidates.Single();
        drive.ChangeBusinessVersion = true;
        var changed = await service.ExecuteAsync([candidate], Path.Combine(f.Root, "changed"), false, 0, 1, default);
        Assert.AreEqual(RestoreFileStatus.Failed, changed.Files.Single().Status);
        Assert.IsFalse(Directory.EnumerateFiles(changed.OutputRoot, "a.txt", SearchOption.AllDirectories).Any());
        Assert.IsFalse(File.Exists(CloudRestoreService.VerificationPath(a.C, a.P)));
        drive.ChangeBusinessVersion = false;
        var current = drive.Items[candidate.ItemId]; drive.Items[candidate.ItemId] = (current.Item with { ETag = candidate.ETag }, current.Parent, current.Bytes);
        using var cancellation = new CancellationTokenSource();
        drive.BeforeDownload = (name, token) => { if (name == "a.txt") { cancellation.Cancel(); token.ThrowIfCancellationRequested(); } return Task.CompletedTask; };
        var cancelled = await service.ExecuteAsync([candidate], Path.Combine(f.Root, "cancelled"), false, 0, 1, cancellation.Token);
        Assert.AreEqual(130, cancelled.ExitCode);
        Assert.IsFalse(Directory.EnumerateFiles(cancelled.OutputRoot, "*.restore-part", SearchOption.AllDirectories).Any());
        Assert.IsFalse(File.Exists(CloudRestoreService.VerificationPath(a.C, a.P)));
    }

    [TestMethod]
    public async Task RotationPrioritizesDeferredFileDespiteOversizedAndSmallTrailingCandidates()
    {
        using var f = new Fixture(); var a = f.Device("A");
        foreach (var entry in new[] { ("a.txt", "aaa"), ("b.txt", "bbbb"), ("c.txt", "cc"), ("z.txt", new string('z', 100)) })
            File.WriteAllText(Path.Combine(a.P.Source, entry.Item1), entry.Item2);
        await Apply(a.S);
        var service = new CloudRestoreService(a.C, () => f.Drive);
        var candidates = (await service.ListAsync(a.P.ProjectId, RestoreScope.Current, null, default)).Candidates;
        var output = Path.Combine(f.Root, "rotation");
        var first = await service.ExecuteAsync(candidates, output, true, 5, 1, default);
        Assert.HasCount(2, first.Files.Where(r => r.Status == RestoreFileStatus.Verified));
        Assert.IsTrue(first.Files.Any(r => r.Path == "b.txt" && r.Status == RestoreFileStatus.BudgetSkipped));
        var second = await service.ExecuteAsync(candidates, output, true, 5, 1, default);
        Assert.IsTrue(second.Files.Any(r => r.Path == "b.txt" && r.Status == RestoreFileStatus.Verified), "The first deferred eligible file must lead the next run.");
        Assert.IsLessThanOrEqualTo(5L, second.VerifiedBytes);
    }

    private static Dictionary<string, string> StateFiles(string root) => Directory.EnumerateFiles(root, "*.json", SearchOption.AllDirectories)
        .ToDictionary(p => p, p => Convert.ToHexString(File.ReadAllBytes(p)));
}
