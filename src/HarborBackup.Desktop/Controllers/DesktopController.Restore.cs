using HarborBackup.Core.Domain;
using HarborBackup.Windows.Integration;

namespace HarborBackup.Desktop.Controllers;

internal sealed partial class DesktopController
{
    public Task<RestoreCatalog> ListRestoreAsync(string project, RestoreScope scope, string? path, CancellationToken token) =>
        ExclusiveAsync(cancellation => new BackupRuntime(Paths).ListRestore(project, scope, path, cancellation), token);

    public Task<RestoreReport> RestoreAsync(IReadOnlyList<RestoreCandidate> candidates, string outputRoot,
        bool testOnly, long maxBytes, int groups, CancellationToken token) =>
        ExclusiveAsync(cancellation => new BackupRuntime(Paths).Restore(candidates, outputRoot, testOnly, maxBytes, groups, cancellation), token);
}
