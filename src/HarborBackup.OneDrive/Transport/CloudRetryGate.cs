using System.Net.Http.Headers;

namespace HarborBackup.OneDrive.Transport;

// Shared by concurrent requests in one drive session. A throttle pauses subsequent requests to that host.
internal sealed class CloudRetryGate(TimeProvider? timeProvider = null, Func<TimeSpan, CancellationToken, Task>? wait = null)
{
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
    internal async Task<long> WaitAsync(string host, CancellationToken token)
    {
        var started = clock.GetUtcNow();
        while (true)
        {
            token.ThrowIfCancellationRequested();
            TimeSpan remaining;
            lock (sync) remaining = deadlines.GetValueOrDefault(host) - clock.GetUtcNow();
            if (remaining <= TimeSpan.Zero) return Math.Max(0, (long)(clock.GetUtcNow() - started).TotalMilliseconds);
            var slice = remaining > TimeSpan.FromMinutes(1) ? TimeSpan.FromMinutes(1) : remaining;
            if (wait is not null) await wait(slice, token); else await Task.Delay(slice, clock, token);
        }
    }
}
