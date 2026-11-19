using System.Diagnostics;
using System.Net;
using System.Text.Json;
using System.Text.Json.Nodes;
using HarborBackup.OneDrive.Authentication;
using HarborBackup.OneDrive.Transport;
using HarborBackup.Windows.Integration;
using HarborBackup.Windows.Persistence.Configuration;

namespace HarborBackup.Windows.Tests;

[TestClass]
public sealed class BandwidthLimitTests
{
    private sealed class Clock : TimeProvider
    {
        private long timestamp;
        public List<TimeSpan> Delays { get; } = [];
        public override long TimestampFrequency => TimeSpan.TicksPerSecond;
        public override long GetTimestamp() => timestamp;
        public TimeSpan Elapsed => TimeSpan.FromTicks(timestamp);
        public Task Wait(TimeSpan delay, CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            Delays.Add(delay); timestamp += delay.Ticks;
            return Task.CompletedTask;
        }
    }
    private sealed class Tokens : IAccessTokens { public Task<string> GetAsync(CancellationToken token) => Task.FromResult("synthetic"); }
    private sealed class Handler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> send) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token) => send(request, token);
    }
    private sealed class Sink : Stream
    {
        public long Bytes { get; private set; }
        public int LargestWrite { get; private set; }
        public override bool CanRead => false;
        public override bool CanSeek => false;
        public override bool CanWrite => true;
        public override long Length => Bytes;
        public override long Position { get => Bytes; set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) { Bytes += count; LargestWrite = Math.Max(LargestWrite, count); }
        public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken token = default)
        { token.ThrowIfCancellationRequested(); Bytes += buffer.Length; LargestWrite = Math.Max(LargestWrite, buffer.Length); return ValueTask.CompletedTask; }
    }
    private static HttpResponseMessage Item(string name, int size) => new(HttpStatusCode.OK)
    { Content = new StringContent(JsonSerializer.Serialize(new { id = name, name, eTag = "version", size })) };

    [TestMethod]
    public void TransferSettingsDefaultToUnlimitedAndPersistWithoutChangingMappings()
    {
        var defaults = BackupSettingsReader.Parse("{\"schemaVersion\":1,\"sync\":{\"enabled\":true}}");
        Assert.AreEqual(0, defaults.UploadKiBPerSecond); Assert.AreEqual(0, defaults.DownloadKiBPerSecond);
        using var ws = new TestWorkspace();
        var editor = new ConfigurationEditor(new(Path.Combine(ws.Config, "projects.json"), Path.Combine(ws.Config, "backup-settings.json")), ws.Device);
        var draft = editor.Load();
        var originalProjects = File.ReadAllBytes(Path.Combine(ws.Config, "projects.json"));
        draft.System["transfer"] = new JsonObject { ["uploadKiBPerSecond"] = 128, ["downloadKiBPerSecond"] = 512 };
        editor.Save(draft, ConfigurationPart.System);
        var settings = ws.Load();
        Assert.AreEqual(128, settings.UploadKiBPerSecond); Assert.AreEqual(512, settings.DownloadKiBPerSecond);
        var options = new CloudAccountStore(DataPaths.FromConfig(Path.Combine(ws.Config, "backup-settings.json"))).TransferOptions();
        Assert.AreEqual(128 * 1024L, options.UploadBytesPerSecond);
        Assert.AreEqual(512 * 1024L, options.DownloadBytesPerSecond);
        CollectionAssert.AreEqual(originalProjects, File.ReadAllBytes(Path.Combine(ws.Config, "projects.json")));
    }

    [TestMethod]
    [DataRow("{\"uploadKiBPerSecond\":-1}")]
    [DataRow("{\"downloadKiBPerSecond\":1048577}")]
    [DataRow("{\"uploadKiBPerSecond\":1.5}")]
    [DataRow("{\"downloadKiBPerSecond\":\"128\"}")]
    [DataRow("{\"uploadKiBPerSecond\":null}")]
    [DataRow("{\"unknownLimit\":5}")]
    [DataRow("null")]
    [DataRow("[]")]
    public void InvalidTransferSettingsAreRejected(string transfer)
        => Assert.ThrowsExactly<FormatException>(() => BackupSettingsReader.Parse(
            "{\"schemaVersion\":1,\"sync\":{\"enabled\":true},\"transfer\":" + transfer + "}"));

    [TestMethod]
    [DataRow(-1L, 0L)]
    [DataRow(0L, -1L)]
    [DataRow(1073741825L, 0L)]
    [DataRow(0L, 1073741825L)]
    public void InvalidProtocolLimitsAreRejectedBeforeRequests(long upload, long download)
        => Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => new GraphDrive(new Tokens(), "drive",
            new Handler((_, _) => throw new AssertFailedException("No request expected.")),
            options: new(UploadBytesPerSecond: upload, DownloadBytesPerSecond: download)));

    [TestMethod]
    public async Task UploadPacesActualWritesInSmallSlicesAndChargesRetriedContent()
    {
        var clock = new Clock(); var attempts = 0; var sinks = new List<Sink>();
        using var drive = new GraphDrive(new Tokens(), "drive", new Handler(async (request, token) =>
        {
            var sink = new Sink(); sinks.Add(sink);
            Assert.AreEqual(1024L, request.Content!.Headers.ContentLength);
            await request.Content.CopyToAsync(sink, token);
            if (++attempts == 1) return new((HttpStatusCode)429)
                { Headers = { RetryAfter = new System.Net.Http.Headers.RetryConditionHeaderValue(TimeSpan.Zero) } };
            return Item("file.bin", 1024);
        }), new CloudRetryGate(), options: new(UploadBytesPerSecond: 1024), rateTimeProvider: clock, rateWait: clock.Wait);
        using var source = new MemoryStream(new byte[1024]);
        await drive.UploadAsync("root", "file.bin", source, null, _ => { }, default, reconcileExisting: false);
        Assert.AreEqual(2, attempts);
        Assert.IsTrue(clock.Elapsed >= TimeSpan.FromSeconds(1.99));
        Assert.IsTrue(clock.Delays.All(d => d <= TimeSpan.FromMilliseconds(100)));
        Assert.IsTrue(sinks.All(s => s.Bytes == 1024 && s.LargestWrite <= 102));
    }

    [TestMethod]
    public async Task UnlimitedContentDoesNotWaitAndDownloadLimitLeavesControlRequestsUntouched()
    {
        var clock = new Clock(); var bytes = new byte[2048];
        using var drive = new GraphDrive(new Tokens(), "drive", new Handler(async (request, token) =>
        {
            if (request.Method == HttpMethod.Put)
            { using var sink = new Sink(); await request.Content!.CopyToAsync(sink, token); return Item("file.bin", bytes.Length); }
            return request.RequestUri!.AbsolutePath.EndsWith("/content", StringComparison.Ordinal)
                ? new(HttpStatusCode.OK) { Content = new ByteArrayContent(bytes) } : Item("file.bin", bytes.Length);
        }), new CloudRetryGate(), options: new(DownloadBytesPerSecond: 2048), rateTimeProvider: clock, rateWait: clock.Wait);
        using var source = new MemoryStream(bytes);
        await drive.UploadAsync("root", "file.bin", source, null, _ => { }, default, reconcileExisting: false);
        Assert.AreEqual(TimeSpan.Zero, clock.Elapsed);
        await drive.RootAsync(default);
        Assert.AreEqual(TimeSpan.Zero, clock.Elapsed);
        using var target = new Sink();
        await drive.DownloadAsync(new("file.bin", "file.bin", "version", bytes.Length, false), target, default);
        Assert.AreEqual((long)bytes.Length, target.Bytes);
        Assert.IsLessThanOrEqualTo(204, target.LargestWrite);
        Assert.IsTrue(clock.Elapsed >= TimeSpan.FromSeconds(.99));
    }

    [TestMethod]
    public async Task UploadSessionKeepsItsRangeAndPublishContractWhilePacingEachChunk()
    {
        const int length = 320 * 1024 + 17;
        var clock = new Clock(); string? stagingName = null; var chunks = 0;
        using var drive = new GraphDrive(new Tokens(), "drive", new Handler(async (request, token) =>
        {
            if (request.Method == HttpMethod.Post)
            {
                using var body = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(token));
                stagingName = body.RootElement.GetProperty("item").GetProperty("name").GetString();
                return new(HttpStatusCode.OK) { Content = new StringContent(JsonSerializer.Serialize(new
                    { uploadUrl = "https://upload.1drv.com/session", expirationDateTime = DateTimeOffset.UtcNow.AddHours(1) })) };
            }
            if (request.Method == HttpMethod.Patch) return Item("file.bin", length);
            var range = request.Content!.Headers.ContentRange!;
            Assert.AreEqual((long)length, range.Length);
            Assert.AreEqual(chunks == 0 ? 0L : 320L * 1024, range.From);
            using var sink = new Sink(); await request.Content.CopyToAsync(sink, token);
            Assert.AreEqual(chunks == 0 ? 320L * 1024 : 17L, sink.Bytes);
            Assert.IsLessThanOrEqualTo(32 * 1024, sink.LargestWrite);
            return ++chunks == 1
                ? new(HttpStatusCode.Accepted) { Content = new StringContent(JsonSerializer.Serialize(new
                    { nextExpectedRanges = new[] { "327680-" }, expirationDateTime = DateTimeOffset.UtcNow.AddHours(1) })) }
                : Item(stagingName!, length);
        }), new CloudRetryGate(), options: new(UploadChunkBytes: 320 * 1024, UploadBytesPerSecond: 320 * 1024),
            rateTimeProvider: clock, rateWait: clock.Wait);
        using var source = new MemoryStream(new byte[length]);
        // An existing staging reservation makes this small synthetic body exercise upload sessions.
        var reserved = new TransferSession("", DateTimeOffset.MinValue, 0, ".harbor-upload-" + Guid.NewGuid().ToString("N") + ".tmp");
        await drive.UploadAsync("root", "file.bin", source, reserved, _ => { }, default, reconcileExisting: false);
        Assert.AreEqual(2, chunks);
        Assert.IsTrue(clock.Elapsed >= TimeSpan.FromSeconds(1));
    }

    [TestMethod]
    public async Task ConcurrentUploadsAndDownloadsShareTheirDirectionsRealTimeBudget()
    {
        const int length = 8 * 1024;
        using var drive = new GraphDrive(new Tokens(), "drive", new Handler(async (request, token) =>
        {
            if (request.Method == HttpMethod.Put)
            {
                using var sink = new Sink(); await request.Content!.CopyToAsync(sink, token);
                return Item(request.RequestUri!.AbsolutePath.Contains("a.bin", StringComparison.Ordinal) ? "a.bin" : "b.bin", length);
            }
            return request.RequestUri!.AbsolutePath.EndsWith("/content", StringComparison.Ordinal)
                ? new(HttpStatusCode.OK) { Content = new ByteArrayContent(new byte[length]) } : Item("file.bin", length);
        }), options: new(UploadBytesPerSecond: 32 * 1024, DownloadBytesPerSecond: 32 * 1024));
        using var first = new MemoryStream(new byte[length]); using var second = new MemoryStream(new byte[length]);
        var watch = Stopwatch.StartNew();
        await Task.WhenAll(drive.UploadAsync("root", "a.bin", first, null, _ => { }, default, reconcileExisting: false),
            drive.UploadAsync("root", "b.bin", second, null, _ => { }, default, reconcileExisting: false));
        Assert.IsTrue(watch.Elapsed >= TimeSpan.FromMilliseconds(450), $"Concurrent upload budget was bypassed: {watch.Elapsed}.");
        using var targetOne = new Sink(); using var targetTwo = new Sink(); watch.Restart();
        var item = new RemoteItem("file.bin", "file.bin", "version", length, false);
        await Task.WhenAll(drive.DownloadAsync(item, targetOne, default), drive.DownloadAsync(item, targetTwo, default));
        Assert.IsTrue(watch.Elapsed >= TimeSpan.FromMilliseconds(450), $"Concurrent download budget was bypassed: {watch.Elapsed}.");
        Assert.AreEqual(2L * length, targetOne.Bytes + targetTwo.Bytes);
    }

    [TestMethod]
    public async Task CancellationInterruptsAnActiveUploadPacingWait()
    {
        var waiting = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var stop = new CancellationTokenSource();
        using var drive = new GraphDrive(new Tokens(), "drive", new Handler(async (request, token) =>
        {
            using var sink = new Sink(); await request.Content!.CopyToAsync(sink, token); return Item("file.bin", 1024);
        }), new CloudRetryGate(), options: new(UploadBytesPerSecond: 1024), rateWait: (_, token) =>
        { waiting.TrySetResult(); return Task.Delay(Timeout.Infinite, token); });
        using var source = new MemoryStream(new byte[1024]);
        var running = drive.UploadAsync("root", "file.bin", source, null, _ => { }, stop.Token, reconcileExisting: false);
        await waiting.Task.WaitAsync(TimeSpan.FromSeconds(5));
        stop.Cancel();
        await Assert.ThrowsAsync<OperationCanceledException>(() => running.WaitAsync(TimeSpan.FromSeconds(5)));
    }
}
