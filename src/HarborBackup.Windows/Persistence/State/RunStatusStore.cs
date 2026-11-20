using System.Text.Json;
using System.Text.Json.Nodes;
using HarborBackup.Core.Domain;
using HarborBackup.Windows.FileSystem;
using HarborBackup.Windows.Integration;

namespace HarborBackup.Windows.Persistence;

public static class RunStatusStore
{
    public static string PathFor(PreviewSettings settings) => Path.Combine(settings.StateDirectory, "devices", settings.DeviceId, "last-run.json");
    public static JsonNode? Read(PreviewSettings settings)
    {
        var content = AtomicJson.ReadText(PathFor(settings));
        if (content is null) return null;
        return Parse(content);
    }

    private static JsonObject Empty() => new()
    {
        ["schemaVersion"] = 3, ["cloudUploadStatus"] = "Unverified", ["projects"] = new JsonArray(),
        ["lastSuccessfulBackupAt"] = null, ["lastRun"] = null
    };

    private static JsonObject Parse(string content)
    {
        using var document = JsonInput.Parse(content);
        var schema = JsonInput.Get(document.RootElement, "schemaVersion").GetInt32();
        if (schema != 3)
            throw new FormatException("Unsupported status schema; original record preserved.");
        var root = JsonNode.Parse(content, new JsonNodeOptions { PropertyNameCaseInsensitive = true })!.AsObject();
        if (root["projects"] is not JsonArray projects) throw new FormatException("Invalid status project list.");
        foreach (var key in new[] { "cloudUploadStatus", "lastSuccessfulBackupAt", "lastRun" })
            if (!root.ContainsKey(key)) throw new FormatException($"Missing status property: {key}");
        foreach (var item in projects)
        {
            if (item is not JsonObject project || !FileVersion.IsHash(project["projectId"]?.GetValue<string>() ?? "") || string.IsNullOrWhiteSpace(project["destination"]?.GetValue<string>()) ||
                project["source"] is null || project["target"] is null || project["consecutiveFailures"]?.GetValue<int>() is not >= 0)
                throw new FormatException("Invalid project status record.");
            foreach (var key in new[] { "lastAttemptAt", "lastResult", "lastSuccessfulBackupAt", "lastFailureAt", "lastFailureError",
                "lastError", "durationSeconds", "cloudUploadStatus" })
                if (!project.ContainsKey(key)) throw new FormatException($"Missing project status property: {key}");
        }
        return root;
    }

