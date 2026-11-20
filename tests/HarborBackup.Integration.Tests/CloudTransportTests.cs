using System.Security.Cryptography;
using System.Text;
using HarborBackup.Core.Domain;
using HarborBackup.OneDrive.Transport;
using HarborBackup.Windows.FileSystem;
using HarborBackup.Windows.Integration;
using HarborBackup.Windows.Persistence;
using HarborBackup.Windows.Persistence.Configuration;

namespace HarborBackup.Windows.Tests;

[TestClass]
[DoNotParallelize]
public sealed class CloudTransportTests
{
    internal class FakeDrive : ICloudDrive
    {
        internal sealed record BodyTransfer(string Direction, string ItemId, string ParentId, string Name, long Bytes);
        public readonly System.Collections.Concurrent.ConcurrentQueue<BodyTransfer> BodyTransfers = new();
        public readonly System.Collections.Concurrent.ConcurrentDictionary<string,(RemoteItem Item,string Parent,byte[] Bytes)> Items = new();
        private readonly object folderGate = new();
        public Func<string, CancellationToken, Task>? BeforeUpload;
        public Func<RemoteItem, Task>? AfterUpload;
        public Func<string, CancellationToken, Task>? BeforeDownload;
        public bool FailCopies;
        public Action<RemoteItem, string, string>? BeforeMove;
        public int Finds;
        public readonly System.Collections.Concurrent.ConcurrentQueue<(string Parent,string Name)> Lookups = new();
        public readonly System.Collections.Concurrent.ConcurrentQueue<(string Name,bool Reconcile)> UploadIntents = new();
        public bool FailAfterCommit;
        public int Downloads;
        public int BusinessDownloads;
        public bool LoseNextMoveResponse;
        public int Enumerations;
        public List<string> EnumeratedParents = [];
        public int Copies;
        public bool BlockChildren;
        public TaskCompletionSource ChildrenStarted = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public FakeDrive() { Items["drive-root"]=(new("drive-root","OneDrive","drive-etag",0,true),"",[]); Items["root"]=(new("root","HarborBackup","root-etag",0,true, ParentId: "drive-root"),"drive-root",[]); }
        public void Dispose() { }
        public Task<RemoteItem> RootAsync(CancellationToken t)=>Task.FromResult(Items["drive-root"].Item);
        public Task<RemoteItem> GetAsync(string id,CancellationToken t)=>Task.FromResult(Items[id].Item);
        public Task<RemoteItem?> FindAsync(string p,string n,CancellationToken t)
        {
            Interlocked.Increment(ref Finds); Lookups.Enqueue((p,n));
            return Task.FromResult(Items.Values.Where(x=>x.Parent==p&&x.Item.Name.Equals(n,StringComparison.OrdinalIgnoreCase)).Select(x=>x.Item).SingleOrDefault());
        }
        public async Task<IReadOnlyList<RemoteItem>> ChildrenAsync(string p,CancellationToken t)
        {
            if (BlockChildren) { ChildrenStarted.TrySetResult(); await Task.Delay(Timeout.Infinite,t); }
            return await Enumerate(p);
        }
        private Task<IReadOnlyList<RemoteItem>> Enumerate(string p) { Enumerations++; EnumeratedParents.Add(p); return Task.FromResult<IReadOnlyList<RemoteItem>>(Items.Values.Where(x=>x.Parent==p).Select(x=>x.Item).ToArray()); }
        public Task<RemoteItem> FolderAsync(string p,string n,CancellationToken t)
        {
            lock(folderGate) return Task.FromResult(Items.Values.Where(x=>x.Parent==p&&x.Item.Name.Equals(n,StringComparison.OrdinalIgnoreCase))
                .Select(x=>x.Item).SingleOrDefault()??Add(p,n,[],true));
        }
        public RemoteItem Add(string p,string n,byte[] bytes,bool folder=false)
        {
            var id=Guid.NewGuid().ToString("N");using var stream=new MemoryStream(bytes);
            var item=new RemoteItem(id,n,Guid.NewGuid().ToString("N"),bytes.Length,folder,null,
                folder?null:QuickXor.ComputeAsync(stream,default).GetAwaiter().GetResult(), ParentId: p, ContentTag: Guid.NewGuid().ToString("N"));
            Assert.IsTrue(Items.TryAdd(id,(item,p,bytes)));return item;
        }
        public async Task<RemoteItem> UploadAsync(string p,string n,Stream s,TransferSession? session,Action<TransferSession?> checkpoint,CancellationToken t,bool reconcileExisting=true)
        {
            UploadIntents.Enqueue((n,reconcileExisting));
            if(reconcileExisting) { var existing=await FindAsync(p,n,t);if(existing is not null)return existing; }
            if(BeforeUpload is not null) await BeforeUpload(s is FileStream file ? Path.GetFileName(file.Name) : n,t);
            if (!Items.TryGetValue(p,out var parent) || !parent.Item.Folder) throw new IOException("Cloud parent is unavailable.");
            if (Items.Values.Any(v => v.Parent==p && v.Item.Name.Equals(n,StringComparison.OrdinalIgnoreCase))) throw new IOException("Concurrent create rejected.");
            using var memory=new MemoryStream();await s.CopyToAsync(memory,t);
            var result=Add(p,n,memory.ToArray());
            BodyTransfers.Enqueue(new("Upload", result.Id, p, n, memory.Length));
            if (AfterUpload is not null) await AfterUpload(result);
            if(FailAfterCommit&&n.EndsWith(".json")&&n!="repository.json"&&Items[p].Item.Name!="locations"&&n!="index.json")
            {FailAfterCommit=false;throw new IOException("Simulated lost commit response.");}
            return result;
        }
        public async Task DownloadAsync(RemoteItem i,Stream d,CancellationToken t)
        {
            if (BeforeDownload is not null) await BeforeDownload(i.Name,t);
            Interlocked.Increment(ref Downloads);
            var parent = Items[i.Id].Parent;
            while (Items.TryGetValue(parent, out var ancestor))
            {
                if (ancestor.Item.Name is "content" or "Documents") { Interlocked.Increment(ref BusinessDownloads); break; }
                parent = ancestor.Parent;
            }
            Assert.AreEqual(i.ETag,Items[i.Id].Item.ETag);await d.WriteAsync(Items[i.Id].Bytes,t);
            BodyTransfers.Enqueue(new("Download", i.Id, Items[i.Id].Parent, i.Name, Items[i.Id].Bytes.LongLength));
        }
        public Task DeleteAsync(RemoteItem item, CancellationToken token)
        {
            Assert.AreEqual(item.ETag, Items[item.Id].Item.ETag);
            void Remove(string id) { foreach(var child in Items.Values.Where(v=>v.Parent==id).Select(v=>v.Item.Id).ToArray()) Remove(child); Items.TryRemove(id,out _); }
            Remove(item.Id); return Task.CompletedTask;
        }
        public async Task<RemoteItem> MoveAsync(RemoteItem item,string parent,string name,DateTimeOffset? modified,CancellationToken token)
        {
            BeforeMove?.Invoke(item,parent,name);
            var actual=Items[item.Id];
            if(actual.Item.ETag!=item.ETag || await FindAsync(parent,name,token) is not null) throw new IOException("Concurrent move rejected.");
            var moved=actual.Item with { ParentId=parent, Name=name, ETag=Guid.NewGuid().ToString("N"), ModifiedUtc=modified??actual.Item.ModifiedUtc };
            Items[item.Id]=(moved,parent,actual.Bytes);if(LoseNextMoveResponse){LoseNextMoveResponse=false;throw new IOException("Lost move response.");}return moved;
        }
        public Task<RemoteItem> SetModifiedAsync(RemoteItem item,DateTimeOffset modified,CancellationToken token)
        {
            var actual=Items[item.Id]; Assert.AreEqual(item.ETag,actual.Item.ETag);
            var updated=actual.Item with { ModifiedUtc=modified, ETag=Guid.NewGuid().ToString("N") };
            Items[item.Id]=(updated,actual.Parent,actual.Bytes); return Task.FromResult(updated);
        }
        public async Task<RemoteItem> CopyAsync(RemoteItem s,string p,string n,CancellationToken t)
        {if(FailCopies) throw new IOException("Injected copy failure."); Copies++;return await FindAsync(p,n,t)??Add(p,n,Items[s.Id].Bytes.ToArray());}
    }

