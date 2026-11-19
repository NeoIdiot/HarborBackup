using System.ComponentModel;
using System.Text.Json;
using HarborBackup.Core.Domain;

namespace HarborBackup.Windows.Persistence;

// Only the display summary/history/logs are best-effort. Baselines and journals never use this path.
internal static class RunReportPersistence
{
    internal static BackupReport Save(BackupReport report, Action<BackupReport> writeStatus, Action<BackupReport> writeHistory)
    {
        BackupReport Attempt(BackupReport current, Action<BackupReport> write, string code)
        {
            try { write(current); return current; }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or
                FormatException or InvalidOperationException or Win32Exception)
            {
                return current with { Diagnostics = [.. current.Diagnostics, new(code, ex.Message, false)] };
            }
        }
        // History is still attempted if status is corrupt, locked or full; the returned result always
        // carries both diagnostics. A failed medium cannot be relied upon to persist its own failure.
        var result = Attempt(report, writeStatus, "RunStatusWriteFailed");
        return Attempt(result, writeHistory, "RunHistoryWriteFailed");
    }
}
