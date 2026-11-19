using HarborBackup.Desktop.Localization;
using HarborBackup.Desktop.Presentation;
using HarborBackup.Desktop.Styling;
using HarborBackup.Desktop.Views.Dialogs;
using HarborBackup.OneDrive.Transport;
using HarborBackup.Windows.Integration;
using HarborBackup.Windows.Persistence.Configuration;

namespace HarborBackup.Desktop.Views;

internal sealed partial class MainForm
{
    private readonly List<Button> localSelectionButtons = [];
    private readonly Label catalogStatus = new()
    {
        Name = "CloudCatalogStatus", Dock = DockStyle.Fill, AutoEllipsis = true,
        TextAlign = ContentAlignment.MiddleLeft, ForeColor = AppStyle.Muted, Margin = Padding.Empty
    };
    private readonly Panel catalogStrip = new() { Name = "CloudCatalogStrip", Dock = DockStyle.Top, Height = 32 };
    private readonly Button retryCatalog = AppStyle.Button("重试");
    private readonly Button catalogDetails = AppStyle.Button("查看详情");
    private readonly ActivityIndicator catalogProgress = new() { Name = "CloudCatalogProgress",
        Width = 18, Height = 18, Visible = false };
    private readonly Label catalogWarning = new() { ForeColor = Color.FromArgb(164, 100, 0), AutoSize = false,
        Width = 18, Dock = DockStyle.Left, TextAlign = ContentAlignment.MiddleLeft, Visible = false };
    private string? catalogError;
    private CatalogDisplay catalogDisplay;
    private bool retryCatalogOnReconnect;
    private enum CatalogDisplay { Initial, Loading, Loaded, Failed, Offline, Canceled }
    private readonly Button refreshCatalog = AppStyle.Button("刷新云端项目");
    private readonly Button cancelCatalog = AppStyle.Button("停止加载");
    private IReadOnlyList<CloudProjectTarget> cloudProjects = [];
    private CloudAccountSettings? catalogAccount;
    private CancellationTokenSource? catalogCancellation;
    private bool catalogLoaded;
    private IReadOnlyList<CloudProjectTarget> AvailableCloudProjects =>
        ProjectListPresentation.VisibleCloudProjects(controller.State.Configuration, cloudProjects);

    private void CheckCatalogAccount()
    {
        if (catalogAccount == controller.CloudAccount) return;
        catalogCancellation?.Cancel();
        catalogAccount = controller.CloudAccount;
        cloudProjects = [];
        catalogLoaded = false;
        catalogError = null;
        retryCatalogOnReconnect = false;
        catalogDisplay = CatalogDisplay.Initial;
        UiText.Set(catalogStatus, controller.CloudAccount.Enabled && !controller.CloudAccount.RequiresAuthorization
            ? "刷新以查看云端项目" : "请先在设置中连接账户并选择管理目录。");
        BindProjectRows();
    }

    private void BindProjectRows()
    {
        var selected = (projectGrid.CurrentRow?.DataBoundItem as ProjectRow)?.ProjectId;
        projectGrid.DataSource = ProjectListPresentation.Merge(controller.State.Projects, AvailableCloudProjects);
        if (catalogDisplay == CatalogDisplay.Loaded) ShowCatalogCount();
        foreach (DataGridViewRow row in projectGrid.Rows)
            if ((row.DataBoundItem as ProjectRow)?.ProjectId == selected)
            { projectGrid.CurrentCell = row.Cells[0]; break; }
        UpdateProjectActions();
    }

    private void ShowCatalogCount() => UiText.Set(catalogStatus, "已刷新 · {0} 个可加入项目",
        ProjectListPresentation.Merge(controller.State.Projects, AvailableCloudProjects).Count(row => !row.IsLocal));

    private void CheckCatalogConnectivity(DesktopState state)
    {
        if (catalogDisplay != CatalogDisplay.Offline || state.NetworkOffline || catalogCancellation is not null) return;
        if (IsDisposed || Disposing) return;
        if (retryCatalogOnReconnect && Visible && currentPage == "备份项目")
        {
            if (!state.Busy) _ = LoadCloudProjectsAsync(retryAfterReconnect: true);
            return;
        }

        catalogDisplay = CatalogDisplay.Initial;
        UiText.Set(catalogStatus, catalogLoaded ? "显示上次结果，可刷新" : "刷新以查看云端项目");
    }

