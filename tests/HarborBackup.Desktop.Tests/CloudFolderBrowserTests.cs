using System.Collections.Concurrent;
using HarborBackup.Desktop.Presentation;
using HarborBackup.OneDrive.Transport;
using HarborBackup.Windows.Integration;
using HarborBackup.Windows.Persistence.Configuration;

namespace HarborBackup.Desktop.Tests;

[TestClass]
public sealed class CloudFolderBrowserTests
{
    private static readonly CloudAccountSettings Account = new(Enabled: true, AccountId: "account", DriveId: "drive", RootId: "management");
    private static RemoteItem Folder(string id) => new(id, id, "tag", 0, true, DisplayPath: id == "root" ? "" : id);
    private static CloudFolderListing Listing(string id, params string[] children) => new(Folder(id), children.Select(Folder).ToArray());

    [TestMethod]
    public async Task RootAliasAndRepeatedVisitsShareSuccessfulObservationsUntilExpiry()
    {
        var instant = DateTimeOffset.UtcNow;
        var reads = 0;
        var browser = new CloudFolderBrowser(() => Account, (_, id, _) =>
        {
            reads++; return Task.FromResult(Listing(id ?? "root", "child"));
        }, () => instant);
        var root = await browser.BrowseAsync(null, default);
        Assert.AreSame(root, await browser.BrowseAsync("root", default));
        Assert.AreSame(root, await browser.BrowseAsync(null, default));
        await browser.BrowseAsync("child", default);
        Assert.AreSame(root, await browser.BrowseAsync(null, default));
        Assert.AreEqual(2, reads, "Returning to the parent or reopening the picker must reuse the same browser cache.");
        instant += TimeSpan.FromMinutes(5);
        Assert.AreNotSame(root, await browser.BrowseAsync(null, default));
        Assert.AreEqual(3, reads);
    }

    [TestMethod]
    public async Task RefreshReplacesOnlySuccessfulResultsAndFailureCanBeRetried()
    {
        var reads = 0; var fail = false;
        var browser = new CloudFolderBrowser(() => Account, (_, id, _) =>
        {
            reads++;
            if (fail) throw new IOException("Synthetic listing failure");
            return Task.FromResult(Listing(id ?? "root", reads.ToString()));
        });
        var original = await browser.BrowseAsync(null, default);
        fail = true;
        await Assert.ThrowsExactlyAsync<IOException>(() => browser.BrowseAsync(null, default, refresh: true));
        Assert.AreSame(original, await browser.BrowseAsync("root", default));
        await Assert.ThrowsExactlyAsync<IOException>(() => browser.BrowseAsync("uncached", default));
        fail = false;
        var fresh = await browser.BrowseAsync(null, default, refresh: true);
        Assert.AreNotSame(original, fresh);
        Assert.AreSame(fresh, await browser.BrowseAsync(null, default));
        await browser.BrowseAsync("uncached", default);
        Assert.AreEqual(5, reads);
    }

    [TestMethod]
    public async Task RefreshingParentAfterRenameInvalidatesCachedDescendantPaths()
    {
        var name = "Before";
        var reads = new Dictionary<string, int>();
        var browser = new CloudFolderBrowser(() => Account, (_, id, _) =>
        {
            var key = id ?? "root";
            reads[key] = reads.GetValueOrDefault(key) + 1;
            return Task.FromResult(key switch
            {
                "root" => new CloudFolderListing(Folder("root"), [Folder("child") with { Name = name, DisplayPath = name }]),
                "child" => new CloudFolderListing(Folder("child") with { Name = name, DisplayPath = name }, [Folder("leaf")]),
                _ => new CloudFolderListing(Folder("leaf") with { DisplayPath = name + "/Leaf" }, [])
            });
        });
        var root = await browser.BrowseAsync(null, default);
        await browser.PrefetchAsync(root, default);
        var child = await browser.BrowseAsync("child", default);
        await browser.PrefetchAsync(child, default);
        Assert.AreEqual("Before/Leaf", (await browser.BrowseAsync("leaf", default)).Current.DisplayPath);
        name = "After";
        await browser.BrowseAsync(null, default, refresh: true);
        Assert.AreEqual("After", (await browser.BrowseAsync("child", default)).Current.DisplayPath);
        Assert.AreEqual("After/Leaf", (await browser.BrowseAsync("leaf", default)).Current.DisplayPath);
        Assert.IsTrue(reads.Values.All(count => count == 2));
    }

