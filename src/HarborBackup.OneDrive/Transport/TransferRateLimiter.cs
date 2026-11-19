using System.Net;

namespace HarborBackup.OneDrive.Transport;

// One limiter per direction and drive session. Concurrent streams share the same byte budget;
// a cancelled wait does not reserve future capacity for bytes that were never transferred.
internal sealed class TransferRateLimiter
{
    private readonly long bytesPerSecond;
    private readonly TimeProvider clock;
    private readonly Func<TimeSpan, CancellationToken, Task>? wait;
    private readonly SemaphoreSlim gate = new(1, 1);
    internal bool Enabled => bytesPerSecond != 0;
    internal int ChunkBytes => Enabled ? (int)Math.Clamp(bytesPerSecond / 10, 1, 64 * 1024) : 64 * 1024;

    internal TransferRateLimiter(long bytesPerSecond, TimeProvider? timeProvider = null,
        Func<TimeSpan, CancellationToken, Task>? wait = null)
    {
        if (bytesPerSecond < 0) throw new ArgumentOutOfRangeException(nameof(bytesPerSecond));
        this.bytesPerSecond = bytesPerSecond;
        clock = timeProvider ?? TimeProvider.System;
        this.wait = wait;
    }

    internal async Task WaitAsync(int bytes, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        if (bytes < 0 || bytes > ChunkBytes) throw new ArgumentOutOfRangeException(nameof(bytes));
        if (!Enabled || bytes == 0) return;
        await gate.WaitAsync(token);
        try
        {
            var started = clock.GetTimestamp();
            var duration = TimeSpan.FromSeconds(bytes / (double)bytesPerSecond);
            while (duration - clock.GetElapsedTime(started) is { } remaining && remaining > TimeSpan.Zero)
            {
                token.ThrowIfCancellationRequested();
                var slice = remaining > TimeSpan.FromMilliseconds(100) ? TimeSpan.FromMilliseconds(100) : remaining;
                if (wait is not null) await wait(slice, token); else await Task.Delay(slice, clock, token);
            }
        }
        finally { gate.Release(); }
    }
}

// Retains the original length/Content-Range contract while pacing the actual HTTP writes.
internal sealed class RateLimitedByteContent(byte[] bytes, TransferRateLimiter limiter,
    CancellationTokenSource requestDeadline) : HttpContent
{
    internal static readonly TimeSpan NetworkTimeout = TimeSpan.FromMinutes(3);
    protected override bool TryComputeLength(out long length) { length = bytes.Length; return true; }
    protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context) =>
        SerializeToStreamAsync(stream, context, requestDeadline.Token);
    protected override async Task SerializeToStreamAsync(Stream stream, TransportContext? context, CancellationToken token)
    {
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(token, requestDeadline.Token);
        for (var offset = 0; offset < bytes.Length;)
        {
            var count = Math.Min(limiter.ChunkBytes, bytes.Length - offset);
            // Deliberate pacing (including waiting for other streams' permits) is not a stalled
            // network operation. Caller cancellation remains active throughout this wait.
            requestDeadline.CancelAfter(Timeout.InfiniteTimeSpan);
            try { await limiter.WaitAsync(count, stop.Token); }
            finally { requestDeadline.CancelAfter(NetworkTimeout); }
            await stream.WriteAsync(bytes.AsMemory(offset, count), stop.Token);
            offset += count;
        }
    }
}
