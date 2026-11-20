using System.Net.Http.Headers;

namespace HarborBackup.OneDrive.Transport;

// Production drive instances share host deadlines across runs. Tests may inject
// an isolated gate and clock; supplying a fake handler never shares production state.
internal sealed class CloudRetryGate(TimeProvider? timeProvider = null, Func<TimeSpan, CancellationToken, Task>? wait = null)
{
    internal static CloudRetryGate Shared { get; } = new();
    private static readonly TimeSpan YieldThreshold = TimeSpan.FromSeconds(5);
    private readonly TimeProvider clock = timeProvider ?? TimeProvider.System;
    private readonly object sync = new();
    private readonly Dictionary<string, DateTimeOffset> deadlines = new(StringComparer.OrdinalIgnoreCase);

    internal TimeSpan Defer(string host, RetryConditionHeaderValue? header, int attempt)
    {
        var now = clock.GetUtcNow();
        var delay = header?.Delta ?? (header?.Date is { } date ? date - now :
            TimeSpan.FromSeconds(Math.Min(60, Math.Pow(2, attempt + 1)) + Random.Shared.NextDouble()));
        if (delay < TimeSpan.Zero) delay = TimeSpan.Zero;
        var due = delay > DateTimeOffset.MaxValue - now ? DateTimeOffset.MaxValue : now + delay;
        lock (sync)
            if (!deadlines.TryGetValue(host, out var current) || due > current) deadlines[host] = due;
        return delay;
    }
    internal async Task<long> WaitAsync(string host, CancellationToken token, bool yieldLongWaits = false)
    {
        var started = clock.GetUtcNow();
        while (true)
        {
            token.ThrowIfCancellationRequested();
            TimeSpan remaining;
            DateTimeOffset deadline;
            lock (sync) deadline = deadlines.GetValueOrDefault(host);
            remaining = deadline - clock.GetUtcNow();
            if (remaining <= TimeSpan.Zero) return Math.Max(0, (long)(clock.GetUtcNow() - started).TotalMilliseconds);
            var elapsed = clock.GetUtcNow() - started;
            if (yieldLongWaits && (remaining > YieldThreshold || elapsed >= YieldThreshold))
                throw new CloudCooldownException(deadline, Math.Max(0, (long)elapsed.TotalMilliseconds));
            var slice = remaining > TimeSpan.FromMinutes(1) ? TimeSpan.FromMinutes(1) : remaining;
            if (yieldLongWaits && slice > YieldThreshold - elapsed) slice = YieldThreshold - elapsed;
            if (wait is not null) await wait(slice, token); else await Task.Delay(slice, clock, token);
        }
    }
    internal DateTimeOffset? NotBefore(string host)
    {
        lock (sync) return deadlines.TryGetValue(host, out var due) ? due : null;
    }
    internal DateTimeOffset? NotBefore()
    {
        lock (sync)
        {
            var now = clock.GetUtcNow();
            return deadlines.Values.Where(due => due > now).Select(due => (DateTimeOffset?)due).Max();
        }
    }
}
