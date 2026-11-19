using System.Collections;
using System.Globalization;
using System.Resources;
using System.Text;
using HarborBackup.Desktop.Localization;
using HarborBackup.Desktop.Lifecycle;
using HarborBackup.Windows.Integration;

namespace HarborBackup.Windows.Tests;

[TestClass]
[DoNotParallelize]
public sealed class LocalizationTests
{
    [TestMethod]
    public void BothCatalogsHaveMatchingKeysAndFormatArguments()
    {
        var resources = new ResourceManager("HarborBackup.Desktop.Localization.Strings", typeof(UiText).Assembly);
        var zh = resources.GetResourceSet(CultureInfo.InvariantCulture, true, false)!;
        var en = resources.GetResourceSet(CultureInfo.GetCultureInfo("en-US"), true, false)!;
        Assert.IsNotNull(en, "English satellite resources must be deployed with the application.");
        var chineseKeys = zh.Cast<DictionaryEntry>().Select(e => (string)e.Key).Order().ToArray();
        var englishKeys = en.Cast<DictionaryEntry>().Select(e => (string)e.Key).Order().ToArray();
        CollectionAssert.AreEqual(chineseKeys, englishKeys);
        foreach (var key in chineseKeys)
            Assert.AreEqual(CompositeFormat.Parse(zh.GetString(key)!).MinimumArgumentCount,
                CompositeFormat.Parse(en.GetString(key)!).MinimumArgumentCount, key);
        Assert.AreEqual("Settings", en.GetString("设置"));
        Assert.AreEqual("设置", zh.GetString("设置"));
    }

    [TestMethod]
    public void ExistingPreferencesDefaultToChineseAndLanguageDoesNotChangePaths()
    {
        using var ws = new TestWorkspace();
        var path = Path.Combine(ws.Config, "preferences.json");
        File.WriteAllText(path, "{\"schemaVersion\":1,\"width\":1100,\"height\":720,\"oneDriveRoot\":\"C:\\\\数据\"}");
        var paths = new RuntimePaths(Path.Combine(ws.Config, "projects.json"), Path.Combine(ws.Config, "backup-settings.json"));
        var store = new DesktopPreferencesStore(paths, path);
        var original = store.Read();
        Assert.AreEqual("zh-CN", original.Language);
        store.Save(original with { Language = "en-US" });
        var loaded = new DesktopPreferencesStore(paths, path).Read();
        Assert.AreEqual("en-US", loaded.Language);
        Assert.AreEqual(original.Width, loaded.Width);
    }
}
