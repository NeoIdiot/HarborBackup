using System.Text.Json;
using System.Text.Json.Nodes;
using HarborBackup.Core.Domain;
using HarborBackup.Windows.Integration;
using HarborBackup.Windows.Persistence;
using HarborBackup.Windows.Persistence.Configuration;
using static HarborBackup.Windows.Tests.CloudTransportTests;

namespace HarborBackup.Windows.Tests;

[TestClass]
[DoNotParallelize]
public sealed class ExclusionContinuityTests
{
    private static ConfigurationEditor Persist(ConfigurationSnapshot config, ProjectDefinition project, params CloudMapping[] retired)
    {
        var data = DataPaths.FromState(config.Preview.StateDirectory);
        Directory.CreateDirectory(data.Config);
        File.WriteAllText(Path.Combine(data.Config, "onedrive-account.json"), JsonSerializer.Serialize(config.CloudAccount, AtomicJson.Options));
        var entry = new JsonObject { ["id"] = project.TaskId, ["source"] = project.Source, ["destination"] = project.Destination };
        if (project.MappingVersion is not null) entry["mappingVersion"] = project.MappingVersion;
        if (project.BindingVersion != 0) entry["bindingVersion"] = project.BindingVersion;
        if (project.CloudFolderId is not null) entry["cloudFolderId"] = project.CloudFolderId;
        entry["excludedFiles"] = JsonSerializer.SerializeToNode(project.ExcludedFiles);
        entry["excludedDirectories"] = JsonSerializer.SerializeToNode(project.ExcludedDirectories);
        var projects = new JsonObject { ["schemaVersion"] = 2, ["folders"] = new JsonArray(entry),
            ["retiredMappings"] = JsonSerializer.SerializeToNode(retired, AtomicJson.Options) };
        var paths = new RuntimePaths(Path.Combine(data.Config, "projects.json"), Path.Combine(data.Config, "backup-settings.json"));
        File.WriteAllText(paths.ProjectConfig, projects.ToJsonString());
        File.WriteAllText(paths.SystemConfig, "{\"schemaVersion\":1,\"sync\":{\"enabled\":true}}");
        return new(paths, config.Preview.DeviceId);
    }

    private static ConfigurationSnapshot Load(ConfigurationEditor editor) => editor.Validate(editor.Load());
    private static string Fingerprint(ProjectDefinition p) => DeviceIdentity.Fingerprint(p.ExcludedDirectories, p.ExcludedFiles, "OneDrive/" + p.Destination + "/" + p.CloudFolderId);
    private static ProjectDefinition Legacy(ProjectDefinition p) => p with { Target = "OneDrive/" + p.Destination, Fingerprint = Fingerprint(p), MappingVersion = Guid.NewGuid().ToString("N") };
    private static Dictionary<string, string> Evidence(string root) => Directory.GetFiles(root, "*", SearchOption.AllDirectories).ToDictionary(p => p, File.ReadAllText);
    private static void Unchanged(Dictionary<string, string> evidence)
    { foreach (var pair in evidence) Assert.AreEqual(pair.Value, File.ReadAllText(pair.Key), pair.Key); }

