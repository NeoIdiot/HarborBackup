using HarborBackup.Core.Domain;
using HarborBackup.Windows.FileSystem;
using HarborBackup.Windows.Persistence;

namespace HarborBackup.Windows.Integration;

public sealed partial class CloudBackupSession
{
    // Called only after the original journal identity, mapping and parent have been validated.
    // An unpublished upload can be retired when the business tree is still unchanged. Any
    // temporary upload/session remains described by the exact retained journal, never deleted.
    private async Task<bool> RetainChangedUploadAsync(string path, string text, CloudJournal j, CancellationToken token)
    {
        if (j.Direction != SyncDirection.Backup || !j.Publish || j.Local is null ||
            j.Phase is not ("Prepared" or "Uploading") || j.Uploaded is not null || j.Archived is not null ||
            j.ArchiveParent is not null || j.ArchiveName is not null || j.PreviousHistory is not null ||
            j.Incoming is not null || j.Committed is not null) return false;
        var local = await new FileObserver().ObserveAsync(LocalPath(j.Path), null, new(), true, new(), token);
        if (local.Kind is not (ObservationKind.File or ObservationKind.Missing)) return false;
        if (local.Version?.Sha256 == j.Local.Sha256 && local.Version.Length == j.Local.Length) return false;
        var current = await drive.FindAsync(j.Parent, Path.GetFileName(j.Path), token);
        // A changed live target must still go through ordinary reconciliation, never be dismissed.
        try { RequireExpected(j.Expected, current); }
        catch (IOException) { return false; }
        token.ThrowIfCancellationRequested();
        var retained = Path.Combine(StateRoot, "retained-transactions", j.Id + ".json");
        SafePath.Validate(retained, config.Preview.StateDirectory);
        Directory.CreateDirectory(Path.GetDirectoryName(retained)!);
        if (AtomicJson.ReadText(path) != text) throw new IOException("Recovery record changed; evidence retained.");
        File.Move(path, retained); // Create-only move. Keep bytes, upload sessions and cloud stages intact.
        return true; // No baseline, cloud write or local business write is committed by retirement.
    }
}
