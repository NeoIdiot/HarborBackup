using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;

namespace HarborBackup.OneDrive.Transport;

public sealed class CloudOfflineException() : IOException(CloudNetworkAvailability.Offline) { }

public static class CloudNetworkAvailability
{
    public const string Offline = "当前未连接网络，请连接网络后重试。";
    public const string Unreachable = "暂时无法连接 OneDrive，请检查网络或代理设置后重试。";
    public const string SecureConnection = "无法与 OneDrive 建立安全连接，请检查系统时间或代理设置后重试。";
    public const string ServiceUnavailable = "OneDrive 服务暂时不可用，请稍后重试。";

    // An available interface is only a hint; the actual OneDrive request determines reachability.
    public static void EnsureAvailable(CancellationToken token, Func<bool>? available = null)
    {
        token.ThrowIfCancellationRequested();
        bool online;
        try { online = (available ?? NetworkInterface.GetIsNetworkAvailable)(); }
        catch (NetworkInformationException) { return; }
        if (!online) throw new CloudOfflineException();
    }

    public static string? FriendlyMessage(Exception error)
    {
        if (error is OperationCanceledException) return null;
        for (Exception? probe = error; probe is not null; probe = probe.InnerException)
            if (probe is CloudOfflineException) return Offline;
        for (Exception? current = error; current is not null; current = current.InnerException)
        {
            if (current is CloudRequestException service && (int)service.StatusCode is 502 or 503 or 504)
                return ServiceUnavailable;
            if (current is CloudTransportException transport && transport.Causes.Contains("AuthenticationException/TLS", StringComparison.Ordinal)) return SecureConnection;
            if (current is HttpRequestException request)
            {
                if (request.StatusCode is { } status)
                    return status is HttpStatusCode.BadGateway or HttpStatusCode.ServiceUnavailable or HttpStatusCode.GatewayTimeout ? ServiceUnavailable : null;
                return request.HttpRequestError == HttpRequestError.SecureConnectionError ? SecureConnection : Unreachable;
            }
            if (current is SocketException) return Unreachable;
        }
        return null;
    }

    // Existing reports keep their sanitized technical diagnostics; ordinary UI shows a short summary.
    public static string? FriendlyMessage(string diagnostic)
    {
        if (diagnostic is Offline or Unreachable or SecureConnection or ServiceUnavailable) return diagnostic;
        if (diagnostic.StartsWith("OneDrive network failure: host=", StringComparison.Ordinal))
            return diagnostic.Contains("SecureConnectionError", StringComparison.Ordinal) || diagnostic.Contains("AuthenticationException/TLS", StringComparison.Ordinal)
                ? SecureConnection : Unreachable;
        if (diagnostic.StartsWith("OneDrive: ", StringComparison.Ordinal) && new[] { 502, 503, 504 }.Any(status => diagnostic.Contains($"failed (HTTP {status};", StringComparison.Ordinal)))
            return ServiceUnavailable;
        return null;
    }
}
