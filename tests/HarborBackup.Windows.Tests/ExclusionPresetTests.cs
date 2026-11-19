using System.Text.Json.Nodes;
using HarborBackup.Core.Domain;
using HarborBackup.Windows.FileSystem;
using HarborBackup.Windows.Integration;
using HarborBackup.Windows.Persistence.Configuration;

namespace HarborBackup.Windows.Tests;

[TestClass]
[DoNotParallelize]
public sealed class ExclusionPresetTests
{
    private static ConfigurationEditor Editor(TestWorkspace ws) => new(new(
        Path.Combine(ws.Config, "projects.json"), Path.Combine(ws.Config, "backup-settings.json")), ws.Device);

    [TestMethod]
    public void DefaultsExcludeNothingAndPublishedCatalogPatternsAreFixed()
    {
        using var ws = new TestWorkspace();
        Assert.IsEmpty(ws.Load().Projects.Single().ExcludedFiles);
        CollectionAssert.AreEqual(new[] { "word-locks-v1", "excel-locks-v1", "powerpoint-locks-v1",
            "windows-thumbnails-v1", "windows-folder-settings-v1", "macos-folder-info-v1" }, ExclusionPresets.All.Select(p => p.Id).ToArray());
        CollectionAssert.AreEqual(new[] { "~$*.docx", "~$*.doc", "~$*.xlsx", "~$*.xls", "~$*.pptx", "~$*.ppt",
            "Thumbs.db", "desktop.ini", ".DS_Store" }, ExclusionPresets.All.SelectMany(p => p.Files).ToArray());
    }

    [TestMethod]
    public void PresetsMergeWithCustomRulesAndMatchNestedNamesWithoutExcludingOrdinaryDocuments()
    {
        using var ws = new TestWorkspace();
        ws.WriteProjectConfig(new { builtInExclusions = new[] { "word-locks-v1", "word-locks-v1", "excel-locks-v1" },
            excludedFiles = new[] { "~$*.DOCX", "*.tmp" }, excludedDirectories = new[] { "cache" },
            folders = new[] { new { source = ws.Source, destination = "Demo", excludedFiles = new[] { "secret.txt" } } } });
        var project = ws.Load().Projects.Single();
        Assert.HasCount(6, project.ExcludedFiles);
        foreach (var path in new[] { "~$方案.docx", "nested/~$PLAN.DOCX", "deep/nested/~$sheet.xls", "nested/a.tmp", "secret.txt" })
            Assert.IsTrue(PathRules.Excluded(path, project.ExcludedFiles, project.Source), path);
        foreach (var path in new[] { "方案.docx", "nested/~方案.docx", "sheet.xlsx", "notes.txt" })
            Assert.IsFalse(PathRules.Excluded(path, project.ExcludedFiles, project.Source), path);
        Assert.IsNotNull(project.ExclusionSources);
        CollectionAssert.AreEqual(new[] { "~$*.DOCX", "*.tmp" }, project.ExclusionSources.GlobalFiles.ToArray());
        CollectionAssert.AreEqual(new[] { "word-locks-v1", "excel-locks-v1" }, project.ExclusionSources.BuiltInPresetIds.ToArray());
        CollectionAssert.AreEqual(new[] { "cache" }, project.ExcludedDirectories.ToArray());
    }

    [TestMethod]
    [DataRow("null")]
    [DataRow("\"word-locks-v1\"")]
    [DataRow("[null]")]
    [DataRow("[12]")]
    [DataRow("[\"word-locks-v2\"]")]
    public void InvalidPresetSelectionsAreRejectedEvenWithoutProjects(string json)
    {
        using var ws = new TestWorkspace();
        File.WriteAllText(Path.Combine(ws.Config, "projects.json"), "{\"schemaVersion\":2,\"folders\":[],\"builtInExclusions\":" + json + "}");
        Assert.Throws<FormatException>(() => ws.Load());
        Assert.IsFalse(Directory.Exists(ws.State));
    }

