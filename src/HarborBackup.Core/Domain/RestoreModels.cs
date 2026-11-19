namespace HarborBackup.Core.Domain;

public enum RestoreScope { Current, Archive }
public enum RestoreFileStatus { Verified, EvidenceInsufficient, BudgetSkipped, Failed, Cancelled }

// A candidate is a version-bound read intent, never permission to replace a business file.
public sealed record RestoreCandidate(string Id, string ProjectId, string Destination, string Binding,
    string Path, RestoreScope Scope, string ItemId, string ETag, string? ParentId, string ItemName,
    long Length, string? Sha256, string? QuickXorHash, DateTimeOffset? ModifiedUtc);
public sealed record RestoreCatalog(IReadOnlyList<RestoreCandidate> Candidates, IReadOnlyList<string> Diagnostics);
public sealed record RestoreFileResult(string CandidateId, string ProjectId, string Path, RestoreScope Scope,
    RestoreFileStatus Status, long Bytes, string? OutputPath = null, string? Error = null);
public sealed record RestoreReport(string Mode, DateTimeOffset StartedUtc, DateTimeOffset CompletedUtc,
    string OutputRoot, IReadOnlyList<RestoreFileResult> Files, IReadOnlyList<string> Diagnostics,
    int Group = 0, int GroupCount = 1)
{
    public long VerifiedBytes => Files.Where(f => f.Status == RestoreFileStatus.Verified).Sum(f => f.Bytes);
    public int VerifiedFiles => Files.Count(f => f.Status == RestoreFileStatus.Verified);
    public int ExitCode => Files.Any(f => f.Status == RestoreFileStatus.Cancelled) ? 130 :
        Files.Any(f => f.Status == RestoreFileStatus.Failed) ? 1 :
        Diagnostics.Count > 0 || Files.Count == 0 || Files.Any(f => f.Status != RestoreFileStatus.Verified) ? 2 : 0;
}
