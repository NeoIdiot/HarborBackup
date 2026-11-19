using System.Text.Json;
using HarborBackup.Core.Domain;
using HarborBackup.Windows.FileSystem;
using HarborBackup.Windows.Persistence;

namespace HarborBackup.Windows.Integration;

// A failed download with no original local file and no recorded side effects is a
// read intent. A new confirmed pull can supersede it without replaying an old plan.
internal sealed record PreparedPullRestart(string JournalFile, string Signature, string Id)
{
    public static PreparedPullRestart? Read(string path, string text, CloudJournal j)
    {
        if (j.SchemaVersion is not (2 or 4 or 5) || j.Direction != SyncDirection.Pull || !j.Publish ||
            j.Phase != "Prepared" || j.Protection != ProtectionKind.None || j.Local is not null ||
            j.Session is not null || j.Uploaded is not null || j.Archived is not null || j.ArchiveParent is not null ||
            j.ArchiveName is not null || j.PreviousHistory is not null || j.Committed is not null || j.Incoming is not null ||
            j.Remote is not { SchemaVersion: 1, Directory: false } remote || j.Expected is not { Folder: false } expected ||
            remote.Path != j.Path || remote.ItemId != expected.Id || remote.ETag != expected.ETag ||
            remote.Version.Length != expected.Size || expected.Size < 0 || !FileVersion.IsHash(remote.Version.Sha256) ||
            expected.Name != Path.GetFileName(j.Path) || expected.ParentId != j.Parent)
            return null;
        using var document = JsonDocument.Parse(text);
        foreach (var field in document.RootElement.EnumerateObject())
        {
            if (new[] { "staged", "archiveFolder", "previousArchive" }.Contains(field.Name, StringComparer.OrdinalIgnoreCase) &&
                field.Value.ValueKind != JsonValueKind.Null) return null;
            if (field.Name.Equals("previousArchives", StringComparison.OrdinalIgnoreCase) &&
                field.Value.ValueKind != JsonValueKind.Null &&
                (field.Value.ValueKind != JsonValueKind.Array || field.Value.GetArrayLength() != 0)) return null;
        }
        return new(path, AtomicJson.Signature(text)!, j.Id);
    }

    public void Retain(ConfigurationSnapshot config)
    {
        SafePath.Validate(JournalFile, config.Preview.StateDirectory);
        if (AtomicJson.Signature(AtomicJson.ReadText(JournalFile)) != Signature)
            throw new IOException("PullPreviewChanged");
        var retained = Path.Combine(Path.GetDirectoryName(Path.GetDirectoryName(JournalFile))!, "retained-transactions", Id + ".json");
        SafePath.Validate(retained, config.Preview.StateDirectory);
        Directory.CreateDirectory(Path.GetDirectoryName(retained)!);
        File.Move(JournalFile, retained); // Never replace evidence or remove partial download/rollback files.
    }
}
