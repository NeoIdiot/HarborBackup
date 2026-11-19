using HarborBackup.Desktop.Controllers;
using HarborBackup.OneDrive.Transport;
using HarborBackup.Desktop.Localization;
using HarborBackup.Desktop.Presentation;
using HarborBackup.Desktop.Views.Dialogs;
using HarborBackup.Desktop.Views.Pages;
using HarborBackup.Windows.Integration;
using HarborBackup.Windows.Persistence.Configuration;

namespace HarborBackup.Windows.Tests;

[TestClass]
[DoNotParallelize]
public sealed class CloudAccountUiTests
{
    [TestMethod]
    [DataRow("zh-CN")]
    [DataRow("en-US")]
    public async Task OfflineBrowseShowsRetryHintAndPreservesAuthorizationAndDraft(string language)
    {
        using var ws = new TestWorkspace();
        var paths = new RuntimePaths(Path.Combine(ws.Config, "projects.json"), Path.Combine(ws.Config, "backup-settings.json"));
        var store = new CloudAccountStore(paths.Data);
        var before = File.ReadAllText(Path.Combine(ws.Config, "onedrive-account.json"));
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            using var host = new Form();
            async Task CheckAsync()
            {
                try
                {
                    UiText.SetLanguage(language);
                    var online = false;
                    var controller = new DesktopController(paths, networkAvailable: () => online); await controller.RefreshAsync();
                    var session = new FakeAccountSession(store) { Offline = true };
                    using var dialog = new CloudAccountDialog(controller, session);
                    dialog.Show(host); await dialog.Initialization;
                    Assert.AreEqual(UiText.Text(CloudNetworkAvailability.Offline), dialog.Controls.Find("CloudFeedback", true).Single().Text);
                    Assert.IsFalse(dialog.Controls.Find("CloudErrorDetails", true).Single().Visible);
                    var draft = dialog.Controls.Find("CloudRepositoryName", true).Single(); draft.Text = "keep draft";
                    ((Button)dialog.Controls.Find("刷新记录", true).Single()).PerformClick(); await Idle(dialog);
                    Assert.AreEqual("keep draft", draft.Text);
                    Assert.AreEqual(before, File.ReadAllText(Path.Combine(ws.Config, "onedrive-account.json")));
                    Assert.IsFalse(store.Read().RequiresAuthorization);
                    online = true;
                    await controller.TickAsync();
                    Assert.AreNotEqual(UiText.Text(CloudNetworkAvailability.Offline), dialog.Controls.Find("CloudFeedback", true).Single().Text);
                    Assert.AreEqual("keep draft", draft.Text);
                    Assert.AreEqual(before, File.ReadAllText(Path.Combine(ws.Config, "onedrive-account.json")));
                    completion.TrySetResult();
                }
                catch (Exception ex) { completion.TrySetException(ex); }
                finally { host.Close(); }
            }
            host.Shown += async (_, _) => await CheckAsync();
            Application.Run(host);
        }) { IsBackground = true };
        thread.SetApartmentState(ApartmentState.STA); thread.Start();
        await completion.Task.WaitAsync(TimeSpan.FromSeconds(20)); Assert.IsTrue(thread.Join(TimeSpan.FromSeconds(3)));
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task AccountModeShowsCloudPathsAndBilingualConnectionDialogWithoutNetwork(bool existingAccount)
    {
        using var ws = new TestWorkspace();
        var paths = new RuntimePaths(Path.Combine(ws.Config,"projects.json"),Path.Combine(ws.Config,"backup-settings.json"));
        var store = new CloudAccountStore(paths.Data);
        if (existingAccount) store.Save(new(Enabled:true,ClientId:Guid.NewGuid().ToString(),AccountId:"synthetic-account",AccountName:"test@example.invalid",
            DriveId:"synthetic-drive",RootId:"synthetic-root",RootName:"Harbor Backup",RepositoryId:Guid.NewGuid().ToString("N")),store.Read());
        if (!existingAccount) store.Save(new(), store.Read());
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(()=>
        {
            using var form = new Form { Width=1180,Height=800 };
            async Task CheckAsync()
            {
                try
                {
                    foreach(var language in new[]{"zh-CN","en-US"})
                    {
                        UiText.SetLanguage(language);
                        var controller = new DesktopController(paths); await controller.RefreshAsync();
                        if (existingAccount) Assert.IsNotNull(controller.State.Configuration?.CloudAccount);
                        using var page = new SettingsPage(controller,new ConfigurationPresenter(controller));
                        form.Controls.Add(page); page.UpdateState(controller.State);
                        var field=(TextBox)page.Controls.Find("MetadataDirectory",true).Single();
                        if (existingAccount) Assert.AreEqual("OneDrive/Harbor Backup/metadata",field.Text);
                        using var dialog=new CloudAccountDialog(controller, new FakeAccountSession(store));
                        dialog.Show(form); await dialog.Initialization;
                        var client = dialog.Controls.Find("CloudClientId",true).Single();
                        var custom = (CheckBox)dialog.Controls.Find("UseCustomCloudApplication",true).Single();
                        var advanced = dialog.Controls.Find("CloudAdvancedSettings",true).Single();
                        Assert.AreEqual(store.Read().ClientId,client.Text);
                        Assert.AreEqual(existingAccount,custom.Checked);
                        Assert.AreEqual(existingAccount,advanced.Visible);
                        advanced.Visible = true;
                        custom.Checked = true;
                        Assert.IsTrue(client.Visible);
                        custom.Checked = false;
                        Assert.IsFalse(client.Visible);
                        Assert.AreEqual(store.Read().ClientId,client.Text, "Toggling defaults must not discard the custom ID.");
                        advanced.Visible = false;
                        Assert.AreEqual(language=="en-US"?"OneDrive account":"OneDrive 账户连接",dialog.Text);
                        if(Environment.GetEnvironmentVariable("HARBORBACKUP_SCREENSHOT_DIR") is { } folder)
                        {
                            Directory.CreateDirectory(folder);
                            using var image=new System.Drawing.Bitmap(dialog.Width,dialog.Height);
                            dialog.DrawToBitmap(image,new System.Drawing.Rectangle(0,0,image.Width,image.Height));
                            image.Save(Path.Combine(folder,language+"-cloud-account.png"));
                        }
                        dialog.Close();form.Controls.Remove(page);
                    }
                    completion.SetResult();
                }
                catch(Exception ex){completion.SetException(ex);}
                finally{form.Close();}
            }
            form.Shown += async (_,_) => await CheckAsync();
            Application.Run(form);
        });
        thread.SetApartmentState(ApartmentState.STA);thread.Start();
        await completion.Task.WaitAsync(TimeSpan.FromSeconds(40));
    }
    [TestMethod]
    [DataRow("zh-CN", 1f)]
    [DataRow("en-US", 1.5f)]
    [DataRow("zh-CN", 2f)]
    public async Task FixedFooterAndFailedApplyPreserveDraftThenReopenSavedRepository(string language, float scale)
    {
        using var ws = new TestWorkspace();
        var paths = new RuntimePaths(Path.Combine(ws.Config, "projects.json"), Path.Combine(ws.Config, "backup-settings.json"));
        var store = new CloudAccountStore(paths.Data);
        store.Save(new(ClientId: Guid.NewGuid().ToString(), AccountId: "signed-in", AccountName: "test@example.invalid", DriveId: "drive"), store.Read());
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            using var host = new Form();
            async Task CheckAsync()
            {
                try
                {
                    UiText.SetLanguage(language);
                    var controller = new DesktopController(paths);
                    await controller.RefreshAsync();
                    var fake = new FakeAccountSession(store);
                    using (var dialog = new CloudAccountDialog(controller, fake))
                    {
                        dialog.Show(host); await dialog.Initialization;
                        dialog.Scale(new SizeF(scale, scale));
                        dialog.Size = dialog.MinimumSize;
                        Control Find(string name) => dialog.Controls.Find(name, true).Single();
                        Assert.IsNull(fake.LastBrowseId);
                        Assert.AreEqual(UiText.Text("已连接，待选择云端仓库"), Find("SavedCloudLocation").Text);
                        var name = (TextBox)Find("CloudRepositoryName");
                        name.Text = "MyBackup";
                        fake.FailBrowse = true;
                        ((Button)Find("刷新记录")).PerformClick();
                        await Idle(dialog);
                        Assert.AreEqual("MyBackup", name.Text);
                        Assert.HasCount(2, ((ListBox)Find("CloudFolders")).Items);
                        fake.FailBrowse = false;
                        ((Button)Find("刷新记录")).PerformClick(); await Idle(dialog);
                        Assert.AreEqual("MyBackup", name.Text, "Refreshing must not reset the draft repository name.");
                        ((ComboBox)Find("CloudSelectionMode")).SelectedIndex = 1;
                        ((ListBox)Find("CloudFolders")).SelectedIndex = 0;
                        ((Button)Find("刷新记录")).PerformClick(); await Idle(dialog);
                        Assert.AreEqual(0, ((ListBox)Find("CloudFolders")).SelectedIndex, "Refreshing must preserve the selected folder.");
                        ((ComboBox)Find("CloudArchiveRetention")).SelectedIndex = 1;
                        fake.FailApply = true;
                        ((Button)Find("ApplyCloudStorage")).PerformClick(); await Idle(dialog);
                        Assert.IsFalse(store.Read().Enabled);
                        Assert.AreEqual("projects", fake.LastSelection!.FolderId);
                        Assert.AreEqual(UiText.Text("云端存储设置未保存，请查看详情。"), Find("CloudFeedback").Text);
                        Assert.IsTrue(Find("CloudErrorDetails").Visible);
                        Assert.AreEqual(0, ((ListBox)Find("CloudFolders")).SelectedIndex);
                        foreach (var controlName in new[] { "CloudFeedback", "ApplyCloudStorage", "CloudErrorDetails", "CloudClose" })
                        {
                            var control = Find(controlName);
                            var bounds = dialog.RectangleToClient(control.RectangleToScreen(control.ClientRectangle));
                            Assert.IsTrue(dialog.ClientRectangle.Contains(bounds), controlName + " must remain within the window.");
                        }
                        var feedback = Find("CloudFeedback");
                        var measured = TextRenderer.MeasureText(feedback.Text, feedback.Font, new Size(feedback.Width, int.MaxValue), TextFormatFlags.WordBreak);
                        Assert.IsGreaterThanOrEqualTo(measured.Height, feedback.Height, "Status text must not be clipped.");
                        if (Environment.GetEnvironmentVariable("HARBORBACKUP_SCREENSHOT_DIR") is { } folder)
                        {
                            Directory.CreateDirectory(folder);
                            using var image = new Bitmap(dialog.Width, dialog.Height);
                            dialog.DrawToBitmap(image, new Rectangle(0, 0, image.Width, image.Height));
                            image.Save(Path.Combine(folder, $"account-footer-{language}-{scale}.png"));
                        }
                        fake.FailApply = false;
                        ((Button)Find("ApplyCloudStorage")).PerformClick(); await Idle(dialog);
                        Assert.IsTrue(store.Read().Enabled);
                        Assert.AreEqual(Core.Domain.LocalRetention.KeepLocal, store.Read().ArchiveRetention);
                        Assert.AreEqual(UiText.Text("云端仓库和保留策略已保存。"), feedback.Text);
                        dialog.Close();
                    }
                    using (var reopened = new CloudAccountDialog(controller, fake))
                    {
                        reopened.Show(host); await reopened.Initialization;
                        Assert.AreEqual("projects", fake.LastBrowseId);
                        Assert.AreEqual("OneDrive/Projects", reopened.Controls.Find("SavedCloudLocation", true).Single().Text);
                        reopened.Close();
                    }
                    using var settings = new SettingsPage(controller, new ConfigurationPresenter(controller));
                    settings.UpdateState(controller.State);
                    Assert.AreEqual("OneDrive/Projects", settings.Controls.Find("CloudRoot", true).Single().Text);
                    completion.SetResult();
                }
                catch (Exception ex) { completion.SetException(ex); }
                finally { host.Close(); }
            }
            host.Shown += async (_, _) => await CheckAsync();
            Application.Run(host);
        });
        thread.SetApartmentState(ApartmentState.STA); thread.Start();
        await completion.Task.WaitAsync(TimeSpan.FromSeconds(60));
    }

    private static async Task Idle(CloudAccountDialog dialog)
    {
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (dialog.IsWorking && DateTime.UtcNow < deadline) await Task.Delay(20);
        Assert.IsFalse(dialog.IsWorking, "Dialog operation did not complete.");
    }
}
