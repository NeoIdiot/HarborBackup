using System.Diagnostics;
using HarborBackup.Desktop.Localization;
using HarborBackup.Core.Domain;
using HarborBackup.Desktop.Controllers;
using HarborBackup.Desktop.Presentation;
using HarborBackup.Desktop.Styling;
using HarborBackup.Desktop.Views.Dialogs;
using HarborBackup.Desktop.Views.Pages;
using HarborBackup.Windows.Persistence;
using HarborBackup.Windows.Persistence.Configuration;

namespace HarborBackup.Desktop.Views;

internal sealed partial class MainForm : Form
{
    private readonly DesktopController controller;
    private readonly ConfigurationPresenter presenter;
    private readonly Action<string>? reportProblem;
    private readonly Panel content = new() { Dock = DockStyle.Fill, Padding = new Padding(28, 0, 28, 16) };
    private readonly Label status = AppStyle.Label("", 26);
    private readonly Label currentStatus = AppStyle.Label("正在加载", 50);
    private readonly Label lastRun = AppStyle.Label("尚未运行", 50);
    private readonly Label nextRun = AppStyle.Label("尚未安排", 50);
    private readonly Label pageTitle = AppStyle.Label("概览", 40);
    private readonly Label notice = AppStyle.Label("", 32);
    private readonly Label progressText = AppStyle.Label("", 24);
    private readonly Panel dashboard = new() { Dock = DockStyle.Fill };
    private readonly Panel welcome = new() { Dock = DockStyle.Fill, Name = "WelcomePanel", AutoScroll = true };
    private readonly Label welcomeHeading = AppStyle.Label("", 48);
    private readonly Label welcomeBody = AppStyle.Label("", 56);
    private readonly ProgressBar progressBar = new() { Dock = DockStyle.Top, Height = 5, Visible = false };
    private readonly DataGridView projectGrid = AppStyle.Grid(("Destination", "项目 / 目标相对路径", 19), ("DisplayLocalStatus", "本机状态", 12), ("DisplaySource", "本地源目录", 17), ("Target", "实际目标", 17), ("DisplayResult", "最近结果", 12), ("DisplayProtection", "项目保护状态", 23));
    private readonly DataGridView overviewGrid = AppStyle.Grid(("Destination", "备份项目", 25), ("DisplayResult", "最近结果", 20), ("DisplayLastFullSuccess", "最近完整成功", 18), ("DisplayLastRestoreVerification", "最近还原核验", 18), ("DisplayBacklog", "已知积压", 19));
    private readonly DataGridView historyGrid = AppStyle.Grid(("Time", "结束时间", 25), ("Mode", "操作", 15), ("ExitCode", "结果", 15), ("ProjectNames", "项目", 45));
    private readonly DataGridView detailGrid = AppStyle.Grid(("DisplayProject", "项目", 20), ("Path", "相对路径", 25), ("DisplayAction", "动作", 15), ("DisplayDetails", "详情", 40));
    private readonly TextBox filter = AppStyle.Text();
    private readonly TextBox historyFilter = new() { Width = 210, PlaceholderText = "按项目筛选记录…" };
    private readonly ComboBox historyResult = new() { Width = 130, DropDownStyle = ComboBoxStyle.DropDownList };
    private readonly DateTimePicker historySince = new() { Width = 160, ShowCheckBox = true, Checked = false, Format = DateTimePickerFormat.Short };
    private readonly Label detailCount = AppStyle.Label("", 30);
    private readonly Dictionary<string, Control> pages = [];
    private readonly Dictionary<string, Button> navigation = [];
    private readonly List<Button> idleButtons = [];
    private readonly List<Button> cloudProjectButtons = [];
    private readonly SettingsPage settings;
    private readonly Button pause;
    private readonly Button cancel;
    private IReadOnlyList<ProjectRow>? previousProjects;
    private IReadOnlyList<HistoryRecord>? previousHistory;
    private IReadOnlyList<DetailRow>? previousDetails;
    private IReadOnlyList<DetailRow> shownDetails = [];
    private bool closingPermanently;
    private Task? pagePreparation;
    private string currentPage = "概览";
    public event Action? HiddenToTray;
    public event Func<Task>? ExitRequested;

