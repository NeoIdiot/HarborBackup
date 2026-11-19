using System.Text.Json.Nodes;
using HarborBackup.Core.Domain;
using HarborBackup.Windows.Integration;
using HarborBackup.Windows.Persistence;
using HarborBackup.Windows.Persistence.Configuration;
using static HarborBackup.Windows.Tests.CloudTransportTests;

namespace HarborBackup.Windows.Tests;

[TestClass]
[DoNotParallelize]
public sealed class ConfigurationImpactTests
{
    [TestMethod]
    [DataRow(true)]
    [DataRow(false)]
    public void DraftRuleImpactCountsExcludedAndReintroducedPathsWithoutSaving(bool exclude)
    {
        using var f = new Fixture(); using var ws = new TestWorkspace();
        var data = new DataPaths(Path.Combine(ws.Root, "data")); var store = new CloudAccountStore(data);
        store.Save(f.Account, store.Read());
        ws.WriteProjectConfig(new { folders = new[] { new { source = ws.Source, destination = "Documents", bindingVersion = 1,
            mappingVersion = Guid.NewGuid().ToString("N") } }, excludedFiles = exclude ? Array.Empty<string>() : new[] { "*.tmp" } });
        File.WriteAllText(Path.Combine(ws.Source, "a.tmp"), "temporary"); File.WriteAllText(Path.Combine(ws.Source, "keep.txt"), "keep");
        var paths = new RuntimePaths(Path.Combine(ws.Config, "projects.json"), Path.Combine(ws.Config, "backup-settings.json"));
        var editor = new ConfigurationEditor(paths, ws.Device); var before = File.ReadAllText(paths.ProjectConfig);
        var draft = editor.Load(); draft.Projects["excludedFiles"] = exclude ? new JsonArray("*.tmp") : new JsonArray();
        var report = editor.Preview(draft, driveFactory: () => f.Drive);
        var impact = report.ConfigurationImpact.Single();
        Assert.IsFalse(impact.MappingChanged); Assert.IsTrue(impact.RulesChanged);
        Assert.AreEqual(exclude ? 1 : 0, impact.NewlyExcluded); Assert.AreEqual(exclude ? 0 : 1, impact.NewlyIncluded);
        Assert.AreEqual(exclude ? 1 : 2, report.Summary.Transfers);
        Assert.AreEqual(before, File.ReadAllText(paths.ProjectConfig));
        Assert.IsFalse(Directory.Exists(ws.State)); Assert.IsEmpty(f.Drive.UploadIntents);
        Assert.AreEqual(0, f.Drive.BusinessDownloads);
    }
    [TestMethod]
    public void BudgetSettingsSaveKeepsMappingAndReadyEvidence()
    {
        using var ws = new TestWorkspace();
        ws.WriteProjectConfig(new { folders = new[] { new { source = ws.Source, destination = "Documents", bindingVersion = 1,
            mappingVersion = Guid.NewGuid().ToString("N") } } });
        var paths = new RuntimePaths(Path.Combine(ws.Config, "projects.json"), Path.Combine(ws.Config, "backup-settings.json"));
        var editor = new ConfigurationEditor(paths, ws.Device); var before = ws.Load(); var project = before.Projects.Single();
        CloudMapping.MarkReady(project, before);
        var projects = File.ReadAllText(paths.ProjectConfig);
        var draft = editor.Load(); var schedule = draft.System["schedule"]!.AsObject();
        schedule["automaticMaxTransferMiB"] = 16; schedule["automaticMaxDurationMinutes"] = 3;
        editor.Save(draft, ConfigurationPart.System);
        var after = ws.Load();
        Assert.AreEqual(new AutomaticBackupBudget(16 * 1048576, 3), after.AutomaticBudget);
        Assert.AreEqual(CloudMapping.Bind(project, before), CloudMapping.Bind(after.Projects.Single(), after));
        Assert.IsFalse(CloudMapping.NeedsFirstOperation(after.Projects.Single(), after));
        Assert.AreEqual(projects, File.ReadAllText(paths.ProjectConfig));
    }
    [TestMethod]
    [DataRow(-1, 0)]
    [DataRow(1048577, 0)]
    [DataRow(0, -1)]
    [DataRow(0, 1441)]
    public void InvalidAutomaticBudgetsAreRejected(int mib, int minutes)
    {
        var json = new JsonObject { ["schemaVersion"] = 1, ["sync"] = new JsonObject { ["enabled"] = true },
            ["schedule"] = new JsonObject { ["automaticMaxTransferMiB"] = mib, ["automaticMaxDurationMinutes"] = minutes } };
        Assert.ThrowsExactly<FormatException>(() => BackupSettingsReader.Parse(json.ToJsonString()));
    }
}
