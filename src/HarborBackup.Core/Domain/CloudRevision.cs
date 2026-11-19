namespace HarborBackup.Core.Domain;

// Version evidence supports three-way decisions. The direct cloud tree is observed independently; listings are never a lock.
public sealed record CloudRevision(int SchemaVersion, string Id, string Project, string Path,
    string Device, string[] Parents, string ItemId, string ETag, FileVersion Version, DateTimeOffset CreatedUtc,
    bool Directory = false, bool ContentHashKnown = true);

public static class CloudRevisions
{
    public static FileDecision Decide(SyncDirection direction, FileObservation local,
        IReadOnlyList<CloudRevision> heads, string? commonHash)
    {
        if (heads.Count > 1 && heads.Select(h => (h.Version.Sha256, h.Directory)).Distinct().Count() > 1)
            return new(FileAction.ConflictUnresolved, true, FileSide.None, FileSide.Both, ProtectionKind.Conflict, "ConcurrentCloudVersions");
        var remote = heads.FirstOrDefault();
        if (remote is { Directory: false, ContentHashKnown: false })
            return new(FileAction.Deferred, true, FileSide.None, FileSide.None, ProtectionKind.None, "CloudMetadataRequired");
        var observation = remote is null ? FileObservation.Missing : remote.Directory ? FileObservation.Directory :
            FileObservation.Present(remote.Version, false);
        var baseline = commonHash is null ? null : new FileBaseline(
            new(commonHash, 0, DateTimeOffset.UnixEpoch, DateTimeOffset.UnixEpoch),
            new(commonHash, 0, DateTimeOffset.UnixEpoch, DateTimeOffset.UnixEpoch), commonHash);
        return ConflictDecision.Resolve(direction, local, observation, baseline);
    }
}
