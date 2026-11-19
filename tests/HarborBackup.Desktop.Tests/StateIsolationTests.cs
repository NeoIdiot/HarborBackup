using HarborBackup.Desktop.Controllers;
using HarborBackup.Desktop.Views.Dialogs;
using HarborBackup.Core.Domain;
using HarborBackup.Windows.Integration;
using HarborBackup.Windows.Persistence;
using HarborBackup.Windows.Persistence.Configuration;

namespace HarborBackup.Windows.Tests;

[TestClass]
[DoNotParallelize]
public sealed class StateIsolationTests
{
    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task SavedRepositoryRetentionRemainsEditableWithoutCloudListing(bool needsAuthorization)
    {
        using var ws = new TestWorkspace();
        var paths = new RuntimePaths(Path.Combine(ws.Config, "projects.json"), Path.Combine(ws.Config, "backup-settings.json"));
        var store = new CloudAccountStore(paths.Data); var old = store.Read();
        if (needsAuthorization) store.Save(old with { RequiresAuthorization = true }, old);
        var directory = Path.Combine(paths.Data.State, "pending", "cloud-transactions"); Directory.CreateDirectory(directory);
        var evidence = Path.Combine(directory, "keep.json"); File.WriteAllText(evidence, "keep");
        var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            using var host = new Form();
            async Task CheckAsync()
            {
                try
                {
                    var opens = 0;
                    var session = new CloudAccountSession(paths.Data, _ => { opens++; throw new IOException("offline"); });
                    var controller = new DesktopController(paths); await controller.RefreshAsync();
                    using var dialog = new CloudAccountDialog(controller, session);
                    dialog.Show(host); await dialog.Initialization;
                    Assert.AreEqual(1, ((ComboBox)dialog.Controls.Find("CloudSelectionMode", true).Single()).SelectedIndex);
                    var beforeSave = opens;
                    ((ComboBox)dialog.Controls.Find("CloudArchiveRetention", true).Single()).SelectedIndex = 1;
                    ((Button)dialog.Controls.Find("ApplyCloudStorage", true).Single()).PerformClick();
                    var deadline = DateTime.UtcNow.AddSeconds(10);
                    while (dialog.IsWorking && DateTime.UtcNow < deadline) await Task.Delay(20);
                    Assert.IsFalse(dialog.IsWorking);
                    Assert.AreEqual(LocalRetention.KeepLocal, store.Read().ArchiveRetention);
                    Assert.AreEqual(beforeSave, opens);
                    Assert.AreEqual(old.RepositoryId, store.Read().RepositoryId);
                    Assert.AreEqual("keep", File.ReadAllText(evidence));
                    dialog.Close(); done.TrySetResult();
                }
                catch (Exception ex) { done.TrySetException(ex); }
                finally { host.Close(); }
            }
            host.Shown += async (_, _) => await CheckAsync();
            Application.Run(host);
        }) { IsBackground = true };
        thread.SetApartmentState(ApartmentState.STA); thread.Start();
        await done.Task.WaitAsync(TimeSpan.FromSeconds(20));
        Assert.IsTrue(thread.Join(TimeSpan.FromSeconds(3)));
    }

    [TestMethod]
    [DataRow("scheduler")]
    [DataRow("last-run")]
    public async Task InvalidDisplayOrControlStateKeepsConfigurationEditable(string name)
    {
        using var ws = new TestWorkspace();
        var paths = new RuntimePaths(Path.Combine(ws.Config, "projects.json"), Path.Combine(ws.Config, "backup-settings.json"));
        var controller = new DesktopController(paths); await controller.RefreshAsync();
        var c = controller.State.Configuration!;
        var state = Path.Combine(c.Preview.StateDirectory, "devices", c.Preview.DeviceId, name + ".json");
        Directory.CreateDirectory(Path.GetDirectoryName(state)!); File.WriteAllText(state, "broken");
        await controller.RefreshAsync();
        Assert.IsNotNull(controller.State.Configuration);
        Assert.HasCount(1, controller.State.Projects);
        Assert.IsFalse(controller.State.NeedsSetup);
        Assert.HasCount(1, controller.State.Details);
        Assert.AreNotEqual("配置错误", controller.State.Details[0].Action);
        if (name == "scheduler") Assert.IsTrue(controller.State.Paused);
        var rows = controller.State.Details; await controller.RefreshAsync();
        Assert.AreSame(rows, controller.State.Details, "Repeated diagnostics must not continually rebind the grid.");
        var draft = controller.Editor.Load(); draft.System["schedule"]!["intervalMinutes"] = 47;
        controller.Editor.Save(draft, ConfigurationPart.System);
        await controller.RefreshAsync();
        Assert.AreEqual(47, controller.State.Configuration!.IntervalMinutes);
        Assert.AreEqual("broken", File.ReadAllText(state));
        File.Delete(state);
        await controller.RefreshAsync();
        Assert.IsEmpty(controller.State.Details);
        Assert.IsNull(controller.State.Error);
    }
}