    [TestMethod]
    public async Task ExpiredParentThatDiscoversRenameInvalidatesNewerChildObservation()
    {
        var instant = DateTimeOffset.UtcNow;
        var name = "Before"; var childReads = 0;
        var browser = new CloudFolderBrowser(() => Account, (_, id, _) =>
        {
            if (id is null) return Task.FromResult(new CloudFolderListing(Folder("root"), [Folder("child") with { Name = name }]));
            childReads++;
            return Task.FromResult(new CloudFolderListing(Folder("child") with { DisplayPath = name }, []));
        }, () => instant);
        await browser.BrowseAsync(null, default);
        instant += TimeSpan.FromMinutes(4);
        Assert.AreEqual("Before", (await browser.BrowseAsync("child", default)).Current.DisplayPath);
        instant += TimeSpan.FromMinutes(2); name = "After";
        await browser.BrowseAsync(null, default);
        Assert.AreEqual("After", (await browser.BrowseAsync("child", default)).Current.DisplayPath);
        Assert.AreEqual(2, childReads, "A still-young child cache must be invalidated when the expired parent reveals its rename.");
    }

    [TestMethod]
    public async Task ParentReorderingAndTagChangesDoNotInvalidateUnchangedChildStructure()
    {
        var instant = DateTimeOffset.UtcNow;
        var rootReads = 0; var childReads = 0;
        var browser = new CloudFolderBrowser(() => Account, (_, id, _) =>
        {
            if (id is null)
            {
                rootReads++;
                var children = new[] { Folder("child"), Folder("other") };
                if (rootReads > 1) Array.Reverse(children);
                return Task.FromResult(new CloudFolderListing(Folder("root") with { ETag = rootReads.ToString() }, children));
            }
            childReads++; return Task.FromResult(Listing(id));
        }, () => instant);
        await browser.BrowseAsync(null, default);
        instant += TimeSpan.FromMinutes(4);
        var child = await browser.BrowseAsync("child", default);
        instant += TimeSpan.FromMinutes(2);
        await browser.BrowseAsync(null, default);
        Assert.AreSame(child, await browser.BrowseAsync("child", default));
        Assert.AreEqual(1, childReads);
    }

    [TestMethod]
    public async Task SuccessfulRefreshDiscardsAnOlderDescendantReadThatIgnoresCancellation()
    {
        var oldReply = new TaskCompletionSource<CloudFolderListing>();
        var leafReads = 0;
        var browser = new CloudFolderBrowser(() => Account, (_, id, _) =>
        {
            if (id is null) return Task.FromResult(Listing("root", "leaf"));
            return ++leafReads == 1 ? oldReply.Task : Task.FromResult(Listing("leaf", "current"));
        });
        await browser.BrowseAsync(null, default);
        var obsolete = browser.BrowseAsync("leaf", default);
        await browser.BrowseAsync(null, default, refresh: true);
        await Assert.ThrowsAsync<OperationCanceledException>(() => obsolete);
        oldReply.SetResult(Listing("leaf", "obsolete"));
        Assert.AreEqual("current", (await browser.BrowseAsync("leaf", default)).Folders.Single().Id);
        Assert.AreEqual(2, leafReads);
    }

    [TestMethod]
    public async Task ExplicitRefreshStartsAFreshReadInsteadOfJoiningOlderPrefetch()
    {
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var oldReply = new TaskCompletionSource<CloudFolderListing>();
        var childReads = 0;
        var browser = new CloudFolderBrowser(() => Account, (_, id, _) =>
        {
            if (id is null) return Task.FromResult(Listing("root", "child"));
            if (Interlocked.Increment(ref childReads) == 1) { started.TrySetResult(); return oldReply.Task; }
            return Task.FromResult(Listing("child", "current"));
        });
        var root = await browser.BrowseAsync(null, default);
        var prefetch = browser.PrefetchAsync(root, default);
        await started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var refreshed = await browser.BrowseAsync("child", default, refresh: true);
        await prefetch.WaitAsync(TimeSpan.FromSeconds(5));
        oldReply.SetResult(Listing("child", "obsolete"));
        Assert.AreSame(refreshed, await browser.BrowseAsync("child", default));
        Assert.AreEqual("current", refreshed.Folders.Single().Id);
        Assert.AreEqual(2, childReads);
    }

    [TestMethod]
    public async Task ExpiredObservationDoesNotBecomeFreshWhenAReadFails()
    {
        var instant = DateTimeOffset.UtcNow; var fail = false; var reads = 0;
        var browser = new CloudFolderBrowser(() => Account, (_, id, _) =>
        {
            reads++;
            if (fail) throw new IOException("Synthetic offline");
            return Task.FromResult(Listing(id ?? "root"));
        }, () => instant);
        await browser.BrowseAsync(null, default);
        instant += TimeSpan.FromMinutes(6); fail = true;
        await Assert.ThrowsExactlyAsync<IOException>(() => browser.BrowseAsync(null, default));
        await Assert.ThrowsExactlyAsync<IOException>(() => browser.BrowseAsync("root", default));
        Assert.AreEqual(3, reads);
    }

