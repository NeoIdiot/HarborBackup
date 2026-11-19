using System.Text.Json;
using System.Text.Json.Nodes;
using HarborBackup.Core.Application;
using HarborBackup.Core.Domain;
using HarborBackup.Windows.FileSystem;
using HarborBackup.Windows.Integration;
using HarborBackup.Windows.Persistence;
using HarborBackup.Windows.Persistence.Configuration;
namespace HarborBackup.Windows.Tests;

[TestClass]
[DoNotParallelize]
public sealed class StorageLayoutTests
{
    [TestMethod]
    public void CacheCleanupPreservesCloudRecoveryAndProtection()
    {
        using var ws = new TestWorkspace();
        var data = DataPaths.FromState(ws.State);
        var pending = ws.Write(data.State, "devices/d/cloud/binding/cloud-transactions/pending.json", "recovery evidence");
        var archive = ws.Write(data.Archives, "version.txt", "protected bytes");
        ws.Write(data.Cache, "cloud-inventory/snapshot.json", "disposable");
        Assert.IsGreaterThan(0L, DataMaintenance.ClearDisposableData(data));
        Assert.AreEqual("recovery evidence", File.ReadAllText(pending));
        Assert.AreEqual("protected bytes", File.ReadAllText(archive));
        Assert.AreEqual(0L, DataMaintenance.CacheBytes(data));
    }
    [TestMethod]
    public void StableProjectIdSurvivesRenameAndMappingChangeInvalidatesBaseline()
    {
        using var ws = new TestWorkspace();
        var before = ws.Load();
        var editor = new ConfigurationEditor(new(Path.Combine(ws.Config, "projects.json"), Path.Combine(ws.Config, "backup-settings.json")), ws.Device);
        var draft = editor.Load(); draft.Projects["folders"]![0]!["name"] = "A new display name";
        editor.Save(draft, ConfigurationPart.Projects);
        Assert.AreEqual(before.Projects[0].TaskId, ws.Load().Projects[0].TaskId);
        draft = editor.Load(); draft.Projects["folders"]![0]!["destination"] = "Other";
        editor.Save(draft, ConfigurationPart.Projects);
        Assert.AreEqual(before.Projects[0].TaskId, ws.Load().Projects[0].TaskId);
        Assert.AreNotEqual(before.Projects[0].Fingerprint, ws.Load().Projects[0].Fingerprint);
    }
    [TestMethod]
    public void CleanupRefusesConcurrentOperationAndKeepsRecentTemporaryFiles()
    {
        using var ws = new TestWorkspace(); var data = DataPaths.FromState(ws.State);
        Directory.CreateDirectory(data.Temp); var recent = Path.Combine(data.Temp, "recent.txt"); File.WriteAllText(recent, "keep");
        using (var runLock = new RunLock())
        {
            Exception? error = null;
            var thread = new Thread(() => { try { DataMaintenance.ClearDisposableData(data); } catch (Exception ex) { error = ex; } });
            thread.Start(); Assert.IsTrue(thread.Join(5000)); Assert.IsInstanceOfType<InvalidOperationException>(error);
        }
        DataMaintenance.ClearDisposableData(data); Assert.IsTrue(File.Exists(recent));
    }
    [TestMethod]
    public void HistoryAndLogRetentionNeverDeleteRecoveryOrProtectionData()
    {
        using var ws = new TestWorkspace(); var config = ws.Load(); var data = DataPaths.FromState(ws.State);
        foreach (var dir in new[] { data.Logs, data.History, data.State, data.Archives, data.Conflicts })
        {
            Directory.CreateDirectory(dir);
            var path = Path.Combine(dir, dir == data.Logs ? "old.txt" : "old.json");
            File.WriteAllText(path, "evidence"); File.SetLastWriteTimeUtc(path, DateTime.UtcNow.AddDays(-400));
        }
        var report = new BackupReport("Backup", DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, [], false);
        RunStatusStore.Log(config, report);
        Assert.IsFalse(File.Exists(Path.Combine(data.Logs, "old.txt")));
        Assert.IsFalse(File.Exists(Path.Combine(data.History, "old.json")));
        foreach (var dir in new[] { data.State, data.Archives, data.Conflicts }) Assert.AreEqual("evidence", File.ReadAllText(Path.Combine(dir, "old.json")));
    }}