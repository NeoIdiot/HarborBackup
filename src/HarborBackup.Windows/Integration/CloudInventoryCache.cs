using System.Text.Json;
using HarborBackup.Core.Domain;
using HarborBackup.OneDrive.Transport;
using HarborBackup.Windows.FileSystem;
using HarborBackup.Windows.Persistence;

namespace HarborBackup.Windows.Integration;

internal sealed record CloudInventoryEntry(string Path, RemoteItem Item);
internal sealed record CloudInventorySnapshot(CloudInventoryEntry[] Entries, RemoteItem[] Metadata, long Excluded,
    string? BusinessRootId = null, string? MetadataRootId = null, RemoteItem[]? Guards = null);
internal sealed class CloudInventoryCache(string path, string binding, ICloudDrive drive, TimeProvider? timeProvider = null)
{
    private readonly TimeProvider clock = timeProvider ?? TimeProvider.System;
    private string? invalidation;
    private bool Rebuild(string reason) { invalidation = reason; return false; }
    private sealed record Saved(int SchemaVersion, string Binding, string Cursor, DateTimeOffset CreatedUtc, CloudInventorySnapshot Snapshot);
    private Saved? Load()
    {
        SafePath.Validate(path);
        try
        {
            if (!File.Exists(path) || new FileInfo(path).Length > 32 * 1024 * 1024) return null;
            var value = JsonSerializer.Deserialize<Saved>(AtomicJson.ReadText(path)!, AtomicJson.Options);
            if (value is null || value.SchemaVersion != 2 || value.Binding != binding || string.IsNullOrWhiteSpace(value.Cursor) ||
                value.CreatedUtc > clock.GetUtcNow() || clock.GetUtcNow() - value.CreatedUtc >= TimeSpan.FromHours(6) ||
                value.Snapshot is not { Entries: not null, Metadata: not null } snapshot || snapshot.Excluded < 0 ||
                snapshot.Guards is not null && snapshot.Guards.Any(g => g is null || !g.Folder || string.IsNullOrEmpty(g.Id) || string.IsNullOrEmpty(g.Name)) ||
                snapshot.Entries.Any(e => e is null || e.Item is null || string.IsNullOrEmpty(e.Item.Id) || e.Item.Size < 0 || string.IsNullOrEmpty(e.Item.Name) ||
                    PathRules.Relative(e.Path) != e.Path || Path.GetFileName(e.Path) != e.Item.Name) ||
                snapshot.Entries.Select(e => e.Path).Distinct(StringComparer.OrdinalIgnoreCase).Count() != snapshot.Entries.Length ||
                snapshot.Metadata.Any(i => i is null || i.Folder || string.IsNullOrEmpty(i.Id) || string.IsNullOrEmpty(i.Name) || i.Size < 0 || !i.Name.EndsWith(".json", StringComparison.Ordinal)))
                return null;
            return value;
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException or ArgumentException or FormatException) { return null; }
    }

    public async Task<CloudInventorySnapshot> ReadAsync(bool force, Func<Task<CloudInventorySnapshot>> enumerate,
        ReadMetrics metrics, CancellationToken token)
    {
        if (drive is not ICloudChanges changes) return await enumerate();
        var saved = force ? null : Load();
        var before = await ObserveAsync(changes, saved?.Cursor, token);
        if (saved is not null && await UnchangedAsync(before, saved.Snapshot, token))
        {
            // Advancing an irrelevant delta must not extend the full-observation deadline.
            Save(saved with { Cursor = before!.Cursor });
            metrics.CloudInventoryCacheHits++;
            return saved.Snapshot;
        }
        var snapshot = await enumerate();
        metrics.CloudInventoryInvalidationReason = invalidation;
        if (before is not null)
        {
            // A scan crossing a cloud change is usable only with normal write-time checks.
            // Never bless it as the stable snapshot associated with a newer cursor.
            var after = await ObserveAsync(changes, before.Cursor, token);
            if (await UnchangedAsync(after, snapshot, token))
            {
                Save(new Saved(2, binding, after!.Cursor, clock.GetUtcNow(), snapshot));
            }
        }
        return snapshot;
    }

