using HarborBackup.Windows.Integration;
using Microsoft.Win32;

namespace HarborBackup.Windows.Integration;

public sealed class LoginStartup(RuntimePaths paths)
{
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private string Command
    {
        get
        {
            static string Quote(string value)
            {
                if (value.Contains('"')) throw new ArgumentException("Invalid startup path.");
                // Windows command-line parsing consumes backslashes before the closing quote in pairs.
                var trailingSeparators = value.Length - value.TrimEnd('\\').Length;
                return '"' + value + new string('\\', trailingSeparators) + '"';
            }
            var executable = Path.Combine(AppContext.BaseDirectory, "HarborBackup.Desktop.exe");
            if (!File.Exists(executable)) throw new FileNotFoundException("Tray executable was not found.", executable);
            return Quote(executable) + " --background --config " + Quote(paths.ProjectConfig) + " --system-config " + Quote(paths.SystemConfig);
        }
    }
    public bool IsEnabled()
    {
        using var key = Registry.CurrentUser.OpenSubKey(RunKey);
        return string.Equals(key?.GetValue("HarborBackup") as string, Command, StringComparison.OrdinalIgnoreCase);
    }
    public void SetEnabled(bool enabled)
    {
        using var key = Registry.CurrentUser.CreateSubKey(RunKey);
        if (enabled) key.SetValue("HarborBackup", Command, RegistryValueKind.String);
        else key.DeleteValue("HarborBackup", throwOnMissingValue: false);
    }
}
