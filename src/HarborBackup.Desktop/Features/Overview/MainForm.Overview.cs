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
    private void BuildOverviewPage()
    {
        var overviewPage = Page("概览");
        overviewPage.Controls.Add(dashboard);
        overviewPage.Controls.Add(welcome);
        var overviewList = GridSurface(overviewGrid, "\uE8B7", "还没有备份项目", "在备份项目页添加需要保护的文件夹。");
        overviewGrid.Name = "OverviewGrid";
        overviewGrid.DefaultCellStyle.WrapMode = DataGridViewTriState.True;
        overviewGrid.AutoSizeRowsMode = DataGridViewAutoSizeRowsMode.DisplayedCells;
        dashboard.Controls.Add(overviewList);
        notice.ForeColor = AppStyle.Muted;
        dashboard.Controls.Add(notice);
        var cards = new TableLayoutPanel { Name = "OverviewCards", Dock = DockStyle.Top, Height = 92, ColumnCount = 3, RowCount = 1, Padding = new Padding(0, 0, 0, 8) };
        cards.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        var normal = new Font(Font.FontFamily, 12.5f);
        var normalState = new Font(Font.FontFamily, 12.5f, FontStyle.Bold);
        var compact = new Font(Font.FontFamily, 12f);
        var compactState = new Font(Font.FontFamily, 12f, FontStyle.Bold);
        var captionFont = new Font(Font.FontFamily, 9f);
        Disposed += (_, _) => { normal.Dispose(); normalState.Dispose(); compact.Dispose(); compactState.Dispose(); captionFont.Dispose(); };
        foreach (var (caption, value) in new[] { ("当前状态", currentStatus), ("上次运行结束", lastRun), ("下次自动备份", nextRun) })
        {
            cards.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100f / 3));
            var card = new SurfacePanel { Dock = DockStyle.Fill, Padding = new Padding(14, 8, 14, 8), Margin = new Padding(0, 0, caption == "下次自动备份" ? 0 : 10, 0) };
            value.Dock = DockStyle.Fill;
            value.Font = value == currentStatus ? normalState : normal;
            card.Controls.Add(value);
            var captionLabel = AppStyle.Label(caption, 22); captionLabel.ForeColor = AppStyle.Muted; captionLabel.Font = captionFont;
            card.Controls.Add(captionLabel);
            cards.Controls.Add(card);
        }
        dashboard.Controls.Add(cards);
        void FitCards()
        {
            var dpi = DeviceDpi / 96f;
            var dense = dashboard.ClientSize.Width / dpi < 840 || dashboard.ClientSize.Height / dpi < 500;
            cards.Height = (int)Math.Round((dense ? 76 : 92) * dpi);
            currentStatus.Font = dense ? compactState : normalState;
            lastRun.Font = nextRun.Font = dense ? compact : normal;
        }
        dashboard.SizeChanged += (_, _) => FitCards();
        DpiChanged += (_, _) => BeginInvoke((Action)(() => { if (!IsDisposed) FitCards(); }));
        FitCards();
        BuildWelcome();
    }
}
