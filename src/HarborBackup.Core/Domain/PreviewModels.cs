namespace HarborBackup.Core.Domain;

public sealed record ProjectDefinition(string Source, string Destination, string Target,
    IReadOnlyList<string> ExcludedDirectories, IReadOnlyList<string> ExcludedFiles,
    string TaskId, string Fingerprint, string? ConfigurationError = null)
{
    public string? CloudFolderId { get; init; }
    public string? MappingVersion { get; init; }
    // Zero preserves the exact serialization and binding of existing recovery snapshots.
    [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingDefault)]
    public int BindingVersion { get; init; }
    // Presentation provenance must not change persisted recovery signatures or mapping bindings.
    [System.Text.Json.Serialization.JsonIgnore]
    public ExclusionRuleSources? ExclusionSources { get; init; }
    [System.Text.Json.Serialization.JsonIgnore]
    public string ProjectId => TaskId;
}
public sealed record HashPolicy(bool UseMetadataCache = true, int FullCheckIntervalDays = 7);
public sealed record PreviewSettings(string StateDirectory, string DeviceId, HashPolicy Hash, bool ContinueOnError = true);
public sealed record PreviewFile(string RelativePath, FileObservation Source, FileObservation Target,
    FileBaseline? Baseline);
public sealed record PreviewIssue(string Code, string Message, bool IsFailure = true)
{
    [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingDefault)]
    public FileFailureKind FailureKind { get; init; }
    [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingDefault)]
    public RecoveryDisposition Recovery { get; init; }
}
public sealed class ReadMetrics
{
    public long HashedFiles { get; set; }
    public long HashBytes { get; set; }
    public long CachedFiles { get; set; }
    public long ScannedEntries { get; set; }
    public long CloudDirectoriesListed { get; set; }
    public long CloudInventoryCacheHits { get; set; }
    public string? CloudInventoryInvalidationReason { get; set; }
    public long CloudMetadataCacheHits { get; set; }
    public long CloudMetadataRecordsRead { get; set; }
    public long CloudMetadataCheckpointHits { get; set; }
    public long CloudMetadataHistoricalRecordsSkipped { get; set; }
    public long CloudMetadataCatalogEntries { get; set; }
    public long CloudMetadataBatchFiles { get; set; }
    public long CloudObservationUploads { get; set; }
    public long CloudObservationSkipped { get; set; }
    public long CloudRequests { get; set; }
    public long CloudRetries { get; set; }
    public long CloudThrottledRequests { get; set; }
    public long CloudConcurrencyReductions { get; set; }
    public long CloudRetryWaitMilliseconds { get; set; }
    public long CloudReadMilliseconds { get; set; }
    public long ScanMilliseconds { get; set; }
    // Verified uploaded content, including protection uploads; not wire bytes or bandwidth.
    public long UploadHashPasses { get; set; }
    public long UploadHashBytes { get; set; }
    public long CloudUploadedFiles { get; set; }
    public long CloudUploadedBytes { get; set; }
    public long CloudUploadMilliseconds { get; set; }
    // Sum of file operation durations; overlaps under concurrency.
    public long FileProcessingMilliseconds { get; set; }
    public long LocalJournalWrites { get; set; }
    // A pruned directory counts once; its contents are deliberately not enumerated.
    public long ExcludedLocalEntries { get; set; }
    public long ExcludedCloudEntries { get; set; }
}
public sealed record ProjectSnapshot(IReadOnlyList<PreviewFile> Files,
    IReadOnlyList<PreviewIssue> Issues, ReadMetrics Metrics);
public sealed record PreviewPlan(string Path, FileDecision Decision, bool SourceVerified, bool TargetVerified,
    string? SourceError = null, string? TargetError = null)
{
    public VerificationCategory Verification { get; init; } = VerificationCategory.Unverifiable;
    public long TransferBytes { get; init; }
}
public sealed record ProjectPreview(string Destination, IReadOnlyList<PreviewPlan> Plans,
    IReadOnlyList<PreviewIssue> Issues, ReadMetrics Metrics)
{
    public ExclusionRuleSources? ExclusionSources { get; init; }
    public IReadOnlyList<string> ExcludedDirectories { get; init; } = [];
    public IReadOnlyList<string> ExcludedFiles { get; init; } = [];
    public string ProjectId { get; init; } = "";
    public VerificationSummary Verification => VerificationSummary.From(Plans);
    public int ExitCode => Issues.Any(i => i.IsFailure) ? 1 :
        Issues.Count != 0 || Plans.Any(p => p.Decision.NeedsAttention) ? 2 : 0;
}
public sealed record PullFileSelection(string Path, string Confirmation, bool IsNew, long Bytes);
public sealed record PullSelection(string Destination, bool EstablishBaseline, IReadOnlyList<PullFileSelection> Files)
{
    public string ProjectId { get; init; } = "";
}
public sealed record PreviewReport(IReadOnlyList<ProjectPreview> Projects)
{
    public PullSelection? PullSelection { get; init; }
    public PullSelection? UploadSelection { get; init; }
    public string Mode { get; init; } = "Preview";
    public PlanSummary Summary => PlanSummary.From(Projects);
    public VerificationSummary Verification => VerificationSummary.From(Projects.SelectMany(p => p.Plans));
    public string VerificationEvidence => "LocalSha256AndCloudMetadata";
    public IReadOnlyList<ProjectConfigurationImpact> ConfigurationImpact { get; init; } = [];
    public string CloudUploadStatus { get; init; } = "Unverified";
    public int ExitCode => Projects.Any(p => p.ExitCode == 1) ? 1 : Projects.Any(p => p.ExitCode == 2) ||
        Mode == "Verify" && (Verification.Different + Verification.MissingLocal + Verification.MissingCloud +
            Verification.Unverifiable + Verification.TypeConflicts > 0) ? 2 : 0;
}