    [TestMethod]
    [DataRow(SyncDirection.Backup)]
    [DataRow(SyncDirection.Pull)]
    public async Task RuleEditKeepsReadyStateAndOnlyTransfersChangedIncludedFiles(SyncDirection direction)
    {
        using var f = new Fixture(true); var a = f.Device("A");
        var editor = Persist(a.C, a.P with { BindingVersion = 1, MappingVersion = Guid.NewGuid().ToString("N") });
        var c = Load(editor); var p = c.Projects.Single();
        foreach (var name in new[] { "a.txt", "same.txt", "~$draft.docx" }) File.WriteAllText(Path.Combine(p.Source, name), "base");
        await Apply(new(p, c, f.Account, f.Drive)); CloudMapping.MarkReady(p, c);
        var binding = CloudMapping.Bind(p, c); var evidence = Evidence(c.Preview.StateDirectory);
        var locations = f.Drive.Items.Values.Count(v => f.Drive.Items.TryGetValue(v.Parent, out var parent) && parent.Item.Name == "locations");
        var draft = editor.Load(); draft.Projects["builtInExclusions"] = new JsonArray("word-locks-v1");
        editor.Save(draft, ConfigurationPart.Projects);
        var next = Load(editor); var project = next.Projects.Single();
        Assert.AreEqual(binding, CloudMapping.Bind(project, next));
        Assert.IsFalse(CloudMapping.NeedsFirstOperation(project, next)); Unchanged(evidence);
        if (direction == SyncDirection.Backup) File.WriteAllText(Path.Combine(p.Source, "a.txt"), "updated local");
        else
        {
            var b = f.Device("B"); await Apply(b.S, SyncDirection.Pull);
            File.WriteAllText(Path.Combine(b.P.Source, "a.txt"), "updated cloud"); await Apply(b.S);
        }
        File.WriteAllText(Path.Combine(p.Source, "~$draft.docx"), "ignored local change");
        var runtime = new CloudBackupRuntime(next, () => f.Drive);
        var preview = await runtime.PreviewAsync(direction, null, false, default);
        Assert.AreEqual(FileAction.Copy, preview.Projects.Single().Plans.Single(v => v.Path == "a.txt").Decision.Action);
        Assert.AreEqual(FileAction.Equal, preview.Projects.Single().Plans.Single(v => v.Path == "same.txt").Decision.Action);
        Assert.IsFalse(preview.Projects.Single().Plans.Any(v => v.Path == "~$draft.docx"));
        var result = await runtime.ExecuteAsync(direction, null, false, false, null, default, automatic: direction == SyncDirection.Backup);
        Assert.AreEqual(0, result.ExitCode, JsonSerializer.Serialize(result));
        CollectionAssert.AreEqual(new[] { "a.txt" }, result.Projects.Single().Files.Where(v => v.Changed).Select(v => v.Path).ToArray());
        Assert.AreEqual("ignored local change", File.ReadAllText(Path.Combine(p.Source, "~$draft.docx")));
        var root = (await f.Drive.FindAsync("drive-root", "Documents", default))!;
        var ignored = (await f.Drive.FindAsync(root.Id, "~$draft.docx", default))!;
        Assert.AreEqual("base", System.Text.Encoding.UTF8.GetString(f.Drive.Items[ignored.Id].Bytes));
        if (direction == SyncDirection.Backup)
            Assert.AreEqual(locations, f.Drive.Items.Values.Count(v => f.Drive.Items.TryGetValue(v.Parent, out var parent) && parent.Item.Name == "locations"));
    }

    [TestMethod]
    [DataRow(false, false)]
    [DataRow(false, true)]
    [DataRow(true, false)]
    [DataRow(true, true)]
    public async Task RejoiningRemovedLegacyProjectAlwaysRequiresNewConfirmation(bool versionless, bool changeRules)
    {
        using var f = new Fixture(); var a = f.Device("A"); var old = Legacy(a.P);
        if (versionless) old = old with { MappingVersion = null };
        var c = a.C with { Projects = [old] };
        File.WriteAllText(Path.Combine(old.Source, "a.txt"), "base");
        await Apply(new(old, c, f.Account, f.Drive)); CloudMapping.MarkReady(old, c);
        var mapping = CloudMapping.Capture(old, c); var editor = Persist(c, old, mapping);
        // Reproduce a removal saved by 0.9.13: only the old recovery snapshot remains.
        var configPath = Path.Combine(DataPaths.FromState(c.Preview.StateDirectory).Config, "projects.json");
        var document = JsonNode.Parse(File.ReadAllText(configPath))!;
        document["folders"] = new JsonArray(); File.WriteAllText(configPath, document.ToJsonString());
        var evidence = Evidence(c.Preview.StateDirectory);
        var draft = editor.Load();
        draft.Projects["folders"]!.AsArray().Add(new JsonObject
        {
            ["id"] = old.ProjectId, ["source"] = old.Source, ["destination"] = old.Destination,
            ["excludedFiles"] = changeRules ? new JsonArray("*.tmp") : new JsonArray()
        });
        editor.Save(draft, ConfigurationPart.Projects);
        var joined = Load(editor); var project = joined.Projects.Single();
        Assert.IsNotNull(project.MappingVersion); Assert.AreNotEqual(old.MappingVersion, project.MappingVersion);
        Assert.IsTrue(CloudMapping.NeedsFirstOperation(project, joined));
        var read = await new CloudBackupSession(project, joined, f.Account, f.Drive).ReadAsync(false, default);
        Assert.IsNull(read.Snapshot.Files.Single().Baseline);
        var automatic = await new CloudBackupRuntime(joined, () => f.Drive).ExecuteAsync(SyncDirection.Backup, null, false, false, null, default, automatic: true);
        Assert.AreEqual("MappingAwaitingOperation", automatic.Projects.Single().Issues.Single().Code);
        Unchanged(evidence);
    }

