using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using HarborBackup.Core.Domain;
using HarborBackup.OneDrive.Transport;
using HarborBackup.Windows.Integration;
using HarborBackup.Windows.Persistence;
using HarborBackup.Windows.Persistence.Configuration;
using static HarborBackup.Windows.Tests.CloudTransportTests;

namespace HarborBackup.Windows.Tests;

[TestClass]
[DoNotParallelize]
public sealed class ProjectFolderRenameTransferTests
{
    public TestContext TestContext { get; set; } = null!;

    [TestMethod]
    [DataRow("parent", "local", SyncDirection.Backup, false, true, false)]
    [DataRow("parent", "local", SyncDirection.Pull, false, true, false)]
    [DataRow("parent", "cloud", SyncDirection.Backup, false, true, false)]
    [DataRow("parent", "cloud", SyncDirection.Pull, false, true, false)]
    [DataRow("parent", "both", SyncDirection.Backup, false, true, false)]
    [DataRow("parent", "both", SyncDirection.Pull, false, true, false)]
    [DataRow("root", "local", SyncDirection.Backup, false, true, false)]
    [DataRow("root", "local", SyncDirection.Pull, false, true, false)]
    [DataRow("root", "cloud", SyncDirection.Backup, false, true, false)]
    [DataRow("root", "cloud", SyncDirection.Pull, false, true, false)]
    [DataRow("root", "both", SyncDirection.Backup, false, true, false)]
    [DataRow("root", "both", SyncDirection.Pull, false, true, false)]
    [DataRow("parent", "both", SyncDirection.Backup, true, true, false)]
    [DataRow("parent", "both", SyncDirection.Pull, true, true, false)]
    [DataRow("parent", "both", SyncDirection.Backup, false, false, false)]
    [DataRow("parent", "both", SyncDirection.Pull, false, false, false)]
    [DataRow("parent", "both", SyncDirection.Backup, false, true, true)]
    [DataRow("parent", "both", SyncDirection.Pull, false, true, true)]
    public async Task PureRenameKeepsEveryBusinessBodyWithoutUploadOrDownload(string shape, string side,
        SyncDirection direction, bool coldCache, bool keepFolderId, bool changeFileEtags)
    {
        using var s = await Scenario.CreateAsync(shape);
        var originalId = s.Project.ProjectId;
        var originalBinding = CloudMapping.Bind(s.Project, s.Config);
        var originalBodies = s.CloudBodies();
        var protection = s.ProtectionFiles();
        var copies = s.Drive.Copies;
        await s.RenameAsync(side, keepFolderId, changeFileEtags);
        if (coldCache) s.ClearCache();
        Assert.AreEqual(originalId, s.Project.ProjectId);
        Assert.AreNotEqual(originalBinding, CloudMapping.Bind(s.Project, s.Config));
        Assert.AreEqual(1, s.Project.BindingVersion);
        Assert.IsTrue(s.Config.RetiredMappings.Any(m => m.Binding == originalBinding));

        // Start a new runtime after saving: no in-memory session or scan survives the rename.
        var runtime = s.Runtime();
        var beforePreview = s.Capture();
        var preview = await runtime.PreviewAsync(direction, s.Project.ProjectId, false, default,
            prepareUpload: direction == SyncDirection.Backup);
        var previewEvidence = s.Evidence("preview", beforePreview, preview.Projects.Single().Metrics,
            preview.Projects.Single().Plans.Select(p => new Decision(p.Path, p.Decision.Action.ToString(), p.Decision.Reason)).ToArray());
        Assert.AreEqual(0, preview.ExitCode, JsonSerializer.Serialize(preview));
        Assert.AreEqual(28, preview.Projects.Single().Plans.Count(p => p.Decision.Action == FileAction.Equal));
        Assert.IsFalse(preview.Projects.Single().Plans.Any(p => p.Decision.Action == FileAction.Copy));
        AssertNoBusinessTransfer(previewEvidence);
        AssertNoProtectionOrUnknownTransfer(previewEvidence);

        var beforeExecution = s.Capture();
        var execution = await ExecuteAsync(runtime, direction, s.Project.ProjectId, preview);
        var executionEvidence = s.Evidence("execution", beforeExecution, execution.Projects.Single().Metrics,
            execution.Projects.Single().Files.Select(p => new Decision(p.Path, p.Action.ToString(), p.Error)).ToArray());
        WriteEvidence($"pure-{shape}-{side}-{direction}-cold{coldCache}-folderid{keepFolderId}-etag{changeFileEtags}", s,
            [previewEvidence, executionEvidence]);
        Assert.AreEqual(0, execution.ExitCode, JsonSerializer.Serialize(execution));
        AssertNoBusinessTransfer(executionEvidence);
        AssertNoProtectionOrUnknownTransfer(executionEvidence);
        Assert.IsFalse(execution.Projects.Single().Files.Any(p => p.Changed));
        Assert.AreEqual(copies, s.Drive.Copies);
        CollectionAssert.AreEquivalent(protection, s.ProtectionFiles(), "Pure renaming must not create protection bodies.");
        CollectionAssert.AreEquivalent(originalBodies.Keys.ToArray(), s.CloudBodies().Keys.ToArray());
        foreach (var pair in originalBodies) Assert.AreEqual(pair.Value, s.CloudBodies()[pair.Key]);
        s.AssertContent(s.Payloads);
    }

