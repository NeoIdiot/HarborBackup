using HarborBackup.Core.Domain;
using HarborBackup.OneDrive.Transport;

namespace HarborBackup.Windows.Integration;

public static class CloudRunStatus
{
    public static bool IsOfflineIssue(string code, string? message) => message == CloudNetworkAvailability.Offline ||
        code == "DeviceStatusPending" && message?.EndsWith("\n" + CloudNetworkAvailability.Offline, StringComparison.Ordinal) == true;

    public static bool IsOfflineInterruption(ProjectRun run) => run.ExitCode != 0 &&
        run.Issues.All(i => IsOfflineIssue(i.Code, i.Message)) &&
        run.Files.Where(f => f.NeedsAttention()).All(f => f.Error == CloudNetworkAvailability.Offline);
}
