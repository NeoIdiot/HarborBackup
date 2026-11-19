using HarborBackup.Core.Domain;
using HarborBackup.Desktop.Localization;

namespace HarborBackup.Desktop.Presentation;

internal static class RestoreReportPresentation
{
    internal static string Details(RestoreReport report)
    {
        var entries = report.Files.Select(file =>
        {
            var text = UiText.Text("来源：{0}\n路径：{1}\n结果：{2}\n已核验字节：{3:N0}",
                UiText.Text(file.Scope == RestoreScope.Current ? "云端当前文件" : "上一份归档"), file.Path,
                Status(file.Status), file.Status == RestoreFileStatus.Verified ? file.Bytes : 0);
            if (file.OutputPath is { } path) text += "\n" + UiText.Text("保存位置：{0}", path);
            if (file.Error is { } error) text += "\n" + UiText.Text("说明：{0}", Error(error));
            return text;
        }).ToList();
        if (entries.Count == 0) entries.Add(UiText.Text("本轮没有可核验的文件。"));
        entries.AddRange(report.Diagnostics.Select(d => UiText.Text("说明：{0}", Error(d))));
        return string.Join("\n\n", entries).Replace("\r\n", "\n").Replace("\n", Environment.NewLine);
    }

    private static string Status(RestoreFileStatus status) => UiText.Text(status switch
    {
        RestoreFileStatus.Verified => "正文校验通过",
        RestoreFileStatus.EvidenceInsufficient => "SHA-256 证据不足",
        RestoreFileStatus.BudgetSkipped => "本轮预算暂未覆盖",
        RestoreFileStatus.Failed => "恢复失败",
        RestoreFileStatus.Cancelled => "已安全停止",
        _ => "证据不足，无法确认"
    });

    private static string Error(string error) => error switch
    {
        "Restore-test byte budget reached." => UiText.Text("本轮正文预算未覆盖此文件；可增加预算后核验。"),
        "Trusted SHA-256 evidence is unavailable; no content was restored." => UiText.Text("缺少可信的 SHA-256 证据，未下载该文件。"),
        "Cloud restore version changed; list candidates again." => UiText.Text("云端版本已变化，请重新列出可恢复文件。"),
        "Cloud restore version changed during download." => UiText.Text("下载期间云端版本发生变化，本次结果未保存。"),
        "Restore never overwrites an existing path." => UiText.Text("输出位置已有同名内容，请选择其他目录。"),
        "Restored content length differs from evidence." => UiText.Text("下载内容的大小与记录不符，本次结果未保存。"),
        "Restored content checksum differs from evidence." => UiText.Text("下载内容的摘要与记录不符，本次结果未保存。"),
        "Restore project is no longer configured." => UiText.Text("此项目已从本机配置移除，请重新选择项目。"),
        "Restore mapping or candidate changed." => UiText.Text("项目映射或候选版本已变化，请重新列出可恢复文件。"),
        _ => CloudAccountPresentation.Message(error)
    };
}
