using System.Text.Json.Nodes;
using HarborBackup.Core.Domain;
using System.Diagnostics;
using HarborBackup.Windows.Integration;
using HarborBackup.Desktop.Controllers;
using HarborBackup.Desktop.Presentation;
using HarborBackup.Desktop.Styling;
using HarborBackup.Desktop.Localization;
using HarborBackup.Desktop.Views.Dialogs;
using HarborBackup.Windows.Persistence.Configuration;

namespace HarborBackup.Desktop.Views.Pages;

internal sealed class SettingsPage : UserControl
{
    private readonly IConfigurationPresenter presenter;
    private readonly DesktopController controller;
    private ConfigurationDraft? draft;
    private readonly NumericUpDown interval = Number(1, 1440);
    private readonly NumericUpDown delay = Number(1, 1440);
    private readonly NumericUpDown timeout = Number(1, 1440);
    private readonly NumericUpDown automaticMiB = Number(0, 1048576);
    private readonly NumericUpDown automaticMinutes = Number(0, 1440);
    private readonly NumericUpDown uploadKiB = Number(0, 1048576);
    private readonly NumericUpDown downloadKiB = Number(0, 1048576);
    private readonly NumericUpDown days = Number(1, 365);
    private readonly NumericUpDown logDays = Number(1, 3650);
    private readonly NumericUpDown historyDays = Number(1, 3650);
    private readonly CheckBox metadataCache = new() { Name = "UseMetadataCache", AutoSize = true };
    private readonly CheckBox continueOnError = new() { AutoSize = true };
    private readonly Label feedback = new() { AutoSize = true, Dock = DockStyle.Top, Padding = new Padding(0, 4, 0, 4), Visible = false };
    private readonly TextBox cloud = new() { Name = "CloudRoot", ReadOnly = true, Dock = DockStyle.Top, BorderStyle = BorderStyle.None, BackColor = AppStyle.Surface };
    private readonly Label startup = new() { AutoSize = true, Anchor = AnchorStyles.Left };
    private readonly ComboBox language = new() { Name = "LanguageSelector", DropDownStyle = ComboBoxStyle.DropDownList, Width = 180 };
    private readonly Button save;
    private readonly FlowLayoutPanel actions;
    internal Control Actions => actions;
    private readonly List<(TextBox Field, Func<string> Resolve)> locations = [];
    private bool loading;
    private bool changingLanguage;
    private bool dirty;
    private Task? draftLoad;
    private readonly Panel scroll = new() { Name = "SettingsScroll", Dock = DockStyle.Fill, AutoScroll = true };

