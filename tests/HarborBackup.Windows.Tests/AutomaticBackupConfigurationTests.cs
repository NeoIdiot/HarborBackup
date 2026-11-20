using System.Text.Json.Nodes;
using HarborBackup.Windows.Integration;
using HarborBackup.Windows.Persistence.Configuration;

namespace HarborBackup.Windows.Tests;

[TestClass]
[DoNotParallelize]
public sealed class AutomaticBackupConfigurationTests
{
    private static ConfigurationEditor Editor(TestWorkspace ws) => new(new(Path.Combine(ws.Config, "projects.json"),
        Path.Combine(ws.Config, "backup-settings.json")), ws.Device, Path.Combine(ws.Root, "desktop"));

    [TestMethod]
    public void ProjectPausePersistsWithoutChangingMappingOrRecoveryEvidence()
    {
        using var ws = new TestWorkspace();
        var editor = Editor(ws);
        editor.Save(editor.Load(), ConfigurationPart.Projects);
        var before = ws.Load(); var project = before.Projects.Single();
        Assert.IsFalse(before.IsProjectPaused(project.ProjectId));
        CloudMapping.MarkReady(project, before);
        var mapping = CloudMapping.Capture(project, before);
        ws.Write(CloudMapping.StateRoot(project, before), "baselines.json", "retained evidence");
        var state = Directory.GetFiles(ws.State, "*", SearchOption.AllDirectories).ToDictionary(p => p, File.ReadAllBytes);
        foreach (var paused in new[] { true, false, true })
        {
            var draft = editor.Load();
            draft.Projects["folders"]![0]!["backupPaused"] = paused;
            editor.Save(draft, ConfigurationPart.Projects);
            var after = ws.Load(); var next = after.Projects.Single();
            Assert.AreEqual(paused, after.IsProjectPaused(next.ProjectId));
            Assert.AreEqual(mapping.Signature, CloudMapping.Capture(next, after).Signature);
            Assert.AreEqual(mapping.Binding, CloudMapping.Bind(next, after));
            Assert.IsFalse(CloudMapping.NeedsFirstOperation(next, after));
            Assert.HasCount(before.RetiredMappings.Count, after.RetiredMappings);
            CollectionAssert.AreEquivalent(state.Keys.ToArray(), Directory.GetFiles(ws.State, "*", SearchOption.AllDirectories));
            foreach (var pair in state) CollectionAssert.AreEqual(pair.Value, File.ReadAllBytes(pair.Key));
        }
    }

    [TestMethod]
    [DataRow("null")]
    [DataRow("1")]
    [DataRow("\"true\"")]
    public void InvalidProjectPauseIsRejected(string json)
    {
        using var ws = new TestWorkspace();
        var draft = Editor(ws).Load();
        draft.Projects["folders"]![0]!["backupPaused"] = JsonNode.Parse(json);
        File.WriteAllText(Path.Combine(ws.Config, "projects.json"), draft.Projects.ToJsonString());
        Assert.ThrowsExactly<FormatException>(() => ws.Load());
    }

    [TestMethod]
    public void AutomaticModeDefaultsToUploadAndReadsLegacyPolicyPerProject()
    {
        using var ws = new TestWorkspace();
        var initial = ws.Load();
        Assert.AreEqual(AutomaticBackupMode.UploadOnly, initial.GetAutomaticBackupMode(initial.Projects.Single().ProjectId));
        Assert.AreEqual(AutomaticBackupMode.UploadOnly, initial.GetAutomaticBackupMode(new string('0', 64)));
        ws.WriteProjectConfig(new { folders = new object[]
        {
            new { source = ws.Source, destination = "Projects/Default" },
            new { source = ws.Source, destination = "Projects/Upload", automaticBidirectional = false },
            new { source = ws.Source, destination = "Projects/Both", automaticBidirectional = true }
        } });
        var config = ws.Load();
        Assert.HasCount(3, config.AutomaticBackupModes);
        foreach (var project in config.Projects)
            Assert.AreEqual(project.Destination == "Projects/Both" ? AutomaticBackupMode.Bidirectional : AutomaticBackupMode.UploadOnly,
                config.GetAutomaticBackupMode(project.ProjectId));
        Assert.IsFalse(Directory.Exists(ws.State));
    }