    [TestMethod]
    public async Task AlreadyResetLegacyMappingRecoversBaselineAndPreservesItThroughSaveAndReinclude()
    {
        using var f = new Fixture(true); var a = f.Device("A"); var old = Legacy(a.P);
        foreach (var name in new[] { "a.txt", "~$draft.docx" }) File.WriteAllText(Path.Combine(old.Source, name), "base");
        var original = a.C with { Projects = [old] };
        await Apply(new(old, original, f.Account, f.Drive)); CloudMapping.MarkReady(old, original);
        var oldMapping = CloudMapping.Capture(old, original);
        var reset = old with { ExcludedFiles = ["~$*.docx"], MappingVersion = Guid.NewGuid().ToString("N") };
        reset = reset with { Fingerprint = Fingerprint(reset) };
        var resetConfig = original with { Projects = [reset], RetiredMappings = [oldMapping] };
        Assert.IsTrue(CloudMapping.NeedsFirstOperation(reset, resetConfig)); // The 0.9.13 behavior being repaired.
        var editor = Persist(resetConfig, reset, oldMapping);
        var evidence = Evidence(original.Preview.StateDirectory);
        var continued = Load(editor); var project = continued.Projects.Single();
        Assert.IsFalse(CloudMapping.NeedsFirstOperation(project, continued));
        var read = await new CloudBackupSession(project, continued, f.Account, f.Drive).ReadAsync(false, default);
        Assert.IsNotNull(read.Snapshot.Files.Single().Baseline);
        Unchanged(evidence);
        var b = f.Device("B"); await Apply(b.S, SyncDirection.Pull);
        File.WriteAllText(Path.Combine(b.P.Source, "~$draft.docx"), "cloud edit"); await Apply(b.S);
        var downloads = f.Drive.BusinessDownloads;
        File.WriteAllText(Path.Combine(project.Source, "~$draft.docx"), "local edit");
        File.WriteAllText(Path.Combine(project.Source, "a.txt"), "local next");
        var runtime = new CloudBackupRuntime(continued, () => f.Drive);
        Assert.AreEqual(0, (await runtime.ExecuteAsync(SyncDirection.Backup, null, false, false, null, default, automatic: true)).ExitCode);
        var draft = editor.Load(); draft.Projects["folders"]![0]!["excludedFiles"] = new JsonArray();
        editor.Save(draft, ConfigurationPart.Projects);
        var reopened = Load(editor); var active = reopened.Projects.Single();
        Assert.IsFalse(CloudMapping.NeedsFirstOperation(active, reopened));
        var session = new CloudBackupSession(active, reopened, f.Account, f.Drive);
        var included = await session.ReadAsync(false, default);
        var conflict = included.Snapshot.Files.Single(v => v.RelativePath == "~$draft.docx");
        Assert.IsNotNull(conflict.Baseline);
        Assert.AreEqual("BothChangedSinceBaseline", session.Decide(conflict, included.Heads[conflict.RelativePath], SyncDirection.Backup).Reason);
        var unchanged = included.Snapshot.Files.Single(v => v.RelativePath == "a.txt");
        Assert.AreEqual(FileAction.Equal, session.Decide(unchanged, included.Heads[unchanged.RelativePath], SyncDirection.Backup).Action);
        Unchanged(evidence); Assert.AreEqual(downloads, f.Drive.BusinessDownloads);
    }

