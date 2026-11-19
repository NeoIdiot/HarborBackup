using System.Text;
using HarborBackup.Windows.Integration;
using HarborBackup.Windows.Persistence;
using static HarborBackup.Windows.Tests.CloudTransportTests;

namespace HarborBackup.Windows.Tests;

[TestClass]
[DoNotParallelize]
public sealed class ProjectRenameTests
{
    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task ConcurrentLatestRegistrationsMustAgreeOnPhysicalFolder(bool differentFolder)
    {
        using var f = new Fixture(); var a = f.Device("A");
        File.WriteAllText(Path.Combine(a.P.Source, "a.txt"), "base"); await Apply(a.S);
        var repository = new CloudRepository(f.Drive, f.Account, DataPaths.FromState(a.C.Preview.StateDirectory));
        var registry = new CloudProjectRegistry(repository, f.Drive, f.Account.DriveId!);
        var current = (await registry.ReadAsync(a.P.ProjectId, default)).Single();
        var otherFolder = differentFolder ? await f.Drive.FolderAsync("drive-root", "Other", default) : await f.Drive.GetAsync(current.FolderId, default);
        var next = current with { MappingId = DeviceIdentity.Digest("concurrent-registration"), FolderId = otherFolder.Id,
            Destination = differentFolder ? "Other" : current.Destination };
        var locations = (await repository.FolderAsync("metadata/projects/" + a.P.ProjectId + "/locations", false, default))!;
        await repository.JsonAsync(locations.Id, next.MappingId + ".json", next, default);
        var writes = f.Drive.UploadIntents.Count;
        if (differentFolder)
        {
            await Assert.ThrowsExactlyAsync<IOException>(() => CloudProjectCatalog.ReadAsync(DataPaths.FromState(a.C.Preview.StateDirectory), f.Account, f.Drive, default));
            File.WriteAllText(Path.Combine(a.P.Source, "a.txt"), "local changed");
            var result = await new CloudBackupRuntime(a.C, () => f.Drive).ExecuteAsync(HarborBackup.Core.Domain.SyncDirection.Backup, null, false, false, null, default, automatic: true);
            Assert.AreEqual(1, result.ExitCode);
            Assert.HasCount(writes, f.Drive.UploadIntents);
        }
        else
        {
            var catalog = await CloudProjectCatalog.ReadAsync(DataPaths.FromState(a.C.Preview.StateDirectory), f.Account, f.Drive, default);
            Assert.AreEqual(current.FolderId, catalog.Single().FolderId);
        }
    }

    [TestMethod]
    public async Task EmptyProjectRegistersItsIdWithoutUploadingBusinessFiles()
    {
        using var f = new Fixture(); var a = f.Device("A");
        var runtime = new CloudBackupRuntime(a.C, () => f.Drive);
        Assert.AreEqual(0, (await runtime.ExecuteAsync(HarborBackup.Core.Domain.SyncDirection.Backup, a.P.ProjectId, false, false, null, default)).ExitCode);
        var catalog = await CloudProjectCatalog.ReadAsync(DataPaths.FromState(a.C.Preview.StateDirectory), f.Account, f.Drive, default);
        Assert.HasCount(1, catalog); Assert.AreEqual(a.P.ProjectId, catalog[0].ProjectId);
        Assert.IsFalse(f.Drive.UploadIntents.Any(p => !p.Name.EndsWith(".json", StringComparison.Ordinal)));
    }
    [TestMethod]
    public async Task CatalogFollowsRenamedFolderAndDeduplicatesHistoricalRegistrationsWithoutWrites()
    {
        using var f = new Fixture(); var a = f.Device("A");
        File.WriteAllText(Path.Combine(a.P.Source, "a.txt"), "payload"); await Apply(a.S);
        var original = (await f.Drive.FindAsync("drive-root", "Documents", default))!;
        var parent = await f.Drive.FolderAsync("drive-root", "LabProjects", default);
        var renamed = await f.Drive.MoveAsync(original, parent.Id, "Demo", null, default);
        var repository = new CloudRepository(f.Drive, f.Account, DataPaths.FromState(a.C.Preview.StateDirectory));
        var next = a.P with { Destination = "LabProjects/Demo", MappingVersion = Guid.NewGuid().ToString("N") };
        await new CloudProjectRegistry(repository, f.Drive, f.Account.DriveId!).RegisterAsync(next, renamed, default);
        var before = f.Drive.Items.ToDictionary(p => p.Key, p => (p.Value.Item, Convert.ToBase64String(p.Value.Bytes)));
        var writes = f.Drive.UploadIntents.Count;
        var choices = await CloudProjectCatalog.ReadAsync(DataPaths.FromState(a.C.Preview.StateDirectory), f.Account, f.Drive, default);
        Assert.HasCount(1, choices);
        Assert.AreEqual("LabProjects/Demo", choices[0].Destination);
        Assert.AreEqual(original.Id, choices[0].FolderId);
        Assert.AreEqual(a.P.ProjectId, choices[0].ProjectId);
        Assert.HasCount(writes, f.Drive.UploadIntents);
        Assert.HasCount(before.Count, f.Drive.Items);
        foreach (var (id, value) in f.Drive.Items)
            Assert.AreEqual(before[id], (value.Item, Convert.ToBase64String(value.Bytes)));
        Assert.AreEqual("payload", Encoding.UTF8.GetString(f.Drive.Items.Values.Single(p => p.Item.Name == "a.txt").Bytes));
    }