    [TestMethod]
    [DataRow("upload", AutomaticBackupMode.UploadOnly)]
    [DataRow("bidirectional", AutomaticBackupMode.Bidirectional)]
    [DataRow("download", AutomaticBackupMode.DownloadOnly)]
    public void ExplicitModeOverridesLegacyPolicy(string value, AutomaticBackupMode expected)
    {
        using var ws = new TestWorkspace();
        var path = Path.Combine(ws.Config, "projects.json");
        var document = JsonNode.Parse(File.ReadAllText(path))!;
        document["folders"]![0]!["automaticBackupMode"] = value;
        // A superseded field is not interpreted by clients that understand the new policy.
        document["folders"]![0]!["automaticBidirectional"] = "obsolete value";
        File.WriteAllText(path, document.ToJsonString());
        var config = ws.Load();
        Assert.AreEqual(expected, config.GetAutomaticBackupMode(config.Projects.Single().ProjectId));
        Assert.IsFalse(Directory.Exists(ws.State));
    }

    [TestMethod]
    [DataRow("null")]
    [DataRow("true")]
    [DataRow("1")]
    [DataRow("[]")]
    [DataRow("{}")]
    [DataRow("\"\"")]
    [DataRow("\"Upload\"")]
    [DataRow("\"mirror\"")]
    public void InvalidExplicitModesAreRejectedWithoutFallingBackToLegacy(string json)
    {
        using var ws = new TestWorkspace();
        var path = Path.Combine(ws.Config, "projects.json");
        var document = JsonNode.Parse(File.ReadAllText(path))!;
        document["folders"]![0]!["automaticBackupMode"] = JsonNode.Parse(json);
        document["folders"]![0]!["automaticBidirectional"] = true;
        File.WriteAllText(path, document.ToJsonString());
        Assert.Throws<FormatException>(() => ws.Load());
        Assert.IsFalse(Directory.Exists(ws.State));
    }

    [TestMethod]
    [DataRow("null")]
    [DataRow("1")]
    [DataRow("\"true\"")]
    [DataRow("[]")]
    [DataRow("{}")]
    public void InvalidAutomaticBidirectionalValuesAreRejected(string json)
    {
        using var ws = new TestWorkspace();
        var path = Path.Combine(ws.Config, "projects.json");
        var document = JsonNode.Parse(File.ReadAllText(path))!;
        document["folders"]![0]!["automaticBidirectional"] = JsonNode.Parse(json);
        File.WriteAllText(path, document.ToJsonString());
        Assert.Throws<FormatException>(() => ws.Load());
        Assert.IsFalse(Directory.Exists(ws.State));
    }

    [TestMethod]
    public void TogglingAutomaticPolicyKeepsMappingRecoverySignaturesReadinessAndState()
    {
        using var ws = new TestWorkspace();
        var editor = Editor(ws); var initial = editor.Load();
        initial.Projects["folders"]![0]!["mappingVersion"] = Guid.NewGuid().ToString("N");
        File.WriteAllText(Path.Combine(ws.Config, "projects.json"), initial.Projects.ToJsonString());
        editor.Save(editor.Load(), ConfigurationPart.Projects);
        var before = ws.Load(); var project = before.Projects.Single();
        CloudMapping.MarkReady(project, before);
        var originalMapping = CloudMapping.Capture(project, before);
        ws.Write(CloudMapping.StateRoot(project, before), "baselines.json", "retained baseline evidence");
        var state = Directory.GetFiles(ws.State, "*", SearchOption.AllDirectories).ToDictionary(p => p, File.ReadAllBytes);
        foreach (var mode in new[] { "bidirectional", "download", "upload", "download" })
        {
            var draft = editor.Load();
            draft.Projects["folders"]![0]!["automaticBackupMode"] = mode;
            editor.Save(draft, ConfigurationPart.Projects);
            var after = ws.Load(); var next = after.Projects.Single();
            Assert.AreEqual(mode switch { "bidirectional" => AutomaticBackupMode.Bidirectional,
                "download" => AutomaticBackupMode.DownloadOnly, _ => AutomaticBackupMode.UploadOnly }, after.GetAutomaticBackupMode(next.ProjectId));
            Assert.AreEqual(project.MappingVersion, next.MappingVersion);
            Assert.AreEqual(originalMapping.Signature, CloudMapping.Capture(next, after).Signature);
            Assert.AreEqual(originalMapping.Binding, CloudMapping.Bind(next, after));
            originalMapping.Validate(after);
            Assert.IsFalse(CloudMapping.NeedsFirstOperation(next, after));
            Assert.HasCount(before.RetiredMappings.Count, after.RetiredMappings);
            CollectionAssert.AreEquivalent(state.Keys.ToArray(), Directory.GetFiles(ws.State, "*", SearchOption.AllDirectories));
            foreach (var pair in state) CollectionAssert.AreEqual(pair.Value, File.ReadAllBytes(pair.Key));
        }
    }
}
