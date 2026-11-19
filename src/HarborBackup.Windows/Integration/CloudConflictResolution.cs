using HarborBackup.Core.Domain;
using HarborBackup.OneDrive.Transport;
using HarborBackup.Windows.Persistence;
using HarborBackup.Windows.Persistence.Configuration;

namespace HarborBackup.Windows.Integration;

public sealed record CloudConflictInspection(string Destination, string Path, FileVersion? Local,
    IReadOnlyList<CloudRevision> Versions, string Confirmation);

public static class CloudConflictResolution
{
    public static async Task<CloudConflictInspection> InspectAsync(ConfigurationSnapshot config, string destination, string path,
        ICloudDrive drive, CancellationToken token)
    {
        var project = BackupRuntime.Select(config, destination).Single();
        var recovery = CloudMapping.ScanPending(config);
        if (recovery.Problems.FirstOrDefault(p => p.Affects(project, path)) is { } problem)
            throw new IOException(problem.Description);
        if (recovery.Files.Any(p => p.Affects(project, path)))
            throw new IOException("此文件涉及未完成操作，请先恢复该文件；其他文件仍可处理。");
        var session = new CloudBackupSession(project, config, config.CloudAccount!, drive);
        var read = await session.ReadAsync(true, token);
        var file = read.Snapshot.Files.Single(f => f.RelativePath.Equals(path, StringComparison.OrdinalIgnoreCase));
        var heads = read.Heads.GetValueOrDefault(file.RelativePath) ?? [];
        if (file.Source.Kind is not (ObservationKind.File or ObservationKind.Missing) || heads.Any(h => h.Directory))
            throw new IOException("File/directory conflicts require manual review.");
        var confirmation = DeviceIdentity.Digest(CloudMapping.Bind(project, config) + "/" + config.CloudAccount!.AccountId + "/" + config.CloudAccount.RepositoryId + "/" + config.CloudAccount.RootId + "/" +
            project.Source + "/" + project.Fingerprint + "/" + file.RelativePath + "/" + file.Source.Version?.Sha256 + "/" +
            string.Join("/", heads.Select(h => h.Id + ":" + h.ETag).Order()));
        return new(destination, file.RelativePath, file.Source.Version, heads, confirmation);
    }
    public static async Task ResolveAsync(ConfigurationSnapshot config, CloudConflictInspection expected, string choice,
        ICloudDrive drive, CancellationToken token)
    {
        var actual = await InspectAsync(config, expected.Destination, expected.Path, drive, token);
        if (actual.Confirmation != expected.Confirmation) throw new IOException("Versions changed after confirmation. Inspect again.");
        var project = BackupRuntime.Select(config, expected.Destination).Single();
        var session = new CloudBackupSession(project, config, config.CloudAccount!, drive);
        var repository = new CloudRepository(drive, config.CloudAccount!, DataPaths.FromState(config.Preview.StateDirectory));
        await session.ReadAsync(false, token);
        var local = actual.Local is null ? FileObservation.Missing : FileObservation.Present(actual.Local, true);
        if (choice == "local")
        {
            if (actual.Local is null) throw new IOException("No local version to keep.");
            await session.ApplyAsync(new(actual.Path, local, FileObservation.Missing, null), actual.Versions.ToArray(),
                SyncDirection.Backup, new(FileAction.Copy, true, FileSide.Target, FileSide.Both, ProtectionKind.Conflict, "ExplicitCloudResolution"), token);
        }
        else
        {
            var selected = actual.Versions.SingleOrDefault(v => v.Id == choice) ?? throw new IOException("Selected version is no longer a conflict head.");
            foreach (var version in actual.Versions) await repository.ProtectAsync(project, version, ProtectionKind.Conflict, token);
            await session.ApplyAsync(new(actual.Path, local, FileObservation.Present(selected.Version, true), null), [selected],
                SyncDirection.Pull, new(FileAction.Copy, true, FileSide.Source, FileSide.Both, ProtectionKind.Conflict, "ExplicitCloudResolution"), token);
        }
    }
}
