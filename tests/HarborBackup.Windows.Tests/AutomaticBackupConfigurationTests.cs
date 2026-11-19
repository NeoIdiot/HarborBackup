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
    public void AutomaticBidirectionalDefaultsOffAndIsSelectedPerProject()
    {
        using var ws = new TestWorkspace();
        Assert.IsEmpty(ws.Load().AutomaticBidirectionalProjects);
        ws.WriteProjectConfig(new { folders = new object[]
        {
            new { source = ws.Source, destination = "Projects/Default" },
            new { source = ws.Source, destination = "Projects/Upload", automaticBidirectional = false },
            new { source = ws.Source, destination = "Projects/Both", automaticBidirectional = true }
        } });
        var config = ws.Load();
        Assert.HasCount(1, config.AutomaticBidirectionalProjects);
        Assert.Contains(config.Projects.Single(p => p.Destination == "Projects/Both").ProjectId, config.AutomaticBidirectionalProjects);
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
        foreach (var enabled in new[] { true, false, true })
        {
            var draft = editor.Load();
            draft.Projects["folders"]![0]!["automaticBidirectional"] = enabled;
            editor.Save(draft, ConfigurationPart.Projects);
            var after = ws.Load(); var next = after.Projects.Single();
            Assert.AreEqual(enabled, after.AutomaticBidirectionalProjects.Contains(next.ProjectId));
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
