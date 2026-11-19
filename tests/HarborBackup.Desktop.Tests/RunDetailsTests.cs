using System.Text.Json;
using HarborBackup.Core.Domain;
using HarborBackup.Desktop.Controllers;
using HarborBackup.Desktop.Localization;
using HarborBackup.Desktop.Presentation;
using HarborBackup.Desktop.Views;
using HarborBackup.Desktop.Views.Dialogs;
using HarborBackup.Windows.Integration;
using HarborBackup.Windows.Persistence;

namespace HarborBackup.Windows.Tests;

[TestClass]
[DoNotParallelize]
public sealed class RunDetailsTests
{
    [TestMethod]
    [DataRow("zh-CN")]
    [DataRow("en-US")]
    public void PersistenceWarningShowsSeparatelyFromCompletedTransfer(string language)
    {
        UiText.SetLanguage(language);
        var report = new BackupReport("Backup", DateTimeOffset.UtcNow, DateTimeOffset.UtcNow,
            [new("Demo", [new("a.txt", FileAction.Copy, true)], [], new())])
        { CloudUploadStatus = "CloudOperationsConfirmed", Diagnostics = [new("RunStatusWriteFailed", "synthetic I/O failure", false)] };
        var rows = ReportPresentation.Details(report);
        Assert.HasCount(2, rows);
        Assert.IsFalse(rows[0].Attention);
        Assert.IsTrue(rows[1].SystemRow && rows[1].Attention);
        Assert.AreEqual(UiText.Text("RunStatusWriteFailed"), rows[1].DisplayAction);
        Assert.Contains("synthetic I/O failure", rows[1].DisplayDetails);
        Assert.AreEqual(2, report.ExitCode);
        Assert.AreEqual("CloudOperationsConfirmed", report.CloudUploadStatus);
    }

