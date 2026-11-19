using HarborBackup.Core.Domain;
using HarborBackup.Desktop.Controllers;
using HarborBackup.Desktop.Localization;
using HarborBackup.Desktop.Presentation;
using HarborBackup.Desktop.Styling;

namespace HarborBackup.Desktop.Views.Dialogs;

internal sealed class RestoreDialog : Form
{
    private sealed record ProjectChoice(string Id, string Name) { public override string ToString() => Name; }
    private sealed record CandidateRow(string Project, string Source, string Path, long Bytes, string Evidence, RestoreCandidate Candidate);
    private readonly DesktopController controller;
    private readonly ComboBox project = new() { Name = "RestoreProject", AccessibleName = UiText.Text("项目"), DropDownStyle = ComboBoxStyle.DropDownList, Width = 230 };
    private readonly ComboBox scope = new() { Name = "RestoreScope", AccessibleName = UiText.Text("来源"), DropDownStyle = ComboBoxStyle.DropDownList, Width = 170 };
    private readonly TextBox filter = new() { Name = "RestorePathFilter", AccessibleName = UiText.Text("筛选相对路径（含子目录）"), Width = 280, PlaceholderText = UiText.Text("筛选相对路径（含子目录）") };
    private readonly TextBox output = new() { Name = "RestoreOutput", Width = 430, AccessibleName = UiText.Text("独立输出目录") };
    private readonly NumericUpDown budget = new() { Name = "RestoreMaxMiB", AccessibleName = UiText.Text("恢复演练预算（MiB，0 不限）"), Minimum = 0, Maximum = 1048576, Value = 100, Width = 90 };
    private readonly NumericUpDown groups = new() { Name = "RestoreGroups", AccessibleName = UiText.Text("轮换组数"), Minimum = 1, Maximum = 256, Value = 1, Width = 60 };
    private readonly DataGridView grid = AppStyle.Grid(("Project", "项目", 15), ("Source", "来源", 12),
        ("Path", "相对路径", 43), ("Bytes", "字节", 12), ("Evidence", "校验依据", 18));
    private readonly TextBox details = new() { Multiline = true, ReadOnly = true, ScrollBars = ScrollBars.Vertical, Dock = DockStyle.Bottom, Height = 110 };
    private readonly Label feedback = new() { AutoSize = true, Dock = DockStyle.Bottom, Padding = new Padding(0, 8, 0, 8) };
    private readonly Button refresh;
    private readonly Button restore;
    private readonly Button test;
    private readonly Button stop;
    private CancellationTokenSource? operation;
    private bool closeWhenIdle;

