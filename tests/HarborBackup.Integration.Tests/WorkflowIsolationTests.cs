using System.Text;
using System.Text.Json;
using HarborBackup.Core.Domain;
using HarborBackup.Windows.Integration;
using HarborBackup.Windows.Persistence;
using static HarborBackup.Windows.Tests.CloudTransportTests;

namespace HarborBackup.Windows.Tests;

[TestClass]
[DoNotParallelize]
public sealed class WorkflowIsolationTests
{
    [TestMethod]
    public async Task ObservationFailureDoesNotRevokeReadinessOrStopOtherProjects()
    {
        using var f = new Fixture(); var a = f.Device("A");
        var first = a.P with { MappingVersion = Guid.NewGuid().ToString("N") };
        var other = Other(f, first) with { MappingVersion = Guid.NewGuid().ToString("N") };
        File.WriteAllText(Path.Combine(first.Source, "a.txt"), "first");
        File.WriteAllText(Path.Combine(other.Source, "b.txt"), "second");
        var c = a.C with { Projects = [first, other], Preview = a.C.Preview with { ContinueOnError = false } };
        f.Drive.AfterUpload = item =>
        {
            var parent = item.ParentId;
            while (parent is not null && f.Drive.Items.TryGetValue(parent, out var ancestor))
            {
                if (ancestor.Item.Name == "devices") throw new IOException("Observation response lost");
                parent = ancestor.Parent;
            }
            return Task.CompletedTask;
        };
        var runtime = new CloudBackupRuntime(c, () => f.Drive);
        var result = await runtime.ExecuteAsync(SyncDirection.Backup, null, false, false, null, default);
        Assert.AreEqual(2, result.ExitCode, JsonSerializer.Serialize(result));
        Assert.HasCount(2, result.Projects);
        foreach (var run in result.Projects)
        {
            Assert.IsTrue(run.Files.Single().Changed);
            Assert.AreEqual("DeviceStatusPending", run.Issues.Single().Code);
            Assert.IsFalse(run.Issues.Single().IsFailure);
        }
        Assert.IsFalse(CloudMapping.NeedsFirstOperation(first, c));
        Assert.IsFalse(CloudMapping.NeedsFirstOperation(other, c));
        Assert.HasCount(2, Directory.GetFiles(c.Preview.StateDirectory, "*.json", SearchOption.AllDirectories).Where(p => p.Contains("cloud-outbox")));
        f.Drive.AfterUpload = null;
        var retry = await runtime.ExecuteAsync(SyncDirection.Backup, null, false, false, null, default, automatic: true);
        Assert.AreEqual(0, retry.ExitCode, JsonSerializer.Serialize(retry));
        Assert.IsFalse(retry.Projects.SelectMany(p => p.Files).Any(p => p.Changed));
        Assert.IsEmpty(Directory.GetFiles(c.Preview.StateDirectory, "*.json", SearchOption.AllDirectories).Where(p => p.Contains("cloud-outbox")));
    }

    [TestMethod]
    [DataRow("broken-json")]
    [DataRow("unknown-schema")]
    [DataRow("unsafe-path")]
    public async Task KnownDamagedMappingDoesNotBlockIndependentProject(string kind)
    {
        using var f = new Fixture(); var a = f.Device("A"); var other = Other(f, a.P);
        var c = a.C with { Projects = [a.P, other] };
        File.WriteAllText(Path.Combine(other.Source, "b.txt"), "other");
        var journal = Damage(a.P, c, kind);
        var before = File.ReadAllBytes(journal);
        var runtime = new CloudBackupRuntime(c, () => f.Drive);
        var preview = await runtime.PreviewAsync(SyncDirection.Backup, "Other", false, default, prepareUpload: true);
        Assert.AreEqual(0, preview.ExitCode, JsonSerializer.Serialize(preview));
        Assert.HasCount(1, preview.UploadSelection!.Files);
        Assert.AreEqual(0, (await runtime.ExecuteAsync(SyncDirection.Backup, "Other", false, false, null, default, confirmedUpload: preview.UploadSelection)).ExitCode);
        var blocked = await runtime.ExecuteAsync(SyncDirection.Backup, "Documents", false, false, null, default);
        Assert.AreEqual(1, blocked.ExitCode);
        CollectionAssert.AreEqual(before, File.ReadAllBytes(journal));
        var scan = CloudMapping.ScanPending(c);
        Assert.HasCount(1, scan.Problems); Assert.IsNotNull(scan.Problems[0].Mapping);
    }

