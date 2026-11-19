using System.Text;
using HarborBackup.Core.Domain;
using HarborBackup.Windows.Integration;
using static HarborBackup.Windows.Tests.CloudTransportTests;

namespace HarborBackup.Windows.Tests;

[TestClass]
[DoNotParallelize]
public sealed class ProjectOnboardingTests
{
    [TestMethod]
    public async Task RegisteredFolderUsesOriginalProjectIdentityWithoutWritingContent()
    {
        using var f = new Fixture(); var a = f.Device("A");
        File.WriteAllText(Path.Combine(a.P.Source, "local.txt"), "shared"); await Apply(a.S);
        var before = f.Drive.Items.ToDictionary(p => p.Key, p => (p.Value.Item, Convert.ToBase64String(p.Value.Bytes)));
        var downloads = f.Drive.BusinessDownloads;
        var candidate = a.P with { TaskId = DeviceIdentity.Digest("new-local-project"), CloudFolderId = null };
        var inspection = await CloudProjectOnboarding.InspectAsync(DataPaths.FromState(a.C.Preview.StateDirectory), f.Account, candidate, f.Drive, default);
        Assert.AreEqual(a.P.ProjectId, inspection.ExistingProject?.ProjectId);
        Assert.IsTrue(inspection.HasContents);
        Assert.AreEqual(downloads, f.Drive.BusinessDownloads);
        CollectionAssert.AreEquivalent(before.Keys.ToArray(), f.Drive.Items.Keys.ToArray());
        foreach (var pair in before) Assert.AreEqual(pair.Value, (f.Drive.Items[pair.Key].Item, Convert.ToBase64String(f.Drive.Items[pair.Key].Bytes)));
    }

    [TestMethod]
    public async Task UnregisteredNonemptyFolderRequiresReviewWhileEmptyOrMissingTargetsDoNot()
    {
        using var f = new Fixture(); var a = f.Device("A");
        var root = await f.Drive.FolderAsync("drive-root", a.P.Destination, default);
        var empty = await CloudProjectOnboarding.InspectAsync(DataPaths.FromState(a.C.Preview.StateDirectory), f.Account, a.P, f.Drive, default);
        Assert.IsNull(empty.ExistingProject); Assert.IsFalse(empty.HasContents);
        f.Drive.Add(root.Id, "existing.txt", Encoding.UTF8.GetBytes("remote"));
        var before = f.Drive.Items.Count;
        var existing = await CloudProjectOnboarding.InspectAsync(DataPaths.FromState(a.C.Preview.StateDirectory), f.Account, a.P, f.Drive, default);
        Assert.IsNull(existing.ExistingProject); Assert.IsTrue(existing.HasContents); Assert.AreEqual(root.Id, existing.FolderId);
        var missing = await CloudProjectOnboarding.InspectAsync(DataPaths.FromState(a.C.Preview.StateDirectory), f.Account, a.P with { Destination = "Missing/Project" }, f.Drive, default);
        Assert.IsNull(missing.FolderId); Assert.IsFalse(missing.HasContents);
        Assert.HasCount(before, f.Drive.Items); Assert.AreEqual(0, f.Drive.BusinessDownloads);
    }

    [TestMethod]
    public async Task StaleFolderSelectionAndCancellationNeverAuthorizeANewMapping()
    {
        using var f = new Fixture(); var a = f.Device("A");
        await Assert.ThrowsExactlyAsync<IOException>(() => CloudProjectOnboarding.InspectAsync(
            DataPaths.FromState(a.C.Preview.StateDirectory), f.Account, a.P with { CloudFolderId = "stale" }, f.Drive, default));
        using var cancelled = new CancellationTokenSource(); cancelled.Cancel();
        await Assert.ThrowsExactlyAsync<OperationCanceledException>(() => CloudProjectOnboarding.InspectAsync(
            DataPaths.FromState(a.C.Preview.StateDirectory), f.Account, a.P, f.Drive, cancelled.Token));
    }

    [TestMethod]
    public async Task MultipleProjectIdsForOneFolderRequireAnExplicitChoice()
    {
        using var f = new Fixture(); var a = f.Device("A");
        File.WriteAllText(Path.Combine(a.P.Source, "same.txt"), "content"); await Apply(a.S);
        var target = (await f.Drive.FindAsync("drive-root", a.P.Destination, default))!;
        var repository = new CloudRepository(f.Drive, f.Account, DataPaths.FromState(a.C.Preview.StateDirectory));
        await new CloudProjectRegistry(repository, f.Drive, f.Account.DriveId!).RegisterAsync(
            a.P with { TaskId = DeviceIdentity.Digest("another-id"), MappingVersion = Guid.NewGuid().ToString("N") }, target, default);
        var before = f.Drive.Items.Count;
        await Assert.ThrowsExactlyAsync<IOException>(() => CloudProjectOnboarding.InspectAsync(
            DataPaths.FromState(a.C.Preview.StateDirectory), f.Account, a.P, f.Drive, default));
        Assert.HasCount(before, f.Drive.Items);
    }
}
