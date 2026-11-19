using System.Diagnostics;
using System.Text.Json;
using HarborBackup.Core.Domain;
using HarborBackup.OneDrive.Transport;
using HarborBackup.Windows.Integration;
using HarborBackup.Windows.Persistence;
using static HarborBackup.Windows.Tests.CloudTransportTests;

namespace HarborBackup.Windows.Tests;

[TestClass]
[DoNotParallelize]
public sealed class MetadataCheckpointTests
{
    [TestMethod]
    public async Task ColdCheckpointAndIncrementalTailKeepOriginalEvidenceAndRejectCorruption()
    {
        using var f = new Fixture(); var a = f.Device("A");
        File.WriteAllText(Path.Combine(a.P.Source, "a.txt"), "current");
        await Apply(a.S);
        var data = DataPaths.FromState(a.C.Preview.StateDirectory);
        var repository = new CloudRepository(f.Drive, f.Account, data);
        var folder = await repository.FolderAsync("metadata/projects/" + a.P.ProjectId + "/files", false, default);
        Assert.IsNotNull(folder);
        var original = (await f.Drive.ChildrenAsync(folder.Id, default)).Single();
        var metadata = await repository.ReadJsonAsync<CloudFileMetadata>(original, default);
        for (var i = 0; i < 128; i++)
        {
            var id = Guid.NewGuid().ToString("N");
            var old = metadata with { Revision = metadata.Revision with { Id = id, ETag = "old-" + i }, Item = metadata.Item with { ETag = "old-" + i } };
            f.Drive.Add(folder.Id, id + ".json", JsonSerializer.SerializeToUtf8Bytes(old, AtomicJson.Options));
        }
        var first = await new CloudBackupSession(a.P, a.C, f.Account, f.Drive).ReadAsync(false, default);
        Assert.AreEqual(129L, first.Snapshot.Metrics.CloudMetadataRecordsRead);
        var session = new CloudBackupSession(a.P, a.C, f.Account, f.Drive);
        var warm = await session.ReadAsync(false, default);
        Assert.AreEqual(1L, warm.Snapshot.Metrics.CloudMetadataCheckpointHits);
        Assert.AreEqual(0L, warm.Snapshot.Metrics.CloudMetadataRecordsRead);
        await session.PublishCheckpointAsync(default);
        var b = f.Device("B");
        var cold = await b.S.ReadAsync(false, default);
        Assert.AreEqual(1L, cold.Snapshot.Metrics.CloudMetadataCheckpointHits);
        Assert.AreEqual(0L, cold.Snapshot.Metrics.CloudMetadataRecordsRead);
        Assert.IsTrue(cold.Heads["a.txt"].Single().ContentHashKnown);
        Assert.HasCount(129, await f.Drive.ChildrenAsync(folder.Id, default));
        Assert.AreEqual(0, f.Drive.BusinessDownloads);
        var id2 = Guid.NewGuid().ToString("N");
        var added = f.Drive.Add(folder.Id, id2 + ".json", JsonSerializer.SerializeToUtf8Bytes(metadata with { Revision = metadata.Revision with { Id = id2 } }, AtomicJson.Options));
        var tail = await new CloudBackupSession(b.P, b.C, f.Account, f.Drive).ReadAsync(false, default);
        var catalog = await f.Drive.ChildrenAsync(folder.Id, default);
        var bucketSize = catalog.Count(i => CloudMetadataCheckpoint.BucketFor(i.Id) == CloudMetadataCheckpoint.BucketFor(added.Id));
        Assert.AreEqual((long)bucketSize, tail.Snapshot.Metrics.CloudMetadataRecordsRead);
        Assert.AreEqual(1L, tail.Snapshot.Metrics.CloudMetadataCheckpointHits);
        // Replacing a covered evidence version invalidates its whole bucket, including a locally cached one.
        var oldItem = f.Drive.Items[original.Id];
        f.Drive.Items[original.Id] = (oldItem.Item with { ETag = "changed" }, oldItem.Parent, oldItem.Bytes);
        var refreshed = await new CloudBackupSession(b.P, b.C, f.Account, f.Drive).ReadAsync(false, default);
        bucketSize = catalog.Count(i => CloudMetadataCheckpoint.BucketFor(i.Id) == CloudMetadataCheckpoint.BucketFor(original.Id));
        Assert.AreEqual(1L, refreshed.Snapshot.Metrics.CloudMetadataCheckpointHits);
        Assert.AreEqual((long)bucketSize, refreshed.Snapshot.Metrics.CloudMetadataRecordsRead);
    }