    [TestMethod]
    [DataRow(SyncDirection.Backup)]
    [DataRow(SyncDirection.Pull)]
    public async Task RenamedMappingTransfersOnlyTheOneFileWithDifferentContent(SyncDirection direction)
    {
        using var s = await Scenario.CreateAsync("parent");
        const string changedPath = "business.json";
        var changed = Encoding.UTF8.GetBytes("{\"value\":\"changed after the initial backup\"}");
        if (direction == SyncDirection.Pull)
        {
            // Publish trusted changed cloud evidence through the normal uploader, then retain
            // the original local bytes to model a genuinely different file at the renamed location.
            File.WriteAllBytes(Path.Combine(s.Project.Source, changedPath), changed);
            var publish = await s.Runtime().ExecuteAsync(SyncDirection.Backup, s.Project.ProjectId, false, false, null, default);
            Assert.AreEqual(0, publish.ExitCode, JsonSerializer.Serialize(publish));
            File.WriteAllBytes(Path.Combine(s.Project.Source, changedPath), s.Payloads[changedPath]);
        }
        await s.RenameAsync("both", keepFolderId: true, changeFileEtags: false);
        if (direction == SyncDirection.Backup) File.WriteAllBytes(Path.Combine(s.Project.Source, changedPath), changed);
        var runtime = s.Runtime(); var beforePreview = s.Capture();
        var preview = await runtime.PreviewAsync(direction, s.Project.ProjectId, false, default,
            prepareUpload: direction == SyncDirection.Backup);
        var previewEvidence = s.Evidence("preview", beforePreview, preview.Projects.Single().Metrics,
            preview.Projects.Single().Plans.Select(p => new Decision(p.Path, p.Decision.Action.ToString(), p.Decision.Reason)).ToArray());
        Assert.AreEqual(0, preview.ExitCode, JsonSerializer.Serialize(preview));
        CollectionAssert.AreEqual(new[] { changedPath }, preview.Projects.Single().Plans.Where(p => p.Decision.Action == FileAction.Copy).Select(p => p.Path).ToArray());
        AssertNoBusinessTransfer(previewEvidence);
        var beforeExecution = s.Capture();
        var execution = await ExecuteAsync(runtime, direction, s.Project.ProjectId, preview);
        var evidence = s.Evidence("execution", beforeExecution, execution.Projects.Single().Metrics,
            execution.Projects.Single().Files.Select(p => new Decision(p.Path, p.Action.ToString(), p.Error)).ToArray());
        WriteEvidence("one-changed-file-" + direction, s, [previewEvidence, evidence]);
        Assert.AreEqual(0, execution.ExitCode, JsonSerializer.Serialize(execution));
        CollectionAssert.AreEqual(new[] { changedPath }, execution.Projects.Single().Files.Where(p => p.Changed).Select(p => p.Path).ToArray());
        Assert.AreEqual(direction == SyncDirection.Backup ? 1 : 0, evidence.BusinessUploads);
        Assert.AreEqual(direction == SyncDirection.Pull ? 1 : 0, evidence.BusinessDownloads);
        Assert.AreEqual(direction == SyncDirection.Backup ? changed.LongLength : 0L, evidence.BusinessUploadedBytes);
        Assert.AreEqual(direction == SyncDirection.Pull ? changed.LongLength : 0L, evidence.BusinessDownloadedBytes);
        var expected = s.Payloads.ToDictionary(p => p.Key, p => p.Value); expected[changedPath] = changed;
        s.AssertContent(expected);
    }

