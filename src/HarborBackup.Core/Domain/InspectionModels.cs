namespace HarborBackup.Core.Domain;

public enum VerificationCategory { Unverifiable, Match, Different, MissingLocal, MissingCloud, Directory, Absent, TypeConflict }
public sealed record VerificationSummary(int Match, int Different, int MissingLocal, int MissingCloud,
    int Unverifiable, int Directories, int Absent, int TypeConflicts)
{
    public static VerificationSummary From(IEnumerable<PreviewPlan> plans)
    {
        var counts = plans.GroupBy(p => p.Verification).ToDictionary(g => g.Key, g => g.Count());
        int N(VerificationCategory category) => counts.GetValueOrDefault(category);
        return new(N(VerificationCategory.Match), N(VerificationCategory.Different), N(VerificationCategory.MissingLocal),
            N(VerificationCategory.MissingCloud), N(VerificationCategory.Unverifiable), N(VerificationCategory.Directory),
            N(VerificationCategory.Absent), N(VerificationCategory.TypeConflict));
    }
    public static VerificationCategory Classify(FileObservation local, FileObservation cloud, bool cloudHashKnown)
    {
        if (local.Kind is ObservationKind.Unavailable or ObservationKind.Unstable or ObservationKind.Unsafe ||
            cloud.Kind is ObservationKind.Unavailable or ObservationKind.Unstable or ObservationKind.Unsafe) return VerificationCategory.Unverifiable;
        if (local.Kind == ObservationKind.Missing && cloud.Kind == ObservationKind.Missing) return VerificationCategory.Absent;
        if (local.Kind == ObservationKind.Missing) return VerificationCategory.MissingLocal;
        if (cloud.Kind == ObservationKind.Missing) return VerificationCategory.MissingCloud;
        if (local.Kind != cloud.Kind) return VerificationCategory.TypeConflict;
        if (local.Kind == ObservationKind.Directory) return VerificationCategory.Directory;
        if (!cloudHashKnown) return VerificationCategory.Unverifiable;
        return local.Version!.Sha256 == cloud.Version!.Sha256 ? VerificationCategory.Match : VerificationCategory.Different;
    }
}
public sealed record PlanSummary(int Transfers, int CreatesDirectories, int Protected, int NeedsAttention,
    long TransferBytes, long ExcludedLocal, long ExcludedCloud)
{
    public static PlanSummary From(IReadOnlyList<ProjectPreview> projects) => new(
        projects.Sum(p => p.Plans.Count(f => f.Decision.Action == FileAction.Copy)),
        projects.Sum(p => p.Plans.Count(f => f.Decision.Action == FileAction.CreateDirectory)),
        projects.Sum(p => p.Plans.Count(f => f.Decision.Protection != ProtectionKind.None)),
        projects.Sum(p => p.Plans.Count(f => f.Decision.NeedsAttention) + p.Issues.Count),
        projects.Sum(p => p.Plans.Where(f => f.Decision.Action == FileAction.Copy).Sum(f => f.TransferBytes)),
        projects.Sum(p => p.Metrics.ExcludedLocalEntries), projects.Sum(p => p.Metrics.ExcludedCloudEntries));
}
public sealed record ProjectConfigurationImpact(string ProjectId, string Destination, bool MappingChanged,
    bool RulesChanged, int? NewlyExcluded, int? NewlyIncluded);
public sealed record AutomaticBackupBudget(long MaxBytes = 0, int MaxDurationMinutes = 0);
public enum FileFailureKind { None, FileUnavailable, Network, Authorization, RecoveryRequired, Budget, Cancelled }
