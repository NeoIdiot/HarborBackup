using System.Text;
using System.Text.Json;
using HarborBackup.Core.Domain;
using HarborBackup.OneDrive.Transport;
using HarborBackup.Windows.FileSystem;
using HarborBackup.Windows.Integration;
using HarborBackup.Windows.Persistence;
using HarborBackup.Windows.Persistence.Configuration;

if (args.Length != 3) throw new ArgumentException("Use <absolute isolated root> <seed|interrupt|recover> <phase>.");
var root = Path.GetFullPath(args[0]);
SafePath.Validate(root);
if (!File.Exists(Path.Combine(root, "synthetic-test.marker"))) throw new IOException("An explicitly created synthetic-test marker is required.");
var mode = args[1]; var phase = args[2];
using var cloud = new DiskDrive(root, mode == "interrupt" ? phase : null);
var source = Path.Combine(root, "source"); Directory.CreateDirectory(source);
var data = new DataPaths(Path.Combine(root, "data"));
var account = new CloudAccountSettings(Enabled: true, ClientId: "synthetic-client", AccountId: "synthetic-account", DriveId: "synthetic-drive",
    RootId: "management", RepositoryId: "11111111111111111111111111111111");
var project = new ProjectDefinition(source, "Documents", "Documents", [], [], DeviceIdentity.Digest("synthetic-project"), DeviceIdentity.Digest("mapping"));
var config = new ConfigurationSnapshot([project], new(data.State, DeviceIdentity.Digest("synthetic-device"), new()), 10, 2, data.Root)
    { CloudAccount = account, LogDirectory = data.Logs, HistoryDirectory = data.History, ArchiveDirectory = data.Archives, ConflictDirectory = data.Conflicts };
var runtime = new CloudBackupRuntime(config, () => cloud, fileConcurrency: 1);
if (mode == "seed")
{
    cloud.Initialize(account.RepositoryId!);
    File.WriteAllText(Path.Combine(source, "document.txt"), "before");
    var seed = await runtime.ExecuteAsync(SyncDirection.Backup, null, false, false, null, default);
    if (seed.ExitCode != 0) throw new IOException(JsonSerializer.Serialize(seed, AtomicJson.Options));
    File.WriteAllText(Path.Combine(source, "document.txt"), "after-crash");
}
else if (mode is "interrupt" or "recover")
{
    var result = await runtime.ExecuteAsync(SyncDirection.Backup, null, false, false, null, default);
    if (mode == "interrupt") throw new IOException("Expected interruption boundary was not reached: " + phase);
    if (result.ExitCode != 0) throw new IOException(JsonSerializer.Serialize(result, AtomicJson.Options));
    var target = await cloud.FindAsync((await cloud.FindAsync("drive-root", "Documents", default))!.Id, "document.txt", default);
    if (target is null || Encoding.UTF8.GetString(cloud.Entries[target.Id].Bytes) != "after-crash") throw new IOException("Recovered content differs.");
    var preview = await runtime.PreviewAsync(SyncDirection.Backup, null, true, default);
    if (preview.Verification.Match != 1 || preview.Verification.Unverifiable != 0) throw new IOException("Recovered baseline is not verified.");
    if (new CloudBackupSession(project, config, account, cloud).Pending) throw new IOException("Recovery left an active transaction.");
    var repository = new CloudRepository(cloud, account, data);
    var protectionRoots = new[] { await repository.FolderAsync("archives", false, default), await repository.FolderAsync("conflicts", false, default) }
        .Where(i => i is not null).Select(i => i!.Id).ToHashSet(StringComparer.Ordinal);
    bool IsProtected(DiskDrive.Entry entry)
    {
        var parent = entry.Parent;
        while (parent is not null && cloud.Entries.TryGetValue(parent, out var ancestor))
        { if (protectionRoots.Contains(parent)) return true; parent = ancestor.Parent; }
        return false;
    }
    // An uncertain completed upload may preserve its predecessor as unresolved conflict material.
    // Both forms are valid protection; normal history alone may already have rotated.
    if (!cloud.Entries.Values.Any(e => !e.Item.Folder && Encoding.UTF8.GetString(e.Bytes) == "before" && IsProtected(e)))
        throw new IOException("Predecessor was not protected.");
    File.WriteAllText(Path.Combine(root, "verified.json"), JsonSerializer.Serialize(new { phase, verified = true, baselineVerified = true, predecessorProtected = true, sourceUnchanged = File.ReadAllText(Path.Combine(source, "document.txt")) == "after-crash" }));
}
else throw new ArgumentException("Unknown mode.");