    [TestMethod]
    public void SaveAndUncheckPreserveManualRulesBusinessContentAndRecoveryEvidence()
    {
        using var ws = new TestWorkspace();
        var editor = Editor(ws); var draft = editor.Load();
        draft.Projects["excludedFiles"] = new JsonArray("~$*.docx");
        draft.Projects["builtInExclusions"] = new JsonArray("word-locks-v1");
        var local = ws.Write(ws.Source, "~$draft.docx", "local");
        var cloud = ws.Write(ws.Target, "~$draft.docx", "cloud");
        var state = ws.Write(ws.State, "evidence.json", "retained");
        editor.Save(draft, ConfigurationPart.Projects);
        var selected = ws.Load();
        Assert.HasCount(2, selected.Projects.Single().ExcludedFiles);
        var unselected = editor.Load(); unselected.Projects["builtInExclusions"] = new JsonArray();
        editor.Save(unselected, ConfigurationPart.Projects);
        var result = ws.Load();
        CollectionAssert.AreEqual(new[] { "~$*.docx" }, result.Projects.Single().ExcludedFiles.ToArray());
        Assert.AreEqual(selected.Projects.Single().MappingVersion, result.Projects.Single().MappingVersion);
        Assert.AreEqual(CloudMapping.Bind(selected.Projects.Single(), selected), CloudMapping.Bind(result.Projects.Single(), result));
        Assert.HasCount(selected.RetiredMappings.Count, result.RetiredMappings);
        Assert.AreEqual("local", File.ReadAllText(local)); Assert.AreEqual("cloud", File.ReadAllText(cloud));
        Assert.AreEqual("retained", File.ReadAllText(state));
        Assert.IsTrue(PathRules.Excluded("~$draft.docx", result.Projects.Single().ExcludedFiles, ws.Source));
        Assert.IsFalse(PathRules.Excluded("~$draft.doc", result.Projects.Single().ExcludedFiles, ws.Source));
    }

    [TestMethod]
    [DataRow("preset")]
    [DataRow("global-file")]
    [DataRow("global-directory")]
    [DataRow("project-file")]
    [DataRow("project-directory")]
    public void RuleEditsKeepInitializedMappingAndDoNotWriteState(string kind)
    {
        using var ws = new TestWorkspace(); var editor = Editor(ws); var initial = editor.Load();
        initial.Projects["folders"]![0]!["mappingVersion"] = Guid.NewGuid().ToString("N");
        File.WriteAllText(Path.Combine(ws.Config, "projects.json"), initial.Projects.ToJsonString());
        editor.Save(editor.Load(), ConfigurationPart.Projects);
        var before = ws.Load(); var project = before.Projects.Single();
        CloudMapping.MarkReady(project, before);
        var state = Directory.GetFiles(ws.State, "*", SearchOption.AllDirectories).ToDictionary(p => p, File.ReadAllText);
        foreach (var enabled in new[] { true, false, true })
        {
            var draft = editor.Load();
            var node = kind.StartsWith("project") ? draft.Projects["folders"]![0]!.AsObject() : draft.Projects;
            var key = kind == "preset" ? "builtInExclusions" : kind.EndsWith("directory") ? "excludedDirectories" : "excludedFiles";
            node[key] = enabled ? new JsonArray(kind == "preset" ? "word-locks-v1" : kind.EndsWith("directory") ? "cache" : "*.tmp") : new JsonArray();
            editor.Save(draft, ConfigurationPart.Projects);
            var after = ws.Load(); var next = after.Projects.Single();
            Assert.AreEqual(project.MappingVersion, next.MappingVersion);
            Assert.AreEqual(CloudMapping.Bind(project, before), CloudMapping.Bind(next, after));
            Assert.IsFalse(CloudMapping.NeedsFirstOperation(next, after));
            Assert.HasCount(before.RetiredMappings.Count, after.RetiredMappings);
            CollectionAssert.AreEquivalent(state.Keys.ToArray(), Directory.GetFiles(ws.State, "*", SearchOption.AllDirectories));
            foreach (var pair in state) Assert.AreEqual(pair.Value, File.ReadAllText(pair.Key));
        }
    }

    [TestMethod]
    [DataRow("null")]
    [DataRow("2")]
    [DataRow("\"1\"")]
    public void UnsupportedBindingVersionCannotBeSilentlyReadAsLegacy(string json)
    {
        using var ws = new TestWorkspace();
        var path = Path.Combine(ws.Config, "projects.json"); var document = JsonNode.Parse(File.ReadAllText(path))!;
        document["folders"]![0]!["bindingVersion"] = JsonNode.Parse(json);
        File.WriteAllText(path, document.ToJsonString());
        Assert.Throws<FormatException>(() => ws.Load());
    }

    [TestMethod]
    public void SelectionOrderDoesNotChangeEffectiveFingerprint()
    {
        using var ws = new TestWorkspace(); var editor = Editor(ws); var draft = editor.Load();
        draft.Projects["builtInExclusions"] = new JsonArray("excel-locks-v1", "word-locks-v1");
        var first = editor.Validate(draft).Projects.Single();
        draft.Projects["builtInExclusions"] = new JsonArray("word-locks-v1", "excel-locks-v1", "word-locks-v1");
        Assert.AreEqual(first.Fingerprint, editor.Validate(draft).Projects.Single().Fingerprint);
        var config = editor.Validate(draft); var project = config.Projects.Single();
        Assert.AreEqual(CloudMapping.Bind(project with { ExclusionSources = null }, config), CloudMapping.Bind(project, config));
        CloudMapping.Capture(project, config).Validate(config);
    }
}
