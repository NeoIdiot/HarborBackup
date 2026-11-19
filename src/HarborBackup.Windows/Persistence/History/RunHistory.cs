using System.Text.Json;
using HarborBackup.Core.Domain;
using HarborBackup.Windows.FileSystem;

namespace HarborBackup.Windows.Persistence;

public sealed record HistoryRecord(string Path, DateTimeOffset Time, string Mode, int ExitCode, BackupReport? Report, string? Error = null)
{
    public IReadOnlyList<string> Projects => Report?.Projects.Select(p => p.Destination)
        .Where(p => !string.IsNullOrWhiteSpace(p)).Distinct(StringComparer.OrdinalIgnoreCase).ToArray() ?? [];
    public string ProjectNames => string.Join("、", Projects);
}

public static class RunHistory
{
    public static IReadOnlyList<HistoryRecord> Read(string historyDirectory, int limit = 100)
    {
        var directory = historyDirectory;
        SafePath.Validate(directory);
        if (!Directory.Exists(directory)) return [];
        var result = new List<HistoryRecord>();
        foreach (var path in Directory.EnumerateFiles(directory, "*.json").OrderDescending(StringComparer.Ordinal).Take(limit))
        {
            try
            {
                SafePath.Validate(path, directory);
                if (new FileInfo(path).Length > 8 * 1024 * 1024) throw new IOException("记录超过 8 MiB，请通过日志目录查看完整文件。");
                using var document = JsonInput.Read(path);
                var report = JsonSerializer.Deserialize<BackupReport>(document.RootElement.GetRawText(), AtomicJson.Options) ?? throw new FormatException("Invalid run report.");
                if (string.IsNullOrWhiteSpace(report.Mode) || report.Diagnostics is null || report.Diagnostics.Any(d => d is null) ||
                    report.Projects is null || report.Projects.Any(p => p is null || p.Files is null ||
                    p.Issues is null || p.Metrics is null || p.Files.Any(f => f is null) || p.Issues.Any(i => i is null)))
                    throw new FormatException("运行记录结构不完整。");
                result.Add(new(path, report.CompletedAtUtc, report.Mode, report.ExitCode, report));
            }
            catch (Exception ex) when (ex is IOException or JsonException or FormatException or InvalidOperationException or UnauthorizedAccessException)
            { result.Add(new(path, DateTimeOffset.MinValue, "无法读取", 1, null, ex.Message)); }
        }
        return result;
    }
}