    private sealed class CapturedProgress : IProgress<BackupProgress>
    {
        public List<BackupProgress> Values = [];
        public void Report(BackupProgress value) => Values.Add(value);
    }
    [TestMethod]
    public async Task BuiltInFilesAreSkippedInBothDirectionsAndReturnAfterUnchecking()
    {
        using var f = new Fixture(); var a = f.Device("A");
        var patterns = ExclusionPresets.All.SelectMany(p => p.Files).ToArray();
        var project = a.P with { ExcludedFiles = patterns, Fingerprint = DeviceIdentity.Digest("presets-enabled"),
            ExclusionSources = new(ExclusionPresets.All.Select(p => p.Id).ToArray(), [], [], [], []) };
        var config = a.C with { Projects = [project] };
        var nested = Path.Combine(project.Source, "nested"); Directory.CreateDirectory(nested);
        var root = await f.Drive.FolderAsync("drive-root", "Documents", default);
        var cloudNested = await f.Drive.FolderAsync(root.Id, "nested", default);
        foreach (var pattern in patterns)
        {
            var name = pattern.Replace("*", "draft").ToUpperInvariant();
            File.WriteAllText(Path.Combine(nested, name), "local ignored");
            f.Drive.Add(cloudNested.Id, name, Encoding.UTF8.GetBytes("cloud ignored"));
        }
        foreach (var name in new[] { "方案.docx", "~方案.docx" }) File.WriteAllText(Path.Combine(project.Source, name), "keep");
        var runtime = new CloudBackupRuntime(config, () => f.Drive);
        var preview = await runtime.PreviewAsync(SyncDirection.Backup, "Documents", false, default);
        Assert.AreEqual(0, preview.ExitCode);
        Assert.AreEqual(8L, preview.Projects.Single().Metrics.HashBytes);
        Assert.AreEqual((long)patterns.Length, preview.Projects.Single().Metrics.ExcludedLocalEntries);
        Assert.AreEqual((long)patterns.Length, preview.Projects.Single().Metrics.ExcludedCloudEntries);
        Assert.AreEqual(project.ExclusionSources, preview.Projects.Single().ExclusionSources);
        Assert.IsFalse(preview.Projects.Single().Plans.Any(p => patterns.Any(rule => PathRules.Wildcard(p.Path.Split('/')[^1], rule))));
        var backup = await runtime.ExecuteAsync(SyncDirection.Backup, "Documents", false, false, null, default);
        Assert.AreEqual(0, backup.ExitCode);
        var pull = await runtime.PreviewAsync(SyncDirection.Pull, "Documents", false, default);
        Assert.IsFalse(pull.Projects.Single().Plans.Any(p => patterns.Any(rule => PathRules.Wildcard(p.Path.Split('/')[^1], rule))));
        Assert.AreEqual(0, (await runtime.ExecuteAsync(SyncDirection.Pull, "Documents", false, false, null, default)).ExitCode);
        Assert.AreEqual(0, f.Drive.BusinessDownloads);
        foreach (var pattern in patterns)
        {
            var name = pattern.Replace("*", "draft").ToUpperInvariant();
            Assert.AreEqual("local ignored", File.ReadAllText(Path.Combine(nested, name)));
            var item = await f.Drive.FindAsync(cloudNested.Id, name, default);
            Assert.IsNotNull(item); Assert.AreEqual("cloud ignored", Encoding.UTF8.GetString(f.Drive.Items[item.Id].Bytes));
        }
        var unselected = project with { ExcludedFiles = [], Fingerprint = DeviceIdentity.Digest("presets-disabled"), ExclusionSources = null };
        var restored = await new CloudBackupRuntime(config with { Projects = [unselected] }, () => f.Drive)
            .PreviewAsync(SyncDirection.Backup, "Documents", false, default);
        Assert.IsTrue(restored.Projects.Single().Plans.Any(p => p.Path.EndsWith("~$DRAFT.DOCX")));
    }

