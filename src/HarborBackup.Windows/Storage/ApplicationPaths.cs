using HarborBackup.Windows.FileSystem;
namespace HarborBackup.Windows.Integration;

public sealed record DataPaths(string Root)
{
    public string Config => Path.Combine(Root, "config");
    public string Projects => Path.Combine(Config, "projects.json");
    public string BackupSettings => Path.Combine(Config, "backup-settings.json");
    public string DesktopSettings => Path.Combine(Config, "desktop-settings.json");
    public string State => Path.Combine(Root, "state");
    public string History => Path.Combine(Root, "history");
    public string Logs => Path.Combine(Root, "logs");
    public string Protection => Path.Combine(Root, "protection");
    public string Archives => Path.Combine(Protection, "archives");
    public string Conflicts => Path.Combine(Protection, "conflicts");
    public string Cache => Path.Combine(Root, "cache");
    public string Hashes => Path.Combine(Cache, "hashes");
    public string Temp => Path.Combine(Root, "temp");
    public static DataPaths FromConfig(string path) => new(Path.GetDirectoryName(Path.GetDirectoryName(PathRules.Absolute(path)))!);
    public static DataPaths FromState(string path) => new(Path.GetDirectoryName(PathRules.Absolute(path))!);
}
public static class ApplicationPaths
{
    public static string InstallRoot(string? applicationDirectory = null)
    {
        var app = Path.TrimEndingDirectorySeparator(PathRules.Absolute(applicationDirectory ?? AppContext.BaseDirectory));
        return Path.GetFileName(app).Equals("app", StringComparison.OrdinalIgnoreCase) ? Path.GetDirectoryName(app)! : app;
    }
    public static RuntimePaths Resolve(string? project = null, string? system = null,
        string? applicationDirectory = null, string? dataDirectory = null)
    {
        var data = new DataPaths(PathRules.Absolute(dataDirectory ?? Path.Combine(InstallRoot(applicationDirectory), "data")));
        system ??= project is null ? data.BackupSettings : Path.Combine(Path.GetDirectoryName(PathRules.Absolute(project))!, "backup-settings.json");
        project ??= Path.Combine(Path.GetDirectoryName(PathRules.Absolute(system))!, "projects.json");
        if (!Path.GetDirectoryName(PathRules.Absolute(project))!.Equals(Path.GetDirectoryName(PathRules.Absolute(system)), StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("Configuration files must share one config directory.");
        return new(PathRules.Absolute(project), PathRules.Absolute(system));
    }
}