    [TestMethod]
    [DataRow("source")]
    [DataRow("destination")]
    [DataRow("folder")]
    [DataRow("account")]
    [DataRow("device")]
    [DataRow("signature")]
    [DataRow("new-version")]
    public async Task ContinuityNeverCrossesDifferentIdentityOrInvalidEvidence(string kind)
    {
        using var f = new Fixture(); var a = f.Device("A"); var old = Legacy(a.P);
        File.WriteAllText(Path.Combine(old.Source, "a.txt"), "base");
        var c = a.C with { Projects = [old] }; await Apply(new(old, c, f.Account, f.Drive)); CloudMapping.MarkReady(old, c);
        var evidence = Evidence(c.Preview.StateDirectory);
        var snapshot = CloudMapping.Capture(old, c);
        var current = old with { BindingVersion = 1, ExcludedFiles = ["*.tmp"], MappingVersion = Guid.NewGuid().ToString("N") };
        current = current with { Fingerprint = Fingerprint(current) };
        if (kind == "source") current = current with { Source = Path.Combine(f.Root, "other") };
        if (kind == "destination") current = current with { Destination = "Other", Target = "OneDrive/Other" };
        if (kind == "folder") current = current with { CloudFolderId = "another-folder" };
        if (kind == "signature") snapshot = snapshot with { Signature = new string('0', 64) };
        if (kind == "new-version") snapshot = CloudMapping.Capture(old with { BindingVersion = 1 }, c);
        var next = c with { Projects = [current], RetiredMappings = [snapshot] };
        if (kind == "account") next = next with { CloudAccount = f.Account with { AccountId = "different" } };
        if (kind == "device") next = next with { Preview = c.Preview with { DeviceId = DeviceIdentity.Digest("different") } };
        Assert.IsTrue(CloudMapping.NeedsFirstOperation(current, next)); Unchanged(evidence);
        if (kind == "signature")
            await Assert.ThrowsExactlyAsync<IOException>(() => new CloudBackupSession(current, next, f.Account, f.Drive).ReadAsync(false, default));
        else Assert.IsEmpty(CloudMapping.PreviousEvidence(current, next).ToArray());
    }

    [TestMethod]
    public void SwitchingAwayAndBackCannotReactivateOlderMatchingPath()
    {
        using var f = new Fixture(); var a = f.Device("A"); var old = Legacy(a.P);
        CloudMapping.MarkReady(old, a.C);
        var elsewhere = old with { Source = Path.Combine(f.Root, "other"), MappingVersion = Guid.NewGuid().ToString("N") };
        var current = old with { BindingVersion = 1, ExcludedFiles = ["*.tmp"], MappingVersion = Guid.NewGuid().ToString("N"), Fingerprint = DeviceIdentity.Digest("new rules") };
        var c = a.C with { Projects = [current], RetiredMappings = [CloudMapping.Capture(old, a.C), CloudMapping.Capture(elsewhere, a.C)] };
        Assert.IsTrue(CloudMapping.NeedsFirstOperation(current, c));
        Assert.IsEmpty(CloudMapping.PreviousEvidence(current, c).ToArray());
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task UpgradeWithoutRuleChangesRetainsVersionedAndVersionlessEvidence(bool versionless)
    {
        using var f = new Fixture(); var a = f.Device("A"); var old = Legacy(a.P);
        if (versionless) old = old with { MappingVersion = null };
        var c = a.C with { Projects = [old] };
        File.WriteAllText(Path.Combine(old.Source, "a.txt"), "base");
        await Apply(new(old, c, f.Account, f.Drive)); CloudMapping.MarkReady(old, c);
        var snapshot = CloudMapping.Capture(old, c);
        var serialized = JsonSerializer.Serialize(snapshot, AtomicJson.Options);
        Assert.DoesNotContain("bindingVersion", serialized);
        JsonSerializer.Deserialize<CloudMapping>(serialized, AtomicJson.Options)!.Validate(c);
        var editor = Persist(c, old); var evidence = Evidence(c.Preview.StateDirectory);
        var upgraded = Load(editor); var project = upgraded.Projects.Single();
        Assert.IsFalse(CloudMapping.NeedsFirstOperation(project, upgraded));
        Assert.IsNotNull((await new CloudBackupSession(project, upgraded, f.Account, f.Drive).ReadAsync(false, default)).Snapshot.Files.Single().Baseline);
        editor.Save(editor.Load(), ConfigurationPart.Projects);
        var reopened = Load(editor);
        Assert.IsFalse(CloudMapping.NeedsFirstOperation(reopened.Projects.Single(), reopened));
        Unchanged(evidence);
    }

    [TestMethod]
    [DataRow("ready")]
    [DataRow("baseline")]
    public async Task DamagedCurrentEvidenceCannotFallBackToOlderValidEvidence(string kind)
    {
        using var f = new Fixture(); var a = f.Device("A"); var old = Legacy(a.P);
        var c = a.C with { Projects = [old] }; File.WriteAllText(Path.Combine(old.Source, "a.txt"), "base");
        await Apply(new(old, c, f.Account, f.Drive)); CloudMapping.MarkReady(old, c);
        var editor = Persist(c, old); var current = Load(editor); var project = current.Projects.Single();
        var root = CloudMapping.StateRoot(project, current);
        var path = kind == "ready" ? Path.Combine(root, "mapping-ready.json") :
            Path.Combine(root, "baselines", DeviceIdentity.Digest("A.TXT") + ".json");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!); File.WriteAllText(path, "broken");
        if (kind == "ready") Assert.IsTrue(CloudMapping.NeedsFirstOperation(project, current));
        else await Assert.ThrowsExactlyAsync<JsonException>(() => new CloudBackupSession(project, current, f.Account, f.Drive).ReadAsync(false, default));
        Assert.AreEqual("broken", File.ReadAllText(path));
    }

