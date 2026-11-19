using HarborBackup.Core.Domain;
using HarborBackup.Desktop.Controllers;
using HarborBackup.Desktop.Localization;
using HarborBackup.Desktop.Presentation;
using HarborBackup.Desktop.Views;
using HarborBackup.Desktop.Views.Dialogs;
using HarborBackup.Windows.Integration;
using HarborBackup.Windows.Persistence;
using HarborBackup.Windows.Persistence.Configuration;

namespace HarborBackup.Windows.Tests;

[TestClass]
[DoNotParallelize]
public sealed class AutomaticBidirectionalUiTests
{
    private sealed class Presenter(ConfigurationEditor editor) : IConfigurationPresenter
    {
        public Task<ConfigurationDraft> LoadAsync() => Task.FromResult(editor.Load());
        public Task<ConfigurationSnapshot> ValidateAsync(ConfigurationDraft draft) => Task.FromResult(editor.Validate(draft));
        public Task<bool> SaveAsync(ConfigurationDraft draft, ConfigurationPart part)
        { editor.Save(draft, part); return Task.FromResult(true); }
        public Task<CloudProjectOnboarding> InspectNewProjectAsync(ProjectDefinition project, CancellationToken token) => throw new AssertFailedException();
        public Task<CloudFolderListing> BrowseCloudAsync(string? folderId) => throw new AssertFailedException();
        public Task<PreviewReport> PreviewAsync(ConfigurationDraft draft, string? destination = null,
            CancellationToken cancellationToken = default, IProgress<BackupProgress>? progress = null) => throw new AssertFailedException();
    }

    [TestMethod]
    [DataRow("zh-CN")]
    [DataRow("en-US")]
    public async Task ProjectOptionDefaultsOffAndCancelSaveAndReopenKeepItsScope(string language) => await OnSta(() =>
    {
        using var ws = new TestWorkspace();
        UiText.SetLanguage(language);
        var paths = Paths(ws);
        var editor = new ConfigurationEditor(paths, ws.Device); var presenter = new Presenter(editor);
        var original = File.ReadAllText(paths.ProjectConfig);
        using (var added = new ProjectEditorDialog(presenter, editor.Load(), null, "OneDrive/"))
            Assert.IsFalse(Choice(added).Checked, "New projects must default to upload-only automatic work.");
        using (var global = new ProjectEditorDialog(presenter, editor.Load(), null, null, global: true))
            Assert.IsEmpty(global.Controls.Find("AutomaticBidirectional", true), "The option belongs to one project.");
        using (var canceled = new ProjectEditorDialog(presenter, editor.Load(), 0, "OneDrive/"))
        {
            canceled.Show(); var choice = Choice(canceled);
            Assert.IsFalse(choice.Checked, "Projects without the optional field default off.");
            choice.Checked = true;
            ((Button)canceled.Controls.Find("CancelDraftOperation", true).Single()).PerformClick();
        }
        Assert.AreEqual(original, File.ReadAllText(paths.ProjectConfig));
        using (var enabled = new ProjectEditorDialog(presenter, editor.Load(), 0, "OneDrive/"))
        {
            enabled.Show();
            var choice = Choice(enabled); choice.Checked = true;
            Assert.AreEqual(UiText.Text("自动双向备份"), choice.Text);
            Assert.AreEqual(choice.Text, choice.AccessibleName);
            foreach (var width in new[] { 650, 1000, 780 })
            {
                enabled.Size = new(width, 740); enabled.PerformLayout(); Application.DoEvents();
                var hint = enabled.Controls.Find("AutomaticBidirectionalHint", true).Single();
                Assert.IsLessThanOrEqualTo(choice.Parent!.ClientSize.Width, choice.Right);
                Assert.IsLessThanOrEqualTo(hint.Parent!.ClientSize.Width, hint.Right);
                Assert.IsFalse(string.IsNullOrWhiteSpace(hint.Text));
            }
            Capture(enabled, "automatic-project-option-" + language);
            ((Button)enabled.Controls.Find("保存配置", true).Single()).PerformClick();
            Assert.AreEqual(DialogResult.OK, enabled.DialogResult);
        }
        Assert.Contains(ws.Load().Projects.Single().ProjectId, ws.Load().AutomaticBidirectionalProjects);
        using (var reopened = new ProjectEditorDialog(presenter, editor.Load(), 0, "OneDrive/"))
        {
            reopened.Show(); Assert.IsTrue(Choice(reopened).Checked);
            UiText.SetLanguage(language == "zh-CN" ? "en-US" : "zh-CN");
            Assert.AreEqual(UiText.Text("自动双向备份"), Choice(reopened).Text);
            Choice(reopened).Checked = false;
            ((Button)reopened.Controls.Find("保存配置", true).Single()).PerformClick();
            Assert.AreEqual(DialogResult.OK, reopened.DialogResult);
        }
        Assert.IsEmpty(ws.Load().AutomaticBidirectionalProjects);
    });

