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
        if (error is AccountAuthorizationRequiredException || error is CloudRequestException
            { StatusCode: HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden }) return FileFailureKind.Authorization;
        if (error is CloudTransportException or CloudOfflineException or HttpRequestException ||
            error is CloudRequestException cloud && ((int)cloud.StatusCode >= 500 || cloud.StatusCode is HttpStatusCode.TooManyRequests or HttpStatusCode.RequestTimeout))
            return FileFailureKind.Network;
        if (error is UnsafePathException or CloudRequestException) return FileFailureKind.RecoveryRequired;
        // Only the local-read phase proves there have been no business/state writes.
        // The same native exception during commit may concern credentials or recovery state.
        if (preparingLocal && (error is IOException or UnauthorizedAccessException)) return FileFailureKind.FileUnavailable;
        return FileFailureKind.RecoveryRequired;
    }
}
