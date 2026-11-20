using HarborBackup.Core.Domain;
using HarborBackup.OneDrive.Transport;

namespace HarborBackup.Windows.Integration;

// Bounded by the runtime's in-flight files. The handle stays locked through decision and upload.
internal sealed class PreparedUpload(PreviewFile file, FileStream? stream = null, TransferHashes? hashes = null, Action? release = null) : IAsyncDisposable
{
    private int disposed;
    public PreviewFile File { get; } = file;
    public FileStream? Stream { get; } = stream;
    public TransferHashes? Hashes { get; } = hashes;
    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref disposed, 1) != 0) return;
        release?.Invoke();
        if (Stream is not null) await Stream.DisposeAsync();
    }
}
