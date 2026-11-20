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
        public Task<CloudFolderListing> BrowseCloudAsync(string? folderId, CancellationToken token = default, bool refresh = false) => throw new AssertFailedException();
        public Task<PreviewReport> PreviewAsync(ConfigurationDraft draft, string? destination = null,
            CancellationToken cancellationToken = default, IProgress<BackupProgress>? progress = null) => throw new AssertFailedException();
    }

    [TestMethod]
    [DataRow("zh-CN")]
    [DataRow("en-US")]
    public async Task ProjectModesDefaultToUploadAndCancelSaveAndReopenKeepTheirScope(string language) => await OnSta(() =>
    {
        using var ws = new TestWorkspace();
        UiText.SetLanguage(language);
        var paths = Paths(ws);
        var editor = new ConfigurationEditor(paths, ws.Device); var presenter = new Presenter(editor);
        var original = File.ReadAllText(paths.ProjectConfig);
        using (var added = new ProjectEditorDialog(presenter, editor.Load(), null, "OneDrive/"))
            Assert.AreEqual(0, Choice(added).SelectedIndex, "New projects must default to upload-only automatic work.");
        using (var global = new ProjectEditorDialog(presenter, editor.Load(), null, null, global: true))
            Assert.IsEmpty(global.Controls.Find("AutomaticBackupMode", true), "The option belongs to one project.");
        using (var canceled = new ProjectEditorDialog(presenter, editor.Load(), 0, "OneDrive/"))
        {
            canceled.Show(); var choice = Choice(canceled);
            Assert.AreEqual(0, choice.SelectedIndex, "Projects without the optional field default to upload.");
            choice.SelectedIndex = 2;
            ((Button)canceled.Controls.Find("CancelDraftOperation", true).Single()).PerformClick();
        }
        Assert.AreEqual(original, File.ReadAllText(paths.ProjectConfig));
        foreach (var selected in new[] { 1, 2, 0 })
        {
            UiText.SetLanguage(language);
            using (var dialog = new ProjectEditorDialog(presenter, editor.Load(), 0, "OneDrive/"))
            {
                dialog.Show();
                var choice = Choice(dialog); choice.SelectedIndex = selected;
                Assert.AreEqual(ComboBoxStyle.DropDownList, choice.DropDownStyle);
                Assert.AreEqual(UiText.Text("自动备份模式"), choice.AccessibleName);
                Assert.IsEmpty(dialog.Controls.Find("ResolvedTarget", true));
                foreach (var width in new[] { 650, 1000, 780 })
                {
                    dialog.Size = new(width, 740); dialog.PerformLayout(); Application.DoEvents();
                    var hint = dialog.Controls.Find("AutomaticBackupModeHint", true).Single();
                    var frame = dialog.Controls.Find("AutomaticBackupModeFrame", true).Single();
                    var card = frame.Parent!;
                    Assert.IsGreaterThanOrEqualTo(card.Padding.Left, frame.Left);
                    Assert.IsLessThanOrEqualTo(card.ClientSize.Width - card.Padding.Right, frame.Right);
                    var optionWidth = choice.Items.Cast<string>().Max(text => TextRenderer.MeasureText(text, choice.Font).Width);
                    Assert.IsGreaterThanOrEqualTo(optionWidth + SystemInformation.VerticalScrollBarWidth, choice.Parent!.ClientSize.Width,
                        "The dropdown must leave enough visible width for every mode and its arrow.");
                    Assert.IsLessThanOrEqualTo(hint.Parent!.ClientSize.Width, hint.Right);
                    Assert.AreEqual(UiText.Text(selected switch
                    {
                        1 => "自动上传本地更新、下载云端更新。", 2 => "下载云端更新，不上传本地业务文件。",
                        _ => "仅将本地更新上传到 OneDrive。"
                    }), hint.Text);
                }
                Capture(dialog, $"automatic-project-mode-{selected}-{language}");
                ((Button)dialog.Controls.Find("保存配置", true).Single()).PerformClick();
                Assert.AreEqual(DialogResult.OK, dialog.DialogResult);
            }
            var config = ws.Load();
            Assert.AreEqual((AutomaticBackupMode)selected, config.GetAutomaticBackupMode(config.Projects.Single().ProjectId));
            var saved = System.Text.Json.Nodes.JsonNode.Parse(File.ReadAllText(paths.ProjectConfig))!["folders"]![0]!;
            Assert.IsNull(saved["automaticBidirectional"]);
            Assert.AreEqual(selected switch { 1 => "bidirectional", 2 => "download", _ => "upload" }, saved["automaticBackupMode"]!.GetValue<string>());
            using var reopened = new ProjectEditorDialog(presenter, editor.Load(), 0, "OneDrive/");
            reopened.Show(); Assert.AreEqual(selected, Choice(reopened).SelectedIndex);
            UiText.SetLanguage(UiText.Language == "zh-CN" ? "en-US" : "zh-CN");
            Assert.AreEqual(selected, Choice(reopened).SelectedIndex, "Changing language must keep the chosen direction.");
            Assert.AreEqual(UiText.Text(selected switch { 1 => "双向备份", 2 => "仅下载", _ => "仅上传" }), Choice(reopened).Text);
        }
    });

    [TestMethod]
    [DataRow(false, 0)]
    [DataRow(true, 1)]
    public async Task LegacyBooleanModeOpensAsTheCorrespondingChoice(bool bidirectional, int expected) => await OnSta(() =>
    {
        using var ws = new TestWorkspace();
        ws.WriteProjectConfig(new { folders = new[] { new { source = ws.Source, destination = "Projects/Legacy", automaticBidirectional = bidirectional } } });
        var editor = new ConfigurationEditor(Paths(ws), ws.Device);
        using var dialog = new ProjectEditorDialog(new Presenter(editor), editor.Load(), 0, "OneDrive/");
        Assert.AreEqual(expected, Choice(dialog).SelectedIndex);
    });

    [TestMethod]
    [DataRow("zh-CN")]
    [DataRow("en-US")]
    public async Task ProjectListShowsEachLocalModeAndLeavesCloudOnlyModeEmpty(string language)
    {
        using var ws = new TestWorkspace();
        var enabledId = DeviceIdentity.Digest("two-way");
        var uploadId = DeviceIdentity.Digest("upload-only");
        var downloadId = DeviceIdentity.Digest("download-only");
        ws.WriteProjectConfig(new { folders = new[]
        {
            new { id = enabledId, source = ws.Source, destination = "Projects/TwoWay", automaticBackupMode = "bidirectional" },
            new { id = uploadId, source = Path.Combine(ws.Root, "other-local"), destination = "Projects/Upload", automaticBackupMode = "upload" },
            new { id = downloadId, source = Path.Combine(ws.Root, "download-local"), destination = "Projects/Download", automaticBackupMode = "download" }
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
                        Assert.AreEqual(UiText.Text("双向备份"), rows.Single(r => r.ProjectId == enabledId).DisplayAutomaticMode);
                        Assert.AreEqual(UiText.Text("仅上传"), rows.Single(r => r.ProjectId == uploadId).DisplayAutomaticMode);
                        Assert.AreEqual(UiText.Text("仅下载"), rows.Single(r => r.ProjectId == downloadId).DisplayAutomaticMode);
                        var column = grid.Columns.Cast<DataGridViewColumn>().Single(c => c.DataPropertyName == "DisplayAutomaticMode");
                        Assert.AreEqual(UiText.Text("备份模式"), column.HeaderText);
                        Assert.HasCount(6, grid.Columns);
                        Assert.IsFalse(grid.Columns.Cast<DataGridViewColumn>().Any(c => c.DataPropertyName == "Target"));
                        Assert.AreEqual(DataGridViewAutoSizeRowsMode.None, grid.AutoSizeRowsMode);
                        Assert.AreEqual(DataGridViewTriState.False, grid.DefaultCellStyle.WrapMode);
                        Assert.AreEqual(DataGridViewTriState.False, grid.ColumnHeadersDefaultCellStyle.WrapMode);
                        var merged = ProjectListPresentation.Merge(rows, [new("Projects/Remote", "remote-folder", DeviceIdentity.Digest("remote"))]);
                        Assert.AreEqual("—", merged.Single(r => !r.IsLocal).DisplayAutomaticMode);
                        var now = DateTimeOffset.UtcNow;
                        rows[0] = rows[0] with { Health = new ProjectHealthSnapshot("binding", now.AddHours(-30), now.AddHours(-1),
                            123456, now.AddHours(-25), true, "Archive", 7654321), NeedsInitialization = false };
                        grid.DataSource = rows;
                        Application.DoEvents();
                        var height = grid.Rows[0].Height;
                        foreach (var size in new[] { form.MinimumSize, new Size(1320, 860) })
                        {
                            form.Size = size; form.PerformLayout(); Application.DoEvents();
                            Assert.AreEqual(height, grid.Rows[0].Height, "Protection details must not grow project rows when resizing.");
                            Assert.DoesNotContain('\n', rows[0].DisplayProtection);
                        }
                        using (var details = new ProjectStatusDialog(rows[0]))
                        {
                            details.Show(form);
                            var content = details.Controls.Find("ProjectStatusDetails", true).Single();
                            Assert.IsTrue(content.Visible);
                            Assert.IsGreaterThan(150, content.Height, "Status details must be laid out in the dialog body.");
                            var text = content.Text;
                            StringAssert.Contains(text, rows[0].DisplayProtectionDetails.ReplaceLineEndings(Environment.NewLine));
                            StringAssert.Contains(text, rows[0].DisplaySource);
                            StringAssert.Contains(text, rows[0].Target);
                            Capture(details, "automatic-project-status-" + language);
                        }
                        form.Size = form.MinimumSize; form.PerformLayout(); Application.DoEvents();
                        Capture(form, "automatic-project-list-" + language);
                        grid.CurrentCell = grid.Rows.Cast<DataGridViewRow>().Single(r => ((ProjectRow)r.DataBoundItem!).ProjectId == enabledId).Cells[0];
                        var pause = (Button)form.Controls.Find("PauseSelectedProject", true).Single();
                        Assert.IsTrue(pause.Enabled);
                        Assert.AreEqual(UiText.Text("暂停选中备份"), pause.Text);
                        foreach (var paused in new[] { true, false })
                        {
                            pause.PerformClick();
                            var deadline = DateTime.UtcNow.AddSeconds(10);
                            while (controller.State.Configuration!.IsProjectPaused(enabledId) != paused || controller.Busy)
                            {
                                if (DateTime.UtcNow >= deadline) Assert.Fail("Project pause did not finish saving.");
                                await Task.Delay(20);
                            }
                            Assert.AreEqual(paused, ws.Load().IsProjectPaused(enabledId));
                            Assert.IsFalse(ws.Load().IsProjectPaused(uploadId));
                            Assert.AreEqual(UiText.Text(paused ? "恢复选中备份" : "暂停选中备份"), pause.Text);
                            Assert.AreEqual(UiText.Text(paused ? "项目备份已暂停" : "双向备份"),
                                controller.State.Projects.Single(r => r.ProjectId == enabledId).DisplayAutomaticMode);
                            Application.DoEvents();
                            var available = pause.ClientSize.Width - pause.Padding.Horizontal;
                            Assert.IsLessThanOrEqualTo(available, TextRenderer.MeasureText(pause.Text, pause.Font).Width,
                                "The project pause caption must fit at minimum window size.");
                            Capture(form, $"project-pause-{paused}-{language}");
                        }
                        var restarted = new DesktopController(Paths(ws));
                        await restarted.RefreshAsync();
                        Assert.IsFalse(restarted.State.Projects.Single(r => r.ProjectId == enabledId).BackupPaused);
                        grid.DataSource = ProjectListPresentation.Merge(controller.State.Projects,
                            [new("Projects/Remote", "remote-folder", DeviceIdentity.Digest("remote"))]);
                        grid.CurrentCell = grid.Rows.Cast<DataGridViewRow>().Single(r => !((ProjectRow)r.DataBoundItem!).IsLocal).Cells[0];
                        Assert.IsFalse(pause.Enabled, "Cloud-only projects have no local pause policy.");
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
    private static ComboBox Choice(Form dialog) => (ComboBox)dialog.Controls.Find("AutomaticBackupMode", true).Single();
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