    private void Save(Saved value)
    {
        SafePath.Validate(path);
        try { AtomicJson.Write(path, value, AtomicJson.Signature(AtomicJson.ReadText(path))); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { /* Rebuildable cache only. */ }
    }
    private async Task<bool> UnchangedAsync(CloudChanges? changes, CloudInventorySnapshot snapshot, CancellationToken token)
    {
        if (changes is null || changes.ResyncRequired) return Rebuild("UnavailableOrResync");
        if (!changes.Changed) return true;
        if (changes.Items is not { Count: > 0 } || snapshot.BusinessRootId is null || snapshot.MetadataRootId is null || snapshot.Guards is null) return Rebuild("MissingScope");
        try
        {
            var guards = snapshot.Guards.Concat(snapshot.Entries.Where(e => e.Item.Folder).Select(e => e.Item)).ToDictionary(i => i.Id);
            var files = snapshot.Entries.Where(e => !e.Item.Folder).Select(e => e.Item).Concat(snapshot.Metadata).ToDictionary(i => i.Id);
            var delta = changes.Items.ToDictionary(i => i.Id);
            // Folder tags may reflect a descendant change; structural changes always invalidate.
            foreach (var item in changes.Items)
            {
                if (files.TryGetValue(item.Id, out var file))
                {
                    if (item.Deleted || item.ETag != file.ETag || item.Size != file.Size || item.ParentId != file.ParentId || item.Name != file.Name)
                        return Rebuild("TrackedFileChanged");
                }
                if (guards.TryGetValue(item.Id, out var guard) &&
                    (item.Deleted || item.Name != guard.Name || item.ParentId != guard.ParentId)) return Rebuild("FolderStructureChanged");
            }
            var fetched = new Dictionary<string, RemoteItem>();
            foreach (var item in changes.Items)
            {
                token.ThrowIfCancellationRequested();
                if (guards.ContainsKey(item.Id) || files.ContainsKey(item.Id)) continue;
                if (item.Deleted || item.ParentId is null) return Rebuild("UnknownDeletionOrParent");
                var parentId = item.ParentId;
                var visited = new HashSet<string>();
                while (true)
                {
                    if (!visited.Add(parentId) || visited.Count > 128) return false;
                    if (parentId == snapshot.BusinessRootId || parentId == snapshot.MetadataRootId) return Rebuild("ChangedInsideScope");
                    if (guards.TryGetValue(parentId, out var guardedParent))
                    {
                        if (guardedParent.ParentId is null) break;
                        parentId = guardedParent.ParentId;
                        continue;
                    }
                    if (delta.TryGetValue(parentId, out var parentChange))
                    {
                        if (parentChange.Deleted || parentChange.ParentId is null) return false;
                        parentId = parentChange.ParentId;
                        continue;
                    }
                    if (!guards.TryGetValue(parentId, out var parent) && !fetched.TryGetValue(parentId, out parent))
                        fetched[parentId] = parent = await drive.GetAsync(parentId, token);
                    if (!parent.Folder) return false;
                    if (parent.ParentId is null)
                    {
                        if (!guards.TryGetValue(parent.Id, out var knownRoot) || knownRoot.ParentId is not null) return false;
                        break;
                    }
                    parentId = parent.ParentId;
                }
            }
            return true;
        }
        catch (Exception ex) when (ex is IOException or HttpRequestException or ArgumentException or KeyNotFoundException) { return false; }
    }

    private static async Task<CloudChanges?> ObserveAsync(ICloudChanges changes, string? cursor, CancellationToken token)
    {
        try { return await changes.ChangesAsync(cursor, token); }
        catch (Exception ex) when (ex is IOException or HttpRequestException or JsonException or FormatException) { return null; }
    }
}
