using System.Text.Json.Nodes;
using HarborBackup.Core.Domain;
using HarborBackup.Windows.Integration;
using HarborBackup.Windows.Persistence.Configuration;

namespace HarborBackup.Windows.Tests;

[TestClass]
[DoNotParallelize]
public sealed class ConfigurationEditorTests
{
    [TestMethod]
    public void OldConfigurationIsRejectedWithoutMigrationOrWrites()
    {
        using var ws = new TestWorkspace();
        var path = Path.Combine(ws.Config, "projects.json");
        var old = "{\"schemaVersion\":1,\"folders\":[]}"; File.WriteAllText(path, old);
        Assert.Throws<FormatException>(() => Editor(ws).Validate(Editor(ws).Load()));
        Assert.AreEqual(old, File.ReadAllText(path));
        Assert.IsFalse(Directory.Exists(ws.State));
    }
    [TestMethod]
    public void InvalidSavedDestinationCanBeRepairedWithoutRunningAnOperation()
    {
        using var ws = new TestWorkspace();
        ws.WriteProjectConfig(new { folders = new[] { new { source = ws.Source, destination = "../invalid" } } });
        var editor = Editor(ws); var draft = editor.Load();
        draft.Projects["folders"]![0]!["destination"] = "Projects/Fixed";
        editor.Save(draft, ConfigurationPart.Projects);
        Assert.AreEqual("Projects/Fixed", ws.Load().Projects.Single().Destination);
        Assert.IsNotNull(ws.Load().Projects.Single().MappingVersion);
        Assert.IsFalse(Directory.Exists(ws.State));
    }

    [TestMethod]
    public void SwitchingSourceOnlyWritesConfigurationAndEachSwitchGetsFreshEvidenceScope()
    {
        using var ws = new TestWorkspace();
        var original = ws.Load(); var first = original.Projects.Single();
        var file = ws.Write(ws.Source, "a.txt", "unchanged");
        var state = ws.Write(ws.State, "evidence.json", "retained");
        var editor = Editor(ws); var draft = editor.Load();
        var missing = Path.Combine(ws.Root, "missing-source");
        draft.Projects["folders"]![0]!["source"] = missing;
        var preview = editor.Validate(draft);
        editor.Save(draft, ConfigurationPart.Projects);
        var second = ws.Load().Projects.Single();
        Assert.AreEqual(first.TaskId, second.TaskId);
        Assert.AreEqual(first.Destination, second.Destination);
        Assert.AreEqual(preview.Projects.Single().MappingVersion, second.MappingVersion);
        Assert.IsNotNull(second.MappingVersion);
        Assert.IsFalse(Directory.Exists(missing));
        Assert.AreEqual("unchanged", File.ReadAllText(file)); Assert.AreEqual("retained", File.ReadAllText(state));
        Assert.HasCount(1, Directory.GetFiles(ws.State, "*", SearchOption.AllDirectories));
        var back = editor.Load(); back.Projects["folders"]![0]!["source"] = ws.Source;
        editor.Save(back, ConfigurationPart.Projects);
        var thirdConfig = ws.Load(); var third = thirdConfig.Projects.Single();
        Assert.AreNotEqual(second.MappingVersion, third.MappingVersion);
        Assert.AreNotEqual(CloudMapping.Bind(first, original), CloudMapping.Bind(third, thirdConfig));
        Assert.IsTrue(CloudMapping.NeedsFirstOperation(third, thirdConfig));
        editor.Save(editor.Load(), ConfigurationPart.Projects);
        Assert.AreEqual(third.MappingVersion, ws.Load().Projects.Single().MappingVersion);
    }

