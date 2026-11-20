using HarborBackup.Core.Domain;
using HarborBackup.Desktop.Localization;
using HarborBackup.Desktop.Presentation;
using HarborBackup.OneDrive.Transport;

namespace HarborBackup.Desktop;

internal sealed class ProblemNotificationPolicy
{
    private int? lastProblem;

    internal bool ShouldNotify(DesktopState state)
    {
        if (state.Busy || state.Status == "已安全停止") return false;

        var error = state.Error is { } message && !IsOffline(message) ? message : null;
        var failed = state.Status is "失败" or "运行失败" or "请检查配置";
        if (state.Status != "请检查配置" && ReportPresentation.IsAutomaticRetryOnly(state.Details)) failed = false;
        var signature = new HashCode();
        var issueCount = 0;
        foreach (var row in state.Details)
        {
            if (!row.Attention || row.IsOfflineIssue || row.IsAutomaticRetry || row.Recovery == RecoveryDisposition.Stopped ||
                row.FailureKind is FileFailureKind.Budget or FileFailureKind.Cancelled ||
                row.Action is "AutomaticTimeBudgetReached" or "AutomaticTransferBudgetReached") continue;
            signature.Add(row.ProjectId.Length > 0 ? row.ProjectId : row.Project);
            signature.Add(row.Path);
            signature.Add(row.Action);
            signature.Add(row.Details);
            issueCount++;
        }
        if (state.NeedsSetup && !failed || error is null && issueCount == 0 && !failed)
        {
            lastProblem = null;
            return false;
        }

        signature.Add(error);
        signature.Add(failed);
        var key = signature.ToHashCode();
        if (lastProblem == key) return false;
        lastProblem = key;
        return true;
    }

    internal static bool IsOffline(string message) => message == CloudNetworkAvailability.Offline ||
        message == UiText.Text(CloudNetworkAvailability.Offline);
}
