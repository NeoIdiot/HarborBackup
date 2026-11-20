using System.Text;
using HarborBackup.Core.Domain;
using HarborBackup.Windows.Integration;
using HarborBackup.Windows.Persistence;
using static HarborBackup.Windows.Tests.CloudTransportTests;

namespace HarborBackup.Windows.Tests;

[TestClass]
[DoNotParallelize]
public sealed class ProjectIdentityOperationTests
{
    private static (ProjectDefinition Project, CloudBackupSession Session) OtherProject(Fixture fixture,
        ConfigurationSnapshot config)
    {
        var device = fixture.Device("Other");
        var project = device.P with { TaskId = DeviceIdentity.Digest("other-project"), Destination = "Other",
            Target = "OneDrive/Other", Fingerprint = DeviceIdentity.Digest("other-mapping") };
        return (project, new(project, config with { Projects = [project] }, fixture.Account, fixture.Drive));
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task ConflictActionsUseProjectIdAndIgnoreAnotherProjectsDisplayDestination(bool export)
    {
        using var fixture = new Fixture(); var device = fixture.Device("A");
        var other = OtherProject(fixture, device.C);
        var source = Path.Combine(device.P.Source, "a.txt");
        File.WriteAllText(source, "original-cloud"); await Apply(device.S);
        File.WriteAllText(Path.Combine(other.Project.Source, "a.txt"), "other-cloud"); await Apply(other.Session);
        File.WriteAllText(source, "chosen-local");
        File.WriteAllText(Path.Combine(other.Project.Source, "a.txt"), "other-local");
        var config = device.C with { Projects = [device.P, other.Project] };
        var inspection = await CloudConflictResolution.InspectAsync(config, device.P.Destination, "a.txt", fixture.Drive, default);
        Assert.AreEqual(device.P.ProjectId, inspection.ProjectId);
        Assert.AreEqual(device.P.Destination, inspection.Destination);
        var changedDisplay = inspection with { Destination = other.Project.Destination };
        if (export)
        {
            var output = await CloudConflictExport.ExportAsync(config, changedDisplay, "local", fixture.Drive,
                Path.Combine(fixture.Root, "exports"), default);
            Assert.AreEqual("chosen-local", File.ReadAllText(output));
        }
        else
        {
            await CloudConflictResolution.ResolveAsync(config, changedDisplay, "local", fixture.Drive, default);
            var root = (await fixture.Drive.FindAsync("drive-root", device.P.Destination, default))!;
            var item = (await fixture.Drive.FindAsync(root.Id, "a.txt", default))!;
            Assert.AreEqual("chosen-local", Encoding.UTF8.GetString(fixture.Drive.Items[item.Id].Bytes));
        }
        var otherRoot = (await fixture.Drive.FindAsync("drive-root", other.Project.Destination, default))!;
        var otherItem = (await fixture.Drive.FindAsync(otherRoot.Id, "a.txt", default))!;
        Assert.AreEqual("other-cloud", Encoding.UTF8.GetString(fixture.Drive.Items[otherItem.Id].Bytes));
        Assert.AreEqual("other-local", File.ReadAllText(Path.Combine(other.Project.Source, "a.txt")));
    }

    [TestMethod]
    [DataRow("", false)]
    [DataRow("", true)]
    [DataRow("invalid-id", false)]
    [DataRow("invalid-id", true)]
    [DataRow("ffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffff", false)]
    [DataRow("ffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffff", true)]
    public async Task ConflictActionsRejectMissingInvalidOrUnknownProjectIdsWithoutPathFallback(string projectId, bool export)
    {
        using var fixture = new Fixture(); var device = fixture.Device("A");
        File.WriteAllText(Path.Combine(device.P.Source, "a.txt"), "payload"); await Apply(device.S);
        var inspection = await CloudConflictResolution.InspectAsync(device.C, device.P.ProjectId, "a.txt", fixture.Drive, default);
        var forged = inspection with { ProjectId = projectId, Destination = device.P.ProjectId };
        var writes = fixture.Drive.UploadIntents.Count; var reads = fixture.Drive.Downloads;
        var output = Path.Combine(fixture.Root, "exports");
        if (export)
            await Assert.ThrowsExactlyAsync<IOException>(() => CloudConflictExport.ExportAsync(device.C, forged, "local", fixture.Drive, output, default));
        else
            await Assert.ThrowsExactlyAsync<IOException>(() => CloudConflictResolution.ResolveAsync(device.C, forged, "local", fixture.Drive, default));
        Assert.HasCount(writes, fixture.Drive.UploadIntents);
        Assert.AreEqual(reads, fixture.Drive.Downloads);
        Assert.IsFalse(Directory.Exists(output));
        Assert.AreEqual("payload", File.ReadAllText(Path.Combine(device.P.Source, "a.txt")));
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task AnotherValidProjectIdCannotReuseOriginalConflictConfirmation(bool export)
    {
        using var fixture = new Fixture(); var device = fixture.Device("A");
        var other = OtherProject(fixture, device.C);
        File.WriteAllText(Path.Combine(device.P.Source, "a.txt"), "original"); await Apply(device.S);
        File.WriteAllText(Path.Combine(other.Project.Source, "a.txt"), "other"); await Apply(other.Session);
        var config = device.C with { Projects = [device.P, other.Project] };
        var inspection = await CloudConflictResolution.InspectAsync(config, device.P.ProjectId, "a.txt", fixture.Drive, default);
        var forged = inspection with { ProjectId = other.Project.ProjectId };
        var writes = fixture.Drive.UploadIntents.Count;
        var output = Path.Combine(fixture.Root, "exports");
        if (export)
            await Assert.ThrowsExactlyAsync<IOException>(() => CloudConflictExport.ExportAsync(config, forged, "local", fixture.Drive, output, default));
        else
            await Assert.ThrowsExactlyAsync<IOException>(() => CloudConflictResolution.ResolveAsync(config, forged, "local", fixture.Drive, default));
        Assert.HasCount(writes, fixture.Drive.UploadIntents);
        Assert.IsFalse(Directory.Exists(output));
        Assert.AreEqual("original", File.ReadAllText(Path.Combine(device.P.Source, "a.txt")));
        Assert.AreEqual("other", File.ReadAllText(Path.Combine(other.Project.Source, "a.txt")));
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task StableProjectIdDoesNotAuthorizeOldConflictConsentAfterMappingRename(bool export)
    {
        using var fixture = new Fixture(); var device = fixture.Device("A");
        File.WriteAllText(Path.Combine(device.P.Source, "a.txt"), "payload"); await Apply(device.S);
        var inspection = await CloudConflictResolution.InspectAsync(device.C, device.P.ProjectId, "a.txt", fixture.Drive, default);
        var oldRoot = (await fixture.Drive.FindAsync("drive-root", device.P.Destination, default))!;
        var renamed = await fixture.Drive.MoveAsync(oldRoot, "drive-root", "MyProjects", null, default);
        var source = Path.Combine(fixture.Root, "renamed-source"); Directory.Move(device.P.Source, source);
        var project = device.P with { Source = source, Destination = "MyProjects", Target = "OneDrive/MyProjects",
            CloudFolderId = renamed.Id, BindingVersion = 1, MappingVersion = Guid.NewGuid().ToString("N") };
        var config = device.C with { Projects = [project], RetiredMappings = [CloudMapping.Capture(device.P, device.C)] };
        Assert.AreEqual(inspection.ProjectId, project.ProjectId);
        var writes = fixture.Drive.UploadIntents.Count;
        var output = Path.Combine(fixture.Root, "exports");
        if (export)
            await Assert.ThrowsExactlyAsync<IOException>(() => CloudConflictExport.ExportAsync(config, inspection, "local", fixture.Drive, output, default));
        else
            await Assert.ThrowsExactlyAsync<IOException>(() => CloudConflictResolution.ResolveAsync(config, inspection, "local", fixture.Drive, default));
        Assert.HasCount(writes, fixture.Drive.UploadIntents);
        Assert.IsFalse(Directory.Exists(output));
        Assert.AreEqual("payload", File.ReadAllText(Path.Combine(source, "a.txt")));
    }

    [TestMethod]
    public async Task ArchiveRestoreKeepsPreviousLocationHistoryUnderTheSameProjectIdAfterRename()
    {
        using var fixture = new Fixture(); var device = fixture.Device("A");
        var other = OtherProject(fixture, device.C);
        var source = Path.Combine(device.P.Source, "a.txt");
        File.WriteAllText(source, "original-history"); await Apply(device.S);
        File.WriteAllText(source, "current-content"); await Apply(device.S);
        var otherSource = Path.Combine(other.Project.Source, "a.txt");
        File.WriteAllText(otherSource, "other-history"); await Apply(other.Session);
        File.WriteAllText(otherSource, "other-current"); await Apply(other.Session);
        var oldRoot = (await fixture.Drive.FindAsync("drive-root", device.P.Destination, default))!;
        var renamed = await fixture.Drive.MoveAsync(oldRoot, "drive-root", "MyProjects", null, default);
        var project = device.P with { Destination = "MyProjects", Target = "OneDrive/MyProjects", CloudFolderId = renamed.Id,
            BindingVersion = 1, MappingVersion = Guid.NewGuid().ToString("N") };
        var config = device.C with { Projects = [project, other.Project], RetiredMappings = [CloudMapping.Capture(device.P, device.C)] };
        var service = new CloudRestoreService(config, () => fixture.Drive);
        var writes = fixture.Drive.UploadIntents.Count;
        var selected = await service.ListAsync(project.ProjectId, RestoreScope.Archive, null, default);
        var history = selected.Candidates.Single();
        Assert.AreEqual(project.ProjectId, history.ProjectId);
        Assert.AreEqual("MyProjects", history.Destination, "The current display location must not hide the old protection location.");
        var output = await service.ExportAsync(history, Path.Combine(fixture.Root, "restored"), default);
        Assert.AreEqual(RestoreFileStatus.Verified, output.Status);
        Assert.AreEqual("original-history", File.ReadAllText(output.OutputPath!));
        var all = await service.ListAsync(null, RestoreScope.Archive, null, default);
        Assert.HasCount(2, all.Candidates);
        Assert.HasCount(1, all.Candidates.Where(c => c.ProjectId == project.ProjectId));
        Assert.HasCount(1, all.Candidates.Where(c => c.ProjectId == other.Project.ProjectId));
        Assert.HasCount(writes, fixture.Drive.UploadIntents);
        Assert.AreEqual("current-content", File.ReadAllText(source));
        Assert.AreEqual("other-current", File.ReadAllText(otherSource));
    }
}
