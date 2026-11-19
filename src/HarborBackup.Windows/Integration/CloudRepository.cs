using System.Text.Json;
using HarborBackup.Core.Domain;
using HarborBackup.OneDrive.Transport;
using HarborBackup.Windows.FileSystem;
using HarborBackup.Windows.Persistence;
using HarborBackup.Windows.Persistence.Configuration;

namespace HarborBackup.Windows.Integration;

public sealed class CloudRepository(ICloudDrive drive, CloudAccountSettings account, DataPaths data)
{
    public static string ProjectKey(ProjectDefinition project) => project.ProjectId;
    private static readonly object cacheGate = new();
    private static string? ReadCache(string path)
    {
        lock (cacheGate)
        {
            try { return AtomicJson.ReadText(path); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return null; }
        }
    }
    private static void WriteCache(string path, object value)
    {
        lock (cacheGate)
        {
            try { AtomicJson.Write(path, value, AtomicJson.Signature(AtomicJson.ReadText(path))); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { /* Disposable cache must not fail a verified transfer. */ }
        }
    }
    private RemoteItem? managementRoot;
    private readonly SemaphoreSlim folderGate = new(1, 1);
    private readonly Dictionary<string, RemoteItem> folders = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, RemoteItem> observationGuards = new();
    internal void ResetObservation() { managementRoot = null; folders.Clear(); observationGuards.Clear(); }
    internal RemoteItem[] ObservationGuards => observationGuards.Values.Concat(folders.Values).DistinctBy(i => i.Id).ToArray();
    public async Task ValidateTargetAsync(RemoteItem target, CancellationToken token)
    {
        if (!target.Folder) throw new IOException("Cloud target is not a folder.");
        var driveRoot = await drive.RootAsync(token);
        var ancestors = new Dictionary<string, RemoteItem> { [driveRoot.Id] = driveRoot, [target.Id] = target };
        if (target.Id == driveRoot.Id) throw new IOException("Choose a project folder, not the drive root.");
        managementRoot = await drive.GetAsync(account.RootId!, token);
        ancestors[managementRoot.Id] = managementRoot;
        foreach (var start in new[] { target, managementRoot })
        {
            var current = start; var visited = new HashSet<string>();
            while (visited.Add(current.Id))
            {
                if (start.Id == target.Id ? current.Id == account.RootId : current.Id == target.Id)
                    throw new IOException("Backup target overlaps management storage.");
                if (current.ParentId is null) break;
                if (!ancestors.TryGetValue(current.ParentId, out var parent))
                    ancestors[current.ParentId] = parent = await drive.GetAsync(current.ParentId, token);
                current = parent;
            }
        }
        foreach (var pair in ancestors) observationGuards[pair.Key] = pair.Value;
    }
    public async Task<RemoteItem?> DirectFolderAsync(string path, bool create, CancellationToken token)
    {
        var current = await drive.RootAsync(token);
        if (current.Id == account.RootId) throw new IOException("Management folder cannot be the drive root.");
        foreach (var part in PathRules.Relative(path, true).Split('/'))
        {
            var next = create ? await drive.FolderAsync(current.Id, part, token) : await drive.FindAsync(current.Id, part, token);
            if (next is null) return null;
            if (!next.Folder || next.Id == account.RootId) throw new IOException("Backup target overlaps management storage or is not a folder.");
            current = next;
        }
        // Reject an ancestor of the management directory, including aliases and renamed roots.
        var management = await drive.GetAsync(account.RootId!, token);
        var visited = new HashSet<string>();
        while (visited.Add(management.Id))
        {
            if (management.Id == current.Id) throw new IOException("Backup target overlaps management storage.");
            if (management.ParentId is null) break;
            management = await drive.GetAsync(management.ParentId, token);
        }
        return current;
    }
    public async Task<RemoteItem?> FolderAsync(string path, bool create, CancellationToken token)
    {
        await folderGate.WaitAsync(token);
        try { return await FolderCoreAsync(path, create, token); }
        finally { folderGate.Release(); }
    }
    private async Task<RemoteItem?> FolderCoreAsync(string path, bool create, CancellationToken token)
    {
        if (folders.TryGetValue(path, out var known)) return known;
        var current = managementRoot ??= await drive.GetAsync(account.RootId!, token);
        if (!current.Folder) throw new IOException("Cloud repository root is not a folder.");
        var prefix = "";
        foreach (var part in (account.Folders ?? new CloudFolders()).Resolve(path).Split('/'))
        {
            prefix += "/" + part;
            if (folders.TryGetValue("resolved:" + prefix, out var cached)) { current = cached; continue; }
            var next = create ? await drive.FolderAsync(current.Id, part, token) : await drive.FindAsync(current.Id, part, token);
            if (next is null) return null;
            if (!next.Folder) throw new IOException("Cloud repository folder replaced by a file.");
            current = next;
            folders["resolved:" + prefix] = current;
        }
        folders[path] = current;
        return current;
    }
    public async Task ValidateAsync(CancellationToken token)
    {
        var marker = await drive.FindAsync(account.RootId!, "repository.json", token) ?? throw new IOException("Missing cloud repository identity.");
        var document = await ReadIdentityAsync(marker, token);
        if (document.SchemaVersion == 1) throw new IOException("RepositoryUpgradeRequired");
        if (document.SchemaVersion != 2 || document.RepositoryId != account.RepositoryId || (document.Folders ?? new CloudFolders()) != (account.Folders ?? new CloudFolders()))
            throw new IOException("Cloud repository identity mismatch. No write performed.");
    }
    [System.Text.Json.Serialization.JsonUnmappedMemberHandling(System.Text.Json.Serialization.JsonUnmappedMemberHandling.Disallow)]
    public sealed record RepositoryIdentity(int SchemaVersion, string RepositoryId, CloudFolders? Folders = null);
    internal async Task<RepositoryIdentity> ReadIdentityAsync(RemoteItem marker, CancellationToken token)
    {
        var json = await ReadJsonAsync<JsonElement>(marker, token);
        using var validated = JsonInput.Parse(json.GetRawText());
        var document = System.Text.Json.Nodes.JsonNode.Parse(json.GetRawText(), new() { PropertyNameCaseInsensitive = true })!.AsObject();
        var schema = document["schemaVersion"]?.GetValue<int>();
        if (schema is not (1 or 2)) throw new IOException("Unsupported repository identity.");
        if (schema == 2 && (document["folders"] is not System.Text.Json.Nodes.JsonObject layout ||
            new[] { "archives", "conflicts", "metadata" }.Any(name => layout[name] is null)))
            throw new IOException("Incomplete repository layout.");
        if (schema == 1 && document["folders"] is System.Text.Json.Nodes.JsonObject folders) folders.Remove("backups");
        var identity = JsonSerializer.Deserialize<RepositoryIdentity>(document.ToJsonString(), AtomicJson.Options)
            ?? throw new IOException("Invalid repository identity.");
        if (!Guid.TryParseExact(identity.RepositoryId, "N", out _) || schema == 2 && identity.Folders is null)
            throw new IOException("Invalid repository identity.");
        (identity.Folders ?? new CloudFolders()).Validate();
        return identity;
    }
    public async Task<T> ReadJsonAsync<T>(RemoteItem item, CancellationToken token, ReadMetrics? metrics = null)
    {
        if (item.Folder || item.Size > 32 * 1024 * 1024) throw new IOException("Cloud metadata exceeds its limit.");
        var cache = Path.Combine(data.Cache, "cloud", DeviceIdentity.Digest(account.DriveId! + "/" + item.Id + "/" + item.ETag) + ".json");
        SafePath.Validate(cache, data.Root);
        if (ReadCache(cache) is { } cached)
        {
            try
            {
                var cachedValue = JsonSerializer.Deserialize<T>(cached, AtomicJson.Options) ?? throw new FormatException("Empty metadata.");
                if (metrics is not null) lock (metrics) metrics.CloudMetadataCacheHits++;
                return cachedValue;
            }
            catch (Exception ex) when (ex is JsonException or FormatException) { /* Cache is disposable; fetch a verified copy. */ }
        }
        using var memory = new MemoryStream();
        await drive.DownloadAsync(item, memory, token);
        if (memory.Length != item.Size) throw new IOException("Cloud metadata length mismatch.");
        var value = JsonSerializer.Deserialize<T>(memory.ToArray(), AtomicJson.Options) ?? throw new FormatException("Empty cloud metadata.");
        WriteCache(cache, value);
        return value;
    }
    internal async Task<HashSet<string>> WarmMetadataAsync<T>(RemoteItem[] items, CancellationToken token)
    {
        var warmed = new HashSet<string>();
        if (drive is not ICloudMetadataReads reader) return warmed;
        var pending = new List<RemoteItem>();
        string CachePath(RemoteItem item) => Path.Combine(data.Cache, "cloud", DeviceIdentity.Digest(account.DriveId! + "/" + item.Id + "/" + item.ETag) + ".json");
        foreach (var item in items)
        {
            if (item.Folder || item.Size < 0 || item.Size > 32 * 1024 * 1024) throw new IOException("Cloud metadata exceeds its limit.");
            var cache = CachePath(item); SafePath.Validate(cache, data.Root);
            try { if (ReadCache(cache) is { } cached && JsonSerializer.Deserialize<T>(cached, AtomicJson.Options) is not null) continue; }
            catch (Exception ex) when (ex is JsonException or FormatException) { }
            pending.Add(item);
        }
        while (pending.Count > 1)
        {
            long size = 0;
            var group = pending.Take(20).TakeWhile(i => (size += i.Size) <= 64 * 1024 * 1024).ToArray();
            if (group.Length < 2) { pending.RemoveAt(0); continue; }
            var contents = await reader.ReadMetadataAsync(group, token);
            // Parse all returned values before populating any cache entry.
            var values = group.Select(i => JsonSerializer.Deserialize<T>(contents[i.Id], AtomicJson.Options) ?? throw new FormatException("Empty cloud metadata.")).ToArray();
            for (var i = 0; i < group.Length; i++) { WriteCache(CachePath(group[i]), values[i]!); warmed.Add(group[i].Id); }
            pending.RemoveRange(0, group.Length);
        }
        return warmed;
    }
    public async Task<RemoteItem> JsonAsync(string parent, string name, object value, CancellationToken token, bool reconcileExisting = true)
    {
        var bytes = JsonSerializer.SerializeToUtf8Bytes(value, AtomicJson.Options);
        using var memory = new MemoryStream(bytes);
        var result = reconcileExisting && drive is ICloudImmutableWrites immutable
            ? await immutable.UploadImmutableAsync(parent, name, memory, token)
            : await drive.UploadAsync(parent, name, memory, null, _ => { }, token, reconcileExisting);
        memory.Position = 0;
        var expected = await QuickXor.ComputeAsync(memory, token);
        if (!string.IsNullOrEmpty(result.QuickXorHash))
        {
            if (result.Size != bytes.Length || result.QuickXorHash != expected) throw new IOException("Existing metadata differs; refusing replacement.");
        }
        else
        {
            // Metadata only: business file contents are never downloaded by this path.
            using var actual = new MemoryStream();
            await drive.DownloadAsync(result, actual, token);
            if (!actual.ToArray().AsSpan().SequenceEqual(bytes)) throw new IOException("Existing metadata differs; refusing replacement.");
        }
        var cache = Path.Combine(data.Cache, "cloud", DeviceIdentity.Digest(account.DriveId! + "/" + result.Id + "/" + result.ETag) + ".json");
        WriteCache(cache, value);
        return result;
    }
    public async Task VerifyAsync(RemoteItem item, FileVersion expected, string? quickXor, bool full, CancellationToken token)
    {
        if (item.Folder || item.Size != expected.Length || string.IsNullOrWhiteSpace(item.ETag)) throw new IOException("Cloud content length or type mismatch.");
        if (!string.IsNullOrEmpty(quickXor) && !string.IsNullOrEmpty(item.QuickXorHash))
        {
            if (item.QuickXorHash != quickXor) throw new IOException("Cloud transfer checksum mismatch.");
            return;
        }
        throw new IOException("Cloud checksum is not available. Content was retained; retry verification without downloading it.");
    }
    public Task<RemoteItem> ProtectAsync(ProjectDefinition project, CloudRevision revision, ProtectionKind kind, CancellationToken token) =>
        new CloudProtectionStore(this, drive, account, data).CopyAsync(project, revision, kind, token);
    public async Task DownloadVerifiedAsync(RemoteItem item, FileVersion version, string target, CancellationToken token)
    {
        SafePath.Validate(target);
        if (File.Exists(target))
        {
            var observed = await new FileObserver().ObserveAsync(target, null, new(), true, new(), token);
            if (observed.Version?.Sha256 == version.Sha256) return;
            throw new IOException("Existing local protection content differs.");
        }
        Directory.CreateDirectory(Path.GetDirectoryName(target)!);
        var pending = target + "." + version.Sha256 + ".part";
        SafePath.Validate(pending);
        await using (var output = new FileStream(pending, FileMode.OpenOrCreate, FileAccess.Write, FileShare.None, 131072, true))
        { if (output.Length > version.Length) throw new IOException("Invalid partial download length."); output.Position = output.Length; if (output.Length != version.Length || version.Length == 0) await drive.DownloadAsync(item, output, token); output.Flush(true); }
        var actual = await new FileObserver().ObserveAsync(pending, null, new(), true, new(), token);
        if (actual.Version?.Sha256 != version.Sha256 || actual.Version.Length != version.Length)
            throw new IOException("Downloaded content mismatch; partial file retained.");
        File.SetLastWriteTimeUtc(pending, version.LastWriteTimeUtc.UtcDateTime);
        File.Move(pending, target);
    }

}
