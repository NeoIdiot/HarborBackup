namespace HarborBackup.Core.Application;

public sealed record RecoveryRetryKey(string ProjectId, string Binding);
public sealed record RecoveryRetryCandidate(RecoveryRetryKey Key, DateTimeOffset? NotBeforeUtc = null);

/// <summary>One pending retry per physical project binding; waiting never holds an execution lease.</summary>
public sealed class RecoveryRetrySchedule(TimeProvider? timeProvider = null, Func<double>? jitter = null)
{
    private readonly TimeProvider clock = timeProvider ?? TimeProvider.System;
    private readonly Dictionary<RecoveryRetryKey, Entry> entries = [];
    private sealed class Entry(long origin, TimeSpan delay, DateTimeOffset? notBefore)
    {
        internal long Origin = origin;
        internal TimeSpan Delay = delay;
        internal DateTimeOffset? NotBefore = notBefore;
        internal int Failures = 1;
        internal bool Attempting;
    }

    public int Count => entries.Count;
    public DateTimeOffset? NextDueUtc => entries.Values.Where(e => !e.Attempting)
        .Select(e => (DateTimeOffset?)(clock.GetUtcNow() + Remaining(e))).Min();

    public void Synchronize(IEnumerable<RecoveryRetryCandidate> candidates)
    {
        var active = candidates.GroupBy(c => c.Key).ToDictionary(g => g.Key,
            g => g.Select(c => c.NotBeforeUtc).Max());
        foreach (var key in entries.Keys.Where(k => !active.ContainsKey(k)).ToArray()) entries.Remove(key);
        foreach (var (key, notBefore) in active)
        {
            if (!entries.TryGetValue(key, out var entry)) entries[key] = new(clock.GetTimestamp(), Backoff(1), notBefore);
            else if (notBefore > entry.NotBefore || entry.NotBefore is null) entry.NotBefore = notBefore;
        }
    }

    public void Failed(RecoveryRetryKey key, DateTimeOffset? notBefore = null)
    {
        if (!entries.TryGetValue(key, out var entry)) entries[key] = new(clock.GetTimestamp(), Backoff(1), notBefore);
        else
        {
            entry.Failures++;
            entry.Origin = clock.GetTimestamp();
            entry.Delay = Backoff(entry.Failures);
            entry.Attempting = false;
            if (notBefore > entry.NotBefore || entry.NotBefore is null) entry.NotBefore = notBefore;
        }
    }

    // A connectivity edge is a hint to check sooner, not permission to bypass a
    // provider's Retry-After deadline. Repeated online ticks do not call this.
    public void Connected()
    {
        foreach (var entry in entries.Values.Where(e => !e.Attempting))
        {
            var remaining = entry.Delay - clock.GetElapsedTime(entry.Origin);
            if (remaining <= TimeSpan.FromSeconds(2)) continue;
            entry.Origin = clock.GetTimestamp();
            entry.Delay = TimeSpan.FromSeconds(2);
        }
    }

    public bool TryGetDue(out RecoveryRetryKey? key)
    {
        key = entries.Where(p => !p.Value.Attempting && Remaining(p.Value) <= TimeSpan.Zero)
            .OrderBy(p => p.Key.ProjectId, StringComparer.Ordinal).ThenBy(p => p.Key.Binding, StringComparer.Ordinal)
            .Select(p => p.Key).FirstOrDefault();
        return key is not null;
    }

    public bool Accepted(RecoveryRetryKey key)
    {
        if (!entries.TryGetValue(key, out var entry) || entry.Attempting || Remaining(entry) > TimeSpan.Zero) return false;
        entry.Attempting = true;
        return true;
    }

    // The execution host can fail before producing a run report (for example,
    // another host owns the run lock). Only release a still-authorized existing
    // attempt after the caller has synchronized its durable candidates again.
    public bool ReleaseFailedAttempt(RecoveryRetryKey key, DateTimeOffset? notBefore = null)
    {
        if (!entries.TryGetValue(key, out var entry) || !entry.Attempting) return false;
        Failed(key, notBefore);
        return true;
    }

    public void Remove(RecoveryRetryKey key) => entries.Remove(key);
    public void Clear() => entries.Clear();

    private TimeSpan Backoff(int failures)
    {
        var seconds = 15d * Math.Pow(2, Math.Min(8, failures - 1));
        var factor = .8 + .4 * Math.Clamp(jitter?.Invoke() ?? Random.Shared.NextDouble(), 0, 1);
        return TimeSpan.FromSeconds(Math.Min(300, seconds * factor));
    }

    private TimeSpan Remaining(Entry entry)
    {
        var backoff = entry.Delay - clock.GetElapsedTime(entry.Origin);
        var provider = entry.NotBefore is { } due ? due - clock.GetUtcNow() : TimeSpan.Zero;
        return backoff > provider ? backoff : provider;
    }
}
