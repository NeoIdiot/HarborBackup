using System.Diagnostics;
using HarborBackup.Windows.Integration;
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
    private void BuildIssuesPage()
    {
        var issuesPage = Page("待处理问题");
        detailGrid.Name = "IssuesGrid";
        issuesPage.Controls.Add(GridSurface(detailGrid, "\uE73E", "当前视图没有待处理条目", "这里只显示最近结果中需要处理的条目；历史动作请在运行记录中查看。"));
        issuesPage.Controls.Add(detailCount);
        var filterFrame = AppStyle.Input(filter); filterFrame.Dock = DockStyle.Top;
        issuesPage.Controls.Add(filterFrame);
        issuesPage.Controls.Add(AppStyle.Toolbar(AppStyle.Button("打开冲突目录", async (_, _) => await OpenProtectionAsync("conflicts")),
            AppStyle.Button("打开归档目录", async (_, _) => await OpenProtectionAsync("archives")),
            AppStyle.Button("设备文件状态", (_, _) => { using var dialog = new SharedMetadataDialog(controller); dialog.ShowDialog(this); }),
            AppStyle.Button("解决选中文件冲突", async (_, _) => await ResolveSelectedConflictAsync())));
        issuesPage.Controls.Add(AppStyle.Toolbar(Operation("恢复未完成事务", DesktopCommand.Recover),
            AppStyle.Button("独立目录恢复", (_, _) => { using var dialog = new RestoreDialog(controller); dialog.ShowDialog(this); }),
            AppStyle.Button("查看选中详情", (_, _) => ShowSelectedIssue())));
        detailGrid.CellDoubleClick += (_, e) => { if (e.RowIndex >= 0) ShowSelectedIssue(); };
        filter.PlaceholderText = "筛选项目、路径、动作或错误…";
        filter.AccessibleName = "筛选项目、路径、动作或错误";
        filter.TextChanged += (_, _) => ShowDetails();
    }
    private void ShowSelectedIssue()
    {
        if (detailGrid.CurrentRow?.DataBoundItem is not DetailRow row) return;
        using var dialog = new RunDetailsDialog([row]);
        dialog.ShowDialog(this);
    }
    private async Task OpenProtectionAsync(string kind)
    {
        try
        {
            var url = await controller.ExclusiveAsync(() =>
            {
                using var drive = new CloudAccountStore(controller.Paths.Data).Open();
                var repository = new CloudRepository(drive, controller.CloudAccount, controller.Paths.Data);
                return repository.FolderAsync(kind, false, default).GetAwaiter().GetResult()?.WebUrl;
            });
            if (url is null || !Uri.TryCreate(url, UriKind.Absolute, out var uri) || uri.Scheme != "https") throw new IOException("Cloud protection folder has not been created.");
            Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
        }
        catch (Exception ex) { ShowError(CloudAccountPresentation.Error(ex)); }
    }
    private async Task ResolveSelectedConflictAsync()
    {
        if (detailGrid.CurrentRow?.DataBoundItem is not DetailRow row || row.ActionProjectId is not { } projectId || string.IsNullOrEmpty(row.Path)) return;
        try
        {
                var expected = await controller.ExclusiveAsync(() =>
                {
                    using var drive = new CloudAccountStore(controller.Paths.Data).Open();
                    return CloudConflictResolution.InspectAsync(new BackupRuntime(controller.Paths).Load(), projectId, row.Path, drive, default).GetAwaiter().GetResult();
                });
                using var choice = new CloudConflictDialog(expected, (selected, outputRoot, token) => controller.ExclusiveAsync(cancellation =>
                {
                    using var drive = new CloudAccountStore(controller.Paths.Data).Open();
                    return CloudConflictExport.ExportAsync(new BackupRuntime(controller.Paths).Load(), expected, selected, drive,
                        outputRoot, cancellation).GetAwaiter().GetResult();
                }, token));
                if (choice.ShowDialog(this) != DialogResult.OK) return;
                await controller.ExclusiveAsync(() =>
                {
                    using var drive = new CloudAccountStore(controller.Paths.Data).Open();
                    CloudConflictResolution.ResolveAsync(new BackupRuntime(controller.Paths).Load(), expected, choice.Selected!, drive, default).GetAwaiter().GetResult();
                    return true;
                });
                await controller.RunAsync(DesktopCommand.Preview, projectId);
                ShowDetails();
        }
        catch (Exception ex) { ShowError(CloudAccountPresentation.Error(ex)); }
    }
}
