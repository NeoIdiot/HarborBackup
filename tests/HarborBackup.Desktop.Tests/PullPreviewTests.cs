using HarborBackup.Core.Domain;
using HarborBackup.Desktop.Localization;
using HarborBackup.Desktop.Presentation;
using HarborBackup.Desktop.Views.Dialogs;

namespace HarborBackup.Windows.Tests;

[TestClass]
[DoNotParallelize]
public sealed class PullPreviewTests
{
    [TestMethod]
    [DataRow("zh-CN")]
    [DataRow("en-US")]
    public async Task RetryIsSelectableAndProjectSummaryDoesNotDoubleCountPendingFile(string language)
    {
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            try
            {
                UiText.SetLanguage(language);
                var retry = new FileDecision(FileAction.Copy, false, FileSide.Source, FileSide.None, ProtectionKind.None, "RetryInterruptedPull");
                var ordinary = retry with { Reason = "TargetOnly" };
                var blocked = new FileDecision(FileAction.Deferred, false, FileSide.None, FileSide.None, ProtectionKind.None, "PendingFileOperation");
                var projectId = new string('a', 64);
                var report = new PreviewReport([new("Demo", [new("retry.docx", retry, false, false),
                    new("new.docx", ordinary, false, false), new("blocked.docx", blocked, false, false)],
                    [new("PendingOperationsRetained", "未完成操作涉及的文件暂不可写入；其他文件仍可选择，旧恢复材料会保留。", false)], new()) { ProjectId = projectId }])
                { PullSelection = new("Demo", true, [new("retry.docx", "retry", true, 10), new("new.docx", "new", true, 20)]) { ProjectId = projectId } };
                using var dialog = new PreviewDialog(report, true, @"C:\Example", "OneDrive/Demo");
                dialog.Show();
                var grid = (DataGridView)dialog.Controls.Find("PullPreviewGrid", true).Single();
                var counts = dialog.Controls.Find("PreviewCounts", true).Single();
                StringAssert.Contains(counts.Text, language == "zh-CN" ? "待处理 1" : "Needs attention 1");
                Assert.IsTrue((bool)grid.Rows[0].Cells["Selected"].Value!);
                Assert.IsTrue(grid.Rows[2].Cells["Selected"].ReadOnly);
                var rows = ReportPresentation.Details(report);
                StringAssert.Contains(rows[0].DisplayDetails, language == "zh-CN" ? "重新下载" : "Download again");
                StringAssert.Contains(rows[1].DisplayDetails, language == "zh-CN" ? "下载到本地" : "Download to the local folder");
                Assert.IsFalse(rows.Any(r => r.DisplayDetails.Contains("None/None")));
                Assert.IsGreaterThan(70, grid.Height);
                if (Environment.GetEnvironmentVariable("HARBORBACKUP_SCREENSHOT_DIR") is { } folder)
                {
                    Directory.CreateDirectory(folder);
                    using var bitmap = new System.Drawing.Bitmap(dialog.Width, dialog.Height);
                    dialog.DrawToBitmap(bitmap, new System.Drawing.Rectangle(0, 0, bitmap.Width, bitmap.Height));
                    bitmap.Save(Path.Combine(folder, "pull-retry-" + language + ".png"));
                }
                ((Button)dialog.Controls.Find("ExecuteSelectedPull", true).Single()).PerformClick();
                Assert.HasCount(2, dialog.SelectedPull!.Files);
                completion.TrySetResult();
            }
            catch (Exception ex) { completion.TrySetException(ex); }
        }) { IsBackground = true };
        thread.SetApartmentState(ApartmentState.STA); thread.Start();
        await completion.Task.WaitAsync(TimeSpan.FromSeconds(15));
        Assert.IsTrue(thread.Join(TimeSpan.FromSeconds(3)));
    }
}
