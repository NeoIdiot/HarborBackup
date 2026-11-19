using HarborBackup.Desktop.Presentation;
using HarborBackup.Desktop.Controllers;
using HarborBackup.Desktop.Localization;
using HarborBackup.Desktop.Lifecycle;
using HarborBackup.Desktop.Views;
using Microsoft.Win32;
using HarborBackup.Windows.Persistence.Configuration;
using HarborBackup.Windows.Integration;

namespace HarborBackup.Desktop;

internal enum TrayCommand { Backup, Preview, Pull, Verify, FullBackup, Recover }

/// <summary>Owns desktop lifetime only. Both UI surfaces use the same controller.</summary>
internal sealed class TrayContext : ApplicationContext
{
    private readonly DesktopController controller;
    private readonly DesktopPreferencesStore preferenceStore;
    private DesktopPreferences preferences;
    private readonly NotifyIcon icon;
    private readonly Styling.TrayIcons trayIcons = new();
    private readonly ContextMenuStrip menu = new Styling.AppMenu(showChecks: true);
    private readonly System.Windows.Forms.Timer timer = new() { Interval = 1000 };
    private readonly Control dispatcher = new();
    private readonly ToolStripMenuItem statusItem = new("正在加载配置") { Enabled = false };
    private readonly ToolStripMenuItem nextItem = new("尚未安排") { Enabled = false };
    private readonly ToolStripMenuItem pauseItem = new("暂停后续自动备份");
    private readonly List<ToolStripMenuItem> operationItems = [];
    private readonly ProblemNotificationPolicy problemNotifications = new();
    private MainForm? window;
    private bool closing;
    private string? lastImmediateProblem;
    private long lastImmediateProblemAt;
    internal bool IconVisible => icon.Visible;
    internal int UiTicks { get; private set; }
    internal MainForm? Window => window;
    internal DesktopController Controller => controller;
    internal ContextMenuStrip Menu => menu;

    public TrayContext(RuntimePaths paths, bool startHidden = true, string? preferencesPath = null,
        Func<CloudAccountSettings, CancellationToken, Task<IReadOnlyList<CloudProjectTarget>>>? cloudCatalogReader = null)
    {
        preferenceStore = new(paths, preferencesPath);
        preferences = preferenceStore.Read();
        UiText.SetLanguage(preferences.Language);
        controller = new(paths, cloudCatalogReader);
        dispatcher.CreateControl();
        icon = new NotifyIcon { Icon = Styling.AppStyle.BrandIcon, Text = "HarborBackup", ContextMenuStrip = menu, Visible = true };
        menu.Items.Add("打开主窗口", null, (_, _) => ShowStatus());
        menu.Items.Add(statusItem);
        menu.Items.Add(nextItem);
        menu.Items.Add(new ToolStripSeparator());
        Add("立即备份", DesktopCommand.Backup);
        menu.Items.Add("预览 / 取回更新…", null, async (_, _) => { ShowStatus(); await window!.NavigateAsync("备份项目"); });
        Add("完整哈希核查（只读）", DesktopCommand.Verify);
        Add("恢复未完成事务", DesktopCommand.Recover);
        pauseItem.Click += async (_, _) => await controller.SetPausedAsync(!controller.State.Paused);
        menu.Items.Add(pauseItem);
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("退出并停止本机自动备份", null, async (_, _) => await ShutdownAsync());
        foreach (ToolStripItem item in menu.Items) if (item is not ToolStripSeparator) UiText.Bind(item, item.Text ?? "");
        icon.DoubleClick += (_, _) => ShowStatus();
        icon.BalloonTipClicked += (_, _) => ShowStatus();
        controller.Changed += UpdateMenu;
        UiText.Changed += UpdateMenu;
        timer.Tick += async (_, _) =>
        {
            UiTicks++;
            try { await controller.TickAsync(); }
            catch (Exception ex) { ReportProblem(CloudAccountPresentation.Error(ex)); }
        };
        SystemEvents.SessionEnding += OnSessionEnding;
        SystemEvents.DisplaySettingsChanged += OnTrayEnvironmentChanged;
        SystemEvents.UserPreferenceChanged += OnTrayPreferenceChanged;
        timer.Start();
        if (!startHidden) ShowStatus();
        _ = controller.RefreshAsync(includeHistory: true);
    }
    private void Add(string caption, DesktopCommand command)
    {
        var item = new ToolStripMenuItem(caption);
        item.Click += async (_, _) => await controller.RunAsync(command);
        operationItems.Add(item);
        menu.Items.Add(item);
    }
    internal Task RefreshConfigurationAsync() => controller.RefreshAsync(true);
    internal async Task ExecuteCommandAsync(TrayCommand command, string? destination = null, bool automatic = false) =>
        await controller.RunAsync(Enum.Parse<DesktopCommand>(command.ToString()), destination, automatic);

