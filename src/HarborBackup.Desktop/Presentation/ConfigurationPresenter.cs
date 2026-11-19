using HarborBackup.Core.Domain;
using HarborBackup.Desktop.Controllers;
using HarborBackup.Windows.Persistence;
using HarborBackup.Windows.Persistence.Configuration;

namespace HarborBackup.Desktop.Presentation;

internal interface IConfigurationPresenter
{
    Task<HarborBackup.Windows.Integration.CloudProjectOnboarding> InspectNewProjectAsync(ProjectDefinition project, CancellationToken token);
    Task<HarborBackup.Windows.Integration.CloudFolderListing> BrowseCloudAsync(string? folderId);
    Task<ConfigurationDraft> LoadAsync();
    Task<ConfigurationSnapshot> ValidateAsync(ConfigurationDraft draft);
    Task<PreviewReport> PreviewAsync(ConfigurationDraft draft, string? destination = null,
        CancellationToken cancellationToken = default, IProgress<BackupProgress>? progress = null);
    Task<bool> SaveAsync(ConfigurationDraft draft, ConfigurationPart part);
}

internal sealed class ConfigurationPresenter(DesktopController controller) : IConfigurationPresenter
{
    public Task<HarborBackup.Windows.Integration.CloudProjectOnboarding> InspectNewProjectAsync(ProjectDefinition project, CancellationToken token) =>
        controller.ExclusiveAsync(cancellation =>
        {
            var store = new HarborBackup.Windows.Persistence.Configuration.CloudAccountStore(controller.Paths.Data);
            var account = store.Read();
            if (!account.Enabled || account.RequiresAuthorization)
                throw new InvalidOperationException("Connect OneDrive and select a cloud repository in settings.");
            using var drive = store.Open(account);
            return HarborBackup.Windows.Integration.CloudProjectOnboarding.InspectAsync(controller.Paths.Data, account, project, drive, cancellation).GetAwaiter().GetResult();
        }, token);
    public Task<HarborBackup.Windows.Integration.CloudFolderListing> BrowseCloudAsync(string? folderId)
    {
        var session = new HarborBackup.Windows.Integration.CloudAccountSession(controller.Paths.Data);
        return session.BrowseAsync(session.Read(), folderId, CancellationToken.None);
    }
    public Task<ConfigurationDraft> LoadAsync() => Task.Run(() => controller.Editor.Load());
    public Task<ConfigurationSnapshot> ValidateAsync(ConfigurationDraft draft) => Task.Run(() => controller.Editor.Validate(draft));
    public Task<PreviewReport> PreviewAsync(ConfigurationDraft draft, string? destination = null,
        CancellationToken cancellationToken = default, IProgress<BackupProgress>? progress = null) =>
        controller.ExclusiveAsync(token => controller.Editor.Preview(draft, destination, token: token, progress: progress), cancellationToken);
    public Task<bool> SaveAsync(ConfigurationDraft draft, ConfigurationPart part) => controller.ExclusiveAsync(() =>
    { controller.Editor.Save(draft, part); return true; });
}
