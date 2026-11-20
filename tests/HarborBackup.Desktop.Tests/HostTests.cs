using System.Text.Json.Nodes;
using HarborBackup.Core.Domain;
using HarborBackup.Desktop;
using HarborBackup.Desktop.Controllers;
using HarborBackup.Desktop.Localization;
using HarborBackup.Desktop.Views;
using HarborBackup.Desktop.Lifecycle;
using HarborBackup.Desktop.Presentation;
using HarborBackup.Desktop.Views.Dialogs;
using HarborBackup.Desktop.Views.Pages;
using HarborBackup.Windows.Integration;
using HarborBackup.Windows.Persistence;

namespace HarborBackup.Windows.Tests;

[TestClass]
[DoNotParallelize]
public sealed class HostTests
{
    [TestMethod]
    public void RemovedDirectoryOverrideIsRejected()
    {
        Assert.Throws<ArgumentException>(() => Program.ParsePaths(["--onedrive-root", @"C:\old-sync"]));
    }
    [ClassInitialize]
    public static void Initialize(TestContext _)
    {
        Application.SetHighDpiMode(Environment.GetEnvironmentVariable("HARBORBACKUP_UI_DPI") == "96" ? HighDpiMode.DpiUnaware : HighDpiMode.PerMonitorV2);
        Application.EnableVisualStyles();
    }
    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public void LogsCannotWriteInsideEitherBusinessRoot(bool target)
    {
        using var ws = new TestWorkspace();
        ws.WriteSystemConfig(new { sync = new { enabled = true }, logging = new { directory = target ? ws.Target : ws.Source } });
        Assert.Throws<FormatException>(() => ws.Load());
        Assert.IsEmpty(Directory.GetFiles(ws.Source));
    }

    [TestMethod]
    public void RuntimeWritesCompatibleStatusAndDeviceLocalPause()
    {
        using var ws = new TestWorkspace();
        ws.Write(ws.Source, "a.txt", "first");
        var runtime = Runtime(ws);
        var config = runtime.Load();
        RunStatusStore.Write(runtime.Load(), new BackupReport("Backup", DateTimeOffset.UtcNow, DateTimeOffset.UtcNow,
            [new("Projects/Demo", [new("a.txt", FileAction.Copy, true)], [], new()) { ProjectId = config.Projects.Single().ProjectId }]) { CloudUploadStatus = "CloudCommitted" });
        var status = RunStatusStore.Read(config.Preview)!;
        var project = status["projects"]![0]!;
        Assert.AreEqual("CloudCommitted", status["cloudUploadStatus"]!.GetValue<string>());
        Assert.IsFalse(project.AsObject().ContainsKey("freeSpacePending"));
        var stamp = project["lastSuccessfulBackupAt"]!.GetValue<string>();
        var previous = JsonNode.Parse(File.ReadAllText(RunStatusStore.PathFor(config.Preview)))!;
        previous["customHistory"] = "retain me";
        File.WriteAllText(RunStatusStore.PathFor(config.Preview), previous.ToJsonString());
        var failing = new BackupReport("Backup", DateTimeOffset.UtcNow, DateTimeOffset.UtcNow,
            [new("Projects/Demo", [], [new("Injected", "failure")], new()) { ProjectId = config.Projects.Single().ProjectId }]);
        RunStatusStore.Write(config, failing);
        status = RunStatusStore.Read(config.Preview)!;
        Assert.AreEqual(stamp, status["projects"]![0]!["lastSuccessfulBackupAt"]!.GetValue<string>());
        Assert.AreEqual(1, status["projects"]![0]!["consecutiveFailures"]!.GetValue<int>());
        Assert.AreEqual("retain me", status["customHistory"]!.GetValue<string>());
        var before = File.ReadAllText(Path.Combine(ws.Config, "backup-settings.json"));
        var control = new DeviceControlStore(config.Preview);
        control.SetPaused(true);
        Assert.IsTrue(new DeviceControlStore(config.Preview).ReadPaused());
        control.SetPaused(false);
        Assert.IsFalse(control.ReadPaused());
        Assert.AreEqual(before, File.ReadAllText(Path.Combine(ws.Config, "backup-settings.json")));
        Assert.Throws<ArgumentException>(() => runtime.Execute(SyncDirection.Pull));
    }

    [TestMethod]
    public void CorruptStatusIsNeverReplacedByAnEmptyHistory()
    {
        using var ws = new TestWorkspace();
        var config = ws.Load();
        var path = RunStatusStore.PathFor(config.Preview);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        const string corrupt = "{\"schemaVersion\":2,\"projects\":[]}";
        File.WriteAllText(path, corrupt);
        Assert.Throws<FormatException>(() => RunStatusStore.Write(config, new("Recover", DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, [])));
        Assert.AreEqual(corrupt, File.ReadAllText(path));
    }

