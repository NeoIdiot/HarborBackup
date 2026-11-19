using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using HarborBackup.OneDrive.Authentication;

namespace HarborBackup.OneDrive.Transport;

public sealed partial class GraphDrive : ICloudDrive, ICloudChanges, ICloudDiagnostics, ICloudImmutableWrites, ICloudMetadataReads
{
    private readonly HttpClient http;
    private readonly IAccessTokens tokens;
    private readonly string drive;
    private readonly CloudRetryGate retryGate;
    private readonly Func<bool>? networkAvailable;
    private long requestCount, retryCount, retryWait, throttledCount;
    private readonly int uploadChunkBytes;
    private readonly TransferRateLimiter uploadRate, downloadRate;
    public CloudNetworkMetrics NetworkMetrics => new(Interlocked.Read(ref requestCount), Interlocked.Read(ref retryCount), Interlocked.Read(ref retryWait), Interlocked.Read(ref throttledCount));
    public GraphDrive(IAccessTokens tokens, string drive, HttpMessageHandler? handler = null, GraphTransferOptions? options = null)
        : this(tokens, drive, handler, new CloudRetryGate(), options: options) { }
    internal GraphDrive(IAccessTokens tokens, string drive, HttpMessageHandler? handler, CloudRetryGate gate, Func<bool>? networkAvailable = null, GraphTransferOptions? options = null,
        TimeProvider? rateTimeProvider = null, Func<TimeSpan, CancellationToken, Task>? rateWait = null)
    {
        options ??= new();
        uploadChunkBytes = options.Validate();
        uploadRate = new(options.UploadBytesPerSecond, rateTimeProvider, rateWait);
        downloadRate = new(options.DownloadBytesPerSecond, rateTimeProvider, rateWait);
        retryGate = gate;
        this.networkAvailable = networkAvailable ?? (handler is null ? new Func<bool>(System.Net.NetworkInformation.NetworkInterface.GetIsNetworkAvailable) : null);
        this.tokens = tokens; this.drive = drive;
        http = new(new NetworkDiagnosticHandler(handler ?? new HttpClientHandler { AllowAutoRedirect = false })) { Timeout = Timeout.InfiniteTimeSpan };
    }
    public void Dispose() => http.Dispose();
    public async Task<RemoteItem> UploadImmutableAsync(string parent, string name, Stream source, CancellationToken token)
    {
        try { return await UploadAsync(parent, name, source, null, _ => { }, token, reconcileExisting: false); }
        catch (CloudRequestException ex) when (ex.StatusCode == HttpStatusCode.Conflict)
        { return await FindAsync(parent, name, token) ?? throw new IOException("Conflicting immutable metadata disappeared."); }
    }
    private static string E(string value) => Uri.EscapeDataString(value);
    private string Base => string.IsNullOrEmpty(drive) ? "/me/drive" : "/drives/" + E(drive);
    private string Item(string id) => Base + "/items/" + E(id);
    public static void ValidateName(string name)
    {
        if (string.IsNullOrWhiteSpace(name) || name is "." or ".." || name.Length > 255 ||
            name.IndexOfAny(['/', '\\', ':', '*', '?', '"', '<', '>', '|']) >= 0 ||
            name.Any(char.IsControl) || name.EndsWith('.') || name.EndsWith(' '))
            throw new ArgumentException("Invalid OneDrive name.");
    }
    private static void ValidateUrl(Uri uri, bool graph)
    {
        if (uri.Scheme != "https" || !string.IsNullOrEmpty(uri.UserInfo) || !uri.IsDefaultPort ||
            (graph ? uri.Host != "graph.microsoft.com" :
            !(uri.Host == "graph.microsoft.com" || uri.Host == "my.microsoftpersonalcontent.com" || uri.Host.EndsWith(".sharepoint.com", StringComparison.OrdinalIgnoreCase) ||
              uri.Host.EndsWith(".1drv.com", StringComparison.OrdinalIgnoreCase) ||
              uri.Host.EndsWith(".onedrive.com", StringComparison.OrdinalIgnoreCase) ||
              uri.Host.EndsWith(".storage.live.com", StringComparison.OrdinalIgnoreCase))))
            throw new IOException("Unexpected cloud service endpoint.");
    }
    private async Task<HttpResponseMessage> SendAsync(HttpMethod method, string path, object? body, bool graph,
        CancellationToken token, byte[]? bytes = null, long? offset = null, long? total = null, long? rangeStart = null, string? ifMatch = null, string? operation = null, bool readOnlyBatch = false)
    {
        var uri = new Uri(path.StartsWith('/') ? "https://graph.microsoft.com/v1.0" + path : path);
        ValidateUrl(uri, graph);
        for (var attempt = 0; ; attempt++)
        {
            if (networkAvailable is not null) CloudNetworkAvailability.EnsureAvailable(token, networkAvailable);
            Interlocked.Add(ref retryWait, await retryGate.WaitAsync(uri.Host, token));
            using var request = new HttpRequestMessage(method, uri);
            using var requestDeadline = CancellationTokenSource.CreateLinkedTokenSource(token);
            request.Options.Set(NetworkRequestContext.AttemptKey, attempt + 1);
            if (operation is not null) request.Options.Set(NetworkRequestContext.OperationKey, operation);
            if (graph) request.Headers.Authorization = new("Bearer", await tokens.GetAsync(token));
            if (ifMatch is not null) request.Headers.TryAddWithoutValidation("If-Match", ifMatch);
            if (rangeStart is > 0) request.Headers.Range = new RangeHeaderValue(rangeStart, null);
            if (body is not null) request.Content = JsonContent.Create(body);
            if (bytes is not null)
            {
                request.Content = uploadRate.Enabled ? new RateLimitedByteContent(bytes, uploadRate, requestDeadline) : new ByteArrayContent(bytes);
                request.Content.Headers.ContentType = new("application/octet-stream");
                if (offset is not null) request.Content.Headers.ContentRange = new(offset.Value, offset.Value + bytes.Length - 1, total!.Value);
            }
            HttpResponseMessage result;
            try
            {
                Interlocked.Increment(ref requestCount);
                requestDeadline.CancelAfter(RateLimitedByteContent.NetworkTimeout);
                result = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, requestDeadline.Token);
            }
            catch (Exception ex) when ((method == HttpMethod.Get || readOnlyBatch) && attempt < 4 && !token.IsCancellationRequested &&
                ex is HttpRequestException or OperationCanceledException)
            {
                retryGate.Defer(uri.Host, null, attempt);
                Interlocked.Increment(ref retryCount);
                continue;
            }
            catch (OperationCanceledException ex) when (!token.IsCancellationRequested)
            { throw NetworkRequestContext.For(request).Failure(ex, "Timeout"); }
            result.RequestMessage ??= request;
            if ((int)result.StatusCode == 429) Interlocked.Increment(ref throttledCount);
            // 429 explicitly rejects a request. Ambiguous failed writes are reconciled by journals, not replayed.
            var transient = (int)result.StatusCode == 429 || (method == HttpMethod.Get || readOnlyBatch) && (int)result.StatusCode is 503 or 504;
            if (transient)
            {
                retryGate.Defer(uri.Host, result.Headers.RetryAfter, attempt);
                if (attempt < 4)
                {
                    result.Dispose();
                    Interlocked.Increment(ref retryCount);
                    continue;
                }
            }
            return result;
        }
    }
    private static async Task<JsonElement> JsonAsync(HttpResponseMessage response, CancellationToken token)
    {
        if (!response.IsSuccessStatusCode) throw await CloudRequestException.ReadAsync(response, token);
        await using var stream = await response.Content.ReadAsStreamAsync(token);
        using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: token);
        return doc.RootElement.Clone();
    }
    private static RemoteItem Parse(JsonElement json) => new(json.GetProperty("id").GetString()!,
        json.GetProperty("name").GetString()!, json.TryGetProperty("eTag", out var etag) ? etag.GetString()! : "",
        json.TryGetProperty("size", out var size) ? size.GetInt64() : 0, json.TryGetProperty("folder", out _),
        json.TryGetProperty("webUrl", out var url) ? url.GetString() : null, json.TryGetProperty("file", out var file) && file.TryGetProperty("hashes", out var hashes) && hashes.TryGetProperty("quickXorHash", out var q) ? q.GetString() : null, json.TryGetProperty("parentReference", out var parent) && parent.TryGetProperty("path", out var path) ? Uri.UnescapeDataString(path.GetString()!.Split(':' ).Last()).Trim('/') + "/" + json.GetProperty("name").GetString() : null,
        json.TryGetProperty("parentReference", out var reference) && reference.TryGetProperty("id", out var parentId) ? parentId.GetString() : null,
        json.TryGetProperty("fileSystemInfo", out var info) && info.TryGetProperty("lastModifiedDateTime", out var modified) ? modified.GetDateTimeOffset() : null,
        json.TryGetProperty("cTag", out var contentTag) ? contentTag.GetString() : null);
    public async Task<CloudChanges> ChangesAsync(string? cursor, CancellationToken token)
    {
        var next = cursor ?? Base + "/root/delta?token=latest&$select=id,name,parentReference,deleted,folder,eTag,size";
        var changed = cursor is null;
        var changes = new Dictionary<string, CloudChange>();
        var visited = new HashSet<string>();
        while (true)
        {
            if (!visited.Add(next) || visited.Count > 10000) throw new IOException("Cloud delta pagination exceeded its safety limit.");
            using var response = await SendAsync(HttpMethod.Get, next, null, true, token);
            if (response.StatusCode == HttpStatusCode.Gone && cursor is not null)
                return await ChangesAsync(null, token);
            var json = await JsonAsync(response, token);
            if (json.GetProperty("value").GetArrayLength() != 0) changed = true;
            foreach (var item in json.GetProperty("value").EnumerateArray())
            {
                var id = item.GetProperty("id").GetString() ?? throw new IOException("Missing delta item identity.");
                changes[id] = new(id, item.TryGetProperty("name", out var name) ? name.GetString() : null,
                    item.TryGetProperty("parentReference", out var parent) && parent.TryGetProperty("id", out var parentId) ? parentId.GetString() : null,
                    item.TryGetProperty("folder", out _), item.TryGetProperty("deleted", out _),
                    item.TryGetProperty("eTag", out var etag) ? etag.GetString() : null,
                    item.TryGetProperty("size", out var size) ? size.GetInt64() : null);
                if (changes.Count > 200000) throw new IOException("Cloud change detail limit exceeded; a full project observation is required.");
            }
            if (json.TryGetProperty("@odata.nextLink", out var page)) { next = page.GetString()!; continue; }
            var result = json.GetProperty("@odata.deltaLink").GetString()!;
            ValidateUrl(new Uri(result), true);
            return new(result, changed, changes.Values.ToArray(), cursor is null);
        }
    }
    public async Task<string> DriveIdAsync(CancellationToken token)
    {
        using var response = await SendAsync(HttpMethod.Get, Base, null, true, token);
        return (await JsonAsync(response, token)).GetProperty("id").GetString()!;
    }
    public async Task<RemoteItem> RootAsync(CancellationToken token)
    {
        using var response = await SendAsync(HttpMethod.Get, Base + "/root", null, true, token);
        return Parse(await JsonAsync(response, token));
    }
    public async Task<RemoteItem> GetAsync(string id, CancellationToken token)
    {
        using var response = await SendAsync(HttpMethod.Get, Item(id), null, true, token);
        return Parse(await JsonAsync(response, token));
    }
    public async Task<RemoteItem?> FindAsync(string parent, string name, CancellationToken token)
    {
        ValidateName(name);
        using var response = await SendAsync(HttpMethod.Get, Item(parent) + ":/" + E(name), null, true, token);
        if (response.StatusCode == HttpStatusCode.NotFound) return null;
        return Parse(await JsonAsync(response, token));
    }
    public async Task<IReadOnlyList<RemoteItem>> ChildrenAsync(string parent, CancellationToken token)
    {
        var all = new List<RemoteItem>(); string? next = Item(parent) + "/children?$top=200";
        var visited = new HashSet<string>();
        while (next is not null)
        {
            if (!visited.Add(next) || all.Count > 200000) throw new IOException("Cloud enumeration exceeded its safety limit.");
            using var response = await SendAsync(HttpMethod.Get, next, null, true, token);
            var json = await JsonAsync(response, token);
            all.AddRange(json.GetProperty("value").EnumerateArray().Select(Parse));
            next = json.TryGetProperty("@odata.nextLink", out var link) ? link.GetString() : null;
        }
        if (all.Select(i => i.Name).Distinct(StringComparer.OrdinalIgnoreCase).Count() != all.Count)
            throw new IOException("Cloud directory contains case/name collisions.");
        return all;
    }
    public async Task<RemoteItem> FolderAsync(string parent, string name, CancellationToken token)
    {
        ValidateName(name);
        var existing = await FindAsync(parent, name, token);
        if (existing is not null) return existing.Folder ? existing : throw new IOException("File occupies a cloud folder.");
        using var response = await SendAsync(HttpMethod.Post, Item(parent) + "/children",
            new Dictionary<string, object> { ["name"] = name, ["folder"] = new { }, ["@microsoft.graph.conflictBehavior"] = "fail" }, true, token);
        if (response.StatusCode == HttpStatusCode.Conflict)
            return await FindAsync(parent, name, token) is { Folder: true } raced ? raced : throw new IOException("Concurrent folder creation failed.");
        return Parse(await JsonAsync(response, token));
    }
    public async Task<RemoteItem> UploadAsync(string parent, string name, Stream source, TransferSession? session,
        Action<TransferSession?> checkpoint, CancellationToken token, bool reconcileExisting = true)
    {
        ValidateName(name);
        if (!source.CanSeek) throw new ArgumentException("Uploads require a stable seekable source.");
        if (reconcileExisting)
        {
            var existing = await FindAsync(parent, name, token);
            if (existing is not null) return existing; // Retry/recovery only. Normal writes use create-only collision handling.
        }

        // Small metadata/content uses the atomic create-only endpoint. In particular, a failed
        // repository initialization cannot leave an upload-session placeholder at repository.json.
        const int directLimit = 4 * 1024 * 1024;
        if (source.Length <= directLimit && session is null)
        {
            source.Position = 0;
            var bytes = new byte[(int)source.Length];
            await source.ReadExactlyAsync(bytes, token);
            using var response = await SendAsync(HttpMethod.Put, Item(parent) + ":/" + E(name) +
                ":/content?@microsoft.graph.conflictBehavior=fail", null, true, token, bytes);
            return VerifyPublished(Parse(await JsonAsync(response, token)), name, source.Length);
        }

        // Some personal OneDrive endpoints reject conflictBehavior inside createUploadSession,
        // and omitting it can REPLACE an existing item, even with If-None-Match. Therefore sessions
        // only write our random staging name; publish uses a separate, create-only rename.
        // Legacy sessions addressed the final name and must never be resumed by this implementation.
        if (session?.StagingName is null) session = null;
        if (session is null)
        {
            session = new("", DateTimeOffset.MinValue, 0, ".harbor-upload-" + Guid.NewGuid().ToString("N") + ".tmp");
            checkpoint(session); // Reserve the name in the journal before requesting a cloud session.
        }
        ValidateStagingName(session.StagingName!);
        if (session.CompletedItemId is not null)
        {
            var completed = await GetAsync(session.CompletedItemId, token);
            if (completed.Name != session.StagingName || completed.ETag != session.CompletedETag ||
                completed.Folder || completed.Size != source.Length)
                throw new IOException("Staged cloud upload changed; recovery stopped.");
            return await PublishUploadAsync(completed, name, source.Length, checkpoint, token);
        }

        long offset = 0;
        if (!string.IsNullOrEmpty(session.Url) && session.ExpiresUtc > DateTimeOffset.UtcNow)
        {
            using var status = await SendAsync(HttpMethod.Get, session.Url, null, false, token);
            if (status.StatusCode is HttpStatusCode.NotFound or HttpStatusCode.Gone)
                session = session with { Url = "", ExpiresUtc = DateTimeOffset.MinValue, Offset = 0 };
            else offset = Offset(await JsonAsync(status, token));
        }
        else session = session with { Url = "", Offset = 0 };

        if (string.IsNullOrEmpty(session.Url))
        {
            using var response = await SendAsync(HttpMethod.Post, Item(parent) + ":/" + E(session.StagingName!) + ":/createUploadSession",
                new { item = new { name = session.StagingName } }, true, token);
            var json = await JsonAsync(response, token);
            session = session with { Url = json.GetProperty("uploadUrl").GetString()!,
                ExpiresUtc = json.GetProperty("expirationDateTime").GetDateTimeOffset(), Offset = 0 };
            ValidateUrl(new Uri(session.Url), false);
            checkpoint(session);
        }
        var chunk = uploadChunkBytes;
        while (offset < source.Length)
        {
            token.ThrowIfCancellationRequested();
            if (offset < 0 || offset % (320 * 1024) != 0) throw new IOException("Invalid upload resume offset.");
            source.Position = offset;
            var bytes = new byte[(int)Math.Min(chunk, source.Length - offset)];
            await source.ReadExactlyAsync(bytes, token);
            using var response = await SendAsync(HttpMethod.Put, session.Url, null, false, token, bytes, offset, source.Length);
            var json = await JsonAsync(response, token);
            if (response.StatusCode is HttpStatusCode.OK or HttpStatusCode.Created)
            {
                var completed = VerifyPublished(Parse(json), session.StagingName!, source.Length);
                session = session with { CompletedItemId = completed.Id, CompletedETag = completed.ETag };
                checkpoint(session); // If publish fails, recovery retains the complete uploaded content.
                return await PublishUploadAsync(completed, name, source.Length, checkpoint, token);
            }
            if (response.StatusCode != HttpStatusCode.Accepted) throw new IOException("Unexpected upload response.");
            offset = Offset(json);
            session = session with { Offset = offset, ExpiresUtc = json.GetProperty("expirationDateTime").GetDateTimeOffset() };
            checkpoint(session);
        }
        throw new IOException("Upload completion is not confirmed; recover before retrying.");
    }

    private static void ValidateStagingName(string name)
    {
        if (!TransferSession.IsTemporaryName(name))
            throw new IOException("Invalid staged upload identity.");
    }

    private static RemoteItem VerifyPublished(RemoteItem item, string name, long length)
    {
        if (item.Folder || !string.Equals(item.Name, name, StringComparison.OrdinalIgnoreCase) ||
            item.Size != length || string.IsNullOrWhiteSpace(item.ETag))
            throw new IOException("Uploaded cloud item does not match the expected name, size or version.");
        return item;
    }

    private async Task<RemoteItem> PublishUploadAsync(RemoteItem staged, string name, long length,
        Action<TransferSession?> checkpoint, CancellationToken token)
    {
        using var response = await SendAsync(HttpMethod.Patch, Item(staged.Id) + "?@microsoft.graph.conflictBehavior=fail",
            new { name }, true, token, ifMatch: staged.ETag);
        var result = VerifyPublished(Parse(await JsonAsync(response, token)), name, length);
        checkpoint(null);
        return result;
    }

    private static long Offset(JsonElement json) => long.Parse(json.GetProperty("nextExpectedRanges")[0].GetString()!.Split('-')[0],
        System.Globalization.CultureInfo.InvariantCulture);
    public async Task DownloadAsync(RemoteItem item, Stream destination, CancellationToken token)
    {
        var current = await GetAsync(item.Id, token);
        if (current.ETag != item.ETag || current.Size != item.Size) throw new IOException("Cloud file changed before download.");
        var start = destination.CanSeek ? destination.Position : 0;
        using var response = await DownloadResponseAsync(item.Id, start, token);
        if (!response.IsSuccessStatusCode) throw await CloudRequestException.ReadAsync(response, token);
        await CopyDownloadAsync(response, destination, start, item.Size, token);
        if ((await GetAsync(item.Id, token)).ETag != item.ETag) throw new IOException("Cloud file changed during download.");
    }
    private async Task<HttpResponseMessage> DownloadResponseAsync(string id, long start, CancellationToken token)
    {
        var uri = new Uri("https://graph.microsoft.com/v1.0" + Item(id) + "/content");
        var visited = new HashSet<Uri>();
        for (var hop = 0; ; hop++)
        {
            token.ThrowIfCancellationRequested();
            ValidateUrl(uri, hop == 0);
            if (hop > 10 || !visited.Add(uri)) throw new IOException("Cloud download redirect limit exceeded.");
            // Only the original Graph request receives a bearer token. Validate every subsequent
            // endpoint, including relative redirects, and preserve Range across the entire chain.
            var response = await SendAsync(HttpMethod.Get, uri.AbsoluteUri, null, hop == 0, token,
                rangeStart: hop == 0 ? null : start, operation: "Download file content");
            if (response.StatusCode is not (HttpStatusCode.MovedPermanently or HttpStatusCode.Redirect or
                HttpStatusCode.SeeOther or HttpStatusCode.TemporaryRedirect or HttpStatusCode.PermanentRedirect)) return response;
            using (response)
            {
                var location = response.Headers.Location ?? throw new IOException("Missing download location.");
                uri = new Uri(uri, location);
            }
        }
    }
    private async Task CopyDownloadAsync(HttpResponseMessage response, Stream destination, long start, long expected, CancellationToken token)
    {
        if (response.StatusCode == HttpStatusCode.PartialContent)
        {
            var range = response.Content.Headers.ContentRange;
            if (range?.From != start || range.Length != expected) throw new IOException("Invalid cloud download range.");
        }
        else if (start != 0)
        {
            destination.SetLength(0); destination.Position = 0; // Server ignored Range; safely restart this staged file.
        }
        await using var source = await response.Content.ReadAsStreamAsync(token);
        if (!downloadRate.Enabled) { await source.CopyToAsync(destination, token); return; }
        var buffer = new byte[downloadRate.ChunkBytes];
        int read;
        while ((read = await source.ReadAsync(buffer, token)) != 0)
        {
            await downloadRate.WaitAsync(read, token);
            await destination.WriteAsync(buffer.AsMemory(0, read), token);
        }
    }
    public async Task DeleteAsync(RemoteItem item, CancellationToken token)
    {
        using var response = await SendAsync(HttpMethod.Delete, Item(item.Id), null, true, token, ifMatch: item.ETag);
        if (!response.IsSuccessStatusCode) throw await CloudRequestException.ReadAsync(response, token);
    }
    // Conditional relocation preserves old bytes instead of an unconditional content replacement.
    public async Task<RemoteItem> MoveAsync(RemoteItem source, string parent, string name, DateTimeOffset? modifiedUtc, CancellationToken token)
    {
        ValidateName(name);
        var body = new Dictionary<string, object> { ["parentReference"] = new { id = parent }, ["name"] = name };
        if (modifiedUtc is { } modified) body["fileSystemInfo"] = new { lastModifiedDateTime = modified.UtcDateTime };
        using var response = await SendAsync(HttpMethod.Patch, Item(source.Id) + "?@microsoft.graph.conflictBehavior=fail",
            body, true, token, ifMatch: source.ETag);
        var result = Parse(await JsonAsync(response, token));
        if (result.Id != source.Id || result.ParentId != parent || result.Name != name || result.Folder != source.Folder || result.Size != source.Size)
            throw new IOException("Cloud relocation returned an unexpected item. Recovery evidence retained.");
        return result;
    }
    public async Task<RemoteItem> SetModifiedAsync(RemoteItem item, DateTimeOffset modified, CancellationToken token)
    {
        using var response = await SendAsync(HttpMethod.Patch, Item(item.Id),
            new { fileSystemInfo = new { lastModifiedDateTime = modified.UtcDateTime } }, true, token, ifMatch: item.ETag);
        var result = Parse(await JsonAsync(response, token));
        if (result.Id != item.Id || result.Name != item.Name || result.Size != item.Size || result.ParentId != item.ParentId)
            throw new IOException("Modified-time update returned an unexpected item.");
        return result;
    }
    public async Task<RemoteItem> CopyAsync(RemoteItem source, string parent, string name, CancellationToken token)
    {
        ValidateName(name);
        var existing = await FindAsync(parent, name, token);
        if (existing is not null) return existing; // Verified by the caller before use.
        if ((await GetAsync(source.Id, token)).ETag != source.ETag) throw new IOException("Cloud archive source changed.");
        using var response = await SendAsync(HttpMethod.Post, Item(source.Id) + "/copy",
            new { parentReference = new { driveId = drive, id = parent }, name }, true, token);
        if (response.StatusCode != HttpStatusCode.Accepted) { await JsonAsync(response, token); throw new IOException("Cloud copy was not queued."); }
        var monitor = response.Headers.Location ?? throw new IOException("Missing cloud copy monitor.");
        // The monitor URL is preauthenticated; never forward a Graph bearer token.
        var completed = false;
        for (var count = 0; count < 120; count++)
        {
            await Task.Delay(TimeSpan.FromSeconds(2), token);
            using var status = await SendAsync(HttpMethod.Get, monitor.AbsoluteUri, null, false, token);
            if (status.StatusCode == HttpStatusCode.SeeOther) { completed = true; break; }
            var json = await JsonAsync(status, token);
            var state = json.GetProperty("status").GetString();
            if (state == "completed") { completed = true; break; }
            if (state is "failed" or "deleteFailed") throw new IOException("Cloud copy failed; original version retained.");
        }
        if (!completed) throw new IOException("Cloud copy pending; retry recovery.");
        var result = await FindAsync(parent, name, token) ?? throw new IOException("Cloud copy pending; retry recovery.");
        if ((await GetAsync(source.Id, token)).ETag != source.ETag) throw new IOException("Cloud archive source changed during copy.");
        return result;
    }
}