    [TestMethod]
    public async Task DamagedOptionalCheckpointFallsBackAndFullCheckReadsOriginalRecords()
    {
        using var f = new Fixture(); var a = f.Device("A");
        File.WriteAllText(Path.Combine(a.P.Source, "x.txt"), "x"); await Apply(a.S);
        var data = DataPaths.FromState(a.C.Preview.StateDirectory);
        var repo = new CloudRepository(f.Drive, f.Account, data);
        var folder = (await repo.FolderAsync("metadata/projects/" + a.P.ProjectId + "/files", false, default))!;
        var meta = await repo.ReadJsonAsync<CloudFileMetadata>((await f.Drive.ChildrenAsync(folder.Id, default)).Single(), default);
        for (var i = 0; i < 128; i++)
        {
            var id = Guid.NewGuid().ToString("N");
            f.Drive.Add(folder.Id, id + ".json", JsonSerializer.SerializeToUtf8Bytes(meta with { Revision = meta.Revision with { Id = id } }, AtomicJson.Options));
        }
        await a.S.ReadAsync(false, default);
        File.WriteAllText(Path.Combine(data.Cache, "cloud-metadata-checkpoints", a.P.ProjectId + ".json"), "{broken");
        var fallback = await a.S.ReadAsync(false, default);
        Assert.AreEqual(129L, fallback.Snapshot.Metrics.CloudMetadataRecordsRead);
        var full = await a.S.ReadAsync(true, default);
        Assert.AreEqual(0L, full.Snapshot.Metrics.CloudMetadataCheckpointHits);
        Assert.AreEqual(129L, full.Snapshot.Metrics.CloudMetadataRecordsRead);
    }

    [TestMethod]
    public async Task BrokenNewestCloudCheckpointDoesNotHideAValidOlderCheckpoint()
    {
        using var f = new Fixture(); var a = f.Device("A");
        File.WriteAllText(Path.Combine(a.P.Source, "a.txt"), "current"); await Apply(a.S);
        var data = DataPaths.FromState(a.C.Preview.StateDirectory);
        var repo = new CloudRepository(f.Drive, f.Account, data);
        var folder = (await repo.FolderAsync("metadata/projects/" + a.P.ProjectId + "/files", false, default))!;
        var meta = await repo.ReadJsonAsync<CloudFileMetadata>((await f.Drive.ChildrenAsync(folder.Id, default)).Single(), default);
        for (var i = 0; i < 128; i++)
        {
            var id = Guid.NewGuid().ToString("N");
            f.Drive.Add(folder.Id, id + ".json", JsonSerializer.SerializeToUtf8Bytes(meta with { Revision = meta.Revision with { Id = id } }, AtomicJson.Options));
        }
        await a.S.ReadAsync(false, default); await a.S.PublishCheckpointAsync(default);
        var checkpoints = (await repo.FolderAsync("metadata/projects/" + a.P.ProjectId + "/checkpoints", false, default))!;
        f.Drive.Add(checkpoints.Id, "999999999999-broken.json", "{broken"u8.ToArray());
        var b = f.Device("B");
        var read = await b.S.ReadAsync(false, default);
        Assert.AreEqual(1L, read.Snapshot.Metrics.CloudMetadataCheckpointHits);
        Assert.AreEqual(0L, read.Snapshot.Metrics.CloudMetadataRecordsRead);
        Assert.IsTrue(read.Heads["a.txt"].Single().ContentHashKnown);
        Assert.HasCount(129, await f.Drive.ChildrenAsync(folder.Id, default));
    }

    [TestMethod]
    public async Task RemovingAnExclusionReloadsEvidenceMissingFromTheCheckpoint()
    {
        using var f = new Fixture(); var a = f.Device("A");
        File.WriteAllText(Path.Combine(a.P.Source, "a.txt"), "a");
        File.WriteAllText(Path.Combine(a.P.Source, "b.tmp"), "b"); await Apply(a.S);
        var data = DataPaths.FromState(a.C.Preview.StateDirectory);
        var repo = new CloudRepository(f.Drive, f.Account, data);
        var folder = (await repo.FolderAsync("metadata/projects/" + a.P.ProjectId + "/files", false, default))!;
        var record = (await f.Drive.ChildrenAsync(folder.Id, default)).First();
        var meta = await repo.ReadJsonAsync<CloudFileMetadata>(record, default);
        for (var i = 0; i < 127; i++)
        {
            var id = Guid.NewGuid().ToString("N");
            f.Drive.Add(folder.Id, id + ".json", JsonSerializer.SerializeToUtf8Bytes(meta with { Revision = meta.Revision with { Id = id } }, AtomicJson.Options));
        }
        var filtered = a.P with { ExcludedFiles = ["*.tmp"] };
        await new CloudBackupSession(filtered, a.C with { Projects = [filtered] }, f.Account, f.Drive).ReadAsync(false, default);
        var read = await a.S.ReadAsync(false, default);
        Assert.AreEqual(0L, read.Snapshot.Metrics.CloudMetadataCheckpointHits);
        Assert.AreEqual(129L, read.Snapshot.Metrics.CloudMetadataRecordsRead);
        Assert.IsTrue(read.Heads["b.tmp"].Single().ContentHashKnown);
        Assert.AreEqual(0, f.Drive.BusinessDownloads);
    }