    [TestMethod]
    public async Task OneCanceledWaiterDoesNotCancelAnotherWaiterForTheSameDirectory()
    {
        var reply = new TaskCompletionSource<CloudFolderListing>(TaskCreationOptions.RunContinuationsAsynchronously);
        var reads = 0; CancellationToken underlying = default;
        var browser = new CloudFolderBrowser(() => Account, (_, _, token) =>
        {
            reads++; underlying = token; return reply.Task.WaitAsync(token);
        });
        using var firstCancellation = new CancellationTokenSource();
        var first = browser.BrowseAsync("child", firstCancellation.Token);
        var second = browser.BrowseAsync("child", default);
        firstCancellation.Cancel();
        await Assert.ThrowsAsync<OperationCanceledException>(() => first);
        Assert.AreEqual(1, reads);
        Assert.IsFalse(underlying.IsCancellationRequested);
        Assert.IsFalse(second.IsCompleted);
        reply.SetResult(Listing("child"));
        var result = await second.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.AreSame(result, await browser.BrowseAsync("child", default));
    }

    [TestMethod]
    public async Task LastCanceledWaiterCancelsTheReadAndLateReplyCannotPopulateCache()
    {
        // Synchronous completion makes the ignored-cancellation reply finish before the cache assertion.
        var oldReply = new TaskCompletionSource<CloudFolderListing>();
        var newReply = new TaskCompletionSource<CloudFolderListing>();
        var reads = 0; CancellationToken oldToken = default;
        var browser = new CloudFolderBrowser(() => Account, (_, _, token) =>
        {
            if (++reads == 1) { oldToken = token; return oldReply.Task; }
            return newReply.Task;
        });
        using var cancellation = new CancellationTokenSource();
        var abandoned = browser.BrowseAsync("child", cancellation.Token);
        cancellation.Cancel();
        await Assert.ThrowsAsync<OperationCanceledException>(() => abandoned);
        Assert.IsTrue(oldToken.IsCancellationRequested);
        var replacement = browser.BrowseAsync("child", default);
        oldReply.SetResult(Listing("child", "obsolete"));
        newReply.SetResult(Listing("child", "current"));
        var result = await replacement;
        Assert.AreEqual("current", result.Folders.Single().Id);
        Assert.AreSame(result, await browser.BrowseAsync("child", default));
        Assert.AreEqual(2, reads);
    }

    [TestMethod]
    [DataRow("account")]
    [DataRow("drive")]
    [DataRow("repository")]
    [DataRow("authorization")]
    public async Task ChangedAccountSettingsNeverReuseAnotherScopesCache(string change)
    {
        var current = Account; var reads = 0;
        var browser = new CloudFolderBrowser(() => current, (_, id, _) =>
        {
            reads++; return Task.FromResult(Listing(id ?? "root", reads.ToString()));
        });
        var original = await browser.BrowseAsync(null, default);
        current = change switch
        {
            "account" => current with { AccountId = "other" },
            "drive" => current with { DriveId = "other" },
            "repository" => current with { RepositoryId = "other" },
            _ => current with { RequiresAuthorization = true }
        };
        Assert.AreNotSame(original, await browser.BrowseAsync(null, default));
        Assert.AreEqual(2, reads);
    }

    [TestMethod]
    public async Task SwitchingAccountCancelsOldWaitersAndDiscardsIgnoredCancellationReply()
    {
        var current = Account;
        var oldReply = new TaskCompletionSource<CloudFolderListing>();
        var reads = 0;
        var browser = new CloudFolderBrowser(() => current, (account, _, _) =>
        {
            reads++;
            return account == Account ? oldReply.Task : Task.FromResult(Listing("root", "new-account"));
        });
        var obsolete = browser.BrowseAsync(null, default);
        current = Account with { DriveId = "another-drive" };
        var fresh = await browser.BrowseAsync(null, default);
        await Assert.ThrowsAsync<OperationCanceledException>(() => obsolete);
        oldReply.SetResult(Listing("root", "old-account"));
        Assert.AreSame(fresh, await browser.BrowseAsync(null, default));
        Assert.AreEqual("new-account", fresh.Folders.Single().Id);
        Assert.AreEqual(2, reads);
    }

    [TestMethod]
    public async Task AccountChangeDuringReadIsRejectedWithoutAnotherBrowseCall()
    {
        var current = Account;
        var reply = new TaskCompletionSource<CloudFolderListing>();
        var browser = new CloudFolderBrowser(() => current, (_, _, _) => reply.Task);
        var obsolete = browser.BrowseAsync(null, default);
        current = Account with { DriveId = "another-drive" };
        reply.SetResult(Listing("root"));
        await Assert.ThrowsExactlyAsync<IOException>(() => obsolete);
    }

