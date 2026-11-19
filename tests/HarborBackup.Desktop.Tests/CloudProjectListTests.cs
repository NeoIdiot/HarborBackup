using HarborBackup.Desktop.Controllers;
using HarborBackup.Desktop.Localization;
using HarborBackup.Desktop.Presentation;
using HarborBackup.Desktop.Views;
using HarborBackup.OneDrive.Transport;
using HarborBackup.Windows.Integration;
using HarborBackup.Windows.Persistence.Configuration;

namespace HarborBackup.Windows.Tests;

[TestClass]
[DoNotParallelize]
public sealed class CloudProjectListTests
{
    [TestMethod]
    public void MergeKeepsLocalMappingAndAddsCloudOnlyRowsWithoutDuplicates()
    {
        var rows = ProjectListPresentation.Merge(
            [new("Projects/Demo", @"D:\work", "OneDrive/Projects/Demo", "尚未运行", LocalStatus: "本地目录存在", ProjectId: DeviceIdentity.Digest("demo"))],
            [new("projects/demo", "a", DeviceIdentity.Digest("demo")), new("Projects/Remote", "b", DeviceIdentity.Digest("b"))]);
        Assert.HasCount(2, rows);
        Assert.IsTrue(rows[0].IsLocal);
        Assert.AreEqual(@"D:\work", rows[0].Source);
        Assert.AreEqual("a", rows[0].CloudFolderId);
        Assert.AreEqual("本地目录存在", rows[0].LocalStatus);
        Assert.IsFalse(rows[1].IsLocal);
        Assert.AreEqual("未关联本机目录", rows[1].LocalStatus);
        Assert.AreEqual("", rows[1].Source);
    }

    [TestMethod]
    public void PresenceSeparatesFolderMissingAndFile()
    {
        using var ws = new TestWorkspace();
        Assert.AreEqual("本地目录存在", ProjectListPresentation.LocalStatus(ws.Source));
        Assert.AreEqual("本地目录缺失", ProjectListPresentation.LocalStatus(Path.Combine(ws.Source, "absent")));
        var file = ws.Write(ws.Source, "file.txt", "content");
        Assert.AreEqual("本地路径不是目录", ProjectListPresentation.LocalStatus(file));
    }

    [TestMethod]
    public void SameNameAndFolderDoNotMergeDifferentProjectIds()
    {
        var first = DeviceIdentity.Digest("first"); var second = DeviceIdentity.Digest("second");
        var rows = ProjectListPresentation.Merge(
            [new("Projects/Demo", "local", "OneDrive/Projects/Demo", "尚未运行", CloudFolderId: "folder", ProjectId: first)],
            [new("Projects/Demo", "folder", second)]);
        Assert.HasCount(2, rows);
        CollectionAssert.AreEquivalent(new[] { first, second }, rows.Select(r => r.ProjectId).ToArray());
    }

    [TestMethod]
    public void FolderIdentityMergesRenamedCatalogEntriesWithoutChangingLocalMapping()
    {
        var rows = ProjectListPresentation.Merge(
            [new("New/Demo", "local", "OneDrive/New/Demo", "尚未运行", CloudFolderId: "same-folder", ProjectId: DeviceIdentity.Digest("same-folder"))],
            [new("Old/Demo", "same-folder", DeviceIdentity.Digest("same-folder")), new("New/Demo", "same-folder", DeviceIdentity.Digest("same-folder"))]);
        Assert.HasCount(1, rows);
        Assert.AreEqual("New/Demo", rows[0].Destination);
        Assert.AreEqual("local", rows[0].Source);
    }

