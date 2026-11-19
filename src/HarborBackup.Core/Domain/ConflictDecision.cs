namespace HarborBackup.Core.Domain;

public enum FileAction
{
    Absent, Equal, Copy, SourceOnly, TargetOnly, NeedsPull, NeedsPush,
    ConflictUnresolved, Deferred, TypeConflict, CreateDirectory, Directory
}
public enum FileSide { None, Source, Target, Both }
public enum ProtectionKind { None, Archive, Conflict }

public sealed record FileDecision(FileAction Action, bool Conflict, FileSide WriteSide,
    FileSide ProtectSide, ProtectionKind Protection, string Reason)
{
    public bool NeedsAttention => Action is FileAction.NeedsPull or FileAction.NeedsPush or
        FileAction.ConflictUnresolved or FileAction.Deferred or FileAction.TypeConflict;
}

public static class ConflictDecision
{
    public static FileDecision Resolve(SyncDirection direction, FileObservation source,
        FileObservation target, FileBaseline? baseline = null)
    {
        if (!Enum.IsDefined(direction)) throw new ArgumentOutOfRangeException(nameof(direction));
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(target);
        if (baseline is { IsConsistent: false }) return None(FileAction.Deferred, "InvalidBaseline");
        if (source.Kind == ObservationKind.Unsafe || target.Kind == ObservationKind.Unsafe)
            return None(FileAction.TypeConflict, "UnsafePath", true);
        if (source.Kind is ObservationKind.Unavailable or ObservationKind.Unstable ||
            target.Kind is ObservationKind.Unavailable or ObservationKind.Unstable)
            return None(FileAction.Deferred, "ObservationUnavailable");
        if (source.Kind == ObservationKind.Missing && target.Kind == ObservationKind.Missing)
            return None(FileAction.Absent, "BothMissing");
        if (source.Kind == ObservationKind.Directory || target.Kind == ObservationKind.Directory)
        {
            if (source.Kind == ObservationKind.File || target.Kind == ObservationKind.File)
                return None(FileAction.TypeConflict, "FileDirectoryCollision", true);
            if (direction == SyncDirection.Backup && target.Kind == ObservationKind.Missing)
                return new(FileAction.CreateDirectory, false, FileSide.Target, FileSide.None, ProtectionKind.None, "SourceDirectoryOnly");
            if (direction == SyncDirection.Pull && source.Kind == ObservationKind.Missing)
                return new(FileAction.CreateDirectory, false, FileSide.Source, FileSide.None, ProtectionKind.None, "TargetDirectoryOnly");
            return None(FileAction.Directory, "PreserveDirectory");
        }
        if (target.Kind == ObservationKind.Missing)
            return direction == SyncDirection.Backup
                ? new(FileAction.Copy, false, FileSide.Target, FileSide.None, ProtectionKind.None, "SourceOnly")
                : None(FileAction.SourceOnly, "DoNotUploadDuringPull");
        if (source.Kind == ObservationKind.Missing)
            return direction == SyncDirection.Pull
                ? new(FileAction.Copy, false, FileSide.Source, FileSide.None, ProtectionKind.None, "TargetOnly")
                : None(FileAction.TargetOnly, "DoNotPullDuringBackup");

        var s = source.Version!;
        var t = target.Version!;
        if (s.Sha256 == t.Sha256) return None(FileAction.Equal, "SameContent");
        if (baseline?.LastCommonHash is not { } common)
            return new(FileAction.ConflictUnresolved, true, FileSide.None, FileSide.Both, ProtectionKind.Conflict, "NoCommonCloudBaseline");
        var sourceChanged = s.Sha256 != common;
        var targetChanged = t.Sha256 != common;
        if (sourceChanged && targetChanged)
            return new(FileAction.ConflictUnresolved, true, FileSide.None, FileSide.Both, ProtectionKind.Conflict, "BothChangedSinceBaseline");
        var mayCopy = direction == SyncDirection.Backup ? sourceChanged : targetChanged;
        var side = direction == SyncDirection.Backup ? FileSide.Target : FileSide.Source;
        return mayCopy
            ? new(FileAction.Copy, false, side, side, ProtectionKind.Archive, "OneSideChangedSinceBaseline")
            : None(direction == SyncDirection.Backup ? FileAction.NeedsPull : FileAction.NeedsPush, "OppositeSideChangedSinceBaseline");
    }

    private static FileDecision None(FileAction action, string reason, bool conflict = false) =>
        new(action, conflict, FileSide.None, FileSide.None, ProtectionKind.None, reason);
}
