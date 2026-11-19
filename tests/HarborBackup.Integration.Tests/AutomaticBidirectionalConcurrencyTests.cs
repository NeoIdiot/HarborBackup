using System.Collections.Concurrent;
using System.Text;
using System.Text.Json;
using HarborBackup.Core.Domain;
using HarborBackup.Windows.Integration;
using HarborBackup.Windows.Persistence;
using static HarborBackup.Windows.Tests.CloudTransportTests;

namespace HarborBackup.Windows.Tests;

[TestClass]
[DoNotParallelize]
public sealed class AutomaticBidirectionalConcurrencyTests
{
    private static readonly string[] DownloadNames = ["0-download.txt", "2-download.txt", "4-download.txt", "6-download.txt"];
    private static readonly string[] UploadNames = ["1-upload.txt", "3-upload.txt", "5-upload.txt", "7-upload.txt"];

    private sealed class TransferGate
    {
        public readonly TaskCompletionSource FourStarted = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public readonly TaskCompletionSource FourCancelled = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public readonly TaskCompletionSource Release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public readonly TaskCompletionSource AllowCancelledExit = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public readonly ConcurrentQueue<(string Name, SyncDirection Direction)> Started = new();
        public int Active, Peak;
        private int cancelled;

        public Task UploadAsync(string name, CancellationToken token) => WaitAsync(name, SyncDirection.Backup, token);
        public Task DownloadAsync(string name, CancellationToken token) => WaitAsync(name, SyncDirection.Pull, token);

