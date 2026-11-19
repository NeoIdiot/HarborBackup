namespace HarborBackup.Core.Application;

/// <summary>One operation at a time; rejected triggers are never queued.</summary>
public sealed class RunCoordinator
{
    private readonly object gate = new();
    private readonly CancellationTokenSource shutdown = new();
    private Task? active;
    private CancellationTokenSource? current;
    private bool paused;
    private bool stopping;
    public bool IsBusy { get { lock (gate) return active is { IsCompleted: false }; } }
    public bool Paused { get { lock (gate) return paused; } set { lock (gate) paused = value; } }
    public bool IsStopping { get { lock (gate) return stopping; } }

    public Task? TryStart(Func<CancellationToken, Task> operation, bool automatic = false)
    {
        lock (gate)
        {
            if (stopping || (automatic && paused) || active is { IsCompleted: false }) return null;
            current?.Dispose();
            current = CancellationTokenSource.CreateLinkedTokenSource(shutdown.Token);
            var token = current.Token;
            active = Task.Run(() => operation(token));
            return active;
        }
    }

    public void CancelCurrent()
    {
        lock (gate) { if (active is { IsCompleted: false }) current?.Cancel(); }
    }

    public Task StopAsync()
    {
        lock (gate)
        {
            stopping = true;
            shutdown.Cancel();
            return active ?? Task.CompletedTask;
        }
    }
}
