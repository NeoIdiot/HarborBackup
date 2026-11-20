using System.Text.Json;
using HarborBackup.Core.Domain;
using HarborBackup.Windows.FileSystem;
using HarborBackup.Windows.Persistence;
using HarborBackup.Windows.Persistence.Configuration;

namespace HarborBackup.Windows.Integration;

public sealed record CloudAutomaticRetryCandidate(string ProjectId, string Binding, DateTimeOffset? RetryNotBeforeUtc);

/// <summary>A scheduling hint only; execution independently verifies the full authorization and receipts.</summary>
public static class CloudAutomaticRecovery
{
    public static IReadOnlyList<CloudAutomaticRetryCandidate> ReadCandidates(ConfigurationSnapshot config)
    {
        var result = new List<CloudAutomaticRetryCandidate>();
        foreach (var project in config.Projects.Where(p => p.ConfigurationError is null))
        {
            try
            {
                foreach (var task in CloudResumeTaskStore.ReadWaiting(project, config))
                    if ((!task.Automatic || !CloudMapping.NeedsFirstOperation(project, config)) &&
                        (!task.Automatic || task.AutomaticMode == config.GetAutomaticBackupMode(project.ProjectId)))
                        result.Add(new(project.ProjectId, task.Mapping.Binding, task.RetryNotBeforeUtc));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or ArgumentException or FormatException)
            { /* Untrusted state never grants automatic authorization. Execution reports its diagnosis. */ }
        }
        return result.GroupBy(c => (c.ProjectId, c.Binding)).Select(g =>
            new CloudAutomaticRetryCandidate(g.Key.ProjectId, g.Key.Binding, g.Select(c => c.RetryNotBeforeUtc).Max())).ToArray();
    }

    // Restrictive provider cooldown evidence does not authorize file writes. Read
    // only bounded controls, not historical task definitions or file ledgers.
    public static DateTimeOffset? RetryNotBefore(ConfigurationSnapshot config)
    {
        DateTimeOffset? latest = null;
        foreach (var project in config.Projects.Where(p => p.ConfigurationError is null))
        {
            try
            {
                var due = CloudResumeTaskStore.RetryNotBefore(project, config);
                if (latest is null || due > latest) latest = due;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or ArgumentException or FormatException)
            { /* Foreign or unverifiable controls do not impose a cloud identity. */ }
        }
        return latest;
    }
}

internal enum CloudResumeState { Running, NetworkWait, Stopped, AwaitingConfirmation, RepairRequired, Completed, BudgetWait }
internal sealed record CloudResumeCompletion(string Path, string? Hash, bool Directory);
internal sealed record CloudResumeTask(int SchemaVersion, string Id, CloudMapping Mapping, SyncDirection Direction,
    bool Automatic, AutomaticBackupMode AutomaticMode, PullSelection? Selection, string[]? Scope,
    CloudJournal[] Journals, CloudResumeCompletion[] Completed, string[] ReservedPaths, long ReservedBytes,
    long ActiveMilliseconds, CloudResumeState State, bool PublicationOnly, DateTimeOffset? RetryNotBeforeUtc, string Signature);

// Authorization and scope are written once. Constant-size controls and independently checked
// file receipts avoid rewriting all selections/journals for each file in a large project.
internal sealed class CloudResumeTaskStore
{
    private sealed record Definition(int SchemaVersion, string Id, CloudMapping Mapping, SyncDirection Direction,
        bool Automatic, AutomaticBackupMode AutomaticMode, PullSelection? Selection, string Signature);
    private sealed record Control(int SchemaVersion, string Id, string ProjectId, string Binding, string DefinitionSignature,
        CloudResumeState State, long ActiveMilliseconds, string BudgetEpoch, long InheritedReservedBytes,
        bool PublicationOnly, DateTimeOffset? RetryNotBeforeUtc, string Signature);
    private sealed record ScopeRecord(int SchemaVersion, string Id, string Binding, string[] Paths, string Signature);
    private sealed record JournalReceipt(CloudJournal Original, CloudJournal Latest);
    private sealed record FileReceipt(int SchemaVersion, string Id, string Binding, string Path, bool Reserved,
        long Bytes, string? ReservationEpoch, CloudResumeCompletion? Completed, JournalReceipt[] Journals,
        RecoveryDisposition Recovery, string Signature);

