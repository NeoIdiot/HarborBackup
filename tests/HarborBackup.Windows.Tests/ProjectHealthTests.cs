using System.Text.Json;
using HarborBackup.Core.Domain;
using HarborBackup.Windows.Integration;
using HarborBackup.Windows.Persistence;

namespace HarborBackup.Windows.Tests;

[TestClass]
public sealed class ProjectHealthTests
{
    private static ConfigurationSnapshot Config(TestWorkspace ws)
    {
        var config = ws.Load();
        return config with { Projects = [config.Projects[0] with { BindingVersion = 1, MappingVersion = Guid.NewGuid().ToString("N") }] };
    }

    private static void Save(ConfigurationSnapshot config, DateTimeOffset time, bool? complete,
        IReadOnlyList<FileResult> files, IReadOnlyList<PreviewIssue>? issues = null) => RunStatusStore.Write(config,
        new("Backup", time.AddMinutes(-1), time,
            [new(config.Projects[0].Destination, files, issues ?? [], new())
                { ProjectId = config.Projects[0].ProjectId, CoverageComplete = complete }]));

    [TestMethod]
    public void CompleteSuccessIsScopedToCurrentMappingAndRuleEditsKeepIt()
    {
        using var ws = new TestWorkspace(); var config = Config(ws); var project = config.Projects[0];
        var time = DateTimeOffset.UtcNow.AddHours(-2);
        Save(config, time, true, [new("a.txt", FileAction.Equal, false)]);
        var status = RunStatusStore.Read(config.Preview);
        Assert.AreEqual(time, ProjectHealthStore.Read(config, project, status).LastFullSuccessUtc);
        var rules = project with { ExcludedFiles = ["*.tmp"], Fingerprint = new string('a', 64) };
        Assert.AreEqual(time, ProjectHealthStore.Read(config, rules, status).LastFullSuccessUtc);
        var moved = project with { Source = Path.Combine(ws.Root, "moved"), MappingVersion = Guid.NewGuid().ToString("N") };
        Assert.IsNull(ProjectHealthStore.Read(config, moved, status).LastFullSuccessUtc);
        status!["projects"]![0]!.AsObject().Remove("healthBinding");
        Assert.IsNull(ProjectHealthStore.Read(config, project, status).LastFullSuccessUtc, "Unbound old summaries cannot prove current mapping coverage.");
    }

    [TestMethod]
    public void KnownBacklogKeepsFirstSeenAcrossPartialAttemptsAndOnlyCompleteCoverageClearsIt()
    {
        using var ws = new TestWorkspace(); var config = Config(ws); var project = config.Projects[0];
        var first = DateTimeOffset.UtcNow.AddHours(-30);
        Save(config, first, false, [new("a.txt", FileAction.Deferred, false, "Unavailable")]);
        Save(config, first.AddHours(2), false, [new("a.txt", FileAction.Deferred, false, "Unavailable"), new("b.txt", FileAction.Deferred, false)]);
        var health = ProjectHealthStore.Read(config, project, RunStatusStore.Read(config.Preview));
        Assert.AreEqual(first, health.OldestPendingUtc); Assert.AreEqual(2, health.KnownBacklogCount);
        Assert.IsTrue(health.PendingCountIncomplete); Assert.IsTrue(health.NeedsAttention(DateTimeOffset.UtcNow));
        Save(config, first.AddHours(4), false, [new("a.txt", FileAction.Equal, false)]);
        health = ProjectHealthStore.Read(config, project, RunStatusStore.Read(config.Preview));
        Assert.AreEqual(1, health.KnownBacklogCount); Assert.IsNull(health.LastFullSuccessUtc);
        Assert.AreEqual(first, health.OldestPendingUtc, "Unobserved work keeps the conservative first-pending time.");
        var success = DateTimeOffset.UtcNow.AddMinutes(-1);
        Save(config, success, true, [new("b.txt", FileAction.Equal, false)]);
        health = ProjectHealthStore.Read(config, project, RunStatusStore.Read(config.Preview));
        Assert.AreEqual(0, health.KnownBacklogCount); Assert.IsNull(health.OldestPendingUtc);
        Assert.AreEqual(success, health.LastFullSuccessUtc); Assert.IsFalse(health.NeedsAttention(DateTimeOffset.UtcNow));
    }

    [TestMethod]
    public void RestoreEvidenceRequiresAllIdentitiesValidTimeAndPositiveVerifiedScope()
    {
        using var ws = new TestWorkspace(); var config = Config(ws); var project = config.Projects[0]; var account = config.CloudAccount!;
        var time = DateTimeOffset.UtcNow.AddMinutes(-1);
        var evidence = new RestoreVerificationState(1, config.Preview.DeviceId, account.AccountId, account.DriveId,
            account.RepositoryId, account.RootId, CloudMapping.Bind(project, config), project.ProjectId, time, 2, 18, RestoreScope.Archive);
        var path = CloudRestoreService.VerificationPath(config, project); Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        void Write(RestoreVerificationState value) => File.WriteAllText(path, JsonSerializer.Serialize(value, AtomicJson.Options));
        Write(evidence);
        var health = ProjectHealthStore.Read(config, project, null);
        Assert.AreEqual(time, health.LastRestoreVerifiedUtc); Assert.AreEqual(2, health.RestoreVerifiedFiles); Assert.AreEqual("Archive", health.RestoreScope);
        foreach (var invalid in new[] { evidence with { DeviceId = new string('f', 64) }, evidence with { VerifiedFiles = 0 },
            evidence with { AccountId = "other" }, evidence with { VerifiedUtc = DateTimeOffset.UtcNow.AddHours(1) },
            evidence with { Binding = new string('a', 64) } })
        {
            Write(invalid);
            Assert.ThrowsExactly<FormatException>(() => ProjectHealthStore.Read(config, project, null));
        }
    }
}