    [TestMethod]
    public async Task UnknownRecoveryScopeStillBlocksWrites()
    {
        using var f = new Fixture(); var a = f.Device("A");
        File.WriteAllText(Path.Combine(a.P.Source, "a.txt"), "local");
        var directory = Path.Combine(a.C.Preview.StateDirectory, "devices", a.C.Preview.DeviceId, "cloud", new string('0', 64), "cloud-transactions");
        Directory.CreateDirectory(directory); var journal = Path.Combine(directory, "unknown.json"); File.WriteAllText(journal, "broken");
        var runtime = new CloudBackupRuntime(a.C, () => f.Drive);
        Assert.IsEmpty((await runtime.PreviewAsync(SyncDirection.Backup, "Documents", false, default, prepareUpload: true)).UploadSelection!.Files);
        Assert.AreEqual(1, (await runtime.ExecuteAsync(SyncDirection.Backup, "Documents", false, false, null, default)).ExitCode);
        Assert.IsEmpty(f.Drive.UploadIntents); Assert.AreEqual("broken", File.ReadAllText(journal));
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task ConflictingJournalLocationCannotBeScopedToTheContainingProject(bool alteredSnapshot)
    {
        using var f = new Fixture(); var a = f.Device("A"); var other = Other(f, a.P);
        var c = a.C with { Projects = [a.P, other] };
        File.WriteAllText(Path.Combine(a.P.Source, "a.txt"), "local");
        var id = Guid.NewGuid().ToString("N");
        var path = Path.Combine(CloudMapping.StateRoot(other, c), "cloud-transactions", id + ".json");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, JsonSerializer.Serialize(new CloudJournal(5, id, CloudMapping.Bind(a.P, c), "a.txt", SyncDirection.Backup,
            null, null, null, true, ProtectionKind.None, "parent", Mapping: CloudMapping.Capture(a.P, c)), AtomicJson.Options));
        if (alteredSnapshot)
        {
            var node = System.Text.Json.Nodes.JsonNode.Parse(File.ReadAllText(path))!;
            node["mapping"]!["project"]!["source"] = Path.Combine(f.Root, "altered-source");
            File.WriteAllText(path, node.ToJsonString());
        }
        var before = File.ReadAllBytes(path);
        var scan = CloudMapping.ScanPending(c);
        Assert.IsNull(scan.Problems.Single().Mapping);
        var result = await new CloudBackupRuntime(c, () => f.Drive).ExecuteAsync(SyncDirection.Backup, "Documents", false, false, null, default);
        Assert.AreEqual(1, result.ExitCode); Assert.IsEmpty(f.Drive.UploadIntents);
        CollectionAssert.AreEqual(before, File.ReadAllBytes(path));
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task OverlappingCloudOrLocalMappingsRemainProtected(bool cloudOverlap)
    {
        using var f = new Fixture(); var a = f.Device("A");
        var original = cloudOverlap ? a.P with { CloudFolderId = "shared" } : a.P;
        var other = Other(f, original) with { CloudFolderId = cloudOverlap ? "shared" : null,
            Source = cloudOverlap ? Path.Combine(f.Root, "other") : original.Source };
        var c = a.C with { Projects = [original, other] };
        File.WriteAllText(Path.Combine(other.Source, "b.txt"), "local");
        var journal = Damage(original, c, "broken-json");
        var report = await new CloudBackupRuntime(c, () => f.Drive).ExecuteAsync(SyncDirection.Backup, "Other", false, false, null, default);
        Assert.AreEqual(1, report.ExitCode); Assert.IsEmpty(f.Drive.UploadIntents);
        Assert.AreEqual("broken", File.ReadAllText(journal));
    }

    [TestMethod]
    [DataRow("broken")]
    [DataRow("123")]
    [DataRow("null")]
    public async Task InvalidReadinessProofRequiresManualOperationWithoutBlockingPreview(string marker)
    {
        using var f = new Fixture(); var a = f.Device("A");
        var p = a.P with { MappingVersion = Guid.NewGuid().ToString("N") };
        var c = a.C with { Projects = [p] };
        File.WriteAllText(Path.Combine(p.Source, "a.txt"), "local");
        var path = Path.Combine(CloudMapping.StateRoot(p, c), "mapping-ready.json");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!); File.WriteAllText(path, marker);
        var runtime = new CloudBackupRuntime(c, () => f.Drive);
        Assert.AreEqual("MappingAwaitingOperation", (await runtime.ExecuteAsync(SyncDirection.Backup, null, false, false, null, default, automatic: true)).Projects.Single().Issues.Single().Code);
        var preview = await runtime.PreviewAsync(SyncDirection.Backup, "Documents", false, default, prepareUpload: true);
        Assert.HasCount(1, preview.UploadSelection!.Files); Assert.AreEqual(marker, File.ReadAllText(path));
        Assert.AreEqual(0, (await runtime.ExecuteAsync(SyncDirection.Backup, "Documents", false, false, null, default, confirmedUpload: preview.UploadSelection)).ExitCode);
        Assert.IsFalse(CloudMapping.NeedsFirstOperation(p, c));
    }

    private static ProjectDefinition Other(Fixture f, ProjectDefinition original)
    {
        var source = Path.Combine(f.Root, "other"); Directory.CreateDirectory(source);
        return original with { Source = source, Destination = "Other", TaskId = DeviceIdentity.Digest("Other") };
    }

    private static string Damage(ProjectDefinition project, ConfigurationSnapshot c, string kind)
    {
        var id = Guid.NewGuid().ToString("N");
        var path = Path.Combine(CloudMapping.StateRoot(project, c), "cloud-transactions", id + ".json");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, kind == "broken-json" ? "broken" : JsonSerializer.Serialize(new CloudJournal(
            kind == "unknown-schema" ? 99 : 5, id, CloudMapping.Bind(project, c), "../outside.txt", SyncDirection.Backup,
            null, null, null, true, ProtectionKind.None, "parent", Mapping: CloudMapping.Capture(project, c)), AtomicJson.Options));
        return path;
    }
}