    private void UpdateMenu()
    {
        if (closing) return;
        var state = controller.State;
        statusItem.Text = UiText.Text("状态：{0}", UiText.Text(state.Status));
        nextItem.Text = state.Paused ? UiText.Text("后续自动备份已暂停") : UiText.Text("下次备份：{0}", state.NextDue?.ToLocalTime().ToString("HH:mm:ss") ?? UiText.Text("尚未安排"));
        pauseItem.Text = UiText.Text(state.Paused ? "恢复自动备份" : "暂停后续自动备份");
        statusItem.AccessibleName = statusItem.Text; nextItem.AccessibleName = nextItem.Text; pauseItem.AccessibleName = pauseItem.Text;
        pauseItem.Checked = state.Paused;
        pauseItem.Enabled = state.Configuration is not null;
        foreach (var item in operationItems) item.Enabled = !state.Busy && state.Configuration?.Projects.Count > 0;
        var presentation = TrayPresentation.Create(state, controller.CloudAccount);
        var nextIcon = trayIcons.Get(presentation.Badge, Styling.TrayIcons.TaskbarIconSize(), SystemInformation.HighContrast);
        if (!ReferenceEquals(icon.Icon, nextIcon)) icon.Icon = nextIcon;
        if (icon.Text != presentation.ToolTip) icon.Text = presentation.ToolTip;
        if (problemNotifications.ShouldNotify(state))
            _ = NotifyProblemAsync(UiText.Text("请在待处理问题或运行记录中查看详情。"));
    }

    private void ReportProblem(string message)
    {
        if (closing) return;
        if (ProblemNotificationPolicy.IsOffline(message))
        {
            controller.ObserveOffline();
            return;
        }

        var now = Environment.TickCount64;
        if (message == lastImmediateProblem && now - lastImmediateProblemAt < 30_000) return;
        lastImmediateProblem = message;
        lastImmediateProblemAt = now;
        _ = NotifyProblemAsync(message);
    }

    private async Task NotifyProblemAsync(string message)
    {
        if (closing) return;
        var title = "HarborBackup · " + UiText.Text("有待处理问题");
        if (await Task.Run(() => WindowsNotification.TryShow(title, message)) || closing) return;
        icon.ShowBalloonTip(4000, title, message, ToolTipIcon.Warning);
    }

    private void OnTrayPreferenceChanged(object sender, UserPreferenceChangedEventArgs e) => OnTrayEnvironmentChanged(sender, e);
    private void OnTrayEnvironmentChanged(object? sender, EventArgs e)
    {
        if (closing || dispatcher.IsDisposed || !dispatcher.IsHandleCreated) return;
        try { dispatcher.BeginInvoke((Action)(() =>
        {
            if (closing) return;
            icon.Icon = Styling.AppStyle.BrandIcon;
            trayIcons.Clear(); // Reset theme/DPI variants after detaching the displayed icon.
            UpdateMenu();
        })); }
        catch (InvalidOperationException) when (closing || dispatcher.IsDisposed || !dispatcher.IsHandleCreated) { }
    }

