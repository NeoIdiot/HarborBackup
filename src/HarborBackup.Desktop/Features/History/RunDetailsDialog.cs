using HarborBackup.Desktop.Localization;
using HarborBackup.Desktop.Presentation;
using HarborBackup.Desktop.Styling;
using HarborBackup.Windows.Persistence;

namespace HarborBackup.Desktop.Views.Dialogs;

internal sealed class RunDetailsDialog : Form
{
    public RunDetailsDialog(HistoryRecord record, string? projectFilter = null) : this(record.Report is null
        ? [new("日志", "", "读取错误", record.Error ?? "", true, SystemRow: true)]
        : ReportPresentation.Details(record.Report), "运行详情",
        $"{record.Time.ToLocalTime():yyyy-MM-dd HH:mm:ss} · {UiText.Text(ReportPresentation.Mode(record.Mode))} · {UiText.Text(ReportPresentation.Result(record.ExitCode))}" +
        (record.Report is null ? "" : Environment.NewLine + UiText.Text("共 {0} 个项目，完成写入 {1} 项，待处理 {2} 项", record.Projects.Count,
            record.Report.Projects.Sum(p => p.Files.Count(f => f.Changed)), ReportPresentation.Details(record.Report).Count(r => r.Attention))),
        record.Path, projectFilter, record.Projects) { }

    public RunDetailsDialog(IReadOnlyList<DetailRow> rows, string title = "条目详情", string? summary = null,
        string? logPath = null, string? projectFilter = null, IReadOnlyList<string>? projectNames = null)
    {
        Name = "RunDetailsDialog";
        AppStyle.Configure(this, title, new Size(960, 640));
        MinimumSize = new Size(760, 500);
        Padding = new Padding(16);
        var grid = AppStyle.Grid(("DisplayProject", "项目", 22), ("Path", "相对路径", 28),
            ("DisplayAction", "动作", 15), ("DisplayDetails", "详情", 35));
        grid.Name = "RunDetailsGrid";
        var search = new TextBox { Name = "RunDetailsFilter", Width = 320, PlaceholderText = UiText.Text("筛选项目、路径、动作或错误…") };
        var projects = (projectNames ?? rows.Where(r => !r.SystemRow).Select(r => r.Project).ToArray()).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        var project = new ComboBox { Name = "RunDetailsProject", Width = 240, DropDownStyle = ComboBoxStyle.DropDownList, Visible = projects.Length > 1 };
        project.Items.Add(UiText.Text("全部项目（{0}）", projects.Length));
        project.Items.AddRange(projects);
        var matches = projects.Select((name, index) => (name, index)).Where(p => !string.IsNullOrEmpty(projectFilter) &&
            p.name.Contains(projectFilter, StringComparison.OrdinalIgnoreCase)).ToArray();
        project.SelectedIndex = matches.Length == 1 ? matches[0].index + 1 : 0;
        var attention = UiText.Bind(new CheckBox { Name = "RunDetailsAttention", AutoSize = true, Padding = new Padding(0, 7, 0, 0) }, "仅看待处理");
        var count = new Label { AutoSize = true, Dock = DockStyle.Top, Padding = new Padding(0, 6, 0, 6) };
        var full = new TextBox { Name = "RunDetailsText", ReadOnly = true, Multiline = true,
            WordWrap = true, ScrollBars = ScrollBars.Vertical, Dock = DockStyle.Fill, BorderStyle = BorderStyle.None };
        var detail = AppStyle.Input(full);
        detail.Dock = DockStyle.Fill;
        // Let layout divide the available space after DPI scaling. Recalculating a pixel
        // height in SizeChanged can scale the detail pane twice during initial autoscaling.
        var content = new TableLayoutPanel
        {
            Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 2, Margin = Padding.Empty,
            Padding = Padding.Empty
        };
        content.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        content.RowStyles.Add(new RowStyle(SizeType.Percent, 70));
        content.RowStyles.Add(new RowStyle(SizeType.Percent, 30));
        grid.Margin = detail.Margin = Padding.Empty;
        content.Controls.Add(grid, 0, 0);
        content.Controls.Add(detail, 0, 1);
        void SelectRow()
        {
            full.Text = grid.CurrentRow?.DataBoundItem is DetailRow row
                ? $"{row.DisplayProject}\r\n{row.Path}\r\n{row.DisplayAction}\r\n{row.DisplayDetails}" : "";
        }
        void Filter()
        {
            var query = search.Text.Trim();
            var selectedProject = project.SelectedIndex > 0 ? projects[project.SelectedIndex - 1] : null;
            var filtered = rows.Where(r => (selectedProject is null || r.Project.Equals(selectedProject, StringComparison.OrdinalIgnoreCase)) &&
                (!attention.Checked || r.Attention) &&
                (r.DisplayProject + r.Path + r.DisplayAction + r.DisplayDetails).Contains(query, StringComparison.OrdinalIgnoreCase)).ToArray();
            grid.DataSource = filtered;
            UiText.Set(count, "显示 {0} / {1} 项", filtered.Length, rows.Count);
            SelectRow();
        }
        grid.SelectionChanged += (_, _) => SelectRow();
        search.TextChanged += (_, _) => Filter();
        attention.CheckedChanged += (_, _) => Filter();
        project.SelectedIndexChanged += (_, _) => Filter();
        var close = AppStyle.Button("关闭", (_, _) => Close());
        close.Name = "CloseRunDetails";
        CancelButton = close;
        Controls.Add(content);
        Controls.Add(count);
        Controls.Add(AppStyle.Toolbar(project, AppStyle.Input(search), attention));
        if (logPath is not null)
        {
            var path = new TextBox { Name = "RunLogPath", Text = logPath, ReadOnly = true, Dock = DockStyle.Fill, AccessibleName = UiText.Text("日志文件") };
            var frame = AppStyle.Input(path); frame.Dock = DockStyle.Top;
            Controls.Add(frame);
        }
        if (summary is not null) Controls.Add(new Label { Name = "RunDetailsSummary", Text = summary, AutoSize = true, Dock = DockStyle.Top, Padding = new Padding(0, 0, 0, 8) });
        Controls.Add(AppStyle.DialogActions(close));
        Filter();
        ResumeLayout(true);
    }
}