    [TestMethod]
    public async Task ExcludedDirectoryIsPrunedBeforeHashingOrCloudTraversal()
    {
        using var f = new Fixture(); var a = f.Device("A");
        const string excluded = "newdata_process_environment";
        var project = a.P with { ExcludedDirectories = [excluded] };
        var config = a.C with { Projects = [project] };
        Directory.CreateDirectory(Path.Combine(project.Source,excluded,"nested"));
        File.WriteAllText(Path.Combine(project.Source,excluded,"nested","ignored.txt"),"excluded payload");
        File.WriteAllText(Path.Combine(project.Source,"keep.txt"),"keep");
        var root = await f.Drive.FolderAsync("drive-root","Documents",default);
        var excludedCloud = await f.Drive.FolderAsync(root.Id,excluded,default);
        f.Drive.Add(excludedCloud.Id,"cloud.txt",Encoding.UTF8.GetBytes("cloud"));
        var runtime = new CloudBackupRuntime(config,() => f.Drive);
        var report = await runtime.PreviewAsync(SyncDirection.Backup,"Documents",false,default);
        Assert.AreEqual(0,report.ExitCode);
        CollectionAssert.AreEqual(new[] { "keep.txt" },report.Projects.Single().Plans.Select(p => p.Path).ToArray());
        Assert.AreEqual(4L,report.Projects.Single().Metrics.HashBytes);
        Assert.AreEqual(1L,report.Projects.Single().Metrics.ExcludedLocalEntries);
        Assert.AreEqual(1L,report.Projects.Single().Metrics.ExcludedCloudEntries);
        CollectionAssert.AreEqual(new[] { excluded },report.Projects.Single().ExcludedDirectories.ToArray());
        Assert.DoesNotContain(excludedCloud.Id,f.Drive.EnumeratedParents);
        var backup = await runtime.ExecuteAsync(SyncDirection.Backup,"Documents",false,false,null,default);
        Assert.AreEqual(0,backup.ExitCode);
        Assert.IsNull(await f.Drive.FindAsync(excludedCloud.Id,"nested",default));
        var pull = await runtime.PreviewAsync(SyncDirection.Pull,"Documents",false,default);
        CollectionAssert.AreEqual(new[] { "keep.txt" },pull.Projects.Single().Plans.Select(p => p.Path).ToArray());
        Assert.AreEqual(0,f.Drive.BusinessDownloads);
    }

    [TestMethod]
    public async Task PreviewReportsCloudWaitAndCancelsWithoutWritingBusinessFiles()
    {
        using var f = new Fixture(); var a = f.Device("A");
        await f.Drive.FolderAsync("drive-root","Documents",default);
        var items = f.Drive.Items.Count;
        f.Drive.BlockChildren = true;
        using var cancellation = new CancellationTokenSource();
        var progress = new CapturedProgress();
        var runtime = new CloudBackupRuntime(a.C,() => f.Drive);
        var pending = runtime.PreviewAsync(SyncDirection.Backup,"Documents",false,cancellation.Token,progress:progress);
        await f.Drive.ChildrenStarted.Task.WaitAsync(TimeSpan.FromSeconds(3));
        Assert.IsTrue(progress.Values.Any(p => p.Phase == BackupPhase.ReadingMetadata));
        cancellation.Cancel();
        await Assert.ThrowsAsync<OperationCanceledException>(async () => await pending.WaitAsync(TimeSpan.FromSeconds(3)));
        Assert.HasCount(items,f.Drive.Items);
        Assert.AreEqual(0,f.Drive.BusinessDownloads);
        Assert.IsEmpty(Directory.GetFiles(a.P.Source));
        Assert.IsFalse(a.S.Pending);
        f.Drive.BlockChildren = false;
        var retry = await runtime.PreviewAsync(SyncDirection.Backup,"Documents",false,default);
        Assert.AreEqual(0,retry.ExitCode);
    }

    [TestMethod]
    public async Task OriginalTreeAndChangingDirectoryEtagsNeedNoContentDownloads()
    {
        using var f=new Fixture();var a=f.Device("A");
        Directory.CreateDirectory(Path.Combine(a.P.Source,"docs","empty"));
        File.WriteAllText(Path.Combine(a.P.Source,"docs","说明 文件.txt"),"payload");
        await Apply(a.S);
        var root=(await f.Drive.FindAsync("drive-root","Documents",default))!;
        var docs=(await f.Drive.FindAsync(root.Id,"docs",default))!;
        var file=await f.Drive.FindAsync(docs.Id,"说明 文件.txt",default);
        Assert.IsNotNull(file);Assert.IsNotNull(await f.Drive.FindAsync(docs.Id,"empty",default));
        Assert.IsNull(await f.Drive.FindAsync("root","backups",default));
        var stored=f.Drive.Items[docs.Id];f.Drive.Items[docs.Id]=(stored.Item with {ETag="directory-etag-changed"},stored.Parent,stored.Bytes);
        var read=await a.S.ReadAsync(false,default);
        Assert.AreEqual(FileAction.Equal,a.S.Decide(read.Snapshot.Files.Single(v=>v.RelativePath.EndsWith(".txt")),read.Heads["docs/说明 文件.txt"],SyncDirection.Backup).Action);
        Assert.AreEqual(0,f.Drive.BusinessDownloads);
    }
    [TestMethod]
    public async Task ExistingUnmanagedTargetIsDeferredUntilExplicitPull()
    {
        using var f=new Fixture();var a=f.Device("A");
        var root=await f.Drive.FolderAsync("drive-root","Documents",default);
        var original=f.Drive.Add(root.Id,"a.txt",Encoding.UTF8.GetBytes("cloud"));
        File.WriteAllText(Path.Combine(a.P.Source,"a.txt"),"local");
        await Apply(a.S);
        Assert.AreEqual("cloud",Encoding.UTF8.GetString(f.Drive.Items[original.Id].Bytes));
        Assert.AreEqual(0,f.Drive.BusinessDownloads);
        var preview=await a.S.ReadAsync(false,default);
        Assert.AreEqual(FileAction.Deferred,a.S.Decide(preview.Snapshot.Files.Single(),preview.Heads["a.txt"],SyncDirection.Backup).Action);
        await Apply(a.S,SyncDirection.Pull);
        Assert.AreEqual("local",File.ReadAllText(Path.Combine(a.P.Source,"a.txt")));
        Assert.AreEqual(0,f.Drive.BusinessDownloads);
        var runtime = new CloudBackupRuntime(a.C, () => f.Drive);
        var baseline = await runtime.PreviewAsync(SyncDirection.Pull, "Documents", false, default);
        Assert.IsNotNull(baseline.PullSelection);
        Assert.HasCount(1, baseline.PullSelection.Files);
        var result = await runtime.ExecuteAsync(SyncDirection.Pull, "Documents", false, false, null, default, baseline.PullSelection);
        Assert.AreEqual(0, result.ExitCode);
        Assert.AreEqual("cloud",File.ReadAllText(Path.Combine(a.P.Source,"a.txt")));
        Assert.IsTrue(f.Drive.Items.Values.Any(v=>Encoding.UTF8.GetString(v.Bytes)=="local"));
        var after=await a.S.ReadAsync(false,default);Assert.IsTrue(after.Heads["a.txt"].Single().ContentHashKnown);
    }
    [TestMethod]
    public async Task ConfirmedPullOnlyWritesSelectedFilesAndCreatesTheirParents()
    {
        using var f = new Fixture(); var a = f.Device("A"); var b = f.Device("B");
        Directory.CreateDirectory(Path.Combine(a.P.Source,"folder"));
        File.WriteAllText(Path.Combine(a.P.Source,"folder","a.txt"),"selected");
        File.WriteAllText(Path.Combine(a.P.Source,"b.txt"),"not selected");
        await Apply(a.S);
        var runtime = new CloudBackupRuntime(b.C, () => f.Drive);
        var preview = await runtime.PreviewAsync(SyncDirection.Pull,"Documents",false,default);
        Assert.IsNotNull(preview.PullSelection);
        var selection = preview.PullSelection with { Files = preview.PullSelection.Files.Where(v => v.Path == "folder/a.txt").ToArray() };
        File.WriteAllText(Path.Combine(a.P.Source,"later.txt"),"added after preview");
        await Apply(a.S);
        var result = await runtime.ExecuteAsync(SyncDirection.Pull,"Documents",false,false,null,default,selection);
        Assert.AreEqual(0,result.ExitCode);
        Assert.AreEqual("selected",File.ReadAllText(Path.Combine(b.P.Source,"folder","a.txt")));
        Assert.IsFalse(File.Exists(Path.Combine(b.P.Source,"b.txt")));
        Assert.IsFalse(File.Exists(Path.Combine(b.P.Source,"later.txt")));
        Assert.AreEqual(1,f.Drive.BusinessDownloads);
    }

