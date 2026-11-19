namespace HarborBackup.Core.Domain;

public enum ObservationKind { Missing, File, Directory, Unavailable, Unstable, Unsafe }
public enum SyncDirection { Backup, Pull }

public sealed record FileVersion
{
    public string Sha256 { get; }
    public long Length { get; }
    public DateTimeOffset LastWriteTimeUtc { get; }
    public DateTimeOffset HashedAtUtc { get; }

    public FileVersion(string sha256, long length, DateTimeOffset lastWriteTimeUtc, DateTimeOffset hashedAtUtc)
    {
        if (!IsHash(sha256)) throw new ArgumentException("Expected a lowercase SHA-256 digest.", nameof(sha256));
        ArgumentOutOfRangeException.ThrowIfNegative(length);
        Sha256 = sha256;
        Length = length;
        LastWriteTimeUtc = lastWriteTimeUtc.ToUniversalTime();
        HashedAtUtc = hashedAtUtc.ToUniversalTime();
    }

    public static bool IsHash(string? value) => value is { Length: 64 } &&
        value.All(c => c is >= '0' and <= '9' or >= 'a' and <= 'f');
}

public sealed record FileObservation
{
    public ObservationKind Kind { get; }
    public FileVersion? Version { get; }
    public bool Verified { get; }
    public string? Error { get; }

    private FileObservation(ObservationKind kind, FileVersion? version, bool verified, string? error)
        => (Kind, Version, Verified, Error) = (kind, version, verified, error);

    public static FileObservation Missing { get; } = new(ObservationKind.Missing, null, false, null);
    public static FileObservation Directory { get; } = new(ObservationKind.Directory, null, false, null);
    public static FileObservation Present(FileVersion version, bool verified) =>
        new(ObservationKind.File, version ?? throw new ArgumentNullException(nameof(version)), verified, null);
    public static FileObservation Problem(ObservationKind kind, string error) => kind switch
    {
        ObservationKind.Unavailable or ObservationKind.Unstable or ObservationKind.Unsafe => new(kind, null, false, error),
        _ => throw new ArgumentOutOfRangeException(nameof(kind))
    };
}

public sealed record FileBaseline(FileVersion? Source, FileVersion? Target, string? LastCommonHash)
{
    public bool IsConsistent => LastCommonHash is null ||
        (FileVersion.IsHash(LastCommonHash) && Source?.Sha256 == LastCommonHash && Target?.Sha256 == LastCommonHash);
}
