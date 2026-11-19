using HarborBackup.Windows.Integration;
using HarborBackup.Windows.Persistence.Configuration;

namespace HarborBackup.Windows.Tests;

[TestClass]
[DoNotParallelize]
public sealed class FreshDataTests
{
    [TestMethod]
    public void FreshStartRegeneratesEmptyConfigurationAndRetainsEveryOldFile()
    {
        using var ws = new TestWorkspace();
        var paths = new RuntimePaths(Path.Combine(ws.Config, "projects.json"), Path.Combine(ws.Config, "backup-settings.json"));
        ws.Write(ws.State, "transactions/keep.json", "unreadable old recovery evidence");
        var business = ws.Write(ws.Source, "a.txt", "business");
        var before = Directory.GetFiles(paths.Data.Root, "*", SearchOption.AllDirectories)
            .ToDictionary(p => Path.GetRelativePath(paths.Data.Root, p), File.ReadAllBytes);
        var result = FreshDataInitializer.Recreate(paths, "Local\\HarborFreshTest." + Guid.NewGuid().ToString("N"));
        Assert.IsNotNull(result.RetainedData);
        foreach (var (relative, bytes) in before)
            CollectionAssert.AreEqual(bytes, File.ReadAllBytes(Path.Combine(result.RetainedData, relative)));
        Assert.HasCount(2, Directory.GetFiles(paths.Data.Root, "*", SearchOption.AllDirectories));
        var editor = new ConfigurationEditor(paths, ws.Device);
        Assert.IsEmpty(editor.Validate(editor.Load()).Projects);
        Assert.IsFalse(new CloudAccountStore(paths.Data).Read().Enabled);
        Assert.AreEqual("business", File.ReadAllText(business));
    }

    [TestMethod]
    public void ActiveApplicationRejectsFreshStartWithoutChangingData()
    {
        using var ws = new TestWorkspace();
        var paths = new RuntimePaths(Path.Combine(ws.Config, "projects.json"), Path.Combine(ws.Config, "backup-settings.json"));
        var name = "Local\\HarborFreshTest." + Guid.NewGuid().ToString("N");
        using var running = new InstallationGuard(name);
        var before = File.ReadAllBytes(paths.ProjectConfig);
        Assert.Throws<IOException>(() => FreshDataInitializer.Recreate(paths, name));
        CollectionAssert.AreEqual(before, File.ReadAllBytes(paths.ProjectConfig));
    }
}