    private readonly object gate = new();
    private readonly string path;
    private readonly string stateRoot;
    private readonly string bindingRoot;
    private readonly string taskRoot;
    private readonly Definition definition;
    private Control control;
    private string? controlSignature;
    private string[]? scope;
    private HashSet<string>? scopeSet;
    private readonly Dictionary<string, FileReceipt> receipts = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, string?> receiptSignatures = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, JournalReceipt> journals = new(StringComparer.Ordinal);
    private long reservedBytes;
    private CloudResumeTask? snapshot;
    private bool retired;

    private CloudResumeTaskStore(string path, string stateRoot, Definition definition, Control control, string? signature, string? bindingRoot = null)
    {
        this.path = path; this.stateRoot = stateRoot; this.definition = definition;
        this.control = control; controlSignature = signature;
        this.bindingRoot = bindingRoot ?? Path.GetDirectoryName(path)!;
        taskRoot = Path.Combine(this.bindingRoot, "resume-tasks", definition.Id);
    }
    private static string Name(ProjectDefinition project, ConfigurationSnapshot config) =>
        Path.Combine(CloudMapping.StateRoot(project, config), "resume-task.json");
    private static string Digest<T>(T value) => DeviceIdentity.Digest(JsonSerializer.Serialize(value, AtomicJson.Options));
    private static string Checksum(Definition x) => Digest(x with { Signature = "" });
    private static string Checksum(Control x) => Digest(x with { Signature = "" });
    private static string Checksum(ScopeRecord x) => Digest(x with { Signature = "" });
    private static string Checksum(FileReceipt x) => Digest(x with { Signature = "" });
    private static string FileKey(string relative) => DeviceIdentity.Digest(relative.ToUpperInvariant());
    private string ReceiptPath(string relative) => Path.Combine(taskRoot, "files", FileKey(relative) + ".json");

    public CloudResumeTask Value
    {
        get
        {
            lock (gate)
                return snapshot ??= new(1, definition.Id, definition.Mapping, definition.Direction,
                    definition.Automatic, definition.AutomaticMode, definition.Selection, scope,
                    journals.Values.Select(j => j.Original).ToArray(),
                    receipts.Values.Where(r => r.Completed is not null).Select(r => r.Completed!).ToArray(),
                    receipts.Values.Where(r => r.Reserved && r.ReservationEpoch == control.BudgetEpoch).Select(r => r.Path).ToArray(), reservedBytes,
                    control.ActiveMilliseconds, control.State, control.PublicationOnly, control.RetryNotBeforeUtc, control.Signature);
        }
    }

    private sealed record LocatedControl(string Path, Control Value, string Text, bool Current);
    private static string ActiveRoot(ProjectDefinition project, ConfigurationSnapshot config) =>
        Path.Combine(CloudMapping.StateRoot(project, config), "active-tasks");
    private static bool QueuedState(CloudResumeState state) => state is CloudResumeState.NetworkWait or CloudResumeState.BudgetWait;

    private static Control? ReadControl(ProjectDefinition project, ConfigurationSnapshot config, out string? text) =>
        ReadControlAt(Name(project, config), project, config, out text);

    private static Control? ReadControlAt(string path, ProjectDefinition project, ConfigurationSnapshot config, out string? text)
    {
        SafePath.Validate(path, config.Preview.StateDirectory);
        text = AtomicJson.ReadText(path);
        if (text is null) return null;
        var c = JsonSerializer.Deserialize<Control>(text, AtomicJson.Options) ?? throw new IOException("Invalid recovery task.");
        if (c.SchemaVersion != 1 || !Guid.TryParseExact(c.Id, "N", out _) || !FileVersion.IsHash(c.DefinitionSignature ?? "") ||
            c.Signature != Checksum(c) || c.ProjectId != project.ProjectId || c.Binding != CloudMapping.Bind(project, config) ||
            !Enum.IsDefined(c.State) || c.ActiveMilliseconds < 0 || c.InheritedReservedBytes < 0 || !Guid.TryParseExact(c.BudgetEpoch, "N", out _))
            throw new IOException("Recovery task identity mismatch. Evidence retained.");
        if (path != Name(project, config) && Path.GetFileName(path) != c.Id + ".json")
            throw new IOException("Recovery queued task location mismatch. Evidence retained.");
        return c;
    }