    [TestMethod]
    public async Task CacheHasABoundedLeastRecentlyUsedDirectoryCount()
    {
        var reads = 0;
        var browser = new CloudFolderBrowser(() => Account, (_, id, _) =>
        {
            reads++; return Task.FromResult(Listing(id!));
        });
        for (var i = 0; i < 128; i++) await browser.BrowseAsync(i.ToString(), default);
        await browser.BrowseAsync("0", default);
        await browser.BrowseAsync("128", default);
        await browser.BrowseAsync("0", default);
        Assert.AreEqual(129, reads);
        await browser.BrowseAsync("1", default);
        Assert.AreEqual(130, reads, "The oldest untouched directory must be evicted instead of allowing unbounded growth.");
    }

    [TestMethod]
    public async Task PrefetchIsBoundedPrioritizesConfiguredFolderAndLeavesForegroundUnblocked()
    {
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var firstPair = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var requested = new ConcurrentQueue<string>();
        var active = 0; var maximum = 0;
        var browser = new CloudFolderBrowser(() => Account, async (_, id, token) =>
        {
            if (id is null) return Listing("root", Enumerable.Range(0, 20).Select(i => "child-" + i).ToArray());
            if (id == "foreground") return Listing(id);
            requested.Enqueue(id);
            var count = Interlocked.Increment(ref active);
            lock (requested) maximum = Math.Max(maximum, count);
            if (count == 2) firstPair.TrySetResult();
            try { await release.Task.WaitAsync(token); return Listing(id, "grandchild-" + id); }
            finally { Interlocked.Decrement(ref active); }
        });
        var root = await browser.BrowseAsync(null, default);
        var prefetch = browser.PrefetchAsync(root, default, "child-19");
        await firstPair.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.IsTrue(requested.Take(2).Contains("child-19"));
        await browser.BrowseAsync("foreground", default).WaitAsync(TimeSpan.FromSeconds(5));
        release.SetResult();
        await prefetch.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.HasCount(8, requested);
        Assert.AreEqual(2, maximum);
        Assert.IsFalse(requested.Any(id => id.StartsWith("grandchild-", StringComparison.Ordinal)));
        await browser.BrowseAsync("child-19", default);
        Assert.HasCount(8, requested, "Demand navigation to a prefetched directory must make no additional request.");
    }

    [TestMethod]
    public async Task PrefetchFailuresAreOptionalAndNeverCacheAFailure()
    {
        var reads = new ConcurrentDictionary<string, int>();
        var browser = new CloudFolderBrowser(() => Account, (_, id, _) =>
        {
            if (id is null) return Task.FromResult(Listing("root", "good", "bad"));
            reads.AddOrUpdate(id, 1, (_, count) => count + 1);
            if (id == "bad") throw new IOException("Synthetic prefetch failure");
            return Task.FromResult(Listing(id));
        });
        var root = await browser.BrowseAsync(null, default);
        await browser.PrefetchAsync(root, default);
        await browser.BrowseAsync("good", default);
        await Assert.ThrowsExactlyAsync<IOException>(() => browser.BrowseAsync("bad", default));
        Assert.AreEqual(1, reads["good"]);
        Assert.AreEqual(2, reads["bad"]);
    }

    [TestMethod]
    public async Task CancelingPrefetchDoesNotCancelAJoinedForegroundRequest()
    {
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var reply = new TaskCompletionSource<CloudFolderListing>(TaskCreationOptions.RunContinuationsAsynchronously);
        var childReads = 0; CancellationToken underlying = default;
        var browser = new CloudFolderBrowser(() => Account, (_, id, token) =>
        {
            if (id is null) return Task.FromResult(Listing("root", "child"));
            childReads++; underlying = token; started.TrySetResult(); return reply.Task.WaitAsync(token);
        });
        var root = await browser.BrowseAsync(null, default);
        using var cancellation = new CancellationTokenSource();
        var prefetch = browser.PrefetchAsync(root, cancellation.Token);
        await started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var foreground = browser.BrowseAsync("child", default);
        cancellation.Cancel();
        await Assert.ThrowsAsync<OperationCanceledException>(() => prefetch);
        Assert.IsFalse(underlying.IsCancellationRequested);
        reply.SetResult(Listing("child"));
        await foreground.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.AreEqual(1, childReads);
    }

    [TestMethod]
    public async Task StaleListingCannotPrefetchFolderIdsUnderAnotherAccount()
    {
        var current = Account; var reads = 0;
        var browser = new CloudFolderBrowser(() => current, (_, id, _) =>
        {
            reads++; return Task.FromResult(Listing(id ?? "root", "child"));
        });
        var old = await browser.BrowseAsync(null, default);
        current = current with { AccountId = "other" };
        await browser.PrefetchAsync(old, default);
        Assert.AreEqual(1, reads);
    }
}
