using System.Text.Json;
using System.Text.Json.Nodes;
using HarborBackup.Core.Domain;
using HarborBackup.Windows.Integration;
using HarborBackup.Windows.Persistence;
using HarborBackup.Windows.Persistence.Configuration;

namespace HarborBackup.Windows.Tests;

[TestClass]
[DoNotParallelize]
public sealed class RepositoryFormatTests
{
    private sealed class Fixture : IDisposable
    {
        public readonly TestWorkspace Workspace = new();
        public readonly CloudTransportTests.FakeDrive Drive = new();
        public DataPaths Data => new(Path.Combine(Workspace.Root,"data"));
        public CloudAccountStore Store => new(Data);
        public CloudAccountSession Session => new(Data,_ => Drive);
        public string SettingsPath => Path.Combine(Data.Config,"onedrive-account.json");
        public CloudAccountSettings Account;
        public readonly CloudFolders Layout = new("protection/history","protection/conflicts","coordination");
        public Fixture()
        {
            Account = Store.Read() with { RootId = "root", Folders = Layout };
            Store.Save(Account,Store.Read());
        }
        public JsonObject Identity(int schema = 1)
        {
            var node = JsonSerializer.SerializeToNode(new CloudRepository.RepositoryIdentity(schema,Account.RepositoryId!,Layout),AtomicJson.Options)!.AsObject();
            if (schema == 1) node["folders"]!["backups"] = "old/content";
            return node;
        }
        public void AddMarker(JsonObject node) => Drive.Add("root","repository.json",JsonSerializer.SerializeToUtf8Bytes(node,AtomicJson.Options));
        public CloudStorageSelection Selection => new(false,"root","","HarborBackup",Layout,LocalRetention.OnlineOnly,LocalRetention.OnlineOnly);
        public void Dispose() { Drive.Dispose(); Workspace.Dispose(); }
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task UpgradePreservesIdentityLayoutAndDataAndResumesLostMoveResponse(bool lostResponse)
    {
        using var f = new Fixture();
        var legacy = JsonSerializer.SerializeToNode(f.Account,AtomicJson.Options)!;
        legacy["schemaVersion"] = 1; legacy["folders"]!["backups"] = "old/content";
        File.WriteAllText(f.SettingsPath,legacy.ToJsonString());
        var originalSettings = File.ReadAllText(f.SettingsPath);
        f.AddMarker(f.Identity());
        var oldMarker = (await f.Drive.FindAsync("root","repository.json",default))!;
        var originalBytes = f.Drive.Items[oldMarker.Id].Bytes.ToArray();
        var existing = f.Drive.Add("root","old-content",[1,2,3]);
        var expected = f.Store.Read();
        Assert.AreEqual(2,expected.SchemaVersion);
        Assert.AreEqual(originalSettings,File.ReadAllText(f.SettingsPath),"Reading legacy config must not persist changes.");
        var reader = new CloudRepository(f.Drive,expected,f.Data);
        var unsupported = await Assert.ThrowsExactlyAsync<IOException>(() => reader.ValidateAsync(default));
        Assert.AreEqual("RepositoryUpgradeRequired",unsupported.Message);
        Assert.IsNotNull(await f.Drive.FindAsync("root","repository.json",default));
        f.Drive.LoseNextMoveResponse = lostResponse;
        if (lostResponse)
        {
            await Assert.ThrowsExactlyAsync<IOException>(() => f.Session.ApplyAsync(expected,f.Selection,default));
            Assert.IsNull(await f.Drive.FindAsync("root","repository.json",default));
            Assert.AreEqual(originalSettings,File.ReadAllText(f.SettingsPath));
        }
        var saved = await f.Session.ApplyAsync(expected,f.Selection,default);
        Assert.AreEqual(f.Account.RepositoryId,saved.RepositoryId);
        Assert.AreEqual(f.Layout,saved.Folders);
        Assert.AreEqual(2,saved.SchemaVersion);
        Assert.AreEqual(saved,f.Store.Read());
        var marker = (await f.Drive.FindAsync("root","repository.json",default))!;
        var document = JsonNode.Parse(f.Drive.Items[marker.Id].Bytes)!;
        Assert.AreEqual(2,document["schemaVersion"]!.GetValue<int>());
        Assert.IsNull(document["folders"]!["backups"]);
        var local = JsonNode.Parse(File.ReadAllText(f.SettingsPath))!;
        Assert.AreEqual(2,local["schemaVersion"]!.GetValue<int>());
        Assert.IsNull(local["folders"]!["backups"]);
        var backup = (await f.Drive.FindAsync("root",".harborbackup-repository-v1.json",default))!;
        Assert.AreEqual(oldMarker.Id,backup.Id);
        CollectionAssert.AreEqual(originalBytes,f.Drive.Items[backup.Id].Bytes);
        CollectionAssert.AreEqual(new byte[]{1,2,3},f.Drive.Items[existing.Id].Bytes);
        await new CloudRepository(f.Drive,saved,f.Data).ValidateAsync(default);
        await f.Session.ApplyAsync(saved,f.Selection,default);
        Assert.AreEqual(marker.ETag,(await f.Drive.FindAsync("root","repository.json",default))!.ETag);
    }