    [TestMethod]
    public void LegacyTransactionRemainsDiscoverableAfterProjectRemoval()
    {
        using var ws = new TestWorkspace(); var config = ws.Load(); var project = config.Projects.Single();
        var id = Guid.NewGuid().ToString("N");
        var path = Path.Combine(CloudMapping.StateRoot(project, config), "cloud-transactions", id + ".json");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var journal = new CloudJournal(4, id, CloudMapping.Bind(project, config), "a.txt", SyncDirection.Backup, null, null, null, true, ProtectionKind.None, "parent");
        File.WriteAllText(path, System.Text.Json.JsonSerializer.Serialize(journal, HarborBackup.Windows.Persistence.AtomicJson.Options));
        var before = File.ReadAllBytes(path);
        var editor = Editor(ws); var draft = editor.Load(); draft.Projects["folders"]!.AsArray().Clear();
        editor.Save(draft, ConfigurationPart.Projects);
        var pending = CloudMapping.Pending(ws.Load());
        Assert.HasCount(1, pending); Assert.AreEqual(ws.Source, pending[0].Project.Source);
        CollectionAssert.AreEqual(before, File.ReadAllBytes(path));
    }

    [TestMethod]
    public void ProjectSavePreservesUnknownFieldsExpressionsAndOtherDocument()
    {
        using var ws = new TestWorkspace();
        var expression = "%TEMP%/" + Path.GetRelativePath(Path.GetTempPath(), ws.Source).Replace('\\', '/');
        ws.WriteProjectConfig(new { custom = "retain", folders = new[] { new { source = expression, destination = "Projects/Demo", note = "保留说明" } } });
        var systemBefore = File.ReadAllBytes(Path.Combine(ws.Config, "backup-settings.json"));
        var editor = Editor(ws);
        var draft = editor.Load();
        draft.Projects["folders"]![0]!["excludedFiles"] = new JsonArray("*.tmp");
        editor.Save(draft, ConfigurationPart.Projects);
        var read = editor.Load();
        Assert.AreEqual("retain", read.Projects["custom"]!.GetValue<string>());
        Assert.AreEqual(expression, read.Projects["folders"]![0]!["source"]!.GetValue<string>());
        Assert.AreEqual("保留说明", read.Projects["folders"]![0]!["note"]!.GetValue<string>());
        CollectionAssert.AreEqual(systemBefore, File.ReadAllBytes(Path.Combine(ws.Config, "backup-settings.json")));
        Assert.IsFalse(Directory.Exists(ws.State));
    }

    [TestMethod]
    public void SystemSaveUsesDiskProjectsAndPreservesUneditedSettings()
    {
        using var ws = new TestWorkspace();
        ws.WriteSystemConfig(new { sync = new { enabled = true }, schedule = new { intervalMinutes = 30 }, custom = 42, logging = new { verbose = true } });
        var editor = Editor(ws);
        var draft = editor.Load();
        var projectBefore = File.ReadAllBytes(Path.Combine(ws.Config, "projects.json"));
        draft.Projects["folders"]![0]!["destination"] = "not-saved";
        draft.System["schedule"]!["intervalMinutes"] = 15;
        editor.Save(draft, ConfigurationPart.System);
        CollectionAssert.AreEqual(projectBefore, File.ReadAllBytes(Path.Combine(ws.Config, "projects.json")));
        Assert.AreEqual(15, ws.Load().IntervalMinutes);
        Assert.AreEqual(42, editor.Load().System["custom"]!.GetValue<int>());
        Assert.IsTrue(editor.Load().System["logging"]!["verbose"]!.GetValue<bool>());
    }

    [TestMethod]
    [DataRow(ConfigurationPart.Projects)]
    [DataRow(ConfigurationPart.System)]
    public void ExternalChangesRejectSaveAndKeepDraft(ConfigurationPart part)
    {
        using var ws = new TestWorkspace();
        var editor = Editor(ws);
        var draft = editor.Load();
        draft.Projects["folders"]![0]!["destination"] = "new-target";
        var changed = Path.Combine(ws.Config, "backup-settings.json");
        File.AppendAllText(changed, "\n ");
        var before = File.ReadAllText(changed);
        Assert.Throws<IOException>(() => editor.Save(draft, part));
        Assert.AreEqual(before, File.ReadAllText(changed));
        Assert.AreEqual("new-target", draft.Projects["folders"]![0]!["destination"]!.GetValue<string>());
        Assert.AreEqual("Projects/Demo", ws.Load().Projects[0].Destination);
    }

