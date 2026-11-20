using HarborBackup.Windows.Integration;
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
    private readonly Button projectMore = AppStyle.Button("更多操作  ▾");
    private readonly Button pauseProject = AppStyle.Button("暂停选中备份");
    private void BuildProjectsPage()
    {
        var projectsPage = Page("备份项目");
        projectsPage.Controls.Add(GridSurface(projectGrid, "\uE8B7", "添加第一个备份项目", "选择本地源目录和 OneDrive 中的目标路径。保存前可以预览。"));
        projectsPage.Controls.Add(BuildCatalogStatus());
        pauseProject.Name = "PauseSelectedProject";
        pauseProject.Click += async (_, _) => await ToggleProjectPauseAsync();
        var more = projectMore;
        more.Name = "ProjectMoreActions";
        var moreMenu = new AppMenu();
        moreMenu.Items.Add("查看项目状态…", null, (_, _) =>
        {
            if (projectGrid.CurrentRow?.DataBoundItem is not ProjectRow row) return;
            using var dialog = new ProjectStatusDialog(row);
            dialog.ShowDialog(this);
        }).Name = "ViewProjectStatus";
        moreMenu.Items.Add("完整核查（只读）", null, async (_, _) => await ExecuteAsync(DesktopCommand.Verify, true));
        moreMenu.Items.Add("核查并备份", null, async (_, _) => await ExecuteAsync(DesktopCommand.FullBackup, true));
        moreMenu.Items.Add(new ToolStripSeparator());
        moreMenu.Items.Add("移除项目…", null, async (_, _) => await RemoveAsync()).Name = "RemoveProject";
        foreach (ToolStripItem item in moreMenu.Items) if (item is not ToolStripSeparator) UiText.Bind(item, item.Text ?? "");
        more.Click += (_, _) => moreMenu.Show(more, new Point(0, more.Height));
        more.ContextMenuStrip = moreMenu;
        Disposed += (_, _) => moreMenu.Dispose();
        projectsPage.Controls.Add(AppStyle.Toolbar(Operation("预览上传", DesktopCommand.Preview, true), Operation("上传选中", DesktopCommand.Backup, true),
            Operation("下载更新…", DesktopCommand.PullPreview, true), CloudProjectButton("取回云端项目…", true), pauseProject, more));
        projectGrid.Name = "ProjectGrid";
        projectGrid.DefaultCellStyle.WrapMode = DataGridViewTriState.False;
        projectGrid.ColumnHeadersDefaultCellStyle.WrapMode = DataGridViewTriState.False;
        projectGrid.AutoSizeRowsMode = DataGridViewAutoSizeRowsMode.None;
        void FitStatusColumns()
        {
            int Width(params string[] keys) => keys.Max(key => TextRenderer.MeasureText(UiText.Text(key), projectGrid.Font).Width)
                + projectGrid.DefaultCellStyle.Padding.Horizontal + 6;
            projectGrid.Columns[4].MinimumWidth = Width("保护状态", "状态未知", "待初始化", "有待处理项", "记录较旧", "无已知积压");
            projectGrid.Columns[5].MinimumWidth = Width("备份模式", "仅上传", "双向备份", "仅下载", "项目备份已暂停");
        }
        UiText.Watch(projectGrid, FitStatusColumns);
        projectGrid.FontChanged += (_, _) => FitStatusColumns();
        projectGrid.DpiChangedAfterParent += (_, _) => FitStatusColumns();
        projectGrid.CellToolTipTextNeeded += (_, e) =>
        {
            if (e.RowIndex < 0 || e.ColumnIndex < 0 || projectGrid.Rows[e.RowIndex].DataBoundItem is not ProjectRow row) return;
            e.ToolTipText = projectGrid.Columns[e.ColumnIndex].DataPropertyName switch
            {
                "DisplayProtection" => row.DisplayProtectionDetails,
                "Destination" => row.Target,
                "DisplaySource" => row.DisplaySource,
                _ => projectGrid.Rows[e.RowIndex].Cells[e.ColumnIndex].FormattedValue?.ToString() ?? ""
            };
        };
        projectGrid.CurrentCellChanged += (_, _) => UpdateProjectActions();
        refreshCatalog.Click += async (_, _) => await LoadCloudProjectsAsync();
        cancelCatalog.Click += (_, _) => catalogCancellation?.Cancel();
        var add = EditButton("添加项目", null);
        projectsPage.Controls.Add(AppStyle.Toolbar(add, CloudProjectButton("加入云端项目", false), EditButton("编辑选中", -1), EditButton("全局排除", null, true), refreshCatalog));
    }

    private async Task ToggleProjectPauseAsync()
    {
        if (controller.Busy || projectGrid.CurrentRow?.DataBoundItem is not ProjectRow { IsLocal: true } row) return;
        try
        {
            var draft = await presenter.LoadAsync();
            var entry = draft.Projects["folders"]!.AsArray().Single(p => p?["id"]?.GetValue<string>() == row.ProjectId)!;
            entry["backupPaused"] = !(entry["backupPaused"]?.GetValue<bool>() ?? false);
            await presenter.SaveAsync(draft, ConfigurationPart.Projects);
        }
        catch (Exception ex) { ShowError(CloudAccountPresentation.Error(ex)); }
    }

    private Button CloudProjectButton(string title, bool retrieve)
    {
        var button = AppStyle.Button(title, async (_, _) => await JoinCloudProjectAsync(retrieve));
        cloudProjectButtons.Add(button);
        return button;
    }

    private async Task JoinCloudProjectAsync(bool retrieve)
    {
        if (!controller.CloudAccount.Enabled || controller.CloudAccount.RequiresAuthorization) { ShowError(UiText.Text("请先连接并浏览云端目录。")); return; }
        try
        {
            CloudProjectTarget selected;
            if (projectGrid.CurrentRow?.DataBoundItem is ProjectRow { CloudFolderId: not null } row)
                selected = new(row.Destination, row.CloudFolderId, row.ProjectId);
            else
            {
                await LoadCloudProjectsAsync();
                if (!catalogLoaded || AvailableCloudProjects.Count == 0) return;
                using var picker = new CloudProjectDialog(AvailableCloudProjects, retrieve);
                if (picker.ShowDialog(this) != DialogResult.OK || picker.Selected is not { } choice) return;
                selected = choice;
            }
            var draft = await presenter.LoadAsync();
            var existing = draft.Projects["folders"]!.AsArray().FirstOrDefault(p =>
                p?["id"]?.GetValue<string>() == selected.ProjectId);
            string source;
            if (existing is not null)
            {
                if (!retrieve) { ShowError(UiText.Text("此云端项目已加入本机列表。")); return; }
                source = existing["source"]!.GetValue<string>();
                selected = new(existing["destination"]!.GetValue<string>(), selected.FolderId, selected.ProjectId);
            }
            else
            {
                using var local = new FolderBrowserDialog { Description = UiText.Text("选择此项目在本机的保存目录") };
                if (local.ShowDialog(this) != DialogResult.OK) return;
                source = local.SelectedPath;
                draft.Projects["folders"]!.AsArray().Add(new System.Text.Json.Nodes.JsonObject
                { ["id"] = selected.ProjectId, ["source"] = source, ["destination"] = selected.Destination, ["cloudFolderId"] = selected.FolderId });
            }
            if (!retrieve)
            {
                await presenter.SaveAsync(draft, ConfigurationPart.Projects);
                await controller.RefreshAsync(true);
                return;
            }
            var preview = await controller.ExclusiveAsync(() => controller.Editor.Preview(draft, selected.ProjectId, SyncDirection.Pull));
            using var review = new PreviewDialog(preview, true, source, CloudAccountPresentation.Path(selected.Destination));
            if (review.ShowDialog(this) != DialogResult.OK || review.SelectedPull is not { } selection) return;
            await controller.RunAsync(DesktopCommand.Pull, selected.ProjectId, confirmedPull: selection, joinDraft: existing is null ? draft : null);
        }
        catch (Exception ex) { ShowError(CloudAccountPresentation.Error(ex)); }
    }
}
