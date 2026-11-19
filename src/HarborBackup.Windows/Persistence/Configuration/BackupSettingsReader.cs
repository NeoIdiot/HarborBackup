using HarborBackup.Core.Domain;
using static HarborBackup.Windows.Persistence.JsonInput;

namespace HarborBackup.Windows.Persistence.Configuration;

// Pure settings validation: neither account readiness nor source availability is required.
public sealed record BackupSettingsValues(HashPolicy Hash, bool ContinueOnError, int IntervalMinutes,
    int StartDelayMinutes, int ExecutionLimitMinutes, int LogRetentionDays, int HistoryRetentionDays)
{
    public AutomaticBackupBudget AutomaticBudget { get; init; } = new();
    public int UploadKiBPerSecond { get; init; }
    public int DownloadKiBPerSecond { get; init; }
}

public static class BackupSettingsReader
{
    public static BackupSettingsValues Parse(string json)
    {
        using var doc = JsonInput.Parse(json);
        var sys = doc.RootElement;
        if (sys.ValueKind != System.Text.Json.JsonValueKind.Object) throw new FormatException("System configuration must be an object.");
        if (Integer(Get(sys, "schemaVersion"), -1, 1, 1) != 1) throw new FormatException("Unsupported backup settings version.");
        var sync = Get(sys, "sync");
        Fields(sync, "enabled", "conflictPolicy", "preserveConflictCopies", "propagateDeletes", "stateDirectory", "conflictDirectory", "archiveDirectory", "hash", "archiveRetention");
        if (!Boolean(Get(sync, "enabled"), false)) throw new FormatException("Backup settings require sync.enabled=true.");
        if ((Has(Get(sync, "conflictPolicy")) && Text(Get(sync, "conflictPolicy")) != "newer") ||
            !Boolean(Get(sync, "preserveConflictCopies"), true) || Boolean(Get(sync, "propagateDeletes"), false))
            throw new FormatException("Preserved copies and no deletion propagation are required.");
        foreach (var obsolete in new[] { "stateDirectory", "archiveDirectory", "conflictDirectory" })
            if (Has(Get(sync, obsolete))) throw new FormatException("Internal paths are managed by the data layout.");
        var hash = Get(sync, "hash");
        Fields(hash, "algorithm", "useMetadataCache", "fullCheckIntervalDays", "verifyAfterCopy");
        if ((Has(Get(hash, "algorithm")) && Text(Get(hash, "algorithm")) != "SHA256") || !Boolean(Get(hash, "verifyAfterCopy"), true))
            throw new FormatException("SHA256 and post-copy verification are required.");
        var policy = new HashPolicy(Boolean(Get(hash, "useMetadataCache"), true), Integer(Get(hash, "fullCheckIntervalDays"), 7, 1, 365));
        var logging = Get(sys, "logging");
        if (Has(Get(logging, "directory"))) throw new FormatException("Logs use the managed logs directory.");
        var schedule = Get(sys, "schedule");
        var transfer = Get(sys, "transfer");
        if (transfer.ValueKind == System.Text.Json.JsonValueKind.Null) throw new FormatException("Transfer limits must be an object.");
        Fields(transfer, "uploadKiBPerSecond", "downloadKiBPerSecond");
        int Limit(string name)
        {
            var value = Get(transfer, name);
            if (value.ValueKind == System.Text.Json.JsonValueKind.Null) throw new FormatException("Transfer limits must be integers.");
            return Integer(value, 0, 0, 1048576);
        }
        return new(policy, Boolean(Get(sys, "continueOnError"), true),
            Integer(Get(schedule, "intervalMinutes"), 10, 1, 1440),
            Integer(Get(schedule, "startDelayMinutes"), 2, 1, 1440),
            Integer(Get(schedule, "executionTimeLimitMinutes"), 120, 1, 1440),
            Integer(Get(logging, "retentionDays"), 30, 1, 3650),
            Integer(Get(Get(sys, "history"), "retentionDays"), 90, 1, 3650))
        { AutomaticBudget = new((long)Integer(Get(schedule, "automaticMaxTransferMiB"), 0, 0, 1048576) * 1048576,
            Integer(Get(schedule, "automaticMaxDurationMinutes"), 0, 0, 1440)),
          UploadKiBPerSecond = Limit("uploadKiBPerSecond"),
          DownloadKiBPerSecond = Limit("downloadKiBPerSecond") };
    }
}