    [TestMethod]
    public async Task RenamedProjectRemainsOneTaskAndConfirmedBackupWritesOnlyNewConfiguredTarget()
    {
        using var f = new Fixture(); var a = f.Device("A");
        File.WriteAllText(Path.Combine(a.P.Source, "a.txt"), "old"); await Apply(a.S);
        var source = Path.Combine(f.Root, "new-source"); Directory.CreateDirectory(source);
        File.WriteAllText(Path.Combine(source, "a.txt"), "new");
        var project = a.P with { Source = source, Destination = "LabProjects/Demo", Target = "OneDrive/LabProjects/Demo", MappingVersion = Guid.NewGuid().ToString("N") };
        var config = a.C with { Projects = [project], RetiredMappings = [CloudMapping.Capture(a.P, a.C)] };
        var runtime = new CloudBackupRuntime(config, () => f.Drive);
        var preview = await runtime.PreviewAsync(HarborBackup.Core.Domain.SyncDirection.Backup, project.Destination, false, default, prepareUpload: true);
        var result = await runtime.ExecuteAsync(HarborBackup.Core.Domain.SyncDirection.Backup, project.Destination, false, false, null, default,
            confirmedUpload: preview.UploadSelection);
        Assert.AreEqual(0, result.ExitCode);
        Assert.AreEqual(a.P.TaskId, project.TaskId);
        var oldTarget = (await f.Drive.FindAsync("drive-root", "Documents", default))!;
        var newParent = (await f.Drive.FindAsync("drive-root", "LabProjects", default))!;
        var newTarget = (await f.Drive.FindAsync(newParent.Id, "Demo", default))!;
        Assert.AreEqual("old", Encoding.UTF8.GetString(f.Drive.Items[(await f.Drive.FindAsync(oldTarget.Id, "a.txt", default))!.Id].Bytes));
        Assert.AreEqual("new", Encoding.UTF8.GetString(f.Drive.Items[(await f.Drive.FindAsync(newTarget.Id, "a.txt", default))!.Id].Bytes));
        Assert.AreEqual("old", File.ReadAllText(Path.Combine(a.P.Source, "a.txt")));
        var catalog = await CloudProjectCatalog.ReadAsync(DataPaths.FromState(config.Preview.StateDirectory), f.Account, f.Drive, default);
        Assert.HasCount(1, catalog);
        Assert.AreEqual(project.ProjectId, catalog[0].ProjectId);
        Assert.AreEqual("LabProjects/Demo", catalog[0].Destination);
    }

