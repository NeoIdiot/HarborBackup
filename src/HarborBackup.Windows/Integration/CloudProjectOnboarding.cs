using HarborBackup.Core.Domain;

namespace HarborBackup.Windows.Integration;

public sealed record CloudProjectOnboarding(CloudProjectTarget? ExistingProject, string? FolderId, bool HasContents)
{
    public static async Task<CloudProjectOnboarding> InspectAsync(DataPaths data,
        Persistence.Configuration.CloudAccountSettings account, ProjectDefinition project,
        OneDrive.Transport.ICloudDrive drive, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        var repository = new CloudRepository(drive, account, data);
        await repository.ValidateAsync(token);
        var target = await repository.DirectFolderAsync(project.Destination, false, token);
        if (project.CloudFolderId is { } expected && target?.Id != expected)
            throw new IOException("Cloud target changed. Browse and select the folder again.");
        if (target is null) return new(null, null, false);
        await repository.ValidateTargetAsync(target, token);
        var catalog = await CloudProjectCatalog.ReadAsync(data, account, drive, token);
        var registered = catalog.Where(p => p.FolderId == target.Id).ToArray();
        if (registered.Length > 1)
            throw new IOException("该云端目录登记了多个项目，请从“加入云端项目”中明确选择原项目。");
        return new(registered.SingleOrDefault(), target.Id, (await drive.ChildrenAsync(target.Id, token)).Count > 0);
    }
}