    [TestMethod]
    public async Task ChangingOnlyConfigurationToAnEmptyCloudTargetUploadsTheNewTarget()
    {
        using var s = await Scenario.CreateAsync("parent");
        var originals = s.CloudBodies();
        s.SaveMapping(s.Project.Source, "MyProjects/Demo", null);
        var runtime = s.Runtime(); var beforePreview = s.Capture();
        var preview = await runtime.PreviewAsync(SyncDirection.Backup, s.Project.ProjectId, false, default, prepareUpload: true);
        var previewEvidence = s.Evidence("preview", beforePreview, preview.Projects.Single().Metrics,
            preview.Projects.Single().Plans.Select(p => new Decision(p.Path, p.Decision.Action.ToString(), p.Decision.Reason)).ToArray());
        Assert.AreEqual(0, preview.ExitCode, JsonSerializer.Serialize(preview));
        Assert.AreEqual(28, preview.Projects.Single().Plans.Count(p => p.Decision.Action == FileAction.Copy));
        AssertNoBusinessTransfer(previewEvidence);
        var beforeExecution = s.Capture();
        var execution = await ExecuteAsync(runtime, SyncDirection.Backup, s.Project.ProjectId, preview);
        s.BusinessRootId = (await s.ResolveCloudAsync("MyProjects/Demo")).Id;
        var evidence = s.Evidence("execution", beforeExecution, execution.Projects.Single().Metrics,
            execution.Projects.Single().Files.Select(p => new Decision(p.Path, p.Action.ToString(), p.Error)).ToArray());
        WriteEvidence("configuration-only-empty-target", s, [previewEvidence, evidence]);
        Assert.AreEqual(0, execution.ExitCode, JsonSerializer.Serialize(execution));
        Assert.AreEqual(28, evidence.BusinessUploads);
        Assert.AreEqual(s.Payloads.Values.Sum(p => p.LongLength), evidence.BusinessUploadedBytes);
        Assert.AreEqual(0, evidence.BusinessDownloads);
        foreach (var pair in originals) Assert.AreEqual(pair.Value, Convert.ToHexStringLower(SHA256.HashData(s.Drive.Items[pair.Key].Bytes)));
        s.AssertContent(s.Payloads);
    }

