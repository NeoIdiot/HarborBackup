using HarborBackup.Core.Domain;
using HarborBackup.OneDrive.Transport;

namespace HarborBackup.Windows.Integration;

public static class CloudRunStatus
{
    public static bool IsAutomaticRetry(FileResult file) => Recovery(file) == RecoveryDisposition.AutomaticRetry;
    public static bool IsAutomaticRetry(PreviewIssue issue) => Recovery(issue) == RecoveryDisposition.AutomaticRetry;

    // A mixed project may still contain independently resumable files. This is a
    // scheduling hint, never permission to replay an unverified journal.
    public static bool IsAutomaticRetry(ProjectRun run) => run.Recovery != RecoveryDisposition.Stopped &&
        (run.Files.Any(IsAutomaticRetry) || run.Issues.Any(IsAutomaticRetry) ||
            run.Recovery == RecoveryDisposition.AutomaticRetry &&
            !run.Files.Select(Recovery).Concat(run.Issues.Select(Recovery)).Any(r =>
                r is RecoveryDisposition.RepairRequired or RecoveryDisposition.AwaitingConfirmation or RecoveryDisposition.Stopped));

    public static bool IsAutomaticRetryOnly(ProjectRun run) => IsAutomaticRetry(run) &&
        Recovery(run) == RecoveryDisposition.AutomaticRetry &&
        run.Files.Where(f => f.NeedsAttention()).All(IsAutomaticRetry) && run.Issues.All(IsAutomaticRetry);

    public static RecoveryDisposition Recovery(ProjectRun run)
    {
        if (run.Recovery == RecoveryDisposition.Stopped) return RecoveryDisposition.Stopped;
        var states = run.Files.Select(Recovery).Concat(run.Issues.Select(Recovery)).Append(run.Recovery).ToArray();
        foreach (var state in new[] { RecoveryDisposition.RepairRequired, RecoveryDisposition.AwaitingConfirmation,
            RecoveryDisposition.AutomaticRetry, RecoveryDisposition.Stopped })
            if (states.Contains(state)) return state;
        return RecoveryDisposition.None;
    }

    public static RecoveryDisposition Recovery(FileResult file) => file.Recovery == RecoveryDisposition.Stopped ? file.Recovery :
        file.FailureKind is FileFailureKind.Authorization or FileFailureKind.RecoveryRequired ? RecoveryDisposition.RepairRequired :
        file.FailureKind == FileFailureKind.Cancelled ? RecoveryDisposition.Stopped :
        file.Action is FileAction.ConflictUnresolved or FileAction.TypeConflict or FileAction.NeedsPull or FileAction.NeedsPush
            ? RecoveryDisposition.AwaitingConfirmation : file.Recovery;

    public static RecoveryDisposition Recovery(PreviewIssue issue) => issue.Recovery == RecoveryDisposition.Stopped ? issue.Recovery :
        issue.FailureKind is FileFailureKind.Authorization or FileFailureKind.RecoveryRequired ? RecoveryDisposition.RepairRequired :
        issue.FailureKind == FileFailureKind.Cancelled ? RecoveryDisposition.Stopped : issue.Recovery;

    public static string Result(ProjectRun run) => Recovery(run) switch
    {
        RecoveryDisposition.AutomaticRetry => "WaitingForRetry",
        RecoveryDisposition.AwaitingConfirmation => "AwaitingConfirmation",
        RecoveryDisposition.RepairRequired => "RepairRequired",
        RecoveryDisposition.Stopped => "Stopped",
        _ => run.ExitCode == 0 ? "Success" : IsOfflineInterruption(run) ? "OfflineInterrupted" : run.ExitCode == 1 ? "Failed" : "Warning"
    };

    public static bool IsOfflineIssue(string code, string? message) => message == CloudNetworkAvailability.Offline ||
        code == "DeviceStatusPending" && message?.EndsWith("\n" + CloudNetworkAvailability.Offline, StringComparison.Ordinal) == true;

    public static bool IsOfflineInterruption(ProjectRun run) => run.ExitCode != 0 && (IsAutomaticRetryOnly(run) &&
        (run.Files.Any(f => f.FailureKind == FileFailureKind.Network) || run.Issues.Any(i => i.FailureKind == FileFailureKind.Network)) ||
        run.Recovery == RecoveryDisposition.None &&
        run.Issues.All(i => IsOfflineIssue(i.Code, i.Message)) &&
        run.Files.Where(f => f.NeedsAttention()).All(f => f.Error == CloudNetworkAvailability.Offline));
}
