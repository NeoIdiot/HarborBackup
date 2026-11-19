using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;
using HarborBackup.Core.Domain;
using HarborBackup.OneDrive.Authentication;
using HarborBackup.OneDrive.Transport;
using HarborBackup.Windows.Integration;
using HarborBackup.Windows.Persistence;
using HarborBackup.Windows.Persistence.Configuration;

var work = Path.GetFullPath(args[0]);
HarborBackup.Windows.FileSystem.SafePath.Validate(work, Path.Combine(Environment.CurrentDirectory, "artifacts"));
var production = new DataPaths(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs", "HarborBackup", "data"));
var original = new CloudAccountStore(production).Read();
var configPath = Path.Combine(production.Config, "onedrive-account.json");
var credentialPath = Path.Combine(production.Root, "credentials", original.ClientId + ".bin");
var configHash = SHA256.HashData(File.ReadAllBytes(configPath));
var credentialHash = SHA256.HashData(File.ReadAllBytes(credentialPath));
var aData = new DataPaths(Path.Combine(work, "device-a", "data"));
Directory.CreateDirectory(Path.Combine(aData.Root, "credentials"));
var credentialCopy = Path.Combine(aData.Root, "credentials", original.ClientId + ".bin");
File.Copy(credentialPath, credentialCopy, false);
var trace = new Trace(work);
var tokens = new TimedTokens(new AccountTokens(original.ClientId, Path.GetDirectoryName(credentialCopy)!, original.AccountId), trace);
MeasuredDrive Open() => new(new GraphDrive(tokens, original.DriveId!, new ObservedHandler(trace)), trace);
using var setup = Open();
using var deadline = new CancellationTokenSource(TimeSpan.FromMinutes(20));
var token = deadline.Token;
var cloudName = "HarborBackup-flow-test-" + Guid.NewGuid().ToString("N");
RemoteItem? owned = null;
using var heartbeat = new Timer(_ => Console.WriteLine("PROGRESS " + trace.Scenario), null, TimeSpan.FromSeconds(25), TimeSpan.FromSeconds(25));
void Check(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
try
{
    trace.Scenario = "setup";
    _ = await tokens.GetAsync(token);
    var root = await setup.RootAsync(token);
    owned = await setup.FolderAsync(root.Id, cloudName, token);
    File.WriteAllText(Path.Combine(work, "owned-root.json"), JsonSerializer.Serialize(new { owned.Id, name = cloudName, driveId = original.DriveId }));
    Console.WriteLine("CREATED " + cloudName);
    if (args.Contains("--transport-compare"))
    {
        var payload = RandomNumberGenerator.GetBytes(8 * 1024);
        using var hashInput = new MemoryStream(payload);
        var checksum = await QuickXor.ComputeAsync(hashInput, token);
        var metadataFolder = await setup.FolderAsync(owned.Id, "transport-metadata", token);
        for (var repeat = 0; repeat < 2; repeat++)
        foreach (var concurrency in repeat == 0 ? new[] { 4, 8 } : new[] { 8, 4 })
        {
            trace.Scenario = "setup";
            var parent = await setup.FolderAsync(owned.Id, $"small-{repeat}-{concurrency}", token);
            using var drive = Open();
            var repo = new CloudRepository(drive, original, aData);
            trace.Begin($"transport-small-{concurrency}-round-{repeat}");
            await Parallel.ForEachAsync(Enumerable.Range(0, 8), new ParallelOptions { MaxDegreeOfParallelism = concurrency, CancellationToken = token }, async (i, ct) =>
            {
                using var input = new MemoryStream(payload);
                var item = await drive.UploadAsync(parent.Id, $"synthetic-{i}.bin", input, null, _ => { }, ct, reconcileExisting: false);
                Check(item.QuickXorHash == checksum && item.Size == payload.Length, "Small transport checksum mismatch.");
                var dated = await drive.SetModifiedAsync(item, DateTimeOffset.UtcNow.AddDays(-1), ct);
                await repo.JsonAsync(metadataFolder.Id, $"record-{repeat}-{concurrency}-{i}.json", new { item = dated.Id, dated.ETag }, ct, reconcileExisting: false);
            });
            trace.Finish(0, new() { CloudRequests = drive.NetworkMetrics.Requests, CloudUploadedFiles = 8, CloudUploadedBytes = 8 * payload.Length });
        }
        var large = RandomNumberGenerator.GetBytes(32 * 1024 * 1024);
        using var largeHash = new MemoryStream(large);
        var expected = await QuickXor.ComputeAsync(largeHash, token);
        for (var repeat = 0; repeat < 2; repeat++)
        foreach (var chunk in repeat == 0 ? new[] { 10 * 320 * 1024, 10 * 1024 * 1024 } : new[] { 10 * 1024 * 1024, 10 * 320 * 1024 })
        {
            using var drive = new MeasuredDrive(new GraphDrive(tokens, original.DriveId!, new ObservedHandler(trace), new(chunk)), trace);
            trace.Begin($"transport-large-{chunk}-round-{repeat}");
            using var input = new MemoryStream(large);
            var item = await drive.UploadAsync(owned.Id, $"large-{repeat}-{chunk}.bin", input, null, _ => { }, token, reconcileExisting: false);
            Check(item.Size == large.Length && item.QuickXorHash == expected, "Large transport checksum mismatch.");
            trace.Finish(0, new() { CloudRequests = drive.NetworkMetrics.Requests, CloudUploadedFiles = 1, CloudUploadedBytes = large.Length });
        }
    }
    else
    {
    var management = await setup.FolderAsync(owned.Id, "management", token);
    var target = await setup.FolderAsync(owned.Id, "project", token);
    var account = original with { RootId = management.Id, RootName = cloudName + "/management", RepositoryId = Guid.NewGuid().ToString("N"), Folders = new() };
    await new CloudRepository(setup, account, aData).JsonAsync(management.Id, "repository.json", new CloudRepository.RepositoryIdentity(2, account.RepositoryId!, new()), token, reconcileExisting: false);
    var source = Path.Combine(work, "device-a", "source");
    var expected = new Dictionary<string, byte[]>();
    for (var i = 0; i < 4; i++)
    {
        var relative = $"folder-{i / 2}/synthetic-{i}.bin";
        var path = Path.Combine(source, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        expected[relative] = RandomNumberGenerator.GetBytes(8 * 1024);
        File.WriteAllBytes(path, expected[relative]);
    }
    var project = new ProjectDefinition(source, cloudName + "/project", "Owned flow test", [], [], DeviceIdentity.Digest(cloudName), DeviceIdentity.Digest(cloudName + "/A"))
    { CloudFolderId = target.Id, MappingVersion = Guid.NewGuid().ToString("N") };
    ConfigurationSnapshot Config(DataPaths data, ProjectDefinition p, string device) => new([p], new(data.State, DeviceIdentity.Digest(cloudName + device), new()), 10, 2, data.Root)
    { CloudAccount = account, LogDirectory = data.Logs, HistoryDirectory = data.History, ArchiveDirectory = data.Archives, ConflictDirectory = data.Conflicts };
    var a = Config(aData, project, "A");
    var aRuntime = new CloudBackupRuntime(a, Open);
    async Task Execute(string name, CloudBackupRuntime runtime, SyncDirection direction, string? destination = null, PullSelection? selection = null)
    {
        trace.Begin(name);
        var result = await runtime.ExecuteAsync(direction, destination, false, false, trace, token, confirmedPull: selection);
        trace.Finish(result.ExitCode, result.Projects.Single().Metrics);
        Check(result.ExitCode == 0, "Business operation failed: " + name);
    }
    async Task<PreviewReport> Preview(string name, CloudBackupRuntime runtime, SyncDirection direction, string destination)
    {
        trace.Begin(name);
        var result = await runtime.PreviewAsync(direction, destination, false, token, trace);
        trace.Finish(result.ExitCode, result.Projects.Single().Metrics);
        Check(result.ExitCode == 0, "Preview failed: " + name);
        return result;
    }
    await Execute("01-first-backup", aRuntime, SyncDirection.Backup);
    await Execute("02-no-change-after-upload", aRuntime, SyncDirection.Backup);
    await Execute("03-no-change-settling", aRuntime, SyncDirection.Backup);
    await Execute("04-no-change-steady", aRuntime, SyncDirection.Backup);
    trace.Scenario = "setup";
    using (var unrelated = new MemoryStream(new byte[] { 1, 2, 3 }))
        await setup.UploadAsync(owned.Id, "unrelated.bin", unrelated, null, _ => { }, token, reconcileExisting: false);
    await Preview("04b-unrelated-preview", aRuntime, SyncDirection.Backup, project.ProjectId);
    if (!args.Contains("--cache-only"))
    {
    foreach (var relative in expected.Keys.Take(2).ToArray())
    {
        expected[relative] = RandomNumberGenerator.GetBytes(8 * 1024);
        File.WriteAllBytes(Path.Combine(source, relative), expected[relative]);
    }
    await Preview("05-changed-upload-preview", aRuntime, SyncDirection.Backup, project.ProjectId);
    await Execute("06-changed-backup-with-history", aRuntime, SyncDirection.Backup);
    var bData = new DataPaths(Path.Combine(work, "device-b", "data"));
    var bProject = project with { Source = Path.Combine(work, "device-b", "source"), Fingerprint = DeviceIdentity.Digest(cloudName + "/B"), MappingVersion = Guid.NewGuid().ToString("N") };
    var bRuntime = new CloudBackupRuntime(Config(bData, bProject, "B"), Open);
    var preview = await Preview("07-new-device-pull-preview", bRuntime, SyncDirection.Pull, project.ProjectId);
    Check(preview.PullSelection is not null, "Pull confirmation unavailable.");
    await Execute("08-confirmed-pull", bRuntime, SyncDirection.Pull, project.ProjectId, preview.PullSelection);
    foreach (var pair in expected)
        Check(SHA256.HashData(File.ReadAllBytes(Path.Combine(bProject.Source, pair.Key))).SequenceEqual(SHA256.HashData(pair.Value)), "Pulled content hash mismatch.");
    await Execute("09-pull-device-no-change-backup", bRuntime, SyncDirection.Backup);
    }
    Console.WriteLine(args.Contains("--cache-only") ? "PASS cache workflow scenarios" : "PASS all workflow scenarios and pulled SHA256 verification");
    }
}
catch (Exception ex)
{
    Console.WriteLine(JsonSerializer.Serialize(new { failure = trace.Scenario, type = ex.GetType().Name,
        status = ex is CloudRequestException request ? (int?)request.StatusCode : null,
        frames = new StackTrace(ex, false).GetFrames().Take(8).Select(f => f.GetMethod()?.DeclaringType?.Name + "." + f.GetMethod()?.Name) }));
    Environment.ExitCode = 1;
}
finally
{
    trace.Scenario = "cleanup";
    var cloudDeleted = false;
    try
    {
        if (owned is not null)
        {
            using var cleanup = new CancellationTokenSource(TimeSpan.FromMinutes(3));
            var current = await setup.GetAsync(owned.Id, cleanup.Token);
            Check(current.Id == owned.Id && current.Name == cloudName && current.Folder, "Cleanup identity mismatch.");
            await setup.DeleteAsync(current, cleanup.Token);
            cloudDeleted = true;
        }
    }
    catch (Exception ex) { Console.WriteLine("CLEANUP_FAILURE " + ex.GetType().Name); Environment.ExitCode = 1; }
    finally
    {
        File.Delete(credentialCopy);
        var cleanupResult = new { cloudDeleted, credentialsRemoved = !File.Exists(credentialCopy),
            configUnchanged = SHA256.HashData(File.ReadAllBytes(configPath)).SequenceEqual(configHash),
            credentialUnchanged = SHA256.HashData(File.ReadAllBytes(credentialPath)).SequenceEqual(credentialHash) };
        File.WriteAllText(Path.Combine(work, "cleanup.json"), JsonSerializer.Serialize(cleanupResult));
        Console.WriteLine("CLEANUP " + JsonSerializer.Serialize(cleanupResult));
        if (!cleanupResult.configUnchanged || !cleanupResult.credentialUnchanged || !cleanupResult.credentialsRemoved || !cloudDeleted) Environment.ExitCode = 1;
        trace.Save();
    }
}

sealed record Span(long Id, string Scenario, string Kind, string Operation, string Caller, long Parent, double Start, double End, int Status = 0, string Resource = "");
sealed record ProgressEvent(string Scenario, double At, string Phase, string? Path);
sealed class Trace(string directory) : IProgress<BackupProgress>
{
    private readonly Stopwatch clock = Stopwatch.StartNew();
    private long nextId;
    public readonly AsyncLocal<long> Current = new();
    public readonly ConcurrentQueue<Span> Spans = new();
    public readonly ConcurrentQueue<ProgressEvent> Events = new();
    public readonly ConcurrentQueue<object> Deltas = new();
    public readonly List<object> Results = [];
    public string Scenario = "setup";
    private double scenarioStart;
    public double Now => clock.Elapsed.TotalMilliseconds;
    public long Id() => Interlocked.Increment(ref nextId);
    public void Report(BackupProgress value) => Events.Enqueue(new(Scenario, Now, value.Phase.ToString(), value.Path));
    public void Begin(string name) { Scenario = name; scenarioStart = Now; Console.WriteLine("START " + name); }
    public void Save()
    {
        var options = new JsonSerializerOptions { WriteIndented = true };
        File.WriteAllText(Path.Combine(directory, "trace.json"), JsonSerializer.Serialize(new { spans = Spans.ToArray(), progress = Events.ToArray(), deltas = Deltas.ToArray() }, options));
        File.WriteAllText(Path.Combine(directory, "results.json"), JsonSerializer.Serialize(Results, options));
    }
    public static double Union(IEnumerable<(double Start, double End)> input)
    {
        double total = 0, start = -1, end = -1;
        foreach (var v in input.OrderBy(v => v.Start))
        {
            if (v.End <= v.Start) continue;
            if (start < 0) { start = v.Start; end = v.End; }
            else if (v.Start <= end) end = Math.Max(end, v.End);
            else { total += end - start; start = v.Start; end = v.End; }
        }
        return total + (start < 0 ? 0 : end - start);
    }
    public void Finish(int exitCode, ReadMetrics metrics)
    {
        var finish = Now;
        var all = Spans.Where(s => s.Scenario == Scenario).ToArray();
        var http = all.Where(s => s.Kind == "http").ToArray();
        var events = Events.Where(e => e.Scenario == Scenario).OrderBy(e => e.At).ToArray();
        double First(string phase, double after = 0) => events.FirstOrDefault(e => e.Phase == phase && e.At >= after)?.At ?? finish;
        var readStart = First("ReadingMetadata");
        var scanStart = First("Scanning", readStart);
        var processStart = First("Processing", scanStart);
        var publishStart = First("Publishing", processStart);
        // These are sequential coarse phases. Concurrent per-file UI events are not added as wall time.
        var boundaries = new[] { ("entry-recovery", scenarioStart, readStart), ("cloud-observation", readStart, scanStart),
            ("local-scan", scanStart, processStart), ("files-and-mapping", processStart, publishStart), ("publication-and-report", publishStart, finish) };
        if (events.Length == 0) boundaries = [("transport", scenarioStart, finish)];
        var result = new { scenario = Scenario, exitCode, elapsedMs = Math.Round(finish - scenarioStart, 2),
            startedAtMs = scenarioStart, endedAtMs = finish,
            requests = http.Length, httpActiveWallMs = Math.Round(Union(http.Select(s => (s.Start, s.End))), 2),
            httpSummedMs = Math.Round(http.Sum(s => s.End - s.Start), 2),
            phases = boundaries.Where(b => b.Item3 > b.Item2).Select(b => new { phase = b.Item1, wallMs = Math.Round(b.Item3 - b.Item2, 2),
                requests = http.Count(s => s.Start >= b.Item2 && s.Start < b.Item3),
                httpActiveWallMs = Math.Round(Union(http.Select(s => (Math.Max(s.Start, b.Item2), Math.Min(s.End, b.Item3)))), 2) }).ToArray(),
            api = all.Where(s => s.Kind == "api").GroupBy(s => s.Caller + " -> " + s.Operation)
                .Select(g => new { operation = g.Key, count = g.Count(), summedMs = Math.Round(g.Sum(s => s.End - s.Start), 2) }).OrderByDescending(g => g.summedMs).ToArray(),
            http = http.GroupBy(s => s.Operation).Select(g => new { operation = g.Key, count = g.Count(), summedMs = Math.Round(g.Sum(s => s.End - s.Start), 2), maxMs = Math.Round(g.Max(s => s.End - s.Start), 2) }).ToArray(),
            duplicateGetRequests = http.Where(s => s.Operation.StartsWith("GET ", StringComparison.Ordinal)).GroupBy(s => s.Resource).Sum(g => g.Count() - 1),
            tokenCalls = all.Count(s => s.Kind == "token"), tokenSummedMs = Math.Round(all.Where(s => s.Kind == "token").Sum(s => s.End - s.Start), 2), metrics };
        Results.Add(result); Save();
        Console.WriteLine("RESULT " + JsonSerializer.Serialize(new { result.scenario, result.exitCode, result.elapsedMs, result.requests, result.httpActiveWallMs, result.phases,
            result.duplicateGetRequests, metrics.CloudInventoryCacheHits, metrics.CloudObservationUploads, metrics.CloudObservationSkipped }));
    }
}
sealed class TimedTokens(IAccessTokens inner, Trace trace) : IAccessTokens
{
    public async Task<string> GetAsync(CancellationToken token)
    {
        var start = trace.Now; var id = trace.Id(); var scenario = trace.Scenario; var parent = trace.Current.Value;
        try { return await inner.GetAsync(token); }
        finally { trace.Spans.Enqueue(new(id, scenario, "token", "GetToken", "", parent, start, trace.Now)); }
    }
}
sealed class MeasuredDrive(GraphDrive inner, Trace trace) : ICloudDrive, ICloudChanges, ICloudDiagnostics, ICloudImmutableWrites, ICloudMetadataReads
{
    public CloudNetworkMetrics NetworkMetrics => inner.NetworkMetrics;
    private async Task<T> Call<T>(string operation, Func<Task<T>> action)
    {
        var caller = new StackTrace(false).GetFrames().Select(f => f.GetMethod()?.DeclaringType?.FullName + "." + f.GetMethod()?.Name)
            .FirstOrDefault(n => n.StartsWith("HarborBackup.Windows.", StringComparison.Ordinal)) ?? "harness";
        caller = Regex.Replace(caller, @".*\.(\w+)\+<([^>]+)>d__\d+\.MoveNext", "$1.$2");
        var id = trace.Id(); var start = trace.Now; var scenario = trace.Scenario; var old = trace.Current.Value;
        trace.Current.Value = id;
        try { return await action(); }
        finally { trace.Spans.Enqueue(new(id, scenario, "api", operation, caller, old, start, trace.Now)); trace.Current.Value = old; }
    }
    private async Task Call(string operation, Func<Task> action) => await Call(operation, async () => { await action(); return true; });
    public void Dispose() => inner.Dispose();
    public Task<IReadOnlyDictionary<string, byte[]>> ReadMetadataAsync(IReadOnlyList<RemoteItem> items, CancellationToken t) => Call("ReadMetadataBatch", () => inner.ReadMetadataAsync(items, t));
    public Task<RemoteItem> UploadImmutableAsync(string p, string n, Stream s, CancellationToken t) => Call("UploadMetadata", () => inner.UploadImmutableAsync(p, n, s, t));
    public Task<RemoteItem> RootAsync(CancellationToken t) => Call("Root", () => inner.RootAsync(t));
    public Task<RemoteItem> GetAsync(string id, CancellationToken t) => Call("GetItem", () => inner.GetAsync(id, t));
    public Task<RemoteItem?> FindAsync(string p, string n, CancellationToken t) => Call("FindByName", () => inner.FindAsync(p, n, t));
    public Task<IReadOnlyList<RemoteItem>> ChildrenAsync(string p, CancellationToken t) => Call("ListChildren", () => inner.ChildrenAsync(p, t));
    public Task<RemoteItem> FolderAsync(string p, string n, CancellationToken t) => Call("EnsureFolder", () => inner.FolderAsync(p, n, t));
    public Task<CloudChanges> ChangesAsync(string? cursor, CancellationToken t) => Call("Delta", async () =>
    {
        var changes = await inner.ChangesAsync(cursor, t);
        trace.Deltas.Enqueue(new { scenario = trace.Scenario, changes.Changed, changes.ResyncRequired,
            items = changes.Items?.Select(i => new { i.Id, i.ParentId, nameHash = i.Name is null ? null : Convert.ToHexString(SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(i.Name))), i.Folder, i.Deleted }).ToArray() });
        return changes;
    });
    public Task<RemoteItem> UploadAsync(string p, string n, Stream s, TransferSession? r, Action<TransferSession?> c, CancellationToken t, bool reconcileExisting = true) =>
        Call(n.EndsWith(".json", StringComparison.OrdinalIgnoreCase) ? "UploadMetadata" : "UploadBusiness", () => inner.UploadAsync(p, n, s, r, c, t, reconcileExisting));
    public Task DownloadAsync(RemoteItem i, Stream d, CancellationToken t) => Call(i.Name.EndsWith(".json", StringComparison.OrdinalIgnoreCase) ? "DownloadMetadata" : "DownloadBusiness", () => inner.DownloadAsync(i, d, t));
    public Task DeleteAsync(RemoteItem i, CancellationToken t) => Call("Delete", () => inner.DeleteAsync(i, t));
    public Task<RemoteItem> MoveAsync(RemoteItem i, string p, string n, DateTimeOffset? m, CancellationToken t) => Call("Move", () => inner.MoveAsync(i, p, n, m, t));
    public Task<RemoteItem> SetModifiedAsync(RemoteItem i, DateTimeOffset m, CancellationToken t) => Call("SetModified", () => inner.SetModifiedAsync(i, m, t));
    public Task<RemoteItem> CopyAsync(RemoteItem i, string p, string n, CancellationToken t) => Call("Copy", () => inner.CopyAsync(i, p, n, t));
}
sealed class ObservedHandler(Trace trace) : DelegatingHandler(new HttpClientHandler { AllowAutoRedirect = false })
{
    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
    {
        var start = trace.Now; var id = trace.Id(); var scenario = trace.Scenario; var parent = trace.Current.Value;
        var path = request.RequestUri!.AbsolutePath;
        var kind = request.RequestUri.Host != "graph.microsoft.com" ? "content-host" : path.EndsWith("/$batch") ? "read-only-batch" : path.EndsWith("/delta") ? "delta" : path.EndsWith("/children") ? "children" : path.EndsWith("/content") ? "content" : "item";
        var operation = request.Method.Method + " " + kind;
        var resource = Convert.ToHexString(SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(request.RequestUri.AbsoluteUri)));
        try
        {
            var response = await base.SendAsync(request, token);
            var done = 0;
            void Finish() { if (Interlocked.Exchange(ref done, 1) == 0) trace.Spans.Enqueue(new(id, scenario, "http", operation, "", parent, start, trace.Now, (int)response.StatusCode, resource)); }
            response.Content = new ObservedContent(response.Content, Finish);
            return response;
        }
        catch { trace.Spans.Enqueue(new(id, scenario, "http", operation, "", parent, start, trace.Now, 0, resource)); throw; }
    }
}
sealed class ObservedContent : HttpContent
{
    private readonly HttpContent inner; private readonly Action finish;
    public ObservedContent(HttpContent inner, Action finish) { this.inner = inner; this.finish = finish; foreach (var h in inner.Headers) Headers.TryAddWithoutValidation(h.Key, h.Value); }
    protected override bool TryComputeLength(out long length) { length = inner.Headers.ContentLength ?? 0; return inner.Headers.ContentLength.HasValue; }
    protected override Task<Stream> CreateContentReadStreamAsync() => CreateContentReadStreamAsync(default);
    protected override async Task<Stream> CreateContentReadStreamAsync(CancellationToken token) => new ObservedStream(await inner.ReadAsStreamAsync(token), finish);
    protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context) => SerializeToStreamAsync(stream, context, default);
    protected override async Task SerializeToStreamAsync(Stream stream, TransportContext? context, CancellationToken token) { try { await inner.CopyToAsync(stream, token); } finally { finish(); } }
    protected override void Dispose(bool disposing) { if (disposing) { try { inner.Dispose(); } finally { finish(); } } base.Dispose(disposing); }
}
sealed class ObservedStream(Stream inner, Action finish) : Stream
{
    public override bool CanRead => inner.CanRead; public override bool CanWrite => false; public override bool CanSeek => inner.CanSeek;
    public override long Length => inner.Length; public override long Position { get => inner.Position; set => inner.Position = value; }
    public override void Flush() => inner.Flush(); public override long Seek(long o, SeekOrigin origin) => inner.Seek(o, origin);
    public override void SetLength(long n) => throw new NotSupportedException(); public override void Write(byte[] b, int o, int n) => throw new NotSupportedException();
    public override int Read(byte[] b, int o, int n) { var count = inner.Read(b, o, n); if (count == 0) finish(); return count; }
    public override Task<int> ReadAsync(byte[] b, int o, int n, CancellationToken t) => ReadAsync(b.AsMemory(o, n), t).AsTask();
    public override async ValueTask<int> ReadAsync(Memory<byte> b, CancellationToken t = default) { var count = await inner.ReadAsync(b, t); if (count == 0) finish(); return count; }
    protected override void Dispose(bool disposing) { if (disposing) { try { inner.Dispose(); } finally { finish(); } } base.Dispose(disposing); }
}
