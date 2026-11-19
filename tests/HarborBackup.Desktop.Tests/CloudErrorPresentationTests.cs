using System.Net;
using HarborBackup.Desktop.Localization;
using HarborBackup.Desktop.Presentation;
using HarborBackup.OneDrive.Authentication;
using HarborBackup.OneDrive.Transport;

namespace HarborBackup.Windows.Tests;

[TestClass]
[DoNotParallelize]
public sealed class CloudErrorPresentationTests
{
    private sealed class Tokens : IAccessTokens
    {
        public Task<string> GetAsync(CancellationToken token) => Task.FromResult("synthetic-token");
    }
    private sealed class Handler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.BadRequest)
            {
                Content = new StringContent("""{"error":{"code":"invalidRequest","message":"secret-token https://example.invalid/secret","innerError":{"request-id":"01234567-89ab-cdef-0123-456789abcdef"}}}""")
            });
    }

    private sealed class BrokenUpload : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token) =>
            request.Method == HttpMethod.Get ? Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound))
            : throw new HttpRequestException(HttpRequestError.SecureConnectionError, "secret-url?access_token=secret",
                new System.Security.Authentication.AuthenticationException("secret-inner-message"));
    }

    [TestMethod]
    public async Task NetworkMessagesAreLocalizedAndTechnicalDiagnosticsStayOutOfTheUi()
    {
        using var drive = new GraphDrive(new Tokens(), "synthetic-drive", new BrokenUpload());
        using var source = new MemoryStream([1, 2, 3]);
        var error = await Assert.ThrowsExactlyAsync<CloudTransportException>(() => drive.UploadAsync("parent", "test", source, null, _ => { }, default));
        var language = UiText.Language;
        try
        {
            UiText.SetLanguage("zh-CN");
            var chinese = CloudAccountPresentation.Error(error);
            Assert.Contains("建立安全连接", chinese);
            UiText.SetLanguage("en-US");
            var english = CloudAccountPresentation.Error(error);
            Assert.Contains("secure connection", english);
            foreach (var message in new[] { chinese, english })
            {
                Assert.DoesNotContain("SecureConnectionError", message);
                Assert.DoesNotContain("AuthenticationException", message);
                Assert.DoesNotContain("graph.microsoft.com", message);
                Assert.DoesNotContain("secret", message);
                Assert.DoesNotContain("access_token", message);
            }
            Assert.Contains("graph.microsoft.com", error.Message);
            Assert.Contains("SecureConnectionError", error.Message);
            var row = new DetailRow("project", "file.txt", "failed", error.Message, true);
            Assert.AreEqual(english, row.DisplayDetails);
            Assert.AreEqual(error.Message, row.Details);
        }
        finally { UiText.SetLanguage(language); }
    }

    [TestMethod]
    public void OfflineHintsDoNotHideAuthorizationLocalErrorsOrUserCancellation()
    {
        var language = UiText.Language;
        try
        {
            foreach (var lang in new[] { "zh-CN", "en-US" })
            {
                UiText.SetLanguage(lang);
                var message = CloudAccountPresentation.Error(new CloudOfflineException());
                Assert.Contains(lang == "zh-CN" ? "连接网络后重试" : "Connect to a network", message);
                var report = new HarborBackup.Core.Domain.PreviewReport([new("project", [], [new("CloudPreviewFailed", CloudNetworkAvailability.Offline)], new())]);
                Assert.IsTrue(ReportPresentation.OfflineOnly(report));
                var mixed = new HarborBackup.Core.Domain.PreviewReport([new("project", [], [new("CloudPreviewFailed", CloudNetworkAvailability.Offline),
                    new("CloudPreviewFailed", "Local disk unavailable")], new())]);
                Assert.IsFalse(ReportPresentation.OfflineOnly(mixed));
            }
            Assert.IsNull(CloudNetworkAvailability.FriendlyMessage(new AccountAuthorizationRequiredException("auth required")));
            Assert.IsNull(CloudNetworkAvailability.FriendlyMessage(new IOException("disk full")));
            Assert.IsNull(CloudNetworkAvailability.FriendlyMessage(new OperationCanceledException()));
            Assert.IsNull(CloudNetworkAvailability.FriendlyMessage(new HttpRequestException("forbidden", null, HttpStatusCode.Forbidden)));
        }
        finally { UiText.SetLanguage(language); }
    }

    [TestMethod]
    public async Task CloudErrorDetailsAreLocalizedAndDoNotExposeRawServerMessage()
    {
        using var drive = new GraphDrive(new Tokens(), "synthetic-drive", new Handler());
        var error = await Assert.ThrowsExactlyAsync<CloudRequestException>(() => drive.RootAsync(default));
        var language = UiText.Language;
        try
        {
            UiText.SetLanguage("zh-CN");
            var chinese = CloudAccountPresentation.Error(error);
            Assert.Contains("读取云端项目", chinese);
            Assert.Contains("错误代码：invalidRequest", chinese);
            UiText.SetLanguage("en-US");
            var english = CloudAccountPresentation.Error(error);
            Assert.Contains("Operation: Read cloud item", english);
            Assert.Contains("Error code: invalidRequest", english);
            foreach (var message in new[] { chinese, english })
            {
                Assert.Contains("400", message);
                Assert.Contains("01234567-89ab-cdef-0123-456789abcdef", message);
                Assert.DoesNotContain("secret", message);
                Assert.DoesNotContain("https://", message);
            }
        }
        finally { UiText.SetLanguage(language); }
    }
}
