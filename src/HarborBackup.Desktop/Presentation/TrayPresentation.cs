using HarborBackup.Desktop.Localization;
using HarborBackup.Windows.Persistence.Configuration;

namespace HarborBackup.Desktop.Presentation;

internal enum TrayBadge { None, Running, Attention, Failed, Paused, Waiting }

internal sealed record TrayPresentation(TrayBadge Badge, string ToolTip)
{
    internal static TrayPresentation Create(DesktopState state, CloudAccountSettings account)
    {
        var attention = state.Details.Where(d => d.Attention).ToArray();
        var files = attention.Where(d => !d.SystemRow && d.Path.Length > 0)
            .Select(d => (d.ProjectId.Length > 0 ? d.ProjectId : d.Project) + "\0" + d.Path).Distinct(StringComparer.OrdinalIgnoreCase).Count();
        var connection = state.NeedsSetup || !account.Enabled || account.RequiresAuthorization;
        var network = state.NetworkOffline && !connection;
        // Current blocking errors win; saved per-project results do not cover a running operation.
        var failed = state.Status == "请检查配置" || !connection &&
            (state.Error is not null || state.Status is "失败" or "运行失败");
        var badge = failed ? TrayBadge.Failed : state.OperationBusy ? TrayBadge.Running : network ? TrayBadge.Waiting :
            !connection && state.Projects.Any(p => p.Result == "失败") ? TrayBadge.Failed :
            !connection && (attention.Length > 0 || state.Projects.Any(p => p.Result == "有待处理问题")) ? TrayBadge.Attention :
            connection ? TrayBadge.Waiting : state.Paused ? TrayBadge.Paused : TrayBadge.None;
        var caption = badge switch
        {
            TrayBadge.Failed => UiText.Text("操作失败"),
            TrayBadge.Running => UiText.Text(state.CanCancel ? state.Status : "正在运行"),
            TrayBadge.Attention => files == 1 ? UiText.Text("托盘单个文件待处理") : files > 1 ? UiText.Text("托盘待处理文件：{0}", files) : UiText.Text("有待处理问题"),
            TrayBadge.Paused => UiText.Text("托盘自动备份已暂停"),
            TrayBadge.Waiting => UiText.Text(network ? "等待网络" : account.RequiresAuthorization ? "需要重新授权" : "等待连接"),
            _ => UiText.Text("空闲")
        };
        var text = "HarborBackup · " + caption;
        void Append(string value) { if (text.Length + value.Length + 3 <= 63) text += " · " + value; }
        if (state.Paused && badge != TrayBadge.Paused) Append(UiText.Text("托盘自动备份已暂停"));
        if (files > 0 && badge is TrayBadge.Running or TrayBadge.Failed) Append(UiText.Text("托盘待处理计数：{0}", files));
        return new(badge, text.Length <= 63 ? text : "HarborBackup · " + UiText.Text("正在运行"));
    }
}
