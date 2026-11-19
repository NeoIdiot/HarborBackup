using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using HarborBackup.Core.Domain;
using HarborBackup.OneDrive.Transport;
using HarborBackup.Windows.FileSystem;
using HarborBackup.Windows.Persistence;
using HarborBackup.Windows.Persistence.Configuration;

namespace HarborBackup.Windows.Integration;

// Rebuildable acceleration only. Original immutable file evidence is never removed.
// A fixed number of catalog digests keeps historical record bodies out of the payload.
internal sealed class CloudMetadataCheckpoint(CloudRepository repository, ICloudDrive drive, DataPaths data,
    string projectId, string? scope = null)
{
    private const int BucketCount = 256;
    private const long MaximumBytes = 32 * 1024 * 1024;
    private static readonly object cacheGate = new();
    internal sealed record Bucket(int Id, int Count, string Digest);
    internal sealed record Head(string SourceId, int Bucket, CloudFileMetadata Metadata);
    internal sealed record Unknown(string Path, string Id, string ETag, long Size);
    internal sealed record Payload(int SchemaVersion, string ProjectId, Bucket[] Buckets, Head[] Heads, Unknown[] Unknown, string? Scope = null);
    internal sealed record Envelope(Payload Payload, string Checksum);
    internal sealed record Hit(Payload Payload, RemoteItem[] Remaining, CloudFileMetadata[] Heads);
    private string LocalPath => Path.Combine(data.Cache, "cloud-metadata-checkpoints", projectId + ".json");
    private string Area => "metadata/projects/" + projectId + "/checkpoints";
    internal static string Scope(CloudAccountSettings account) => DeviceIdentity.Digest(JsonSerializer.Serialize(new
        { account.AccountId, account.DriveId, account.RepositoryId, account.RootId, account.Folders }, AtomicJson.Options));
    private static string Digest(Payload payload) => DeviceIdentity.Digest(JsonSerializer.Serialize(payload, AtomicJson.Options));
    internal static int BucketFor(string id)
    {
        // Only partitioning uses FNV. Each partition's complete contents are hashed with SHA-256.
        uint hash = 2166136261;
        foreach (var c in id) hash = unchecked((hash ^ c) * 16777619);
        return (byte)hash;
    }
    private static void Append(IncrementalHash hash, string value)
    {
        var length = Encoding.UTF8.GetByteCount(value);
        Span<byte> prefix = stackalloc byte[4]; BinaryPrimitives.WriteInt32LittleEndian(prefix, length); hash.AppendData(prefix);
        Span<byte> small = stackalloc byte[1024];
        if (length <= small.Length) { var written = Encoding.UTF8.GetBytes(value, small); hash.AppendData(small[..written]); }
        else hash.AppendData(Encoding.UTF8.GetBytes(value));
    }
    private static Bucket[] Summarize(RemoteItem[] sources)
    {
        var groups = Enumerable.Range(0, BucketCount).Select(_ => new List<RemoteItem>()).ToArray();
        foreach (var item in sources)
        {
            if (item is null || item.Folder || string.IsNullOrWhiteSpace(item.Id) || string.IsNullOrWhiteSpace(item.ETag) ||
                item.Size < 0 || string.IsNullOrEmpty(item.Name) || !item.Name.EndsWith(".json", StringComparison.Ordinal))
                throw new FormatException("Invalid metadata catalog entry.");
            groups[BucketFor(item.Id)].Add(item);
        }
        var result = new Bucket[BucketCount];
        Span<byte> size = stackalloc byte[8];
        for (var bucket = 0; bucket < groups.Length; bucket++)
        {
            var items = groups[bucket]; items.Sort((a, b) => StringComparer.Ordinal.Compare(a.Id, b.Id));
            using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            string? previous = null;
            foreach (var item in items)
            {
                if (previous == item.Id) throw new FormatException("Duplicate metadata catalog identity.");
                previous = item.Id;
                Append(hash, item.Id); Append(hash, item.Name); Append(hash, item.ETag);
                BinaryPrimitives.WriteInt64LittleEndian(size, item.Size); hash.AppendData(size);
            }
            result[bucket] = new(bucket, items.Count, Convert.ToHexStringLower(hash.GetHashAndReset()));
        }
        return result;
    }
    internal static Payload Create(string project, RemoteItem[] sources, IEnumerable<CloudFileMetadata> heads,
        IReadOnlyDictionary<string, RemoteItem> observed, string? scope = null)
    {
        var values = heads.OrderBy(m => m.Revision.Path, StringComparer.OrdinalIgnoreCase).ToArray();
        var names = values.Select(m => m.Revision.Id + ".json").ToHashSet(StringComparer.Ordinal);
        var identities = sources.Where(i => names.Contains(i.Name)).ToDictionary(i => i.Name, StringComparer.Ordinal);
        var paths = values.Select(m => m.Revision.Path).ToHashSet(StringComparer.OrdinalIgnoreCase);
        return new(2, project, Summarize(sources), values.Select(m => identities.TryGetValue(m.Revision.Id + ".json", out var item)
            ? new Head(item.Id, BucketFor(item.Id), m) : throw new FormatException("Checkpoint head has no catalog evidence.")).ToArray(),
            observed.Where(p => !p.Value.Folder && !paths.Contains(p.Key))
                .Select(p => new Unknown(p.Key, p.Value.Id, p.Value.ETag, p.Value.Size)).ToArray(), scope);
    }
    private static bool ValidHead(Head head, string project)
    {
        if (head is null || string.IsNullOrWhiteSpace(head.SourceId) || head.Bucket != BucketFor(head.SourceId)) return false;
        var m = head.Metadata;
        return m is { SchemaVersion: 2, Revision: { SchemaVersion: 1, Version: not null }, Item: not null } &&
            !m.Item.Folder && m.Revision.ContentHashKnown && m.Revision.Version.Length >= 0 &&
            m.Item.Name == Path.GetFileName(m.Revision.Path) && m.Revision.ETag == m.Item.ETag && m.Revision.Project == project &&
            Guid.TryParseExact(m.Revision.Id, "N", out _) && PathRules.Relative(m.Revision.Path) == m.Revision.Path &&
            FileVersion.IsHash(m.Revision.Version.Sha256) && m.Item.Id == m.Revision.ItemId && m.Item.Size == m.Revision.Version.Length;
    }
    internal static bool Covers(Payload value, string project, RemoteItem[] current, out RemoteItem[] remaining, string? scope = null)
        => TryCover(value, project, current, scope, out remaining, out _);
    private static bool TryCover(Payload value, string project, RemoteItem[] current, string? scope,
        out RemoteItem[] remaining, out CloudFileMetadata[] heads)
    {
        remaining = current; heads = [];
        try
        {
            if (value is null || value.SchemaVersion != 2 || value.ProjectId != project || value.Scope != scope ||
                value.Buckets is not { Length: BucketCount } || value.Heads is null || value.Unknown is null ||
                value.Buckets.Any(b => b is null || b.Id is < 0 or >= BucketCount || b.Count < 0 || !FileVersion.IsHash(b.Digest)) ||
                value.Buckets.Select(b => b.Id).Distinct().Count() != BucketCount || value.Heads.Any(h => !ValidHead(h, project)) ||
                value.Heads.Select(h => h.Metadata.Revision.Path).Distinct(StringComparer.OrdinalIgnoreCase).Count() != value.Heads.Length ||
                value.Unknown.Any(u => u is null || PathRules.Relative(u.Path) != u.Path || string.IsNullOrWhiteSpace(u.Id) || string.IsNullOrWhiteSpace(u.ETag) || u.Size < 0))
                return false;
            var saved = value.Buckets.ToDictionary(b => b.Id);
            var same = Summarize(current).Select(b => b == saved[b.Id]).ToArray();
            var needed = value.Heads.Where(h => same[h.Bucket]).Select(h => h.SourceId).ToHashSet(StringComparer.Ordinal);
            var sourceHeads = current.Where(i => needed.Contains(i.Id)).ToDictionary(i => i.Id, StringComparer.Ordinal);
            var usable = value.Heads.Where(h => same[h.Bucket]).ToArray();
            if (usable.Any(h => !sourceHeads.TryGetValue(h.SourceId, out var item) || item.Name != h.Metadata.Revision.Id + ".json")) return false;
            remaining = current.Where(i => !same[BucketFor(i.Id)]).ToArray();
            if (remaining.Length == current.Length) return false;
            heads = usable.Select(h => h.Metadata).ToArray();
            return true;
        }
        catch (Exception ex) when (ex is ArgumentException or FormatException) { return false; }
    }
    private Payload? Unwrap(Envelope? envelope)
    {
        if (envelope?.Payload is not { } payload || envelope.Checksum != Digest(payload)) return null;
        return payload;
    }
    internal async Task<Hit?> ReadAsync(RemoteItem[] items, CancellationToken token)
    {
        if (items.Length < 128) return null;
        try
        {
            Envelope? envelope;
            lock (cacheGate)
            {
                SafePath.Validate(LocalPath, data.Cache);
                envelope = File.Exists(LocalPath) && new FileInfo(LocalPath).Length <= MaximumBytes
                    ? JsonSerializer.Deserialize<Envelope>(AtomicJson.ReadText(LocalPath) ?? "null", AtomicJson.Options) : null;
            }
            var local = Unwrap(envelope);
            if (local is not null && TryCover(local, projectId, items, scope, out var tail, out var heads)) return new(local, tail, heads);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or ArgumentException or FormatException) { }
        try
        {
            var folder = await repository.FolderAsync(Area, false, token);
            if (folder is null) return null;
            var candidates = (await drive.ChildrenAsync(folder.Id, token)).Where(i => !i.Folder && i.Size is >= 0 and <= MaximumBytes && i.Name.EndsWith(".json", StringComparison.Ordinal))
                .OrderByDescending(i => i.Name, StringComparer.Ordinal).Take(3);
            foreach (var item in candidates)
            {
                try
                {
                    var payload = Unwrap(await repository.ReadJsonAsync<Envelope>(item, token));
                    if (payload is not null && TryCover(payload, projectId, items, scope, out var tail, out var heads)) return new(payload, tail, heads);
                }
                catch (Exception ex) when (ex is IOException or HttpRequestException or UnauthorizedAccessException or JsonException or ArgumentException or FormatException) { }
            }
        }
        catch (Exception ex) when (ex is IOException or HttpRequestException or UnauthorizedAccessException or JsonException or ArgumentException or FormatException) { }
        return null;
    }
    internal void Save(Payload payload)
    {
        if (payload.Buckets.Sum(b => b.Count) < 128) return;
        try
        {
            var envelope = new Envelope(payload, Digest(payload));
            if (JsonSerializer.SerializeToUtf8Bytes(envelope, AtomicJson.Options).Length > MaximumBytes) return;
            lock (cacheGate) AtomicJson.Write(LocalPath, envelope, AtomicJson.Signature(AtomicJson.ReadText(LocalPath)));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }
    internal async Task PublishAsync(Payload payload, CancellationToken token)
    {
        var count = payload.Buckets.Sum(b => b.Count);
        if (count < 128) return;
        var envelope = new Envelope(payload, Digest(payload));
        if (JsonSerializer.SerializeToUtf8Bytes(envelope, AtomicJson.Options).Length > MaximumBytes) return;
        var folder = await repository.FolderAsync(Area, true, token);
        var previous = (await drive.ChildrenAsync(folder!.Id, token)).Where(i => !i.Folder)
            .Select(i => long.TryParse(i.Name.Split('-')[0], out var amount) ? amount : 0).DefaultIfEmpty().Max();
        if (previous > 0 && count - previous < 128) return;
        await repository.JsonAsync(folder.Id, count.ToString("D12", System.Globalization.CultureInfo.InvariantCulture) + "-" + envelope.Checksum + ".json", envelope, token);
    }
}
