using HarborBackup.Core.Domain;
using HarborBackup.Desktop.Controllers;
using HarborBackup.Desktop.Localization;
using HarborBackup.Desktop.Presentation;
using HarborBackup.Desktop.Styling;
using HarborBackup.OneDrive.Authentication;
using HarborBackup.OneDrive.Transport;
using HarborBackup.Windows.Integration;
using HarborBackup.Windows.Persistence.Configuration;

namespace HarborBackup.Desktop.Views.Dialogs;

internal sealed class CloudAccountDialog : Form
{
    private readonly DesktopController controller;
    private readonly ICloudAccountSession session;
    private readonly CheckBox customApplication = new() { Name = "UseCustomCloudApplication", AutoSize = true };
    private readonly FlowLayoutPanel advanced = new() { Name = "CloudAdvancedSettings", AutoSize = true, FlowDirection = FlowDirection.TopDown, WrapContents = false, Dock = DockStyle.Fill, Visible = false };
    private readonly TextBox client = new() { Name = "CloudClientId", Width = 420 };
    private readonly TextBox rootName = new() { Name = "CloudRepositoryName", Text = "HarborBackup", Dock = DockStyle.Fill };
    private readonly Label accountLabel = Field("CloudAccountName");
    private readonly Label savedLocation = Field("SavedCloudLocation");
    private readonly Label location = Field("CloudBrowseLocation");
    private readonly Label target = Field("CloudTargetPreview");
    private readonly Label feedback = Field("CloudFeedback");
    private readonly ComboBox mode = new() { Name = "CloudSelectionMode", DropDownStyle = ComboBoxStyle.DropDownList, Width = 260 };
    private readonly ComboBox archive = new() { Name = "CloudArchiveRetention", DropDownStyle = ComboBoxStyle.DropDownList, Width = 220 };
    private readonly ComboBox conflict = new() { Name = "CloudConflictRetention", DropDownStyle = ComboBoxStyle.DropDownList, Width = 220 };
    private readonly ListBox folders = new AppListBox() { Name = "CloudFolders", Dock = DockStyle.Fill, Height = 130, DisplayMember = "Name" };
    private readonly TableLayoutPanel table = new() { Dock = DockStyle.Top, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, ColumnCount = 2, Padding = new Padding(8) };
    private readonly Button apply;
    private readonly Button details;
    private readonly Button configureFolders;
    private readonly Button refreshCloud;
    private readonly Button upCloud;
    private readonly Button useCurrentCloud;
    private readonly CancellationTokenSource cancellation = new();
    private readonly Stack<(string Id, string Path)> parents = new();
    private RemoteItem? parent;
    private RemoteItem? selected;
    private string currentPath = "";
    private string? selectedPath;
    private string errorDetails = "";
    private bool busy;
    private bool offlineFeedback;
    internal bool IsWorking => busy;
    private readonly TaskCompletionSource initialization = new(TaskCreationOptions.RunContinuationsAsynchronously);
    internal Task Initialization => initialization.Task;
    private bool closeWhenIdle;
    private CloudAccountSettings settings = new();
    private CloudFolders layout = new();

