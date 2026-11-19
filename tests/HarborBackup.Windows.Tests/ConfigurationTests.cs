using HarborBackup.Windows.FileSystem;

namespace HarborBackup.Windows.Tests;

[TestClass]
public sealed class ConfigurationTests
{
    [TestMethod]
    public void LegacyStorageFileCannotActivateRemovedDirectoryTransport()
    {
        using var ws = new TestWorkspace();
        File.Delete(Path.Combine(ws.Config, "onedrive-account.json"));
        var oldSettings = Path.Combine(ws.Config, "storage-settings.json");
        File.WriteAllText(oldSettings, System.Text.Json.JsonSerializer.Serialize(new { schemaVersion = 1, oneDriveRoot = ws.Cloud }));
        var evidence = File.ReadAllBytes(oldSettings);
        var paths = new HarborBackup.Windows.Integration.RuntimePaths(Path.Combine(ws.Config, "projects.json"), Path.Combine(ws.Config, "backup-settings.json"));
        Assert.ThrowsExactly<InvalidOperationException>(() => new HarborBackup.Windows.Integration.BackupRuntime(paths).Load());
        CollectionAssert.AreEqual(evidence, File.ReadAllBytes(oldSettings));
        Assert.IsFalse(Directory.Exists(ws.Target));
    }
    [TestMethod]
    public void LoadsExistingIntervalAndDoesNotCreateStateDirectories()
    {
        using var ws = new TestWorkspace();
        var config = ws.Load();
        Assert.AreEqual(30, config.IntervalMinutes);
        Assert.AreEqual(7, config.Preview.Hash.FullCheckIntervalDays);
        Assert.AreEqual(ws.Source, config.Projects[0].Source);
        Assert.IsFalse(Directory.Exists(ws.State));
    }

    [TestMethod]
    public void TargetAliasesAndParentChildOverlapAreRejected()
    {
        using var ws = new TestWorkspace();
        ws.WriteProjectConfig(new { folders = new[] {
            new { source = ws.Source, destination = "Projects/Demo" },
            new { source = ws.Source, destination = "projects/demo/sub" } } });
        Assert.Throws<FormatException>(() => ws.Load());
    }

    [TestMethod]
    public void SourceErrorsDoNotDiscardOtherProjects()
    {
        using var ws = new TestWorkspace();
        ws.WriteProjectConfig(new { folders = new[] {
            new { source = "C:ambiguous", destination = "Bad" },
            new { source = ws.Source, destination = "Good" } } });
        var config = ws.Load();
        Assert.IsNotNull(config.Projects[0].ConfigurationError);
        Assert.IsNull(config.Projects[1].ConfigurationError);
    }

    [TestMethod]
    public void SourceInsideApplicationDataIsRejected()
    {
        using var ws = new TestWorkspace();
        ws.WriteProjectConfig(new { folders = new[] { new { source = ws.Config, destination = "Demo" } } });
        Assert.IsNotNull(ws.Load().Projects[0].ConfigurationError);
    }

    [TestMethod]
    [DataRow("../state")]
    [DataRow(".")]
    [DataRow("docs/state")]
    [DataRow("conflicts")]
    public void StateCannotEscapeOrOverlapProtectedRoots(string statePath)
    {
        using var ws = new TestWorkspace();
        ws.WriteSystemConfig(new { sync = new { enabled = true, stateDirectory = statePath } });
        Assert.Throws<FormatException>(() => ws.Load());
    }

    [TestMethod]
    [DataRow("../escape")]
    [DataRow("CON")]
    [DataRow("demo./file")]
    [DataRow("C:/absolute")]
    [DataRow("demo//sub")]
    public void UnsafeRelativePathsAreRejected(string path) => Assert.Throws<FormatException>(() => PathRules.Relative(path));

    [TestMethod]
    public void DesktopAndEnvironmentExpansionAreExplicit()
    {
        Assert.AreEqual(@"D:\Desktop\项目", PathRules.Source("项目", @"D:\Desktop"));
        Assert.AreEqual(@"C:\Work\项目", PathRules.Source("%WORK%/项目", @"D:\Desktop", key => key == "WORK" ? @"C:\Work" : null));
        Assert.Throws<FormatException>(() => PathRules.Source("%UNSET%", @"D:\Desktop", _ => null));
        Assert.Throws<FormatException>(() => PathRules.Source(@"relative\sub", @"D:\Desktop"));
    }

    [TestMethod]
    public void ExclusionsPreserveScopeOrderAndLiteralBrackets()
    {
        var patterns = PathRules.Exclusions(@"C:\Source", ["*.tmp", "config/secret.json", "[draft].txt", "*.tmp"], false);
        Assert.HasCount(3, patterns);
        Assert.IsTrue(PathRules.Excluded("nested/a.tmp", patterns, @"C:\Source"));
        Assert.IsTrue(PathRules.Excluded("config/secret.json", patterns, @"C:\Source"));
        Assert.IsFalse(PathRules.Excluded("nested/config/secret.json", patterns, @"C:\Source"));
        Assert.IsTrue(PathRules.Excluded("[draft].txt", patterns, @"C:\Source"));
        Assert.IsFalse(PathRules.Excluded("d.txt", patterns, @"C:\Source"));
        Assert.Throws<FormatException>(() => PathRules.Exclusions(@"C:\Source", ["data/*"], true));
    }

    [TestMethod]
    public void DuplicateJsonKeysAreRejected()
    {
        using var ws = new TestWorkspace();
        File.WriteAllText(Path.Combine(ws.Config, "backup-settings.json"), "{\"sync\":{\"enabled\":true,\"Enabled\":false}}");
        Assert.Throws<FormatException>(() => ws.Load());
    }
}
