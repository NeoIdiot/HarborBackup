using HarborBackup.Core.Domain;
using HarborBackup.Desktop.Localization;
using HarborBackup.Desktop.Presentation;
using HarborBackup.Desktop.Styling;

namespace HarborBackup.Desktop.Views.Dialogs;

internal sealed class PreviewDialog : Form
{
    internal PullSelection? SelectedPull { get; private set; }
    internal PullSelection? SelectedUpload { get; private set; }
    public PreviewDialog(PreviewReport report, bool allowPull = false, string? source = null, string? target = null, bool allowUpload = false, bool reviewBeforeSave = false)
    {
        var selection = allowUpload ? report.UploadSelection : allowPull ? report.PullSelection : null;
        var actionable = allowPull || allowUpload;
        AppStyle.Configure(this, allowUpload ? "上传预览 · 请核对写入方向" : allowPull ? "取回预览 · 请核对写入方向" : "只读预览", new Size(960, 620));
        MinimumSize = new Size(760, 450);
        if (reviewBeforeSave) { Name = "InitialProjectReview"; UiText.Set(this, "首次内容核对"); }
        Padding = new Padding(20);
        if (report.Mode == "Verify") UiText.Set(this, "核验报告");
        var grid = AppStyle.Grid(("DisplayProject", "项目", 18), ("Path", "相对路径", 24), ("DisplayAction", "计划动作", 14),
            ("DisplayVerification", "核验分类", 18), ("DisplayDetails", "说明", 26));
        grid.Name = "PullPreviewGrid";
        var rows = ReportPresentation.Details(report);
        var candidates = selection?.Files.ToDictionary(f => f.Path, StringComparer.OrdinalIgnoreCase) ?? [];
        var summary = new Label { AutoSize = true, Dock = DockStyle.Top, Padding = new Padding(0, 0, 0, 12) };
        if (allowUpload) UiText.Set(summary, "读取本地：{0}\n写入云端：{1}\n勾选差异项表示使用本地版本；覆盖前保护云端旧内容，文件变化时跳过。", source, target);
        else if (allowPull) UiText.Set(summary, "读取目标：{0}\n写入本地源：{1}\n仅执行勾选项；文件变化时跳过，覆盖前保护旧内容。", target, source);
        else if (reviewBeforeSave) UiText.Set(summary, "本机目录：{0}\n云端目录：{1}\n核对后仅保存配置，双方文件均保留。差异请在保存后明确选择上传或取回。", source, target);
        else UiText.Set(summary, "只读预览，不修改文件或状态。");
        var plan = new Label { Name = "RunPlanSummary", AutoSize = true, Dock = DockStyle.Top, Padding = new Padding(0, 0, 0, 8) };
        var totals = report.Summary; var verification = report.Verification;
        UiText.Set(plan, "计划：传输 {0} 项 / {1:F2} MiB，创建目录 {2} 项，保护旧内容 {3} 项，待处理 {4} 项。\n核验：一致 {5}，不同 {6}，本地缺失 {7}，云端缺失 {8}，无法确认 {9}，类型冲突 {10}。\n一致性依据为本地 SHA-256 与云端元信息；云端正文未下载。",
            totals.Transfers, totals.TransferBytes / 1048576d, totals.CreatesDirectories, totals.Protected, totals.NeedsAttention,
            verification.Match, verification.Different, verification.MissingLocal, verification.MissingCloud, verification.Unverifiable, verification.TypeConflicts);
        var counts = new Label { Name = "PreviewCounts", AutoSize = true, Dock = DockStyle.Bottom, Padding = new Padding(0, 8, 0, 8) };
        var close = AppStyle.Button("关闭", (_, _) => Close());
        var pull = AppStyle.Button(allowUpload ? "上传所选文件" : "取回所选文件", (_, _) =>
        {
            grid.EndEdit();
            var files = Chosen();
            if (selection is not null && (files.Length > 0 || report.Projects.All(p => p.Plans.Count == 0) && report.ExitCode == 0))
            {
                if (allowUpload) SelectedUpload = selection with { Files = files };
                else SelectedPull = selection with { Files = files };
                DialogResult = DialogResult.OK; Close();
            }
        }, true);
        pull.Name = "ExecuteSelectedPull";
        PullFileSelection[] Chosen() => grid.Rows.Cast<DataGridViewRow>()
            .Where(r => r.DataBoundItem is DetailRow d && candidates.ContainsKey(d.Path) && r.Cells["Selected"].Value is true)
            .Select(r => candidates[((DetailRow)r.DataBoundItem!).Path]).ToArray();
        void UpdateCounts()
        {
            var chosen = selection is null ? [] : Chosen();
            pull.Enabled = selection is not null && report.ExitCode != 1 && (chosen.Length > 0 || report.Projects.All(p => p.Plans.Count == 0) && report.ExitCode == 0);
            UiText.Set(counts, "已选 {0} 项 · 新增 {1} · 更新 {2} · 传输约 {3:F2} MiB · 待处理 {4}",
                chosen.Length, chosen.Count(f => f.IsNew), chosen.Count(f => !f.IsNew),
                chosen.Sum(f => (double)f.Bytes) / 1048576d, report.Projects.Sum(p =>
                    p.Issues.Count(i => i.Code is not ("PendingOperationsRetained" or "PendingOperationsUnverified") ||
                        !p.Plans.Any(f => f.Decision.Reason == "PendingFileOperation")) +
                    p.Plans.Count(f => f.Decision.NeedsAttention && !candidates.ContainsKey(f.Path))));
        }
        if (selection is not null)
        {
            grid.ReadOnly = false;
            foreach (DataGridViewColumn column in grid.Columns) column.ReadOnly = true;
            grid.Columns.Add(new DataGridViewCheckBoxColumn { Name = "Selected", HeaderText = UiText.Text("选择"),
                AutoSizeMode = DataGridViewAutoSizeColumnMode.None, Width = 80, DisplayIndex = 0, ReadOnly = false,
                HeaderCell = { Style = { Padding = new Padding(4, 0, 4, 0), WrapMode = DataGridViewTriState.False } } });
            UiText.Watch(grid, () => grid.Columns["Selected"]!.HeaderText = UiText.Text("选择"));
            grid.CurrentCellDirtyStateChanged += (_, _) => { if (grid.IsCurrentCellDirty) grid.CommitEdit(DataGridViewDataErrorContexts.Commit); };
            var initializingSelection = false;
            grid.CellValueChanged += (_, _) => { if (!initializingSelection) UpdateCounts(); };
            grid.DataBindingComplete += (_, _) =>
            {
                initializingSelection = true;
                try
                {
                    foreach (DataGridViewRow row in grid.Rows)
                    {
                        var eligible = row.DataBoundItem is DetailRow d && candidates.ContainsKey(d.Path);
                        row.Cells["Selected"].ReadOnly = !eligible;
                        row.Cells["Selected"].Value = eligible;
                    }
                }
                finally { initializingSelection = false; }
                UpdateCounts();
            };
        }
        grid.DataSource = rows;
        var exclusions = AppStyle.Button("排除规则…", (_, _) =>
        {
            using var detail = new Form();
            AppStyle.Configure(detail, "本次预览的排除规则", new Size(720, 520));
            detail.MinimumSize = new Size(520, 360); detail.Padding = new Padding(20);
            var body = new RichTextBox { Dock = DockStyle.Fill, ReadOnly = true, Font = detail.Font,
                DetectUrls = false, WordWrap = true, ScrollBars = RichTextBoxScrollBars.Vertical };
            body.Text = string.Join(Environment.NewLine + Environment.NewLine, report.Projects.Select(p =>
                p.Destination + Environment.NewLine +
                ExclusionPresentation.Sources(p.ExclusionSources) +
                UiText.Text("最终有效规则") + Environment.NewLine +
                UiText.Text("排除目录：{0}", p.ExcludedDirectories.Count == 0 ? UiText.Text("无") : string.Join("; ", p.ExcludedDirectories)) + Environment.NewLine +
                UiText.Text("排除文件：{0}", p.ExcludedFiles.Count == 0 ? UiText.Text("无") : string.Join("; ", p.ExcludedFiles)) + Environment.NewLine +
                UiText.Text("本地跳过 {0} 项，云端跳过 {1} 项。目录按一项统计，包含整个子树。", p.Metrics.ExcludedLocalEntries, p.Metrics.ExcludedCloudEntries)));
            var detailActions = AppStyle.DialogActions(AppStyle.Button("关闭", (_, _) => detail.Close()));
            detailActions.Dock = DockStyle.Bottom;
            detail.Controls.Add(AppStyle.Input(body)); detail.Controls.Add(detailActions);
            detail.ResumeLayout(true); detail.ShowDialog(this);
        });
        var details = AppStyle.Button("查看选中详情", (_, _) =>
        {
            if (grid.CurrentRow?.DataBoundItem is not DetailRow row) return;
            using var dialog = new RunDetailsDialog([row]);
            dialog.ShowDialog(this);
        });
        FlowLayoutPanel toolbar;
        if (reviewBeforeSave)
        {
            var accept = AppStyle.Button("确认核对并保存配置", (_, _) => { DialogResult = DialogResult.OK; Close(); }, true);
            accept.Name = "ConfirmInitialReview"; accept.Enabled = report.ExitCode != 1;
            toolbar = AppStyle.DialogActions(details, exclusions, accept, close);
        }
        else toolbar = actionable ? AppStyle.DialogActions(details, exclusions, pull, close) : AppStyle.DialogActions(details, exclusions, close);
        if (report.ConfigurationImpact.Count > 0)
        {
            var impact = AppStyle.Button("配置变更影响", (_, _) =>
            {
                using var dialog = new Form(); AppStyle.Configure(dialog, "配置变更影响", new Size(740, 480)); dialog.Padding = new Padding(20);
                var body = new RichTextBox { Name = "ConfigurationImpactDetails", ReadOnly = true, Dock = DockStyle.Fill, Font = dialog.Font };
                body.Text = UiText.Text("范围数量为两次只读观察的估算，目录按观察条目统计；执行前仍会重新核验。") + Environment.NewLine + Environment.NewLine +
                    string.Join(Environment.NewLine + Environment.NewLine, report.ConfigurationImpact.Select(p => p.Destination + Environment.NewLine +
                        UiText.Text("目录变更：{0}；排除规则变更：{1}；新增排除：{2}；重新纳入：{3}。",
                            UiText.Text(p.MappingChanged ? "是" : "否"), UiText.Text(p.RulesChanged ? "是" : "否"),
                            p.NewlyExcluded?.ToString() ?? UiText.Text("无法确认"), p.NewlyIncluded?.ToString() ?? UiText.Text("无法确认"))));
                dialog.Controls.Add(body); dialog.ShowDialog(this);
            });
            impact.Name = "ShowConfigurationImpact"; toolbar.Controls.Add(impact);
        }
        toolbar.Dock = DockStyle.Bottom;
        Controls.Add(grid);
        Controls.Add(plan);
        Controls.Add(summary);
        if (actionable) Controls.Add(counts);
        Controls.Add(toolbar);
        void FitSummary()
        {
            summary.MaximumSize = new Size(Math.Max(100, ClientSize.Width - Padding.Horizontal), 0);
            counts.MaximumSize = summary.MaximumSize;
            plan.MaximumSize = summary.MaximumSize;
        }
        SizeChanged += (_, _) => FitSummary();
        FitSummary();
        UpdateCounts();
        if (!actionable) pull.Dispose();
        ResumeLayout(true);
    }
}