    [TestMethod]
    public async Task ConfirmedPullRejectsChangedCloudFileWithoutDownloading()
    {
        using var f = new Fixture(); var a = f.Device("A"); var b = f.Device("B");
        File.WriteAllText(Path.Combine(a.P.Source,"a.txt"),"first"); await Apply(a.S);
        var runtime = new CloudBackupRuntime(b.C, () => f.Drive);
        var preview = await runtime.PreviewAsync(SyncDirection.Pull,"Documents",false,default);
        File.WriteAllText(Path.Combine(a.P.Source,"a.txt"),"second"); await Apply(a.S);
        var result = await runtime.ExecuteAsync(SyncDirection.Pull,"Documents",false,false,null,default,preview.PullSelection);
        Assert.AreEqual(2,result.ExitCode);
        Assert.AreEqual("PullPreviewChanged",result.Projects.Single().Files.Single().Error);
        Assert.IsFalse(File.Exists(Path.Combine(b.P.Source,"a.txt")));
        Assert.AreEqual(0,f.Drive.BusinessDownloads);
    }

    [TestMethod]
    public async Task ConfirmedPullRejectsLocalEditEvenIfSizeAndTimestampAreUnchanged()
    {
        using var f = new Fixture(); var a = f.Device("A"); var b = f.Device("B");
        var source = Path.Combine(a.P.Source,"a.txt"); var local = Path.Combine(b.P.Source,"a.txt");
        File.WriteAllText(source,"base"); await Apply(a.S); await Apply(b.S,SyncDirection.Pull);
        File.WriteAllText(source,"cloud"); await Apply(a.S);
        var runtime = new CloudBackupRuntime(b.C, () => f.Drive);
        var preview = await runtime.PreviewAsync(SyncDirection.Pull,"Documents",false,default);
        var timestamp = File.GetLastWriteTimeUtc(local);
        File.WriteAllText(local,"edit"); File.SetLastWriteTimeUtc(local,timestamp);
        var downloads = f.Drive.BusinessDownloads;
        var result = await runtime.ExecuteAsync(SyncDirection.Pull,"Documents",false,false,null,default,preview.PullSelection);
        Assert.AreEqual(2,result.ExitCode);
        Assert.AreEqual("edit",File.ReadAllText(local));
        Assert.AreEqual(downloads,f.Drive.BusinessDownloads);
    }

    [TestMethod]
    public async Task PullSelectionIsBoundToLocalProjectMapping()
    {
        using var f = new Fixture(); var a = f.Device("A"); var b = f.Device("B"); var c = f.Device("C");
        File.WriteAllText(Path.Combine(a.P.Source,"a.txt"),"cloud"); await Apply(a.S);
        var preview = await new CloudBackupRuntime(b.C, () => f.Drive).PreviewAsync(SyncDirection.Pull,"Documents",false,default);
        var result = await new CloudBackupRuntime(c.C, () => f.Drive).ExecuteAsync(SyncDirection.Pull,"Documents",false,false,null,default,preview.PullSelection);
        Assert.AreEqual(2,result.ExitCode);
        Assert.IsFalse(File.Exists(Path.Combine(c.P.Source,"a.txt")));
        Assert.AreEqual(0,f.Drive.BusinessDownloads);
    }

    [TestMethod]
    public async Task PullPreviewOffersExplicitReplacementButUnconfirmedPullKeepsLocalChanges()
    {
        using var f = new Fixture(); var a = f.Device("A"); var b = f.Device("B");
        File.WriteAllText(Path.Combine(a.P.Source,"a.txt"),"base"); await Apply(a.S); await Apply(b.S,SyncDirection.Pull);
        var runtime = new CloudBackupRuntime(b.C, () => f.Drive);
        var equal = await runtime.PreviewAsync(SyncDirection.Pull,"Documents",false,default);
        Assert.IsNotNull(equal.PullSelection); Assert.HasCount(0,equal.PullSelection.Files);
        File.WriteAllText(Path.Combine(b.P.Source,"a.txt"),"local change");
        var changed = await runtime.PreviewAsync(SyncDirection.Pull,"Documents",false,default);
        Assert.AreEqual(FileAction.Copy,changed.Projects.Single().Plans.Single().Decision.Action);
        Assert.IsNotNull(changed.PullSelection); Assert.HasCount(1,changed.PullSelection.Files);
        var downloads = f.Drive.BusinessDownloads;
        await Apply(b.S,SyncDirection.Pull);
        Assert.AreEqual(downloads,f.Drive.BusinessDownloads);
        Assert.AreEqual("local change",File.ReadAllText(Path.Combine(b.P.Source,"a.txt")));
    }

