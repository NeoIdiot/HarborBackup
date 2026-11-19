using HarborBackup.Core.Domain;
using HarborBackup.OneDrive.Transport;
using HarborBackup.Windows.Persistence;

namespace HarborBackup.Windows.Integration;

internal static class ConfigurationImpactReader
{
    private static bool SameRules(ProjectDefinition a, ProjectDefinition b) =>
        a.ExcludedDirectories.ToHashSet(StringComparer.OrdinalIgnoreCase).SetEquals(b.ExcludedDirectories) &&
        a.ExcludedFiles.ToHashSet(StringComparer.OrdinalIgnoreCase).SetEquals(b.ExcludedFiles);

    public static async Task<PreviewReport> ReadAsync(ConfigurationSnapshot? original, ConfigurationSnapshot candidate,
        PreviewReport report, SyncDirection direction, Func<ICloudDrive>? driveFactory, CancellationToken token,
        IProgress<BackupProgress>? progress)
    {
        var projects = candidate.Projects.Where(p => report.Projects.Any(r => r.ProjectId == p.ProjectId)).ToArray();
        var previous = original?.Projects.Where(p => projects.Any(n => CloudMapping.SameLocation(p, n) && !SameRules(p, n))).ToArray() ?? [];
        var before = previous.Length == 0 ? null : await new CloudBackupRuntime(original! with { Projects = previous }, driveFactory)
            .PreviewAsync(direction, null, false, token, progress);
        var impacts = projects.Select(project =>
        {
            var old = original?.Projects.FirstOrDefault(p => p.ProjectId == project.ProjectId);
            var mappingChanged = old is null || !CloudMapping.SameLocation(old, project);
            var rulesChanged = old is not null && !SameRules(old, project);
            var prior = before?.Projects.SingleOrDefault(p => p.ProjectId == project.ProjectId);
            var current = report.Projects.Single(p => p.ProjectId == project.ProjectId);
            var comparable = !mappingChanged && rulesChanged && prior is not null && prior.ExitCode != 1 && current.ExitCode != 1;
            int? Removed() => comparable ? prior!.Plans.Select(p => p.Path).Except(current.Plans.Select(p => p.Path), StringComparer.OrdinalIgnoreCase).Count() : null;
            int? Added() => comparable ? current.Plans.Select(p => p.Path).Except(prior!.Plans.Select(p => p.Path), StringComparer.OrdinalIgnoreCase).Count() : null;
            return new ProjectConfigurationImpact(project.ProjectId, project.Destination, mappingChanged, rulesChanged,
                rulesChanged ? Removed() : mappingChanged ? null : 0, rulesChanged ? Added() : mappingChanged ? null : 0);
        }).ToArray();
        return report with { ConfigurationImpact = impacts };
    }
}
