using System.Collections.Concurrent;
using HarborBackup.Core.Domain;
using HarborBackup.Windows.Integration;
using static HarborBackup.Windows.Tests.CloudTransportTests;

namespace HarborBackup.Windows.Tests;

[TestClass]
[DoNotParallelize]
public sealed class CloudOptimizationTests
{
    private sealed class ThrottledDrive : FakeDrive, HarborBackup.OneDrive.Transport.ICloudDiagnostics
    {
        public long Throttles;
        public HarborBackup.OneDrive.Transport.CloudNetworkMetrics NetworkMetrics => new(0, 0, 0, Interlocked.Read(ref Throttles));
    }
    [TestMethod]
    public async Task SuccessfulThrottleRetryReducesSubsequentDispatchWithoutAbandoningInflightFiles()
    {
        using var fixture = new Fixture(); var drive = new ThrottledDrive();
        foreach (var entry in fixture.Drive.Items) drive.Items[entry.Key] = entry.Value;
        fixture.Drive = drive;
        var device = fixture.Device("A");
        for (var i = 0; i < 8; i++) File.WriteAllText(Path.Combine(device.P.Source, $"{i}.txt"), "payload");
        var first = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var second = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseFirst = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseSecond = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var started = 0; var activeSecond = 0; var peakSecond = 0;
        drive.BeforeUpload = async (name, token) =>
        {
            if (!name.EndsWith(".txt", StringComparison.Ordinal)) return;
            var count = Interlocked.Increment(ref started);
            if (count <= 4)
            {
                if (count == 4) first.TrySetResult();
                await releaseFirst.Task.WaitAsync(token);
            }
            else
            {
                var active = Interlocked.Increment(ref activeSecond);
                int previous;
                do { previous = peakSecond; } while (active > previous && Interlocked.CompareExchange(ref peakSecond, active, previous) != previous);
                if (active == 2) second.TrySetResult();
                try { await releaseSecond.Task.WaitAsync(token); }
                finally { Interlocked.Decrement(ref activeSecond); }
            }
        };
        using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        var executing = new CloudBackupRuntime(device.C, () => drive, fileConcurrency: 4).ExecuteAsync(SyncDirection.Backup, null, false, false, null, stop.Token);
        try
        {
            await first.Task.WaitAsync(stop.Token);
            Interlocked.Exchange(ref drive.Throttles, 1);
            releaseFirst.TrySetResult();
            await second.Task.WaitAsync(stop.Token);
            Assert.AreEqual(2, peakSecond);
        }
        finally { releaseFirst.TrySetResult(); releaseSecond.TrySetResult(); }
        var result = await executing;
        Assert.AreEqual(0, result.ExitCode);
        Assert.AreEqual(1L, result.Projects.Single().Metrics.CloudConcurrencyReductions);
        Assert.AreEqual(8, started);
        Assert.IsFalse(device.S.Pending);
    }
    private sealed class UploadGate
    {
        public readonly TaskCompletionSource PairStarted = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public readonly TaskCompletionSource Release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public readonly ConcurrentQueue<string> Started = new();
        private int active;
        public int Peak;
        public string? Fail;
        public async Task WaitAsync(string name, CancellationToken token)
        {
            if (!name.EndsWith(".txt", StringComparison.Ordinal)) return;
            Started.Enqueue(name);
            var count = Interlocked.Increment(ref active);
            int previous;
            do { previous = Peak; } while (count > previous && Interlocked.CompareExchange(ref Peak, count, previous) != previous);
            if (Started.Count == 2) PairStarted.TrySetResult();
            try
            {
                await Release.Task.WaitAsync(token);
                if (name == Fail) throw new IOException("Injected upload failure.");
            }
            finally { Interlocked.Decrement(ref active); }
        }
    }

    [TestMethod]
    public async Task UnchangedCommonBaselineNeedsNoExtraLookupOrRewrite()
    {
        using var fixture = new Fixture(); var device = fixture.Device("A");
        File.WriteAllText(Path.Combine(device.P.Source, "file.txt"), "original");
        await Apply(device.S);
        var read = await device.S.ReadAsync(false, default);
        var file = read.Snapshot.Files.Single();
        var baseline = Directory.GetFiles(fixture.Root, "*.json", SearchOption.AllDirectories)
            .Single(p => Path.GetFileName(Path.GetDirectoryName(p)) == "baselines");
        var timestamp = DateTime.UtcNow.AddDays(-10);
        File.SetLastWriteTimeUtc(baseline, timestamp);
        var before = fixture.Drive.Finds;
        var downloads = fixture.Drive.Downloads;
        var heads = read.Heads[file.RelativePath];
        var decision = device.S.Decide(file, heads, SyncDirection.Backup);
        Assert.AreEqual(FileAction.Equal, decision.Action);
        await device.S.ApplyAsync(file, heads, SyncDirection.Backup, decision, default);
        Assert.AreEqual(before, fixture.Drive.Finds);
        Assert.AreEqual(downloads, fixture.Drive.Downloads);
        Assert.AreEqual(timestamp, File.GetLastWriteTimeUtc(baseline));
    }