    [TestMethod]
    public async Task OnlyPreviousHistoryRemainsAfterThreeOverwritesWithoutDownloads()
    {
        using var f=new Fixture();var a=f.Device("A");
        foreach(var value in new[]{"first","second","third","fourth"})
        {File.WriteAllText(Path.Combine(a.P.Source,"a.txt"),value);await Apply(a.S);}
        var root=(await f.Drive.FindAsync("drive-root","Documents",default))!;
        var live=(await f.Drive.FindAsync(root.Id,"a.txt",default))!;
        Assert.AreEqual("fourth",Encoding.UTF8.GetString(f.Drive.Items[live.Id].Bytes));
        var repository=new CloudRepository(f.Drive,f.Account,DataPaths.FromState(a.C.Preview.StateDirectory));
        var archive=(await repository.FolderAsync("archives/Documents",false,default))!;
        var versions=await f.Drive.ChildrenAsync(archive.Id,default);Assert.HasCount(1,versions);
        var old=(await f.Drive.FindAsync(archive.Id,"a.txt",default))!;
        Assert.AreEqual("third",Encoding.UTF8.GetString(f.Drive.Items[old.Id].Bytes));
        Assert.AreEqual(0,f.Drive.BusinessDownloads);
    }
    [TestMethod]
    public async Task LostUploadResponseRecoversAtOriginalPath()
    {
        using var f=new Fixture();var a=f.Device("A");
        File.WriteAllText(Path.Combine(a.P.Source,"a.txt"),"content");
        f.Drive.AfterUpload=item=>item.Name=="a.txt"?throw new IOException("Lost upload response."):Task.CompletedTask;
        await Assert.ThrowsExactlyAsync<IOException>(()=>Apply(a.S));Assert.IsTrue(a.S.Pending);
        f.Drive.AfterUpload=null;
        var recovery=new CloudBackupSession(a.P,a.C,f.Account,f.Drive);
        await recovery.RecoverAsync(default);Assert.IsFalse(recovery.Pending);
        var root=(await f.Drive.FindAsync("drive-root","Documents",default))!;
        Assert.HasCount(1,await f.Drive.ChildrenAsync(root.Id,default));
        Assert.AreEqual(0,f.Drive.BusinessDownloads);
    }
    [TestMethod]
    public async Task LostArchiveMoveResponseRetainsOldAndFinishesNewFile()
    {
        using var f=new Fixture();var a=f.Device("A");
        File.WriteAllText(Path.Combine(a.P.Source,"a.txt"),"old");await Apply(a.S);
        File.WriteAllText(Path.Combine(a.P.Source,"a.txt"),"new");
        f.Drive.LoseNextMoveResponse=true;
        await Assert.ThrowsExactlyAsync<IOException>(()=>Apply(a.S));
        await new CloudBackupSession(a.P,a.C,f.Account,f.Drive).RecoverAsync(default);
        await Apply(a.S); // Rollback restored the old version; the next backup uploads the new one.
        var root=(await f.Drive.FindAsync("drive-root","Documents",default))!;
        var current=(await f.Drive.FindAsync(root.Id,"a.txt",default))!;
        Assert.AreEqual("new",Encoding.UTF8.GetString(f.Drive.Items[current.Id].Bytes));
        Assert.IsTrue(f.Drive.Items.Values.Any(i=>Encoding.UTF8.GetString(i.Bytes)=="old"));
        Assert.IsFalse(a.S.Pending);
    }
    [TestMethod]
    public async Task ManagementDirectoryCannotBeUsedAsProjectTarget()
    {
        using var f=new Fixture();var a=f.Device("A");
        var p=a.P with {Destination="HarborBackup/metadata"};
        await Assert.ThrowsExactlyAsync<IOException>(()=>new CloudBackupSession(p,a.C,f.Account,f.Drive).ReadAsync(false,default));
    }
    [TestMethod]
    public async Task MissingCloudFileDoesNotDeleteLocalAndMissingLocalDoesNotDeleteCloud()
    {
        using var f=new Fixture();var a=f.Device("A");
        var path=Path.Combine(a.P.Source,"a.txt");File.WriteAllText(path,"keep");await Apply(a.S);
        File.Delete(path);await Apply(a.S);
        var root=(await f.Drive.FindAsync("drive-root","Documents",default))!;
        Assert.IsNotNull(await f.Drive.FindAsync(root.Id,"a.txt",default));
        Assert.IsFalse(File.Exists(path));
    }


    [TestMethod]
    public async Task SharedMetadataQueryDoesNotReadSourceOrDownloadBusinessFiles()
    {
        using var f=new Fixture();var a=f.Device("A");
        File.WriteAllText(Path.Combine(a.P.Source,"a.txt"),"payload");await Apply(a.S);
        Directory.Delete(a.P.Source,true);
        var records=await new CloudBackupRuntime(a.C,()=>f.Drive).RevisionsAsync(null,default);
        Assert.HasCount(1,records);Assert.AreEqual(0,f.Drive.BusinessDownloads);
    }
    [TestMethod]
    public async Task RecreatedDestinationIsRejectedBySharedFolderIdentity()
    {
        using var f=new Fixture();var a=f.Device("A");
        File.WriteAllText(Path.Combine(a.P.Source,"a.txt"),"payload");await Apply(a.S);
        var original=(await f.Drive.FindAsync("drive-root","Documents",default))!;
        await f.Drive.DeleteAsync(original,default);
        await f.Drive.FolderAsync("drive-root","Documents",default);
        await Assert.ThrowsExactlyAsync<IOException>(()=>new CloudBackupSession(a.P,a.C,f.Account,f.Drive).ReadAsync(false,default));
        Assert.AreEqual("payload",File.ReadAllText(Path.Combine(a.P.Source,"a.txt")));
    }
    [TestMethod]
    public async Task RecoveryRejectsChangedTargetParent()
    {
        using var f=new Fixture();var a=f.Device("A");
        File.WriteAllText(Path.Combine(a.P.Source,"a.txt"),"payload");
        f.Drive.AfterUpload=item=>item.Name=="a.txt"?throw new IOException("Lost upload response."):Task.CompletedTask;
        await Assert.ThrowsExactlyAsync<IOException>(()=>Apply(a.S));
        var journal=Directory.GetFiles(a.C.Preview.StateDirectory,"*.json",SearchOption.AllDirectories)
            .Single(p=>Path.GetDirectoryName(p)!.EndsWith("cloud-transactions"));
        var json=System.Text.Json.Nodes.JsonNode.Parse(File.ReadAllText(journal))!;
        json["parent"]="root";File.WriteAllText(journal,json.ToJsonString());
        var before=f.Drive.Items.Count;
        await Assert.ThrowsExactlyAsync<IOException>(()=>new CloudBackupSession(a.P,a.C,f.Account,f.Drive).RecoverAsync(default));
        Assert.HasCount(before,f.Drive.Items);Assert.IsTrue(File.Exists(journal));
    }