    [TestMethod]
    [DataRow("identity")]
    [DataRow("layout")]
    [DataRow("future")]
    [DataRow("v2-backups")]
    [DataRow("v2-missing")]
    [DataRow("backup-collision")]
    [DataRow("pending")]
    public async Task InvalidOrUnsafeUpgradeDoesNotChangeCloudOrSavedSettings(string problem)
    {
        using var f = new Fixture();
        var document = f.Identity(problem == "future" ? 3 : problem.StartsWith("v2-",StringComparison.Ordinal) ? 2 : 1);
        if (problem == "identity") document["repositoryId"] = Guid.NewGuid().ToString("N");
        if (problem == "layout") document["folders"]!["metadata"] = "different";
        if (problem == "v2-backups") document["folders"]!["backups"] = "backups";
        if (problem == "v2-missing") document["folders"]!.AsObject().Remove("metadata");
        f.AddMarker(document);
        if (problem == "backup-collision") f.Drive.Add("root",".harborbackup-repository-v1.json",[4,5]);
        if (problem == "pending")
        {
            var dir = Path.Combine(f.Data.State,"devices","test","cloud-transactions");
            Directory.CreateDirectory(dir); File.WriteAllText(Path.Combine(dir,"pending.json"),"{}");
        }
        var before = f.Drive.Items.ToDictionary(p=>p.Key,p=>(p.Value.Item.ETag,Bytes:Convert.ToHexString(p.Value.Bytes)));
        var settings = File.ReadAllText(f.SettingsPath);
        var failed = false;
        try { await f.Session.ApplyAsync(f.Store.Read(),f.Selection,default); }
        catch (Exception ex) when (ex is IOException or JsonException or FormatException) { failed = true; }
        Assert.IsTrue(failed);
        Assert.AreEqual(settings,File.ReadAllText(f.SettingsPath));
        Assert.HasCount(before.Count,f.Drive.Items);
        foreach(var item in f.Drive.Items)
        {
            Assert.AreEqual(before[item.Key].ETag,item.Value.Item.ETag);
            Assert.AreEqual(before[item.Key].Bytes,Convert.ToHexString(item.Value.Bytes));
        }
    }

    [TestMethod]
    public async Task LocalSaveFailureAfterCloudUpgradeCanBeRetried()
    {
        using var f = new Fixture(); f.AddMarker(f.Identity());
        var original = File.ReadAllText(f.SettingsPath);
        // The cloud migration may finish before the atomic local replace can acquire this file.
        using(var held = new FileStream(f.SettingsPath,FileMode.Open,FileAccess.Read,FileShare.Read))
            await Assert.ThrowsAsync<IOException>(()=>f.Session.ApplyAsync(f.Account,f.Selection,default));
        Assert.AreEqual(original,File.ReadAllText(f.SettingsPath));
        var marker = (await f.Drive.FindAsync("root","repository.json",default))!;
        Assert.AreEqual(2,JsonNode.Parse(f.Drive.Items[marker.Id].Bytes)!["schemaVersion"]!.GetValue<int>());
        var saved = await f.Session.ApplyAsync(f.Account,f.Selection,default);
        Assert.AreEqual(saved,f.Store.Read());
    }

    [TestMethod]
    public void V2AccountRejectsRemovedFieldAndManagementAreaRejectsBackups()
    {
        using var f = new Fixture();
        var node = JsonNode.Parse(File.ReadAllText(f.SettingsPath))!;
        node["folders"]!["backups"] = "backups";
        File.WriteAllText(f.SettingsPath,node.ToJsonString());
        Assert.ThrowsExactly<JsonException>(()=>f.Store.Read());
        Assert.ThrowsExactly<ArgumentException>(()=>f.Layout.Resolve("backups/file.txt"));
    }
}
