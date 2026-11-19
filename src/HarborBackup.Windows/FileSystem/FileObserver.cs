using System.ComponentModel;
using System.Security.Cryptography;
using HarborBackup.Core.Domain;

namespace HarborBackup.Windows.FileSystem;

public sealed class FileObserver(TimeProvider? timeProvider = null)
{
    private readonly TimeProvider clock = timeProvider ?? TimeProvider.System;

    public async Task<FileObservation> ObserveAsync(string path, FileVersion? cached, HashPolicy policy,
        bool force, ReadMetrics metrics, CancellationToken cancellationToken = default, bool deferHash = false)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var observed = false;
        try
        {
            SafePath.Validate(path);
            var attributes = File.GetAttributes(path);
            observed = true;
            if ((attributes & FileAttributes.Directory) != 0) return FileObservation.Directory;
            var before = new FileInfo(path);
            var length = before.Length;
            var modified = new DateTimeOffset(before.LastWriteTimeUtc);
            var now = clock.GetUtcNow();
            if (modified > now.AddMinutes(5)) return FileObservation.Problem(ObservationKind.Unstable, "FutureModificationTime");
            if (!force && policy.UseMetadataCache && cached is not null && cached.Length == length &&
                cached.LastWriteTimeUtc == modified && cached.HashedAtUtc <= now &&
                now - cached.HashedAtUtc < TimeSpan.FromDays(policy.FullCheckIntervalDays))
            {
                metrics.CachedFiles++;
                return FileObservation.Present(cached, false);
            }
            if (deferHash) return FileObservation.Problem(ObservationKind.Unavailable, "ContentReadPending");
            await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read,
                131072, FileOptions.Asynchronous | FileOptions.SequentialScan);
            var hash = Convert.ToHexStringLower(await SHA256.HashDataAsync(stream, cancellationToken));
            var after = new FileInfo(path);
            if (length != after.Length || modified.UtcDateTime != after.LastWriteTimeUtc)
                return FileObservation.Problem(ObservationKind.Unstable, "ChangedDuringHashing");
            metrics.HashedFiles++;
            metrics.HashBytes += length;
            return FileObservation.Present(new(hash, length, modified, clock.GetUtcNow()), true);
        }
        catch (FileNotFoundException) { return observed ? FileObservation.Problem(ObservationKind.Unstable, "DisappearedDuringRead") : FileObservation.Missing; }
        catch (DirectoryNotFoundException) { return observed ? FileObservation.Problem(ObservationKind.Unstable, "DisappearedDuringRead") : FileObservation.Missing; }
        catch (UnsafePathException ex) { return FileObservation.Problem(ObservationKind.Unsafe, ex.Message); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or Win32Exception)
        {
            return FileObservation.Problem(ObservationKind.Unavailable, ex.Message);
        }
    }
}
