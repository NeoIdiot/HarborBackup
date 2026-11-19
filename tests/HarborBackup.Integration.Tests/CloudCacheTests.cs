using HarborBackup.Core.Domain;
using HarborBackup.OneDrive.Transport;
using HarborBackup.Windows.Integration;
using static HarborBackup.Windows.Tests.CloudTransportTests;

namespace HarborBackup.Windows.Tests;

[TestClass]
[DoNotParallelize]
public sealed class CloudCacheTests
{
    private sealed class DetailedDrive : FakeDrive, ICloudChanges
    {
        public string Version = "1";
        public CloudChange[] Changes = [];
        public Task<CloudChanges> ChangesAsync(string? cursor, CancellationToken token) =>
            Task.FromResult(new CloudChanges(Version, cursor != Version, Changes, cursor is null));
    }
    [TestMethod]
    [DataRow("unrelated", 1)]
    [DataRow("unrelated-with-root", 1)]
    [DataRow("known-file", 2)]
    [DataRow("moved-in", 2)]
    [DataRow("unknown-delete", 2)]
    [DataRow("ancestor-move", 2)]
    public async Task DetailedDeltaOnlyReusesProvablyUnrelatedChanges(string kind, int expectedScans)
    {
        using var fixture = new Fixture(); var drive = new DetailedDrive();
        var business = drive.Add("drive-root", "project", [], true);
        var metadata = drive.Add("root", "files", [], true);
        var content = drive.Add(business.Id, "a.txt", [1]);
        var record = drive.Add(metadata.Id, "record.json", [2]);
        var snapshot = new CloudInventorySnapshot([new("a.txt", content)], [record], 0, business.Id, metadata.Id,
            [drive.Items["drive-root"].Item, drive.Items["root"].Item, business, metadata]);
        var path = Path.Combine(fixture.Root, "scoped.json"); var scans = 0;
        Task<CloudInventorySnapshot> Scan() { scans++; return Task.FromResult(snapshot); }
        var cache = new CloudInventoryCache(path, "binding", drive);
        await cache.ReadAsync(false, Scan, new(), default);
        var originalTime = System.Text.Json.Nodes.JsonNode.Parse(File.ReadAllText(path))!["createdUtc"]!.ToString();
        drive.Version = "2";
        drive.Changes = [kind switch
        {
            "known-file" => new(content.Id, content.Name, business.Id, false, false),
            "moved-in" => new("new-folder", "folder", business.Id, true, false),
            "unknown-delete" => new("unknown", null, null, false, true),
            "ancestor-move" => new("root", "renamed", "drive-root", true, false),
            _ => new("unrelated", "other.txt", "drive-root", false, false)
        }];
        if (kind == "unrelated-with-root") drive.Changes = [.. drive.Changes, new("drive-root", "OneDrive", null, true, false)];
        await cache.ReadAsync(false, Scan, new(), default);
        Assert.AreEqual(expectedScans, scans);
        if (kind == "unrelated")
        {
            var saved = System.Text.Json.Nodes.JsonNode.Parse(File.ReadAllText(path))!;
            Assert.AreEqual("2", saved["cursor"]!.ToString());
            Assert.AreEqual(originalTime, saved["createdUtc"]!.ToString());
        }
    }
    private sealed class Clock : TimeProvider
    {
        public DateTimeOffset Now = DateTimeOffset.UtcNow;
        public override DateTimeOffset GetUtcNow() => Now;
    }
    private sealed class ChangesDrive : FakeDrive, ICloudChanges
    {
        public string Version = "1";
        public bool Fail;
        public Task<CloudChanges> ChangesAsync(string? cursor, CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            if (Fail) throw new IOException("Delta unavailable.");
            return Task.FromResult(new CloudChanges(Version, cursor != Version));
        }
    }
    private static CloudInventorySnapshot Snapshot() => new([new("a.txt", new("id", "a.txt", "etag", 1, false))], [], 0);

