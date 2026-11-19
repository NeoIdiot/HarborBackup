using HarborBackup.Core.Domain;

namespace HarborBackup.Windows.Persistence.Configuration;

// A device-local list preference, never an instruction to delete cloud content.
public sealed record DismissedCloudProject(string DriveId, string RepositoryId, string RootId, string ProjectId)
{
    public bool Matches(CloudAccountSettings account, string projectId) =>
        DriveId == account.DriveId && RepositoryId == account.RepositoryId && RootId == account.RootId &&
        ProjectId == projectId;

    internal void Validate()
    {
        if (string.IsNullOrWhiteSpace(DriveId) || string.IsNullOrWhiteSpace(RepositoryId) || string.IsNullOrWhiteSpace(RootId) ||
            !FileVersion.IsHash(ProjectId))
            throw new FormatException("Invalid dismissed cloud project.");
    }
}
