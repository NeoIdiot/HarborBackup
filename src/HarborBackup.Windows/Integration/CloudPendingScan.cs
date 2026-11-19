using HarborBackup.Core.Domain;

namespace HarborBackup.Windows.Integration;

public sealed record CloudRecoveryProblem(CloudMapping? Mapping, string Record, string Message)
{
    public bool Affects(ProjectDefinition project, string? path = null) =>
        Mapping is null || new PendingCloudFile(Mapping, "").Affects(project, path);
    public string Description => Message + Environment.NewLine + Record;
}

public sealed record CloudPendingScan(IReadOnlyList<PendingCloudFile> Files, IReadOnlyList<CloudRecoveryProblem> Problems);
