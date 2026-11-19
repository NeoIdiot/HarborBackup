namespace HarborBackup.Windows.Integration;

/// <summary>Acquire and dispose on the same thread. Hosts must not hold this across an await.</summary>
public sealed class RunLock : IDisposable
{
    public const string Name = @"Local\HarborBackup.Run";
    private readonly Mutex mutex = new(false, Name);
    private readonly int ownerThread = Environment.CurrentManagedThreadId;
    private bool held;

    public RunLock()
    {
        try { held = mutex.WaitOne(0); }
        catch (AbandonedMutexException) { held = true; }
        if (!held)
        {
            mutex.Dispose();
            throw new InvalidOperationException("Busy: another backup or preview holds the run lock.");
        }
    }

    public void Dispose()
    {
        if (!held) return;
        if (Environment.CurrentManagedThreadId != ownerThread)
            throw new InvalidOperationException("The run lock must be released on its owning thread.");
        mutex.ReleaseMutex();
        held = false;
        mutex.Dispose();
    }
}