    internal RestoreDialog(DesktopController controller)
    {
        this.controller = controller;
        AppStyle.Configure(this, "独立目录恢复", new Size(1120, 730)); MinimumSize = new Size(940, 620); Padding = new Padding(18);
        grid.MultiSelect = true;
        grid.Name = "RestoreCandidates";
        foreach (var p in controller.State.Configuration?.Projects ?? []) project.Items.Add(new ProjectChoice(p.ProjectId, p.Destination));
        if (project.Items.Count > 0) project.SelectedIndex = 0;
        scope.Items.AddRange([UiText.Text("云端当前文件"), UiText.Text("上一份归档")]); scope.SelectedIndex = 0;
        refresh = AppStyle.Button("列出可恢复文件", async (_, _) => await LoadRowsAsync());
        restore = AppStyle.Button("恢复选中文件", async (_, _) => await RestoreAsync(false));
        test = AppStyle.Button("演练选中文件", async (_, _) => await RestoreAsync(true));
        stop = AppStyle.Button("停止恢复", (_, _) => operation?.Cancel()); stop.Enabled = false;
        restore.Enabled = test.Enabled = false;
        var browse = AppStyle.Button("浏览输出目录…", (_, _) =>
        {
            using var picker = new FolderBrowserDialog { Description = UiText.Text("独立输出目录"), UseDescriptionForTitle = true };
            if (picker.ShowDialog(this) == DialogResult.OK) output.Text = picker.SelectedPath;
        });
        var selectors = AppStyle.Toolbar(project, scope, AppStyle.Input(filter), refresh);
        var destination = AppStyle.Toolbar(new Label { Text = UiText.Text("独立输出目录"), AutoSize = true }, AppStyle.Input(output), browse);
        var limits = AppStyle.Toolbar(new Label { Text = UiText.Text("恢复演练预算（MiB，0 不限）"), AutoSize = true }, budget,
            new Label { Text = UiText.Text("轮换组数"), AutoSize = true }, groups);
        var actions = AppStyle.Toolbar(restore, test, stop);
        feedback.Text = UiText.Text("选择文件后恢复到独立目录；现有文件不会被覆盖。");
        Controls.Add(grid); Controls.Add(actions); Controls.Add(limits); Controls.Add(destination); Controls.Add(selectors); Controls.Add(details); Controls.Add(feedback);
        project.SelectedIndexChanged += (_, _) => InvalidateCandidates();
        scope.SelectedIndexChanged += (_, _) => InvalidateCandidates();
        filter.TextChanged += (_, _) => InvalidateCandidates();
        FormClosing += (_, e) =>
        {
            if (operation is null) return;
            closeWhenIdle = true; operation.Cancel(); e.Cancel = true; feedback.Text = UiText.Text("关闭前正在停止恢复，请稍候。");
        };
        ResumeLayout(true);
    }
    private void Busy(bool busy)
    {
        project.Enabled = scope.Enabled = filter.Enabled = output.Enabled = refresh.Enabled = restore.Enabled = test.Enabled = budget.Enabled = groups.Enabled = !busy;
        restore.Enabled = test.Enabled = !busy && grid.RowCount > 0;
        stop.Enabled = busy;
    }
    private void InvalidateCandidates()
    {
        if (operation is not null) return;
        grid.DataSource = Array.Empty<CandidateRow>(); restore.Enabled = test.Enabled = false;
        feedback.Text = UiText.Text("范围已改变，请重新列出可恢复文件。");
    }
    private void EndOperation()
    {
        operation?.Dispose(); operation = null;
        if (IsDisposed) return;
        Busy(false); if (closeWhenIdle) Close();
    }
    internal async Task LoadRowsAsync()
    {
        if (operation is not null || project.SelectedItem is not ProjectChoice choice) return;
        operation = new(); Busy(true); feedback.Text = UiText.Text("恢复列表读取中…");
        try
        {
            var result = await controller.ListRestoreAsync(choice.Id, scope.SelectedIndex == 0 ? RestoreScope.Current : RestoreScope.Archive,
                string.IsNullOrWhiteSpace(filter.Text) ? null : filter.Text.Trim(), operation.Token);
            if (IsDisposed) return;
            grid.DataSource = result.Candidates.Select(c => new CandidateRow(c.Destination,
                UiText.Text(c.Scope == RestoreScope.Current ? "云端当前文件" : "上一份归档"), c.Path, c.Length,
                c.Sha256 is null ? UiText.Text("SHA-256 证据不足") : "SHA-256", c)).ToArray();
            grid.SelectAll();
            details.Text = string.Join(Environment.NewLine, result.Diagnostics);
            feedback.Text = UiText.Text("选择文件后恢复到独立目录；现有文件不会被覆盖。");
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { if (!IsDisposed) feedback.Text = CloudAccountPresentation.Error(ex); }
        finally { EndOperation(); }
    }
    private async Task RestoreAsync(bool testOnly)
    {
        if (operation is not null) return;
        var selected = grid.SelectedRows.Cast<DataGridViewRow>().Select(r => r.DataBoundItem).OfType<CandidateRow>().Select(r => r.Candidate).ToArray();
        if (selected.Length == 0) { feedback.Text = UiText.Text("请先选择至少一个文件。"); return; }
        operation = new(); Busy(true); feedback.Text = UiText.Text("正文下载与校验中…");
        try
        {
            var report = await controller.RestoreAsync(selected, output.Text, testOnly, testOnly ? (long)budget.Value * 1024 * 1024 : 0,
                testOnly ? (int)groups.Value : 1, operation.Token);
            if (IsDisposed) return;
            details.Text = RestoreReportPresentation.Details(report);
            feedback.Text = UiText.Text("已完成 {0} 个文件正文校验，共 {1} 字节。报告目录：{2}", report.VerifiedFiles, report.VerifiedBytes, report.OutputRoot);
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { if (!IsDisposed) feedback.Text = CloudAccountPresentation.Error(ex); }
        finally { EndOperation(); }
    }
}
