using HarborBackup.Core.Domain;

namespace HarborBackup.Desktop.Presentation;

// Producers only replace one value; the UI timer samples it instead of queuing one message per file.
internal sealed class ProgressMailbox : IProgress<BackupProgress>
{
    private BackupProgress? latest;
    internal BackupProgress? Latest => Volatile.Read(ref latest);
    public void Report(BackupProgress value) => Volatile.Write(ref latest, value);
    internal BackupProgress? Take() => Interlocked.Exchange(ref latest, null);
    internal void Reset() => Volatile.Write(ref latest, null);
}
