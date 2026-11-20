using HarborBackup.Core.Application;
using HarborBackup.Core.Domain;
using HarborBackup.Desktop.Presentation;
using HarborBackup.Windows.Integration;

namespace HarborBackup.Desktop.Controllers;

internal sealed partial class DesktopController
{
    private readonly TimeProvider clock = timeProvider ?? TimeProvider.System;
    private readonly RecoveryRetrySchedule recoveryRetries = new(timeProvider, retryJitter);
    private long? connectedAt;

    private bool CanDispatchScheduled => !stopping && !Busy && !coordinator.Paused && !networkOffline &&
        config is { Projects.Count: > 0 } && CloudAccount.Enabled && !CloudAccount.RequiresAuthorization &&
        (connectedAt is null || clock.GetElapsedTime(connectedAt.Value) >= TimeSpan.FromSeconds(2));

    private DateTimeOffset? NextWorkDueUtc => new[] { schedule?.NextDueUtc, recoveryRetries.NextDueUtc }.Min();
    private RecoveryDisposition CurrentRecovery => SummaryRecovery(projects.Select(p => p.Recovery));

    private static RecoveryDisposition SummaryRecovery(IEnumerable<RecoveryDisposition> recoveries)
    {
        var states = recoveries.ToArray();
        return new[] { RecoveryDisposition.RepairRequired, RecoveryDisposition.AwaitingConfirmation,
            RecoveryDisposition.AutomaticRetry, RecoveryDisposition.Stopped }.FirstOrDefault(states.Contains);
    }

    private RecoveryRetryKey? RetryKey(string? projectId)
    {
        var project = config?.Projects.SingleOrDefault(p => p.ProjectId == projectId);
        return project is null ? null : new(project.ProjectId, CloudMapping.Bind(project, config!));
    }

    private void SynchronizeRetries(IReadOnlyList<CloudAutomaticRetryCandidate> candidates)
    {
        var current = candidates.Where(c => RetryKey(c.ProjectId)?.Binding == c.Binding)
            .Select(c => new RecoveryRetryCandidate(new(c.ProjectId, c.Binding), c.RetryNotBeforeUtc));
        recoveryRetries.Synchronize(current);
    }

    private void ObserveRetryResult(BackupReport report)
    {
        foreach (var project in report.Projects)
        {
            if (RetryKey(project.ProjectId) is not { } key) continue;
            if (!report.Cancelled && report.BudgetStopReason is null && CloudRunStatus.IsAutomaticRetry(project))
                recoveryRetries.Failed(key, project.RetryNotBeforeUtc);
            else recoveryRetries.Remove(key);
        }
    }

    private static string ReportStatus(BackupReport report)
    {
        if (report.Cancelled) return "已安全停止";
        if (report.Completion == "BudgetLimited") return "预算已用完，下轮继续";
        if (report.Diagnostics.Count > 0) return ReportPresentation.Result(report.ExitCode);
        return SummaryRecovery(report.Projects.Select(CloudRunStatus.Recovery)) switch
        {
            RecoveryDisposition.RepairRequired => "需要修复后重试",
            RecoveryDisposition.AwaitingConfirmation => "需要确认版本",
            RecoveryDisposition.AutomaticRetry => "等待自动重试",
            RecoveryDisposition.Stopped => "已停止",
            _ => report.ExitCode == 0 || report.Projects.Count > 0 && report.Projects.All(CloudRunStatus.IsOfflineInterruption)
                ? "空闲" : ReportPresentation.Result(report.ExitCode)
        };
    }
}