    public static void Write(ConfigurationSnapshot config, BackupReport report)
    {
        var path = PathFor(config.Preview);
        var original = AtomicJson.ReadText(path);
        var root = original is null ? Empty() : Parse(original);
        root["cloudUploadStatus"] = report.CloudUploadStatus;
        root["lastRun"] = JsonSerializer.SerializeToNode(report, AtomicJson.Options);
        if (report.ExitCode == 0 && report.Mode != "Recover") root["lastSuccessfulBackupAt"] = report.CompletedAtUtc;
        var projects = root["projects"]!.AsArray();
        foreach (var run in report.Projects)
        {
            var mapping = config.Projects.FirstOrDefault(p => p.ProjectId == run.ProjectId);
            if (mapping is null) continue;
            var record = projects.OfType<JsonObject>().FirstOrDefault(p => p["projectId"]?.GetValue<string>() == mapping.ProjectId);
            if (record is null)
            {
                record = new JsonObject { ["projectId"] = mapping.ProjectId, ["source"] = mapping.Source, ["destination"] = mapping.Destination,
                    ["target"] = mapping.Target, ["consecutiveFailures"] = 0,
                    ["lastSuccessfulBackupAt"] = null, ["lastFailureAt"] = null, ["lastFailureError"] = null };
                projects.Add(record);
            }
            if (report.Mode != "Recover" && record["healthBinding"]?.GetValue<string>() != CloudMapping.Bind(mapping, config))
            {
                record["lastSuccessfulBackupAt"] = null; record["lastSuccessfulTransferAt"] = null;
                record["lastFailureAt"] = null; record["lastFailureError"] = null; record["consecutiveFailures"] = 0;
            }
            record["source"] = mapping.Source; record["destination"] = mapping.Destination; record["target"] = mapping.Target;
            record["lastAttemptAt"] = report.StartedAtUtc;
            record["lastCompletedRunAt"] = report.CompletedAtUtc;
            record["lastResult"] = report.Cancelled ? "Cancelled" : report.Completion == "BudgetLimited" ? "BudgetLimited" : CloudRunStatus.Result(run);
            record["recovery"] = report.Cancelled ? RecoveryDisposition.Stopped.ToString() : CloudRunStatus.Recovery(run).ToString();
            record["retryNotBeforeUtc"] = run.RetryNotBeforeUtc;
            record["lastError"] = string.Join("; ", run.Issues.Select(i => i.Message).Concat(run.Files.Where(f => f.Error is not null).Select(f => f.Error)));
            record["durationSeconds"] = Math.Round((report.CompletedAtUtc - report.StartedAtUtc).TotalSeconds, 2);
            record["cloudUploadStatus"] = report.CloudUploadStatus;
            if (run.ExitCode == 1 && !CloudRunStatus.IsAutomaticRetryOnly(run))
            {
                record["consecutiveFailures"] = record["consecutiveFailures"]!.GetValue<int>() + 1;
                record["lastFailureAt"] = report.CompletedAtUtc;
                record["lastFailureError"] = record["lastError"]!.DeepClone();
            }
            else record["consecutiveFailures"] = 0;
            if (run.ExitCode == 0 && !report.Cancelled && report.Mode != "Recover") record["lastSuccessfulBackupAt"] = report.CompletedAtUtc;
            if (run.Files.Any(f => f.Changed && f.Action == FileAction.Copy))
            {
                record["lastSuccessfulTransferAt"] = report.CompletedAtUtc;
            }
            record["syncStats"] = new JsonObject { ["Copied"] = run.Files.Count(f => f.Action == FileAction.Copy && f.Changed),
                ["Conflicts"] = run.Files.Count(f => f.Action is FileAction.ConflictUnresolved or FileAction.TypeConflict),
                ["NeedsPull"] = run.Files.Count(f => f.Action == FileAction.NeedsPull), ["NeedsPush"] = run.Files.Count(f => f.Action == FileAction.NeedsPush),
                ["Deferred"] = run.Files.Count(f => f.Action == FileAction.Deferred), ["Hashed"] = run.Metrics.HashedFiles, ["Cached"] = run.Metrics.CachedFiles };
            ProjectHealthStore.Update(config, mapping, run, report, record);
        }
        AtomicJson.Write(path, root, AtomicJson.Signature(original));
    }

    public static void Log(ConfigurationSnapshot config, BackupReport report)
    {
        SafePath.Validate(config.LogDirectory);
        Directory.CreateDirectory(config.LogDirectory);
        var now = report.CompletedAtUtc.ToLocalTime();
        var summary = Path.Combine(config.LogDirectory, $"{now:yyyyMMdd}-{Environment.MachineName}.txt");
        var details = config.HistoryDirectory;
        SafePath.Validate(summary, config.LogDirectory);
        SafePath.Validate(details);
        Directory.CreateDirectory(details);
        var detail = Path.Combine(details, $"{now:yyyyMMdd-HHmmssfff}-{Guid.NewGuid():N}.json");
        File.WriteAllText(detail, JsonSerializer.Serialize(report, AtomicJson.Options));
        File.AppendAllText(summary, $"{now:O} | {report.Mode} | exit={report.ExitCode} | projects={report.Projects.Count} | cloud={report.CloudUploadStatus} | {Path.GetFileName(detail)}{Environment.NewLine}");
        Prune(config.LogDirectory, "*.txt", config.LogRetentionDays);
        Prune(config.HistoryDirectory, "*.json", config.HistoryRetentionDays);
    }
    private static void Prune(string directory, string pattern, int days)
    {
        var cutoff = DateTime.UtcNow.AddDays(-days);
        foreach (var file in Directory.EnumerateFiles(directory, pattern))
        {
            SafePath.Validate(file, directory);
            if (File.GetLastWriteTimeUtc(file) < cutoff) File.Delete(file);
        }
    }}