    [TestMethod]
    public async Task ReintroducedHistoricalCloudVersionReloadsOriginalEvidence()
    {
        using var f = new Fixture(); var a = f.Device("A");
        File.WriteAllText(Path.Combine(a.P.Source, "a.txt"), "current"); await Apply(a.S);
        var data = DataPaths.FromState(a.C.Preview.StateDirectory);
        var repo = new CloudRepository(f.Drive, f.Account, data);
        var folder = (await repo.FolderAsync("metadata/projects/" + a.P.ProjectId + "/files", false, default))!;
        var meta = await repo.ReadJsonAsync<CloudFileMetadata>((await f.Drive.ChildrenAsync(folder.Id, default)).Single(), default);
        var storage = await f.Drive.FolderAsync("root", "test-old-contents", default);
        var oldItem = f.Drive.Add(storage.Id, "a.txt", "old"u8.ToArray());
        var oldHash = Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData("old"u8));
        var oldId = Guid.NewGuid().ToString("N");
        var old = meta with { Item = oldItem, Revision = meta.Revision with { Id = oldId, ItemId = oldItem.Id, ETag = oldItem.ETag,
            Version = new(oldHash, oldItem.Size, meta.Revision.Version.LastWriteTimeUtc, meta.Revision.Version.HashedAtUtc) } };
        f.Drive.Add(folder.Id, oldId + ".json", JsonSerializer.SerializeToUtf8Bytes(old, AtomicJson.Options));
        for (var i = 0; i < 127; i++)
        {
            var id = Guid.NewGuid().ToString("N");
            f.Drive.Add(folder.Id, id + ".json", JsonSerializer.SerializeToUtf8Bytes(meta with { Revision = meta.Revision with { Id = id } }, AtomicJson.Options));
        }
        await a.S.ReadAsync(false, default);
        var current = f.Drive.Items[meta.Item.Id];
        var previous = f.Drive.Items[oldItem.Id];
        // Simulate the provider restoring an already-known historical identity into the tree.
        f.Drive.Items[meta.Item.Id] = (current.Item with { ParentId = storage.Id }, storage.Id, current.Bytes);
        f.Drive.Items[oldItem.Id] = (previous.Item with { ParentId = current.Parent }, current.Parent, previous.Bytes);
        var read = await a.S.ReadAsync(false, default);
        Assert.AreEqual(0L, read.Snapshot.Metrics.CloudMetadataCheckpointHits);
        Assert.AreEqual(129L, read.Snapshot.Metrics.CloudMetadataRecordsRead);
        Assert.AreEqual(oldHash, read.Heads["a.txt"].Single().Version.Sha256);
        Assert.AreEqual(0, f.Drive.BusinessDownloads);
        Assert.HasCount(129, await f.Drive.ChildrenAsync(folder.Id, default));
    }

    [TestMethod]
    public void LongHistoryIndexScalesWithCatalogAndKeepsOnlyCurrentBodies()
    {
        var files = Environment.GetEnvironmentVariable("HARBORBACKUP_LARGE_METADATA_BENCHMARK") == "1" ? 10000 : 100;
        const int rounds = 100;
        var project = new string('a', 64);
        var watch = Stopwatch.StartNew();
        var records = Enumerable.Range(0, files * rounds).Select(i => new RemoteItem(i.ToString(), i.ToString("x32") + ".json", "v1", 2000, false)).ToArray();
        var heads = Enumerable.Range(0, files).Select(i =>
        {
            var item = new RemoteItem("body-" + i, i + ".txt", "body-v1", 10, false);
            var revision = new CloudRevision(1, i.ToString("x32"), project, item.Name, "device", [], item.Id, item.ETag,
                new(new string('b', 64), 10, DateTimeOffset.UnixEpoch, DateTimeOffset.UnixEpoch), DateTimeOffset.UnixEpoch);
            return new CloudFileMetadata(2, revision, item);
        }).ToArray();
        var payload = CloudMetadataCheckpoint.Create(project, records, heads, new Dictionary<string, RemoteItem>());
        Assert.IsTrue(CloudMetadataCheckpoint.Covers(payload, project, records, out var remaining));
        Assert.IsEmpty(remaining);
        Assert.HasCount(files, payload.Heads);
        Assert.HasCount(256, payload.Buckets);
        Assert.AreEqual(files * rounds, payload.Buckets.Sum(b => b.Count));
        var bytes = JsonSerializer.SerializeToUtf8Bytes(payload, AtomicJson.Options).Length;
        Assert.IsLessThan(32 * 1024 * 1024, bytes);
        var added = new RemoteItem("new-catalog-item", Guid.NewGuid().ToString("N") + ".json", "v1", 2000, false);
        Assert.IsTrue(CloudMetadataCheckpoint.Covers(payload, project, records.Append(added).ToArray(), out var changed));
        Assert.IsTrue(changed.All(i => CloudMetadataCheckpoint.BucketFor(i.Id) == CloudMetadataCheckpoint.BucketFor(added.Id)));
        Assert.IsLessThan(records.Length, changed.Length);
        Assert.IsFalse(CloudMetadataCheckpoint.Covers(payload with { Scope = "another-repository" }, project, records, out _));
        Console.WriteLine(JsonSerializer.Serialize(new { benchmark = "metadata-history", files, rounds, records = records.Length, currentBodies = payload.Heads.Length, checkpointBytes = bytes, changedBucketRecords = changed.Length, elapsedMs = watch.ElapsedMilliseconds, managedBytes = GC.GetTotalMemory(false) }));
    }
}