    internal CloudAccountDialog(DesktopController controller, ICloudAccountSession? session = null)
    {
        this.controller = controller;
        this.session = session ?? new CloudAccountSession(controller.Paths.Data);
        AppStyle.Configure(this, "OneDrive 账户连接", new Size(820, 730));
        MinimumSize = new Size(680, 620);
        table.ColumnStyles.Add(new(SizeType.AutoSize));
        table.ColumnStyles.Add(new(SizeType.Percent, 100));
        void Row(string title, Control field)
        {
            var row = table.RowCount++;
            table.RowStyles.Add(new(SizeType.AutoSize));
            var label = UiText.Bind(new Label { AutoSize = true, Anchor = AnchorStyles.Left, Margin = new Padding(0, 8, 12, 8) }, title);
            field.Anchor = AnchorStyles.Left | AnchorStyles.Right;
            field.Margin = new Padding(0, 5, 0, 5);
            table.Controls.Add(label, 0, row);
            table.Controls.Add(field, 1, row);
        }
        Row("已连接账户", accountLabel);
        Row("已保存仓库", savedLocation);
        Row("账户操作", AppStyle.Toolbar(
            AppStyle.Button("连接 OneDrive", async (_, _) => await RunAsync("正在连接账户…", async () =>
            {
                if (customApplication.Checked && !OneDriveApplication.IsValid(client.Text.Trim()))
                    throw new InvalidOperationException(T("请输入有效的自定义 Client ID。"));
                if (!customApplication.Checked && !OneDriveApplication.HasDefaultClientId)
                    throw new InvalidOperationException(T("此开发构建尚未配置默认登录应用，请联系发布者或在高级设置中使用自己的应用。"));
                settings = await this.session.ConnectAsync(customApplication.Checked ? client.Text.Trim() : null, cancellation.Token);
                parents.Clear();
                await RestoreBrowserAsync();
                ShowStatus(CloudAccountPresentation.Summary(settings));
            })),
            AppStyle.Button("断开连接", async (_, _) => await RunAsync("正在断开连接…", async () =>
            {
                settings = await this.session.DisconnectAsync(cancellation.Token);
                ShowStatus(T("需要重新授权"));
            }))));
        Row("高级设置", AppStyle.Button("展开 / 收起", (_, _) => advanced.Visible = !advanced.Visible));
        UiText.Bind(customApplication, "使用自定义应用 Client ID");
        var clientInput = AppStyle.Input(client);
        clientInput.Visible = false;
        customApplication.CheckedChanged += (_, _) => clientInput.Visible = customApplication.Checked;
        advanced.Controls.Add(customApplication);
        advanced.Controls.Add(clientInput);
        advanced.SizeChanged += (_, _) => clientInput.Width = Math.Max(180, advanced.ClientSize.Width - 8);
        var advancedRow = table.RowCount++;
        table.RowStyles.Add(new(SizeType.AutoSize));
        table.Controls.Add(advanced, 1, advancedRow);

        mode.Items.AddRange([T("创建新仓库"), T("使用已有文件夹")]);
        mode.SelectedIndex = 0;
        mode.SelectedIndexChanged += (_, _) => { rootName.Enabled = mode.SelectedIndex == 0; RefreshLabels(); };
        Row("仓库选择方式", AppStyle.Input(mode));
        Row("当前浏览位置", location);
        Row("云端文件夹", AppStyle.Input(folders));
        refreshCloud = AppStyle.Button("刷新记录", async (_, _) => await RunAsync("正在读取云端目录…", () => LoadFoldersAsync(parent?.Id, currentPath)));
        upCloud = AppStyle.Button("上一级", async (_, _) => await RunAsync("正在读取云端目录…", async () =>
        {
            var previous = parents.Count > 0 ? parents.Peek() : ((string Id, string Path)?)null;
            await LoadFoldersAsync(previous?.Id, previous?.Path ?? "");
            if (previous is not null) parents.Pop();
        }));
        useCurrentCloud = AppStyle.Button("使用当前文件夹", (_, _) =>
        {
            if (parent is null) return;
            mode.SelectedIndex = 1; selected = parent; selectedPath = currentPath; RefreshLabels();
        });
        Row("浏览云端", AppStyle.Toolbar(refreshCloud, upCloud, useCurrentCloud));
        folders.SelectedIndexChanged += (_, _) =>
        {
            if (folders.SelectedItem is not RemoteItem item) return;
            selected = item; selectedPath = item.DisplayPath?.Trim('/') ?? Join(currentPath, item.Name);
            RefreshLabels();
        };
        folders.DoubleClick += async (_, _) =>
        {
            if (folders.SelectedItem is not RemoteItem item || parent is null) return;
            var previous = (parent.Id, currentPath);
            var nextPath = item.DisplayPath?.Trim('/') ?? Join(currentPath, item.Name);
            await RunAsync("正在读取云端目录…", async () =>
            {
                await LoadFoldersAsync(item.Id, nextPath);
                parents.Push(previous);
            });
        };
        rootName.TextChanged += (_, _) => RefreshLabels();
        Row("新仓库文件夹名称", AppStyle.Input(rootName));
        Row("将使用的云端位置", target);
        configureFolders = AppStyle.Button("设置云端子目录", (_, _) =>
        {
            using var dialog = new CloudFoldersDialog(layout);
            if (dialog.ShowDialog(this) == DialogResult.OK) layout = dialog.Selected;
        });
        Row("云端子目录", configureFolders);
        mode.SelectedIndexChanged += (_, _) => UpdateCloudControls();
        foreach (var selector in new[] { archive, conflict }) selector.Items.AddRange([T("仅云端保留"), T("同时保留本地")]);
        Row("历史版本本机保留", AppStyle.Input(archive));
        Row("冲突副本本机保留", AppStyle.Input(conflict));

        apply = AppStyle.Button("应用云端存储设置", async (_, _) => await RunAsync("正在应用云端存储设置…", async () =>
        {
            if (parent is null) throw new InvalidOperationException(T("请先连接并浏览云端目录。"));
            var create = mode.SelectedIndex == 0;
            if (!create && selected is null) throw new InvalidOperationException(T("请选择仓库文件夹。"));
            var selection = new CloudStorageSelection(create, create ? parent.Id : selected!.Id, rootName.Text.Trim(),
                create ? Join(currentPath, rootName.Text.Trim()) : selectedPath ?? selected!.Name,
                layout, archive.SelectedIndex == 1 ? LocalRetention.KeepLocal : LocalRetention.OnlineOnly,
                conflict.SelectedIndex == 1 ? LocalRetention.KeepLocal : LocalRetention.OnlineOnly);
            selection = selection with { RetentionOnly = settings.Enabled && !create && selection.FolderId == settings.RootId &&
                layout == (settings.Folders ?? new CloudFolders()) &&
                (selection.ArchiveRetention != settings.ArchiveRetention || selection.ConflictRetention != settings.ConflictRetention) };
            settings = await this.session.ApplyAsync(settings, selection, cancellation.Token);
            layout = settings.Folders ?? new CloudFolders();
            // Keep the applied target explicit; saving policy again must not create a nested repository.
            selected = new(settings.RootId!, settings.RootName.Split('/').Last(), "", 0, true, DisplayPath: settings.RootName);
            selectedPath = settings.RootName;
            mode.SelectedIndex = 1;
            ShowStatus(T("云端仓库和保留策略已保存。"));
        }), true);
        apply.Name = "ApplyCloudStorage";
        details = AppStyle.Button("查看详情", (_, _) => MessageBox.Show(this, errorDetails, T("状态"), MessageBoxButtons.OK, MessageBoxIcon.Information));
        details.Name = "CloudErrorDetails"; details.Visible = false;
        var close = AppStyle.Button("关闭窗口", (_, _) => Close());
        close.Name = "CloudClose";
        var actions = AppStyle.Toolbar(apply, close);
        actions.Dock = DockStyle.None; actions.Anchor = AnchorStyles.Right; actions.WrapContents = false;
        var footer = new TableLayoutPanel { Name = "CloudFooter", AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink,
            Dock = DockStyle.Fill, ColumnCount = 2, RowCount = 2, Padding = new Padding(8, 6, 8, 0), Margin = Padding.Empty };
        footer.ColumnStyles.Add(new(SizeType.Percent, 100)); footer.ColumnStyles.Add(new(SizeType.AutoSize));
        footer.RowStyles.Add(new(SizeType.AutoSize)); footer.RowStyles.Add(new(SizeType.AutoSize));
        feedback.Dock = DockStyle.Top; feedback.Margin = new Padding(0, 0, 0, 6);
        footer.Controls.Add(feedback, 0, 0); footer.SetColumnSpan(feedback, 2);
        footer.Controls.Add(details, 0, 1); footer.Controls.Add(actions, 1, 1);
        footer.SizeChanged += (_, _) => feedback.MaximumSize = new Size(Math.Max(100, footer.ClientSize.Width - footer.Padding.Horizontal), 0);
        var scroll = new Panel { Name = "CloudSettingsScroll", Dock = DockStyle.Fill, AutoScroll = true, Margin = Padding.Empty };
        scroll.Controls.Add(table);
        var shell = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 2, Padding = new Padding(8) };
        shell.ColumnStyles.Add(new(SizeType.Percent, 100));
        shell.RowStyles.Add(new(SizeType.Percent, 100)); shell.RowStyles.Add(new(SizeType.AutoSize));
        shell.Controls.Add(scroll, 0, 0); shell.Controls.Add(footer, 0, 1); Controls.Add(shell);
        Shown += async (_, _) =>
        {
            await RunAsync("正在加载账户设置…", async () =>
        {
            settings = await Task.Run(this.session.Read);
            client.Text = settings.ClientId;
            customApplication.Checked = !string.IsNullOrWhiteSpace(settings.ClientId) &&
                !string.Equals(settings.ClientId, OneDriveApplication.DefaultClientId, StringComparison.OrdinalIgnoreCase);
            advanced.Visible = customApplication.Checked;
            layout = settings.Folders ?? new CloudFolders();
            archive.SelectedIndex = settings.ArchiveRetention == LocalRetention.KeepLocal ? 1 : 0;
            conflict.SelectedIndex = settings.ConflictRetention == LocalRetention.KeepLocal ? 1 : 0;
            if (settings.Enabled && settings.RootId is not null)
            {
                parent = selected = new(settings.RootId, settings.RootName.Split('/').Last(), "", 0, true, DisplayPath: settings.RootName);
                currentPath = selectedPath = settings.RootName;
                mode.SelectedIndex = 1;
            }
            RefreshLabels();
            if (settings.AccountId is not null && !settings.RequiresAuthorization) await RestoreBrowserAsync();
            ShowStatus(CloudAccountPresentation.Summary(settings));
            UpdateCloudControls();
            }, exclusive: false);
            initialization.TrySetResult();
        };
        FormClosing += (_, e) =>
        {
            if (busy) { closeWhenIdle = true; cancellation.Cancel(); e.Cancel = true; ShowStatus(T("正在停止，等待安全边界")); }
        };
        FormClosed += (_, _) => cancellation.Dispose();
        controller.Changed += CheckConnectivity;
        Disposed += (_, _) => controller.Changed -= CheckConnectivity;
        UpdateCloudControls();
        ResumeLayout(true);
    }

    private static Label Field(string name) => new() { Name = name, AutoSize = true, Dock = DockStyle.Top, UseMnemonic = false };
    private static string T(string value) => UiText.Text(value);
    private static string Join(string path, string name) => string.IsNullOrEmpty(path) ? name : path.TrimEnd('/') + "/" + name;
    private void ShowStatus(string message, string detail = "")
    {
        feedback.Text = message; errorDetails = detail; details.Visible = detail.Length > 0;
    }
    private void CheckConnectivity()
    {
        if (!offlineFeedback || controller.State.NetworkOffline || IsDisposed || Disposing) return;
        offlineFeedback = false;
        ShowStatus(CloudAccountPresentation.Summary(settings));
    }
    private bool CloudConnected => settings.AccountId is not null && !settings.RequiresAuthorization;
    private void UpdateCloudControls()
    {
        var connected = CloudConnected;
        // A saved repository keeps retention-only edits local even when browsing needs reauthorization.
        var canApply = connected || settings.Enabled;
        refreshCloud.Enabled = upCloud.Enabled = useCurrentCloud.Enabled = connected;
        apply.Enabled = canApply;
        configureFolders.Enabled = canApply && mode.SelectedIndex == 0;
    }
    private void RefreshLabels()
    {
        accountLabel.Text = settings.AccountName ?? T("尚未连接");
        savedLocation.Text = CloudAccountPresentation.Summary(settings);
        location.Text = parent is null ? T("未加载") : CloudAccountPresentation.Path(currentPath);
        var path = mode.SelectedIndex == 0 ? (parent is null ? null : Join(currentPath, rootName.Text.Trim())) : selectedPath;
        target.Text = path is null ? T("请选择仓库文件夹。") : CloudAccountPresentation.Path(path);
    }
    private async Task RestoreBrowserAsync()
    {
        await LoadFoldersAsync(settings.Enabled ? settings.RootId : null, settings.Enabled ? settings.RootName : "");
        mode.SelectedIndex = settings.Enabled ? 1 : 0;
        rootName.Enabled = mode.SelectedIndex == 0;
    }
    private async Task LoadFoldersAsync(string? id, string path)
    {
        var listing = await session.BrowseAsync(settings, id, cancellation.Token);
        // Update the browser only after a complete listing. Failure preserves the user's selection.
        var previous = parent?.Id == listing.Current.Id ? selected : null;
        parent = listing.Current;
        currentPath = parent.DisplayPath?.Trim('/') ?? path;
        folders.DataSource = listing.Folders;
        folders.SelectedIndex = -1;
        selected = previous is null || previous.Id == parent.Id ? parent
            : listing.Folders.SingleOrDefault(item => item.Id == previous.Id);
        selectedPath = selected is null ? null : selected.Id == parent.Id ? currentPath
            : selected.DisplayPath?.Trim('/') ?? Join(currentPath, selected.Name);
        if (selected is not null && selected.Id != parent.Id) folders.SelectedItem = selected;
        RefreshLabels();
    }
    private async Task RunAsync(string progress, Func<Task> action, bool exclusive = true)
    {
        if (busy) return;
        busy = true; table.Enabled = false; apply.Enabled = false;
        offlineFeedback = false;
        ShowStatus(T(progress));
        try
        {
            if (exclusive) await controller.AccountOperationAsync(action);
            else await action();
            if (feedback.Text == T(progress)) ShowStatus(CloudAccountPresentation.Summary(settings));
        }
        catch (OperationCanceledException) { ShowStatus(T("操作已取消，已保存的设置保持不变。")); }
        catch (Exception ex)
        {
            if (ex is AccountAuthorizationRequiredException) settings = settings with { RequiresAuthorization = true };
            var translated = CloudAccountPresentation.Error(ex);
            var summary = ex is AccountAuthorizationRequiredException ? "需要重新授权" : progress switch
            {
                "正在应用云端存储设置…" => "云端存储设置未保存，请查看详情。",
                "正在连接账户…" => "账户连接未完成，请查看详情。",
                "正在读取云端目录…" or "正在加载账户设置…" => "无法读取云端目录，请查看详情。",
                _ => "操作未完成，请查看详情。"
            };
            var networkMessage = CloudNetworkAvailability.FriendlyMessage(ex);
            if (networkMessage == CloudNetworkAvailability.Offline)
            {
                offlineFeedback = true;
                ShowStatus(translated);
                controller.ObserveOffline();
            }
            else if (networkMessage is not null) ShowStatus(translated);
            else ShowStatus(T(summary), translated);
        }
        finally
        {
            busy = false; table.Enabled = true; UpdateCloudControls(); RefreshLabels();
            if (closeWhenIdle) Close();
        }
    }
}