    [TestMethod]
    public async Task JoinCatalogSkipsLegacyLayoutAndKeepsDirectFolderIdentity()
    {
        using var f=new Fixture();var a=f.Device("A");
        File.WriteAllText(Path.Combine(a.P.Source,"a.txt"),"payload");await Apply(a.S);
        var data=DataPaths.FromState(a.C.Preview.StateDirectory);
        var repository=new CloudRepository(f.Drive,f.Account,data);
        var legacy=(await repository.FolderAsync("metadata/projects/"+DeviceIdentity.Digest("cloud-v1/DOCUMENTS"),true,default))!;
        await repository.JsonAsync(legacy.Id,"project.json",new{schemaVersion=1,destination="Documents"},default);
        var choices=await CloudProjectCatalog.ReadAsync(data,f.Account,f.Drive,default);
        Assert.HasCount(1,choices);Assert.AreEqual("Documents",choices[0].Destination);
        Assert.AreEqual((await f.Drive.FindAsync("drive-root","Documents",default))!.Id,choices[0].FolderId);
    }

    private sealed class DeltaDrive : FakeDrive, ICloudChanges
    {
        public Task<CloudChanges> ChangesAsync(string? cursor,CancellationToken token)
        {
            var current=DeviceIdentity.Digest(string.Join("/",Items.Values.OrderBy(v=>v.Item.Id).Select(v=>v.Item.Id+v.Item.ETag+v.Parent)));
            return Task.FromResult(new CloudChanges(current,current!=cursor));
        }
    }
    internal sealed class Fixture : IDisposable
    {
        public string Root=Path.Combine(Path.GetTempPath(),"HarborCloudTests-"+Guid.NewGuid().ToString("N"));
        public FakeDrive Drive=new();
        public CloudAccountSettings Account=new(Enabled:true,ClientId:Guid.NewGuid().ToString(),AccountId:"test-account",DriveId:"test-drive",
            RootId:"root",RepositoryId:Guid.NewGuid().ToString("N"));
        public Fixture(bool delta=false){if(delta)Drive=new DeltaDrive();Directory.CreateDirectory(Root);Drive.Add("root","repository.json",
            System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(new CloudRepository.RepositoryIdentity(2,Account.RepositoryId!,new()),AtomicJson.Options));}
        public (ProjectDefinition P,ConfigurationSnapshot C,CloudBackupSession S) Device(string name)
        {
            var source=Path.Combine(Root,name,"source");Directory.CreateDirectory(source);
            var data=new DataPaths(Path.Combine(Root,name,"data"));
            var p=new ProjectDefinition(source,"Documents","OneDrive / HarborBackup / Documents",[],[],DeviceIdentity.Digest(Account.RepositoryId!),DeviceIdentity.Digest("mapping"));
            var c=new ConfigurationSnapshot([p],new(data.State,DeviceIdentity.Digest(name),new()),10,2,data.Root)
            {CloudAccount=Account,LogDirectory=data.Logs,HistoryDirectory=data.History,ArchiveDirectory=data.Archives,ConflictDirectory=data.Conflicts};
            return(p,c,new(p,c,Account,Drive));
        }
        public void Dispose(){Directory.Delete(Root,true);}
    }
    internal static async Task Apply(CloudBackupSession session,SyncDirection direction=SyncDirection.Backup)
    {
        var read=await session.ReadAsync(false,default);
        foreach(var f in read.Snapshot.Files)
        {var heads=read.Heads.GetValueOrDefault(f.RelativePath)??[];await session.ApplyAsync(f,heads,direction,session.Decide(f,heads,direction),default);}
    }
    [TestMethod]
    public async Task BackupPullAndCloudArchiveDoNotNeedSyncDirectory()
    {
        using var f=new Fixture();var a=f.Device("A");var b=f.Device("B");
        File.WriteAllText(Path.Combine(a.P.Source,"a.txt"),"first");
        await Apply(a.S);await Apply(b.S,SyncDirection.Pull);
        Assert.AreEqual("first",File.ReadAllText(Path.Combine(b.P.Source,"a.txt")));
        File.WriteAllText(Path.Combine(a.P.Source,"a.txt"),"second");
        await Apply(a.S);Assert.IsNotNull(await new CloudRepository(f.Drive,f.Account,DataPaths.FromState(a.C.Preview.StateDirectory)).FolderAsync("archives/Documents",false,default));
        var read=await b.S.ReadAsync(false,default);var entry=read.Snapshot.Files.Single();
        Assert.AreEqual(FileAction.NeedsPull,b.S.Decide(entry,read.Heads["a.txt"],SyncDirection.Backup).Action);
        await Apply(b.S,SyncDirection.Pull);Assert.AreEqual("second",File.ReadAllText(Path.Combine(b.P.Source,"a.txt")));
        Assert.IsFalse(a.S.Pending);Assert.IsFalse(b.S.Pending);
    }
    [TestMethod]
    public async Task SimultaneousDevicesPreserveLosingUploadWithoutOverwriting()
    {
        using var f=new Fixture();var a=f.Device("A");var b=f.Device("B");
        File.WriteAllText(Path.Combine(a.P.Source,"a.txt"),"base");await Apply(a.S);await Apply(b.S,SyncDirection.Pull);
        File.WriteAllText(Path.Combine(a.P.Source,"a.txt"),"A edit");
        File.WriteAllText(Path.Combine(b.P.Source,"a.txt"),"B edit");
        var ar=await a.S.ReadAsync(false,default);var br=await b.S.ReadAsync(false,default);
        foreach(var (session,read) in new[]{(a.S,ar),(b.S,br)})
        {var entry=read.Snapshot.Files.Single();var heads=read.Heads["a.txt"]; if(ReferenceEquals(session,b.S)) await Assert.ThrowsExactlyAsync<DeferredOperationException>(()=>session.ApplyAsync(entry,heads,SyncDirection.Backup,session.Decide(entry,heads,SyncDirection.Backup),default)); else await session.ApplyAsync(entry,heads,SyncDirection.Backup,session.Decide(entry,heads,SyncDirection.Backup),default);}
        var result=await b.S.ReadAsync(false,default);
        Assert.HasCount(1,result.Heads["a.txt"]);
        Assert.IsFalse(b.S.Pending);
        Assert.AreEqual(FileAction.ConflictUnresolved,b.S.Decide(result.Snapshot.Files.Single(),result.Heads["a.txt"],SyncDirection.Backup).Action);
        Assert.IsTrue(f.Drive.Items.Values.Any(i=>Encoding.UTF8.GetString(i.Bytes)=="A edit"));
        Assert.IsTrue(f.Drive.Items.Values.Any(i=>Encoding.UTF8.GetString(i.Bytes)=="B edit"));
    }
    [TestMethod]
    public async Task LostCommitResponseRecoversWithoutDuplicateRevision()
    {
        using var f=new Fixture();var a=f.Device("A");File.WriteAllText(Path.Combine(a.P.Source,"a.txt"),"content");
        f.Drive.FailAfterCommit=true;
        await Assert.ThrowsExactlyAsync<IOException>(()=>Apply(a.S));
        Assert.IsTrue(a.S.Pending);
        var recovered=new CloudBackupSession(a.P,a.C,f.Account,f.Drive);await recovered.RecoverAsync(default);
        Assert.IsFalse(recovered.Pending);
        Assert.HasCount(1,(await recovered.ReadAsync(false,default)).Heads["a.txt"]);
    }
    [TestMethod]
    public async Task CloudContentTamperingStopsReadAndPreservesLocalSource()
    {
        using var f=new Fixture();var a=f.Device("A");File.WriteAllText(Path.Combine(a.P.Source,"a.txt"),"original");await Apply(a.S);
        var read=await a.S.ReadAsync(false,default);var id=read.Heads["a.txt"][0].ItemId;
        var stored=f.Drive.Items[id];f.Drive.Items[id]=(stored.Item with {ETag="changed",ContentTag="changed-content"},stored.Parent,Encoding.UTF8.GetBytes("tampered"));
        var changed=await a.S.ReadAsync(false,default);
        Assert.AreEqual(FileAction.Deferred,a.S.Decide(changed.Snapshot.Files.Single(),changed.Heads["a.txt"],SyncDirection.Backup).Action);
        Assert.AreEqual("original",File.ReadAllText(Path.Combine(a.P.Source,"a.txt")));
    }
    [TestMethod]
    public async Task ConflictingFirstContactProtectsBothWithoutChoosingNewer()
    {
        using var f=new Fixture();var a=f.Device("A");var b=f.Device("B");
        File.WriteAllText(Path.Combine(a.P.Source,"a.txt"),"A");await Apply(a.S);
        File.WriteAllText(Path.Combine(b.P.Source,"a.txt"),"B");await Apply(b.S);
        Assert.AreEqual("B",File.ReadAllText(Path.Combine(b.P.Source,"a.txt")));
        var read=await b.S.ReadAsync(false,default);Assert.HasCount(1,read.Heads["a.txt"]);
        Assert.AreEqual(FileAction.ConflictUnresolved,b.S.Decide(read.Snapshot.Files.Single(),read.Heads["a.txt"],SyncDirection.Backup).Action);
        Assert.IsGreaterThan(0,f.Drive.Copies);
    }