    [TestMethod]
    [DataRow(1f)]
    [DataRow(1.75f)]
    [DataRow(2f)]
    public async Task SidebarResizesWithAlignedBrandAndNoToggle(float scale)
    {
        UiText.SetLanguage("zh-CN");
        using var ws = new TestWorkspace();
        ws.WriteProjectConfig(new { folders = Enumerable.Range(1, 6).Select(i => new
        {
            id = DeviceIdentity.Digest("demo" + i),
            source = Directory.CreateDirectory(Path.Combine(ws.Source, "Demo" + i)).FullName,
            destination = "Projects/Demo" + i
        }).ToArray() });
        var paths = new RuntimePaths(Path.Combine(ws.Config, "projects.json"), Path.Combine(ws.Config, "backup-settings.json"));
        await OnUiThread(async () =>
        {
            var controller = new DesktopController(paths); await controller.RefreshAsync();
            using var form = new MainForm(controller);
            form.Show(); await form.NavigateAsync("概览");
            var cards = form.Controls.Find("OverviewCards", true).Single();
            form.Size = new(1000, 660); form.PerformLayout();
            var compactHeight = cards.Height;
            Assert.IsLessThanOrEqualTo(80 * form.DeviceDpi / 96, compactHeight);
            var overview = (DataGridView)form.Controls.Find("OverviewGrid", true).Single();
            Assert.AreEqual(6, overview.RowCount);
            Assert.IsGreaterThanOrEqualTo(4, overview.DisplayedRowCount(false));
            Assert.IsTrue(cards.Controls.Cast<Control>().All(card => card.Bottom <= cards.Height - cards.Padding.Bottom));
            if (Environment.GetEnvironmentVariable("HARBORBACKUP_SCREENSHOT_DIR") is { } compactFolder)
            {
                Directory.CreateDirectory(compactFolder);
                using var bitmap = new Bitmap(form.Width, form.Height);
                form.DrawToBitmap(bitmap, new Rectangle(Point.Empty, form.Size));
                bitmap.Save(Path.Combine(compactFolder, "overview-compact.png"));
            }
            Assert.IsFalse(cards.Controls.Cast<Control>().SelectMany(c => c.Controls.Cast<Control>()).OfType<Label>()
                .Any(label => label.Font.Size > 13));
            form.Size = new(1500, 1000); form.PerformLayout();
            Assert.IsGreaterThan(compactHeight, cards.Height);
            Assert.IsLessThanOrEqualTo(96 * form.DeviceDpi / 96, cards.Height);
            Assert.IsFalse(overview.Parent!.Controls.Cast<Control>().OfType<Label>().Any(l => l.Text.StartsWith("备份项目")));
            form.Size = new(1120, 760);
            form.Scale(new SizeF(scale, scale)); form.PerformLayout();
            var sidebar = form.Controls.Find("NavigationSidebar", true).Single();
            Assert.IsEmpty(form.Controls.Find("ToggleNavigation", true));
            var icon = form.Controls.Find("SidebarBrandIcon", true).Single();
            var title = (Label)form.Controls.Find("SidebarBrandName", true).Single();
            Assert.AreEqual(icon.Top + icon.Height / 2, title.Top + title.Height / 2);
            Assert.AreEqual(ContentAlignment.MiddleLeft, title.TextAlign);
            var initialWidth = sidebar.Width;
            form.SidebarWidth = 64;
            Assert.AreEqual(64, form.SidebarWidth);
            Assert.IsLessThan(initialWidth, sidebar.Width);
            foreach (var key in new[] { "概览", "备份项目", "运行记录", "待处理问题", "设置" })
            {
                var button = (HarborBackup.Desktop.Styling.SurfaceButton)form.Controls.Find("Navigate" + key, true).Single();
                Assert.IsTrue(button.IconOnly);
                Assert.AreEqual(UiText.Text(key), button.AccessibleName);
            }
            await form.NavigateAsync("设置");
            var divider = (Splitter)form.Controls.Find("NavigationDivider", true).Single();
            divider.SplitPosition = (int)Math.Round(230 * form.DeviceDpi / 96d);
            Assert.AreEqual(230, form.SidebarWidth);
            Assert.AreEqual(icon.Top + icon.Height / 2, title.Top + title.Height / 2);
            form.SidebarWidth = 64; Assert.AreEqual(64, form.SidebarWidth);
            if (Environment.GetEnvironmentVariable("HARBORBACKUP_SCREENSHOT_DIR") is { } folder)
            {
                Directory.CreateDirectory(folder);
                using var bitmap = new Bitmap(form.Width, form.Height);
                form.DrawToBitmap(bitmap, new Rectangle(Point.Empty, form.Size));
                bitmap.Save(Path.Combine(folder, $"sidebar-collapsed-{scale}.png"));
            }
            form.SidebarWidth = 184; Assert.AreEqual(184, form.SidebarWidth);
            await form.NavigateAsync("概览");
            if (Environment.GetEnvironmentVariable("HARBORBACKUP_SCREENSHOT_DIR") is { } previewFolder)
            {
                Directory.CreateDirectory(previewFolder);
                using var bitmap = new Bitmap(form.Width, form.Height);
                form.DrawToBitmap(bitmap, new Rectangle(Point.Empty, form.Size));
                bitmap.Save(Path.Combine(previewFolder, $"sidebar-brand-{scale}.png"));
            }
            var store = new HarborBackup.Desktop.Lifecycle.DesktopPreferencesStore(paths);
            store.Read(); store.Save(new(SidebarWidth: 64));
            Assert.AreEqual(64, store.Read().SidebarWidth);
            form.ClosePermanently();
        });
    }

    [TestMethod]
    [DataRow(1f)]
    [DataRow(1.75f)]
    [DataRow(2f)]
    public async Task AllProjectsKeepsMultipleRowsVisibleAfterScaling(float scale)
    {
        await OnUiThread(async () =>
        {
            UiText.SetLanguage("zh-CN");
            var rows = Enumerable.Range(0, 1410).Select(i => new DetailRow("Project" + i / 235,
                "file" + i + ".docx", "延期处理", "PendingFileOperation", true)).ToArray();
            using var dialog = new RunDetailsDialog(rows, summary: "共 6 个项目", logPath: "synthetic-history.json");
            dialog.Show(); await Task.Delay(20);
            dialog.Scale(new SizeF(scale, scale));
            dialog.Size = new((int)(960 * scale), (int)(640 * scale));
            dialog.PerformLayout();
            var grid = (DataGridView)dialog.Controls.Find("RunDetailsGrid", true).Single();
            var full = (TextBox)dialog.Controls.Find("RunDetailsText", true).Single();
            var project = (ComboBox)dialog.Controls.Find("RunDetailsProject", true).Single();
            Assert.AreEqual(0, project.SelectedIndex);
            Assert.AreEqual(1410, grid.RowCount);
            Assert.AreEqual(6, grid.Rows.Cast<DataGridViewRow>().Select(r => ((DetailRow)r.DataBoundItem!).Project).Distinct().Count());
            Assert.IsGreaterThanOrEqualTo(4, grid.DisplayedRowCount(false), "All projects needs a usable table viewport.");
            Assert.IsGreaterThan(full.Height, grid.Height, "The table must receive more space than selected-row details.");
            Assert.IsGreaterThanOrEqualTo((grid.Height + full.Parent!.Height) * 65 / 100, grid.Height,
                "Selected-row details must not crowd out the all-projects table after scaling.");
            project.SelectedIndex = 6; Assert.AreEqual(235, grid.RowCount);
            project.SelectedIndex = 0; Assert.AreEqual(1410, grid.RowCount);
            grid.FirstDisplayedScrollingRowIndex = 1175;
            Assert.AreEqual("Project5", ((DetailRow)grid.Rows[grid.FirstDisplayedScrollingRowIndex].DataBoundItem!).Project);
            if (Environment.GetEnvironmentVariable("HARBORBACKUP_SCREENSHOT_DIR") is { } folder)
            {
                Directory.CreateDirectory(folder);
                using var bitmap = new Bitmap(dialog.Width, dialog.Height);
                dialog.DrawToBitmap(bitmap, new Rectangle(Point.Empty, dialog.Size));
                bitmap.Save(Path.Combine(folder, $"all-projects-scale-{scale}.png"));
            }
            dialog.Close();
        });
    }

