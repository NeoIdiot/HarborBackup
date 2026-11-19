using System.Net;
using System.Text;
using System.Text.Json;
using HarborBackup.OneDrive.Authentication;
using HarborBackup.OneDrive.Transport;

namespace HarborBackup.Windows.Tests;

[TestClass]
public sealed class GraphMetadataBatchTests
{
    private sealed class Tokens : IAccessTokens { public Task<string> GetAsync(CancellationToken t) => Task.FromResult("synthetic"); }
    private sealed class Handler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> call) : HttpMessageHandler
    { protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage r, CancellationToken t) => call(r, t); }
    private static HttpResponseMessage Json(object value) => new(HttpStatusCode.OK) { Content = new StringContent(JsonSerializer.Serialize(value)) };
    private static object Item(string id, string tag = "etag") => new { id, name = id + ".json", size = 2, eTag = tag };
    private static RemoteItem[] Items() => [new("a", "a.json", "etag", 2, false), new("b", "b.json", "etag", 2, false)];

    [TestMethod]
    [DataRow("ok")]
    [DataRow("changed-after")]
    [DataRow("missing")]
    [DataRow("duplicate")]
    [DataRow("throttled")]
    [DataRow("forbidden")]
    public async Task MetadataBatchesCorrelateResponsesCheckVersionsAndHandleIndividualFailures(string mode)
    {
        var batches = 0; var downloads = 0; var individual = 0;
        using var drive = new GraphDrive(new Tokens(), "drive", new Handler(async (request, token) =>
        {
            if (request.Method == HttpMethod.Post)
            {
                batches++;
                Assert.AreEqual("/$batch", request.RequestUri!.AbsolutePath[5..]);
                using var body = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(token));
                var replies = body.RootElement.GetProperty("requests").EnumerateArray().Select(entry =>
                {
                    Assert.AreEqual("GET", entry.GetProperty("method").GetString());
                    var index = entry.GetProperty("id").GetString()!;
                    var id = index == "0" ? "a" : "b";
                    var status = mode == "throttled" && batches == 1 && index == "0" ? 429 : mode == "forbidden" ? 403 : 200;
                    return new { id = mode == "duplicate" ? "0" : index, status, headers = new Dictionary<string, string> { ["Retry-After"] = "0" },
                        body = Item(id, mode == "changed-after" && batches == 2 ? "changed" : "etag") };
                }).Reverse().ToArray();
                return Json(new { responses = mode == "missing" ? replies.Take(1).ToArray() : replies });
            }
            Assert.AreEqual(HttpMethod.Get, request.Method);
            if (request.RequestUri!.AbsolutePath.EndsWith("/content", StringComparison.Ordinal))
            { downloads++; return new(HttpStatusCode.OK) { Content = new ByteArrayContent(Encoding.UTF8.GetBytes("{}")) }; }
            individual++;
            return Json(Item("a"));
        }));
        if (mode is "ok" or "throttled")
        {
            var result = await drive.ReadMetadataAsync(Items(), default);
            CollectionAssert.AreEqual(Encoding.UTF8.GetBytes("{}"), result["a"]);
            Assert.AreEqual(2, batches); Assert.AreEqual(2, downloads);
            Assert.AreEqual(mode == "throttled" ? 1 : 0, individual);
            Assert.AreEqual(mode == "throttled" ? 1L : 0L, drive.NetworkMetrics.Retries);
        }
        else
        {
            await Assert.ThrowsAsync<IOException>(() => drive.ReadMetadataAsync(Items(), default));
            Assert.AreEqual(mode == "changed-after" ? 2 : 0, downloads);
        }
    }

    [TestMethod]
    public async Task CancellingMetadataBatchDrainsAllContentReads()
    {
        var active = 0; var started = 0;
        var ready = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var stop = new CancellationTokenSource();
        using var drive = new GraphDrive(new Tokens(), "drive", new Handler(async (request, token) =>
        {
            if (request.Method == HttpMethod.Post)
                return Json(new { responses = new[] { new { id = "0", status = 200, body = Item("a") }, new { id = "1", status = 200, body = Item("b") } } });
            Interlocked.Increment(ref active);
            if (Interlocked.Increment(ref started) == 2) ready.TrySetResult();
            try { await Task.Delay(Timeout.Infinite, token); return Json(new { }); }
            finally { Interlocked.Decrement(ref active); }
        }));
        var reading = drive.ReadMetadataAsync(Items(), stop.Token);
        await ready.Task.WaitAsync(TimeSpan.FromSeconds(5));
        stop.Cancel();
        await Assert.ThrowsAsync<OperationCanceledException>(() => reading);
        Assert.AreEqual(0, active);
    }
}