    [TestMethod]
    public async Task RenamingSourceAndDestinationKeepsOneProjectAndOldRegistrationStaysSuppressedAfterRestart()
    {
        using var ws = new TestWorkspace(); var paths = Paths(ws);
        var evidence = ws.Write(ws.State, "recovery.json", "retain");
        var source = ws.Write(ws.Source, "a.txt", "original");
        await RunForm(paths, (_, _) => Task.FromResult<IReadOnlyList<CloudProjectTarget>>(
            [new("Projects/Demo", "old-folder", DeviceIdentity.Digest("demo")), new("Unrelated/Remote", "remote", DeviceIdentity.Digest("remote"))]), async (controller, form) =>
        {
            await form.NavigateAsync("备份项目");
            Assert.HasCount(2, Grid(form).Rows);
            var original = controller.State.Configuration!.Projects.Single();
            var draft = controller.Editor.Load();
            draft.Projects["folders"]![0]!["source"] = Path.Combine(ws.Root, "renamed-source");
            draft.Projects["folders"]![0]!["destination"] = "LabProjects/Demo";
            await new ConfigurationPresenter(controller).SaveAsync(draft, ConfigurationPart.Projects);
            await controller.RefreshAsync(true);
            Assert.HasCount(2, Grid(form).Rows);
            Assert.IsFalse(Grid(form).Rows.Cast<DataGridViewRow>().Any(r => ((ProjectRow)r.DataBoundItem!).Destination == "Projects/Demo"));
            Assert.AreEqual(original.TaskId, controller.State.Configuration!.Projects.Single().TaskId);
            Assert.IsFalse(Directory.Exists(Path.Combine(ws.Root, "renamed-source")));
            Assert.AreEqual("original", File.ReadAllText(source));
            Assert.AreEqual("retain", File.ReadAllText(evidence));
            Capture(form, "renamed-project");
        });
        await RunForm(paths, (_, _) => Task.FromResult<IReadOnlyList<CloudProjectTarget>>(
            [new("Projects/Demo", "old-folder", DeviceIdentity.Digest("demo"))]), async (_, form) =>
        {
            await form.NavigateAsync("备份项目");
            Assert.HasCount(1, Grid(form).Rows);
            Assert.AreEqual("LabProjects/Demo", ((ProjectRow)Grid(form).Rows[0].DataBoundItem!).Destination);
        });
    }

    [TestMethod]
    [DataRow(false, false)]
    [DataRow(true, false)]
    [DataRow(false, true)]
    public async Task RemovingAnyProjectUpdatesOnlyLocalConfigurationAndSurvivesCatalogRefresh(bool local, bool missingConfig)
    {
        using var ws = new TestWorkspace(); var paths = Paths(ws);
        if (missingConfig) { File.Delete(paths.ProjectConfig); File.Delete(paths.SystemConfig); }
        var evidence = ws.Write(ws.State, "recovery.json", "retain");
        var source = ws.Write(ws.Source, "a.txt", "original");
        var destination = local ? "Projects/Demo" : "Projects/Remote";
        var id = local ? "demo" : "remote";
        Func<CloudAccountSettings, CancellationToken, Task<IReadOnlyList<CloudProjectTarget>>> reader = (_, _) =>
            Task.FromResult<IReadOnlyList<CloudProjectTarget>>([new("Projects/Demo", "demo", DeviceIdentity.Digest("demo")), new("Projects/Remote", "remote", DeviceIdentity.Digest("remote"))]);
        await RunForm(paths, reader, async (controller, form) =>
        {
            await form.NavigateAsync("备份项目");
            var grid = Grid(form);
            grid.CurrentCell = grid.Rows.Cast<DataGridViewRow>().Single(r => ((ProjectRow)r.DataBoundItem!).Destination == destination).Cells[0];
            var more = Button(form, "ProjectMoreActions");
            Assert.IsTrue(more.Enabled);
            Assert.IsTrue(more.ContextMenuStrip!.Items["RemoveProject"]!.Enabled);
            Assert.AreEqual(local, more.ContextMenuStrip.Items[0].Enabled);
            var selected = (ProjectRow)grid.CurrentRow!.DataBoundItem!;
            // List management remains available when account authorization is unavailable.
            var store = new CloudAccountStore(paths.Data); var account = store.Read();
            store.Save(account with { RequiresAuthorization = true }, account);
            await controller.RefreshAsync(true);
            await form.RemoveProjectAsync(selected);
            Assert.IsFalse(grid.Rows.Cast<DataGridViewRow>().Any(r => ((ProjectRow)r.DataBoundItem!).Destination == destination));
            Assert.AreEqual("original", File.ReadAllText(source));
            Assert.AreEqual("retain", File.ReadAllText(evidence));
            Assert.IsTrue(controller.State.Configuration!.DismissedCloudProjects.Single().Matches(account, DeviceIdentity.Digest(id)));
            store.Save(account, store.Read());
            await controller.RefreshAsync(true);
            await form.NavigateAsync("备份项目");
            Assert.HasCount(1, grid.Rows);
        });
        await RunForm(paths, reader, async (controller, form) =>
        {
            await form.NavigateAsync("备份项目");
            Assert.HasCount(1, Grid(form).Rows);
            Assert.IsFalse(Grid(form).Rows.Cast<DataGridViewRow>().Any(r => ((ProjectRow)r.DataBoundItem!).Destination == destination));
            var saved = controller.State.Configuration!;
            Assert.HasCount(1, ProjectListPresentation.VisibleCloudProjects(saved,
                [new("Another/Name", id + "-replacement", DeviceIdentity.Digest(id + "-replacement"))]));
            Assert.HasCount(1, ProjectListPresentation.VisibleCloudProjects(saved with
                { CloudAccount = saved.CloudAccount! with { RepositoryId = Guid.NewGuid().ToString("N") } }, [new(destination, id, DeviceIdentity.Digest(id))]));
        });
    }

