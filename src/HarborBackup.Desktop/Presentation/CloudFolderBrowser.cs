using HarborBackup.Windows.Integration;
using HarborBackup.Windows.Persistence.Configuration;

namespace HarborBackup.Desktop.Presentation;

/// <summary>Disposable navigation observations, never evidence for a cloud write.</summary>
internal sealed class CloudFolderBrowser(
    Func<CloudAccountSettings> readAccount,
    Func<CloudAccountSettings, string?, CancellationToken, Task<CloudFolderListing>> readListing,
    Func<DateTimeOffset>? now = null)
{
    private const int Capacity = 128;
    private const int PrefetchLimit = 8;
    private const string RootKey = "\0root";
    private static readonly TimeSpan Lifetime = TimeSpan.FromMinutes(5);
    private readonly object gate = new();
    private readonly Dictionary<string, Entry> cache = new(StringComparer.Ordinal);
    private readonly Dictionary<string, Request> pending = new(StringComparer.Ordinal);
    private CloudAccountSettings? account;
    private string? rootId;
    private long generation;
    private long access;

    private sealed class Entry(CloudFolderListing listing, DateTimeOffset fetchedAt, long used)
    {
        internal readonly CloudFolderListing Listing = listing;
        internal readonly DateTimeOffset FetchedAt = fetchedAt;
        internal long Used = used;
    }

    private sealed class Request(CloudAccountSettings account, string? folderId, string key, long generation, bool refresh)
    {
        internal readonly CloudAccountSettings Account = account;
        internal readonly string? FolderId = folderId;
        internal readonly string Key = key;
        internal readonly long Generation = generation;
        internal readonly bool Refresh = refresh;
        internal readonly CancellationTokenSource Cancellation = new();
        internal readonly TaskCompletionSource<CloudFolderListing> Completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal int Waiters;
        internal bool Abandoned;
        internal CancellationToken Token => Cancellation.Token;
        internal void Cancel()
        {
            try { Cancellation.Cancel(); }
            catch (ObjectDisposedException) { /* The read finished before cancellation reached it. */ }
        }
    }

    internal Task<CloudFolderListing> BrowseAsync(string? folderId, CancellationToken token, bool refresh = false) =>
        BrowseAsync(folderId, token, refresh, null);

    private async Task<CloudFolderListing> BrowseAsync(string? folderId, CancellationToken token, bool refresh,
        CloudAccountSettings? expectedAccount)
    {
        token.ThrowIfCancellationRequested();
        Request? request = null;
        Entry? cached = null;
        Request[] obsolete = [];
        var start = false;
        lock (gate)
        {
            var current = readAccount();
            if (expectedAccount is not null && current != expectedAccount)
                throw new IOException("OneDrive settings changed. Reload before saving.");
            if (account != current)
            {
                account = current; generation++; rootId = null; cache.Clear();
                obsolete = pending.Values.ToArray(); pending.Clear();
                foreach (var old in obsolete)
                {
                    old.Abandoned = true;
                    old.Completion.TrySetCanceled();
                }
            }
            var key = folderId ?? rootId ?? RootKey;
            if (!refresh && cache.TryGetValue(key, out var entry) && UtcNow - entry.FetchedAt < Lifetime)
            {
                entry.Used = ++access; cached = entry;
            }
            else
            {
                pending.TryGetValue(key, out request);
                if (refresh && request is { Refresh: false } && !request.Completion.Task.IsCompleted)
                {
                    request.Abandoned = true;
                    request.Completion.TrySetCanceled();
                    obsolete = [.. obsolete, request];
                    pending.Remove(key); request = null;
                }
                if (request is null || request.Completion.Task.IsCompleted)
                {
                    request = new(current, folderId, key, generation, refresh);
                    pending[key] = request; start = true;
                }
                request.Waiters++;
            }
        }
        foreach (var old in obsolete) old.Cancel();
        if (cached is not null) return cached.Listing;
        var active = request!;
        if (start) _ = ReadAsync(active);
        try { return await active.Completion.Task.WaitAsync(token).ConfigureAwait(false); }
        finally
        {
            var cancel = false;
            lock (gate)
            {
                if (--active.Waiters == 0 && !active.Completion.Task.IsCompleted)
                {
                    active.Abandoned = true;
                    if (pending.GetValueOrDefault(active.Key) == active) pending.Remove(active.Key);
                    cancel = true;
                }
            }
            if (cancel) active.Cancel();
        }
    }

    private DateTimeOffset UtcNow => now?.Invoke() ?? DateTimeOffset.UtcNow;

    private async Task ReadAsync(Request request)
    {
        Request[] obsolete = [];
        try
        {
            var listing = await readListing(request.Account, request.FolderId, request.Token).ConfigureAwait(false);
            request.Token.ThrowIfCancellationRequested();
            lock (gate)
            {
                if (request.Abandoned || request.Generation != generation || account != request.Account || readAccount() != request.Account)
                    throw new IOException("OneDrive settings changed. Reload before saving.");
                if (request.Refresh || cache.TryGetValue(listing.Current.Id, out var previous) && !SameStructure(previous.Listing, listing))
                {
                    // An ancestor rename also changes every descendant's displayed path. Only
                    // successful fresh evidence invalidates the other navigation observations.
                    cache.Clear(); generation++;
                    obsolete = pending.Values.Where(other => other != request).ToArray();
                    foreach (var other in obsolete)
                    {
                        other.Abandoned = true;
                        other.Completion.TrySetCanceled();
                        pending.Remove(other.Key);
                    }
                }
                listing = listing with { Folders = listing.Folders.ToArray() };
                cache[listing.Current.Id] = new(listing, UtcNow, ++access);
                if (request.FolderId is null) rootId = listing.Current.Id;
                if (cache.Count > Capacity) cache.Remove(cache.MinBy(p => p.Value.Used).Key);
                request.Completion.TrySetResult(listing);
            }
            foreach (var other in obsolete) other.Cancel();
        }
        catch (Exception ex)
        {
            lock (gate)
            {
                if (request.Abandoned || request.Token.IsCancellationRequested)
                    request.Completion.TrySetCanceled();
                else request.Completion.TrySetException(ex);
            }
        }
        finally
        {
            lock (gate)
                if (pending.GetValueOrDefault(request.Key) == request) pending.Remove(request.Key);
            request.Cancellation.Dispose();
        }
    }

    private static bool SameStructure(CloudFolderListing previous, CloudFolderListing current) =>
        previous.Current.Name == current.Current.Name && previous.Current.DisplayPath == current.Current.DisplayPath &&
        previous.Current.ParentId == current.Current.ParentId && previous.Folders.Select(f => (f.Id, f.Name, f.ParentId))
            .ToHashSet().SetEquals(current.Folders.Select(f => (f.Id, f.Name, f.ParentId)));

    internal async Task PrefetchAsync(CloudFolderListing listing, CancellationToken token, string? preferredFolderId = null)
    {
        token.ThrowIfCancellationRequested();
        try
        {
            CloudAccountSettings expected;
            lock (gate)
            {
                expected = readAccount();
                // A stale dialog must not send its old folder IDs to a newly connected account.
                if (account != expected || !cache.TryGetValue(listing.Current.Id, out var entry) || !ReferenceEquals(entry.Listing, listing)) return;
            }
            var children = listing.Folders.OrderByDescending(f => f.Id == preferredFolderId)
                .DistinctBy(f => f.Id).Take(PrefetchLimit).ToArray();
            await Parallel.ForEachAsync(children, new ParallelOptions { MaxDegreeOfParallelism = 2, CancellationToken = token },
                async (folder, cancellation) =>
                {
                    try { await BrowseAsync(folder.Id, cancellation, false, expected).ConfigureAwait(false); }
                    catch (Exception) { cancellation.ThrowIfCancellationRequested(); }
                }).ConfigureAwait(false);
        }
        catch (Exception) { token.ThrowIfCancellationRequested(); }
    }
}