    [TestMethod]
    [DataRow("mapping")]
    [DataRow("source")]
    [DataRow("remove")]
    [DataRow("exclusions")]
    public void PendingEvidenceDoesNotBlockConfigurationOnlyChanges(string change)
    {
        using var ws = new TestWorkspace();
        var transactions = Path.Combine(ws.State, "devices", new string('e', 64), "transactions");
        Directory.CreateDirectory(transactions);
        File.WriteAllText(Path.Combine(transactions, "pending.json"), "damaged evidence");
        var editor = Editor(ws);
        var draft = editor.Load();
        switch (change)
        {
            case "mapping": draft.Projects["folders"]![0]!["destination"] = "changed"; break;
            case "remove": draft.Projects["folders"]!.AsArray().Clear(); break;
            case "source": draft.Projects["folders"]![0]!["source"] = Path.Combine(ws.Root, "missing"); break;
            default: draft.Projects["excludedFiles"] = new JsonArray("*.tmp"); break;
        }
        editor.Save(draft, ConfigurationPart.Projects);
        if (change is "mapping" or "source") Assert.IsNotNull(ws.Load().Projects[0].MappingVersion);
        if (change == "exclusions") Assert.IsNull(ws.Load().Projects[0].MappingVersion);
        Assert.AreEqual("damaged evidence", File.ReadAllText(Path.Combine(transactions, "pending.json")));
    }

    [TestMethod]
    public void RemovingLastProjectRetainsAllBusinessFilesAndState()
    {
        using var ws = new TestWorkspace();
        ws.Write(ws.Source, "a.txt", "source");
        ws.Write(ws.Target, "a.txt", "target");
        var evidence = ws.Write(ws.State, "evidence.txt", "retain");
        var editor = Editor(ws);
        var draft = editor.Load();
        draft.Projects["folders"]!.AsArray().Clear();
        editor.Save(draft, ConfigurationPart.Projects);
        Assert.IsEmpty(ws.Load().Projects);
        Assert.AreEqual("source", File.ReadAllText(Path.Combine(ws.Source, "a.txt")));
        Assert.AreEqual("target", File.ReadAllText(Path.Combine(ws.Target, "a.txt")));
        Assert.AreEqual("retain", File.ReadAllText(evidence));
    }

    [TestMethod]
    public void MissingConfigurationCanBeInitializedWithoutRunningBackup()
    {
        using var ws = new TestWorkspace();
        var folder = Path.Combine(ws.Root, "first-run", "config");
        var editor = new ConfigurationEditor(new(Path.Combine(folder, "projects.json"), Path.Combine(folder, "backup-settings.json")), ws.Device);
        var draft = editor.Load();
        Assert.IsFalse(Directory.Exists(folder));
        ws.WriteAccountConfig(folder);
        draft.Projects["folders"]!.AsArray().Add(new JsonObject { ["id"] = HarborBackup.Windows.Integration.DeviceIdentity.Digest(Guid.NewGuid().ToString("N")), ["source"] = ws.Source, ["destination"] = "First/Project" });
        editor.Save(draft, ConfigurationPart.Projects);
        Assert.HasCount(1, editor.Validate(editor.Load()).Projects);
        Assert.IsFalse(Directory.Exists(Path.Combine(ws.Cloud, "First")));
        Assert.IsFalse(Directory.Exists(Path.Combine(folder, "state")));
    }

    [TestMethod]
    public void BusyBackupLockRejectsConfigurationWrite()
    {
        using var ws = new TestWorkspace();
        var editor = Editor(ws);
        var draft = editor.Load();
        using var held = new RunLock();
        Exception? failure = null;
        var thread = new Thread(() => { try { editor.Save(draft, ConfigurationPart.Projects); } catch (Exception ex) { failure = ex; } });
        thread.Start();
        Assert.IsTrue(thread.Join(TimeSpan.FromSeconds(5)));
        Assert.IsInstanceOfType<InvalidOperationException>(failure);
    }