    [TestMethod]
    public async Task ExplicitMergeKeepsBothCopiesAndConvergesDevices()
    {
        using var f=new Fixture();var a=f.Device("A");var b=f.Device("B");
        File.WriteAllText(Path.Combine(a.P.Source,"a.txt"),"base");await Apply(a.S);await Apply(b.S,SyncDirection.Pull);
        File.WriteAllText(Path.Combine(a.P.Source,"a.txt"),"A");File.WriteAllText(Path.Combine(b.P.Source,"a.txt"),"B");
        var ar=await a.S.ReadAsync(false,default);var br=await b.S.ReadAsync(false,default);
        foreach(var(session,read) in new[]{(a.S,ar),(b.S,br)})
        {var file=read.Snapshot.Files.Single(); if(ReferenceEquals(session,b.S)) await Assert.ThrowsExactlyAsync<DeferredOperationException>(()=>session.ApplyAsync(file,read.Heads["a.txt"],SyncDirection.Backup,session.Decide(file,read.Heads["a.txt"],SyncDirection.Backup),default)); else await session.ApplyAsync(file,read.Heads["a.txt"],SyncDirection.Backup,session.Decide(file,read.Heads["a.txt"],SyncDirection.Backup),default);}
        var inspection=await CloudConflictResolution.InspectAsync(a.C,"Documents","a.txt",f.Drive,default);
        await CloudConflictResolution.ResolveAsync(a.C,inspection,"local",f.Drive,default);
        var readB=await b.S.ReadAsync(false,default);Assert.HasCount(1,readB.Heads["a.txt"]);
        // B has a divergent local version and must explicitly select the merged cloud version.
        var chooseB=await CloudConflictResolution.InspectAsync(b.C,"Documents","a.txt",f.Drive,default);
        await CloudConflictResolution.ResolveAsync(b.C,chooseB,chooseB.Versions.Single().Id,f.Drive,default);
        Assert.AreEqual("A",File.ReadAllText(Path.Combine(b.P.Source,"a.txt")));
        Assert.IsTrue(f.Drive.Items.Values.Any(i=>Encoding.UTF8.GetString(i.Bytes)=="B"));
    }
    [TestMethod]
    public async Task StaleConflictConfirmationRejectsNewLocalContent()
    {
        using var f=new Fixture();var a=f.Device("A");var b=f.Device("B");
        File.WriteAllText(Path.Combine(a.P.Source,"a.txt"),"A");await Apply(a.S);
        File.WriteAllText(Path.Combine(b.P.Source,"a.txt"),"B");
        var inspection=await CloudConflictResolution.InspectAsync(b.C,"Documents","a.txt",f.Drive,default);
        File.WriteAllText(Path.Combine(b.P.Source,"a.txt"),"later");
        await Assert.ThrowsExactlyAsync<IOException>(()=>CloudConflictResolution.ResolveAsync(b.C,inspection,"local",f.Drive,default));
        Assert.AreEqual("later",File.ReadAllText(Path.Combine(b.P.Source,"a.txt")));
    }
    [TestMethod]
    public async Task RetentionSwitchOnlyRemovesVerifiedOwnedCopies()
    {
        using var f=new Fixture();var a=f.Device("A");
        File.WriteAllText(Path.Combine(a.P.Source,"index.json"),"first");await Apply(a.S);
        File.WriteAllText(Path.Combine(a.P.Source,"index.json"),"second");await Apply(a.S);
        var data=DataPaths.FromState(a.C.Preview.StateDirectory);
        await CloudProtectionMaintenance.RunAsync(data,f.Account with {ArchiveRetention=LocalRetention.KeepLocal},f.Drive,default);
        var copies=Directory.GetFiles(data.Archives,"index.json",SearchOption.AllDirectories);Assert.HasCount(1,copies);
        await CloudProtectionMaintenance.RunAsync(data,f.Account,f.Drive,default);
        Assert.IsFalse(File.Exists(copies[0]));Assert.AreEqual("second",File.ReadAllText(Path.Combine(a.P.Source,"index.json")));
    }
    [TestMethod]
    public void AccountConfigurationDoesNotRequireInstalledOneDriveClient()
    {
        using var f=new Fixture();var a=f.Device("A");var data=DataPaths.FromState(a.C.Preview.StateDirectory);
        var store=new CloudAccountStore(data);store.Save(f.Account,store.Read());
        var draft=new ConfigurationEditor(new(data.Projects,data.BackupSettings)).Load();
        draft.Projects["folders"]!.AsArray().Add(new System.Text.Json.Nodes.JsonObject
        {["id"]=DeviceIdentity.Digest(Guid.NewGuid().ToString("N")),["source"]=a.P.Source,["destination"]="Documents"});
        var validated=new ConfigurationEditor(new(data.Projects,data.BackupSettings)).Validate(draft);
        Assert.IsNotNull(validated.CloudAccount);Assert.AreEqual("OneDrive/Documents",validated.Projects.Single().Target);
    }

