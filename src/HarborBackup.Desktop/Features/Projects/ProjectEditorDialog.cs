using System.Text.Json.Nodes;
using System.Diagnostics;
using HarborBackup.Core.Domain;
using HarborBackup.Desktop.Localization;
using HarborBackup.Desktop.Presentation;
using HarborBackup.Desktop.Styling;
using HarborBackup.Windows.Persistence.Configuration;

namespace HarborBackup.Desktop.Views.Dialogs;

internal sealed class ProjectEditorDialog : Form
{
    private readonly IConfigurationPresenter presenter;
    private readonly ConfigurationDraft draft;
    private readonly int? index;
    private readonly bool global;
    private readonly TextBox source = AppStyle.Text();
    private readonly TextBox destination = AppStyle.Text();
    private readonly TextBox directories = AppStyle.Lines();
    private readonly TextBox files = AppStyle.Lines();
    private readonly ComboBox automaticMode = new() { Name = "AutomaticBackupMode", DropDownStyle = ComboBoxStyle.DropDownList, Dock = DockStyle.Top };
    private readonly Dictionary<string, CheckBox> presetChoices = [];
    private readonly Label feedback = AppStyle.Label("修改仅保存在草稿中。", 65);
    private readonly FlowLayoutPanel actions;
    private bool busy;
    private readonly Button cancel;
    private readonly ProgressBar activity = new() { Name = "DraftPreviewActivity", Dock = DockStyle.Top, Height = 5,
        Style = ProgressBarStyle.Marquee, MarqueeAnimationSpeed = 35, Visible = false };
    private readonly System.Windows.Forms.Timer progressTimer = new() { Interval = 250 };
    private readonly Stopwatch elapsed = new();
    private readonly ProgressMailbox progress = new();
    private Control? editorFields;
    private CancellationTokenSource? previewCancellation;
    private bool closeAfterCancel;
    private string? cloudFolderId;
    private readonly string newProjectId = HarborBackup.Windows.Integration.DeviceIdentity.Digest(Guid.NewGuid().ToString("N"));

