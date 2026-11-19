using HarborBackup.Windows.FileSystem;
namespace HarborBackup.Windows.Integration;

public static class DataMaintenance
{
    public static long CacheBytes(DataPaths paths)
    {
        SafePath.Validate(paths.Cache, paths.Root);
        if (!Directory.Exists(paths.Cache)) return 0;
        return EnumerateFiles(paths.Cache).Sum(f => new FileInfo(f).Length);
    }
    // Enumerate without following reparse-point directories, even during read-only measurement.
    private static IEnumerable<string> EnumerateFiles(string root)
    {
        SafePath.Validate(root);
        foreach (var file in Directory.EnumerateFiles(root)) { SafePath.Validate(file, root); yield return file; }
        foreach (var directory in Directory.EnumerateDirectories(root))
        { SafePath.Validate(directory, root); foreach (var file in EnumerateFiles(directory)) yield return file; }
    }
    public static long ClearDisposableData(DataPaths paths)
    {
        using var runLock = new RunLock();
        var removed = 0L;
        foreach (var root in new[] { paths.Cache, paths.Temp })
        {
            SafePath.Validate(root, paths.Root);
            if (!Directory.Exists(root)) continue;
            var files = EnumerateFiles(root).ToArray();
            foreach (var file in files)
            {
                SafePath.Validate(file, root);
                // Ordinary temp output is retained for 24 hours; transaction staging never lives here.
                if (root == paths.Temp && File.GetLastWriteTimeUtc(file) > DateTime.UtcNow.AddDays(-1)) continue;
                var size = new FileInfo(file).Length;
                File.Delete(file);
                removed += size;
            }
        }
        return removed;
    }
}