    [TestMethod]
    public async Task NewBaselineStillChecksLiveCloudVersionBeforeCommitting()
    {
        using var fixture = new Fixture(); var a = fixture.Device("A"); var b = fixture.Device("B");
        File.WriteAllText(Path.Combine(a.P.Source, "file.txt"), "same");
        await Apply(a.S);
        File.WriteAllText(Path.Combine(b.P.Source, "file.txt"), "same");
        var read = await b.S.ReadAsync(false, default);
        var file = read.Snapshot.Files.Single(); var heads = read.Heads[file.RelativePath];
        Assert.IsNull(file.Baseline);
        var remote = fixture.Drive.Items[heads.Single().ItemId];
        fixture.Drive.Items[remote.Item.Id] = (remote.Item with { ETag = "changed" }, remote.Parent, remote.Bytes);
        var before = fixture.Drive.Finds;
        await Assert.ThrowsExactlyAsync<IOException>(() =>
            b.S.ApplyAsync(file, heads, SyncDirection.Backup, b.S.Decide(file, heads, SyncDirection.Backup), default));
        Assert.IsGreaterThan(before, fixture.Drive.Finds);
        Assert.IsFalse(Directory.GetDirectories(Path.Combine(fixture.Root, "B"), "baselines", SearchOption.AllDirectories).Any());
        fixture.Drive.Items[remote.Item.Id] = remote;
        await Apply(b.S);
        Assert.HasCount(1, Directory.GetDirectories(Path.Combine(fixture.Root, "B"), "baselines", SearchOption.AllDirectories));
    }

    [TestMethod]
    public async Task TwoUploadsOverlapKeepSourcesLockedAndPreserveOrdinaryTree()
    {
        using var fixture = new Fixture(); var device = fixture.Device("A");
        foreach (var name in new[] { "a.txt", "b.txt", "c.txt" })
            File.WriteAllText(Path.Combine(device.P.Source, name), name);
        Directory.CreateDirectory(Path.Combine(device.P.Source, "sub"));
        File.WriteAllText(Path.Combine(device.P.Source, "sub", "d.txt"), "nested");
        var gate = new UploadGate(); fixture.Drive.BeforeUpload = gate.WaitAsync;
        using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        var runtime = new CloudBackupRuntime(device.C, () => fixture.Drive, fileConcurrency: 2);
        var running = runtime.ExecuteAsync(SyncDirection.Backup, "Documents", false, false, null, stop.Token);
        try
        {
            await gate.PairStarted.Task.WaitAsync(TimeSpan.FromSeconds(10));
            Assert.HasCount(2, gate.Started);
            foreach (var name in gate.Started)
                Assert.ThrowsExactly<IOException>(() => { using var writer = File.OpenWrite(Path.Combine(device.P.Source, name)); });
        }
        finally { gate.Release.TrySetResult(); }
        var result = await running;
        Assert.AreEqual(0, result.ExitCode, string.Join("\n", result.Projects.SelectMany(p => p.Files).Select(f => f.Error)));
        Assert.AreEqual(2, gate.Peak);
        Assert.HasCount(4, gate.Started);
        Assert.IsFalse(device.S.Pending);
        var read = await device.S.ReadAsync(false, default);
        Assert.IsTrue(read.Heads.ContainsKey("sub/d.txt"));
        Assert.AreEqual(0, fixture.Drive.BusinessDownloads);
    }

    [TestMethod]
    public async Task FailedBatchStopsDispatchAndItsJournalCanRecover()
    {
        using var fixture = new Fixture(); var device = fixture.Device("A");
        foreach (var name in new[] { "a.txt", "b.txt", "c.txt" }) File.WriteAllText(Path.Combine(device.P.Source, name), name);
        var gate = new UploadGate { Fail = "a.txt" }; fixture.Drive.BeforeUpload = gate.WaitAsync;
        using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        var runtime = new CloudBackupRuntime(device.C, () => fixture.Drive, fileConcurrency: 2);
        var running = runtime.ExecuteAsync(SyncDirection.Backup, "Documents", false, false, null, stop.Token);
        try { await gate.PairStarted.Task.WaitAsync(TimeSpan.FromSeconds(10)); }
        finally { gate.Release.TrySetResult(); }
        Assert.AreNotEqual(0, (await running).ExitCode);
        CollectionAssert.AreEquivalent(new[] { "a.txt", "b.txt" }, gate.Started.ToArray());
        Assert.IsTrue(device.S.Pending);
        fixture.Drive.BeforeUpload = null;
        var recovered = await runtime.ExecuteAsync(SyncDirection.Backup, "Documents", false, true, null, default);
        Assert.AreEqual(0, recovered.ExitCode);
        Assert.IsFalse(device.S.Pending);
        var read = await device.S.ReadAsync(false, default);
        Assert.IsTrue(read.Heads.ContainsKey("a.txt"));
        Assert.IsTrue(read.Heads.ContainsKey("b.txt"));
        Assert.IsFalse(read.Heads.ContainsKey("c.txt"));
    }