    [TestMethod]
    public async Task CacheRequiresUnchangedCursorAndRebuildsOnExpiryCorruptionOrFullCheck()
    {
        using var fixture = new Fixture(); var clock = new Clock(); var drive = new ChangesDrive();
        var path = Path.Combine(fixture.Root, "inventory.json"); var enumerations = 0;
        var cache = new CloudInventoryCache(path, "binding", drive, clock);
        Task<CloudInventorySnapshot> Scan() { enumerations++; return Task.FromResult(Snapshot()); }
        await cache.ReadAsync(false, Scan, new(), default);
        var hit = new ReadMetrics(); await cache.ReadAsync(false, Scan, hit, default);
        Assert.AreEqual(1, enumerations); Assert.AreEqual(1L, hit.CloudInventoryCacheHits);
        drive.Version = "2"; await cache.ReadAsync(false, Scan, new(), default);
        Assert.AreEqual(2, enumerations);
        clock.Now += TimeSpan.FromHours(6); await cache.ReadAsync(false, Scan, new(), default);
        Assert.AreEqual(3, enumerations);
        await cache.ReadAsync(true, Scan, new(), default); Assert.AreEqual(4, enumerations);
        File.WriteAllText(path, "{"); await cache.ReadAsync(false, Scan, new(), default); Assert.AreEqual(5, enumerations);
        await new CloudInventoryCache(path, "different-policy", drive, clock).ReadAsync(false, Scan, new(), default);
        Assert.AreEqual(6, enumerations);
        drive.Fail = true; await cache.ReadAsync(false, Scan, new(), default); Assert.AreEqual(7, enumerations);
    }

    [TestMethod]
    public async Task ChangesDuringEnumerationCannotBecomeACacheHit()
    {
        using var fixture = new Fixture(); var drive = new ChangesDrive();
        var path = Path.Combine(fixture.Root, "inventory.json");
        var cache = new CloudInventoryCache(path, "binding", drive);
        await cache.ReadAsync(false, () => { drive.Version = "2"; return Task.FromResult(Snapshot()); }, new(), default);
        Assert.IsFalse(File.Exists(path));
        var enumerated = false;
        await cache.ReadAsync(false, () => { enumerated = true; return Task.FromResult(Snapshot()); }, new(), default);
        Assert.IsTrue(enumerated); Assert.IsTrue(File.Exists(path));
    }

    [TestMethod]
    public async Task CachedInventoryHonorsEditedExclusionsAndCloudDeletion()
    {
        using var fixture = new Fixture(true); var device = fixture.Device("A");
        Directory.CreateDirectory(Path.Combine(device.P.Source, "excluded"));
        File.WriteAllText(Path.Combine(device.P.Source, "excluded", "a.txt"), "first");
        await Apply(device.S);
        await device.S.ReadAsync(false, default); var cached = await device.S.ReadAsync(false, default);
        Assert.AreEqual(1L, cached.Snapshot.Metrics.CloudInventoryCacheHits);
        var edited = device.P with { ExcludedDirectories = ["excluded"] };
        var session = new CloudBackupSession(edited, device.C, fixture.Account, fixture.Drive);
        var filtered = await session.ReadAsync(false, default);
        Assert.HasCount(0, filtered.Snapshot.Files);
        Assert.AreEqual(0L, filtered.Snapshot.Metrics.CloudInventoryCacheHits);
        var root = (await fixture.Drive.FindAsync("drive-root", "Documents", default))!;
        var directory = (await fixture.Drive.FindAsync(root.Id, "excluded", default))!;
        await fixture.Drive.DeleteAsync(directory, default);
        var after = await device.S.ReadAsync(false, default);
        Assert.IsTrue(after.Snapshot.Files.All(f => f.Target.Kind == ObservationKind.Missing));
        Assert.IsTrue(File.Exists(Path.Combine(device.P.Source, "excluded", "a.txt")));
    }

    [TestMethod]
    public async Task CachedInventoryNeverAuthorizesOverwriteWithoutFreshVersionCheck()
    {
        using var fixture = new Fixture(true); var device = fixture.Device("A");
        var source = Path.Combine(device.P.Source, "a.txt"); File.WriteAllText(source, "base");
        await Apply(device.S); await device.S.ReadAsync(false, default);
        File.WriteAllText(source, "local change");
        var read = await device.S.ReadAsync(false, default);
        Assert.AreEqual(1L, read.Snapshot.Metrics.CloudInventoryCacheHits);
        var file = read.Snapshot.Files.Single(); var heads = read.Heads[file.RelativePath];
        var remote = fixture.Drive.Items[heads.Single().ItemId];
        fixture.Drive.Items[remote.Item.Id] = (remote.Item with { ETag = "concurrent" }, remote.Parent, remote.Bytes);
        await Assert.ThrowsExactlyAsync<DeferredOperationException>(() => device.S.ApplyAsync(file, heads,
            SyncDirection.Backup, device.S.Decide(file, heads, SyncDirection.Backup), default));
        Assert.AreEqual("concurrent", fixture.Drive.Items[remote.Item.Id].Item.ETag);
        Assert.AreEqual("local change", File.ReadAllText(source));
    }