    [TestMethod]
    public async Task CopiedCloudFilesWithNewIdentitiesNeedReviewAndExplicitUploadRetransmitsThem()
    {
        using var s = await Scenario.CreateAsync("parent");
        var originalBodies = s.CloudBodies();
        var parent = await s.Drive.FolderAsync("drive-root", "MyProjects", default);
        var copiedRoot = await s.Drive.FolderAsync(parent.Id, "Demo", default);
        await s.CopyTreeAsync(s.BusinessRootId, copiedRoot.Id);
        s.BusinessRootId = copiedRoot.Id;
        s.SaveMapping(s.Project.Source, "MyProjects/Demo", copiedRoot.Id);
        var runtime = s.Runtime(); var beforeReadonly = s.Capture();
        var readonlyPreview = await runtime.PreviewAsync(SyncDirection.Backup, s.Project.ProjectId, false, default);
        var readonlyEvidence = s.Evidence("readonly-preview", beforeReadonly, readonlyPreview.Projects.Single().Metrics,
            readonlyPreview.Projects.Single().Plans.Select(p => new Decision(p.Path, p.Decision.Action.ToString(), p.Decision.Reason)).ToArray());
        Assert.AreEqual(2, readonlyPreview.ExitCode, JsonSerializer.Serialize(readonlyPreview));
        Assert.AreEqual(28, readonlyPreview.Projects.Single().Plans.Count(p => p.Decision.Reason == "CloudMetadataRequired"));
        AssertNoBusinessTransfer(readonlyEvidence);
        var beforeExplicit = s.Capture();
        var explicitPreview = await runtime.PreviewAsync(SyncDirection.Backup, s.Project.ProjectId, false, default, prepareUpload: true);
        var explicitEvidence = s.Evidence("explicit-upload-preview", beforeExplicit, explicitPreview.Projects.Single().Metrics,
            explicitPreview.Projects.Single().Plans.Select(p => new Decision(p.Path, p.Decision.Action.ToString(), p.Decision.Reason)).ToArray());
        Assert.AreEqual(0, explicitPreview.ExitCode, JsonSerializer.Serialize(explicitPreview));
        Assert.AreEqual(28, explicitPreview.Projects.Single().Plans.Count(p => p.Decision.Action == FileAction.Copy));
        AssertNoBusinessTransfer(explicitEvidence);
        var beforeExecution = s.Capture();
        var execution = await ExecuteAsync(runtime, SyncDirection.Backup, s.Project.ProjectId, explicitPreview);
        var evidence = s.Evidence("execution", beforeExecution, execution.Projects.Single().Metrics,
            execution.Projects.Single().Files.Select(p => new Decision(p.Path, p.Action.ToString(), p.Error)).ToArray());
        WriteEvidence("copied-cloud-files-new-identities", s, [readonlyEvidence, explicitEvidence, evidence]);
        Assert.AreEqual(0, execution.ExitCode, JsonSerializer.Serialize(execution));
        Assert.AreEqual(28, evidence.BusinessUploads);
        Assert.AreEqual(s.Payloads.Values.Sum(p => p.LongLength), evidence.BusinessUploadedBytes);
        Assert.AreEqual(0, evidence.BusinessDownloads);
        Assert.HasCount(28, s.ProtectionFiles(), "Every replaced copied body must be protected before the explicit upload.");
        foreach (var pair in originalBodies) Assert.AreEqual(pair.Value, Convert.ToHexStringLower(SHA256.HashData(s.Drive.Items[pair.Key].Bytes)));
        s.AssertContent(s.Payloads);
    }

    private static Task<BackupReport> ExecuteAsync(CloudBackupRuntime runtime, SyncDirection direction, string id, PreviewReport preview) =>
        runtime.ExecuteAsync(direction, id, false, false, null, default,
            confirmedPull: direction == SyncDirection.Pull ? preview.PullSelection : null,
            confirmedUpload: direction == SyncDirection.Backup ? preview.UploadSelection : null);

    private static void AssertNoBusinessTransfer(StageEvidence evidence)
    {
        Assert.AreEqual(0, evidence.BusinessUploads, evidence.Stage);
        Assert.AreEqual(0L, evidence.BusinessUploadedBytes, evidence.Stage);
        Assert.AreEqual(0, evidence.BusinessDownloads, evidence.Stage);
        Assert.AreEqual(0L, evidence.BusinessDownloadedBytes, evidence.Stage);
    }

    private static void AssertNoProtectionOrUnknownTransfer(StageEvidence evidence)
    {
        Assert.AreEqual(0, evidence.ProtectionUploads, evidence.Stage);
        Assert.AreEqual(0L, evidence.ProtectionUploadedBytes, evidence.Stage);
        Assert.AreEqual(0, evidence.ProtectionDownloads, evidence.Stage);
        Assert.AreEqual(0L, evidence.ProtectionDownloadedBytes, evidence.Stage);
        Assert.AreEqual(0, evidence.UnclassifiedUploads, evidence.Stage);
        Assert.AreEqual(0, evidence.UnclassifiedDownloads, evidence.Stage);
    }

