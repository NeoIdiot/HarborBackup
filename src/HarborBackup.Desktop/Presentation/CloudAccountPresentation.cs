using HarborBackup.Desktop.Localization;
using HarborBackup.OneDrive.Transport;
using HarborBackup.Windows.Persistence.Configuration;

namespace HarborBackup.Desktop.Presentation;

internal static class CloudAccountPresentation
{
    public static string Path(params string[] parts) => "OneDrive/" +
        string.Join("/", parts.Select(p => p.Trim('/')).Where(p => p.Length > 0));

    public static string Message(string message) => string.Join("\n", message.Replace("\r\n", "\n").Split('\n')
        .Select(line => UiText.Text(CloudNetworkAvailability.FriendlyMessage(line) ?? line)));

    public static string Error(Exception error) => CloudNetworkAvailability.FriendlyMessage(error) is { } message ? UiText.Text(message) : error is CloudRequestException cloud
        ? UiText.Text("OneDrive 请求失败。\n操作：{0}\nHTTP：{1}\n错误代码：{2}\n请求 ID：{3}",
            UiText.Text(cloud.Operation), (int)cloud.StatusCode, cloud.ErrorCode,
            cloud.RequestId ?? UiText.Text("未提供")) + "\n" + UiText.Text("服务域名：{0} · 尝试：{1} · 请求耗时：{2} ms", cloud.Host, cloud.Attempt, cloud.ElapsedMilliseconds)
        : UiText.Text(error.Message);

    public static string Summary(CloudAccountSettings account)
    {
        if (account.RequiresAuthorization) return UiText.Text("需要重新授权");
        if (account.AccountId is null) return UiText.Text("尚未连接");
        if (!account.Enabled) return UiText.Text("已连接，待选择云端仓库");
        return Path(account.RootName);
    }
}
