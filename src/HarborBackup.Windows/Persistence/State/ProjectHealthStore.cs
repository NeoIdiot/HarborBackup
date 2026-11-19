using System.Text.Json;
using System.Text.Json.Nodes;
using HarborBackup.Core.Domain;
using HarborBackup.Windows.FileSystem;
using HarborBackup.Windows.Integration;
using HarborBackup.Windows.Persistence.Configuration;

namespace HarborBackup.Windows.Persistence;

// Display evidence only. These summaries never authorize transfer, recovery or cleanup.
public sealed record ProjectHealthSnapshot(string Binding, DateTimeOffset? LastFullSuccessUtc = null,
    DateTimeOffset? LastRestoreVerifiedUtc = null, int KnownBacklogCount = 0,
    DateTimeOffset? OldestPendingUtc = null, bool PendingCountIncomplete = false,
    string? RestoreScope = null, int RestoreVerifiedFiles = 0)
{
    public bool NeedsAttention(DateTimeOffset now) =>
        OldestPendingUtc is { } pending && now - pending >= TimeSpan.FromHours(24) ||
        LastFullSuccessUtc is { } success && now - success >= TimeSpan.FromHours(24);
}

public static class ProjectHealthStore
{
    private static DateTimeOffset? Stamp(JsonNode? value, DateTimeOffset now) => value is null ? null :
        DateTimeOffset.TryParse(value.GetValue<string>(), out var parsed) && parsed <= now ? parsed :
        throw new FormatException("Invalid project health timestamp.");

    public static ProjectHealthSnapshot Read(ConfigurationSnapshot config, ProjectDefinition project, JsonNode? status,
        DateTimeOffset? utcNow = null)
    {
        var now = utcNow ?? DateTimeOffset.UtcNow;
        var binding = CloudMapping.Bind(project, config);
        var record = status?["projects"]?.AsArray().FirstOrDefault(p => p?["projectId"]?.GetValue<string>() == project.ProjectId);
        var same = record?["healthBinding"]?.GetValue<string>() == binding;
        var success = same ? Stamp(record?["lastFullSuccessAt"], now) : null;
        var pending = same ? Stamp(record?["pendingSince"], now) : null;
        var paths = same ? record?["knownBacklog"]?.AsObject() : null;
        if (paths is not null)
            foreach (var item in paths)
            {
                PathRules.Relative(item.Key);
                var first = Stamp(item.Value, now) ?? throw new FormatException("Missing backlog timestamp.");
                if (pending is null || first < pending) pending = first;
            }
        var restore = ReadRestoreVerification(config, project, binding, now);
        return new(binding, success, restore?.VerifiedUtc, paths?.Count ?? 0,
            pending, same && record?["pendingCountIncomplete"]?.GetValue<bool>() == true,
            restore?.Scope.ToString(), restore?.VerifiedFiles ?? 0);
    }

    internal static void Update(ConfigurationSnapshot config, ProjectDefinition project, ProjectRun run,
        BackupReport report, JsonObject record)
    {
        // Recovery may describe a retired mapping and must not relabel current-mapping display evidence.
        if (report.Mode == "Recover") return;
        var binding = CloudMapping.Bind(project, config);
        if (record["healthBinding"]?.GetValue<string>() != binding)
        {
            record["healthBinding"] = binding;
            record["lastFullSuccessAt"] = null;
            record["knownBacklog"] = new JsonObject();
            record["pendingSince"] = null;
            record["pendingCountIncomplete"] = false;
        }
        if (record["knownBacklog"] is null) record["knownBacklog"] = new JsonObject();
        var backlog = record["knownBacklog"]!.AsObject();
        var now = report.CompletedAtUtc;
        foreach (var file in run.Files.GroupBy(f => f.Path, StringComparer.OrdinalIgnoreCase).Select(g => g.Last()))
        {
            var existing = backlog.Select(p => p.Key).FirstOrDefault(p => p.Equals(file.Path, StringComparison.OrdinalIgnoreCase));
            if (!file.NeedsAttention()) { if (existing is not null) backlog.Remove(existing); }
            else if (existing is null) backlog[file.Path] = now;
        }
        if (run.CoverageComplete == true && run.ExitCode == 0 && !report.Cancelled)
        {
            backlog.Clear(); record["pendingSince"] = null;
            record["pendingCountIncomplete"] = false;
            record["lastFullSuccessAt"] = now;
        }
        else
        {
            // Unstarted and unselected files are absent from FileResults: this is a lower bound.
            record["pendingCountIncomplete"] = true;
            record["pendingSince"] ??= JsonValue.Create(now);
        }
    }

    private static RestoreVerificationState? ReadRestoreVerification(ConfigurationSnapshot config, ProjectDefinition project,
        string binding, DateTimeOffset now)
    {
        var path = CloudRestoreService.VerificationPath(config, project);
        var content = AtomicJson.ReadText(path);
        if (content is null) return null;
        var value = JsonSerializer.Deserialize<RestoreVerificationState>(content, AtomicJson.Options)
            ?? throw new FormatException("Invalid restore verification record.");
        var account = config.CloudAccount!;
        if (value.SchemaVersion != 1 || value.Binding != binding || value.ProjectId != project.ProjectId ||
            value.DeviceId != config.Preview.DeviceId || value.AccountId != account.AccountId ||
            value.DriveId != account.DriveId || value.RepositoryId != account.RepositoryId || value.RootId != account.RootId ||
            value.Scope is not (RestoreScope.Current or RestoreScope.Archive) ||
            value.VerifiedFiles <= 0 || value.VerifiedBytes < 0 || value.VerifiedUtc > now || value.VerifiedUtc == default)
            throw new FormatException("Restore verification identity mismatch.");
        return value;
    }
}
