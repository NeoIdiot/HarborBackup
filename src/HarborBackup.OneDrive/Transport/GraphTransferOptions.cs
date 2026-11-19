namespace HarborBackup.OneDrive.Transport;

public sealed record GraphTransferOptions(int UploadChunkBytes = 10 * 1024 * 1024,
    long UploadBytesPerSecond = 0, long DownloadBytesPerSecond = 0)
{
    internal int Validate()
    {
        if (UploadChunkBytes < 320 * 1024 || UploadChunkBytes > 20 * 1024 * 1024 || UploadChunkBytes % (320 * 1024) != 0)
            throw new ArgumentOutOfRangeException(nameof(UploadChunkBytes));
        if (UploadBytesPerSecond is < 0 or > 1024L * 1024 * 1024)
            throw new ArgumentOutOfRangeException(nameof(UploadBytesPerSecond));
        if (DownloadBytesPerSecond is < 0 or > 1024L * 1024 * 1024)
            throw new ArgumentOutOfRangeException(nameof(DownloadBytesPerSecond));
        return UploadChunkBytes;
    }
}
