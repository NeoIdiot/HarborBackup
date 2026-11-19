using System.Net;
using System.Net.Sockets;
using System.Security.Authentication;
using HarborBackup.OneDrive.Authentication;
using HarborBackup.OneDrive.Transport;

namespace HarborBackup.Windows.Tests;

[TestClass]
public sealed class NetworkDiagnosticTests
{
    private const string Secret = "private-path?token=secret-password";
    private sealed class Tokens : IAccessTokens { public Task<string> GetAsync(CancellationToken token) => Task.FromResult("bearer-secret"); }
    private sealed class Handler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> send) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token) => send(request, token);
    }
    private static Exception Tls() => new HttpRequestException(HttpRequestError.SecureConnectionError, Secret,
        new AuthenticationException(Secret, new SocketException((int)SocketError.ConnectionReset)));
    private sealed class Clock : TimeProvider
    {
        private DateTimeOffset now = DateTimeOffset.UtcNow;
        public override DateTimeOffset GetUtcNow() => now;
        public Task Wait(TimeSpan delay, CancellationToken token) { token.ThrowIfCancellationRequested(); now += delay; return Task.CompletedTask; }
    }
    private static CloudRetryGate FastGate() { var clock = new Clock(); return new(clock, clock.Wait); }
    private static HttpResponseMessage Root() => new(HttpStatusCode.OK)
    { Content = new StringContent("{\"id\":\"root\",\"name\":\"root\",\"folder\":{}}") };

    private sealed class CountingTokens : IAccessTokens
    {
        public int Calls;
        public Task<string> GetAsync(CancellationToken token) { Calls++; return Task.FromResult("synthetic-token"); }
    }

    [TestMethod]
    public async Task OfflinePreflightDoesNotAccessTokensOrSendRequestsAndRecoversOnRetry()
    {
        var online = false; var requests = 0; var tokens = new CountingTokens();
        using var drive = new GraphDrive(tokens, "drive", new Handler((_, _) => { requests++; return Task.FromResult(Root()); }), FastGate(), () => online);
        await Assert.ThrowsExactlyAsync<CloudOfflineException>(() => drive.RootAsync(default));
        Assert.AreEqual(0, tokens.Calls); Assert.AreEqual(0, requests); Assert.AreEqual(0L, drive.NetworkMetrics.Retries);
        online = true;
        Assert.AreEqual("root", (await drive.RootAsync(default)).Id);
        Assert.AreEqual(1, requests); Assert.AreEqual(1, tokens.Calls);
        online = false;
        using var cancelled = new CancellationTokenSource(); cancelled.Cancel();
        await Assert.ThrowsExactlyAsync<OperationCanceledException>(() => drive.RootAsync(cancelled.Token));
        Assert.AreEqual(1, requests);
    }

    [TestMethod]
    public async Task AuthenticationRequestsAlsoStopOfflineAndUnavailableInterfaceDetectionFallsBackToTheRequest()
    {
        var sent = 0;
        using var offline = new HttpClient(new NetworkDiagnosticHandler(new Handler((_, _) => { sent++; return Task.FromResult(Root()); }), true, () => false));
        await Assert.ThrowsExactlyAsync<CloudOfflineException>(() => offline.GetAsync("https://login.microsoftonline.com/"));
        Assert.AreEqual(0, sent);
        using var unknown = new HttpClient(new NetworkDiagnosticHandler(new Handler((_, _) => { sent++; return Task.FromResult(Root()); }), false,
            () => throw new System.Net.NetworkInformation.NetworkInformationException()));
        using var response = await unknown.GetAsync("https://graph.microsoft.com/");
        Assert.AreEqual(HttpStatusCode.OK, response.StatusCode); Assert.AreEqual(1, sent);
        var wrapped = new Microsoft.Identity.Client.MsalClientException("http_failure", Secret, new CloudOfflineException());
        Assert.AreEqual(CloudNetworkAvailability.Offline, CloudNetworkAvailability.FriendlyMessage(wrapped));
        Assert.AreEqual(CloudNetworkAvailability.Unreachable, CloudNetworkAvailability.FriendlyMessage(new HttpRequestException(HttpRequestError.NameResolutionError, Secret)));
        Assert.AreEqual(CloudNetworkAvailability.ServiceUnavailable, CloudNetworkAvailability.FriendlyMessage(new HttpRequestException("server", null, HttpStatusCode.ServiceUnavailable)));
    }

    [TestMethod]
    public async Task FinalTlsFailurePreservesEndpointAttemptsAndSafeInnerCodes()
    {
        using var drive = new GraphDrive(new Tokens(), "secret-drive",
            new Handler((_, _) => throw Tls()), FastGate());
        var failure = await Assert.ThrowsExactlyAsync<CloudTransportException>(() => drive.RootAsync(default));
        Assert.AreEqual("graph.microsoft.com", failure.Host);
        Assert.AreEqual("Read cloud item", failure.Operation);
        Assert.AreEqual("Send/Headers", failure.Phase);
        Assert.AreEqual(5, failure.Attempt);
        Assert.Contains("SecureConnectionError", failure.Causes);
        Assert.Contains("AuthenticationException/TLS", failure.Causes);
        Assert.Contains("ConnectionReset", failure.Causes);
        Assert.AreEqual(4L, drive.NetworkMetrics.Retries);
        Assert.IsNull(failure.InnerException);
        Safe(failure);
    }

    [TestMethod]
    public async Task UploadEndpointAndAuthenticationHaveDistinctOperations()
    {
        foreach (var auth in new[] { false, true })
        {
            using var client = new HttpClient(new NetworkDiagnosticHandler(new Handler((_, _) => throw Tls()), auth));
            var uri = auth ? "https://login.microsoftonline.com/consumers/" + Secret : "https://test.1drv.com/" + Secret;
            var failure = await Assert.ThrowsExactlyAsync<CloudTransportException>(() => client.PutAsync(uri, new StringContent("secret-body")));
            Assert.AreEqual(auth ? "Authentication" : "Upload file content", failure.Operation);
            Assert.AreEqual(auth ? "login.microsoftonline.com" : "test.1drv.com", failure.Host);
            Safe(failure);
        }
    }

    [TestMethod]
    public async Task FailedWritesAreNotReplayedAndDnsIsRecognizable()
    {
        var writes = 0;
        using var drive = new GraphDrive(new Tokens(), "drive", new Handler((request, _) =>
        {
            if (request.Method == HttpMethod.Get) return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));
            writes++;
            throw new HttpRequestException(HttpRequestError.NameResolutionError, Secret, new SocketException((int)SocketError.HostNotFound));
        }));
        var failure = await Assert.ThrowsExactlyAsync<CloudTransportException>(() => drive.FolderAsync("root", "folder", default));
        Assert.AreEqual(1, writes);
        Assert.AreEqual("Create cloud folder", failure.Operation);
        Assert.Contains("NameResolutionError", failure.Causes);
        Safe(failure);
    }

    [TestMethod]
    public async Task CallerCancellationRemainsCancellation()
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var stop = new CancellationTokenSource();
        using var drive = new GraphDrive(new Tokens(), "drive", new Handler(async (_, token) =>
        {
            entered.SetResult();
            await Task.Delay(Timeout.Infinite, token);
            return Root();
        }));
        var read = drive.RootAsync(stop.Token);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        stop.Cancel();
        await Assert.ThrowsAsync<OperationCanceledException>(() => read);
        Assert.AreEqual(0L, drive.NetworkMetrics.Retries);
    }

    [TestMethod]
    public async Task TimeoutHasContextAndBoundedRetry()
    {
        using var drive = new GraphDrive(new Tokens(), "drive",
            new Handler((_, _) => throw new TaskCanceledException(Secret)),
            FastGate());
        var failure = await Assert.ThrowsExactlyAsync<CloudTransportException>(() => drive.RootAsync(default));
        Assert.AreEqual("Timeout", failure.Phase);
        Assert.AreEqual(5, failure.Attempt);
        Assert.AreEqual("graph.microsoft.com", failure.Host);
        Safe(failure);
    }

    [TestMethod]
    public async Task ResponseBodyFailureIsDiagnosedButDestinationDiskFailureStaysLocal()
    {
        using var client = new HttpClient(new NetworkDiagnosticHandler(new Handler((_, _) => Task.FromResult(
            new HttpResponseMessage(HttpStatusCode.OK) { Content = new StreamContent(new BrokenStream()) }))));
        var failure = await Assert.ThrowsExactlyAsync<CloudTransportException>(() => client.GetStringAsync("https://test.1drv.com/" + Secret));
        Assert.AreEqual("ResponseBody", failure.Phase);
        Assert.Contains("ConnectionReset", failure.Causes);
        Safe(failure);

        using var healthy = new HttpClient(new NetworkDiagnosticHandler(new Handler((_, _) => Task.FromResult(Root()))));
        using var response = await healthy.GetAsync("https://graph.microsoft.com/v1.0/me/drive", HttpCompletionOption.ResponseHeadersRead);
        using var disk = new BrokenDestination();
        using var body = await response.Content.ReadAsStreamAsync();
        var local = await Assert.ThrowsExactlyAsync<IOException>(() => body.CopyToAsync(disk));
        Assert.AreEqual("Synthetic disk full", local.Message);
    }

    [TestMethod]
    public async Task HttpErrorKeepsStatusRequestIdAndHostWithoutRawResponse()
    {
        using var drive = new GraphDrive(new Tokens(), "drive", new Handler((_, _) =>
        {
            var response = new HttpResponseMessage(HttpStatusCode.BadGateway) { Content = new StringContent(Secret) };
            response.Headers.Add("request-id", "01234567-89ab-cdef-0123-456789abcdef");
            return Task.FromResult(response);
        }));
        var failure = await Assert.ThrowsExactlyAsync<CloudRequestException>(() => drive.RootAsync(default));
        Assert.AreEqual("graph.microsoft.com", failure.Host);
        Assert.AreEqual(1, failure.Attempt);
        Assert.AreEqual(HttpStatusCode.BadGateway, failure.StatusCode);
        Assert.Contains("01234567-89ab-cdef-0123-456789abcdef", failure.Message);
        Safe(failure);
    }

    [TestMethod]
    public async Task AuthenticationSdkWrappingCanBeUnwrappedWithoutExposingSdkMessage()
    {
        using var client = new HttpClient(new NetworkDiagnosticHandler(new Handler((_, _) => throw Tls()), authentication: true));
        var diagnostic = await Assert.ThrowsExactlyAsync<CloudTransportException>(() => client.GetAsync("https://login.microsoftonline.com/" + Secret));
        var sdk = new Microsoft.Identity.Client.MsalClientException("http_failure", Secret, diagnostic);
        var restored = CloudTransportException.Find(sdk, default)!;
        Assert.AreSame(diagnostic, restored);
        Assert.AreEqual("Authentication", restored.Operation);
        Safe(restored);
        var context = new NetworkRequestContext("login.microsoftonline.com", "Authentication", 0, System.Diagnostics.Stopwatch.GetTimestamp());
        var canceled = new NetworkCanceledException(context, default);
        Assert.AreEqual("Timeout", CloudTransportException.Find(canceled, default)!.Phase);
        using var stop = new CancellationTokenSource(); stop.Cancel();
        Assert.IsNull(CloudTransportException.Find(canceled, stop.Token));
    }

    private static void Safe(Exception error)
    {
        foreach (var secret in new[] { Secret, "bearer-secret", "secret-body", "secret-drive", "https://", "password" })
            Assert.DoesNotContain(secret, error.ToString());
    }

    private sealed class BrokenStream : MemoryStream
    {
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken token = default) =>
            ValueTask.FromException<int>(new IOException(Secret, new SocketException((int)SocketError.ConnectionReset)));
    }
    private sealed class BrokenDestination : MemoryStream
    {
        public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken token = default) =>
            ValueTask.FromException(new IOException("Synthetic disk full"));
    }
}
