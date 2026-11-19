using HarborBackup.Core.Domain;
using HarborBackup.OneDrive.Transport;
using HarborBackup.Windows.FileSystem;

namespace HarborBackup.Windows.Integration;

internal sealed record CloudProjectLocation(int SchemaVersion, string ProjectId, string DriveId, string FolderId,
    string Destination, string MappingId, long Generation);

// Location revisions never change the project's identity or overwrite recovery evidence.
internal sealed class CloudProjectRegistry(CloudRepository repository, ICloudDrive drive, string driveId,
    IEnumerable<ProjectDefinition>? previous = null)
{
    private static string MappingId(ProjectDefinition project) => project.BindingVersion == 0
        ? DeviceIdentity.Digest(project.TaskId + "/" + project.MappingVersion + "/" + project.Fingerprint)
        : DeviceIdentity.Digest("location-v2/" + System.Text.Json.JsonSerializer.Serialize(new
            { project.TaskId, project.MappingVersion, Destination = project.Destination.ToUpperInvariant(), project.CloudFolderId }));

    public async Task<IReadOnlyList<CloudProjectLocation>> ReadAsync(string projectId, CancellationToken token)
    {
        if (!FileVersion.IsHash(projectId)) throw new IOException("Invalid project ID.");
        var folder = await repository.FolderAsync("metadata/projects/" + projectId + "/locations", false, token);
        var result = new List<CloudProjectLocation>();
        if (folder is null) return result;
        foreach (var item in await drive.ChildrenAsync(folder.Id, token))
        {
            var value = await repository.ReadJsonAsync<CloudProjectLocation>(item, token);
            if (value.SchemaVersion != 3 || value.ProjectId != projectId || value.DriveId != driveId ||
                string.IsNullOrWhiteSpace(value.FolderId) || value.Generation <= 0 || !FileVersion.IsHash(value.MappingId) ||
                item.Name != value.MappingId + ".json" || PathRules.Relative(value.Destination, true) != value.Destination)
                throw new IOException("Cloud project identity mismatch.");
            result.Add(value);
        }
        return result;
    }

    internal static CloudProjectLocation? Latest(IReadOnlyList<CloudProjectLocation> locations)
    {
        if (locations.Count == 0) return null;
        var generation = locations.Max(p => p.Generation);
        var heads = locations.Where(p => p.Generation == generation).ToArray();
        if (heads.Select(p => p.FolderId).Distinct(StringComparer.Ordinal).Skip(1).Any())
            throw new IOException("Cloud project has conflicting latest folder registrations. Review its cloud folders before continuing.");
        return heads.OrderBy(p => p.MappingId, StringComparer.Ordinal).Last();
    }

    public async Task ValidateAsync(ProjectDefinition project, RemoteItem? target, CancellationToken token)
    {
        var locations = await ReadAsync(project.ProjectId, token);
        Validate(project, target, locations);
    }
    private void Validate(ProjectDefinition project, RemoteItem? target, IReadOnlyList<CloudProjectLocation> locations)
    {
        var latest = Latest(locations);
        var identities = (previous ?? []).Append(project).Select(MappingId).ToHashSet(StringComparer.Ordinal);
        var own = locations.Where(p => identities.Contains(p.MappingId)).ToArray();
        if (own.Length > 0 && (own.Any(p => p.FolderId != target?.Id) || latest!.FolderId != target?.Id) ||
            own.Length == 0 && project.MappingVersion is null && latest is not null && latest.FolderId != target?.Id)
            throw new IOException("Cloud target identity changed. Re-select and review the project before writing.");
    }

    public async Task RegisterAsync(ProjectDefinition project, RemoteItem target, CancellationToken token)
    {
        var locations = await ReadAsync(project.ProjectId, token);
        Validate(project, target, locations);
        var latest = Latest(locations);
        if (locations.Any(p => p.MappingId == MappingId(project)))
            return;
        var folder = (await repository.FolderAsync("metadata/projects/" + project.ProjectId + "/locations", true, token))!;
        var location = new CloudProjectLocation(3, project.ProjectId, driveId, target.Id, project.Destination,
            MappingId(project), checked((latest?.Generation ?? 0) + 1));
        await repository.JsonAsync(folder.Id, location.MappingId + ".json", location, token);
        await ValidateAsync(project, target, token);
    }
}
