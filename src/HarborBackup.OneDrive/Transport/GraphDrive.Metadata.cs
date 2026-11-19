using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;

namespace HarborBackup.OneDrive.Transport;

public sealed partial class GraphDrive
{
    public async Task<IReadOnlyDictionary<string, byte[]>> ReadMetadataAsync(IReadOnlyList<RemoteItem> items, CancellationToken token)
    {
        if (items.Count is < 1 or > 20 || items.Any(i => i.Folder || i.Size < 0 || i.Size > 32 * 1024 * 1024 || string.IsNullOrWhiteSpace(i.ETag) || !i.Name.EndsWith(".json", StringComparison.Ordinal)) ||
            items.Sum(i => i.Size) > 64 * 1024 * 1024 || items.Select(i => i.Id).Distinct().Count() != items.Count)
            throw new ArgumentException("Invalid bounded metadata batch.");
        static void Verify(RemoteItem expected, RemoteItem actual)
        {
            if (actual.Id != expected.Id || actual.Folder || actual.ETag != expected.ETag || actual.Size != expected.Size)
                throw new IOException("Cloud metadata changed during batch observation.");
        }
        var before = await ReadVersionsAsync(items, token);
        for (var i = 0; i < items.Count; i++) Verify(items[i], before[i]);
        var bytes = new byte[items.Count][];
        await Parallel.ForEachAsync(Enumerable.Range(0, items.Count), new ParallelOptions { MaxDegreeOfParallelism = 4, CancellationToken = token }, async (index, ct) =>
        {
            using var response = await DownloadResponseAsync(items[index].Id, 0, ct);
            if (!response.IsSuccessStatusCode) throw await CloudRequestException.ReadAsync(response, ct);
            using var memory = new MemoryStream();
            await CopyDownloadAsync(response, memory, 0, items[index].Size, ct);
            if (memory.Length != items[index].Size) throw new IOException("Cloud metadata length mismatch.");
            bytes[index] = memory.ToArray();
        });
        var after = await ReadVersionsAsync(items, token);
        for (var i = 0; i < items.Count; i++) Verify(items[i], after[i]);
        return Enumerable.Range(0, items.Count).ToDictionary(i => items[i].Id, i => bytes[i]);
    }
    private async Task<RemoteItem[]> ReadVersionsAsync(IReadOnlyList<RemoteItem> items, CancellationToken token)
    {
        using var response = await SendAsync(HttpMethod.Post, "/$batch", new { requests = items.Select((item, index) =>
            new { id = index.ToString(System.Globalization.CultureInfo.InvariantCulture), method = "GET", url = Item(item.Id) }).ToArray() },
            true, token, operation: "Read metadata versions", readOnlyBatch: true);
        var json = await JsonAsync(response, token);
        if (!json.TryGetProperty("responses", out var replies) || replies.ValueKind != JsonValueKind.Array || replies.GetArrayLength() != items.Count)
            throw new IOException("Incomplete cloud metadata batch.");
        var result = new RemoteItem[items.Count]; var seen = new HashSet<int>();
        foreach (var reply in replies.EnumerateArray())
        {
            if (!int.TryParse(reply.GetProperty("id").GetString(), out var index) || index < 0 || index >= items.Count || !seen.Add(index))
                throw new IOException("Unexpected cloud metadata batch identity.");
            var status = (HttpStatusCode)reply.GetProperty("status").GetInt32();
            if (status is HttpStatusCode.TooManyRequests or HttpStatusCode.ServiceUnavailable or HttpStatusCode.GatewayTimeout)
            {
                if (status == HttpStatusCode.TooManyRequests) Interlocked.Increment(ref throttledCount);
                RetryConditionHeaderValue? retryAfter = null;
                if (reply.TryGetProperty("headers", out var headers))
                    foreach (var header in headers.EnumerateObject())
                        if (header.Name.Equals("Retry-After", StringComparison.OrdinalIgnoreCase)) RetryConditionHeaderValue.TryParse(header.Value.GetString(), out retryAfter);
                retryGate.Defer("graph.microsoft.com", retryAfter, 0);
                Interlocked.Increment(ref retryCount);
                result[index] = await GetAsync(items[index].Id, token);
            }
            else if ((int)status is >= 200 and <= 299) result[index] = Parse(reply.GetProperty("body"));
            else
            {
                using var failed = new HttpResponseMessage(status) { RequestMessage = new HttpRequestMessage(HttpMethod.Get, "https://graph.microsoft.com/v1.0" + Item(items[index].Id)),
                    Content = new StringContent(reply.TryGetProperty("body", out var body) ? body.GetRawText() : "{}") };
                throw await CloudRequestException.ReadAsync(failed, token);
            }
        }
        return result;
    }
}