    public ProjectEditorDialog(IConfigurationPresenter presenter, ConfigurationDraft draft, int? index, string? cloud, bool global = false)
    {
        this.presenter = presenter;
        this.draft = draft.Copy();
        this.index = index;
        this.global = global;
        source.Name = "SourcePath";
        source.AccessibleName = UiText.Text("本地目录");
        destination.Name = "Destination";
        destination.AccessibleName = UiText.Text("目标相对路径");
        directories.Name = "ExcludedDirectories"; files.Name = "ExcludedFiles";
        directories.AccessibleName = UiText.Text("排除目录，每行一项");
        files.AccessibleName = UiText.Text("排除文件，每行一项");
        AppStyle.Configure(this, global ? "全局排除规则" : index is null ? "添加备份项目" : "编辑备份项目", new Size(780, 740));
        MinimumSize = new Size(650, 580);
        Padding = new Padding(20);
        var scroll = new Panel { Dock = DockStyle.Fill, AutoScroll = true };
        editorFields = scroll; feedback.Name = "DraftPreviewFeedback";
        var fields = new TableLayoutPanel { Dock = DockStyle.Top, AutoSize = true, ColumnCount = 1, Padding = new Padding(0, 0, 16, 0) };
        fields.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        void Add(Control c) { c.Margin = new Padding(0, 0, 0, 12); fields.Controls.Add(c); }
        var heading = AppStyle.Label(global ? "全局排除规则" : index is null ? "添加备份项目" : "编辑备份项目", 44);
        heading.Font = new Font(Font.FontFamily, 18, FontStyle.Bold); Add(heading);
        var record = global ? this.draft.Projects : index is { } i ? this.draft.Projects["folders"]![i]!.AsObject() : new JsonObject();
        if (!global)
        {
            Add(AppStyle.Label("项目 ID", 28));
            var identityInput = AppStyle.Input(new TextBox { Name = "ProjectId", ReadOnly = true, Dock = DockStyle.Top,
                Text = record["id"]?.GetValue<string>() ?? newProjectId, AccessibleName = UiText.Text("项目 ID") });
            identityInput.Dock = DockStyle.Top;
            Add(identityInput);
        }
        source.Text = record["source"]?.GetValue<string>() ?? "";
        destination.Text = record["destination"]?.GetValue<string>() ?? "";
        cloudFolderId = record["cloudFolderId"]?.GetValue<string>();
        directories.Text = ReadLines(record["excludedDirectories"]);
        files.Text = ReadLines(record["excludedFiles"]);
        var initialMode = ReadAutomaticMode(record);
        var selectedPresets = ExclusionPresets.Resolve(ReadLines(this.draft.Projects["builtInExclusions"])
            .Split([ '\r', '\n' ], StringSplitOptions.RemoveEmptyEntries));
        if (global)
        {
            var presetCard = new SurfacePanel { Name = "BuiltInExclusions", Dock = DockStyle.Top,
                AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, Padding = new Padding(16, 10, 16, 12) };
            var choices = new TableLayoutPanel { Dock = DockStyle.Top, AutoSize = true, ColumnCount = 2 };
            choices.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 58));
            choices.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 42));
            foreach (var preset in ExclusionPresets.All)
            {
                var choice = new CheckBox { Name = "Preset_" + preset.Id, AutoSize = true, Anchor = AnchorStyles.Left,
                    Checked = selectedPresets.Any(p => p.Id == preset.Id), Margin = new Padding(0, 5, 8, 5) };
                UiText.Watch(choice, () => { choice.Text = ExclusionPresentation.PresetName(preset.Id); choice.AccessibleName = choice.Text; });
                choice.AccessibleDescription = string.Join("; ", preset.Files);
                presetChoices.Add(preset.Id, choice);
                choices.Controls.Add(choice);
                choices.Controls.Add(new Label { Text = string.Join("; ", preset.Files), AutoSize = true,
                    Anchor = AnchorStyles.Left, Margin = new Padding(0, 5, 0, 5) });
            }
            presetCard.Controls.Add(choices);
            presetCard.Controls.Add(AppStyle.Label("内置规则（勾选后对所有项目生效）", 32));
            Add(presetCard);
        }
        if (!global)
        {
            var sourceCard = new SurfacePanel { Dock = DockStyle.Top, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, Padding = new Padding(16, 10, 16, 14) };
            var sourceBrowse = AppStyle.Button("浏览…", (_, _) =>
            { using var picker = new FolderBrowserDialog(); if (picker.ShowDialog(this) == DialogResult.OK) source.Text = picker.SelectedPath; });
            var sourceRow = PathPickerRow(source, sourceBrowse);
            sourceCard.Controls.Add(sourceRow); sourceCard.Controls.Add(AppStyle.Label("本地目录", 30)); Add(sourceCard);
            source.PlaceholderText = UiText.Text("选择文件夹，或输入包含环境变量的路径");
            var targetCard = new SurfacePanel { Dock = DockStyle.Top, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, Padding = new Padding(16, 10, 16, 14) };
            var targetRows = new TableLayoutPanel { Dock = DockStyle.Top, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, ColumnCount = 1, RowCount = 2, Margin = Padding.Empty };
            targetRows.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            for (var row = 0; row < 2; row++) targetRows.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            targetRows.Controls.Add(AppStyle.Label("云端目录（相对 OneDrive 根目录）", 30));
            var targetBrowse = AppStyle.Button("浏览云端…", (_, _) =>
            {
                using var picker = new CloudTargetDialog(presenter.BrowseCloudAsync, presenter.PrefetchCloudAsync,
                    cloudFolderId, destination.Text);
                if (picker.ShowDialog(this) == DialogResult.OK) { destination.Text = picker.Destination; cloudFolderId = picker.FolderId; }
            });
            var targetInput = PathPickerRow(destination, targetBrowse);
            targetRows.Controls.Add(targetInput);
            foreach (Control control in targetRows.Controls) control.Margin = Padding.Empty;
            targetCard.Controls.Add(targetRows); Add(targetCard);
            var aligningBrowseButtons = false;
            void AlignBrowseButtons()
            {
                if (aligningBrowseButtons) return;
                aligningBrowseButtons = true;
                try
                {
                    var sizes = new[] { sourceBrowse.GetPreferredSize(Size.Empty), targetBrowse.GetPreferredSize(Size.Empty) };
                    var height = new[] { sourceRow.Controls[0], targetInput.Controls[0] }
                        .Max(frame => Math.Max(frame.MinimumSize.Height, frame.GetPreferredSize(Size.Empty).Height));
                    var size = new Size(sizes.Max(s => s.Width), Math.Max(height, sizes.Max(s => s.Height)));
                    foreach (var (row, button) in new[] { (sourceRow, sourceBrowse), (targetInput, targetBrowse) })
                    {
                        if (row.ColumnStyles[1].Width != size.Width) row.ColumnStyles[1].Width = size.Width;
                        button.Size = size;
                        row.Controls[0].Height = size.Height;
                    }
                }
                finally { aligningBrowseButtons = false; }
            }
            sourceRow.Layout += (_, _) => AlignBrowseButtons();
            targetInput.Layout += (_, _) => AlignBrowseButtons();
            foreach (var button in new[] { sourceBrowse, targetBrowse })
            {
                button.FontChanged += (_, _) => AlignBrowseButtons();
                button.TextChanged += (_, _) => AlignBrowseButtons();
                button.DpiChangedAfterParent += (_, _) => AlignBrowseButtons();
            }
            Shown += (_, _) => AlignBrowseButtons();
            AlignBrowseButtons();
            destination.PlaceholderText = UiText.Text("例如 Projects/MyProject");
            var automaticCard = new SurfacePanel { Name = "AutomaticBackupOptions", Dock = DockStyle.Top,
                AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, Padding = new Padding(16, 10, 16, 14) };
            var automaticHint = new Label { Name = "AutomaticBackupModeHint", Dock = DockStyle.Top, AutoSize = true,
                ForeColor = AppStyle.Muted, Padding = new Padding(0, 8, 0, 0) };
            var automaticRules = new Label { Name = "AutomaticBackupRules", Dock = DockStyle.Top, AutoSize = true,
                ForeColor = AppStyle.Muted, Padding = new Padding(0, 4, 0, 0) };
            UiText.Set(automaticRules, "删除不联动；冲突需确认。");
            void UpdateAutomaticHint() => UiText.Set(automaticHint, automaticMode.SelectedIndex switch
            {
                1 => "自动上传本地更新、下载云端更新。",
                2 => "下载云端更新，不上传本地业务文件。",
                _ => "仅将本地更新上传到 OneDrive。"
            });
            automaticMode.SelectedIndexChanged += (_, _) => UpdateAutomaticHint();
            UiText.Watch(automaticMode, () =>
            {
                var selected = automaticMode.SelectedIndex < 0 ? (int)initialMode : automaticMode.SelectedIndex;
                automaticMode.BeginUpdate();
                try
                {
                    automaticMode.Items.Clear();
                    automaticMode.Items.AddRange([UiText.Text("仅上传"), UiText.Text("双向备份"), UiText.Text("仅下载")]);
                    automaticMode.SelectedIndex = selected;
                    automaticMode.AccessibleName = UiText.Text("自动备份模式");
                }
                finally { automaticMode.EndUpdate(); }
            });
            automaticCard.SizeChanged += (_, _) =>
            {
                var width = Math.Max(100, automaticCard.ClientSize.Width - automaticCard.Padding.Horizontal);
                automaticHint.MaximumSize = automaticRules.MaximumSize = new Size(width, 0);
            };
            automaticCard.Controls.Add(automaticRules); automaticCard.Controls.Add(automaticHint);
            var automaticInput = AppStyle.Input(automaticMode); automaticInput.Dock = DockStyle.Top;
            automaticCard.Controls.Add(automaticInput);
            automaticCard.Controls.Add(AppStyle.Label("自动备份模式", 30)); Add(automaticCard);
        }
        var exclusions = new SurfacePanel { Dock = DockStyle.Top, Height = 218, Padding = new Padding(16, 10, 16, 12) };
        var ruleColumns = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, RowCount = 1 };
        ruleColumns.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50)); ruleColumns.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
        foreach (var (caption, input) in new[] { ("排除目录", directories), ("排除文件", files) })
        {
            var column = new Panel { Dock = DockStyle.Fill, Margin = new Padding(0, 0, input == directories ? 12 : 0, 0) };
            input.Dock = DockStyle.Fill;
            column.Controls.Add(AppStyle.Input(input)); column.Controls.Add(AppStyle.Label(caption, 32)); ruleColumns.Controls.Add(column);
        }
        exclusions.Controls.Add(ruleColumns); exclusions.Controls.Add(AppStyle.Label(global ? "自定义规则（每行一项）" : "项目排除规则", 32)); Add(exclusions);
        if (!global)
        {
            var inherited = new Label { AutoSize = true, Dock = DockStyle.Top };
            UiText.Watch(inherited, () => inherited.Text = UiText.Text("全局规则\n内置规则：{0}\n目录：{1}\n文件：{2}",
                ExclusionPresentation.Presets(selectedPresets.Select(p => p.Id)), ReadLines(this.draft.Projects["excludedDirectories"]).Replace(Environment.NewLine, "; "),
                ReadLines(this.draft.Projects["excludedFiles"]).Replace(Environment.NewLine, "; "))); Add(inherited);
            fields.SizeChanged += (_, _) => inherited.MaximumSize = new Size(Math.Max(100, fields.ClientSize.Width - 16), 0);
        }
        feedback.Dock = DockStyle.Bottom;
        feedback.AutoSize = true; feedback.Dock = DockStyle.Top; feedback.Padding = new Padding(0, 10, 0, 10);
        UiText.Set(feedback, global ? "排除规则将在下次扫描时生效，不删除已有文件，也不重置备份状态。" : "保存仅更新配置。");
        cancel = AppStyle.Button("取消", (_, _) => { if (busy) StopPreview(false); else Close(); });
        cancel.Name = "CancelDraftOperation";
        actions = AppStyle.DialogActions(AppStyle.Button("检查配置", async (_, _) => await PerformAsync(false, false)),
            AppStyle.Button("预览草稿", async (_, _) => await PerformAsync(true, false)),
            AppStyle.Button("保存配置", async (_, _) => await PerformAsync(false, true), true),
            cancel, AppStyle.Button("帮助", (_, _) => { using var help = new HelpDialog(); help.ShowDialog(this); }));
        actions.Dock = DockStyle.Bottom;
        scroll.Controls.Add(fields);
        var footer = new TableLayoutPanel { Dock = DockStyle.Bottom, AutoSize = true, ColumnCount = 1 };
        footer.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        footer.Controls.Add(activity); footer.Controls.Add(feedback); footer.Controls.Add(actions);
        footer.SizeChanged += (_, _) => feedback.MaximumSize = new Size(Math.Max(100, footer.ClientSize.Width), 0);
        progressTimer.Tick += (_, _) => UpdatePreviewFeedback();
        Disposed += (_, _) => { progressTimer.Dispose(); previewCancellation?.Cancel(); };
        Controls.Add(scroll);
        Controls.Add(footer);
        destination.TextChanged += (_, _) => cloudFolderId = null;
        FormClosing += (_, e) => { if (busy) { e.Cancel = true; StopPreview(true); } };
        Shown += (_, _) => { if (!global && !source.IsDisposed) source.Focus(); };
        ResumeLayout(true);
    }

    private static TableLayoutPanel PathPickerRow(TextBox input, Button browse)
    {
        var row = new TableLayoutPanel { Name = input.Name + "Row", Dock = DockStyle.Top, AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink, ColumnCount = 2, RowCount = 1, Margin = Padding.Empty };
        row.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        row.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, browse.GetPreferredSize(Size.Empty).Width));
        row.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        var frame = AppStyle.Input(input);
        frame.Dock = DockStyle.None; frame.Anchor = AnchorStyles.Left | AnchorStyles.Right;
        frame.Margin = new Padding(0, 0, 10, 0);
        browse.Name = input.Name + "Browse";
        browse.AutoSize = false; browse.Anchor = AnchorStyles.Right; browse.Margin = Padding.Empty;
        row.Controls.Add(frame, 0, 0); row.Controls.Add(browse, 1, 0);
        return row;
    }

    internal static string ReadLines(JsonNode? node) => node is JsonArray array
        ? string.Join(Environment.NewLine, array.Where(v => v is not null).Select(v => v!.GetValue<string>())) : "";
    internal static JsonArray WriteLines(string text) => new(text.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
        .Select(s => (JsonNode?)JsonValue.Create(s)).ToArray());

    private ConfigurationDraft Collect()
    {
        var candidate = draft.Copy();
        var folders = candidate.Projects["folders"]!.AsArray();
        var record = global ? candidate.Projects : index is { } i ? folders[i]!.AsObject() : new JsonObject();
        if (!global)
        {
            record["source"] = source.Text.Trim();
            record["destination"] = destination.Text.Trim();
            record["automaticBackupMode"] = automaticMode.SelectedIndex switch { 1 => "bidirectional", 2 => "download", _ => "upload" };
            record.Remove("automaticBidirectional");
            if (cloudFolderId is null) record.Remove("cloudFolderId"); else record["cloudFolderId"] = cloudFolderId;
            if (index is null) { record["id"] = newProjectId; folders.Add(record); }
        }
        record["excludedDirectories"] = WriteLines(directories.Text);
        record["excludedFiles"] = WriteLines(files.Text);
        if (global) record["builtInExclusions"] = new JsonArray(presetChoices.Where(p => p.Value.Checked)
            .Select(p => (JsonNode?)JsonValue.Create(p.Key)).ToArray());
        return candidate;
    }
    private static AutomaticBackupMode ReadAutomaticMode(JsonObject record)
    {
        if (record.ContainsKey("automaticBackupMode"))
        {
            return record["automaticBackupMode"]?.GetValue<string>() switch
            {
                "upload" => AutomaticBackupMode.UploadOnly,
                "bidirectional" => AutomaticBackupMode.Bidirectional,
                "download" => AutomaticBackupMode.DownloadOnly,
                _ => throw new FormatException("automaticBackupMode must be upload, bidirectional or download.")
            };
        }
        return record["automaticBidirectional"]?.GetValue<bool>() == true ? AutomaticBackupMode.Bidirectional : AutomaticBackupMode.UploadOnly;
    }
    private void StopPreview(bool close)
    {
        if (previewCancellation is null) return;
        closeAfterCancel |= close;
        previewCancellation.Cancel();
        cancel.Enabled = false;
        UiText.Set(feedback, "正在停止预览，请稍候…");
    }
    private void UpdatePreviewFeedback()
    {
        if (IsDisposed || !busy || previewCancellation is null) return;
        if (previewCancellation.IsCancellationRequested) { UiText.Set(feedback, "正在停止预览，请稍候…"); return; }
        var current = progress.Latest;
        var stage = current?.Phase == BackupPhase.Scanning ? "正在扫描本地文件" :
            current is null ? "正在准备预览" : "正在读取云端目录与元信息";
        UiText.Set(feedback, "{0} · 已处理 {1}/{2} · 哈希 {3:F1} MiB · 已用 {4:F0} 秒\n{5}",
            UiText.Text(stage), current?.Processed ?? 0, current?.Total?.ToString() ?? "—",
            (current?.HashedBytes ?? 0) / 1048576d, elapsed.Elapsed.TotalSeconds,
            current?.Phase == BackupPhase.Scanning ? current.Path ?? "" : current?.Project ?? "");
    }
    private async Task PerformAsync(bool preview, bool save)
    {
        if (busy) return;
        busy = true; closeAfterCancel = false;
        var onboarding = save && !global && index is null;
        var cancellable = preview || onboarding;
        foreach (Control action in actions.Controls) action.Enabled = action == cancel && cancellable;
        if (editorFields is not null) editorFields.Enabled = false;
        feedback.ForeColor = AppStyle.Ink;
        UiText.Set(cancel, cancellable ? "停止预览" : "取消");
        if (cancellable)
        {
            previewCancellation = new(); progress.Reset(); elapsed.Restart();
            activity.Visible = true; progressTimer.Start();
        }
        UiText.Set(feedback, "正在校验…");
        try
        {
            var candidate = Collect();
            var snapshot = await presenter.ValidateAsync(candidate);
            UiText.Set(feedback, "配置有效；尚未保存。");
            if (onboarding)
            {
                var project = snapshot.Projects.Last();
                UiText.Set(feedback, "正在检查云端目录是否已有项目或文件…");
                var inspection = await presenter.InspectNewProjectAsync(project, previewCancellation!.Token);
                previewCancellation.Token.ThrowIfCancellationRequested();
                var entry = candidate.Projects["folders"]!.AsArray().Last()!.AsObject();
                if (inspection.ExistingProject is { } existing)
                {
                    if (candidate.Projects["folders"]!.AsArray().Take(candidate.Projects["folders"]!.AsArray().Count - 1)
                        .Any(p => p?["id"]?.GetValue<string>() == existing.ProjectId))
                        throw new InvalidOperationException("此云端项目已加入本机列表，请编辑原项目。");
                    using var join = new Form { Name = "ConfirmExistingProject" };
                    AppStyle.Configure(join, "接续已有云端项目", new Size(680, 340));
                    join.Padding = new Padding(20);
                    var message = AppStyle.Label("", 180); message.Dock = DockStyle.Fill;
                    UiText.Set(message, "该云端目录已登记为备份项目。接续后沿用原项目，保留所选本机目录和排除规则。\n本机目录：{0}\n云端目录：{1}", project.Source, CloudAccountPresentation.Path(existing.Destination));
                    var continueButton = AppStyle.Button("接续原项目", (_, _) => { join.DialogResult = DialogResult.OK; join.Close(); }, true);
                    continueButton.Name = "ContinueCloudProject";
                    var footer = AppStyle.DialogActions(continueButton, AppStyle.Button("取消", (_, _) => join.Close())); footer.Dock = DockStyle.Bottom;
                    join.Controls.Add(message); join.Controls.Add(footer);
                    if (join.ShowDialog(this) != DialogResult.OK) return;
                    entry["id"] = existing.ProjectId; entry["destination"] = existing.Destination;
                }
                if (inspection.FolderId is { } folderId) entry["cloudFolderId"] = folderId;
                snapshot = await presenter.ValidateAsync(candidate);
                if (inspection.ExistingProject is null && inspection.HasContents)
                {
                    var report = await presenter.PreviewAsync(candidate, snapshot.Projects.Last().ProjectId, previewCancellation.Token, progress);
                    previewCancellation.Token.ThrowIfCancellationRequested();
                    using var review = new PreviewDialog(report, source: project.Source,
                        target: CloudAccountPresentation.Path(project.Destination), reviewBeforeSave: true);
                    if (review.ShowDialog(this) != DialogResult.OK || report.ExitCode == 1) return;
                }
                previewCancellation.Token.ThrowIfCancellationRequested();
            }
            if (preview)
            {
                var projectId = global ? null : snapshot.Projects[index ?? (snapshot.Projects.Count - 1)].ProjectId;
                previewCancellation!.Token.ThrowIfCancellationRequested();
                UpdatePreviewFeedback();
                var report = await presenter.PreviewAsync(candidate, projectId, previewCancellation.Token, progress);
                previewCancellation.Token.ThrowIfCancellationRequested();
                progressTimer.Stop(); elapsed.Stop(); activity.Visible = false;
                UiText.Set(feedback, "预览已完成，草稿尚未保存。");
                using var dialog = new PreviewDialog(report);
                dialog.ShowDialog(this);
            }
            if (save)
            {
                await presenter.SaveAsync(candidate, ConfigurationPart.Projects);
                busy = false;
                DialogResult = DialogResult.OK;
                Close();
            }
        }
        catch (OperationCanceledException) { if (!IsDisposed) UiText.Set(feedback, "预览已停止，草稿已保留。"); }
        catch (Exception ex) { if (!IsDisposed) { UiText.Set(feedback, CloudAccountPresentation.Error(ex)); feedback.ForeColor = SystemInformation.HighContrast ? AppStyle.Ink : Color.Firebrick; } }
        finally
        {
            progressTimer.Stop(); elapsed.Stop();
            previewCancellation?.Dispose(); previewCancellation = null; busy = false;
            if (!IsDisposed)
            {
                activity.Visible = false;
                foreach (Control action in actions.Controls) action.Enabled = true;
                if (editorFields is not null) editorFields.Enabled = true;
                UiText.Set(cancel, "取消");
                if (closeAfterCancel) Close();
            }
        }
    }
}