    [TestMethod]
    [DataRow("zh-CN")]
    [DataRow("en-US")]
    public async Task BatchHistoryShowsAllProjectsAndDetailsOpensMatchingProject(string language)
    {
        using var ws = new TestWorkspace();
        var paths = new RuntimePaths(Path.Combine(ws.Config, "projects.json"), Path.Combine(ws.Config, "backup-settings.json"));
        var destinations = new[] { "Projects/示例一", "Projects/示例二", "Projects/示例三", "Projects/示例四" };
        var now = DateTimeOffset.UtcNow;
        var report = new BackupReport("Backup", now, now, destinations.Select((p, i) => new ProjectRun(p,
            [new("file" + (i + 1) + ".doc", i == 3 ? FileAction.Copy : FileAction.Equal, i == 3)], [], new())).ToArray());
        Directory.CreateDirectory(paths.Data.History);
        var log = Path.Combine(paths.Data.History, "batch.json"); File.WriteAllText(log, JsonSerializer.Serialize(report, AtomicJson.Options));
        await OnUiThread(async () =>
        {
            UiText.SetLanguage(language);
            var controller = new DesktopController(paths); await controller.RefreshAsync(true);
            using var form = new MainForm(controller);
            form.Show(); await form.NavigateAsync("运行记录");
            var grid = (DataGridView)form.Controls.Find("HistoryGrid", true).Single();
            Assert.AreEqual("ProjectNames", grid.Columns[3].DataPropertyName);
            Assert.AreEqual(UiText.Text("项目"), grid.Columns[3].HeaderText);
            var displayed = grid.Rows[0].Cells[3].FormattedValue!.ToString()!;
            StringAssert.Contains(displayed, "4");
            foreach (var project in destinations) StringAssert.Contains(displayed, project.Split('/').Last());
            Assert.DoesNotContain(ws.Root, displayed);
            void Capture(Form target, string name)
            {
                if (Environment.GetEnvironmentVariable("HARBORBACKUP_SCREENSHOT_DIR") is not { } folder) return;
                Directory.CreateDirectory(folder);
                using var bitmap = new System.Drawing.Bitmap(target.Width, target.Height);
                target.DrawToBitmap(bitmap, new System.Drawing.Rectangle(0, 0, bitmap.Width, bitmap.Height));
                bitmap.Save(Path.Combine(folder, name + "-" + language + ".png"));
            }
            Capture(form, "history-projects");
            var filter = (TextBox)form.Controls.Find("HistoryFilter", true).Single(); filter.Text = "示例四";
            Exception? failure = null;
            using var timer = new System.Windows.Forms.Timer { Interval = 50 };
            timer.Tick += (_, _) =>
            {
                var dialog = Application.OpenForms.OfType<RunDetailsDialog>().SingleOrDefault();
                if (dialog is null) return;
                timer.Stop();
                try
                {
                    var project = (ComboBox)dialog.Controls.Find("RunDetailsProject", true).Single();
                    Assert.AreEqual(destinations[3], project.SelectedItem);
                    var details = (DataGridView)dialog.Controls.Find("RunDetailsGrid", true).Single();
                    Assert.AreEqual(1, details.RowCount);
                    Assert.AreEqual("file4.doc", ((DetailRow)details.Rows[0].DataBoundItem!).Path);
                    var path = (TextBox)dialog.Controls.Find("RunLogPath", true).Single();
                    Assert.IsTrue(path.ReadOnly); Assert.AreEqual(log, path.Text);
                    StringAssert.Contains(dialog.Controls.Find("RunDetailsSummary", true).Single().Text,
                        UiText.Text("共 {0} 个项目，完成写入 {1} 项，待处理 {2} 项", 4, 1, 0));
                    project.SelectedIndex = 0; Assert.AreEqual(4, details.RowCount);
                    project.SelectedIndex = 4;
                    dialog.Size = new(760, 500); dialog.PerformLayout();
                    Assert.IsGreaterThan(40, details.Height);
                    Capture(dialog, "run-projects");
                    var close = dialog.Controls.Find("CloseRunDetails", true).Single();
                    Assert.IsTrue(dialog.ClientRectangle.Contains(dialog.RectangleToClient(close.RectangleToScreen(close.ClientRectangle))));
                }
                catch (Exception ex) { failure = ex; }
                finally { dialog.Close(); }
            };
            timer.Start();
            ((Button)form.Controls.Find("查看选中详情", true).Single(c => c.Visible)).PerformClick();
            if (failure is not null) throw failure;
            Assert.AreEqual("示例四", filter.Text);
        });
    }