    [TestMethod]
    public async Task ProjectIdKeepsMetadataAcrossRenameAndAnotherDeviceUsesTheSameIdentity()
    {
        using var f = new Fixture(); var a = f.Device("A");
        File.WriteAllText(Path.Combine(a.P.Source, "a.txt"), "first"); await Apply(a.S);
        var oldTarget = (await f.Drive.FindAsync("drive-root", "Documents", default))!;
        var renamed = await f.Drive.MoveAsync(oldTarget, "drive-root", "Renamed", null, default);
        var newSource = Path.Combine(f.Root, "source-renamed"); Directory.Move(a.P.Source, newSource);
        var p = a.P with { Source = newSource, Destination = "Renamed", CloudFolderId = renamed.Id, MappingVersion = Guid.NewGuid().ToString("N") };
        var c = a.C with { Projects = [p], RetiredMappings = [CloudMapping.Capture(a.P, a.C)] };
        File.WriteAllText(Path.Combine(p.Source, "a.txt"), "second");
        var runtime = new CloudBackupRuntime(c, () => f.Drive);
        var preview = await runtime.PreviewAsync(HarborBackup.Core.Domain.SyncDirection.Backup, p.ProjectId, false, default, prepareUpload: true);
        Assert.AreEqual(p.ProjectId, preview.UploadSelection!.ProjectId);
        Assert.AreEqual(0, (await runtime.ExecuteAsync(HarborBackup.Core.Domain.SyncDirection.Backup, p.ProjectId, false, false, null, default,
            confirmedUpload: preview.UploadSelection)).ExitCode);
        var repository = new CloudRepository(f.Drive, f.Account, DataPaths.FromState(c.Preview.StateDirectory));
        var projects = (await repository.FolderAsync("metadata/projects", false, default))!;
        Assert.HasCount(1, await f.Drive.ChildrenAsync(projects.Id, default));
        var revisions = await runtime.RevisionsAsync(p.ProjectId, default);
        Assert.HasCount(2, revisions); Assert.IsTrue(revisions.All(r => r.Project == p.ProjectId));
        var catalog = await CloudProjectCatalog.ReadAsync(DataPaths.FromState(c.Preview.StateDirectory), f.Account, f.Drive, default);
        Assert.HasCount(1, catalog); Assert.AreEqual(p.ProjectId, catalog[0].ProjectId);
        var b = f.Device("B");
        var joined = b.P with { TaskId = catalog[0].ProjectId, Destination = catalog[0].Destination, CloudFolderId = catalog[0].FolderId };
        var other = new CloudBackupRuntime(b.C with { Projects = [joined] }, () => f.Drive);
        var pull = await other.PreviewAsync(HarborBackup.Core.Domain.SyncDirection.Pull, joined.ProjectId, false, default);
        Assert.AreEqual(0, (await other.ExecuteAsync(HarborBackup.Core.Domain.SyncDirection.Pull, joined.ProjectId, false, false, null, default, pull.PullSelection)).ExitCode);
        Assert.AreEqual("second", File.ReadAllText(Path.Combine(joined.Source, "a.txt")));
    }

    [TestMethod]
    public async Task SelectionCannotAuthorizeAnotherProjectWithTheSameDisplayPath()
    {
        using var f = new Fixture(); var a = f.Device("A");
        File.WriteAllText(Path.Combine(a.P.Source, "a.txt"), "payload");
        var runtime = new CloudBackupRuntime(a.C, () => f.Drive);
        var preview = await runtime.PreviewAsync(HarborBackup.Core.Domain.SyncDirection.Backup, a.P.ProjectId, false, default, prepareUpload: true);
        var forged = preview.UploadSelection! with { ProjectId = DeviceIdentity.Digest("another-project") };
        await Assert.ThrowsExactlyAsync<ArgumentException>(() => runtime.ExecuteAsync(HarborBackup.Core.Domain.SyncDirection.Backup, a.P.ProjectId,
            false, false, null, default, confirmedUpload: forged));
        Assert.IsFalse(f.Drive.Items.Values.Any(i => i.Item.Name == "a.txt"));
    }
}
