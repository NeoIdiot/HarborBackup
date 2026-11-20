using System.Security.Cryptography;
using HarborBackup.Core.Domain;
using HarborBackup.OneDrive.Transport;
using HarborBackup.Windows.FileSystem;
using HarborBackup.Windows.Persistence;
using HarborBackup.Windows.Persistence.Configuration;

namespace HarborBackup.Windows.Integration;

// Explicit content inspection is independent of the metadata-only comparison path.
public static class CloudConflictExport
{
    public static async Task<string> ExportAsync(ConfigurationSnapshot config, CloudConflictInspection expected,
        string choice, ICloudDrive drive, string outputRoot, CancellationToken token)
    {
        CloudRestoreService.ValidateOutputRoot(config, outputRoot);
        var project = CloudConflictResolution.InspectionProject(config, expected);
        var actual = await CloudConflictResolution.InspectAsync(config, project.ProjectId, expected.Path, drive, token);
        if (actual.Confirmation != expected.Confirmation) throw new IOException("Versions changed after confirmation. Inspect again.");
        var relative = PathRules.Relative(actual.Path);
        CloudRevision? selected = null;
        if (choice != "local") selected = actual.Versions.SingleOrDefault(v => v.Id == choice)
            ?? throw new IOException("Selected version is no longer a conflict head.");
        var version = choice == "local" ? actual.Local ?? throw new IOException("No local version to export.") : selected!.Version;
        if (selected is { ContentHashKnown: false }) throw new IOException("Selected version has no trusted SHA-256 evidence.");
        var exportRoot = Path.Combine(outputRoot, "conflict-" + Guid.NewGuid().ToString("N"));
        var target = Path.Combine(exportRoot, relative.Replace('/', Path.DirectorySeparatorChar));
        SafePath.Validate(target, outputRoot);
        Directory.CreateDirectory(Path.GetDirectoryName(target)!);
        SafePath.Validate(target, outputRoot);
        var temporary = target + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            await using (var output = new FileStream(temporary, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None,
                81920, FileOptions.Asynchronous | FileOptions.SequentialScan))
            {
                if (choice == "local")
                {
                    var source = Path.Combine(project.Source, relative.Replace('/', Path.DirectorySeparatorChar));
                    SafePath.Validate(source, project.Source);
                    await using var input = new FileStream(source, FileMode.Open, FileAccess.Read, FileShare.Read,
                        81920, FileOptions.Asynchronous | FileOptions.SequentialScan);
                    await input.CopyToAsync(output, token);
                }
                else
                {
                    var item = await drive.GetAsync(selected!.ItemId, token);
                    if (item.Folder || item.ETag != selected.ETag || item.Size != version.Length)
                        throw new IOException("Cloud version changed before export.");
                    await drive.DownloadAsync(item, output, token);
                }
                output.Position = 0;
                var hash = Convert.ToHexStringLower(await SHA256.HashDataAsync(output, token));
                if (output.Length != version.Length || hash != version.Sha256)
                    throw new IOException("Exported file failed SHA-256 verification.");
                output.Flush(true);
            }
            token.ThrowIfCancellationRequested();
            CloudRestoreService.ValidateOutputRoot(config, outputRoot);
            SafePath.Validate(target, outputRoot);
            File.Move(temporary, target, false);
            File.SetLastWriteTimeUtc(target, version.LastWriteTimeUtc.UtcDateTime);
            return target;
        }
        finally
        {
            if (File.Exists(temporary)) { SafePath.Validate(temporary, outputRoot); File.Delete(temporary); }
        }
    }
}