    public SettingsPage(DesktopController controller, IConfigurationPresenter presenter, Func<string, Task>? changeLanguage = null)
    {
        SuspendLayout();
        this.presenter = presenter; this.controller = controller;
        Name = "SettingsPage"; interval.Name = "IntervalMinutes";
        automaticMiB.Name = "AutomaticMaxTransferMiB"; automaticMinutes.Name = "AutomaticMaxDurationMinutes";
        uploadKiB.Name = "UploadKiBPerSecond"; downloadKiB.Name = "DownloadKiBPerSecond";
        logDays.Name = "LogRetentionDays"; historyDays.Name = "HistoryRetentionDays";
        Dock = DockStyle.Fill; AutoScaleDimensions = new SizeF(96, 96); AutoScaleMode = AutoScaleMode.Dpi;
        var table = new SettingsStack { Name = "SettingsRows", Dock = DockStyle.Top, Padding = new Padding(0, 0, 12, 8) };
        void Section(string caption) => table.Controls.Add(UiText.Bind(new Label { AutoSize = false, UseMnemonic = false,
            Padding = new Padding(0, 10, 0, 8), Margin = Padding.Empty, Font = new Font("Microsoft YaHei UI", 11, FontStyle.Bold) }, caption));
        SurfacePanel Row(string caption, Control value, Control? inlineValue = null, bool stretchValue = false)
        {
            // Content measures the row: no fixed row height and no clipped bottom-docked child.
            var card = new SurfacePanel { Name = "SettingRow",
                Padding = new Padding(14, 10, 14, 10), Margin = new Padding(0, 0, 0, 6) };
            var label = UiText.Bind(new Label { Name = "SettingCaption", AutoSize = true, Anchor = AnchorStyles.Left | AnchorStyles.Right,
                Margin = new Padding(0, 4, 12, 4) }, caption);
            // One measured field per row. Nested wrapping FlowLayoutPanels overestimate height
            // while the parent table is still measuring its available width.
            var row = new SettingRowLayout(label, value, inlineValue, stretchValue);
            card.Controls.Add(row); table.Controls.Add(card); return card;
        }
        Section("界面");
        language.Items.AddRange(["简体中文", "English"]); language.SelectedIndex = UiText.Language == "en-US" ? 1 : 0;
        Row("语言", AppStyle.Input(language));
        Section("本机与存储");
        async Task ManageAccountAsync()
        {
            using var dialog = new CloudAccountDialog(controller);
            dialog.ShowDialog(this);
            await controller.RefreshAsync();
            UpdateState(controller.State);
        }
        Row("OneDrive 账户连接", AppStyle.Button("管理账户与云端仓库", async (_, _) => await ManageAccountAsync()), AppStyle.Input(cloud));
        startup.ForeColor = AppStyle.Muted;
        Row("登录时启动", Inline(startup, AppStyle.Button("启用", async (_, _) => await StartupAsync(true)), AppStyle.Button("停用", async (_, _) => await StartupAsync(false))));
        Section("自动备份");
        Row("备份间隔", Unit(interval, "分钟")); Row("登录后启动延迟", Unit(delay, "分钟")); Row("单轮运行时限", Unit(timeout, "分钟"));
        Row("自动备份传输预算（0 为不限）", Unit(automaticMiB, "MiB"));
        Row("自动备份时间预算（0 为不限）", Unit(automaticMinutes, "分钟"));
        table.Controls.Add(UiText.Bind(new Label { AutoSize = false, ForeColor = AppStyle.Muted, Margin = new Padding(4, 0, 4, 8) },
            "预算按已启动文件的内容大小估算。达到预算后等待在途文件完成，可能略超上限；协议开销和云端内部复制不计。"));
        Section("传输限速");
        Row("上传速度上限（0 为不限）", Unit(uploadKiB, "KiB/s"));
        Row("下载速度上限（0 为不限）", Unit(downloadKiB, "KiB/s"));
        Section("校验与容错");
        Row("完整哈希核查", Unit(days, "天"));
        UiText.Bind(metadataCache, "启用"); Row("哈希缓存", metadataCache);
        UiText.Bind(continueOnError, "继续其他项目");
        Row("单个项目失败", continueOnError);
        Section("记录保留");
        Row("日志保留时间", Unit(logDays, "天")); Row("运行记录保留时间", Unit(historyDays, "天"));
        Section("存储位置");
        var data = controller.Paths.Data;
        var roots = new (string Caption, Func<string> Resolve)[] {
            ("软件目录", () => ApplicationPaths.InstallRoot()) };
        foreach (var location in roots)
        {
            var field = new TextBox { ReadOnly = true, Text = location.Resolve(), BorderStyle = BorderStyle.None, BackColor = AppStyle.Surface };
            locations.Add((field, location.Resolve));
            Row(location.Caption, AppStyle.Button("打开目录", (_, _) => OpenDirectory(location.Resolve())), AppStyle.Input(field));
        }
        void CloudPathRow(string name, string caption, Func<CloudFolders, string> folder)
        {
            string Location() => controller.CloudAccount.Enabled
                ? CloudAccountPresentation.Path(controller.CloudAccount.RootName, folder(controller.CloudAccount.Folders ?? new CloudFolders()))
                : UiText.Text("未设置");
            var field = new TextBox { Name = name, ReadOnly = true, Text = Location(), BorderStyle = BorderStyle.None, BackColor = AppStyle.Surface };
            locations.Add((field, Location));
            Row(caption, AppStyle.Input(field), stretchValue: true);
        }
        CloudPathRow("ArchiveDirectory", "历史版本目录", f => f.Archives);
        CloudPathRow("ConflictDirectory", "冲突目录", f => f.Conflicts);
        CloudPathRow("MetadataDirectory", "共享元信息目录", f => f.Metadata);
        var archivePolicy = new TextBox { Name = "ArchiveRetention", ReadOnly = true, BorderStyle = BorderStyle.None, BackColor = AppStyle.Surface };
        var conflictPolicy = new TextBox { Name = "ConflictRetention", ReadOnly = true, BorderStyle = BorderStyle.None, BackColor = AppStyle.Surface };
        locations.Add((archivePolicy, () => UiText.Text(controller.CloudAccount.ArchiveRetention == LocalRetention.KeepLocal ? "同时保留本地" : "仅云端保留")));
        locations.Add((conflictPolicy, () => UiText.Text(controller.CloudAccount.ConflictRetention == LocalRetention.KeepLocal ? "同时保留本地" : "仅云端保留")));
        Row("历史版本本机保留", AppStyle.Input(archivePolicy), stretchValue: true);
        Row("冲突副本本机保留", AppStyle.Input(conflictPolicy), stretchValue: true);
        Row("文件保留状态", AppStyle.Button("应用到已有副本", async (_, _) =>
        {
            try
            {
                await controller.ExclusiveAsync(() =>
                {
                    using var drive = new CloudAccountStore(controller.Paths.Data).Open();
                    CloudProtectionMaintenance.RunAsync(controller.Paths.Data, controller.CloudAccount, drive, default).GetAwaiter().GetResult();
                    return true;
                });
                UiText.Set(feedback, "设置已保存");
            }
            catch (Exception ex) { UiText.Set(feedback, CloudAccountPresentation.Error(ex)); }
        }));
        var cacheSize = new Label { AutoSize = true, Text = "—" };
        Row("可重建缓存", Inline(cacheSize, AppStyle.Button("统计占用", async (_, _) =>
        { try { var bytes = await controller.ExclusiveAsync(() => DataMaintenance.CacheBytes(data)); cacheSize.Text = $"{bytes / 1048576d:F1} MiB"; } catch (Exception ex) { UiText.Set(feedback, CloudAccountPresentation.Error(ex)); } }),
            AppStyle.Button("清理缓存", async (_, _) =>
        { try { var bytes = await controller.ExclusiveAsync(() => DataMaintenance.ClearDisposableData(data)); UiText.Set(feedback, "已清理 {0} MiB", (bytes / 1048576d).ToString("F1")); cacheSize.Text = "—"; } catch (Exception ex) { UiText.Set(feedback, CloudAccountPresentation.Error(ex)); } })));
        var advancedTools = Row("JSON 配置文件", Inline(
            AppStyle.Button("项目配置", (_, _) => OpenConfig(controller.Paths.ProjectConfig)),
            AppStyle.Button("系统配置", (_, _) => OpenConfig(controller.Paths.SystemConfig))));
        advancedTools.Name = "AdvancedToolsContent";
        var help = AppStyle.Button("帮助", (_, _) => { using var dialog = new HelpDialog(); dialog.ShowDialog(this); });
        help.Name = "SettingsHelp";
        save = AppStyle.Button("保存备份设置", async (_, _) => await SaveAsync(), true);
        actions = AppStyle.Toolbar(save, AppStyle.Button("重新加载", async (_, _) =>
        {
            if (!dirty || MessageBox.Show(this, UiText.Text("放弃本页未保存的修改并重新加载？"), UiText.Text("重新加载"), MessageBoxButtons.OKCancel) == DialogResult.OK)
                await LoadDraftAsync(force: true);
        }), help);
        actions.Name = "SettingsActions"; actions.Dock = DockStyle.None; actions.Anchor = AnchorStyles.Right;
        actions.WrapContents = false; actions.Margin = Padding.Empty;
        var footer = new TableLayoutPanel { Name = "SettingsFooter", Dock = DockStyle.Bottom, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink,
            ColumnCount = 1, RowCount = 1, Margin = Padding.Empty, Padding = new Padding(0, 4, 0, 0) };
        footer.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        footer.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100)); footer.Controls.Add(feedback);
        feedback.TextChanged += (_, _) => feedback.Visible = feedback.Text.Length > 0;
        scroll.Controls.Add(table); Controls.Add(scroll); Controls.Add(footer);
        foreach (var n in new[] { interval, delay, timeout, automaticMiB, automaticMinutes, uploadKiB, downloadKiB, days, logDays, historyDays }) n.ValueChanged += (_, _) => MarkDirty();
        continueOnError.CheckedChanged += (_, _) => MarkDirty();
        metadataCache.CheckedChanged += (_, _) => MarkDirty();
        language.SelectedIndexChanged += async (_, _) =>
        {
            if (changingLanguage) return;
            changingLanguage = true; language.Enabled = false;
            try
            {
                var selected = language.SelectedIndex == 1 ? "en-US" : "zh-CN";
                if (changeLanguage is not null) await changeLanguage(selected); else UiText.SetLanguage(selected);
            }
            catch (Exception ex) { UiText.Set(feedback, CloudAccountPresentation.Error(ex)); }
            finally { language.SelectedIndex = UiText.Language == "en-US" ? 1 : 0; language.Enabled = true; changingLanguage = false; }
        };
        UiText.Watch(this, () =>
        {
            interval.AccessibleName = UiText.Text("备份间隔"); delay.AccessibleName = UiText.Text("登录后启动延迟");
            timeout.AccessibleName = UiText.Text("单轮运行时限"); days.AccessibleName = UiText.Text("完整哈希核查");
            automaticMiB.AccessibleName = UiText.Text("自动备份传输预算（0 为不限）");
            automaticMinutes.AccessibleName = UiText.Text("自动备份时间预算（0 为不限）");
            uploadKiB.AccessibleName = UiText.Text("上传速度上限（0 为不限）");
            downloadKiB.AccessibleName = UiText.Text("下载速度上限（0 为不限）");
            logDays.AccessibleName = UiText.Text("日志保留时间"); historyDays.AccessibleName = UiText.Text("运行记录保留时间");
            metadataCache.AccessibleName = UiText.Text("哈希缓存");
            language.AccessibleName = UiText.Text("语言"); cloud.AccessibleName = UiText.Text("云端存储");
            continueOnError.AccessibleName = UiText.Text("单个项目失败");
        });
        ResumeLayout(true);
    }
    public bool Dirty => dirty;
    protected override void Dispose(bool disposing)
    {
        // The shell hosts these actions in its bottom bar; this page owns their handlers.
        if (disposing) actions.Dispose();
        base.Dispose(disposing);
    }
    private static NumericUpDown Number(int minimum, int maximum) => new() { Minimum = minimum, Maximum = maximum, Width = 100, TextAlign = HorizontalAlignment.Center };
    private static Control Inline(params Control[] controls)
        => new SettingsInlineLayout(controls);
    private static Control Unit(Control input, string unit) => Inline(AppStyle.Input(input), UiText.Bind(new Label { AutoSize = true, Anchor = AnchorStyles.Left }, unit));
    private void MarkDirty() { if (!loading) { dirty = true; UiText.Set(feedback, "有未保存的修改"); } }
    public void UpdateState(DesktopState state)
    {
        save.Enabled = !loading && !state.OperationBusy && draft is not null;
        foreach (var location in locations) { var path = location.Resolve(); if (location.Field.Text != path) location.Field.Text = path; }
        var root = CloudAccountPresentation.Summary(controller.CloudAccount);
        if (cloud.Text != root) cloud.Text = root;
    }
    public Task LoadDraftAsync(bool force = false)
    {
        if (draftLoad is { IsCompleted: false }) return draftLoad;
        if (draft is not null && !force) return Task.CompletedTask;
        return draftLoad = ReadDraftAsync();
    }
    private async Task ReadDraftAsync()
    {
        loading = true;
        scroll.Enabled = false; save.Enabled = false;
        try
        {
            var loaded = await presenter.LoadAsync();
            if (IsDisposed || Disposing) return;
            draft = loaded; var schedule = draft.System["schedule"]; var hash = draft.System["sync"]?["hash"];
            interval.Value = Math.Clamp(schedule?["intervalMinutes"]?.GetValue<int>() ?? 10, 1, 1440);
            delay.Value = Math.Clamp(schedule?["startDelayMinutes"]?.GetValue<int>() ?? 2, 1, 1440);
            timeout.Value = Math.Clamp(schedule?["executionTimeLimitMinutes"]?.GetValue<int>() ?? 120, 1, 1440);
            automaticMiB.Value = Math.Clamp(schedule?["automaticMaxTransferMiB"]?.GetValue<int>() ?? 0, 0, 1048576);
            automaticMinutes.Value = Math.Clamp(schedule?["automaticMaxDurationMinutes"]?.GetValue<int>() ?? 0, 0, 1440);
            uploadKiB.Value = Math.Clamp(draft.System["transfer"]?["uploadKiBPerSecond"]?.GetValue<int>() ?? 0, 0, 1048576);
            downloadKiB.Value = Math.Clamp(draft.System["transfer"]?["downloadKiBPerSecond"]?.GetValue<int>() ?? 0, 0, 1048576);
            days.Value = Math.Clamp(hash?["fullCheckIntervalDays"]?.GetValue<int>() ?? 7, 1, 365);
            continueOnError.Checked = draft.System["continueOnError"]?.GetValue<bool>() ?? true;
            metadataCache.Checked = hash?["useMetadataCache"]?.GetValue<bool>() ?? true;
            logDays.Value = Math.Clamp(draft.System["logging"]?["retentionDays"]?.GetValue<int>() ?? 30, 1, 3650);
            historyDays.Value = Math.Clamp(draft.System["history"]?["retentionDays"]?.GetValue<int>() ?? 90, 1, 3650);
            dirty = false; UiText.Set(feedback, "");
            var enabled = await Task.Run(() => new LoginStartup(controller.Paths).IsEnabled());
            if (!IsDisposed && !Disposing) UiText.Set(startup, enabled ? "已启用" : "未启用");
        }
        catch (Exception ex) { if (!IsDisposed && !Disposing) UiText.Set(feedback, CloudAccountPresentation.Error(ex)); }
        finally { loading = false; if (!IsDisposed && !Disposing) { scroll.Enabled = true; UpdateState(controller.State); } }
    }
    private static JsonObject Object(JsonObject parent, string key)
    { if (parent[key] is null) parent[key] = new JsonObject(); return parent[key]!.AsObject(); }
    private async Task SaveAsync()
    {
        if (draft is null) return;
        save.Enabled = false;
        try
        {
            var candidate = draft.Copy(); var schedule = Object(candidate.System, "schedule");
            schedule["intervalMinutes"] = (int)interval.Value; schedule["startDelayMinutes"] = (int)delay.Value; schedule["executionTimeLimitMinutes"] = (int)timeout.Value;
            schedule["automaticMaxTransferMiB"] = (int)automaticMiB.Value;
            schedule["automaticMaxDurationMinutes"] = (int)automaticMinutes.Value;
            var transfer = Object(candidate.System, "transfer");
            transfer["uploadKiBPerSecond"] = (int)uploadKiB.Value;
            transfer["downloadKiBPerSecond"] = (int)downloadKiB.Value;
            var hash = Object(Object(candidate.System, "sync"), "hash");
            hash["fullCheckIntervalDays"] = (int)days.Value; hash["useMetadataCache"] = metadataCache.Checked;
            candidate.System["continueOnError"] = continueOnError.Checked;
            Object(candidate.System, "logging")["retentionDays"] = (int)logDays.Value;
            Object(candidate.System, "history")["retentionDays"] = (int)historyDays.Value;
            await presenter.SaveAsync(candidate, ConfigurationPart.System); await LoadDraftAsync(force: true); UiText.Set(feedback, "设置已保存");
        }
        catch (Exception ex) { UiText.Set(feedback, CloudAccountPresentation.Error(ex)); }
        finally { UpdateState(controller.State); }
    }
    private async Task StartupAsync(bool enabled)
    {
        try
        {
            await controller.ExclusiveAsync(() => { new LoginStartup(controller.Paths).SetEnabled(enabled); return true; });
            UiText.Set(startup, enabled ? "已启用" : "未启用"); UiText.Set(feedback, "设置已保存");
        }
        catch (Exception ex) { UiText.Set(feedback, CloudAccountPresentation.Error(ex)); }
    }
    private void OpenDirectory(string path)
    {
        try { HarborBackup.Windows.FileSystem.SafePath.Validate(path); Directory.CreateDirectory(path); Process.Start(new ProcessStartInfo(path) { UseShellExecute = true }); }
        catch (Exception ex) { UiText.Set(feedback, CloudAccountPresentation.Error(ex)); }
    }
    private void OpenConfig(string path)
    {
        try
        {
            if (!File.Exists(path)) throw new IOException("配置尚未保存，请先添加项目或保存设置。");
            Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
        }
        catch (Exception ex) { UiText.Set(feedback, CloudAccountPresentation.Error(ex)); }
    }
}