    private void WriteEvidence(string name, Scenario scenario, StageEvidence[] stages)
    {
        var report = new { scenario = name, files = scenario.Payloads.Count,
            businessBytes = scenario.Payloads.Values.Sum(p => p.LongLength), stages };
        var json = JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true });
        TestContext.WriteLine(json);
        if (Environment.GetEnvironmentVariable("HARBORBACKUP_RENAME_REPORT_DIR") is { Length: > 0 } directory)
        {
            Directory.CreateDirectory(directory);
            File.WriteAllText(Path.Combine(directory, name + ".json"), json);
        }
    }

    private sealed record Decision(string Path, string Action, string? Reason);
    private sealed record StageEvidence(string Stage, int BusinessUploads, long BusinessUploadedBytes,
        int BusinessDownloads, long BusinessDownloadedBytes, int ProtectionUploads, long ProtectionUploadedBytes,
        int ProtectionDownloads, long ProtectionDownloadedBytes, int MetadataUploads, long MetadataUploadedBytes,
        int MetadataDownloads, long MetadataDownloadedBytes, int UnclassifiedUploads, int UnclassifiedDownloads,
        long ReportedHashBytes, long ReportedHashedFiles, long CachedFiles, int DirectoryEnumerations, Decision[] Decisions);
    private sealed record Counter(int Transfers, int Enumerations);

    private sealed class Scenario : IDisposable
    {
        private readonly Fixture fixture = new(true);
        private readonly string shape;
        private readonly string localParent;
        private string outerCloudId = "";
        private readonly ConfigurationEditor editor;
        private readonly DataPaths data;
        public FakeDrive Drive => fixture.Drive;
        public ConfigurationSnapshot Config { get; private set; } = null!;
        public ProjectDefinition Project => Config.Projects.Single();
        public string BusinessRootId { get; set; } = "";
        public Dictionary<string, byte[]> Payloads { get; } = CreatePayloads();

        private Scenario(string shape)
        {
            this.shape = shape;
            var device = fixture.Device("A");
            data = DataPaths.FromState(device.C.Preview.StateDirectory);
            localParent = Path.Combine(fixture.Root, "A", "PersonalProjects");
            Directory.CreateDirectory(data.Config);
            File.WriteAllText(Path.Combine(data.Config, "onedrive-account.json"), JsonSerializer.Serialize(fixture.Account, AtomicJson.Options));
            editor = new(new(data.Projects, data.BackupSettings), device.C.Preview.DeviceId);
        }

        public static async Task<Scenario> CreateAsync(string shape)
        {
            var scenario = new Scenario(shape);
            try { await scenario.InitializeAsync(); return scenario; }
            catch { scenario.Dispose(); throw; }
        }

        private async Task InitializeAsync()
        {
            var outer = await Drive.FolderAsync("drive-root", "PersonalProjects", default);
            outerCloudId = outer.Id;
            var target = shape == "parent" ? await Drive.FolderAsync(outer.Id, "Demo", default) : outer;
            BusinessRootId = target.Id;
            var source = shape == "parent" ? Path.Combine(localParent, "Demo") : localParent;
            foreach (var pair in Payloads)
            {
                var path = Path.Combine(source, pair.Key);
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                File.WriteAllBytes(path, pair.Value);
                File.SetLastWriteTimeUtc(path, new DateTime(2026, 10, 1, 0, 0, 0, DateTimeKind.Utc));
            }
            var draft = editor.Load();
            draft.Projects["folders"] = new JsonArray(new JsonObject
            {
                ["id"] = DeviceIdentity.Digest("rename-project-" + fixture.Account.RepositoryId),
                ["source"] = source, ["destination"] = shape == "parent" ? "PersonalProjects/Demo" : "PersonalProjects",
                ["cloudFolderId"] = target.Id
            });
            editor.Save(draft, ConfigurationPart.Projects);
            Config = editor.Validate(editor.Load());
            var initial = await Runtime().ExecuteAsync(SyncDirection.Backup, Project.ProjectId, false, false, null, default);
            Assert.AreEqual(0, initial.ExitCode, JsonSerializer.Serialize(initial));
            Assert.IsFalse(CloudMapping.NeedsFirstOperation(Project, Config));
            AssertContent(Payloads);
        }

        public CloudBackupRuntime Runtime() => new(Config, () => Drive);

        public async Task RenameAsync(string side, bool keepFolderId, bool changeFileEtags)
        {
            var source = Project.Source;
            var destination = Project.Destination;
            if (side is "local" or "both")
            {
                var next = Path.Combine(fixture.Root, "A", "MyProjects");
                Directory.Move(localParent, next);
                source = shape == "parent" ? Path.Combine(next, "Demo") : next;
            }
            if (side is "cloud" or "both")
            {
                await Drive.MoveAsync((await Drive.GetAsync(outerCloudId, default)), "drive-root", "MyProjects", null, default);
                destination = shape == "parent" ? "MyProjects/Demo" : "MyProjects";
            }
            if (changeFileEtags)
                foreach (var id in TreeIds(BusinessRootId).Where(id => !Drive.Items[id].Item.Folder))
                {
                    var stored = Drive.Items[id];
                    Drive.Items[id] = (stored.Item with { ETag = Guid.NewGuid().ToString("N") }, stored.Parent, stored.Bytes);
                }
            SaveMapping(source, destination, keepFolderId ? BusinessRootId : null);
        }

        public void SaveMapping(string source, string destination, string? folderId)
        {
            var draft = editor.Load(); var entry = draft.Projects["folders"]![0]!.AsObject();
            entry["source"] = source; entry["destination"] = destination;
            if (folderId is null) entry.Remove("cloudFolderId"); else entry["cloudFolderId"] = folderId;
            editor.Save(draft, ConfigurationPart.Projects);
            Config = editor.Validate(editor.Load());
        }

        public void ClearCache()
        {
            var cache = Path.GetFullPath(data.Cache);
            if (!cache.StartsWith(Path.GetFullPath(fixture.Root) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("Cache cleanup escaped the isolated fixture.");
            if (Directory.Exists(cache)) Directory.Delete(cache, true);
        }

        public Counter Capture() => new(Drive.BodyTransfers.Count, Drive.Enumerations);

        public StageEvidence Evidence(string stage, Counter before, ReadMetrics metrics, Decision[] decisions)
        {
            var ids = TreeIds(BusinessRootId);
            var protectionIds = ProtectionIds();
            var metadataRoot = FindManagementRoot((fixture.Account.Folders ?? new CloudFolders()).Metadata);
            var metadataIds = metadataRoot is null ? new HashSet<string>() : TreeIds(metadataRoot);
            // The repository marker is control metadata too. Identity and ancestry, rather than
            // extensions such as .json or display folder names, classify every completed body.
            foreach (var marker in Drive.Items.Values.Where(p => p.Parent == fixture.Account.RootId && p.Item.Name == "repository.json"))
                metadataIds.Add(marker.Item.Id);
            var transfers = Drive.BodyTransfers.Skip(before.Transfers).ToArray();
            var uploads = transfers.Where(t => t.Direction == "Upload" && ids.Contains(t.ItemId)).ToArray();
            var downloads = transfers.Where(t => t.Direction == "Download" && ids.Contains(t.ItemId)).ToArray();
            var protectionUploads = transfers.Where(t => t.Direction == "Upload" && protectionIds.Contains(t.ItemId)).ToArray();
            var protectionDownloads = transfers.Where(t => t.Direction == "Download" && protectionIds.Contains(t.ItemId)).ToArray();
            var metadataUploads = transfers.Where(t => t.Direction == "Upload" && metadataIds.Contains(t.ItemId)).ToArray();
            var metadataDownloads = transfers.Where(t => t.Direction == "Download" && metadataIds.Contains(t.ItemId)).ToArray();
            var unknown = transfers.Where(t => !ids.Contains(t.ItemId) && !protectionIds.Contains(t.ItemId) && !metadataIds.Contains(t.ItemId)).ToArray();
            return new(stage, uploads.Length, uploads.Sum(t => t.Bytes), downloads.Length, downloads.Sum(t => t.Bytes),
                protectionUploads.Length, protectionUploads.Sum(t => t.Bytes), protectionDownloads.Length, protectionDownloads.Sum(t => t.Bytes),
                metadataUploads.Length, metadataUploads.Sum(t => t.Bytes), metadataDownloads.Length, metadataDownloads.Sum(t => t.Bytes),
                unknown.Count(t => t.Direction == "Upload"), unknown.Count(t => t.Direction == "Download"),
                metrics.HashBytes, metrics.HashedFiles, metrics.CachedFiles, Drive.Enumerations - before.Enumerations, decisions);
        }

        public Dictionary<string, string> CloudBodies() => TreeIds(BusinessRootId).Where(id => !Drive.Items[id].Item.Folder)
            .ToDictionary(id => id, id => Convert.ToHexStringLower(SHA256.HashData(Drive.Items[id].Bytes)));

        public string[] ProtectionFiles() => ProtectionIds().Where(id => !Drive.Items[id].Item.Folder).Order().ToArray();

        private HashSet<string> ProtectionIds()
        {
            var folders = fixture.Account.Folders ?? new CloudFolders();
            return new[] { folders.Archives, folders.Conflicts }.Select(FindManagementRoot).Where(id => id is not null)
                .SelectMany(id => TreeIds(id!)).ToHashSet(StringComparer.Ordinal);
        }

        private string? FindManagementRoot(string path)
        {
            var current = fixture.Account.RootId!;
            foreach (var part in path.Split('/'))
            {
                var next = Drive.Items.Values.SingleOrDefault(p => p.Parent == current && p.Item.Name == part);
                if (next.Item is null) return null;
                current = next.Item.Id;
            }
            return current;
        }

        private HashSet<string> TreeIds(string root)
        {
            var ids = new HashSet<string>(StringComparer.Ordinal) { root };
            var queue = new Queue<string>(); queue.Enqueue(root);
            while (queue.TryDequeue(out var parent))
                foreach (var item in Drive.Items.Values.Where(p => p.Parent == parent))
                    if (ids.Add(item.Item.Id)) queue.Enqueue(item.Item.Id);
            return ids;
        }

        public async Task<RemoteItem> ResolveCloudAsync(string path)
        {
            var current = await Drive.GetAsync("drive-root", default);
            foreach (var part in path.Split('/')) current = (await Drive.FindAsync(current.Id, part, default))!;
            return current;
        }

        public async Task CopyTreeAsync(string source, string destination)
        {
            foreach (var entry in Drive.Items.Values.Where(p => p.Parent == source).ToArray())
                if (entry.Item.Folder)
                {
                    var folder = await Drive.FolderAsync(destination, entry.Item.Name, default);
                    await CopyTreeAsync(entry.Item.Id, folder.Id);
                }
                else await Drive.CopyAsync(entry.Item, destination, entry.Item.Name, default);
        }

        public void AssertContent(IReadOnlyDictionary<string, byte[]> expected)
        {
            foreach (var pair in expected)
            {
                var local = File.ReadAllBytes(Path.Combine(Project.Source, pair.Key));
                CollectionAssert.AreEqual(pair.Value, local, pair.Key);
                var current = BusinessRootId;
                foreach (var part in pair.Key.Split('/'))
                    current = Drive.Items.Values.Single(p => p.Parent == current && p.Item.Name == part).Item.Id;
                CollectionAssert.AreEqual(pair.Value, Drive.Items[current].Bytes, pair.Key);
            }
            Assert.HasCount(expected.Count, CloudBodies());
        }

        private static Dictionary<string, byte[]> CreatePayloads()
        {
            var files = new Dictionary<string, byte[]>(StringComparer.Ordinal)
            {
                ["empty.bin"] = [], ["business.json"] = Encoding.UTF8.GetBytes("{\"value\":\"original business data\"}"),
                ["report.txt"] = Encoding.UTF8.GetBytes("original report"),
                ["binary.dat"] = Enumerable.Range(0, 256).Select(i => (byte)i).ToArray()
            };
            for (var i = 0; i < 24; i++)
                files[$"nested/group-{i % 3}/level-{i % 2}/file-{i:D2}.txt"] = Encoding.UTF8.GetBytes($"synthetic payload {i:D2}: " + new string('x', i * 7));
            return files;
        }

        public void Dispose() => fixture.Dispose();
    }
}
