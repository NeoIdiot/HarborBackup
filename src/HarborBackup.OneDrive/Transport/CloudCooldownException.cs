namespace HarborBackup.OneDrive.Transport;

// The deadline is enough to schedule a later attempt. Keep credentials, request
// paths and preauthenticated upload URLs out of the exception and its inner chain.
public sealed class CloudCooldownException(DateTimeOffset retryNotBeforeUtc, long waitedMilliseconds = 0)
    : HttpRequestException("OneDrive retry is waiting for the service cooldown.")
{
    public DateTimeOffset RetryNotBeforeUtc { get; } = retryNotBeforeUtc;
    public long WaitedMilliseconds { get; } = waitedMilliseconds;
}
