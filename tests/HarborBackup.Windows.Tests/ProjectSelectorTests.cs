using HarborBackup.Windows.Integration;

namespace HarborBackup.Windows.Tests;

[TestClass]
public sealed class ProjectSelectorTests
{
    [TestMethod]
    public void BusinessSelectionRequiresTheProjectId()
    {
        using var ws = new TestWorkspace();
        var config = ws.Load(); var project = config.Projects.Single();
        Assert.AreEqual(project, BackupRuntime.Select(config, project.ProjectId).Single());
        Assert.ThrowsExactly<ArgumentException>(() => BackupRuntime.Select(config, project.Destination));
        Assert.ThrowsExactly<ArgumentException>(() => BackupRuntime.Select(config, DeviceIdentity.Digest("unknown")));
    }

    [TestMethod]
    public void HumanReadableEntrySelectorResolvesOnceToTheStableId()
    {
        using var ws = new TestWorkspace();
        var config = ws.Load(); var project = config.Projects.Single();
        var id = BackupRuntime.ResolveProjectId(config, project.Destination.ToUpperInvariant());
        Assert.AreEqual(project.ProjectId, id);
        var renamed = project with { Destination = "MyProjects/Demo" };
        var other = project with { TaskId = DeviceIdentity.Digest("other"), Source = Path.Combine(ws.Root, "other") };
        var after = config with { Projects = [renamed, other] };
        Assert.AreEqual(renamed, BackupRuntime.Select(after, id).Single(), "A resolved business ID must survive path reuse by another project.");
        Assert.AreEqual(other.ProjectId, BackupRuntime.ResolveProjectId(after, project.Destination));
    }

    [TestMethod]
    public void UnknownIdCannotFallBackToAnIdenticallyNamedFolder()
    {
        using var ws = new TestWorkspace();
        var config = ws.Load(); var project = config.Projects.Single();
        var unknown = DeviceIdentity.Digest("not-a-configured-project");
        config = config with { Projects = [project with { Destination = unknown }] };
        Assert.ThrowsExactly<ArgumentException>(() => BackupRuntime.ResolveProjectId(config, unknown));
    }

    [TestMethod]
    public void ActualProjectIdTakesPrecedenceOverAnotherProjectsPath()
    {
        using var ws = new TestWorkspace();
        var config = ws.Load(); var project = config.Projects.Single();
        var other = project with { TaskId = DeviceIdentity.Digest("other"), Destination = project.ProjectId };
        config = config with { Projects = [project, other] };
        Assert.AreEqual(project.ProjectId, BackupRuntime.ResolveProjectId(config, project.ProjectId));
        Assert.AreEqual(project, BackupRuntime.Select(config, project.ProjectId).Single());
    }

    [TestMethod]
    public void AmbiguousDisplayPathsCannotChooseAProject()
    {
        using var ws = new TestWorkspace();
        var config = ws.Load(); var project = config.Projects.Single();
        config = config with { Projects = [project, project with { TaskId = DeviceIdentity.Digest("other") }] };
        Assert.ThrowsExactly<ArgumentException>(() => BackupRuntime.ResolveProjectId(config, project.Destination));
        Assert.IsNull(BackupRuntime.ResolveProjectId(config, null));
        Assert.HasCount(2, BackupRuntime.Select(config, null));
    }
}
