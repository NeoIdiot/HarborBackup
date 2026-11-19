namespace HarborBackup.OneDrive.Transport;

public sealed record CloudNetworkMetrics(long Requests, long Retries, long RetryWaitMilliseconds, long ThrottledRequests = 0);
public interface ICloudDiagnostics { CloudNetworkMetrics NetworkMetrics { get; } }
