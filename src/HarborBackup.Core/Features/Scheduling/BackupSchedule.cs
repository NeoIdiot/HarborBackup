namespace HarborBackup.Core.Application;

/// <summary>Monotonic interval scheduling: one due trigger after sleep, never catch-up queues.</summary>
public sealed class BackupSchedule
{
    private readonly TimeProvider clock;
    private long origin;
    private TimeSpan delay;
    public TimeSpan Interval { get; private set; }
    public BackupSchedule(TimeSpan interval, TimeSpan startDelay, TimeProvider? clock = null)
    {
        this.clock = clock ?? TimeProvider.System;
        if (interval <= TimeSpan.Zero || startDelay < TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(interval));
        Interval = interval;
        delay = startDelay;
        origin = this.clock.GetTimestamp();
    }
    public DateTimeOffset NextDueUtc => clock.GetUtcNow() + Remaining;
    public bool IsDue => clock.GetElapsedTime(origin) >= delay;
    private TimeSpan Remaining => TimeSpan.FromTicks(Math.Max(0, (delay - clock.GetElapsedTime(origin)).Ticks));
    public bool TakeDueTrigger()
    {
        return AcceptDue();
    }
    // Call only after an execution has actually been accepted. Offline, paused or
    // busy callers may inspect IsDue without consuming their one merged trigger.
    public bool AcceptDue()
    {
        if (!IsDue) return false;
        origin = clock.GetTimestamp();
        delay = Interval;
        return true;
    }
    public void Reset(TimeSpan interval)
    {
        if (interval <= TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(interval));
        Interval = delay = interval;
        origin = clock.GetTimestamp();
    }
}
