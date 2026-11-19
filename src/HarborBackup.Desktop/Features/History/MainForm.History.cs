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

internal sealed partial class MainForm
{
    private void BuildHistoryPage()
    {
        var historyPage = Page("运行记录");
        historyGrid.Name = "HistoryGrid";
        historyGrid.Columns[3].DefaultCellStyle.WrapMode = DataGridViewTriState.True;
        historyGrid.AutoSizeRowsMode = DataGridViewAutoSizeRowsMode.DisplayedCells;
        historyGrid.CellToolTipTextNeeded += (_, e) =>
        {
            if (e.RowIndex >= 0 && e.ColumnIndex == 3 && historyGrid.Rows[e.RowIndex].DataBoundItem is HistoryRecord record)
                e.ToolTipText = string.Join(Environment.NewLine, record.Projects);
        };
        historyFilter.Name = "HistoryFilter";
        historyPage.Controls.Add(GridSurface(historyGrid, "\uE81C", "没有匹配的运行记录", "运行完成后会在这里记录结果。也可以清除筛选条件，查看已有记录。"));
        historyResult.Items.AddRange(["全部结果", "完成", "有待处理", "失败", "取消"]);
        historyResult.SelectedIndex = 0;
        historyFilter.AccessibleName = "按项目筛选记录";
        historyResult.AccessibleName = "运行结果筛选";
        historySince.AccessibleName = "起始日期筛选";
        historyPage.Controls.Add(AppStyle.Toolbar(AppStyle.Input(historyFilter), AppStyle.Input(historyResult), UiText.Bind(new Label { AutoSize = true, Padding = new Padding(0, 7, 0, 0) }, "起始日期"), AppStyle.Input(historySince)));
        historyFilter.TextChanged += (_, _) => FilterHistory();
        historyResult.SelectedIndexChanged += (_, _) => FilterHistory();
        historySince.ValueChanged += (_, _) => FilterHistory();
        historyPage.Controls.Add(AppStyle.Toolbar(AppStyle.Button("刷新记录", async (_, _) => await controller.RefreshAsync(true)),
            AppStyle.Button("查看选中详情", (_, _) => ShowSelectedRun()), AppStyle.Button("打开日志目录", (_, _) => Open(controller.State.Configuration?.LogDirectory))));
        historyGrid.CellDoubleClick += (_, e) => { if (e.RowIndex >= 0) ShowSelectedRun(); };
        void ShowSelectedRun()
        {
            if (historyGrid.CurrentRow?.DataBoundItem is HistoryRecord item)
            {
                using var dialog = new RunDetailsDialog(item, historyFilter.Text.Trim());
                dialog.ShowDialog(this);
            }
        }
        historyGrid.CellFormatting += (_, e) =>
        {
            if (e.Value is DateTimeOffset stamp) { e.Value = stamp.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss"); e.FormattingApplied = true; }
            else if (e.ColumnIndex == 1 && e.Value is string mode) { e.Value = UiText.Text(ReportPresentation.Mode(mode)); e.FormattingApplied = true; }
            else if (e.ColumnIndex == 2 && e.Value is int code) { e.Value = UiText.Text(ReportPresentation.Result(code)); e.FormattingApplied = true; }
            else if (e.ColumnIndex == 3 && e.RowIndex >= 0 && historyGrid.Rows[e.RowIndex].DataBoundItem is HistoryRecord record)
            {
                var projects = record.Projects;
                var labels = projects.Select(p => p.Split('/').Last()).ToArray();
                var names = labels.Distinct(StringComparer.OrdinalIgnoreCase).Count() == labels.Length ? string.Join("、", labels) : record.ProjectNames;
                e.Value = projects.Count == 0 ? "—" : projects.Count == 1 ? names : UiText.Text("{0} 个项目：{1}", projects.Count, names);
                e.FormattingApplied = true;
            }
        };
    }
}