    internal void ShowStatus()
    {
        if (closing) return;
        if (window is null || window.IsDisposed)
        {
            window = new MainForm(controller, ChangeLanguageAsync, ReportProblem);
            var area = Screen.PrimaryScreen?.WorkingArea ?? new Rectangle(0, 0, 1100, 720);
            var scale = window.DeviceDpi / 96f;
            window.MinimumSize = new Size(Math.Min(window.MinimumSize.Width, area.Width), Math.Min(window.MinimumSize.Height, area.Height));
            window.Size = new Size(Math.Min((int)(preferences.Width * scale), area.Width), Math.Min((int)(preferences.Height * scale), area.Height));
            window.SidebarWidth = preferences.SidebarWidth;
            window.HiddenToTray += () =>
            {
                if (!preferences.CloseHintShown) icon.ShowBalloonTip(3500, UiText.Text("HarborBackup 仍在后台运行"), UiText.Text("关闭主窗口不会停止备份。退出程序请使用托盘菜单。"), ToolTipIcon.Info);
                preferences = preferences with { CloseHintShown = true };
            };
            window.ExitRequested += () => ShutdownAsync();
        }
        window.Show();
        if (window.WindowState == FormWindowState.Minimized) window.WindowState = FormWindowState.Normal;
        window.Activate();
    }
    internal void RequestActivation()
    {
        if (!closing && !dispatcher.IsDisposed && dispatcher.IsHandleCreated)
            dispatcher.BeginInvoke((Action)ShowStatus);
    }
    internal void RequestIssues()
    {
        if (!closing && !dispatcher.IsDisposed && dispatcher.IsHandleCreated)
            dispatcher.BeginInvoke(async () =>
            {
                if (closing) return;
                ShowStatus();
                await window!.NavigateAsync("待处理问题");
            });
    }
    internal void RequestShutdown()
    {
        if (!closing && !dispatcher.IsDisposed && dispatcher.IsHandleCreated)
            dispatcher.BeginInvoke(async () => await ShutdownAsync());
    }
    internal Task ChangeLanguageAsync(string language)
    {
        var next = preferences with { Language = UiText.Normalize(language) };
        // A local preference only: keep the active operation and unsaved configuration draft intact.
        preferenceStore.Save(next);
        preferences = next;
        UiText.SetLanguage(next.Language);
        return Task.CompletedTask;
    }
    private void OnSessionEnding(object sender, SessionEndingEventArgs e)
    {
        if (!dispatcher.IsDisposed && dispatcher.IsHandleCreated)
            dispatcher.BeginInvoke(async () => await ShutdownAsync(force: true));
    }
    internal async Task ShutdownAsync(bool force = false)
    {
        if (closing || (!force && window?.CanExit() == false)) return;
        closing = true;
        timer.Stop();
        menu.Enabled = false;
        icon.Text = UiText.Text("HarborBackup · 等待当前事务安全结束");
        try { await controller.StopAsync(); } catch { /* Transaction evidence remains available. */ }
        if (window is not null)
        {
            var size = window.WindowState == FormWindowState.Normal ? window.Size : window.RestoreBounds.Size;
            var scale = window.DeviceDpi / 96f;
            preferences = preferences with { Width = (int)(size.Width / scale), Height = (int)(size.Height / scale), SidebarWidth = window.SidebarWidth };
        }
        try { await Task.Run(() => preferenceStore.Save(preferences)); } catch { /* Do not overwrite an externally changed preference file. */ }
        window?.ClosePermanently();
        icon.Visible = false;
        ExitThread();
    }
    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            closing = true;
            controller.Changed -= UpdateMenu;
            UiText.Changed -= UpdateMenu;
            SystemEvents.SessionEnding -= OnSessionEnding;
            SystemEvents.DisplaySettingsChanged -= OnTrayEnvironmentChanged;
            SystemEvents.UserPreferenceChanged -= OnTrayPreferenceChanged;
            timer.Dispose();
            icon.Visible = false;
            icon.Dispose();
            trayIcons.Dispose();
            menu.Dispose();
            window?.Dispose();
            dispatcher.Dispose();
        }
        base.Dispose(disposing);
    }
}
