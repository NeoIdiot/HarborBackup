using System.ComponentModel;
using System.Diagnostics;
using System.Net.Sockets;
using System.Security.Authentication;

namespace HarborBackup.OneDrive.Transport;

// Deliberately do not retain the original exception: Message, Data and ToString() may contain
// bearer tokens, account names, proxy passwords or preauthenticated URLs.
public sealed class CloudTransportException : HttpRequestException
{
    public string Host { get; }
    public string Operation { get; }
    public string Phase { get; }
    public int Attempt { get; }
    public long ElapsedMilliseconds { get; }
    public string Causes { get; }
    public string ProxyPolicy => "SystemDefault";
    internal CloudTransportException(NetworkRequestContext context, string phase, Exception error)
        : base(error is HttpRequestException request ? request.HttpRequestError : HttpRequestError.Unknown,
            $"OneDrive network failure: host={context.Host}; operation={context.Operation}; phase={phase}; " +
            $"attempt={context.Attempt}; elapsedMs={context.ElapsedMilliseconds}; proxyPolicy=SystemDefault; causes={Describe(error)}")
    {
        Host = context.Host; Operation = context.Operation; Phase = phase; Attempt = context.Attempt;
        ElapsedMilliseconds = context.ElapsedMilliseconds; Causes = Describe(error);
    }

    private static string Describe(Exception error)
    {
        var causes = new List<string>();
        for (Exception? current = error; current is not null && causes.Count < 8; current = current.InnerException)
        {
            var kind = current switch
            {
                HttpRequestException h => $"HttpRequestException/{h.HttpRequestError}",
                SocketException s => $"SocketException/{s.SocketErrorCode}/native={s.NativeErrorCode}",
                AuthenticationException => "AuthenticationException/TLS",
                Win32Exception w => $"Win32Exception/native={w.NativeErrorCode}",
                OperationCanceledException => "OperationCanceledException",
                IOException => "IOException",
                _ => "Exception"
            };
            causes.Add($"{kind}/HRESULT=0x{current.HResult:X8}");
        }
        return string.Join(" -> ", causes);
    }

    internal static CloudTransportException? Find(Exception error, CancellationToken caller)
    {
        for (Exception? current = error; current is not null; current = current.InnerException)
        {
            if (current is CloudTransportException diagnostic) return diagnostic;
            if (current is NetworkCanceledException canceled && !caller.IsCancellationRequested)
                return new(canceled.Context, "Timeout", canceled);
        }
        return null;
    }
}

internal sealed record NetworkRequestContext(string Host, string Operation, int Attempt, long Started)
{
    internal static readonly HttpRequestOptionsKey<string> OperationKey = new("HarborBackup.Operation");
    internal static readonly HttpRequestOptionsKey<int> AttemptKey = new("HarborBackup.Attempt");
    internal static readonly HttpRequestOptionsKey<DateTimeOffset> RetryNotBeforeKey = new("HarborBackup.RetryNotBeforeUtc");
    internal static readonly HttpRequestOptionsKey<NetworkRequestContext> ContextKey = new("HarborBackup.NetworkContext");
    public long ElapsedMilliseconds => (long)Stopwatch.GetElapsedTime(Started).TotalMilliseconds;

    public static NetworkRequestContext Capture(HttpRequestMessage? request, bool authentication = false)
    {
        var host = request?.RequestUri?.IdnHost;
        if (host is not { Length: > 0 and <= 253 } || !host.All(c => char.IsAsciiLetterOrDigit(c) || c is '.' or '-')) host = "unavailable";
        var attempt = request is not null && request.Options.TryGetValue(AttemptKey, out var value) ? value : 0;
        return new(host, authentication ? "Authentication" : CloudRequestException.OperationFor(request), attempt, Stopwatch.GetTimestamp());
    }
    public static NetworkRequestContext For(HttpRequestMessage? request) =>
        request is not null && request.Options.TryGetValue(ContextKey, out var value) ? value : Capture(request);
    public CloudTransportException Failure(Exception error, string phase) => error as CloudTransportException ?? new(this, phase, error);
}

// Keep cancellation semantics intact so callers can distinguish Stop from timeout.
internal sealed class NetworkCanceledException(NetworkRequestContext context, CancellationToken token)
    : OperationCanceledException("Cloud request canceled or timed out.", token)
{
    public NetworkRequestContext Context { get; } = context;
}
