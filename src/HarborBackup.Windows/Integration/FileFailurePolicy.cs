using System.Net;
using HarborBackup.Core.Domain;
using HarborBackup.OneDrive.Authentication;
using HarborBackup.OneDrive.Transport;
using HarborBackup.Windows.FileSystem;

namespace HarborBackup.Windows.Integration;

internal static class FileFailurePolicy
{
    public static FileFailureKind Classify(Exception error, bool preparingLocal)
    {
        if (error is DeferredOperationException) return FileFailureKind.None;
        if (error is AccountAuthorizationRequiredException || error is CloudRequestException
            { StatusCode: HttpStatusCode.Unauthorized }) return FileFailureKind.Authorization;
        // A quota or access denial needs an environmental fix, not another network retry.
        if (error is CloudRequestException permanent &&
            (permanent.StatusCode is HttpStatusCode.Forbidden or HttpStatusCode.InsufficientStorage ||
                permanent.ErrorCode.Equals("quotaLimitReached", StringComparison.OrdinalIgnoreCase) ||
                permanent.ErrorCode.Equals("insufficientStorage", StringComparison.OrdinalIgnoreCase)))
            return FileFailureKind.RecoveryRequired;
        if (error is CloudTransportException or CloudOfflineException or HttpRequestException ||
            error is CloudRequestException cloud && ((int)cloud.StatusCode >= 500 || cloud.StatusCode is HttpStatusCode.TooManyRequests or HttpStatusCode.RequestTimeout))
            return FileFailureKind.Network;
        if (error is UnsafePathException or CloudRequestException) return FileFailureKind.RecoveryRequired;
        // Only the local-read phase proves there have been no business/state writes.
        // The same native exception during commit may concern credentials or recovery state.
        if (preparingLocal && (error is IOException or UnauthorizedAccessException)) return FileFailureKind.FileUnavailable;
        return FileFailureKind.RecoveryRequired;
    }

    public static RecoveryDisposition Recovery(FileFailureKind kind, Exception error) => kind switch
    {
        FileFailureKind.Network => RecoveryDisposition.AutomaticRetry,
        FileFailureKind.Authorization or FileFailureKind.FileUnavailable => RecoveryDisposition.RepairRequired,
        FileFailureKind.Cancelled => RecoveryDisposition.Stopped,
        _ => error is DeferredOperationException ? RecoveryDisposition.AwaitingConfirmation : RecoveryDisposition.RepairRequired
    };
}