    private static IReadOnlyList<LocatedControl> Controls(ProjectDefinition project, ConfigurationSnapshot config)
    {
        var result = new List<LocatedControl>();
        var current = ReadControl(project, config, out var currentText);
        if (current is not null) result.Add(new(Name(project, config), current, currentText!, true));
        var active = ActiveRoot(project, config);
        SafePath.Validate(active, config.Preview.StateDirectory);
        if (Directory.Exists(active))
            foreach (var path in Directory.EnumerateFiles(active, "*.json").Order(StringComparer.Ordinal))
            {
                SafePath.Validate(path, active);
                // A crash after copying the old marker and before replacing it may
                // leave both. The current marker always supersedes its queued copy.
                if (current is not null && Path.GetFileName(path) == current.Id + ".json") continue;
                if (!Guid.TryParseExact(Path.GetFileNameWithoutExtension(path), "N", out _))
                    throw new IOException("Invalid queued recovery task location. Evidence retained.");
                var control = ReadControlAt(path, project, config, out var text);
                if (control is not null) result.Add(new(path, control, text!, false));
            }
        return result;
    }

    public static bool IsWaiting(ProjectDefinition project, ConfigurationSnapshot config) =>
        Controls(project, config).Any(c => c.Value.State == CloudResumeState.NetworkWait);

    public static DateTimeOffset? RetryNotBefore(ProjectDefinition project, ConfigurationSnapshot config) =>
        Controls(project, config).Select(c => c.Value.RetryNotBeforeUtc).Max();

    public static IReadOnlyList<CloudResumeTask> ReadWaiting(ProjectDefinition project, ConfigurationSnapshot config)
    {
        var controls = Controls(project, config).Where(c => c.Value.State == CloudResumeState.NetworkWait).ToArray();
        // Completed hot paths stop at the small controls above, even when the old
        // task has hundreds of thousands of immutable per-file receipts.
        return controls.Select(c => ReadAt(c, project, config).Value).ToArray();
    }

