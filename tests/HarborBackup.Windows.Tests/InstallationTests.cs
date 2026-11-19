using HarborBackup.Windows.Integration;

namespace HarborBackup.Windows.Tests;

[TestClass]
public sealed class InstallationTests
{
    [TestMethod]
    public void InstalledAndPortablePathsAreStableAndDoNotCreateData()
    {
        using var ws = new TestWorkspace();
        var install = Path.Combine(ws.Root, "install");
        var app = Path.Combine(install, "app");
        Directory.CreateDirectory(app);
        var paths = ApplicationPaths.Resolve(applicationDirectory: app);
        Assert.AreEqual(Path.Combine(install, "data", "config", "projects.json"), paths.ProjectConfig);
        Assert.AreEqual(Path.Combine(install, "data", "config", "desktop-settings.json"), paths.Data.DesktopSettings);
        Assert.IsFalse(Directory.Exists(paths.Data.Root));
        var custom = ApplicationPaths.Resolve(Path.Combine(ws.Config, "projects.json"), Path.Combine(ws.Config, "backup-settings.json"), app);
        Assert.AreEqual(ws.Config, custom.Data.Config);
    }
    [TestMethod]
    public void MaintenanceRejectsLaunchAndRunningMarkerLivesUntilLastProcessLeaves()
    {
        var name = @"Local\HarborBackup.Test-" + Guid.NewGuid().ToString("N");
        using (var maintenance = new Mutex(false, name + ".Maintenance"))
            Assert.Throws<IOException>(() => new InstallationGuard(name));
        using (var first = new InstallationGuard(name))
        {
            using (var second = new InstallationGuard(name)) { }
            Assert.IsTrue(Mutex.TryOpenExisting(name + ".Running", out var marker));
            marker!.Dispose();
        }
        Assert.IsFalse(Mutex.TryOpenExisting(name + ".Running", out _));
    }
}