    [TestMethod]
    public void InvalidConfigurationNeverReplacesOriginal()
    {
        using var ws = new TestWorkspace();
        var editor = Editor(ws);
        var draft = editor.Load();
        var original = File.ReadAllText(Path.Combine(ws.Config, "projects.json"));
        draft.Projects["folders"]![0]!["destination"] = "../escape";
        Assert.Throws<FormatException>(() => editor.Save(draft, ConfigurationPart.Projects));
        Assert.AreEqual(original, File.ReadAllText(Path.Combine(ws.Config, "projects.json")));
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public void SystemSettingsSaveBeforeBindingDoesNotEnableBackup(bool signedIn)
    {
        using var ws = new TestWorkspace();
        var paths = new RuntimePaths(Path.Combine(ws.Config, "projects.json"), Path.Combine(ws.Config, "backup-settings.json"));
        var store = new CloudAccountStore(paths.Data);
        var unbound = signedIn ? new CloudAccountSettings(ClientId: Guid.NewGuid().ToString(), AccountId: "account", DriveId: "drive") : new();
        store.Save(unbound, store.Read());
        // Unavailable project directories must not block independent settings edits.
        ws.WriteProjectConfig(new { folders = new[] { new { source = Path.Combine(ws.Root, "missing"), destination = "Demo" } } });
        var editor = Editor(ws);
        var before = File.ReadAllBytes(paths.ProjectConfig);
        var draft = editor.Load();
        draft.System["schedule"]!["intervalMinutes"] = 17;
        editor.Save(draft, ConfigurationPart.System);
        Assert.AreEqual(17, editor.Load().System["schedule"]!["intervalMinutes"]!.GetValue<int>());
        Assert.AreEqual(unbound, store.Read());
        CollectionAssert.AreEqual(before, File.ReadAllBytes(paths.ProjectConfig));
        Assert.Throws<InvalidOperationException>(() => ws.Load());
        editor.Save(editor.Load(), ConfigurationPart.Projects);
        var invalid = editor.Load();
        invalid.System["schedule"]!["intervalMinutes"] = 0;
        var original = File.ReadAllBytes(paths.SystemConfig);
        Assert.Throws<FormatException>(() => editor.Save(invalid, ConfigurationPart.System));
        CollectionAssert.AreEqual(original, File.ReadAllBytes(paths.SystemConfig));
    }

    [TestMethod]
    public async Task DisconnectKeepsRepositoryButBlocksBackupUntilReauthorization()
    {
        using var ws = new TestWorkspace();
        var paths = new RuntimePaths(Path.Combine(ws.Config, "projects.json"), Path.Combine(ws.Config, "backup-settings.json"));
        var session = new CloudAccountSession(paths.Data);
        var before = session.Read();
        var after = await session.DisconnectAsync(default); // synthetic account, empty token cache, no network
        Assert.IsTrue(after.RequiresAuthorization);
        Assert.AreEqual(before.RootId, after.RootId);
        Assert.AreEqual(before.RepositoryId, after.RepositoryId);
        Assert.IsTrue(after.Enabled);
        Assert.AreEqual(after, session.Read());
        Assert.Throws<InvalidOperationException>(() => ws.Load());
        var editor = Editor(ws);
        editor.Save(editor.Load(), ConfigurationPart.System);
        var draft = editor.Load();
        draft.Projects["folders"]![0]!["source"] = Path.Combine(ws.Root, "new-offline-source");
        editor.Save(draft, ConfigurationPart.Projects);
        Assert.IsFalse(Directory.Exists(Path.Combine(ws.Root, "new-offline-source")));
    }

    private static ConfigurationEditor Editor(TestWorkspace ws) => new(new(Path.Combine(ws.Config, "projects.json"),
        Path.Combine(ws.Config, "backup-settings.json")), ws.Device, Path.Combine(ws.Root, "desktop"));
}