    [TestMethod]
    public async Task CancelledBatchDrainsBothTransfersBeforeReturningAndRecovers()
    {
        using var fixture = new Fixture(); var device = fixture.Device("A");
        foreach (var name in new[] { "a.txt", "b.txt", "c.txt" }) File.WriteAllText(Path.Combine(device.P.Source, name), name);
        var gate = new UploadGate(); fixture.Drive.BeforeUpload = gate.WaitAsync;
        using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        var runtime = new CloudBackupRuntime(device.C, () => fixture.Drive, fileConcurrency: 2);
        var running = runtime.ExecuteAsync(SyncDirection.Backup, "Documents", false, false, null, stop.Token);
        try { await gate.PairStarted.Task.WaitAsync(TimeSpan.FromSeconds(10)); }
        finally { stop.Cancel(); }
        var result = await running;
        Assert.IsTrue(result.Cancelled);
        Assert.HasCount(2, gate.Started);
        Assert.HasCount(2, result.Projects.Single().Files.Where(f => f.Error == "Cancelled").ToArray());
        Assert.IsTrue(device.S.Pending);
        fixture.Drive.BeforeUpload = null;
        Assert.AreEqual(0, (await runtime.ExecuteAsync(SyncDirection.Backup, "Documents", false, true, null, default)).ExitCode);
        Assert.IsFalse(device.S.Pending);
    }

    [TestMethod]
    public async Task ConcurrentUpdatesAndPullKeepOnePredecessorPerFile()
    {
        using var fixture = new Fixture(); var a = fixture.Device("A"); var b = fixture.Device("B");
        var runtime = new CloudBackupRuntime(a.C, () => fixture.Drive);
        foreach (var name in new[] { "a.txt", "b.txt" }) File.WriteAllText(Path.Combine(a.P.Source, name), "first");
        Assert.AreEqual(0, (await runtime.ExecuteAsync(SyncDirection.Backup, "Documents", false, false, null, default)).ExitCode);
        await Apply(b.S, SyncDirection.Pull);
        foreach (var version in new[] { "second", "third" })
        {
            foreach (var name in new[] { "a.txt", "b.txt" }) File.WriteAllText(Path.Combine(a.P.Source, name), version);
            Assert.AreEqual(0, (await runtime.ExecuteAsync(SyncDirection.Backup, "Documents", false, false, null, default)).ExitCode);
        }
        var archiveIndexes = fixture.Drive.Items.Values.Where(v => v.Item.Name is "a.txt" or "b.txt" && v.Parent != fixture.Drive.Items.Values.Single(i => i.Parent == "drive-root" && i.Item.Name == "Documents").Item.Id).ToArray();
        Assert.HasCount(2, archiveIndexes);
        var pull = new CloudBackupRuntime(b.C, () => fixture.Drive);
        var preview = await pull.PreviewAsync(SyncDirection.Pull, "Documents", false, default);
        var gate = new UploadGate(); fixture.Drive.BeforeDownload = gate.WaitAsync;
        using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        var running = pull.ExecuteAsync(SyncDirection.Pull, "Documents", false, false, null, stop.Token, preview.PullSelection);
        try
        {
            var first = await Task.WhenAny(gate.PairStarted.Task, running).WaitAsync(TimeSpan.FromSeconds(10));
            if (first == running)
                Assert.AreEqual(0, (await running).ExitCode, string.Join("\n", (await running).Projects.SelectMany(p => p.Files).Select(v => v.Error)));
            await gate.PairStarted.Task.WaitAsync(TimeSpan.FromSeconds(10));
        }
        catch (TimeoutException) { /* Release the first worker so the actual second-worker error can be asserted below. */ }
        finally { gate.Release.TrySetResult(); await running; }
        Assert.AreEqual(0, (await running).ExitCode, string.Join("\n", (await running).Projects.SelectMany(p => p.Files).Select(v => v.Error)));
        Assert.AreEqual(2, gate.Peak);
        foreach (var name in new[] { "a.txt", "b.txt" }) Assert.AreEqual("third", File.ReadAllText(Path.Combine(b.P.Source, name)));
        Assert.IsFalse(b.S.Pending);
    }
}