    [TestMethod]
    public void UnboundAccountCannotFallBackToExistingSyncDirectory()
    {
        using var ws=new TestWorkspace();
        var paths=new RuntimePaths(Path.Combine(ws.Config,"projects.json"),Path.Combine(ws.Config,"backup-settings.json"));
        var store=new CloudAccountStore(paths.Data);store.Save(new(ClientId:Guid.NewGuid().ToString(),AccountId:"not-bound"),store.Read());
        Assert.ThrowsExactly<InvalidOperationException>(()=>new BackupRuntime(paths).Load());
    }
    [TestMethod]
    public async Task ChangedSourceIntentIsRetainedBeforeUploadingCurrentBytes()
    {
        using var f=new Fixture();var a=f.Device("A");
        var path=Path.Combine(a.P.Source,"a.txt");File.WriteAllText(path,"old");
        var read=await a.S.ReadAsync(false,default);File.WriteAllText(path,"new");
        var entry=read.Snapshot.Files.Single();
        await Assert.ThrowsExactlyAsync<IOException>(()=>a.S.ApplyAsync(entry,[],SyncDirection.Backup,a.S.Decide(entry,[],SyncDirection.Backup),default));
        Assert.IsTrue(a.S.Pending);Assert.AreEqual("new",File.ReadAllText(path));
        var journal=Directory.GetFiles(f.Root,"*.json",SearchOption.AllDirectories)
            .Single(p=>Path.GetDirectoryName(p)!.EndsWith("cloud-transactions",StringComparison.Ordinal));
        var original=File.ReadAllBytes(journal);
        var writes=f.Drive.UploadIntents.Count;
        await a.S.RecoverAsync(default);
        Assert.IsFalse(a.S.Pending);Assert.HasCount(writes,f.Drive.UploadIntents);
        var retained=Directory.GetFiles(f.Root,"*.json",SearchOption.AllDirectories).Single(p=>p.Contains("retained-transactions"));
        CollectionAssert.AreEqual(original,File.ReadAllBytes(retained));
        Assert.AreEqual("new",File.ReadAllText(path));
        await Apply(a.S);
        var root=await f.Drive.FindAsync("drive-root","Documents",default);
        var current=await f.Drive.FindAsync(root!.Id,"a.txt",default);
        Assert.AreEqual("new",Encoding.UTF8.GetString(f.Drive.Items[current!.Id].Bytes));
    }

    [TestMethod]
    public async Task BatchInventoryAndHashCacheAvoidRepeatedContentReads()
    {
        using var f=new Fixture(true);var a=f.Device("A");
        File.WriteAllText(Path.Combine(a.P.Source,"a.txt"),"first");await Apply(a.S);
        await a.S.ReadAsync(false,default);var before=f.Drive.Enumerations;
        var cached=await a.S.ReadAsync(false,default);Assert.AreEqual(before + 1,f.Drive.Enumerations, "Only the project location index is checked; file listings stay cached.");Assert.AreEqual(1L,cached.Snapshot.Metrics.CloudInventoryCacheHits);
        Assert.AreEqual(0L,cached.Snapshot.Metrics.HashBytes);Assert.AreEqual(1L,cached.Snapshot.Metrics.CachedFiles);
        File.WriteAllText(Path.Combine(a.P.Source,"a.txt"),"second");await Apply(a.S);
        var read=await a.S.ReadAsync(false,default);
        Assert.AreEqual(Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes("second"))),read.Heads["a.txt"].Single().Version.Sha256);
        Assert.IsGreaterThan(before,f.Drive.Enumerations);
    }

    [TestMethod]
    public async Task CustomRepositoryFoldersAreUsedConsistently()
    {
        using var f=new Fixture();
        var layout=new CloudFolders("protection/history","protection/conflicts","coordination");
        f.Account=f.Account with {Folders=layout};
        var marker=f.Drive.Items.Values.Single(v=>v.Item.Name=="repository.json");
        var bytes=System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(new CloudRepository.RepositoryIdentity(2,f.Account.RepositoryId!,layout),AtomicJson.Options);
        f.Drive.Items[marker.Item.Id]=(marker.Item with {Size=bytes.Length,ETag="custom-layout"},marker.Parent,bytes);
        var a=f.Device("A");File.WriteAllText(Path.Combine(a.P.Source,"a.txt"),"first");await Apply(a.S);
        File.WriteAllText(Path.Combine(a.P.Source,"a.txt"),"second");await Apply(a.S);
        Assert.IsNotNull(await f.Drive.FindAsync("root","coordination",default));
        var protection=await f.Drive.FindAsync("root","protection",default);
        Assert.IsNotNull(protection);Assert.IsNotNull(await f.Drive.FindAsync(protection.Id,"history",default));
        Assert.IsNull(await f.Drive.FindAsync("root","archives",default));
        Assert.ThrowsExactly<FormatException>(()=>new CloudFolders("same","same/sub","metadata").Validate());
    }
}