sealed class DiskDrive : ICloudDrive
{
    internal sealed record Entry(RemoteItem Item, string? Parent, byte[] Bytes);
    internal Dictionary<string, Entry> Entries;
    private readonly string path, ready;
    private readonly string? phase;
    public DiskDrive(string root, string? phase)
    {
        path = Path.Combine(root, "synthetic-cloud.json"); ready = Path.Combine(root, "interrupt-ready.txt"); this.phase = phase;
        Entries = File.Exists(path) ? JsonSerializer.Deserialize<Dictionary<string, Entry>>(File.ReadAllText(path), AtomicJson.Options)! : [];
    }
    private void Save() => AtomicJson.Write(path, Entries, AtomicJson.Signature(AtomicJson.ReadText(path)));
    private void Pause(string boundary)
    {
        if (phase != boundary) return;
        using (var signal = new FileStream(ready, FileMode.CreateNew, FileAccess.Write, FileShare.Read)) { signal.Write(Encoding.UTF8.GetBytes(boundary)); signal.Flush(true); }
        Thread.Sleep(Timeout.Infinite);
    }
    public void Initialize(string repository)
    {
        Entries["drive-root"] = new(new("drive-root", "OneDrive", "root-v1", 0, true), null, []);
        Entries["management"] = new(new("management", "HarborBackup", "management-v1", 0, true, ParentId: "drive-root"), "drive-root", []);
        Add("management", "repository.json", JsonSerializer.SerializeToUtf8Bytes(new CloudRepository.RepositoryIdentity(2, repository, new()), AtomicJson.Options));
        Save();
    }
    private RemoteItem Add(string parent, string name, byte[] bytes, bool folder = false)
    {
        var id = Guid.NewGuid().ToString("N");
        using var input = new MemoryStream(bytes);
        var item = new RemoteItem(id, name, Guid.NewGuid().ToString("N"), bytes.Length, folder,
            QuickXorHash: folder ? null : QuickXor.ComputeAsync(input, default).GetAwaiter().GetResult(), ParentId: parent, ContentTag: Guid.NewGuid().ToString("N"));
        Entries[id] = new(item, parent, bytes); Save(); return item;
    }
    public Task<RemoteItem> RootAsync(CancellationToken token) => GetAsync("drive-root", token);
    public Task<RemoteItem> GetAsync(string id, CancellationToken token) => Task.FromResult(Entries[id].Item);
    public Task<RemoteItem?> FindAsync(string parent, string name, CancellationToken token) => Task.FromResult(Entries.Values.SingleOrDefault(e => e.Parent == parent && e.Item.Name.Equals(name, StringComparison.OrdinalIgnoreCase))?.Item);
    public Task<IReadOnlyList<RemoteItem>> ChildrenAsync(string parent, CancellationToken token) => Task.FromResult<IReadOnlyList<RemoteItem>>(Entries.Values.Where(e => e.Parent == parent).Select(e => e.Item).ToArray());
    public async Task<RemoteItem> FolderAsync(string parent, string name, CancellationToken token) => await FindAsync(parent, name, token) ?? Add(parent, name, [], true);
    public async Task<RemoteItem> UploadAsync(string parent, string name, Stream input, TransferSession? session, Action<TransferSession?> checkpoint, CancellationToken token, bool reconcileExisting = true)
    {
        if (name == "document.txt") Pause("Archived");
        var existing = await FindAsync(parent, name, token);
        if (existing is not null) { if (reconcileExisting) return existing; throw new IOException("Create collision."); }
        using var output = new MemoryStream(); await input.CopyToAsync(output, token);
        var result = Add(parent, name, output.ToArray());
        if (name == "document.txt") Pause("Uploaded");
        if (name.EndsWith(".json", StringComparison.Ordinal) && Entries[parent].Item.Name == "files") Pause("Metadata");
        return result;
    }
    public async Task DownloadAsync(RemoteItem item, Stream target, CancellationToken token)
    {
        if (Entries[item.Id].Item.ETag != item.ETag) throw new IOException("Changed download.");
        await target.WriteAsync(Entries[item.Id].Bytes.AsMemory((int)target.Position), token);
    }
    public Task DeleteAsync(RemoteItem item, CancellationToken token)
    {
        if (Entries[item.Id].Item.ETag != item.ETag) throw new IOException("Changed delete.");
        Entries.Remove(item.Id); Save(); return Task.CompletedTask;
    }
    public async Task<RemoteItem> MoveAsync(RemoteItem item, string parent, string name, DateTimeOffset? modifiedUtc, CancellationToken token)
    {
        if (Entries[item.Id].Parent is { } previous && Entries[previous].Item.Name == "Documents") Pause("Prepared");
        if (Entries[item.Id].Item.ETag != item.ETag || await FindAsync(parent, name, token) is not null) throw new IOException("Changed move.");
        var moved = item with { Name = name, ParentId = parent, ETag = Guid.NewGuid().ToString("N"), ModifiedUtc = modifiedUtc ?? item.ModifiedUtc };
        Entries[item.Id] = new(moved, parent, Entries[item.Id].Bytes); Save(); return moved;
    }
    public Task<RemoteItem> SetModifiedAsync(RemoteItem item, DateTimeOffset modifiedUtc, CancellationToken token)
    {
        if (Entries[item.Id].Item.ETag != item.ETag) throw new IOException("Changed timestamp.");
        var changed = item with { ModifiedUtc = modifiedUtc, ETag = Guid.NewGuid().ToString("N") };
        Entries[item.Id] = Entries[item.Id] with { Item = changed }; Save(); return Task.FromResult(changed);
    }
    public async Task<RemoteItem> CopyAsync(RemoteItem item, string parent, string name, CancellationToken token) => await FindAsync(parent, name, token) ?? Add(parent, name, Entries[item.Id].Bytes);
    public void Dispose() { }
}