    [TestMethod]
    public async Task IdenticalObservationsSkipWritesButHeartbeatAndChangesPublish()
    {
        using var fixture = new Fixture(); var device = fixture.Device("A"); var clock = new Clock();
        var publisher = new CloudObservationPublisher(device.C, fixture.Drive, clock);
        FileResult[] files = [new("a.txt", FileAction.Equal, false)];
        var metrics = new ReadMetrics();
        await publisher.PublishAsync(device.P, files, metrics, default);
        Assert.AreEqual(1L, metrics.CloudObservationUploads);
        var finds = fixture.Drive.Finds;
        await publisher.PublishAsync(device.P, files, metrics, default);
        Assert.AreEqual(finds, fixture.Drive.Finds); Assert.AreEqual(1L, metrics.CloudObservationSkipped);
        clock.Now += TimeSpan.FromHours(1);
        await publisher.PublishAsync(device.P, files, metrics, default); Assert.AreEqual(2L, metrics.CloudObservationUploads);
        await publisher.PublishAsync(device.P, [new("a.txt", FileAction.NeedsPull, false)], metrics, default);
        Assert.AreEqual(3L, metrics.CloudObservationUploads);
    }

    [TestMethod]
    public async Task ClearingObservationCacheOnlyCausesAnotherPublication()
    {
        using var fixture = new Fixture(); var device = fixture.Device("A");
        var publisher = new CloudObservationPublisher(device.C, fixture.Drive);
        FileResult[] files = [new("a.txt", FileAction.Equal, false)]; var metrics = new ReadMetrics();
        await publisher.PublishAsync(device.P, files, metrics, default);
        var acknowledgement = Directory.GetFiles(fixture.Root, "*.json", SearchOption.AllDirectories)
            .Single(p => p.Contains("cloud-observations"));
        Assert.Contains(Path.DirectorySeparatorChar + "cache" + Path.DirectorySeparatorChar, acknowledgement);
        File.Delete(acknowledgement);
        await publisher.PublishAsync(device.P, files, metrics, default);
        Assert.AreEqual(2L, metrics.CloudObservationUploads);
        Assert.AreEqual(0L, metrics.CloudObservationSkipped);
    }

    [TestMethod]
    public async Task MalformedSnapshotFieldsRebuildInsteadOfFailingBackup()
    {
        using var fixture = new Fixture(); var drive = new ChangesDrive();
        var path = Path.Combine(fixture.Root, "inventory.json");
        var cache = new CloudInventoryCache(path, "binding", drive);
        await cache.ReadAsync(false, () => Task.FromResult(Snapshot()), new(), default);
        var json = System.Text.Json.Nodes.JsonNode.Parse(File.ReadAllText(path))!;
        json["snapshot"]!["metadata"] = new System.Text.Json.Nodes.JsonArray(
            System.Text.Json.Nodes.JsonNode.Parse("{\"id\":\"id\",\"name\":null,\"eTag\":\"etag\",\"size\":1,\"folder\":false}"));
        File.WriteAllText(path, json.ToJsonString());
        var rebuilt = false;
        await cache.ReadAsync(false, () => { rebuilt = true; return Task.FromResult(Snapshot()); }, new(), default);
        Assert.IsTrue(rebuilt);
    }

