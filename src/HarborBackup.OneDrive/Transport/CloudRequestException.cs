using System.Net;
using System.Text.Json;

namespace HarborBackup.OneDrive.Transport;

// Only structured, bounded diagnostic fields cross into UI/logs. Never retain a request,
// response body, bearer token, file path, or preauthenticated transfer URL in an exception.
public sealed class CloudRequestException : IOException
{
    public HttpStatusCode StatusCode { get; }
    public string Operation { get; }
    public string ErrorCode { get; }
    public string? RequestId { get; }
    public string Host { get; }
    public int Attempt { get; }
    public long ElapsedMilliseconds { get; }
    public DateTimeOffset? RetryNotBeforeUtc { get; }

    private CloudRequestException(HttpStatusCode status, string operation, string code, string? requestId, NetworkRequestContext context,
        DateTimeOffset? retryNotBeforeUtc)
        : base($"OneDrive: {operation} failed (HTTP {(int)status}; code: {code}; request ID: {requestId ?? "unavailable"}; host: {context.Host}; attempt: {context.Attempt}; elapsedMs: {context.ElapsedMilliseconds}).")
    {
        StatusCode = status; Operation = operation; ErrorCode = code; RequestId = requestId;
        Host = context.Host; Attempt = context.Attempt; ElapsedMilliseconds = context.ElapsedMilliseconds;
        RetryNotBeforeUtc = retryNotBeforeUtc;
    }

    internal static async Task<CloudRequestException> ReadAsync(HttpResponseMessage response, CancellationToken token)
    {
        var operation = OperationFor(response.RequestMessage);
        var now = DateTimeOffset.UtcNow;
        DateTimeOffset? notBefore = response.Headers.RetryAfter?.Date;
        if (response.Headers.RetryAfter?.Delta is { } delta)
            notBefore = delta > DateTimeOffset.MaxValue - now ? DateTimeOffset.MaxValue : now + delta;
        if (response.RequestMessage is { } request && request.Options.TryGetValue(NetworkRequestContext.RetryNotBeforeKey, out var gateDue) &&
            (notBefore is null || gateDue > notBefore)) notBefore = gateDue;
        var code = "unknown";
        string? requestId = response.Headers.TryGetValues("request-id", out var ids) ? SafeId(ids.FirstOrDefault()) : null;
        const int limit = 32 * 1024;
        try
        {
            await using var stream = await response.Content.ReadAsStreamAsync(token);
            var bytes = new byte[limit + 1];
            var length = 0;
            while (length < bytes.Length)
            {
                var count = await stream.ReadAsync(bytes.AsMemory(length), token);
                if (count == 0) break;
                length += count;
            }
            if (length <= limit)
            {
                using var document = JsonDocument.Parse(bytes.AsMemory(0, length));
                if (document.RootElement.ValueKind == JsonValueKind.Object &&
                    document.RootElement.TryGetProperty("error", out var error) && error.ValueKind == JsonValueKind.Object)
                {
                    if (error.TryGetProperty("code", out var value) && value.ValueKind == JsonValueKind.String)
                        code = SafeCode(value.GetString()) ?? code;
                    if (error.TryGetProperty("innerError", out var inner) && inner.ValueKind == JsonValueKind.Object &&
                        inner.TryGetProperty("request-id", out var id) && id.ValueKind == JsonValueKind.String)
                        requestId ??= SafeId(id.GetString());
                }
            }
        }
        catch (JsonException) { /* Non-JSON errors still retain HTTP status and operation. */ }
        catch (IOException) { /* The error response itself may be truncated. */ }
        catch (HttpRequestException) { /* Preserve the received status and cooldown when its error body cannot be read. */ }
        return new(response.StatusCode, operation, code, requestId, NetworkRequestContext.For(response.RequestMessage), notBefore);
    }

    private static string? SafeCode(string? value) => value is { Length: > 0 and <= 80 } &&
        value.All(c => char.IsAsciiLetterOrDigit(c) || c is '_' or '-' or '.') ? value : null;
    private static string? SafeId(string? value) => Guid.TryParse(value, out var id) ? id.ToString() : null;
    internal static string OperationFor(HttpRequestMessage? request)
    {
        if (request is not null && request.Options.TryGetValue(NetworkRequestContext.OperationKey, out var operation)) return operation;
        var path = request?.RequestUri?.AbsolutePath ?? "";
        if (path.EndsWith("/createUploadSession", StringComparison.Ordinal)) return "Create upload session";
        if (path.EndsWith("/children", StringComparison.Ordinal))
            return request!.Method == HttpMethod.Post ? "Create cloud folder" : "List cloud folders";
        if (path.EndsWith("/copy", StringComparison.Ordinal)) return "Copy cloud file";
        if (path.EndsWith("/delta", StringComparison.Ordinal)) return "Read cloud changes";
        if (request?.Method == HttpMethod.Patch) return "Publish uploaded file";
        if (request?.Method == HttpMethod.Put) return "Upload file content";
        if (path.EndsWith("/content", StringComparison.Ordinal)) return "Download file content";
        if (request?.RequestUri?.Host != "graph.microsoft.com") return "Check cloud transfer";
        return "Read cloud item";
    }
}
