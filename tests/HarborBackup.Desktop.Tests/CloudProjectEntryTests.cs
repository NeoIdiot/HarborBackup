using HarborBackup.Desktop.Controllers;
using HarborBackup.Desktop.Views;
using HarborBackup.Windows.Integration;
using HarborBackup.Windows.Persistence.Configuration;

namespace HarborBackup.Windows.Tests;

[TestClass]
[DoNotParallelize]
public sealed class CloudProjectEntryTests
{
    [TestMethod]
    [DataRow(true, true, false)]
    [DataRow(false, true, false)]
    [DataRow(true, false, false)]
    [DataRow(false, false, false)]
    [DataRow(true, true, true)]
    [DataRow(false, true, true)]
    public async Task FirstCloudProjectNeedsAccountButNotAnExistingLocalProject(bool missingConfig, bool bound, bool requiresAuthorization)
    {
        using var ws = new TestWorkspace();
        var paths = new RuntimePaths(Path.Combine(ws.Config, "projects.json"), Path.Combine(ws.Config, "backup-settings.json"));
        if (missingConfig) { File.Delete(paths.ProjectConfig); File.Delete(paths.SystemConfig); }
        else ws.WriteProjectConfig(new { folders = Array.Empty<object>() });
        var store = new CloudAccountStore(paths.Data); var saved = store.Read();
        store.Save(saved with { Enabled = bound, RequiresAuthorization = requiresAuthorization }, saved);
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            try
            {
                var controller = new DesktopController(paths, (_, _) => Task.FromResult<IReadOnlyList<CloudProjectTarget>>([]));
                using var form = new MainForm(controller);
                async Task CheckAsync()
                {
                    try
                    {
                        await controller.RefreshAsync();
                        await form.NavigateAsync("备份项目");
                        var join = (Button)form.Controls.Find("加入云端项目", true).Single();
                        var retrieve = (Button)form.Controls.Find("取回云端项目…", true).Single();
                        var canStart = bound && !requiresAuthorization;
                        Assert.AreEqual(canStart, join.Enabled);
                        Assert.AreEqual(canStart, retrieve.Enabled);
                        Assert.IsFalse(((Button)form.Controls.Find("立即备份", true).Single()).Enabled);
                        Assert.IsFalse(((Button)form.Controls.Find("取回更新…", true).Single()).Enabled);
                        if (canStart)
                        {
                            using var release = new ManualResetEventSlim();
                            var busy = controller.ExclusiveAsync(() => release.Wait(TimeSpan.FromSeconds(5)));
                            try
                            {
                                Assert.IsFalse(join.Enabled);
                                Assert.IsFalse(retrieve.Enabled);
                            }
                            finally { release.Set(); await busy; }
                            Assert.IsTrue(join.Enabled);
                            Assert.IsTrue(retrieve.Enabled);
                        }
                        if (missingConfig)
                        {
                            Assert.IsFalse(File.Exists(paths.ProjectConfig));
                            Assert.IsFalse(File.Exists(paths.SystemConfig));
                        }
                        completion.TrySetResult();
                    }
                    catch (Exception ex) { completion.TrySetException(ex); }
                    finally { form.ClosePermanently(); }
                }
                form.Shown += async (_, _) => await CheckAsync();
                Application.Run(form);
            }
            catch (Exception ex) { completion.TrySetException(ex); }
        }) { IsBackground = true };
        thread.SetApartmentState(ApartmentState.STA); thread.Start();
        await completion.Task.WaitAsync(TimeSpan.FromSeconds(30));
        Assert.IsTrue(thread.Join(TimeSpan.FromSeconds(5)));
    }
}