    [TestMethod]
    public async Task LockedHashCacheDoesNotBlockIncrementalBackup()
    {
        using var f = new Fixture(); var a = f.Device("A");
        var source = Path.Combine(a.P.Source, "a.txt"); File.WriteAllText(source, "base"); await Apply(a.S);
        var cache = Path.Combine(DataPaths.FromState(a.C.Preview.StateDirectory).Cache, "hashes", CloudMapping.Bind(a.P, a.C) + ".json");
        var before = File.ReadAllText(cache); File.WriteAllText(source, "updated");
        using (File.Open(cache, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
        {
            var result = await new CloudBackupRuntime(a.C, () => f.Drive).ExecuteAsync(SyncDirection.Backup, null, false, false, null, default, automatic: true);
            Assert.AreEqual(0, result.ExitCode, System.Text.Json.JsonSerializer.Serialize(result));
            Assert.IsTrue(result.Projects.Single().Files.Single().Changed);
        }
        Assert.AreEqual(before, File.ReadAllText(cache));
        var root = (await f.Drive.FindAsync("drive-root", "Documents", default))!;
        var remote = (await f.Drive.FindAsync(root.Id, "a.txt", default))!;
        Assert.AreEqual("updated", System.Text.Encoding.UTF8.GetString(f.Drive.Items[remote.Id].Bytes));
        Assert.AreEqual("updated", File.ReadAllText(source));
    }

    [TestMethod]
    public async Task LockedObservationCacheDoesNotLeaveVerifiedStatusPending()
    {
        using var f = new Fixture(); var a = f.Device("A");
        var publisher = new CloudObservationPublisher(a.C, f.Drive);
        FileResult[] files = [new("a.txt", FileAction.Equal, false)];
        await publisher.PublishAsync(a.P, files, new(), default);
        var cache = Directory.GetFiles(f.Root, "*.json", SearchOption.AllDirectories).Single(p => p.Contains("cloud-observations"));
        var before = File.ReadAllText(cache); var metrics = new ReadMetrics();
        using (File.Open(cache, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
            await publisher.PublishAsync(a.P, files, metrics, default);
        Assert.AreEqual(1L, metrics.CloudObservationUploads);
        Assert.AreEqual(before, File.ReadAllText(cache));
        Assert.IsEmpty(Directory.GetFiles(f.Root, "*.json", SearchOption.AllDirectories).Where(p => p.Contains("cloud-outbox")));
    }

    [TestMethod]
    public async Task UnavailableObservationCacheDoesNotHideDamagedOutboxEvidence()
    {
        using var f = new Fixture(); var a = f.Device("A"); var data = DataPaths.FromState(a.C.Preview.StateDirectory);
        var publisher = new CloudObservationPublisher(a.C, f.Drive);
        FileResult[] files = [new("a.txt", FileAction.Equal, false)]; f.Drive.FailAfterCommit = true;
        await Assert.ThrowsExactlyAsync<IOException>(() => publisher.PublishAsync(a.P, files, new(), default));
        var pending = Directory.GetFiles(data.State, "*.json", SearchOption.AllDirectories).Single(p => p.Contains("cloud-outbox"));
        File.WriteAllText(pending, "damaged evidence");
        var cache = Path.Combine(data.Cache, "cloud-observations", a.C.Preview.DeviceId, f.Account.RepositoryId!, a.P.ProjectId + ".json");
        Directory.CreateDirectory(Path.GetDirectoryName(cache)!); File.WriteAllText(cache, "{}");
        using (File.Open(cache, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
            await Assert.ThrowsAsync<System.Text.Json.JsonException>(() => publisher.PublishAsync(a.P, files, new(), default));
        Assert.AreEqual("damaged evidence", File.ReadAllText(pending));
    }

    [TestMethod]
    public async Task LostObservationResponseRemainsInOutboxAndRetryDoesNotDuplicate()
    {
        using var fixture = new Fixture(); var device = fixture.Device("A");
        var publisher = new CloudObservationPublisher(device.C, fixture.Drive);
        FileResult[] files = [new("a.txt", FileAction.Equal, false)];
        fixture.Drive.FailAfterCommit = true;
        await Assert.ThrowsExactlyAsync<IOException>(() => publisher.PublishAsync(device.P, files, new(), default));
        var pending = Directory.GetFiles(fixture.Root, "*.json", SearchOption.AllDirectories)
            .Where(p => p.Contains("cloud-outbox")).ToArray();
        Assert.HasCount(1, pending);
        var cloudFiles = fixture.Drive.Items.Count;
        var metrics = new ReadMetrics();
        await publisher.PublishAsync(device.P, files, metrics, default);
        Assert.HasCount(cloudFiles, fixture.Drive.Items);
        Assert.IsFalse(File.Exists(pending[0]));
        Assert.AreEqual(1L, metrics.CloudObservationUploads);
        Assert.AreEqual(1L, metrics.CloudObservationSkipped);
    }
}