    [TestMethod]
    [DataRow(true)]
    [DataRow(false)]
    public async Task DesktopStartupCloseAndActivationShareOneWindow(bool startHidden)
    {
        using var ws = new TestWorkspace();
        ws.Write(ws.Source, "a.txt", "tray transfer");
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            try
            {
                using var context = new TrayContext(Paths(ws), startHidden: startHidden, preferencesPath: Path.Combine(ws.Config, "test-ui.json"));
                using var starter = new System.Windows.Forms.Timer { Interval = 50 };
                async Task CheckTrayAsync()
                {
                    Assert.IsTrue(context.IconVisible);
                    Assert.HasCount(startHidden ? 0 : 1, Application.OpenForms.Cast<Form>());
                    await Task.Delay(1200);
                    Assert.IsGreaterThan(0, context.UiTicks, "Tray timer must run without a main form.");
                    Assert.IsNotNull(context.Controller.State.Configuration);
                    context.ShowStatus();
                    Assert.HasCount(1, Application.OpenForms.Cast<Form>());
                    var originalWindow = context.Window;
                    var screenshotDirectory = Environment.GetEnvironmentVariable("HARBORBACKUP_SCREENSHOT_DIR");
                    if (!startHidden)
                    {
                        var presenter = new ConfigurationPresenter(context.Controller);
                        // Existing-project edits stay offline. New-project cloud onboarding has its own synthetic tests.
                        using var editor = new ProjectEditorDialog(presenter, await presenter.LoadAsync(), 0, ws.Cloud);
                        editor.Show(context.Window);
                        ((TextBox)editor.Controls.Find("SourcePath", true).Single()).Text = ws.Source;
                        ((TextBox)editor.Controls.Find("Destination", true).Single()).Text = "Projects/AddedFromUi";
                        if (screenshotDirectory is not null)
                        {
                            Directory.CreateDirectory(screenshotDirectory);
                            using var bitmap = new System.Drawing.Bitmap(editor.Width, editor.Height);
                            editor.DrawToBitmap(bitmap, new System.Drawing.Rectangle(0, 0, bitmap.Width, bitmap.Height));
                            bitmap.Save(Path.Combine(screenshotDirectory, "项目编辑.png"), System.Drawing.Imaging.ImageFormat.Png);
                        }
                        ((Button)editor.Controls.Find("保存配置", true).Single()).PerformClick();
                        var deadline = DateTime.UtcNow.AddSeconds(10);
                        while (!editor.IsDisposed && editor.Visible && DateTime.UtcNow < deadline) await Task.Delay(50);
                        Assert.AreEqual(DialogResult.OK, editor.DialogResult);
                        Assert.HasCount(1, Runtime(ws).Load().Projects);
                        await context.Window!.NavigateAsync("设置");
                        var settings = (SettingsPage)context.Window.Controls.Find("SettingsPage", true).Single();
                        await settings.LoadDraftAsync(force: true);
                        ((NumericUpDown)settings.Controls.Find("IntervalMinutes", true).Single()).Value = 45;
                        ((NumericUpDown)settings.Controls.Find("LogRetentionDays", true).Single()).Value = 14;
                        ((NumericUpDown)settings.Controls.Find("HistoryRetentionDays", true).Single()).Value = 180;
                        ((CheckBox)settings.Controls.Find("UseMetadataCache", true).Single()).Checked = false;
                        ((Button)context.Window.Controls.Find("保存备份设置", true).Single()).PerformClick();
                        deadline = DateTime.UtcNow.AddSeconds(10);
                        while (settings.Dirty && DateTime.UtcNow < deadline) await Task.Delay(50);
                        Assert.IsFalse(settings.Dirty);
                        Assert.AreEqual(45, Runtime(ws).Load().IntervalMinutes);
                        var savedSettings = HarborBackup.Windows.Persistence.Configuration.BackupSettingsReader.Parse(File.ReadAllText(Paths(ws).SystemConfig));
                        Assert.AreEqual(14, savedSettings.LogRetentionDays);
                        Assert.AreEqual(180, savedSettings.HistoryRetentionDays);
                        Assert.IsFalse(((CheckBox)settings.Controls.Find("UseMetadataCache", true).Single()).Checked);
                        Assert.IsFalse(JsonNode.Parse(File.ReadAllText(Paths(ws).SystemConfig))!["sync"]!["hash"]!["useMetadataCache"]!.GetValue<bool>());
                    }
                    foreach (var page in new[] { "概览", "备份项目", "运行记录", "待处理问题", "设置" })
                    {
                        await context.Window!.NavigateAsync(page);
                        await Task.Delay(50);
                        if (screenshotDirectory is not null && !startHidden)
                        {
                            Directory.CreateDirectory(screenshotDirectory);
                            using var bitmap = new System.Drawing.Bitmap(context.Window.Width, context.Window.Height);
                            context.Window.DrawToBitmap(bitmap, new System.Drawing.Rectangle(0, 0, bitmap.Width, bitmap.Height));
                            bitmap.Save(Path.Combine(screenshotDirectory, page + ".png"), System.Drawing.Imaging.ImageFormat.Png);
                        }
                    }
                    Application.OpenForms[0]!.Close();
                    Assert.IsFalse(context.Window!.Visible);
                    context.RequestActivation();
                    await Task.Delay(100);
                    Assert.AreSame(originalWindow, context.Window);
                    Assert.IsTrue(context.Window.Visible);
                    foreach (var (page, name) in new[] { ("概览", "Navigate概览"), ("备份项目", "ProjectGrid"), ("设置", "CloudRoot") })
                    {
                        await context.Window.NavigateAsync(page);
                        var focused = context.Window.Controls.Find(name, true).Single();
                        focused.Focus();
                        var escape = Message.Create(focused.Handle, 0x0100, (IntPtr)Keys.Escape, IntPtr.Zero);
                        Assert.IsTrue(focused.PreProcessMessage(ref escape), "ESC must reach the main window from " + name);
                        Assert.IsFalse(context.Window.Visible);
                        Assert.IsFalse(context.Window.IsDisposed);
                        Assert.IsTrue(context.IconVisible);
                        context.ShowStatus();
                        Assert.AreSame(originalWindow, context.Window);
                    }
                    using (var details = new RunDetailsDialog([]))
                    using (var dismiss = new System.Windows.Forms.Timer { Interval = 25 })
                    {
                        Exception? dialogError = null;
                        dismiss.Tick += (_, _) =>
                        {
                            dismiss.Stop();
                            try
                            {
                                var focused = details.Controls.Find("RunDetailsFilter", true).Single();
                                focused.Focus();
                                var escape = Message.Create(focused.Handle, 0x0100, (IntPtr)Keys.Escape, IntPtr.Zero);
                                Assert.IsTrue(focused.PreProcessMessage(ref escape));
                            }
                            catch (Exception ex) { dialogError = ex; details.Close(); }
                        };
                        dismiss.Start(); details.ShowDialog(context.Window);
                        if (dialogError is not null) throw dialogError;
                        Assert.IsTrue(context.Window.Visible, "ESC in a dialog must leave its main window open.");
                        Assert.IsTrue(context.IconVisible);
                    }
                    var ticks = context.UiTicks;
                    await Task.Delay(1200);
                    Assert.IsGreaterThan(ticks, context.UiTicks);
                    Assert.IsTrue(context.IconVisible);
                    await context.ShutdownAsync();
                    Assert.IsFalse(context.IconVisible);
                }
                starter.Tick += async (_, _) =>
                {
                    starter.Stop();
                    try { await CheckTrayAsync(); }
                    catch (Exception ex) { completion.TrySetException(ex); await context.ShutdownAsync(); }
                };
                starter.Start();
                Application.Run(context);
                completion.TrySetResult();
            }
            catch (Exception ex) { completion.TrySetException(ex); }
        }) { IsBackground = true, Name = "HarborBackup tray integration test" };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        await completion.Task.WaitAsync(TimeSpan.FromSeconds(30));
        Assert.IsTrue(thread.Join(TimeSpan.FromSeconds(5)));
    }

    private static RuntimePaths Paths(TestWorkspace ws) => new(Path.Combine(ws.Config, "projects.json"),
        Path.Combine(ws.Config, "backup-settings.json"));
    private static BackupRuntime Runtime(TestWorkspace ws) => new(Paths(ws));

    [TestMethod]
    [DataRow("zh-CN")]
    [DataRow("en-US")]
    public async Task SettingsNavigationPerformance(string language)
    {
        using var ws = new TestWorkspace();
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
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
                        await controller.RefreshAsync();
                        await form.PreparePagesAsync();
                        var settings = (SettingsPage)form.Controls.Find("SettingsPage", true).Single();
                        var samples = new List<object>();
                        var blockingTimes = new List<double>();
                        var layouts = 0;
                        var viewportHeight = settings.Height;
                        settings.Layout += (_, _) => layouts++;
                        for (var pass = 0; pass < 4; pass++)
                        {
                            foreach (var page in new[] { "备份项目", "运行记录", "待处理问题", "设置", "概览" })
                            {
                            layouts = 0;
                            var clock = System.Diagnostics.Stopwatch.StartNew();
                            var navigation = form.NavigateAsync(page);
                            var synchronousMs = clock.Elapsed.TotalMilliseconds;
                            blockingTimes.Add(synchronousMs);
                            await navigation;
                            Assert.AreEqual(viewportHeight, settings.Height, "Navigation must not resize the content viewport when showing/hiding settings actions.");
                            samples.Add(new { pass, page, synchronousMs, totalMs = clock.Elapsed.TotalMilliseconds, layouts,
                                dpi = form.DeviceDpi, viewportHeight = settings.Height });
                            }
                        }
                        var folder = Environment.GetEnvironmentVariable("HARBORBACKUP_SCREENSHOT_DIR");
                        if (folder is not null)
                        {
                            Directory.CreateDirectory(folder);
                            File.WriteAllText(Path.Combine(folder, "navigation-performance.json"), System.Text.Json.JsonSerializer.Serialize(samples));
                            File.WriteAllText(Path.Combine(folder, "navigation-performance-" + language + ".json"), System.Text.Json.JsonSerializer.Serialize(samples));
                        }
                        Assert.IsTrue(blockingTimes.All(ms => ms < 150), "Prepared pages must switch without a perceptible loading pause; inspect navigation-performance.json for individual timings.");
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
                // Publish the result after the message loop and form disposal have finished.
                // The next language case must not touch controls still being torn down.
                if (failure is null) completion.TrySetResult();
                else completion.TrySetException(failure);
            }
        }) { IsBackground = true };
        thread.SetApartmentState(ApartmentState.STA); thread.Start();
        try { await completion.Task.WaitAsync(TimeSpan.FromSeconds(60)); }
        finally { Assert.IsTrue(thread.Join(TimeSpan.FromSeconds(5)), "The navigation test UI thread must exit before the next case starts."); }
    }

    [TestMethod]
    public void RoundedBorderHasEqualCoverageOnAllFourSides()
    {
        using var bitmap = new System.Drawing.Bitmap(161, 81);
        using var graphics = System.Drawing.Graphics.FromImage(bitmap);
        HarborBackup.Desktop.Styling.SurfacePanel.PaintSurface(graphics, bitmap.Size,
            HarborBackup.Desktop.Styling.AppStyle.Background, System.Drawing.Color.White,
            HarborBackup.Desktop.Styling.AppStyle.InputBorder, 4);
        for (var distance = 0; distance < 4; distance++)
        {
            var left = bitmap.GetPixel(distance, 40).ToArgb();
            Assert.AreEqual(left, bitmap.GetPixel(160 - distance, 40).ToArgb(), "Left/right border and halo must be symmetric.");
            Assert.AreEqual(left, bitmap.GetPixel(80, distance).ToArgb(), "Top border must have the same coverage.");
            Assert.AreEqual(left, bitmap.GetPixel(80, 80 - distance).ToArgb(), "Bottom border must have the same coverage.");
        }
    }

    [TestMethod]
    public async Task BackgroundRefreshKeepsStableRowsAndDetectsExternalConfigurationChanges()
    {
        using var ws = new TestWorkspace();
        var paths = Paths(ws);
        var controller = new DesktopController(paths);
        await controller.RefreshAsync();
        var originalRows = controller.State.Projects;
        var busyNotifications = 0;
        controller.Changed += () => { if (controller.State.Busy) busyNotifications++; };
        await controller.RefreshAsync();
        Assert.AreSame(originalRows, controller.State.Projects);
        Assert.AreEqual(0, busyNotifications, "Reading configuration must not announce a transient operation.");

        var configuration = JsonNode.Parse(File.ReadAllText(paths.ProjectConfig))!;
        configuration["folders"]![0]!["destination"] = "Projects/ChangedExternally";
        File.WriteAllText(paths.ProjectConfig, configuration.ToJsonString());
        await controller.RefreshAsync();
        Assert.AreNotSame(originalRows, controller.State.Projects);
        Assert.AreEqual("Projects/ChangedExternally", controller.State.Projects.Single().Destination);

        File.WriteAllText(paths.ProjectConfig, "{ invalid json");
        await controller.RefreshAsync();
        Assert.IsNotNull(controller.State.Error, "Suppressing refresh animation must not suppress configuration errors.");
        Assert.IsFalse(controller.State.NeedsSetup);
        Assert.IsFalse(controller.State.Busy);
        Assert.AreEqual(0, busyNotifications);
    }

    [TestMethod]
    public async Task LanguageSwitchPreservesDraftAndFilesAndRelayoutsSettings()
    {
        using var ws = new TestWorkspace();
        var paths = Paths(ws);
        var originalProjects = File.ReadAllText(paths.ProjectConfig);
        var originalSystem = File.ReadAllText(paths.SystemConfig);
        var preferencesPath = Path.Combine(ws.Config, "language-ui.json");
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            try
            {
                using var context = new TrayContext(paths, startHidden: false, preferencesPath: preferencesPath,
                    cloudCatalogReader: (_, _) => Task.FromResult<IReadOnlyList<CloudProjectTarget>>([]));
                using var starter = new System.Windows.Forms.Timer { Interval = 50 };
                void Capture(Form form, string name)
                {
                    var folder = Environment.GetEnvironmentVariable("HARBORBACKUP_SCREENSHOT_DIR");
                    if (folder is null) return;
                    Directory.CreateDirectory(folder);
                    using var bitmap = new System.Drawing.Bitmap(form.Width, form.Height);
                    form.DrawToBitmap(bitmap, new System.Drawing.Rectangle(0, 0, bitmap.Width, bitmap.Height));
                    bitmap.Save(Path.Combine(folder, name + ".png"));
                    File.WriteAllText(Path.Combine(folder, "render-context.txt"), $"Device DPI: {form.DeviceDpi}; Language: {UiText.Language}");
                }
                async Task CheckLanguageAsync()
                {
                    while (context.Controller.Busy) await Task.Delay(50);
                    var form = context.Window!;
                    await form.NavigateAsync("设置");
                    var settings = (SettingsPage)form.Controls.Find("SettingsPage", true).Single();
                    var input = (NumericUpDown)settings.Controls.Find("IntervalMinutes", true).Single();
                    input.Value = 47;
                    var language = (ComboBox)settings.Controls.Find("LanguageSelector", true).Single();
                    var originalSize = form.Size;
                    foreach (var english in new[] { true, false })
                    {
                        language.SelectedIndex = english ? 1 : 0;
                        await Task.Delay(100);
                        Assert.AreEqual(english ? "en-US" : "zh-CN", UiText.Language);
                        Assert.AreEqual(UiText.Language, new DesktopPreferencesStore(paths, preferencesPath).Read().Language);
                        Assert.IsGreaterThanOrEqualTo(TextRenderer.MeasureText(language.Text, language.Font).Width + 24 * form.DeviceDpi / 96,
                            language.Width, "The selected language and dropdown arrow must fit without clipping.");
                        Assert.AreSame(form, context.Window);
                        Assert.AreEqual(47, input.Value);
                        Assert.IsTrue(settings.Dirty, "Language changes must keep the unsaved draft.");
                        Assert.AreEqual(english ? "Settings" : "设置", form.Controls.Find("Navigate设置", true).Single().Text);
                        Assert.AreEqual(english ? "Run now" : "立即运行", form.Controls.Find("立即运行", true).Single().Text);
                        if (english)
                        {
                            // Exercise the real timer past its five-second configuration poll.
                            var scroll = (Panel)settings.Controls.Find("SettingsScroll", true).Single();
                            input.Focus();
                            var scrollPosition = scroll.AutoScrollPosition;
                            var pageBounds = settings.Bounds;
                            var saveButton = form.Controls.Find("保存备份设置", true).Single();
                            var projectRows = context.Controller.State.Projects;
                            var geometryChanges = 0;
                            var enabledChanges = 0;
                            settings.SizeChanged += (_, _) => geometryChanges++;
                            saveButton.EnabledChanged += (_, _) => enabledChanges++;
                            for (var tick = 0; tick < 10; tick++) await context.Controller.TickAsync();
                            await Task.Delay(5500);
                            Assert.AreEqual(0, geometryChanges, "Background polls must not resize the settings viewport.");
                            Assert.AreEqual(0, enabledChanges, "Background polls must not toggle the Save button.");
                            Assert.AreEqual(pageBounds, settings.Bounds);
                            Assert.AreEqual(scrollPosition, scroll.AutoScrollPosition);
                            Assert.IsTrue(input.ContainsFocus, "Polling must preserve the editor focus.");
                            Assert.AreEqual(47, input.Value);
                            Assert.IsTrue(settings.Dirty);
                            Assert.AreSame(projectRows, context.Controller.State.Projects, "Unchanged project rows must not be rebound.");
                            scroll.AutoScrollPosition = System.Drawing.Point.Empty;
                        }
                        foreach (var compact in new[] { false, true })
                        {
                            form.Size = compact ? form.MinimumSize : originalSize;
                            await form.NavigateAsync("设置");
                            form.PerformLayout(); settings.PerformLayout();
                            var header = form.Controls.Find("PageHeader", true).Single();
                            foreach (var name in new[] { "立即运行", "暂停自动备份", "停止当前操作" })
                            {
                                var button = form.Controls.Find(name, true).Single();
                                if (!button.Visible) continue;
                                var bottom = header.PointToClient(button.PointToScreen(new System.Drawing.Point(0, button.Height))).Y;
                                Assert.IsLessThanOrEqualTo(header.ClientSize.Height, bottom, $"Clipped header action: {button.Text}");
                            }
                            foreach (var caption in settings.Controls.Find("SettingCaption", true).Where(c => c.Visible))
                            {
                                Assert.IsLessThanOrEqualTo(caption.Parent!.ClientSize.Height, caption.Bottom, $"Clipped caption: {caption.Text}");
                                Assert.IsGreaterThanOrEqualTo(caption.GetPreferredSize(new System.Drawing.Size(caption.Width, 0)).Height, caption.Height, $"Insufficient text height: {caption.Text}");
                            }
                            var cloud = settings.Controls.Find("CloudRoot", true).Single();
                            var pathFrame = cloud.Parent!.Parent!;
                            var rootCaption = pathFrame.Parent!.Controls.Cast<Control>().Single(c => c.Name == "SettingCaption");
                            var pathCenter = cloud.PointToScreen(new System.Drawing.Point(0, cloud.Height / 2)).Y;
                            var captionCenter = rootCaption.PointToScreen(new System.Drawing.Point(0, rootCaption.Height / 2)).Y;
                            Assert.IsLessThanOrEqualTo(4 * settings.DeviceDpi / 96, Math.Abs(pathCenter - captionCenter),
                                "Root path must share a line with its caption.");
                            foreach (var card in settings.Controls.Find("SettingRow", true))
                            {
                                var maximumHeight = (card.Contains(cloud) ? 100 : 72) * settings.DeviceDpi / 96;
                                Assert.IsLessThanOrEqualTo(maximumHeight, card.Height, "Settings rows must not reserve empty explanation space.");
                                var row = card.Controls[0];
                                foreach (Control child in row.Controls)
                                {
                                    Assert.IsLessThanOrEqualTo(row.ClientSize.Width, child.Right, $"Clipped setting field: {child.Text}");
                                    Assert.IsLessThanOrEqualTo(row.ClientSize.Height, child.Bottom, $"Clipped setting field: {child.Text}");
                                }
                                foreach (var inline in row.Controls.OfType<HarborBackup.Desktop.Styling.SettingsInlineLayout>())
                                    foreach (Control child in inline.Controls)
                                    {
                                        Assert.IsLessThanOrEqualTo(inline.ClientSize.Width, child.Right, $"Clipped inline setting: {child.Text}");
                                        Assert.IsLessThanOrEqualTo(inline.ClientSize.Height, child.Bottom, $"Clipped inline setting: {child.Text}");
                                    }
                            }
                            var helpButton = form.Controls.Find("SettingsHelp", true).Single();
                            Assert.AreEqual(english ? "Help" : "帮助", helpButton.Text);
                            var actionBar = form.Controls.Find("SettingsActions", true).Single();
                            Assert.AreSame(actionBar, helpButton.Parent);
                            Assert.AreSame(helpButton, actionBar.Controls.Cast<Control>().Last(), "Help belongs at the far right of the fixed footer.");
                            Assert.HasCount(1, settings.Controls.Find("管理账户与云端仓库", true));
                            var captions = settings.Controls.Find("SettingCaption", true).Select(c => c.Text).ToArray();
                            Assert.IsFalse(captions.Contains(UiText.Text("配置目录")));
                            Assert.IsFalse(captions.Contains(UiText.Text("日志目录")));
                            foreach (Control action in actionBar.Controls)
                            {
                                Assert.IsLessThanOrEqualTo(actionBar.ClientSize.Width, action.Right);
                                Assert.IsLessThanOrEqualTo(actionBar.ClientSize.Height, action.Bottom);
                                var bounds = form.RectangleToClient(action.RectangleToScreen(action.ClientRectangle));
                                Assert.IsTrue(form.ClientRectangle.Contains(bounds), "Footer actions must remain visible in compact windows.");
                            }
                            Assert.IsLessThanOrEqualTo(actionBar.Parent!.Padding.Right + 2, actionBar.Parent.ClientSize.Width - actionBar.Right,
                                "Settings actions must align with the right edge of the footer.");
                            Capture(form, (english ? "en" : "zh") + "-settings" + (compact ? "-compact" : ""));
                            var scroll = (Panel)settings.Controls.Find("SettingsScroll", true).Single();
                            scroll.AutoScrollPosition = new System.Drawing.Point(0, scroll.DisplayRectangle.Height);
                            Capture(form, (english ? "en" : "zh") + "-settings-bottom" + (compact ? "-compact" : ""));
                            var tools = settings.Controls.Find("AdvancedToolsContent", true).Single();
                            Assert.IsEmpty(settings.Controls.Find("AdvancedToolsToggle", true));
                            Assert.IsTrue(tools.Visible, "JSON configuration shortcuts must be directly visible.");
                            Assert.IsTrue(settings.Controls.Find("项目配置", true).Single().Visible);
                            Assert.IsTrue(settings.Controls.Find("系统配置", true).Single().Visible);
                            Assert.AreEqual(47, input.Value);
                            scroll.AutoScrollPosition = new System.Drawing.Point(0, scroll.DisplayRectangle.Height);
                            Capture(form, (english ? "en" : "zh") + "-advanced-tools" + (compact ? "-compact" : ""));
                            scroll.AutoScrollPosition = System.Drawing.Point.Empty;
                        }
                        foreach (var page in new[] { "概览", "备份项目", "运行记录", "待处理问题" })
                        {
                            await form.NavigateAsync(page);
                            void CheckEditors(Control parent)
                            {
                                foreach (Control child in parent.Controls)
                                {
                                    if (child is TextBox { Multiline: false, PlaceholderText.Length: > 0 } editor)
                                    {
                                        var textHeight = TextRenderer.MeasureText(editor.PlaceholderText, editor.Font,
                                            System.Drawing.Size.Empty, TextFormatFlags.SingleLine | TextFormatFlags.NoPadding).Height + 1;
                                        Assert.IsGreaterThanOrEqualTo(textHeight, editor.ClientSize.Height, $"Clipped placeholder on {page}: {editor.PlaceholderText}");
                                        Assert.IsLessThanOrEqualTo(editor.Parent!.ClientSize.Height, editor.Bottom);
                                    }
                                    CheckEditors(child);
                                }
                            }
                            CheckEditors(form);
                            Capture(form, (english ? "en-" : "zh-") + page);
                            if (page == "备份项目")
                            {
                                var more = (Button)form.Controls.Find("ProjectMoreActions", true).Single();
                                var projectsGrid = (DataGridView)form.Controls.Find("ProjectGrid", true).Single();
                                Assert.IsTrue(more.Enabled, $"More actions disabled: busy={context.Controller.Busy}; rows={projectsGrid.RowCount}; selected={projectsGrid.CurrentRow?.DataBoundItem}; error={context.Controller.State.Error}");
                                var popup = more.ContextMenuStrip!;
                                var opened = false;
                                ToolStripDropDownCloseReason? closedReason = null;
                                popup.Opened += (_, _) => opened = true;
                                popup.Closed += (_, e) => closedReason = e.CloseReason;
                                form.Activate(); more.Focus();
                                more.PerformClick(); await Task.Delay(50);
                                Assert.IsTrue(popup.Visible, $"opened={opened}; closed={closedReason}; buttonVisible={more.Visible}; active={form.ContainsFocus}");
                                Assert.IsFalse(popup.ShowImageMargin);
                                Assert.IsFalse(popup.ShowCheckMargin);
                                Assert.IsFalse(popup.DropShadowEnabled);
                                if (!SystemInformation.HighContrast)
                                {
                                    Assert.IsNotNull(popup.Region);
                                    Assert.IsFalse(popup.Region.IsVisible(0, 0));
                                }
                                foreach (ToolStripItem item in popup.Items)
                                    if (item is ToolStripMenuItem)
                                        Assert.IsGreaterThanOrEqualTo(TextRenderer.MeasureText(item.Text, item.Font).Height, item.Height);
                                if (Environment.GetEnvironmentVariable("HARBORBACKUP_SCREENSHOT_DIR") is { } menuFolder)
                                {
                                    using var bitmap = new System.Drawing.Bitmap(popup.Width, popup.Height);
                                    popup.DrawToBitmap(bitmap, new System.Drawing.Rectangle(0, 0, popup.Width, popup.Height));
                                    bitmap.Save(Path.Combine(menuFolder, (english ? "en" : "zh") + "-project-menu.png"));
                                }
                                popup.Close();
                                var originalMenuFont = popup.Font;
                                foreach (var scale in new[] { 1.5f, 1.75f, 2f })
                                {
                                    using var scaledFont = new System.Drawing.Font(originalMenuFont.FontFamily, originalMenuFont.Size * scale);
                                    popup.Font = scaledFont;
                                    more.PerformClick(); await Task.Delay(30);
                                    foreach (ToolStripItem item in popup.Items)
                                        if (item is ToolStripMenuItem)
                                            Assert.IsGreaterThanOrEqualTo(TextRenderer.MeasureText(item.Text, item.Font).Height, item.Height);
                                    if (Environment.GetEnvironmentVariable("HARBORBACKUP_SCREENSHOT_DIR") is { } scaledFolder)
                                    {
                                        using var bitmap = new System.Drawing.Bitmap(popup.Width,popup.Height);
                                        popup.DrawToBitmap(bitmap,new System.Drawing.Rectangle(0,0,popup.Width,popup.Height));
                                        bitmap.Save(Path.Combine(scaledFolder,(english ? "en" : "zh")+"-project-menu-"+(int)(scale*100)+".png"));
                                    }
                                    popup.Close(); popup.Font = originalMenuFont;
                                }
                            }
                        }
                        var menu = context.Menu;
                        menu.Show(form, new System.Drawing.Point(60, 60));
                        await Task.Delay(50);
                        Assert.IsFalse(menu.ShowImageMargin);
                        Assert.IsFalse(menu.DropShadowEnabled, "The tray menu must not use the heavy system shadow.");
                        Assert.IsTrue(menu.ShowCheckMargin, "Pause state must still have a check mark.");
                        if (!SystemInformation.HighContrast)
                        {
                            Assert.IsNotNull(menu.Region);
                            Assert.IsFalse(menu.Region.IsVisible(0, 0), "The outer menu corner must be rounded.");
                        }
                        var screenshots = Environment.GetEnvironmentVariable("HARBORBACKUP_SCREENSHOT_DIR");
                        if (screenshots is not null)
                        {
                            using var bitmap = new System.Drawing.Bitmap(menu.Width, menu.Height);
                            menu.DrawToBitmap(bitmap, new System.Drawing.Rectangle(0, 0, menu.Width, menu.Height));
                            bitmap.Save(Path.Combine(screenshots, (english ? "en" : "zh") + "-tray-menu.png"));
                        }
                        menu.Close();
                        using (var help = new HelpDialog())
                        {
                            help.Show(form);
                            var helpText = help.Controls.Find("HelpContent", true).Single().Text;
                            StringAssert.Contains(helpText, english ? "Startup delay" : "登录后启动延迟");
                            Capture(help, (english ? "en" : "zh") + "-help"); help.Close();
                        }
                        var presenter = new ConfigurationPresenter(context.Controller);
                        using (var editor = new ProjectEditorDialog(presenter, await presenter.LoadAsync(), null, "OneDrive/"))
                        {
                            editor.Show(form);
                            ((TextBox)editor.Controls.Find("Destination", true).Single()).Text = "项目/Settings";
                            Assert.AreEqual("项目/Settings", editor.Controls.Find("Destination", true).Single().Text);
                            Assert.IsEmpty(editor.Controls.Find("ResolvedTarget", true));
                            Capture(editor, (english ? "en" : "zh") + "-editor"); editor.Close();
                        }
                        using (var picker = new CloudTargetDialog((_, _, _) => Task.FromResult(new HarborBackup.Windows.Integration.CloudFolderListing(
                            new("drive-root", "OneDrive", "root", 0, true),
                            [new("projects", "Projects", "tag", 0, true)]))))
                        {
                            picker.Show(form);
                            await Task.Delay(100);
                            ((TextBox)picker.Controls.Find("NewCloudFolderName", true).Single()).Text = "New backup";
                            Capture(picker, (english ? "en" : "zh") + "-cloud-target");
                            ((Button)picker.Controls.Find("ChooseCloudTarget", true).Single()).PerformClick();
                            Assert.AreEqual("New backup", picker.Destination);
                            Assert.IsNull(picker.FolderId);
                            picker.Close();
                        }
                        using (var cloudProject = new CloudProjectDialog([new("Projects/Demo","demo",DeviceIdentity.Digest("demo")),new("Projects/工作文档","docs",DeviceIdentity.Digest("docs"))],true))
                        {
                            cloudProject.Show(form);
                            var list = (ListBox)cloudProject.Controls.Find("CloudProjects",true).Single();
                            list.SelectedIndex = 1;
                            cloudProject.Size = cloudProject.MinimumSize;
                            Capture(cloudProject,(english ? "en" : "zh")+"-cloud-project");
                            ((Button)cloudProject.Controls.Find("ChooseCloudProject",true).Single()).PerformClick();
                            Assert.AreEqual(DialogResult.OK,cloudProject.DialogResult);
                            Assert.AreEqual("docs",cloudProject.Selected?.FolderId);
                            cloudProject.Close();
                        }
                        using (var folders = new CloudFoldersDialog(new()))
                        {
                            folders.Show(form); Capture(folders, (english ? "en" : "zh") + "-cloud-layout"); folders.Close();
                        }
                        var conflictVersion = new FileVersion(new string('a',64),100,DateTimeOffset.UtcNow,DateTimeOffset.UtcNow);
                        using (var conflictDialog = new CloudConflictDialog(new("Projects/Demo","docs/example.txt",conflictVersion,
                            [new(1,Guid.NewGuid().ToString("N"),new string('b',64),"docs/example.txt",new string('c',64),[],
                                "item","etag",conflictVersion,DateTimeOffset.UtcNow)],"confirmation")))
                        {
                            conflictDialog.Show(form); Capture(conflictDialog, (english ? "en" : "zh") + "-conflict"); conflictDialog.Close();
                        }
                        using (var preview = new PreviewDialog(new PreviewReport([new("Projects/Demo", [], [], new())])))
                        { preview.Show(form); Capture(preview, (english ? "en" : "zh") + "-preview"); preview.Close(); }
                        var safe = new FileDecision(FileAction.Copy, false, FileSide.Source, FileSide.None, ProtectionKind.None, "TargetOnly");
                        var deferred = new FileDecision(FileAction.Deferred, true, FileSide.None, FileSide.None, ProtectionKind.None, "CloudMetadataRequired");
                        var pullReport = new PreviewReport([new("Projects/Demo",
                            [new("first.txt",safe,true,false),new("second.txt",safe,true,false),new("unknown.txt",deferred,true,false)],[],new())])
                        { PullSelection = new("Projects/Demo",false,[new("first.txt","first-token",true,100),new("second.txt","second-token",true,200)]) };
                        using (var preview = new PreviewDialog(pullReport,true,ws.Source,"OneDrive/Projects/Demo"))
                        {
                            preview.Show(form);
                            await Task.Delay(50);
                            var grid = (DataGridView)preview.Controls.Find("PullPreviewGrid",true).Single();
                            Assert.IsTrue((bool)grid.Rows[0].Cells["Selected"].Value!);
                            Assert.IsTrue(grid.Rows[2].Cells["Selected"].ReadOnly);
                            Assert.IsFalse((bool)grid.Rows[2].Cells["Selected"].Value!);
                            grid.Rows[1].Cells["Selected"].Value = false;
                            preview.Size = preview.MinimumSize;
                            Capture(preview,(english ? "en" : "zh")+"-pull-selected");
                            ((Button)preview.Controls.Find("ExecuteSelectedPull",true).Single()).PerformClick();
                            Assert.IsNotNull(preview.SelectedPull);
                            Assert.HasCount(1,preview.SelectedPull.Files);
                            Assert.AreEqual("first.txt",preview.SelectedPull.Files[0].Path);
                            preview.Close();
                        }
                        await form.NavigateAsync("备份项目");
                        Assert.HasCount(1,form.Controls.Find("取回云端项目…",true));
                        await form.NavigateAsync("设置");
                    }
                    Assert.AreEqual(originalProjects, File.ReadAllText(paths.ProjectConfig));
                    Assert.AreEqual(originalSystem, File.ReadAllText(paths.SystemConfig));
                    Assert.IsTrue(settings.Dirty);
                    await context.ShutdownAsync(force: true);
                }
                starter.Tick += async (_, _) =>
                {
                    starter.Stop();
                    try { await CheckLanguageAsync(); completion.TrySetResult(); }
                    catch (Exception ex) { completion.TrySetException(ex); await context.ShutdownAsync(force: true); }
                };
                starter.Start(); Application.Run(context);
            }
            catch (Exception ex) { completion.TrySetException(ex); }
            finally { UiText.SetLanguage("zh-CN"); }
        }) { IsBackground = true };
        thread.SetApartmentState(ApartmentState.STA); thread.Start();
        await completion.Task.WaitAsync(TimeSpan.FromSeconds(45));
        Assert.IsTrue(thread.Join(TimeSpan.FromSeconds(5)));
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task FirstRunGuidesSetupButDamagedConfigurationRemainsAnError(bool damaged)
    {
        using var ws = new TestWorkspace();
        var paths = Paths(ws);
        File.Delete(paths.ProjectConfig);
        File.Delete(paths.SystemConfig);
        if (damaged) File.WriteAllText(paths.ProjectConfig, "{ invalid json");
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            try
            {
                var controller = new DesktopController(paths);
                using var form = new MainForm(controller);
                async Task CheckSetupAsync()
                {
                    try
                    {
                        await controller.RefreshAsync();
                        Assert.AreEqual(!damaged, controller.State.NeedsSetup);
                        Assert.AreEqual(!damaged, form.Controls.Find("WelcomePanel", true).Single().Visible);
                        Assert.IsFalse(((Button)form.Controls.Find("立即运行", true).Single()).Enabled);
                        Assert.HasCount(damaged ? 1 : 0, controller.State.Details);
                        var screenshotDirectory = Environment.GetEnvironmentVariable("HARBORBACKUP_SCREENSHOT_DIR");
                        if (screenshotDirectory is not null)
                        {
                            Directory.CreateDirectory(screenshotDirectory);
                            foreach (var compact in new[] { false, true })
                            {
                                if (compact) form.Size = form.MinimumSize;
                                using var bitmap = new System.Drawing.Bitmap(form.Width, form.Height);
                                form.DrawToBitmap(bitmap, new System.Drawing.Rectangle(0, 0, bitmap.Width, bitmap.Height));
                                bitmap.Save(Path.Combine(screenshotDirectory, (damaged ? "配置损坏" : "首次使用") + (compact ? "-紧凑" : "") + ".png"));
                            }
                        }
                        if (!damaged) await form.NavigateAsync("设置");
                        Assert.IsFalse(File.Exists(paths.SystemConfig), "Opening setup must not persist defaults or enable automatic work.");
                        if (damaged) Assert.AreEqual("{ invalid json", File.ReadAllText(paths.ProjectConfig));
                        else Assert.IsFalse(File.Exists(paths.ProjectConfig));
                        completion.TrySetResult();
                    }
                    catch (Exception ex) { completion.TrySetException(ex); }
                    finally { form.ClosePermanently(); }
                }
                form.Shown += async (_, _) => await CheckSetupAsync();
                Application.Run(form);
            }
            catch (Exception ex) { completion.TrySetException(ex); }
        }) { IsBackground = true };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        await completion.Task.WaitAsync(TimeSpan.FromSeconds(30));
        Assert.IsTrue(thread.Join(TimeSpan.FromSeconds(5)));
    }

    [TestMethod]
    public async Task InstanceActivationAcceptsShowRequest()
    {
        var shown = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var channel = "HarborBackup.Tests-" + Guid.NewGuid().ToString("N");
        using var activation = new InstanceActivation(channel, () => shown.TrySetResult());
        Assert.IsTrue(await InstanceActivation.ShowAsync(channel));
        await shown.Task.WaitAsync(TimeSpan.FromSeconds(5));
    }

    [TestMethod]
    public async Task InstanceActivationAcceptsSafeExitRequest()
    {
        var exited = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var channel = "HarborBackup.Tests-" + Guid.NewGuid().ToString("N");
        using var activation = new InstanceActivation(channel, () => Assert.Fail("Unexpected show request"), () => exited.TrySetResult());
        Assert.IsTrue(await InstanceActivation.ShutdownAsync(channel));
        await exited.Task.WaitAsync(TimeSpan.FromSeconds(5));
    }

    [TestMethod]
    public void HistoryReadsGoodRunsAndReportsDamagedEntries()
    {
        using var ws = new TestWorkspace();
        ws.Write(ws.Source, "a.txt", "sample");
        var runtime = Runtime(ws);
        RunStatusStore.Log(runtime.Load(), new BackupReport("Backup", DateTimeOffset.UtcNow, DateTimeOffset.UtcNow,
            [new("Projects/Demo", [new("a.txt", FileAction.Copy, true)], [], new())]) { CloudUploadStatus = "CloudCommitted" });
        var config = runtime.Load();
        File.WriteAllText(Path.Combine(config.HistoryDirectory, "99999999-broken.json"), "not json");
        var history = RunHistory.Read(config.HistoryDirectory);
        Assert.HasCount(2, history);
        Assert.IsNotNull(history[0].Error);
        Assert.IsNotNull(history[1].Report);
        Assert.AreEqual("Backup", history[1].Mode);
    }
}
