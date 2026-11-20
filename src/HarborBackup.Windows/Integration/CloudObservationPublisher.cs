using System.Text.Json;
using HarborBackup.Core.Domain;
using HarborBackup.OneDrive.Transport;
using HarborBackup.Windows.FileSystem;
using HarborBackup.Windows.Persistence;
using HarborBackup.Windows.Persistence.Configuration;

namespace HarborBackup.Windows.Integration;

// Immutable cloud records and a local acknowledged fingerprint: failed sends remain in the outbox.
internal sealed class CloudObservationPublisher(ConfigurationSnapshot config, ICloudDrive drive, TimeProvider? timeProvider = null, CloudRepository? sharedRepository = null)
{
    private readonly TimeProvider clock = timeProvider ?? TimeProvider.System;
    private sealed record Acknowledged(int SchemaVersion, string Fingerprint, DateTimeOffset PublishedUtc);
    public async Task PublishAsync(ProjectDefinition project, IReadOnlyList<FileResult> files, ReadMetrics metrics, CancellationToken token, bool drainOnly = false)
    {
        var account = config.CloudAccount!;
        var data = DataPaths.FromState(config.Preview.StateDirectory);
        var key = CloudRepository.ProjectKey(project);
        var outbox = Path.Combine(data.State, "devices", config.Preview.DeviceId, "cloud-outbox", account.RepositoryId!, key);
        var stamp = Path.Combine(data.Cache, "cloud-observations", config.Preview.DeviceId, account.RepositoryId!, key + ".json");
        SafePath.Validate(outbox, data.Root); SafePath.Validate(stamp, data.Root);
        var values = files.OrderBy(f => f.Path, StringComparer.OrdinalIgnoreCase)
            .Select(f => new { f.Path, f.Action, f.Changed, failed = f.Error is not null }).ToArray();
        var fingerprint = DeviceIdentity.Digest(JsonSerializer.Serialize(values, AtomicJson.Options));
        Acknowledged? acknowledged = null;
        try { acknowledged = JsonSerializer.Deserialize<Acknowledged>(AtomicJson.ReadText(stamp) ?? "null", AtomicJson.Options); }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
        { /* An unavailable disposable acknowledgement only causes a fresh publication. */ }
        var repository = sharedRepository ?? new CloudRepository(drive, account, data);
        RemoteItem? folder = null;
        async Task SendAsync(string path)
        {
            using var document = JsonDocument.Parse(AtomicJson.ReadText(path)!);
            var record = document.RootElement;
            if (!Guid.TryParseExact(Path.GetFileNameWithoutExtension(path), "N", out _) || record.GetProperty("schemaVersion").GetInt32() != 1 ||
                record.GetProperty("repository").GetString() != account.RepositoryId ||
                record.GetProperty("project").GetString() != key ||
                record.GetProperty("device").GetString() != config.Preview.DeviceId)
                throw new IOException("Observation outbox identity mismatch.");
            folder ??= (await repository.FolderAsync("metadata/devices/" + config.Preview.DeviceId + "/" + key, true, token))!;
            await repository.JsonAsync(folder.Id, Path.GetFileName(path), record, token);
            metrics.CloudObservationUploads++;
            acknowledged = new(1, DeviceIdentity.Digest(JsonSerializer.Serialize(record.GetProperty("files"), AtomicJson.Options)),
                record.GetProperty("observedUtc").GetDateTimeOffset());
            try { AtomicJson.Write(stamp, acknowledged, AtomicJson.Signature(AtomicJson.ReadText(stamp))); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            { /* Cloud completion is verified independently of this rebuildable cache. */ }
            File.Delete(path);
        }
        // Drain older evidence first. Record acknowledgement only after verified cloud completion.
        if (Directory.Exists(outbox))
            foreach (var path in Directory.EnumerateFiles(outbox, "*.json").OrderBy(File.GetLastWriteTimeUtc))
            { token.ThrowIfCancellationRequested(); SafePath.Validate(path, data.Root); await SendAsync(path); }
        if (drainOnly) return;
        if (acknowledged is { SchemaVersion: 1 } && acknowledged.Fingerprint == fingerprint &&
            acknowledged.PublishedUtc <= clock.GetUtcNow() && clock.GetUtcNow() - acknowledged.PublishedUtc < TimeSpan.FromHours(1))
        { metrics.CloudObservationSkipped++; return; }
        var pending = Path.Combine(outbox, Guid.NewGuid().ToString("N") + ".json");
        AtomicJson.Write(pending, new { schemaVersion = 1, repository = account.RepositoryId, project = key,
            device = config.Preview.DeviceId, observedUtc = clock.GetUtcNow(), files = values }, null);
        await SendAsync(pending);
    }
}