    [TestMethod]
    public async Task FreshDeviceListsCloudProjectsWithoutSavingConfigOrEnablingBackup()
    {
        using var ws = new TestWorkspace();
        var paths = Paths(ws);
        File.Delete(paths.ProjectConfig); File.Delete(paths.SystemConfig);
        var calls = 0;
        await RunForm(paths, (_, _) =>
        {
            Interlocked.Increment(ref calls);
            return Task.FromResult<IReadOnlyList<CloudProjectTarget>>([new("Projects/Remote", "remote", DeviceIdentity.Digest("remote"))]);
        }, async (controller, form) =>
        {
            await form.NavigateAsync("备份项目");
            var grid = Grid(form);
            Assert.HasCount(1, grid.Rows);
            var row = (ProjectRow)grid.Rows[0].DataBoundItem!;
            Assert.IsFalse(row.IsLocal);
            Assert.AreEqual("未关联本机目录", row.LocalStatus);
            Assert.IsTrue(Button(form, "取回云端项目…").Enabled);
            Assert.IsFalse(Button(form, "备份选中").Enabled);
            Assert.IsFalse(Button(form, "编辑选中").Enabled);
            Assert.IsFalse(Button(form, "立即备份").Enabled);
            await controller.RefreshAsync();
            await form.NavigateAsync("概览");
            await form.NavigateAsync("备份项目");
            Assert.AreEqual(1, calls, "Periodic refresh and revisiting must not repeatedly fetch the catalog.");
            Assert.IsFalse(File.Exists(paths.ProjectConfig));
            Assert.IsFalse(File.Exists(paths.SystemConfig));
            Capture(form, "cloud-only-projects");
        });
    }

    [TestMethod]
    public async Task LocalPresenceAndActionsFollowSelectedRow()
    {
        using var ws = new TestWorkspace();
        ws.WriteProjectConfig(new { folders = new[] {
            new { id = DeviceIdentity.Digest("demo"), source = ws.Source, destination = "Projects/Demo" },
            new { id = DeviceIdentity.Digest("missing"), source = Path.Combine(ws.Root, "missing"), destination = "Projects/Missing" }
        } });
        await RunForm(Paths(ws), (_, _) => Task.FromResult<IReadOnlyList<CloudProjectTarget>>(
            [new("projects/demo", "demo", DeviceIdentity.Digest("demo")), new("Projects/Missing", "missing", DeviceIdentity.Digest("missing")), new("Projects/Remote", "remote", DeviceIdentity.Digest("remote"))]),
            async (_, form) =>
            {
                await form.NavigateAsync("备份项目");
                var grid = Grid(form);
                Assert.HasCount(3, grid.Rows);
                var rows = grid.Rows.Cast<DataGridViewRow>().Select(r => (ProjectRow)r.DataBoundItem!).ToArray();
                Assert.AreEqual("本地目录存在", rows[0].LocalStatus);
                Assert.AreEqual("本地目录缺失", rows[1].LocalStatus);
                grid.CurrentCell = grid.Rows[0].Cells[0];
                Assert.IsTrue(Button(form, "备份选中").Enabled);
                grid.CurrentCell = grid.Rows[2].Cells[0];
                Assert.IsFalse(Button(form, "备份选中").Enabled);
                Assert.IsFalse(Button(form, "取回更新…").Enabled);
                Assert.IsTrue(Button(form, "取回云端项目…").Enabled);
                Capture(form, "mixed-projects");
            });
    }

