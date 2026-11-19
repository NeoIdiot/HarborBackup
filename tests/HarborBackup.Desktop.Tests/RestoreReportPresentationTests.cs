using HarborBackup.Core.Domain;
using HarborBackup.Desktop.Localization;
using HarborBackup.Desktop.Presentation;

namespace HarborBackup.Windows.Tests;

[TestClass]
[DoNotParallelize]
public sealed class RestoreReportPresentationTests
{
    [TestMethod]
    [DataRow("zh-CN")]
    [DataRow("en-US")]
    public void RestoreDetailsExplainEachFileWithoutShowingJson(string language)
    {
        UiText.SetLanguage(language);
        var now = DateTimeOffset.UtcNow;
        var report = new RestoreReport("RestoreTest", now, now, @"C:\restore", [
            new("one", "project", "docs/first.txt", RestoreScope.Current, RestoreFileStatus.Verified, 1234, @"C:\restore\first.txt"),
            new("two", "project", "docs/old.txt", RestoreScope.Archive, RestoreFileStatus.Failed, 9876,
                Error: "Restored content checksum differs from evidence."),
            new("three", "project", "docs/later.txt", RestoreScope.Current, RestoreFileStatus.BudgetSkipped, 0,
                Error: "Restore-test byte budget reached.")], []);
        var text = RestoreReportPresentation.Details(report);
        Assert.Contains("docs/first.txt", text); Assert.Contains("docs/old.txt", text); Assert.Contains("docs/later.txt", text);
        Assert.Contains(UiText.Text("云端当前文件"), text); Assert.Contains(UiText.Text("上一份归档"), text);
        Assert.Contains(UiText.Text("正文校验通过"), text); Assert.Contains(UiText.Text("恢复失败"), text);
        Assert.Contains(UiText.Text("本轮预算暂未覆盖"), text);
        Assert.Contains(UiText.Text("下载内容的摘要与记录不符，本次结果未保存。"), text);
        Assert.Contains("1,234", text); Assert.DoesNotContain("9,876", text);
        Assert.DoesNotContain("candidateId", text); Assert.DoesNotContain("BudgetSkipped", text);
        Assert.Contains(@"C:\restore\first.txt", text);
    }
}