    [TestMethod]
    [DataRow("zh-CN")]
    [DataRow("en-US")]
    public async Task ProjectListShowsEachLocalModeAndLeavesCloudOnlyModeEmpty(string language)
    {
        using var ws = new TestWorkspace();
        var enabledId = DeviceIdentity.Digest("two-way");
        var uploadId = DeviceIdentity.Digest("upload-only");
        ws.WriteProjectConfig(new { folders = new[]
        {
            new { id = enabledId, source = ws.Source, destination = "Projects/TwoWay", automaticBidirectional = true },
            new { id = uploadId, source = Path.Combine(ws.Root, "other-local"), destination = "Projects/Upload", automaticBidirectional = false }
        } });
        var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            Exception? failure = null;
            try
            {
                UiText.SetLanguage(language);
                var controller = new DesktopController(Paths(ws), (_, _) => Task.FromResult<IReadOnlyList<CloudProjectTarget>>([]));
                using var form = new MainForm(controller);
                async Task CheckAsync()
                {
                    try
                    {
                        await controller.RefreshAsync(); await form.NavigateAsync("备份项目");
                        var grid = (DataGridView)form.Controls.Find("ProjectGrid", true).Single();
                        var rows = grid.Rows.Cast<DataGridViewRow>().Select(r => (ProjectRow)r.DataBoundItem!).ToArray();
                        Assert.AreEqual(UiText.Text("自动双向"), rows.Single(r => r.ProjectId == enabledId).DisplayAutomaticMode);
                        Assert.AreEqual(UiText.Text("自动上传"), rows.Single(r => r.ProjectId == uploadId).DisplayAutomaticMode);
                        var column = grid.Columns.Cast<DataGridViewColumn>().Single(c => c.DataPropertyName == "DisplayAutomaticMode");
                        Assert.AreEqual(UiText.Text("自动模式"), column.HeaderText);
                        var merged = ProjectListPresentation.Merge(rows, [new("Projects/Remote", "remote-folder", DeviceIdentity.Digest("remote"))]);
                        Assert.AreEqual("—", merged.Single(r => !r.IsLocal).DisplayAutomaticMode);
                        form.Size = form.MinimumSize; form.PerformLayout();
                        Capture(form, "automatic-project-list-" + language);
                    }
                    catch (Exception ex) { failure = ex; }
                    finally
                    {
                        try { form.ClosePermanently(); }
                        catch (Exception ex)
                        {
                            failure = failure is null ? ex : new AggregateException(failure, ex);
                            Application.ExitThread();
                        }
                    }
                }
                form.Shown += async (_, _) => await CheckAsync();
                Application.Run(form);
            }
            catch (Exception ex) { failure = failure is null ? ex : new AggregateException(failure, ex); }
            finally
            {
                try { UiText.SetLanguage("zh-CN"); }
                catch (Exception ex) { failure = failure is null ? ex : new AggregateException(failure, ex); }
                if (failure is null) done.TrySetResult();
                else done.TrySetException(failure);
            }
        }) { IsBackground = true };
        thread.SetApartmentState(ApartmentState.STA); thread.Start();
        try { await done.Task.WaitAsync(TimeSpan.FromSeconds(30)); }
        finally { Assert.IsTrue(thread.Join(TimeSpan.FromSeconds(5)), "The project-list UI thread must exit before the next case starts."); }
    }

    [TestMethod]
    [DataRow("zh-CN")]
    [DataRow("en-US")]
    public void AutomaticRunDetailsDistinguishUploadAndDownload(string language)
    {
        UiText.SetLanguage(language);
        try
        {
            var report = new BackupReport("AutomaticBidirectional", DateTimeOffset.UtcNow, DateTimeOffset.UtcNow,
                [new("Demo", [new("upload.txt", FileAction.Copy, true) { Direction = SyncDirection.Backup },
                    new("download.txt", FileAction.Copy, true) { Direction = SyncDirection.Pull },
                    new("cloud-directory", FileAction.CreateDirectory, true) { Direction = SyncDirection.Backup },
                    new("local-directory", FileAction.CreateDirectory, true) { Direction = SyncDirection.Pull },
                    new("legacy.txt", FileAction.Copy, true)], [], new())]);
            var rows = ReportPresentation.Details(report);
            CollectionAssert.AreEqual(new[] { "上传", "下载", "创建云端目录", "创建本地目录", "复制" }.Select(key => UiText.Text(key)).ToArray(), rows.Select(r => r.DisplayAction).ToArray());
            Assert.AreEqual("自动双向备份", ReportPresentation.Mode(report.Mode));
        }
        finally { UiText.SetLanguage("zh-CN"); }
    }

    private static RuntimePaths Paths(TestWorkspace ws) => new(Path.Combine(ws.Config, "projects.json"), Path.Combine(ws.Config, "backup-settings.json"));
    private static CheckBox Choice(Form dialog) => (CheckBox)dialog.Controls.Find("AutomaticBidirectional", true).Single();
    private static void Capture(Form form, string name)
    {
        if (Environment.GetEnvironmentVariable("HARBORBACKUP_SCREENSHOT_DIR") is not { Length: > 0 } folder) return;
        Directory.CreateDirectory(folder);
        using var bitmap = new Bitmap(form.Width, form.Height); form.DrawToBitmap(bitmap, new Rectangle(Point.Empty, form.Size));
        bitmap.Save(Path.Combine(folder, name + ".png"));
    }
    private static async Task OnSta(Action action)
    {
        var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            Exception? failure = null;
            try { action(); }
            catch (Exception ex) { failure = ex; }
            finally
            {
                try { UiText.SetLanguage("zh-CN"); }
                catch (Exception ex) { failure = failure is null ? ex : new AggregateException(failure, ex); }
                if (failure is null) done.TrySetResult();
                else done.TrySetException(failure);
            }
        }) { IsBackground = true };
        thread.SetApartmentState(ApartmentState.STA); thread.Start();
        try { await done.Task.WaitAsync(TimeSpan.FromSeconds(30)); }
        finally { Assert.IsTrue(thread.Join(TimeSpan.FromSeconds(3)), "The project-editor UI thread must exit before the next case starts."); }
    }
}
