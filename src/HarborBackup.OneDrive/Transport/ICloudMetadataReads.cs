namespace HarborBackup.OneDrive.Transport;

public interface ICloudMetadataReads
{
    // Complete, version-checked JSON bytes only; a partial batch never authorizes a transfer.
    Task<IReadOnlyDictionary<string, byte[]>> ReadMetadataAsync(IReadOnlyList<RemoteItem> items, CancellationToken token);
}