    [TestMethod]
    [DataRow("zh-CN", 760, 500)]
    [DataRow("en-US", 960, 640)]
    [DataRow("en-US", 760, 500)]
    public async Task DetailsFilterFullTextAndCloseFitWindow(string language, int width, int height)
    {
        await OnUiThread(async () =>
        {
            UiText.SetLanguage(language);
            var error = string.Concat(Enumerable.Repeat("完整错误 / full error ", 80));
            using var dialog = new RunDetailsDialog([new("Demo", "complete.txt", "内容一致", "未改写业务文件", false),
                new("Demo", "failed.txt", "失败", error, true)]);
            dialog.Size = new(width, height); dialog.Show(); await Task.Delay(20);
            var grid = (DataGridView)dialog.Controls.Find("RunDetailsGrid", true).Single();
            var full = (TextBox)dialog.Controls.Find("RunDetailsText", true).Single();
            var search = (TextBox)dialog.Controls.Find("RunDetailsFilter", true).Single();
            var attention = (CheckBox)dialog.Controls.Find("RunDetailsAttention", true).Single();
            Assert.AreEqual(2, grid.RowCount);
            attention.Checked = true;
            Assert.AreEqual(1, grid.RowCount); StringAssert.Contains(full.Text, error);
            Assert.IsTrue(full.ReadOnly); Assert.IsTrue(full.Multiline);
            search.Text = "not-found"; Assert.AreEqual(0, grid.RowCount); Assert.AreEqual("", full.Text);
            search.Text = "failed"; Assert.AreEqual(1, grid.RowCount);
            var close = (Button)dialog.Controls.Find("CloseRunDetails", true).Single();
            foreach (var control in new Control[] { search, grid, full, close })
                Assert.IsTrue(dialog.ClientRectangle.Contains(dialog.RectangleToClient(control.RectangleToScreen(control.ClientRectangle))), control.Name);
            Assert.IsGreaterThan(40, grid.Height);
            Assert.IsGreaterThan(dialog.ClientSize.Width - 60, grid.Width, "The table must fill the available width.");
            var gridBounds = dialog.RectangleToClient(grid.RectangleToScreen(grid.ClientRectangle));
            var textBounds = dialog.RectangleToClient(full.RectangleToScreen(full.ClientRectangle));
            var closeBounds = dialog.RectangleToClient(close.RectangleToScreen(close.ClientRectangle));
            Assert.IsGreaterThanOrEqualTo(gridBounds.Bottom, textBounds.Top, "Full text belongs below the table.");
            Assert.IsGreaterThanOrEqualTo(textBounds.Bottom, closeBounds.Top, "Close must stay below the full text.");
            Assert.IsLessThan(90, dialog.ClientSize.Height - closeBounds.Bottom, "Close must remain in the footer.");
            if (Environment.GetEnvironmentVariable("HARBORBACKUP_SCREENSHOT_DIR") is { } folder)
            {
                Directory.CreateDirectory(folder);
                using var bitmap = new System.Drawing.Bitmap(dialog.Width, dialog.Height);
                dialog.DrawToBitmap(bitmap, new System.Drawing.Rectangle(0, 0, bitmap.Width, bitmap.Height));
                bitmap.Save(Path.Combine(folder, $"run-details-{language}-{width}.png"));
            }
            close.PerformClick(); Assert.IsFalse(dialog.Visible);
        });
    }

