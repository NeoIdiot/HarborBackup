using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Text.Json;
using HarborBackup.OneDrive.Authentication;
using HarborBackup.OneDrive.Transport;
using HarborBackup.Windows.Integration;
using HarborBackup.Windows.Persistence.Configuration;
using static HarborBackup.Windows.Tests.CloudTransportTests;

namespace HarborBackup.Windows.Tests;

// Controlled request latency, not a measurement of OneDrive service throughput.
[TestClass]
[DoNotParallelize]
public sealed class CloudNetworkPerformanceTests
{
    public TestContext TestContext { get; set; } = null!;
    private void Record(object value) => TestContext.WriteLine("NETWORK_PERF " + JsonSerializer.Serialize(value));
    private sealed class Tokens : IAccessTokens
    {
        public Task<string> GetAsync(CancellationToken token) => Task.FromResult("synthetic-token");
    }
    private sealed class Handler(Func<HttpRequestMessage, Task<HttpResponseMessage>> respond, int latency) : HttpMessageHandler
    {
        public readonly ConcurrentQueue<string> Methods = new();
        private int active, peak;
        public int Peak => peak;
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            Methods.Enqueue(request.Method.Method);
            var count = Interlocked.Increment(ref active);
            int previous;
            do { previous = peak; } while (count > previous && Interlocked.CompareExchange(ref peak, count, previous) != previous);
            try { await Task.Delay(latency, token); return await respond(request); }
            finally { Interlocked.Decrement(ref active); }
        }
    }
    private static HttpResponseMessage Json(object value, HttpStatusCode status = HttpStatusCode.OK) =>
        new(status) { Content = new StringContent(JsonSerializer.Serialize(value)) };

    [TestMethod]
    public async Task SmallFileThreeWritePathUnderControlledLatency()
    {
        const int files = 12, latency = 80;
        // Repeat to expose scheduling/JIT variation; correctness never depends on a timing threshold.
        for (var repeat = 0; repeat < 3; repeat++)
        foreach (var concurrency in new[] { 1, 4, 8 })
        {
            using var fixture = new Fixture();
            var handler = new Handler(async request =>
            {
                Assert.AreEqual("graph.microsoft.com", request.RequestUri!.Host);
                Assert.AreNotEqual(HttpMethod.Get, request.Method);
                var path = Uri.UnescapeDataString(request.RequestUri.AbsolutePath);
                if (request.Method == HttpMethod.Patch)
                {
                    Assert.IsTrue(request.Headers.Contains("If-Match"));
                    var id = path.Split('/').Last();
                    return Json(new { id, name = id, eTag = "dated", size = 1024 });
                }
                Assert.AreEqual(HttpMethod.Put, request.Method);
                Assert.Contains("conflictBehavior=fail", request.RequestUri.Query);
                var bytes = await request.Content!.ReadAsByteArrayAsync();
                using var stream = new MemoryStream(bytes);
                var hash = await QuickXor.ComputeAsync(stream, default);
                var name = path[..path.LastIndexOf(":/content", StringComparison.Ordinal)].Split('/').Last();
                return Json(new { id = name, name, eTag = "created", size = bytes.Length,
                    file = new { hashes = new { quickXorHash = hash } } }, HttpStatusCode.Created);
            }, latency);
            using var drive = new GraphDrive(new Tokens(), "synthetic-drive", handler);
            var repository = new CloudRepository(drive, new CloudAccountSettings(DriveId: "synthetic-drive"), new DataPaths(fixture.Root));
            var clock = Stopwatch.StartNew();
            await Parallel.ForEachAsync(Enumerable.Range(0, files), new ParallelOptions { MaxDegreeOfParallelism = concurrency }, async (i, token) =>
            {
                using var source = new MemoryStream(new byte[1024]);
                var uploaded = await drive.UploadAsync("parent", $"file-{i}.txt", source, null, _ => { }, token, reconcileExisting: false);
                var dated = await drive.SetModifiedAsync(uploaded, DateTimeOffset.UnixEpoch, token);
                await repository.JsonAsync("metadata", $"record-{i}.json", new { item = dated.Id }, token, reconcileExisting: false);
            });
            clock.Stop();
            Assert.AreEqual(files * 3L, drive.NetworkMetrics.Requests);
            Assert.AreEqual(files * 2, handler.Methods.Count(m => m == "PUT"));
            Assert.AreEqual(files, handler.Methods.Count(m => m == "PATCH"));
            Assert.IsLessThanOrEqualTo(concurrency, handler.Peak);
            Record(new { scenario = "small-files", repeat, files, bytesPerFile = 1024, latencyMs = latency,
                concurrency, elapsedMs = clock.ElapsedMilliseconds, requests = drive.NetworkMetrics.Requests, peak = handler.Peak });
        }
    }

    [TestMethod]
    public async Task LargeUploadMeasuresCurrentSequentialChunkRequests()
    {
        const int length = 32 * 1024 * 1024, latency = 80;
        for (var repeat = 0; repeat < 3; repeat++)
        {
            string? staging = null;
            var chunkSizes = new List<long>();
            var handler = new Handler(async request =>
            {
                if (request.RequestUri!.Host == "my.microsoftpersonalcontent.com")
                {
                    Assert.IsNull(request.Headers.Authorization);
                    var range = request.Content!.Headers.ContentRange!;
                    chunkSizes.Add(range.To!.Value - range.From!.Value + 1);
                    if (range.To != length - 1)
                        return Json(new { nextExpectedRanges = new[] { (range.To + 1) + "-" }, expirationDateTime = DateTimeOffset.UtcNow.AddHours(1) }, HttpStatusCode.Accepted);
                    return Json(new { id = "staged", name = staging, eTag = "stage-tag", size = length }, HttpStatusCode.Created);
                }
                if (request.Method == HttpMethod.Post)
                {
                    using var body = JsonDocument.Parse(await request.Content!.ReadAsStringAsync());
                    staging = body.RootElement.GetProperty("item").GetProperty("name").GetString();
                    return Json(new { uploadUrl = "https://my.microsoftpersonalcontent.com/upload", expirationDateTime = DateTimeOffset.UtcNow.AddHours(1) });
                }
                Assert.AreEqual(HttpMethod.Patch, request.Method);
                Assert.AreEqual("stage-tag", request.Headers.GetValues("If-Match").Single());
                Assert.Contains("conflictBehavior=fail", request.RequestUri.Query);
                return Json(new { id = "staged", name = "large.bin", eTag = "published", size = length });
            }, latency);
            using var drive = new GraphDrive(new Tokens(), "synthetic-drive", handler);
            using var source = new MemoryStream(new byte[length]);
            var clock = Stopwatch.StartNew();
            var result = await drive.UploadAsync("parent", "large.bin", source, null, _ => { }, default, reconcileExisting: false);
            clock.Stop();
            Assert.AreEqual(length, result.Size);
            Assert.AreEqual((long)length, chunkSizes.Sum());
            Assert.AreEqual(chunkSizes.Count + 2L, drive.NetworkMetrics.Requests);
            Assert.AreEqual(1, handler.Peak);
            Record(new { scenario = "large-file", repeat, bytes = length, latencyMs = latency, elapsedMs = clock.ElapsedMilliseconds,
                chunks = chunkSizes.Count, chunkBytes = chunkSizes[0], requests = drive.NetworkMetrics.Requests });
        }
    }

    [TestMethod]
    public async Task InventoryMeasuresWarmCacheAndUnrelatedDriveChange()
    {
        using var fixture = new Fixture(delta: true);
        var device = fixture.Device("A");
        for (var i = 0; i < 12; i++)
        {
            var folder = Path.Combine(device.P.Source, $"folder-{i}");
            Directory.CreateDirectory(folder);
            File.WriteAllText(Path.Combine(folder, "file.txt"), "synthetic-content");
        }
        await Apply(device.S);
        async Task<(int Listings, long Hits)> Measure(string scenario, bool full = false)
        {
            var before = fixture.Drive.Enumerations;
            var downloads = fixture.Drive.Downloads;
            var clock = Stopwatch.StartNew();
            var read = await device.S.ReadAsync(full, default);
            clock.Stop();
            var listings = fixture.Drive.Enumerations - before;
            Record(new { scenario, elapsedMs = clock.ElapsedMilliseconds, listings, metadataDownloads = fixture.Drive.Downloads - downloads,
                inventoryHits = read.Snapshot.Metrics.CloudInventoryCacheHits, metadataHits = read.Snapshot.Metrics.CloudMetadataCacheHits });
            return (listings, read.Snapshot.Metrics.CloudInventoryCacheHits);
        }
        var cold = await Measure("inventory-cold");
        var warm = await Measure("inventory-warm");
        fixture.Drive.Add("drive-root", "unrelated.txt", [1]);
        var unrelated = await Measure("inventory-unrelated-change");
        var full = await Measure("inventory-forced-full", full: true);
        Assert.AreEqual(1L, warm.Hits);
        Assert.IsGreaterThan(warm.Listings, cold.Listings);
        Assert.AreEqual(0L, unrelated.Hits);
        Assert.AreEqual(cold.Listings, unrelated.Listings);
        Assert.AreEqual(cold.Listings, full.Listings);
        Assert.AreEqual(0, fixture.Drive.BusinessDownloads);
        Assert.IsFalse(device.S.Pending);
    }
}