    private Control BuildCatalogStatus()
    {
        var layout = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 3, RowCount = 1, Margin = Padding.Empty };
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 26));
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        var indicator = new Panel { Width = 26, Dock = DockStyle.Fill, Margin = Padding.Empty };
        indicator.Controls.Add(catalogWarning); indicator.Controls.Add(catalogProgress);
        catalogWarning.Paint += (_, e) =>
        {
            var side = Math.Min(catalogWarning.Width, catalogWarning.Height) - 2;
            var bounds = new Rectangle(0, (catalogWarning.Height - side) / 2, side, side);
            e.Graphics.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
            using var fill = new SolidBrush(SystemInformation.HighContrast ? SystemColors.WindowText : Color.FromArgb(164, 100, 0));
            e.Graphics.FillEllipse(fill, bounds);
            TextRenderer.DrawText(e.Graphics, "!", catalogWarning.Font, bounds,
                SystemInformation.HighContrast ? SystemColors.Window : Color.White,
                TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding);
        };
        catalogProgress.Location = new Point(0, 7);
        var actions = new FlowLayoutPanel { AutoSize = true, WrapContents = false, Dock = DockStyle.Fill, Margin = Padding.Empty };
        foreach (var button in new[] { cancelCatalog, retryCatalog, catalogDetails })
        {
            button.MinimumSize = new Size(0, 28); button.Padding = new Padding(8, 0, 8, 0);
            button.Margin = new Padding(6, 0, 0, 0); button.Visible = false;
            actions.Controls.Add(button);
        }
        retryCatalog.Name = "RetryCloudCatalog"; catalogDetails.Name = "CloudCatalogDetails";
        retryCatalog.Click += async (_, _) => await LoadCloudProjectsAsync();
        catalogDetails.Click += (_, _) => { using var dialog = new DiagnosticDialog(catalogError ?? ""); dialog.ShowDialog(this); };
        layout.Controls.Add(indicator, 0, 0); layout.Controls.Add(catalogStatus, 1, 0); layout.Controls.Add(actions, 2, 0);
        catalogStrip.Controls.Add(layout);
        return catalogStrip;
    }

    private void UpdateProjectActions()
    {
        var ready = !controller.Busy && catalogCancellation is null;
        var local = projectGrid.CurrentRow?.DataBoundItem is ProjectRow { IsLocal: true };
        foreach (var button in localSelectionButtons) button.Enabled = !controller.Busy && local && controller.State.Configuration is not null;
        projectMore.Enabled = !controller.Busy && projectGrid.CurrentRow?.DataBoundItem is ProjectRow;
        foreach (ToolStripItem item in projectMore.ContextMenuStrip!.Items)
            if (item is ToolStripMenuItem) item.Enabled = !controller.Busy && (item.Name == "RemoveProject" || local);
        refreshCatalog.Enabled = ready && controller.CloudAccount.Enabled && !controller.CloudAccount.RequiresAuthorization;
        refreshCatalog.Enabled &= !controller.State.NetworkOffline;
        cancelCatalog.Visible = catalogCancellation is not null;
        catalogProgress.Visible = catalogCancellation is not null;
        catalogWarning.Visible = catalogDisplay == CatalogDisplay.Failed;
        catalogStrip.BackColor = catalogDisplay == CatalogDisplay.Failed && !SystemInformation.HighContrast
            ? Color.FromArgb(255, 248, 231) : AppStyle.Background;
        retryCatalog.Visible = catalogDisplay is CatalogDisplay.Failed or CatalogDisplay.Canceled;
        retryCatalog.Enabled = refreshCatalog.Enabled;
        catalogDetails.Visible = catalogDisplay == CatalogDisplay.Failed && catalogError is not null;
        foreach (var button in cloudProjectButtons) button.Enabled = refreshCatalog.Enabled;
    }

    private async Task LoadCloudProjectsAsync(bool retryAfterReconnect = false)
    {
        CheckCatalogAccount();
        if (catalogCancellation is not null || controller.Busy) return;
        if (!controller.CloudAccount.Enabled || controller.CloudAccount.RequiresAuthorization) return;
        retryCatalogOnReconnect = !retryAfterReconnect;
        if (controller.State.NetworkOffline)
        {
            catalogDisplay = CatalogDisplay.Offline;
            catalogError = null;
            UiText.Set(catalogStatus, "等待网络");
            UpdateProjectActions();
            return;
        }
        var account = controller.CloudAccount;
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        catalogCancellation = cancellation;
        catalogDisplay = CatalogDisplay.Loading;
        catalogError = null;
        UiText.Set(catalogStatus, "正在读取云端项目…");
        UpdateProjectActions();
        try
        {
            var result = await controller.ReadCloudProjectsAsync(cancellation.Token);
            cancellation.Token.ThrowIfCancellationRequested();
            if (IsDisposed || account != controller.CloudAccount) return;
            cloudProjects = result;
            catalogLoaded = true;
            catalogDisplay = CatalogDisplay.Loaded;
            BindProjectRows();
            ShowCatalogCount();
        }
        catch (OperationCanceledException)
        {
            if (!IsDisposed && account == controller.CloudAccount)
            {
                catalogDisplay = CatalogDisplay.Canceled;
                UiText.Set(catalogStatus, "云端项目加载已停止，可重新刷新。");
            }
        }
        catch (Exception ex)
        {
            if (!IsDisposed && account == controller.CloudAccount)
            {
                if (CloudNetworkAvailability.FriendlyMessage(ex) == CloudNetworkAvailability.Offline)
                {
                    catalogDisplay = CatalogDisplay.Offline;
                    UiText.Set(catalogStatus, "等待网络");
                    controller.ObserveOffline();
                }
                else
                {
                    catalogDisplay = CatalogDisplay.Failed;
                    catalogError = CloudAccountPresentation.Error(ex);
                    UiText.Set(catalogStatus, catalogLoaded ? "刷新失败，显示上次结果" : "云端项目加载失败");
                }
            }
        }
        finally
        {
            catalogCancellation = null;
            if (!IsDisposed) { CheckCatalogConnectivity(controller.State); UpdateProjectActions(); }
        }
    }
}
