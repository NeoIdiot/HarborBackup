using System.Net;
using System.Net.Http.Headers;
using HarborBackup.OneDrive.Authentication;
using HarborBackup.OneDrive.Transport;

namespace HarborBackup.Windows.Tests;

[TestClass]
public sealed class CloudRetryTests
{
    private sealed class Clock : TimeProvider
    {
        public DateTimeOffset Now = DateTimeOffset.UtcNow;
        public override DateTimeOffset GetUtcNow() => Now;
        public Task Wait(TimeSpan delay, CancellationToken token) { token.ThrowIfCancellationRequested(); Now += delay; return Task.CompletedTask; }
    }
    private sealed class Tokens : IAccessTokens { public Task<string> GetAsync(CancellationToken token) => Task.FromResult("test"); }
    private sealed class Handler(Func<HttpRequestMessage, HttpResponseMessage> send) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token) => Task.FromResult(send(request));
    }
    private sealed class CountingTokens : IAccessTokens
    {
        public int Reads;
        public Task<string> GetAsync(CancellationToken token) { Reads++; return Task.FromResult("secret-bearer-token"); }
    }
    private sealed class BrokenErrorContent : HttpContent
    {
        protected override bool TryComputeLength(out long length) { length = 0; return false; }
        protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context) =>
            Task.FromException(new HttpRequestException("https://private.1drv.com/secret-file?token=secret-bearer-token"));
        protected override Task<Stream> CreateContentReadStreamAsync() =>
            Task.FromException<Stream>(new HttpRequestException("https://private.1drv.com/secret-file?token=secret-bearer-token"));
        protected override Task<Stream> CreateContentReadStreamAsync(CancellationToken token) => CreateContentReadStreamAsync();
    }
    private static HttpResponseMessage Root() => new(HttpStatusCode.OK) { Content = new StringContent("{\"id\":\"root\",\"name\":\"root\",\"folder\":{}}") };
    private static HttpResponseMessage Throttle(int seconds) => new((HttpStatusCode)429)
    { Headers = { RetryAfter = new RetryConditionHeaderValue(TimeSpan.FromSeconds(seconds)) }, Content = new StringContent("{}") };

    [TestMethod]
    public async Task RetryAfterIsHonoredAndRecordedWithoutRealWaiting()
    {
        var clock = new Clock(); var attempts = 0;
        using var drive = new GraphDrive(new Tokens(), "drive", new Handler(_ => ++attempts == 1 ? Throttle(7) : Root()),
            new CloudRetryGate(clock, clock.Wait));
        await drive.RootAsync(default);
        Assert.AreEqual(2L, drive.NetworkMetrics.Requests);
        Assert.AreEqual(1L, drive.NetworkMetrics.Retries);
        Assert.AreEqual(7000L, drive.NetworkMetrics.RetryWaitMilliseconds);
    }

    [TestMethod]
    public async Task ThrottleWaitIsSharedByHostAndCanBeCancelled()
    {
        var clock = new Clock(); var gate = new CloudRetryGate(clock, clock.Wait);
        gate.Defer("graph.microsoft.com", new RetryConditionHeaderValue(clock.Now.AddSeconds(20)), 0);
        gate.Defer("graph.microsoft.com", new RetryConditionHeaderValue(TimeSpan.FromSeconds(1)), 0);
        Assert.AreEqual(0L, await gate.WaitAsync("test.1drv.com", default));
        Assert.AreEqual(20000L, await gate.WaitAsync("graph.microsoft.com", default));
        Assert.AreEqual(0L, await gate.WaitAsync("graph.microsoft.com", default));
        var waiting = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var cancellable = new CloudRetryGate(clock, (delay, token) => { waiting.TrySetResult(); return Task.Delay(Timeout.Infinite, token); });
        cancellable.Defer("graph.microsoft.com", new RetryConditionHeaderValue(TimeSpan.FromHours(2)), 0);
        using var stop = new CancellationTokenSource();
        var blocked = cancellable.WaitAsync("graph.microsoft.com", stop.Token);
        await waiting.Task.WaitAsync(TimeSpan.FromSeconds(5));
        stop.Cancel();
        await Assert.ThrowsExactlyAsync<TaskCanceledException>(() => blocked);
    }

    [TestMethod]
    public async Task TransientReadFailureRetriesWithBoundedJitter()
    {
        var clock = new Clock(); var count = 0;
        using var drive = new GraphDrive(new Tokens(), "drive", new Handler(_ => ++count == 1 ? throw new HttpRequestException("offline") : Root()),
            new CloudRetryGate(clock, clock.Wait));
        await drive.RootAsync(default);
        Assert.AreEqual(2, count); Assert.AreEqual(1L, drive.NetworkMetrics.Retries);
        Assert.IsTrue(drive.NetworkMetrics.RetryWaitMilliseconds is >= 2000 and <= 3000);
    }

    [TestMethod]
    public async Task RepeatedThrottleStopsAfterFiveAttempts()
    {
        var clock = new Clock();
        using var drive = new GraphDrive(new Tokens(), "drive", new Handler(_ => Throttle(0)), new CloudRetryGate(clock, clock.Wait));
        await Assert.ThrowsExactlyAsync<CloudRequestException>(() => drive.RootAsync(default));
        Assert.AreEqual(5L, drive.NetworkMetrics.Requests); Assert.AreEqual(4L, drive.NetworkMetrics.Retries);
    }

    [TestMethod]
    public async Task ExhaustedThrottleCarriesTheDeadlineIntoTheNextOperation()
    {
        var clock = new Clock();
        using var drive = new GraphDrive(new Tokens(), "drive", new Handler(_ => Throttle(17)), new CloudRetryGate(clock, clock.Wait));
        var error = await Assert.ThrowsExactlyAsync<CloudRequestException>(() => drive.RootAsync(default));
        Assert.AreEqual(clock.Now.AddSeconds(17), error.RetryNotBeforeUtc);
        Assert.AreEqual(5L, drive.NetworkMetrics.Requests);
    }

    [TestMethod]
    public async Task ExhaustedReadBackoffWithoutAHeaderStillCarriesItsDeadline()
    {
        var clock = new Clock();
        using var drive = new GraphDrive(new Tokens(), "drive", new Handler(_ => new(HttpStatusCode.ServiceUnavailable)
            { Content = new StringContent("{}") }), new CloudRetryGate(clock, clock.Wait));
        var error = await Assert.ThrowsExactlyAsync<CloudRequestException>(() => drive.RootAsync(default));
        Assert.IsNotNull(error.RetryNotBeforeUtc);
        Assert.IsTrue(error.RetryNotBeforeUtc >= clock.Now.AddSeconds(32));
        Assert.IsTrue(error.RetryNotBeforeUtc <= clock.Now.AddSeconds(33));
    }

    [TestMethod]
    public async Task AmbiguousWriteWithRetryAfterPreservesCooldownWithoutReplayingWrite()
    {
        var writes = 0; var earliest = DateTimeOffset.UtcNow;
        using var drive = new GraphDrive(new Tokens(), "drive", new Handler(request =>
        {
            if (request.Method == HttpMethod.Get) return new(HttpStatusCode.NotFound);
            writes++;
            return new(HttpStatusCode.ServiceUnavailable)
            {
                Headers = { RetryAfter = new RetryConditionHeaderValue(TimeSpan.FromMinutes(2)) },
                Content = new StringContent("{}")
            };
        }));
        var error = await Assert.ThrowsExactlyAsync<CloudRequestException>(() => drive.FolderAsync("root", "new", default));
        Assert.AreEqual(1, writes);
        Assert.IsTrue(error.RetryNotBeforeUtc >= earliest.AddMinutes(2));
        Assert.IsTrue(error.RetryNotBeforeUtc <= DateTimeOffset.UtcNow.AddMinutes(2));
        Assert.IsTrue(drive.RetryNotBeforeUtc >= earliest.AddMinutes(2));
        Assert.AreEqual(0L, drive.NetworkMetrics.Retries);
    }

    [TestMethod]
    public async Task AutomaticLongThrottleReturnsAfterOneRequestWithoutHoldingTheWait()
    {
        var clock = new Clock(); var waits = 0;
        var gate = new CloudRetryGate(clock, (_, _) => { waits++; throw new AssertFailedException("A long automatic cooldown must yield to scheduling."); });
        using var drive = new GraphDrive(new Tokens(), "drive", new Handler(_ => Throttle(120)), gate) { YieldLongRetryWaits = true };
        var before = clock.Now;
        var error = await Assert.ThrowsExactlyAsync<CloudRequestException>(() => drive.RootAsync(default));
        Assert.AreEqual((HttpStatusCode)429, error.StatusCode);
        Assert.AreEqual(before.AddMinutes(2), drive.RetryNotBeforeUtc);
        Assert.IsTrue(error.RetryNotBeforeUtc >= before.AddMinutes(2));
        Assert.AreEqual(1L, drive.NetworkMetrics.Requests);
        Assert.AreEqual(0L, drive.NetworkMetrics.Retries);
        Assert.AreEqual(0, waits);
        Assert.AreEqual(before, clock.Now);
    }

    [TestMethod]
    public async Task AutomaticExistingCooldownDoesNotFetchCredentialsOrSendARequest()
    {
        var clock = new Clock(); var gate = new CloudRetryGate(clock, clock.Wait); var tokens = new CountingTokens();
        gate.Defer("graph.microsoft.com", new RetryConditionHeaderValue(TimeSpan.FromHours(1)), 0);
        var requests = 0;
        using var drive = new GraphDrive(tokens, "secret-drive", new Handler(_ => { requests++; return Root(); }), gate) { YieldLongRetryWaits = true };
        var error = await Assert.ThrowsExactlyAsync<CloudCooldownException>(() => drive.FindAsync("secret-parent", "secret-file", default));
        Assert.AreEqual(clock.Now.AddHours(1), error.RetryNotBeforeUtc);
        Assert.AreEqual(0, requests);
        Assert.AreEqual(0, tokens.Reads);
        Assert.AreEqual(0L, drive.NetworkMetrics.Requests);
        Assert.DoesNotContain("secret", error.ToString());
        Assert.DoesNotContain("https://", error.ToString());
        Assert.IsNull(error.InnerException);
    }

    [TestMethod]
    public async Task NewDriveInstanceKeepsTheSharedHostDeadlineUntilItExpires()
    {
        var clock = new Clock(); var gate = new CloudRetryGate(clock, clock.Wait);
        using (var first = new GraphDrive(new Tokens(), "drive", new Handler(_ => Throttle(20)), gate) { YieldLongRetryWaits = true })
            await Assert.ThrowsExactlyAsync<CloudRequestException>(() => first.RootAsync(default));
        var requests = 0;
        using var next = new GraphDrive(new Tokens(), "another-drive", new Handler(_ => { requests++; return Root(); }), gate) { YieldLongRetryWaits = true };
        Assert.AreEqual(clock.Now.AddSeconds(20), ((ICloudDiagnostics)next).RetryNotBeforeUtc);
        await Assert.ThrowsExactlyAsync<CloudCooldownException>(() => next.RootAsync(default));
        Assert.AreEqual(0, requests);
        clock.Now += TimeSpan.FromSeconds(20);
        Assert.IsNull(next.RetryNotBeforeUtc);
        await next.RootAsync(default);
        Assert.AreEqual(1, requests);
    }

    [TestMethod]
    public async Task AConcurrentExtensionYieldsAfterAtMostFiveSecondsOfAutomaticWaiting()
    {
        var clock = new Clock(); var waits = 0; var began = clock.Now;
        CloudRetryGate? gate = null;
        gate = new(clock, (delay, token) =>
        {
            clock.Wait(delay, token);
            if (++waits == 1) gate!.Defer("graph.microsoft.com", new RetryConditionHeaderValue(TimeSpan.FromSeconds(4)), 0);
            return Task.CompletedTask;
        });
        gate.Defer("graph.microsoft.com", new RetryConditionHeaderValue(TimeSpan.FromSeconds(4)), 0);
        var error = await Assert.ThrowsExactlyAsync<CloudCooldownException>(() => gate.WaitAsync("graph.microsoft.com", default, yieldLongWaits: true));
        Assert.AreEqual(5000L, error.WaitedMilliseconds);
        Assert.AreEqual(began.AddSeconds(8), error.RetryNotBeforeUtc);
        Assert.AreEqual(began.AddSeconds(5), clock.Now);
        Assert.AreEqual(2, waits);
    }

    [TestMethod]
    public async Task UnreadableErrorBodyPreservesTheReceivedStatusAndCooldownWithoutSecrets()
    {
        var clock = new Clock(); var gate = new CloudRetryGate(clock, clock.Wait);
        using var drive = new GraphDrive(new Tokens(), "secret-drive", new Handler(_ => new((HttpStatusCode)429)
        {
            Headers = { RetryAfter = new RetryConditionHeaderValue(TimeSpan.FromMinutes(3)) },
            Content = new BrokenErrorContent()
        }), gate) { YieldLongRetryWaits = true };
        var error = await Assert.ThrowsExactlyAsync<CloudRequestException>(() => drive.GetAsync("secret-file", default));
        Assert.AreEqual((HttpStatusCode)429, error.StatusCode);
        Assert.IsTrue(error.RetryNotBeforeUtc >= clock.Now.AddMinutes(3));
        Assert.AreEqual("unknown", error.ErrorCode);
        Assert.IsNull(error.InnerException);
        Assert.DoesNotContain("secret", error.ToString());
        Assert.DoesNotContain("https://", error.ToString());
    }

    [TestMethod]
    public async Task FakeHandlerInstancesNeverShareAProductionCooldown()
    {
        using var first = new GraphDrive(new Tokens(), "drive", new Handler(_ => Throttle(3600))) { YieldLongRetryWaits = true };
        await Assert.ThrowsExactlyAsync<CloudRequestException>(() => first.RootAsync(default));
        using var second = new GraphDrive(new Tokens(), "drive", new Handler(_ => Root())) { YieldLongRetryWaits = true };
        Assert.IsNull(second.RetryNotBeforeUtc);
        await second.RootAsync(default);
        Assert.AreEqual(1L, second.NetworkMetrics.Requests);
    }

    [TestMethod]
    public async Task AmbiguousWriteFailureIsNotAutomaticallyReplayed()
    {
        var clock = new Clock(); var writes = 0;
        using var drive = new GraphDrive(new Tokens(), "drive", new Handler(request =>
        {
            if (request.Method == HttpMethod.Get) return new(HttpStatusCode.NotFound);
            writes++; return new(HttpStatusCode.ServiceUnavailable) { Content = new StringContent("{}") };
        }), new CloudRetryGate(clock, clock.Wait));
        await Assert.ThrowsExactlyAsync<CloudRequestException>(() => drive.FolderAsync("root", "new", default));
        Assert.AreEqual(1, writes); Assert.AreEqual(0L, drive.NetworkMetrics.Retries);
    }

    [TestMethod]
    public async Task InitialDeltaGetsLatestCursorWithoutEnumeratingEntireDrive()
    {
        using var drive = new GraphDrive(new Tokens(), "drive", new Handler(request =>
        {
            Assert.Contains("token=latest", request.RequestUri!.Query);
            return new(HttpStatusCode.OK) { Content = new StringContent("{\"value\":[],\"@odata.deltaLink\":\"https://graph.microsoft.com/v1.0/drives/drive/root/delta?token=next\"}") };
        }));
        var result = await drive.ChangesAsync(null, default);
        Assert.IsTrue(result.Changed); Assert.Contains("token=next", result.Cursor);
    }
}