    [TestMethod]
    public async Task FailedRefreshKeepsRowsAndAccountSwitchDropsOldCatalog()
    {
        using var ws = new TestWorkspace();
        var calls = 0;
        await RunForm(Paths(ws), (_, _) =>
        {
            if (Interlocked.Increment(ref calls) == 2) throw new IOException("Synthetic catalog failure");
            return Task.FromResult<IReadOnlyList<CloudProjectTarget>>([new("Projects/Remote", "remote", DeviceIdentity.Digest("remote"))]);
        }, async (controller, form) =>
        {
            await form.NavigateAsync("备份项目");
            Assert.HasCount(2, Grid(form).Rows);
            Button(form, "刷新云端项目").PerformClick();
            await WaitUntil(() => calls == 2 && !controller.Busy && Button(form, "刷新云端项目").Enabled);
            Assert.HasCount(2, Grid(form).Rows);
            Assert.AreEqual(UiText.Text("刷新失败，显示上次结果"), form.Controls.Find("CloudCatalogStatus", true).Single().Text);
            await controller.RefreshAsync(true);
            Assert.AreEqual(UiText.Text("刷新失败，显示上次结果"), form.Controls.Find("CloudCatalogStatus", true).Single().Text,
                "Rebinding rows must preserve the failed refresh state.");
            Button(form, "RetryCloudCatalog").PerformClick();
            await WaitUntil(() => calls == 3 && Button(form, "刷新云端项目").Enabled);
            Assert.AreEqual(UiText.Text("已刷新 · {0} 个可加入项目", 1), form.Controls.Find("CloudCatalogStatus", true).Single().Text);
            Assert.IsFalse(Button(form, "CloudCatalogDetails").Visible);
            var store = new CloudAccountStore(controller.Paths.Data);
            var previous = store.Read();
            store.Save(previous with { Enabled = false }, previous);
            await controller.RefreshAsync();
            Assert.HasCount(1, Grid(form).Rows);
            Assert.IsFalse(Button(form, "取回云端项目…").Enabled);
        });
    }