    [TestMethod]
    public async Task HistoricalDetailsDoNotNavigateOrReplaceCurrentIssues()
    {
        using var ws = new TestWorkspace();
        var paths = new RuntimePaths(Path.Combine(ws.Config, "projects.json"), Path.Combine(ws.Config, "backup-settings.json"));
        Directory.CreateDirectory(paths.Data.History);
        var now = DateTimeOffset.UtcNow;
        BackupReport Report(bool current) => new("Backup", now, now,
            [new("Projects/Demo", [new(current ? "current-issue.txt" : "old-success.txt", current ? FileAction.Deferred : FileAction.Equal, false,
                current ? "PendingFileOperation" : null)], [], new())]);
        File.WriteAllText(Path.Combine(paths.Data.History, "02.json"), JsonSerializer.Serialize(Report(true), AtomicJson.Options));
        File.WriteAllText(Path.Combine(paths.Data.History, "01.json"), JsonSerializer.Serialize(Report(false), AtomicJson.Options));
        await OnUiThread(async () =>
        {
            UiText.SetLanguage("zh-CN");
            var controller = new DesktopController(paths);
            await controller.RefreshAsync(true);
            using var form = new MainForm(controller);
            form.Show(); await form.NavigateAsync("运行记录");
            var grid = (DataGridView)form.Controls.Find("HistoryGrid", true).Single();
            var filter = (TextBox)form.Controls.Find("HistoryFilter", true).Single();
            filter.Text = "Demo";
            grid.CurrentCell = grid.Rows[1].Cells[0];
            var selection = grid.CurrentRow!.DataBoundItem;
            var pageTitle = form.Text;
            var currentDetails = controller.State.Details;
            Exception? failure = null;
            using var timer = new System.Windows.Forms.Timer { Interval = 50 };
            timer.Tick += (_, _) =>
            {
                var dialog = Application.OpenForms.OfType<RunDetailsDialog>().SingleOrDefault();
                if (dialog is null) return;
                timer.Stop();
                try
                {
                    Assert.AreSame(form, dialog.Owner);
                    var rows = (DataGridView)dialog.Controls.Find("RunDetailsGrid", true).Single();
                    Assert.AreEqual("old-success.txt", ((DetailRow)rows.Rows[0].DataBoundItem!).Path);
                }
                catch (Exception ex) { failure = ex; }
                finally { dialog.Close(); }
            };
            timer.Start();
            ((Button)form.Controls.Find("查看选中详情", true).Single(c => c.Visible)).PerformClick();
            if (failure is not null) throw failure;
            Assert.AreEqual(pageTitle, form.Text);
            Assert.AreSame(selection, grid.CurrentRow!.DataBoundItem);
            Assert.AreEqual("Demo", filter.Text);
            Assert.AreSame(currentDetails, controller.State.Details);
            await form.NavigateAsync("待处理问题");
            var issues = (DataGridView)form.Controls.Find("IssuesGrid", true).Single();
            Assert.AreEqual(1, issues.RowCount);
            Assert.AreEqual("current-issue.txt", ((DetailRow)issues.Rows[0].DataBoundItem!).Path);
            form.Close();
        });
    }

    [TestMethod]
    public async Task UnreadableHistoryHasFullErrorAndCanClose()
    {
        await OnUiThread(async () =>
        {
            using var dialog = new RunDetailsDialog(new HistoryRecord("missing.json", DateTimeOffset.UtcNow, "无法读取", 1, null, "Invalid report structure"));
            dialog.Show(); await Task.Delay(20);
            StringAssert.Contains(dialog.Controls.Find("RunDetailsText", true).Single().Text, "Invalid report structure");
            dialog.Close();
        });
    }

    private static async Task OnUiThread(Func<Task> action)
    {
        var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            using var host = new Form { ShowInTaskbar = false, WindowState = FormWindowState.Minimized };
            host.Shown += async (_, _) =>
            {
                try { await action(); done.TrySetResult(); }
                catch (Exception ex) { done.TrySetException(ex); }
                finally { host.Close(); }
            };
            Application.Run(host);
        }) { IsBackground = true };
        thread.SetApartmentState(ApartmentState.STA); thread.Start();
        await done.Task.WaitAsync(TimeSpan.FromSeconds(30));
        Assert.IsTrue(thread.Join(TimeSpan.FromSeconds(5)));
    }
}
