namespace HarborBackup.Core.Domain;

/// <summary>Selects a direction without bypassing the caller's validated backup decision.</summary>
public static class AutomaticBackupDecision
{
    public static SyncDirection DirectionFor(FileDecision backupDecision, FileObservation source, FileObservation target)
    {
        ArgumentNullException.ThrowIfNull(backupDecision);
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(target);
        return backupDecision.Action is FileAction.NeedsPull or FileAction.TargetOnly ||
            backupDecision.Action == FileAction.Directory && source.Kind == ObservationKind.Missing && target.Kind == ObservationKind.Directory
                ? SyncDirection.Pull : SyncDirection.Backup;
    }
}
