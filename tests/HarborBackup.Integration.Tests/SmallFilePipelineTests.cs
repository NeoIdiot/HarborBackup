using System.Collections.Concurrent;
using HarborBackup.Core.Domain;
using HarborBackup.Windows.Integration;
using static HarborBackup.Windows.Tests.CloudTransportTests;

namespace HarborBackup.Windows.Tests;

[TestClass]
[DoNotParallelize]
public sealed class SmallFilePipelineTests
{
    private sealed class Gate(Func<string, bool> matches, int target = 4)
    {
        public readonly TaskCompletionSource FourStarted = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public readonly TaskCompletionSource Release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public readonly ConcurrentQueue<string> Started = new();
        public int Active, Peak;
        public async Task WaitAsync(string name, CancellationToken token)
        {
            if (!matches(name)) return;
            Started.Enqueue(name);
            var active = Interlocked.Increment(ref Active);
            int previous;
            do { previous = Peak; } while (active > previous && Interlocked.CompareExchange(ref Peak, active, previous) != previous);
            if (active == target) FourStarted.TrySetResult();
            try { await Release.Task.WaitAsync(token); }
            finally { Interlocked.Decrement(ref Active); }
        }
    }

    [TestMethod]
    [DataRow(SyncDirection.Backup, false, 4)]
    [DataRow(SyncDirection.Pull, false, 4)]
    [DataRow(SyncDirection.Backup, true, 4)]
    [DataRow(SyncDirection.Pull, true, 4)]
    [DataRow(SyncDirection.Backup, false, 8)]
    [DataRow(SyncDirection.Pull, false, 8)]
    [DataRow(SyncDirection.Backup, true, 8)]
    [DataRow(SyncDirection.Pull, true, 8)]
    public async Task ConfiguredAndDefaultWorkersOverlapAndCancellationDrainsAll(SyncDirection direction, bool cancel, int concurrency)
    {
        using var f = new Fixture(); var a = f.Device("A"); var b = f.Device("B");
        for (var i = 0; i < 8; i++) File.WriteAllText(Path.Combine(a.P.Source, $"{i}.txt"), $"content {i}");
        PullSelection? selection = null;
        var project = direction == SyncDirection.Backup ? a : b;
        var runtime = concurrency == 8 ? new CloudBackupRuntime(project.C, () => f.Drive)
            : new CloudBackupRuntime(project.C, () => f.Drive, fileConcurrency: concurrency);
        if (direction == SyncDirection.Pull)
        {
            Assert.AreEqual(0, (await new CloudBackupRuntime(a.C, () => f.Drive).ExecuteAsync(SyncDirection.Backup, null, false, false, null, default)).ExitCode);
            selection = (await runtime.PreviewAsync(direction, "Documents", false, default)).PullSelection;
        }
        var gate = new Gate(name => name.EndsWith(".txt", StringComparison.Ordinal), concurrency);
        if (direction == SyncDirection.Backup) f.Drive.BeforeUpload = gate.WaitAsync;
        else f.Drive.BeforeDownload = gate.WaitAsync;
        using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        var running = runtime.ExecuteAsync(direction, "Documents", false, false, null, stop.Token, confirmedPull: selection);
        try
        {
            await gate.FourStarted.Task.WaitAsync(TimeSpan.FromSeconds(10));
            Assert.HasCount(concurrency, gate.Started);
            if (direction == SyncDirection.Backup)
                foreach (var name in gate.Started) Assert.ThrowsExactly<IOException>(() => { using var writer = File.OpenWrite(Path.Combine(a.P.Source, name)); });
        }
        finally { if (cancel) stop.Cancel(); else gate.Release.TrySetResult(); }
        var result = await running;
        Assert.AreEqual(concurrency, gate.Peak); Assert.AreEqual(0, gate.Active);
        Assert.AreEqual(cancel, result.Cancelled);
        if (cancel)
        {
            Assert.HasCount(concurrency, gate.Started);
            Assert.HasCount(concurrency, result.Projects.Single().Files.Where(p => p.Error == "Cancelled").ToArray());
            Assert.IsTrue(project.S.Pending);
        }
        else
        {
            Assert.AreEqual(0, result.ExitCode, string.Join("\n", result.Projects.SelectMany(p => p.Files).Select(p => p.Error)));
            Assert.HasCount(8, gate.Started); Assert.IsFalse(project.S.Pending);
            if (direction == SyncDirection.Pull)
                for (var i = 0; i < 8; i++) Assert.AreEqual($"content {i}", File.ReadAllText(Path.Combine(b.P.Source, $"{i}.txt")));
        }
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task ColdMetadataReadsAreBoundedAndCancellationIsDrained(bool cancel)
    {
        using var f = new Fixture(); var a = f.Device("A"); var b = f.Device("B");
        for (var i = 0; i < 8; i++) File.WriteAllText(Path.Combine(a.P.Source, $"{i}.txt"), "small file");
        Assert.AreEqual(0, (await new CloudBackupRuntime(a.C, () => f.Drive).ExecuteAsync(SyncDirection.Backup, null, false, false, null, default)).ExitCode);
        var writes = f.Drive.UploadIntents.Count;
        var gate = new Gate(name => Guid.TryParseExact(Path.GetFileNameWithoutExtension(name), "N", out _));
        f.Drive.BeforeDownload = gate.WaitAsync;
        using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        var running = new CloudBackupRuntime(b.C, () => f.Drive).PreviewAsync(SyncDirection.Pull, "Documents", false, stop.Token);
        try { await gate.FourStarted.Task.WaitAsync(TimeSpan.FromSeconds(10)); Assert.HasCount(4, gate.Started); }
        finally { if (cancel) stop.Cancel(); else gate.Release.TrySetResult(); }
        if (cancel) await Assert.ThrowsAsync<OperationCanceledException>(() => running);
        else
        {
            var result = await running;
            Assert.AreEqual(0, result.ExitCode); Assert.HasCount(8, gate.Started);
            Assert.AreEqual(8L, result.Projects.Single().Metrics.CloudMetadataRecordsRead);
        }
        Assert.AreEqual(4, gate.Peak); Assert.AreEqual(0, gate.Active);
        Assert.HasCount(writes, f.Drive.UploadIntents); Assert.AreEqual(0, f.Drive.BusinessDownloads);
    }
}