    [TestMethod]
    public async Task LoadingIsCancelableAndDoesNotBlockNavigation()
    {
        using var ws = new TestWorkspace();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await RunForm(Paths(ws), async (_, token) =>
        {
            entered.SetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, token);
            return [];
        }, async (controller, form) =>
        {
            var loading = form.NavigateAsync("备份项目");
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.IsTrue(Button(form, "停止加载").Visible);
            var strip = form.Controls.Find("CloudCatalogStrip", true).Single();
            Assert.IsTrue(strip.Contains(Button(form, "停止加载")));
            Capture(form, "catalog-loading");
            Assert.IsFalse(Button(form, "取回云端项目…").Enabled);
            Assert.IsFalse(controller.Busy, "Cloud catalog reads must not hold the file-operation coordinator.");
            Assert.IsTrue(Button(form, "编辑选中").Enabled);
            var draft = controller.Editor.Load(); draft.System["schedule"]!["intervalMinutes"] = 47;
            await new ConfigurationPresenter(controller).SaveAsync(draft, ConfigurationPart.System);
            Assert.AreEqual(47, controller.State.Configuration!.IntervalMinutes);
            await form.NavigateAsync("概览");
            await loading.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.IsFalse(controller.Busy);
            Assert.HasCount(1, Grid(form).Rows);
        });
    }

    [TestMethod]
    [DataRow("zh-CN")]
    [DataRow("en-US")]
    public async Task CompactCatalogFailureKeepsFullSelectableDetailsInSeparateDialog(string language)
    {
        UiText.SetLanguage(language);
        using var ws = new TestWorkspace();
        var message = "Synthetic download failure\r\n" + new string('x', 1500) + "\r\nEnd of diagnostic";
        await RunForm(Paths(ws), (_, _) => throw new IOException(message), async (_, form) =>
        {
            await form.NavigateAsync("备份项目");
            var status = (Label)form.Controls.Find("CloudCatalogStatus", true).Single();
            Assert.IsTrue(status.AutoEllipsis);
            Assert.IsGreaterThan(200, status.Width, status.Bounds.ToString());
            Assert.IsGreaterThanOrEqualTo(24, status.Height, status.Bounds.ToString());
            Assert.AreEqual(UiText.Text("云端项目加载失败"), status.Text);
            var strip = form.Controls.Find("CloudCatalogStrip", true).Single();
            Assert.IsLessThanOrEqualTo(32 * form.DeviceDpi / 96, strip.Height);
            Exception? dialogError = null;
            var inspected = false;
            using var timer = new System.Windows.Forms.Timer { Interval = 20 };
            timer.Tick += (_, _) =>
            {
                var dialog = Application.OpenForms.Cast<Form>().SingleOrDefault(f => f.Name == "CloudCatalogDiagnostic");
                if (dialog is null) return;
                timer.Stop();
                try
                {
                    var text = (TextBox)dialog.Controls.Find("DiagnosticText", true).Single();
                    Assert.IsTrue(text.ReadOnly && text.Multiline);
                    Assert.AreEqual(ScrollBars.Vertical, text.ScrollBars);
                    Assert.Contains("End of diagnostic", text.Text);
                    text.SelectAll(); Assert.AreEqual(text.Text, text.SelectedText);
                    inspected = true;
                }
                catch (Exception ex) { dialogError = ex; }
                finally { dialog.Close(); }
            };
            timer.Start(); Button(form, "CloudCatalogDetails").PerformClick();
            if (dialogError is not null) throw dialogError;
            Assert.IsTrue(inspected);
            form.Size = new(1066, 653); form.PerformLayout();
            Assert.IsLessThanOrEqualTo(Grid(form).PointToScreen(Point.Empty).Y,
                strip.PointToScreen(new Point(0, strip.Height)).Y);
            Assert.HasCount(1, Grid(form).Rows);
            Capture(form, "catalog-long-failure-" + language);
        });
    }

    [TestMethod]
    public async Task ReconnectingClearsOfflineCatalogAndRetriesWhilePageIsOpen()
    {
        using var ws = new TestWorkspace();
        var online = false;
        var reads = 0;
        await RunForm(Paths(ws), (_, _) =>
        {
            Interlocked.Increment(ref reads);
            if (!online) throw new CloudOfflineException();
            return Task.FromResult<IReadOnlyList<CloudProjectTarget>>([new("Projects/Remote", "remote", DeviceIdentity.Digest("remote"))]);
        }, async (controller, form) =>
        {
            await form.NavigateAsync("备份项目");
            var status = form.Controls.Find("CloudCatalogStatus", true).Single();
            Assert.AreEqual(UiText.Text("等待网络"), status.Text);
            Assert.IsTrue(controller.State.NetworkOffline);
            Assert.HasCount(1, Grid(form).Rows);
            Assert.AreEqual(1, reads);

            online = true;
            await controller.TickAsync();
            await WaitUntil(() => Grid(form).Rows.Count == 2);
            Assert.AreEqual(UiText.Text("已刷新 · {0} 个可加入项目", 1), status.Text);
            Assert.IsFalse(controller.State.NetworkOffline);
            Assert.AreEqual(2, reads);
        }, networkAvailable: () => online);
    }

    private static RuntimePaths Paths(TestWorkspace ws) => new(Path.Combine(ws.Config, "projects.json"), Path.Combine(ws.Config, "backup-settings.json"));
    private static DataGridView Grid(Form form) => (DataGridView)form.Controls.Find("ProjectGrid", true).Single();
    private static Button Button(Form form, string name) => (Button)form.Controls.Find(name, true).Single();
    private static async Task WaitUntil(Func<bool> ready)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        while (!ready()) await Task.Delay(20, timeout.Token);
    }
    private static void Capture(Form form, string name)
    {
        if (Environment.GetEnvironmentVariable("HARBORBACKUP_SCREENSHOT_DIR") is not { Length: > 0 } folder) return;
        Directory.CreateDirectory(folder);
        using var bitmap = new Bitmap(form.Width, form.Height);
        form.DrawToBitmap(bitmap, new Rectangle(Point.Empty, form.Size));
        bitmap.Save(Path.Combine(folder, name + ".png"));
    }
    private static async Task RunForm(RuntimePaths paths,
        Func<CloudAccountSettings, CancellationToken, Task<IReadOnlyList<CloudProjectTarget>>> reader,
        Func<DesktopController, MainForm, Task> test, Func<bool>? networkAvailable = null)
    {
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            try
            {
                var controller = new DesktopController(paths, reader, networkAvailable);
                using var form = new MainForm(controller);
                async Task CheckAsync()
                {
                    try { await controller.RefreshAsync(); await test(controller, form); completion.TrySetResult(); }
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
