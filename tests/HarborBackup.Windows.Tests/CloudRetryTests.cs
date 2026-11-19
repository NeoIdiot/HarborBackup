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