    [TestMethod]
    public async Task ReincludeRefreshesBothCachesEvenWhenSizeAndTimestampAreUnchanged()
    {
        using var f = new Fixture(true); var a = f.Device("A");
        var p = a.P with { BindingVersion = 1 }; var c = a.C with { Projects = [p] };
        var path = Path.Combine(p.Source, "a.txt"); File.WriteAllText(path, "base");
        var session = new CloudBackupSession(p, c, f.Account, f.Drive); await Apply(session);
        await session.ReadAsync(false, default);
        Assert.AreEqual(1L, (await session.ReadAsync(false, default)).Snapshot.Metrics.CloudInventoryCacheHits);
        var excluded = p with { ExcludedFiles = ["a.txt"] };
        var skipped = await new CloudBackupSession(excluded, c, f.Account, f.Drive).ReadAsync(false, default);
        Assert.IsEmpty(skipped.Snapshot.Files); Assert.AreEqual(0L, skipped.Snapshot.Metrics.CloudInventoryCacheHits);
        var stamp = File.GetLastWriteTimeUtc(path); File.WriteAllText(path, "edit"); File.SetLastWriteTimeUtc(path, stamp);
        var fresh = await session.ReadAsync(false, default);
        Assert.AreEqual(0L, fresh.Snapshot.Metrics.CloudInventoryCacheHits);
        Assert.AreEqual(4L, fresh.Snapshot.Metrics.HashBytes);
        var file = fresh.Snapshot.Files.Single(); Assert.IsNotNull(file.Baseline);
        Assert.AreEqual("OneSideChangedSinceBaseline", session.Decide(file, fresh.Heads["a.txt"], SyncDirection.Backup).Reason);
    }

    [TestMethod]
    [DataRow(0)]
    [DataRow(1)]
    public async Task RuleEditsPreservePendingJournalAndExplicitRecoveryUsesItsOriginalEvidence(int bindingVersion)
    {
        using var f = new Fixture(); var a = f.Device("A");
        var p = Legacy(a.P) with { BindingVersion = bindingVersion }; var c = a.C with { Projects = [p] };
        File.WriteAllText(Path.Combine(p.Source, "seed.txt"), "base");
        await Apply(new(p, c, f.Account, f.Drive)); CloudMapping.MarkReady(p, c);
        File.WriteAllText(Path.Combine(p.Source, "pending.txt"), "keep"); f.Drive.FailAfterCommit = true;
        await new CloudBackupRuntime(c, () => f.Drive).ExecuteAsync(SyncDirection.Backup, null, false, false, null, default);
        var journal = Directory.GetFiles(c.Preview.StateDirectory, "*.json", SearchOption.AllDirectories)
            .Single(v => Path.GetFileName(Path.GetDirectoryName(v)) == "cloud-transactions");
        var before = File.ReadAllText(journal);
        var editor = Persist(c, p); var draft = editor.Load();
        draft.Projects["folders"]![0]!["excludedFiles"] = new JsonArray("pending.txt");
        editor.Save(draft, ConfigurationPart.Projects);
        var next = Load(editor); Assert.IsFalse(CloudMapping.NeedsFirstOperation(next.Projects.Single(), next));
        File.WriteAllText(Path.Combine(p.Source, "other.txt"), "new");
        var runtime = new CloudBackupRuntime(next, () => f.Drive);
        var result = await runtime.ExecuteAsync(SyncDirection.Backup, null, false, false, null, default);
        Assert.IsTrue(result.Projects.Single().Files.Any(v => v.Path == "other.txt" && v.Changed), JsonSerializer.Serialize(result));
        Assert.AreEqual(before, File.ReadAllText(journal));
        Assert.AreEqual("keep", File.ReadAllText(Path.Combine(p.Source, "pending.txt")));
        var recovered = await runtime.ExecuteAsync(SyncDirection.Backup, "Documents", false, true, null, default);
        Assert.AreEqual(0, recovered.ExitCode, JsonSerializer.Serialize(recovered));
        Assert.IsFalse(File.Exists(journal));
    }
}
