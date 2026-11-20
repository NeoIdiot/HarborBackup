using System.Net;
using HarborBackup.Core.Domain;
using HarborBackup.OneDrive.Authentication;
using HarborBackup.OneDrive.Transport;
using HarborBackup.Windows.Integration;

namespace HarborBackup.Windows.Tests;

[TestClass]
public sealed class FileRecoveryClassificationTests
{
    private sealed class Tokens : IAccessTokens
    {
        public Task<string> GetAsync(CancellationToken token) => Task.FromResult("synthetic-token");
    }
    private sealed class Handler(HttpStatusCode status, string code) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token) =>
            Task.FromResult(request.Method == HttpMethod.Get ? new(HttpStatusCode.NotFound) : new HttpResponseMessage(status)
            {
                Headers = { RetryAfter = new System.Net.Http.Headers.RetryConditionHeaderValue(TimeSpan.Zero) },
                Content = new StringContent(System.Text.Json.JsonSerializer.Serialize(new { error = new { code } }))
            });
    }
    private static async Task<CloudRequestException> Failure(HttpStatusCode status, string code)
    {
        using var drive = new GraphDrive(new Tokens(), "synthetic", new Handler(status, code));
        return await Assert.ThrowsExactlyAsync<CloudRequestException>(() => drive.FolderAsync("parent", "target", default));
    }
    [TestMethod]
    [DataRow(HttpStatusCode.Forbidden, "accessDenied")]
    [DataRow(HttpStatusCode.InsufficientStorage, "insufficientStorage")]
    [DataRow(HttpStatusCode.ServiceUnavailable, "quotaLimitReached")]
    public async Task PermissionAndQuotaFailuresRequireRepairInsteadOfNetworkRetry(HttpStatusCode status, string code)
    {
        var error = await Failure(status, code);
        var kind = FileFailurePolicy.Classify(error, false);
        Assert.AreEqual(FileFailureKind.RecoveryRequired, kind);
        Assert.AreEqual(RecoveryDisposition.RepairRequired, FileFailurePolicy.Recovery(kind, error));
    }

    [TestMethod]
    [DataRow(HttpStatusCode.TooManyRequests)]
    [DataRow(HttpStatusCode.RequestTimeout)]
    [DataRow(HttpStatusCode.ServiceUnavailable)]
    public async Task TemporaryServiceErrorsRemainAutomaticallyRetryable(HttpStatusCode status)
    {
        var error = await Failure(status, "temporary");
        var kind = FileFailurePolicy.Classify(error, false);
        Assert.AreEqual(FileFailureKind.Network, kind);
        Assert.AreEqual(RecoveryDisposition.AutomaticRetry, FileFailurePolicy.Recovery(kind, error));
    }

    [TestMethod]
    public async Task ReauthorizationIsARepairActionRatherThanAVersionChoice()
    {
        var error = await Failure(HttpStatusCode.Unauthorized, "unauthorized");
        Assert.AreEqual(FileFailureKind.Authorization, FileFailurePolicy.Classify(error, false));
        Assert.AreEqual(RecoveryDisposition.RepairRequired, FileFailurePolicy.Recovery(FileFailureKind.Authorization, error));
        Assert.AreEqual(RecoveryDisposition.RepairRequired, FileFailurePolicy.Recovery(FileFailureKind.FileUnavailable, new IOException("Unavailable input")));
        var conflict = new DeferredOperationException("Versions require a new selection.");
        Assert.AreEqual(FileFailureKind.None, FileFailurePolicy.Classify(conflict, false));
        Assert.AreEqual(RecoveryDisposition.AwaitingConfirmation, FileFailurePolicy.Recovery(FileFailureKind.None, conflict));
    }
}