        private async Task WaitAsync(string name, SyncDirection direction, CancellationToken token)
        {
            if (!DownloadNames.Contains(name) && !UploadNames.Contains(name)) return;
            Started.Enqueue((name, direction));
            var active = Interlocked.Increment(ref Active);
            int previous;
            do { previous = Peak; } while (active > previous && Interlocked.CompareExchange(ref Peak, active, previous) != previous);
            if (active == 4) FourStarted.TrySetResult();
            try { await Release.Task.WaitAsync(token); }
            catch (OperationCanceledException)
            {
                if (Interlocked.Increment(ref cancelled) == 4) FourCancelled.TrySetResult();
                // Hold the cancelled transfer callbacks open so the test can prove the run drains them.
                await AllowCancelledExit.Task;
                throw;
            }
            finally { Interlocked.Decrement(ref Active); }
        }
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task MixedTransfersOverlapReleaseSourceLocksAndDrainBeforeCancellationReturns(bool cancel)
    {
        using var f = new Fixture(); var a = f.Device("A"); var b = f.Device("B");
        foreach (var name in DownloadNames) File.WriteAllText(Path.Combine(a.P.Source, name), "base");
        await Apply(a.S); await Apply(b.S, SyncDirection.Pull);
        foreach (var name in DownloadNames) File.WriteAllText(Path.Combine(b.P.Source, name), "remote update " + name);
        await Apply(b.S);
        foreach (var name in UploadNames) File.WriteAllText(Path.Combine(a.P.Source, name), "local update " + name);
        var config = a.C with
        {
            AutomaticBidirectionalProjects = new HashSet<string> { a.P.ProjectId },
            // Force upload preparation for existing local files before the run selects their pull direction.
            Preview = a.C.Preview with { Hash = new(UseMetadataCache: false) }
        };
        var runtime = new CloudBackupRuntime(config, () => f.Drive, fileConcurrency: 4);
        var gate = new TransferGate();
        f.Drive.BeforeUpload = gate.UploadAsync; f.Drive.BeforeDownload = gate.DownloadAsync;
        using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        var running = runtime.ExecuteAsync(SyncDirection.Backup, null, false, false, null, stop.Token, automatic: true);
        try
        {
            await gate.FourStarted.Task.WaitAsync(TimeSpan.FromSeconds(10));
            var first = gate.Started.ToArray();
            CollectionAssert.AreEquivalent(new[] { "0-download.txt", "1-upload.txt", "2-download.txt", "3-upload.txt" }, first.Select(t => t.Name).ToArray());
            Assert.HasCount(2, first.Where(t => t.Direction == SyncDirection.Backup).ToArray());
            Assert.HasCount(2, first.Where(t => t.Direction == SyncDirection.Pull).ToArray());
            foreach (var transfer in first)
            {
                var path = Path.Combine(a.P.Source, transfer.Name);
                if (transfer.Direction == SyncDirection.Backup)
                    Assert.ThrowsExactly<IOException>(() => { using var writer = File.OpenWrite(path); });
                else
                {
                    // A prepared candidate that became a pull must release its upload source handle.
                    using var writer = File.Open(path, FileMode.Open, FileAccess.Write, FileShare.None);
                }
            }
            if (cancel)
            {
                stop.Cancel();
                await gate.FourCancelled.Task.WaitAsync(TimeSpan.FromSeconds(10));
                Assert.IsFalse(running.IsCompleted, "The run returned while cancelled transfers were still active.");
            }
        }
        finally
        {
            if (cancel) stop.Cancel();
            gate.Release.TrySetResult(); gate.AllowCancelledExit.TrySetResult();
            await running;
        }
        var report = await running;
        Assert.AreEqual(4, gate.Peak); Assert.AreEqual(0, gate.Active);
        Assert.AreEqual(cancel, report.Cancelled, JsonSerializer.Serialize(report));
        foreach (var name in DownloadNames.Concat(UploadNames))
        {
            using var writer = File.Open(Path.Combine(a.P.Source, name), FileMode.Open, FileAccess.Write, FileShare.None);
        }
        if (!cancel)
        {
            Assert.AreEqual(0, report.ExitCode, JsonSerializer.Serialize(report));
            Assert.HasCount(8, gate.Started); Assert.IsFalse(a.S.Pending);
            foreach (var name in DownloadNames)
                Assert.AreEqual("remote update " + name, File.ReadAllText(Path.Combine(a.P.Source, name)));
            var root = (await f.Drive.FindAsync("drive-root", "Documents", default))!;
            foreach (var name in UploadNames)
            {
                var item = (await f.Drive.FindAsync(root.Id, name, default))!;
                Assert.AreEqual("local update " + name, Encoding.UTF8.GetString(f.Drive.Items[item.Id].Bytes));
            }
            return;
        }
        Assert.AreEqual(130, report.ExitCode);
        Assert.HasCount(4, gate.Started);
        Assert.HasCount(4, report.Projects.Single().Files.Where(p => p.Error == "Cancelled").ToArray());
        var journalRoot = Path.Combine(CloudMapping.StateRoot(a.P, config), "cloud-transactions");
        var journals = Directory.GetFiles(journalRoot, "*.json").ToDictionary(p => p, File.ReadAllBytes);
        Assert.HasCount(4, journals);
        var records = journals.Keys.Select(path => JsonSerializer.Deserialize<CloudJournal>(File.ReadAllText(path), AtomicJson.Options)!).ToArray();
        Assert.HasCount(2, records.Where(j => j.Direction == SyncDirection.Backup).ToArray());
        Assert.HasCount(2, records.Where(j => j.Direction == SyncDirection.Pull).ToArray());
        Assert.IsTrue(a.S.Pending);
        foreach (var name in new[] { "0-download.txt", "2-download.txt" })
            Assert.AreEqual("base", File.ReadAllText(Path.Combine(a.P.Source, name)));

        // Automatic retries must preserve both cancelled transfer directions and continue unrelated files.
        f.Drive.BeforeUpload = null; f.Drive.BeforeDownload = null;
        var next = await runtime.ExecuteAsync(SyncDirection.Backup, null, false, false, null, default, automatic: true);
        Assert.HasCount(4, next.Projects.Single().Files.Where(p => p.Error == "PendingFileOperation").ToArray());
        Assert.HasCount(4, next.Projects.Single().Files.Where(p => p.Changed).ToArray());
        foreach (var journal in journals) CollectionAssert.AreEqual(journal.Value, File.ReadAllBytes(journal.Key));
        CollectionAssert.AreEquivalent(journals.Keys.ToArray(), Directory.GetFiles(journalRoot, "*.json"));
    }
}
