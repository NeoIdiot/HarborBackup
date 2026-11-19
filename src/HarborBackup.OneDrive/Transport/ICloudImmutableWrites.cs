namespace HarborBackup.OneDrive.Transport;

// A conflict returns the existing item for mandatory caller content verification. Never overwrites.
public interface ICloudImmutableWrites
{
    Task<RemoteItem> UploadImmutableAsync(string parent, string name, Stream source, CancellationToken token);
}
