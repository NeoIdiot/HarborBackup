using HarborBackup.Core.Domain;
using HarborBackup.Windows.Integration;
using HarborBackup.Windows.Persistence.Configuration;

namespace HarborBackup.Windows.Tests;

[TestClass]
[DoNotParallelize]
public sealed class CloudBindingTests
{
    [TestMethod]
    [DataRow("drive")]
    [DataRow("repository")]
    [DataRow("client")]
    public void EveryBindingIdentityChangeRetainsPendingEvidenceAndSettings(string field)
    {
        using var ws = new TestWorkspace(); var data = new DataPaths(Path.Combine(ws.Root, "data"));
        var store = new CloudAccountStore(data); var old = store.Read();
        var directory = Path.Combine(data.State, "pending", "cloud-transactions"); Directory.CreateDirectory(directory);
        var pending = Path.Combine(directory, "operation.json"); File.WriteAllText(pending, "retained recovery evidence");
        var configPath = Path.Combine(data.Config, "onedrive-account.json"); var before = File.ReadAllText(configPath);
        var changed = field switch
        {
            "drive" => old with { DriveId = "other-drive" },
            "repository" => old with { RepositoryId = Guid.NewGuid().ToString("N") },
            _ => old with { ClientId = Guid.NewGuid().ToString() }
        };
        Assert.ThrowsExactly<IOException>(() => store.Save(changed, old));
        Assert.AreEqual(before, File.ReadAllText(configPath));
        Assert.AreEqual("retained recovery evidence", File.ReadAllText(pending));
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public void PreferencesAndEquivalentClientIdRemainEditableWithPendingEvidence(bool clientCaseOnly)
    {
        using var ws = new TestWorkspace(); var data = new DataPaths(Path.Combine(ws.Root, "data"));
        var store = new CloudAccountStore(data); var old = store.Read();
        var directory = Path.Combine(data.State, "pending", "cloud-transactions"); Directory.CreateDirectory(directory);
        var pending = Path.Combine(directory, "operation.json"); File.WriteAllText(pending, "retained");
        var changed = clientCaseOnly ? old with { ClientId = old.ClientId.ToUpperInvariant() } :
            old with { AccountName = "Updated display name", ArchiveRetention = LocalRetention.KeepLocal };
        store.Save(changed, old);
        Assert.AreEqual(changed, store.Read()); Assert.AreEqual("retained", File.ReadAllText(pending));
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task RetentionOnlySaveWorksOfflineWithPendingEvidence(bool needsAuthorization)
    {
        using var ws = new TestWorkspace(); var data = new DataPaths(Path.Combine(ws.Root, "data"));
        var store = new CloudAccountStore(data);
        var old = store.Read();
        if (needsAuthorization) { store.Save(old with { RequiresAuthorization = true }, old); old = store.Read(); }
        var directory = Path.Combine(data.State, "pending", "cloud-transactions"); Directory.CreateDirectory(directory);
        var record = Path.Combine(directory, "keep.json"); File.WriteAllText(record, "recovery evidence");
        var session = new CloudAccountSession(data, _ => throw new AssertFailedException("Policy save must not open the cloud transport."));
        var selection = new CloudStorageSelection(false, old.RootId!, "", old.RootName, old.Folders ?? new(), LocalRetention.KeepLocal, LocalRetention.KeepLocal)
        { RetentionOnly = true };
        var saved = await session.ApplyAsync(old, selection, default);
        Assert.AreEqual(old with { ArchiveRetention = LocalRetention.KeepLocal, ConflictRetention = LocalRetention.KeepLocal }, saved);
        Assert.AreEqual(saved, store.Read());
        Assert.AreEqual("recovery evidence", File.ReadAllText(record));
        await Assert.ThrowsAsync<InvalidOperationException>(() => session.ApplyAsync(saved, selection with { FolderId = "different-root" }, default));
        if (needsAuthorization)
            await Assert.ThrowsAsync<InvalidOperationException>(() => session.ApplyAsync(saved, selection with { FolderId = "different-root", RetentionOnly = false }, default));
        else await Assert.ThrowsAsync<IOException>(() => session.ApplyAsync(saved, selection with { FolderId = "different-root", RetentionOnly = false }, default));
    }

    [TestMethod]
    public async Task ApplyBindsAndSavesPoliciesTogetherAndCanReopen()
    {
        using var ws = new TestWorkspace();
        var data = new DataPaths(Path.Combine(ws.Root, "data"));
        var store = new CloudAccountStore(data);
        var old = store.Read() with { Enabled = false, RootId = null, RepositoryId = null };
        store.Save(old, store.Read());
        using var drive = new CloudTransportTests.FakeDrive();
        var session = new CloudAccountSession(data, _ => drive);
        var request = new CloudStorageSelection(true, "root", "Backup", "Backup", new(), LocalRetention.KeepLocal, LocalRetention.OnlineOnly);
        var saved = await session.ApplyAsync(old, request, default);
        Assert.IsTrue(saved.Enabled);
        Assert.AreEqual(LocalRetention.KeepLocal, saved.ArchiveRetention);
        Assert.AreEqual(saved, new CloudAccountStore(data).Read());
        var marker = (await drive.FindAsync(saved.RootId!, "repository.json", default))!;
        var identity = System.Text.Json.Nodes.JsonNode.Parse(drive.Items[marker.Id].Bytes)!;
        Assert.AreEqual(2,identity["schemaVersion"]!.GetValue<int>());
        Assert.IsNull(identity["folders"]!["backups"]);
        Assert.AreEqual(2,saved.SchemaVersion);
        var browse = await session.BrowseAsync(saved, saved.RootId, default);
        Assert.AreEqual(saved.RootId, browse.Current.Id);
        var updated = await session.ApplyAsync(saved, request with { CreateNew = false, FolderId = saved.RootId!, ConflictRetention = LocalRetention.KeepLocal }, default);
        Assert.AreEqual(saved.RepositoryId, updated.RepositoryId);
        Assert.AreEqual(LocalRetention.KeepLocal, updated.ConflictRetention);
    }

    [TestMethod]
    public async Task OrdinaryNonemptyFolderAndStaleAccountNeverReplaceSavedBinding()
    {
        using var ws = new TestWorkspace();
        var data = new DataPaths(Path.Combine(ws.Root, "data"));
        var store = new CloudAccountStore(data);
        var old = store.Read() with { Enabled = false, RootId = null, RepositoryId = null };
        store.Save(old, store.Read());
        using var drive = new CloudTransportTests.FakeDrive();
        var existing = drive.Add("root", "Projects", [], true);
        drive.Add(existing.Id, "important.txt", [1, 2, 3]);
        var session = new CloudAccountSession(data, _ => drive);
        var request = new CloudStorageSelection(false, existing.Id, "", "Projects", new(), LocalRetention.KeepLocal, LocalRetention.KeepLocal);
        await Assert.ThrowsAsync<IOException>(() => session.ApplyAsync(old, request, default));
        Assert.AreEqual(old, store.Read());
        Assert.IsNull(await drive.FindAsync(existing.Id, "repository.json", default));
        Assert.HasCount(4, drive.Items);
        var changed = old with { AccountName = "Changed elsewhere" };
        store.Save(changed, old);
        await Assert.ThrowsAsync<IOException>(() => session.ApplyAsync(old, request with { CreateNew = true, FolderId = "root", Name = "New" }, default));
        Assert.HasCount(4, drive.Items);
        Assert.AreEqual(changed, store.Read());
    }
}