    // Used only to distinguish independently authorized network work from legacy or
    // unknown pending evidence in a scoped report. This does not authorize execution.
    public static IReadOnlySet<string> VerifiedWaitingJournalIds(ProjectDefinition project, ConfigurationSnapshot config)
    {
        var result = new HashSet<string>(StringComparer.Ordinal);
        try
        {
            foreach (var located in Controls(project, config).Where(c => c.Value.State == CloudResumeState.NetworkWait))
            {
                var store = ReadAt(located, project, config);
                var task = store.Value;
                if (task.Automatic && (CloudMapping.NeedsFirstOperation(project, config) ||
                    task.AutomaticMode != config.GetAutomaticBackupMode(project.ProjectId))) continue;
                foreach (var original in task.Journals)
                {
                    if (!store.CanRetry(original.Path) || store.HasCompleted(original.Path)) continue;
                    var journalPath = Path.Combine(CloudMapping.StateRoot(project, config), "cloud-transactions", original.Id + ".json");
                    SafePath.Validate(journalPath, config.Preview.StateDirectory);
                    var text = AtomicJson.ReadText(journalPath);
                    if (text is null) continue;
                    var current = JsonSerializer.Deserialize<CloudJournal>(text, AtomicJson.Options);
                    // Match the signed latest receipt as well as its immutable original intent;
                    // an unrelated or edited journal must remain visible even outside selection.
                    if (current is not null && Intent(current) == Intent(original) &&
                        Digest(current) == Digest(store.LatestJournal(original.Id))) result.Add(original.Id);
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or ArgumentException or FormatException)
        { return new HashSet<string>(StringComparer.Ordinal); }
        return result;
    }

    public static CloudResumeTaskStore? ReadActive(ProjectDefinition project, ConfigurationSnapshot config)
    {
        var controls = Controls(project, config);
        var next = controls.Where(c => c.Value.State == CloudResumeState.NetworkWait)
            .OrderBy(c => c.Current).ThenBy(c => c.Path, StringComparer.Ordinal).FirstOrDefault() ??
            controls.Where(c => c.Value.State == CloudResumeState.BudgetWait)
                .OrderBy(c => c.Current).ThenBy(c => c.Path, StringComparer.Ordinal).FirstOrDefault() ??
            controls.FirstOrDefault(c => c.Current && c.Value.State != CloudResumeState.Completed);
        return next is null ? null : ReadAt(next, project, config);
    }

    // A new unconfirmed, whole-project manual backup supersedes previous unattended
    // authorization. Its explicit recovery may retire old journals or choose current local
    // contents, so an older task must not later interpret those missing journals as damage.
    public static void StopActive(ProjectDefinition project, ConfigurationSnapshot config)
    {
        var stopped = new HashSet<string>(StringComparer.Ordinal);
        while (ReadActive(project, config) is { } task && stopped.Add(task.Value.Id))
            task.SetState(CloudResumeState.Stopped);
    }

    public static CloudResumeTaskStore? Read(ProjectDefinition project, ConfigurationSnapshot config)
    {
        var c = ReadControl(project, config, out var controlText);
        if (c is null) return null;
        return ReadAt(new(Name(project, config), c, controlText!, true), project, config);
    }

    private static CloudResumeTaskStore ReadAt(LocatedControl located, ProjectDefinition project, ConfigurationSnapshot config)
    {
        var c = located.Value;
        var bindingRoot = CloudMapping.StateRoot(project, config);
        var taskRoot = Path.Combine(bindingRoot, "resume-tasks", c.Id);
        var definitionPath = Path.Combine(taskRoot, "authorization.json");
        SafePath.Validate(definitionPath, config.Preview.StateDirectory);
        var text = AtomicJson.ReadText(definitionPath) ?? throw new IOException("Missing recovery authorization.");
        var d = JsonSerializer.Deserialize<Definition>(text, AtomicJson.Options) ?? throw new IOException("Invalid recovery authorization.");
        if (d.SchemaVersion != 1 || d.Id != c.Id || d.Mapping?.Project is null || d.Signature != Checksum(d) ||
            d.Signature != c.DefinitionSignature || d.Mapping.Project.ProjectId != project.ProjectId ||
            d.Mapping.Binding != c.Binding || !Enum.IsDefined(d.Direction) || !Enum.IsDefined(d.AutomaticMode))
            throw new IOException("Recovery authorization identity mismatch. Evidence retained.");
        d.Mapping.Validate(config);
        if (d.Selection is { } selection)
        {
            if (selection.ProjectId != project.ProjectId || selection.Files is null ||
                selection.Files.Any(f => f is null || !FileVersion.IsHash(f.Confirmation ?? "") || f.Bytes < 0) ||
                selection.Files.Select(f => f.Path).Distinct(StringComparer.OrdinalIgnoreCase).Count() != selection.Files.Count)
                throw new IOException("Invalid recovery selection.");
            foreach (var file in selection.Files) ValidateRelative(file.Path);
        }
        var store = new CloudResumeTaskStore(located.Path, config.Preview.StateDirectory, d, c, AtomicJson.Signature(located.Text), bindingRoot);
        store.reservedBytes = c.InheritedReservedBytes;
        var scopePath = Path.Combine(taskRoot, "scope.json");
        var scopeText = AtomicJson.ReadText(scopePath);
        if (scopeText is not null)
        {
            var saved = JsonSerializer.Deserialize<ScopeRecord>(scopeText, AtomicJson.Options) ?? throw new IOException("Invalid recovery scope.");
            if (saved.SchemaVersion != 1 || saved.Id != d.Id || saved.Binding != c.Binding || saved.Paths is null ||
                saved.Signature != Checksum(saved) || saved.Paths.Distinct(StringComparer.OrdinalIgnoreCase).Count() != saved.Paths.Length)
                throw new IOException("Recovery scope identity mismatch.");
            foreach (var relative in saved.Paths) ValidateRelative(relative);
            store.scope = saved.Paths;
            store.scopeSet = new(saved.Paths, StringComparer.OrdinalIgnoreCase);
        }
        else if (d.Selection is not null) throw new IOException("Missing recovery selection scope.");
        if (d.Selection is not null && !store.scope!.ToHashSet(StringComparer.OrdinalIgnoreCase)
            .SetEquals(d.Selection.Files.Select(f => f.Path))) throw new IOException("Recovery selection scope changed.");
        var filesRoot = Path.Combine(taskRoot, "files");
        SafePath.Validate(filesRoot, config.Preview.StateDirectory);
        if (Directory.Exists(filesRoot))
            foreach (var receiptPath in Directory.EnumerateFiles(filesRoot, "*.json"))
            {
                SafePath.Validate(receiptPath, filesRoot);
                var receiptText = AtomicJson.ReadText(receiptPath)!;
                var receipt = JsonSerializer.Deserialize<FileReceipt>(receiptText, AtomicJson.Options) ?? throw new IOException("Invalid recovery receipt.");
                store.ValidateReceipt(receipt, receiptPath, config);
                if (!store.receipts.TryAdd(receipt.Path, receipt)) throw new IOException("Duplicate recovery file receipt.");
                store.receiptSignatures.Add(receipt.Path, AtomicJson.Signature(receiptText));
                if (receipt.Reserved && receipt.ReservationEpoch == c.BudgetEpoch) store.reservedBytes = Add(store.reservedBytes, receipt.Bytes);
                foreach (var journal in receipt.Journals)
                    if (!store.journals.TryAdd(journal.Original.Id, journal)) throw new IOException("Duplicate recovery transaction authorization.");
            }
        return store;
    }

    private static void ValidateRelative(string relative)
    {
        if (relative is null || PathRules.Relative(relative) != relative) throw new IOException("Invalid recovery task scope.");
    }
    private void ValidateReceipt(FileReceipt receipt, string receiptPath, ConfigurationSnapshot config)
    {
        if (receipt.SchemaVersion != 1 || receipt.Id != definition.Id || receipt.Binding != control.Binding ||
            receipt.Path is null || receipt.Signature != Checksum(receipt) || receipt.Bytes < 0 ||
            !receipt.Reserved && receipt.Bytes != 0 || receipt.Journals is null || !Enum.IsDefined(receipt.Recovery) ||
            receipt.Reserved && !Guid.TryParseExact(receipt.ReservationEpoch, "N", out _) ||
            Path.GetFileName(receiptPath) != FileKey(receipt.Path) + ".json")
            throw new IOException("Recovery receipt identity mismatch.");
        ValidateRelative(receipt.Path);
        if (scopeSet is null || !scopeSet.Contains(receipt.Path))
            throw new IOException("Recovery receipt is outside the authorized scope.");
        if (receipt.Completed is { } completed && (completed.Path != receipt.Path ||
            completed.Hash is not null && !FileVersion.IsHash(completed.Hash))) throw new IOException("Invalid completed recovery receipt.");
        foreach (var entry in receipt.Journals)
        {
            if (entry is null || entry.Original is null || entry.Latest is null || entry.Original.Id != entry.Latest.Id ||
                entry.Original.Path != receipt.Path || entry.Latest.Path != receipt.Path)
                throw new IOException("Invalid recovery transaction receipt.");
            foreach (var journal in new[] { entry.Original, entry.Latest })
            {
                if (journal.SchemaVersion != 5 || journal.Binding != control.Binding || journal.Mapping?.Project is null ||
                    journal.Mapping.Project.ProjectId != definition.Mapping.Project.ProjectId ||
                    !Guid.TryParseExact(journal.Id, "N", out _) || !Enum.IsDefined(journal.Direction) ||
                    journal.Local is { } local && (!FileVersion.IsHash(local.Sha256) || local.Length < 0))
                    throw new IOException("Invalid recovery transaction authorization.");
                journal.Mapping.Validate(config);
            }
        }
    }

    public static CloudResumeTaskStore Create(ProjectDefinition project, ConfigurationSnapshot config,
        SyncDirection direction, bool automatic, PullSelection? selection)
    {
        var path = Name(project, config);
        SafePath.Validate(path, config.Preview.StateDirectory);
        var old = ReadControl(project, config, out var previous);
        foreach (var queued in Controls(project, config).Where(c => !c.Current && !QueuedState(c.Value.State)))
            RetainControl(queued.Path, queued.Text, config.Preview.StateDirectory, Path.GetDirectoryName(path)!);
        if (old is not null)
        {
            var queuedPath = Path.Combine(ActiveRoot(project, config), old.Id + ".json");
            SafePath.Validate(queuedPath, config.Preview.StateDirectory);
            var duplicate = AtomicJson.ReadText(queuedPath);
            if (duplicate is not null) RetainControl(queuedPath, duplicate, config.Preview.StateDirectory, Path.GetDirectoryName(path)!);
            if (QueuedState(old.State)) CopyControl(path, queuedPath, previous!, config.Preview.StateDirectory);
            else CopyControl(path, RetainedName(Path.GetDirectoryName(path)!, old.Id), previous!, config.Preview.StateDirectory);
        }
        var id = Guid.NewGuid().ToString("N");
        var d = new Definition(1, id, CloudMapping.Capture(project, config), direction, automatic,
            config.GetAutomaticBackupMode(project.ProjectId), selection, "");
        d = d with { Signature = Checksum(d) };
        var c = new Control(1, id, project.ProjectId, d.Mapping.Binding, d.Signature, CloudResumeState.Running, 0,
            Guid.NewGuid().ToString("N"), 0, false, null, "");
        c = c with { Signature = Checksum(c) };
        var store = new CloudResumeTaskStore(path, config.Preview.StateDirectory, d, c, AtomicJson.Signature(previous));
        var definitionPath = Path.Combine(store.taskRoot, "authorization.json");
        SafePath.Validate(definitionPath, config.Preview.StateDirectory);
        AtomicJson.Write(definitionPath, d, null);
        if (selection is not null) store.WriteScope(selection.Files.Select(f => f.Path));
        store.controlSignature = AtomicJson.Write(path, c, store.controlSignature);
        return store;
    }

    private static string RetainedName(string root, string id) => Path.Combine(root, "retained-tasks", id + "-" + Guid.NewGuid().ToString("N") + ".json");

    private static void CopyControl(string source, string target, string expected, string stateRoot)
    {
        SafePath.Validate(source, stateRoot); SafePath.Validate(target, stateRoot);
        var signature = AtomicJson.Signature(expected);
        if (AtomicJson.Signature(AtomicJson.ReadText(source)) != signature) throw new IOException("Recovery control changed. Evidence retained.");
        Directory.CreateDirectory(Path.GetDirectoryName(target)!);
        SafePath.Validate(source, stateRoot); SafePath.Validate(target, stateRoot);
        using (var input = new FileStream(source, FileMode.Open, FileAccess.Read, FileShare.Read))
        using (var output = new FileStream(target, FileMode.CreateNew, FileAccess.Write, FileShare.None))
        { input.CopyTo(output); output.Flush(true); }
        if (AtomicJson.Signature(AtomicJson.ReadText(source)) != signature || AtomicJson.Signature(AtomicJson.ReadText(target)) != signature)
            throw new IOException("Recovery control changed while retaining it. Evidence retained.");
    }

    private static void RetainControl(string source, string expected, string stateRoot, string bindingRoot)
    {
        SafePath.Validate(source, stateRoot);
        var signature = AtomicJson.Signature(expected);
        if (AtomicJson.Signature(AtomicJson.ReadText(source)) != signature) throw new IOException("Recovery control changed. Evidence retained.");
        var id = Path.GetFileNameWithoutExtension(source);
        var retained = RetainedName(bindingRoot, id);
        SafePath.Validate(retained, stateRoot);
        Directory.CreateDirectory(Path.GetDirectoryName(retained)!);
        SafePath.Validate(source, stateRoot); SafePath.Validate(retained, stateRoot);
        File.Move(source, retained, false);
        if (AtomicJson.Signature(AtomicJson.ReadText(retained)) != signature)
            throw new IOException("Recovery control changed during retention. Displaced evidence retained.");
    }

    private void VerifyControl()
    {
        if (retired) throw new IOException("Recovery task is already retained.");
        if (AtomicJson.Signature(AtomicJson.ReadText(path)) != controlSignature)
            throw new IOException("Recovery control changed. Evidence retained.");
    }
    private void WriteControl(Func<Control, Control> change)
    {
        VerifyControl();
        var next = change(control);
        next = next with { Signature = Checksum(next) };
        controlSignature = AtomicJson.Write(path, next, controlSignature);
        control = next; snapshot = null;
    }
    private void WriteScope(IEnumerable<string> paths)
    {
        var values = paths.ToArray();
        foreach (var relative in values) ValidateRelative(relative);
        if (values.Distinct(StringComparer.OrdinalIgnoreCase).Count() != values.Length) throw new IOException("Duplicate recovery scope path.");
        var record = new ScopeRecord(1, definition.Id, control.Binding, values, "");
        record = record with { Signature = Checksum(record) };
        var scopePath = Path.Combine(taskRoot, "scope.json");
        SafePath.Validate(scopePath, stateRoot);
        AtomicJson.Write(scopePath, record, null);
        scope = values; scopeSet = new(values, StringComparer.OrdinalIgnoreCase); snapshot = null;
    }
    public void SetScope(IEnumerable<string> paths)
    {
        lock (gate)
        {
            if (scope is not null) return;
            VerifyControl(); WriteScope(paths);
        }
    }

    private FileReceipt Receipt(string relative)
    {
        ValidateRelative(relative);
        if (scopeSet is null || !scopeSet.Contains(relative))
            throw new IOException("File is outside the authorized recovery scope.");
        return receipts.GetValueOrDefault(relative) ?? new(1, definition.Id, control.Binding, relative, false, 0, null, null, [], RecoveryDisposition.None, "");
    }
    private void WriteReceipt(FileReceipt receipt)
    {
        VerifyControl();
        receipt = receipt with { Signature = Checksum(receipt) };
        var receiptPath = ReceiptPath(receipt.Path);
        SafePath.Validate(receiptPath, stateRoot);
        var sig = AtomicJson.Write(receiptPath, receipt, receiptSignatures.GetValueOrDefault(receipt.Path));
        receiptSignatures[receipt.Path] = sig; receipts[receipt.Path] = receipt; snapshot = null;
        foreach (var entry in receipt.Journals) journals[entry.Original.Id] = entry;
    }

    public void Track(CloudJournal journal)
    {
        lock (gate)
        {
            var receipt = Receipt(journal.Path);
            var old = receipt.Journals.FirstOrDefault(j => j.Original.Id == journal.Id);
            var changedIntent = old is not null && Intent(old.Original) != Intent(journal);
            if (changedIntent && control.State != CloudResumeState.AwaitingConfirmation)
                WriteControl(c => c with { State = CloudResumeState.AwaitingConfirmation, RetryNotBeforeUtc = null });
            var entries = receipt.Journals.Where(j => j.Original.Id != journal.Id)
                .Append(new JournalReceipt(old?.Original ?? journal, journal)).ToArray();
            WriteReceipt(receipt with { Journals = entries, Completed = old is null ? null : receipt.Completed,
                Recovery = changedIntent ? RecoveryDisposition.AwaitingConfirmation : receipt.Recovery });
        }
    }
    internal static string Intent(CloudJournal j) => Digest(new
    { j.SchemaVersion, j.Id, j.Binding, j.Path, j.Direction, j.Local, j.Remote, j.Expected, j.Publish, j.Protection, j.Parent, j.CreatedUtc, j.Mapping });
    public CloudJournal LatestJournal(string id) { lock (gate) return journals.TryGetValue(id, out var j) ? j.Latest : throw new IOException("Missing original recovery transaction."); }
    public string? PublishedContentHash(string relative)
    {
        lock (gate)
            return receipts.GetValueOrDefault(relative)?.Journals
                .Where(j => j.Latest.Publish && j.Latest.Committed is not null)
                .OrderBy(j => j.Latest.CreatedUtc).Select(j => j.Latest.Committed!.Revision.Version.Sha256).LastOrDefault();
    }
    public bool HasCompleted(string relative) { lock (gate) return receipts.GetValueOrDefault(relative)?.Completed is not null; }
    public RecoveryDisposition GetRecovery(string relative)
    {
        lock (gate)
        {
            var receipt = receipts.GetValueOrDefault(relative);
            return receipt is not null && receipt.Journals.Any(j => Intent(j.Original) != Intent(j.Latest))
                ? RecoveryDisposition.AwaitingConfirmation : receipt?.Recovery ?? RecoveryDisposition.None;
        }
    }
    public bool CanRetry(string relative) => GetRecovery(relative) == RecoveryDisposition.AutomaticRetry;
    public void SetRecovery(string relative, RecoveryDisposition recovery)
    {
        if (!Enum.IsDefined(recovery)) throw new IOException("Invalid recovery disposition.");
        lock (gate)
        {
            var receipt = Receipt(relative);
            if (recovery == RecoveryDisposition.AutomaticRetry && receipt.Journals.Any(j => Intent(j.Original) != Intent(j.Latest)))
                recovery = RecoveryDisposition.AwaitingConfirmation;
            if (receipt.Recovery != recovery) WriteReceipt(receipt with { Recovery = recovery });
        }
    }
    public bool WasReserved(string relative)
    {
        lock (gate) return receipts.GetValueOrDefault(relative) is { Reserved: true } receipt && receipt.ReservationEpoch == control.BudgetEpoch;
    }
    public void ResetBudget(long inheritedBytes, long activeMilliseconds)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(inheritedBytes);
        ArgumentOutOfRangeException.ThrowIfNegative(activeMilliseconds);
        lock (gate)
        {
            WriteControl(c => c with { BudgetEpoch = Guid.NewGuid().ToString("N"), InheritedReservedBytes = inheritedBytes,
                ActiveMilliseconds = activeMilliseconds });
            reservedBytes = inheritedBytes; snapshot = null;
        }
    }
    public void Reserve(string relative, long bytes)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(bytes);
        lock (gate)
        {
            var receipt = Receipt(relative);
            var sameEpoch = receipt.Reserved && receipt.ReservationEpoch == control.BudgetEpoch;
            if (sameEpoch && receipt.Completed is null) return;
            // A completed/retired intent for this path is distinct from the next version.
            // Retrying the same incomplete intent remains idempotent; a new intent pays
            // its own full reservation, including after a restart in the same epoch.
            WriteReceipt(receipt with { Reserved = true, Bytes = sameEpoch ? Add(receipt.Bytes, bytes) : bytes,
                ReservationEpoch = control.BudgetEpoch, Completed = null });
            reservedBytes = Add(reservedBytes, bytes); snapshot = null;
        }
    }
    public void Complete(string relative, string? hash, bool directory = false)
    {
        if (hash is not null && !FileVersion.IsHash(hash)) throw new IOException("Invalid completion hash.");
        lock (gate)
        {
            var receipt = Receipt(relative);
            WriteReceipt(receipt with { Completed = new(relative, hash, directory), Recovery = RecoveryDisposition.None });
        }
    }
    public bool CompletedEqual(PreviewFile file, FileDecision decision)
    {
        lock (gate)
        {
            var completed = receipts.GetValueOrDefault(file.RelativePath)?.Completed;
            return decision.Action is FileAction.Equal or FileAction.Directory && completed is not null &&
                (completed.Directory ? file.Source.Kind == ObservationKind.Directory :
                    completed.Hash is not null && file.Source.Version?.Sha256 == completed.Hash);
        }
    }
    public bool MatchesRestored(PreviewFile file, CloudRevision[] heads)
    {
        lock (gate)
            return receipts.GetValueOrDefault(file.RelativePath)?.Journals.Any(r =>
                r.Latest.RestoredItem is { } restored && r.Original.Local is { } local && r.Original.Remote is { } remote &&
                file.Source.Version is { } source && source.Sha256 == local.Sha256 && source.Length == local.Length &&
                heads.Length == 1 && heads[0].ContentHashKnown && !heads[0].Directory &&
                heads[0].ItemId == restored.Id && heads[0].ETag == restored.ETag &&
                heads[0].Version.Sha256 == remote.Version.Sha256) == true;
    }
    public void SetState(CloudResumeState state, DateTimeOffset? notBefore = null, bool? publicationOnly = null)
    {
        lock (gate)
        {
            WriteControl(c => c with { State = state,
                RetryNotBeforeUtc = state == CloudResumeState.NetworkWait ? Later(c.RetryNotBeforeUtc, notBefore) : null,
                PublicationOnly = publicationOnly ?? c.PublicationOnly });
            if (!QueuedState(state) && state != CloudResumeState.Running && Path.GetDirectoryName(path) == Path.Combine(bindingRoot, "active-tasks"))
            {
                var text = AtomicJson.ReadText(path);
                if (AtomicJson.Signature(text) != controlSignature) throw new IOException("Recovery control changed. Evidence retained.");
                RetainControl(path, text!, stateRoot, bindingRoot);
                retired = true;
            }
        }
    }
    public void AddActiveTime(long milliseconds)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(milliseconds);
        lock (gate) WriteControl(c => c with { ActiveMilliseconds = Add(c.ActiveMilliseconds, milliseconds) });
    }
    private static long Add(long a, long b) => b > long.MaxValue - a ? long.MaxValue : a + b;
    private static DateTimeOffset? Later(DateTimeOffset? a, DateTimeOffset? b) => a is null ? b : b is null || a > b ? a : b;
}
