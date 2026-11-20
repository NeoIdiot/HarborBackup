using HarborBackup.Core.Domain;

namespace HarborBackup.Windows.Integration;

internal sealed class AutomaticRunBudget(AutomaticBackupBudget limits, TimeProvider clock)
{
    private readonly long started = clock.GetTimestamp();
    private readonly object gate = new();
    private long reserved;
    private long previousMilliseconds;
    public long ReservedBytes { get { lock (gate) return reserved; } }
    public long ElapsedMilliseconds
    {
        get { lock (gate) { var elapsed = (long)clock.GetElapsedTime(started).TotalMilliseconds;
            return elapsed > long.MaxValue - previousMilliseconds ? long.MaxValue : previousMilliseconds + elapsed; } }
    }
    public void Restore(long bytes, long activeMilliseconds)
    {
        lock (gate)
        {
            reserved = bytes > long.MaxValue - reserved ? long.MaxValue : reserved + bytes;
            previousMilliseconds = activeMilliseconds > long.MaxValue - previousMilliseconds ? long.MaxValue : previousMilliseconds + activeMilliseconds;
        }
    }
    public string? StopReason { get; private set; }
    public bool TryStart(long bytes, bool writes)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(bytes);
        lock (gate)
        {
            if (StopReason is not null) return false;
            if (limits.MaxDurationMinutes > 0 && clock.GetElapsedTime(started).TotalMilliseconds + previousMilliseconds >= TimeSpan.FromMinutes(limits.MaxDurationMinutes).TotalMilliseconds)
                StopReason = "AutomaticTimeBudgetReached";
            else if (writes && limits.MaxBytes > 0 && reserved >= limits.MaxBytes)
                StopReason = "AutomaticTransferBudgetReached";
            if (StopReason is not null) return false;
            if (writes) reserved = bytes > long.MaxValue - reserved ? long.MaxValue : reserved + bytes;
            return true;
        }
    }
}
