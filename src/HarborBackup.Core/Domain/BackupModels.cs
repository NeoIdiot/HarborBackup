namespace HarborBackup.Core.Domain;

public enum BackupPhase { Recovery, ReadingMetadata, Scanning, Processing, Uploading, Verifying, Protecting, Publishing }
public sealed record BackupProgress(BackupPhase Phase, string Project, string? Path = null, int Processed = 0,
    int? Total = null, long HashedBytes = 0);

public enum LocalRetention { KeepLocal, OnlineOnly }
public sealed record FileResult(string Path, FileAction Action, bool Changed, string? Error = null)
{
    public FileFailureKind FailureKind { get; init; }
    [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    public SyncDirection? Direction { get; init; }
    public bool NeedsAttention() => Error is not null || FailureKind is FileFailureKind.Authorization or FileFailureKind.Network ||
        Action is FileAction.Deferred or FileAction.TypeConflict or FileAction.ConflictUnresolved or FileAction.NeedsPull or FileAction.NeedsPush;
}
public sealed record ProjectRun(string Destination, IReadOnlyList<FileResult> Files, IReadOnlyList<PreviewIssue> Issues,
    ReadMetrics Metrics)
{
    public string ProjectId { get; init; } = "";
    public bool? CoverageComplete { get; init; }
    public int ExitCode => Issues.Any(i => i.IsFailure) || Files.Any(f => f.FailureKind is FileFailureKind.Authorization or FileFailureKind.Network) ? 1 : Issues.Count != 0 ||
        Files.Any(f => f.NeedsAttention()) ? 2 : 0;
}
public sealed record BackupReport(string Mode, DateTimeOffset StartedAtUtc, DateTimeOffset CompletedAtUtc,
    IReadOnlyList<ProjectRun> Projects, bool Cancelled = false)
{
    public string CloudUploadStatus { get; init; } = "Unverified";
    public string? BudgetStopReason { get; init; }
    public string Completion => Cancelled ? "Cancelled" : ExitCode == 1 ? "Failed" :
        BudgetStopReason is not null && Diagnostics.Count == 0 && !Projects.Any(p => p.Issues.Any(i => i.Code != BudgetStopReason)) &&
        !Projects.SelectMany(p => p.Files).Any(f => f.FailureKind != FileFailureKind.Budget && f.NeedsAttention())
            ? "BudgetLimited" : ExitCode == 0 ? "Completed" : "NeedsAttention";
    public IReadOnlyList<PreviewIssue> Diagnostics { get; init; } = [];
    public int ExitCode => Cancelled ? 130 : Projects.Any(p => p.ExitCode == 1) ? 1 :
        Diagnostics.Count > 0 || Projects.Any(p => p.ExitCode == 2) ? 2 : 0;
}
public sealed class DeferredOperationException(string message) : Exception(message);