    public MainForm(DesktopController controller, Func<string, Task>? changeLanguage = null, Action<string>? reportProblem = null)
    {
        this.controller = controller;
        this.reportProblem = reportProblem;
        presenter = new(controller);
        AppStyle.Configure(this, "HarborBackup · 泊岸备份", new Size(1120, 760));
        MinimumSize = new Size(980, 660);
        pageTitle.Font = new Font(Font.FontFamily, 21, FontStyle.Bold);
        StartPosition = FormStartPosition.CenterScreen;
        var sidebar = BuildSidebar();
        pause = AppStyle.Button("暂停自动备份", async (_, _) => await controller.SetPausedAsync(!controller.State.Paused));
        cancel = AppStyle.Button("停止当前操作", (_, _) => controller.Cancel());
        var top = AppStyle.Toolbar(cancel, pause, Operation("立即备份", DesktopCommand.Backup, primary: true));
        top.FlowDirection = FlowDirection.RightToLeft; top.WrapContents = false; top.Dock = DockStyle.Fill; top.Anchor = AnchorStyles.Right;
        top.Padding = Padding.Empty;
        pageTitle.AutoSize = true; pageTitle.Dock = DockStyle.Fill; pageTitle.MinimumSize = new Size(180, 40);
        var header = new TableLayoutPanel { Name = "PageHeader", Dock = DockStyle.Top, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink,
            Padding = new Padding(28, 20, 20, 12), ColumnCount = 2, RowCount = 1 };
        header.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        header.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100)); header.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        header.Controls.Add(pageTitle, 0, 0); header.Controls.Add(top, 1, 0);
        var footer = new TableLayoutPanel { Dock = DockStyle.Bottom, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink,
            ColumnCount = 2, RowCount = 3, Padding = new Padding(28, 0, 28, 10) };
        footer.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        footer.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        for (var row = 0; row < 3; row++) footer.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        status.ForeColor = AppStyle.Muted; progressText.ForeColor = AppStyle.Muted;
        status.Margin = progressText.Margin = progressBar.Margin = Padding.Empty;
        footer.Controls.Add(status, 0, 0);
        footer.Controls.Add(progressBar, 0, 1);
        footer.Controls.Add(progressText, 0, 2);
        footer.SetColumnSpan(progressBar, 2); footer.SetColumnSpan(progressText, 2);
        status.Dock = DockStyle.Fill;
        var main = new Panel { Dock = DockStyle.Fill };
        main.Controls.Add(content);
        main.Controls.Add(footer);
        main.Controls.Add(header);
        Controls.Add(main);
        var divider = new NavigationSplitter { Name = "NavigationDivider", Dock = DockStyle.Left, Width = 7,
            MinSize = 64, MinExtra = 450, Cursor = Cursors.VSplit };
        divider.SplitterMoved += (_, _) => SidebarWidth = SidebarWidth;
        Controls.Add(divider);
        Controls.Add(sidebar);

        BuildOverviewPage();
        BuildProjectsPage();
        BuildHistoryPage();
        BuildIssuesPage();
        settings = new(controller, presenter, changeLanguage);
        footer.Controls.Add(settings.Actions, 1, 0);
        settings.Actions.Visible = false;
        // Reserve the settings action height on every page. Collapsing this row
        // resized the entire content viewport on every trip into/out of Settings.
        void FitFooterActions()
        {
            var height = settings.Actions.GetPreferredSize(Size.Empty).Height;
            var minimum = new Size(0, height);
            if (status.MinimumSize != minimum) status.MinimumSize = minimum;
        }
        settings.Actions.SizeChanged += (_, _) => FitFooterActions();
        settings.Actions.FontChanged += (_, _) => FitFooterActions();
        settings.Actions.DpiChangedAfterParent += (_, _) => FitFooterActions();
        UiText.Watch(footer, FitFooterActions);
        pages.Add("设置", settings);
        content.Controls.Add(settings);
        foreach (var pair in pages) pair.Value.Visible = pair.Key == "概览";
        AppStyle.SelectNavigation(navigation["概览"], true);
        controller.Changed += UpdateState;
        UiText.Changed += RefreshLanguage;
        RefreshLanguage();
        UpdateState();
        Shown += async (_, _) => await PreparePagesAsync();
        FormClosing += (_, e) =>
        {
            if (closingPermanently || e.CloseReason is CloseReason.WindowsShutDown or CloseReason.TaskManagerClosing) return;
            e.Cancel = true;
            Hide();
            HiddenToTray?.Invoke();
        };
        ResumeLayout(true);
    }

    private Panel Page(string title)
    {
        var page = new Panel { Dock = DockStyle.Fill };
        pages.Add(title, page);
        content.Controls.Add(page);
        return page;
    }
    private static Panel GridSurface(DataGridView grid, string glyph, string title, string body)
    {
        var surface = new SurfacePanel { Dock = DockStyle.Fill, Padding = new Padding(1) };
        var host = new Panel { Dock = DockStyle.Fill, BackColor = AppStyle.Surface };
        var empty = new EmptyState(glyph, title, body);
        host.Controls.Add(grid); host.Controls.Add(empty);
        surface.Controls.Add(host);
        void UpdateEmpty()
        {
            empty.Visible = grid.Rows.Count == 0;
            grid.Visible = grid.Rows.Count > 0;
        }
        grid.DataBindingComplete += (_, _) => UpdateEmpty();
        UpdateEmpty();
        return surface;
    }
    private void BuildWelcome()
    {
        var layout = new TableLayoutPanel { Dock = DockStyle.Top, AutoSize = true, ColumnCount = 1, Padding = new Padding(0, 0, 0, 8) };
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        var hero = new SurfacePanel { Dock = DockStyle.Top, Height = 210, Padding = new Padding(26, 22, 26, 20), Margin = new Padding(0, 0, 0, 20) };
        welcomeHeading.Font = new Font(Font.FontFamily, 18, FontStyle.Bold);
        welcomeBody.ForeColor = AppStyle.Muted;
        var actions = AppStyle.Toolbar(AppStyle.Button("设置备份位置", async (_, _) => await NavigateAsync("设置"), true), EditButton("添加第一个项目", null));
        actions.Dock = DockStyle.Bottom;
        hero.Controls.Add(actions); hero.Controls.Add(welcomeBody); hero.Controls.Add(welcomeHeading);
        layout.Controls.Add(hero);
        var steps = new TableLayoutPanel { Dock = DockStyle.Top, Height = 112, ColumnCount = 3, Margin = Padding.Empty };
        var items = new[] { ("01", "选择备份位置"), ("02", "添加备份项目"), ("03", "预览，然后备份") };
        foreach (var (number, title) in items)
        {
            steps.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100f / 3));
            var step = new SurfacePanel { Dock = DockStyle.Fill, Padding = new Padding(18, 14, 16, 12), Margin = new Padding(0, 0, number == "03" ? 0 : 12, 0) };
            var name = AppStyle.Label(title, 36); name.Font = new Font(Font.FontFamily, 11, FontStyle.Bold);
            var index = AppStyle.Label(number, 28); index.ForeColor = AppStyle.Accent; index.Font = new Font("Segoe UI", 11, FontStyle.Bold);
            step.Controls.Add(name); step.Controls.Add(index); steps.Controls.Add(step);
        }
        layout.Controls.Add(steps);
        welcome.Controls.Add(layout);
    }
    private Button Operation(string caption, DesktopCommand command, bool selected = false, bool primary = false)
    {
        var button = AppStyle.Button(caption, async (_, _) => await ExecuteAsync(command, selected), primary);
        (selected ? localSelectionButtons : idleButtons).Add(button);
        return button;
    }
    private Button EditButton(string caption, int? index, bool global = false)
    {
        var button = AppStyle.Button(caption, async (_, _) =>
        {
        try
        {
            var selected = index == -1 ? projectGrid.CurrentRow?.DataBoundItem as ProjectRow : null;
            if (index == -1 && selected is not { IsLocal: true }) return;
            var draft = await presenter.LoadAsync();
            var actualIndex = index == -1 ? draft.Projects["folders"]!.AsArray().ToList().FindIndex(p =>
                p?["id"]?.GetValue<string>() == selected!.ProjectId) : (int?)null;
            if (actualIndex == -1) throw new IOException("项目配置已变化，请刷新后重试。");
            using var dialog = new ProjectEditorDialog(presenter, draft, actualIndex,
                CloudAccountPresentation.Path(), global);
            if (dialog.ShowDialog(this) == DialogResult.OK) await controller.RefreshAsync(true);
        }
        catch (Exception ex) { ShowError(CloudAccountPresentation.Error(ex)); }
        });
        if (index == -1) localSelectionButtons.Add(button);
        return button;
    }

    private async Task RemoveAsync()
    {
        if (projectGrid.CurrentRow?.DataBoundItem is not ProjectRow selected) return;
        if (MessageBox.Show(this, UiText.Text("从本机项目列表移除 {0}？\n只更新本机配置；源、云端文件、历史和恢复材料都会保留。", selected.Destination), UiText.Text("移除项目"), MessageBoxButtons.OKCancel, MessageBoxIcon.Information) != DialogResult.OK) return;
        try
        {
            await RemoveProjectAsync(selected);
        }
        catch (Exception ex) { ShowError(CloudAccountPresentation.Error(ex)); }
    }

    internal async Task RemoveProjectAsync(ProjectRow selected)
    {
        var draft = await presenter.LoadAsync();
        controller.Editor.RemoveProject(draft, selected.ProjectId);
        await presenter.SaveAsync(draft, ConfigurationPart.Projects);
        await controller.RefreshAsync(true);
        BindProjectRows();
    }

    private async Task ExecuteAsync(DesktopCommand command, bool selected)
    {
        var project = selected ? projectGrid.CurrentRow?.DataBoundItem as ProjectRow : null;
        if (selected && project is not { IsLocal: true }) { ShowError("请先选择一个备份项目。"); return; }
        var upload = selected && command is DesktopCommand.Backup or DesktopCommand.FullBackup;
        var result = await controller.RunAsync(upload ? DesktopCommand.UploadPreview : command, project?.ProjectId, fullPreview: command == DesktopCommand.FullBackup);
        if (result is PreviewReport report && !ReportPresentation.OfflineOnly(report) &&
            (report.ExitCode != 1 || report.Projects.Any(p => p.Plans.Count > 0)))
        {
            var pull = command == DesktopCommand.PullPreview;
            using var dialog = new PreviewDialog(report, pull, project?.Source, project?.Target, allowUpload: upload);
            if (dialog.ShowDialog(this) == DialogResult.OK)
            {
                if (pull && dialog.SelectedPull is { } selection)
                    await controller.RunAsync(DesktopCommand.Pull, project!.ProjectId, confirmedPull: selection);
                else if (upload && dialog.SelectedUpload is { } chosen)
                    await controller.RunAsync(command, project!.ProjectId, confirmedUpload: chosen);
            }
        }
    }

    internal async Task NavigateAsync(string page)
    {
        if (!pages.ContainsKey(page)) throw new ArgumentException("Unknown page.", nameof(page));
        if (page != "备份项目") catalogCancellation?.Cancel();
        var changed = currentPage != page;
        if (changed)
        {
            var previous = pages[currentPage];
            currentPage = page;
            UiText.Set(pageTitle, page);
            foreach (var item in navigation)
            {
                AppStyle.SelectNavigation(item.Value, item.Key == page);
            }
            // Showing a page propagates VisibleChanged through every nested container.
            // Batch those layout requests instead of measuring the settings tree per child.
            var containers = new[] { content, settings.Actions.Parent! }.Concat(LayoutContainers(previous))
                .Concat(LayoutContainers(pages[page])).Distinct().ToArray();
            foreach (var container in containers) container.SuspendLayout();
            try
            {
                pages[page].Visible = true;
                previous.Visible = false;
                settings.Actions.Visible = page == "设置";
            }
            finally
            {
                foreach (var container in containers.Reverse()) container.ResumeLayout(false);
                content.PerformLayout();
                settings.Actions.Parent!.PerformLayout();
            }
            UiText.Set(this, "HarborBackup / {0}", UiText.Text(page));
        }
        if (page == "备份项目" && (!catalogLoaded || catalogDisplay == CatalogDisplay.Initial)) await LoadCloudProjectsAsync();
        if (page == "设置") await settings.LoadDraftAsync();
        if (page == "运行记录" && changed) await controller.RefreshAsync(true);
        // UpdateState already maintains the current issue view. Do not rebind it on every visit.
    }

    internal Task PreparePagesAsync() => pagePreparation ??= PreparePageControlsAsync();

    private async Task PreparePageControlsAsync()
    {
        var draft = settings.LoadDraftAsync();
        // Initialize each page behind the active page. Merely accessing Handle does
        // not complete WinForms' first-visible initialization of native editors.
        // Yield between pages; no cloud reads or transfers are started here.
        foreach (var page in pages.Values)
        {
            await Task.Delay(1);
            if (IsDisposed || Disposing) return;
            var active = pages[currentPage];
            if (page == active) continue;
            active.BringToFront();
            try { page.Visible = true; page.CreateControl(); page.PerformLayout(); }
            finally { if (!page.IsDisposed) page.Visible = false; }
        }
        await draft;
    }
    private static IEnumerable<Control> LayoutContainers(Control root)
    {
        if (root is Panel or UserControl) yield return root;
        foreach (Control child in root.Controls)
            foreach (var container in LayoutContainers(child)) yield return container;
    }
    private void ShowDetails()
    {
        var text = filter.Text.Trim();
        var filtered = shownDetails.Where(d => d.Attention &&
            (text.Length == 0 || (d.Project + d.Path + d.DisplayAction + d.DisplayDetails).Contains(text, StringComparison.OrdinalIgnoreCase))).ToArray();
        detailGrid.DataSource = filtered.Take(1000).ToArray();
        UiText.Set(detailCount, "显示 {0} / {1} 项", Math.Min(1000, filtered.Length), filtered.Length);
    }
    private void UpdateState()
    {
        if (IsDisposed) return;
        var state = controller.State;
        CheckCatalogAccount();
        CheckCatalogConnectivity(state);
        var onboarding = state.NeedsSetup || (state.Configuration is not null && state.Projects.Count == 0);
        welcome.Visible = onboarding;
        dashboard.Visible = !onboarding;
        UiText.Set(welcomeHeading, "开始备份");
        var accountReady = controller.CloudAccount.Enabled && !controller.CloudAccount.RequiresAuthorization;
        UiText.Set(welcomeBody, !accountReady ? "请先连接 OneDrive 账户并选择云端仓库。"
            : state.NeedsSetup ? "选择备份位置，然后添加项目。" : "备份位置已就绪，请添加项目。");
        foreach (var button in idleButtons) button.Enabled = !state.Busy && state.Configuration?.Projects.Count > 0;
        pause.Enabled = state.Configuration is not null;
        UiText.Set(pause, state.Paused ? "恢复自动备份" : "暂停自动备份");
        cancel.Enabled = state.CanCancel;
        cancel.Visible = state.CanCancel;
        UiText.Set(currentStatus, state.Status);
        UiText.Set(lastRun, state.LastCompleted?.ToLocalTime().ToString("MM-dd HH:mm:ss") ?? "尚未运行");
        UiText.Set(nextRun, state.Configuration?.Projects.Count is not > 0 ? "请先配置项目" : state.Paused ? "已暂停" : state.NextDue?.ToLocalTime().ToString("HH:mm:ss") ?? "尚未安排");
        if (state.Error is not null) UiText.Set(notice, state.Error);
        else UiText.Set(notice, "{0} 个项目 · {1} 项待处理", state.Projects.Count, state.Details.Count(d => d.Attention));
        notice.ForeColor = state.Error is null ? AppStyle.Muted : SystemInformation.HighContrast ? AppStyle.Ink : Color.FromArgb(164, 74, 16);
        UiText.Set(status, controller.CloudAccount.Enabled ? "{0} · OneDrive 账户直连" : "{0} · 云端上传未验证", UiText.Text(state.Status));
        progressBar.Visible = state.CanCancel;
        if (state.Progress is { Total: > 0 } p)
        {
            progressBar.Style = ProgressBarStyle.Continuous;
            progressBar.Maximum = p.Total.Value;
            progressBar.Value = Math.Clamp(p.Processed, 0, p.Total.Value);
            UiText.Set(progressText, "{0} / {1} · {2}/{3} · 哈希 {4:F1} MiB", p.Project, p.Path, p.Processed, p.Total, p.HashedBytes / 1024d / 1024d);
        }
        else { progressBar.Style = ProgressBarStyle.Marquee; UiText.Set(progressText, state.CanCancel ? "正在处理，请稍候…" : ""); }
        progressText.Visible = state.CanCancel;
        if (!ReferenceEquals(previousProjects, state.Projects))
        {
            previousProjects = state.Projects;
            BindProjectRows();
            overviewGrid.DataSource = state.Projects.ToArray();
        }
        if (!ReferenceEquals(previousHistory, state.History)) { previousHistory = state.History; FilterHistory(); }
        if (!ReferenceEquals(previousDetails, state.Details))
        { previousDetails = state.Details; shownDetails = state.Details; ShowDetails(); }
        UpdateProjectActions();
        settings.UpdateState(state);
    }
    private void Open(string? path)
    {
        try { if (path is null || !Directory.Exists(path)) throw new IOException("目录尚未创建。"); Process.Start(new ProcessStartInfo(path) { UseShellExecute = true }); }
        catch (Exception ex) { ShowError(CloudAccountPresentation.Error(ex)); }
    }
    private void FilterHistory()
    {
        var selectedCode = historyResult.SelectedIndex switch { 1 => 0, 2 => 2, 3 => 1, 4 => 130, _ => -1 };
        var text = historyFilter.Text.Trim();
        historyGrid.DataSource = (previousHistory ?? []).Where(r => (selectedCode == -1 || r.ExitCode == selectedCode) &&
            (!historySince.Checked || r.Time.ToLocalTime().Date >= historySince.Value.Date) &&
            (text.Length == 0 || r.Report?.Projects.Any(p => p.Destination.Contains(text, StringComparison.OrdinalIgnoreCase)) == true)).ToArray();
    }
    private void RefreshLanguage()
    {
        UiText.Set(pageTitle, currentPage);
        UiText.Set(this, "HarborBackup / {0}", UiText.Text(currentPage));
        var selected = historyResult.SelectedIndex;
        historyResult.Items.Clear();
        historyResult.Items.AddRange(new[] { "全部结果", "完成", "有待处理", "失败", "取消" }.Select(UiTextKey).ToArray());
        historyResult.SelectedIndex = Math.Max(0, selected);
        filter.PlaceholderText = UiText.Text("筛选项目、路径、动作或错误…");
        historyFilter.PlaceholderText = UiText.Text("按项目筛选记录…");
        filter.AccessibleName = filter.PlaceholderText; historyFilter.AccessibleName = historyFilter.PlaceholderText;
        historyResult.AccessibleName = UiText.Text("运行结果筛选"); historySince.AccessibleName = UiText.Text("起始日期筛选");
        UpdateState();
        projectGrid.Invalidate();
        if (currentPage == "待处理问题") ShowDetails();
    }
    private static string UiTextKey(string key) => UiText.Text(key);
    private void ShowError(string message)
    {
        if (ProblemNotificationPolicy.IsOffline(message))
        {
            controller.ObserveOffline();
            return;
        }
        UiText.Set(status, message);
        reportProblem?.Invoke(UiText.Text(message));
    }
    public bool CanExit() => !settings.Dirty || MessageBox.Show(this, UiText.Text("设置页有未保存的修改。退出并放弃这些修改？"), UiText.Text("退出程序"), MessageBoxButtons.OKCancel) == DialogResult.OK;
    public void ClosePermanently() { closingPermanently = true; Close(); }
    protected override void Dispose(bool disposing)
    {
        if (disposing) { catalogCancellation?.Cancel(); controller.Changed -= UpdateState; UiText.Changed -= RefreshLanguage; }
        base.Dispose(disposing);
    }
}
