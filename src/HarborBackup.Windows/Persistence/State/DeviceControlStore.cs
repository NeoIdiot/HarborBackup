using System.Text.Json;
using HarborBackup.Core.Domain;

namespace HarborBackup.Windows.Persistence;

public sealed class DeviceControlStore
{
    private readonly string path;
    private readonly string deviceId;
    public DeviceControlStore(PreviewSettings settings)
    {
        if (!FileVersion.IsHash(settings.DeviceId)) throw new FormatException("Invalid device ID.");
        deviceId = settings.DeviceId;
        path = Path.Combine(settings.StateDirectory, "devices", deviceId, "scheduler.json");
    }

    public bool ReadPaused()
    {
        var content = AtomicJson.ReadText(path);
        return content is not null && Parse(content);
    }

    public void SetPaused(bool paused)
    {
        var content = AtomicJson.ReadText(path);
        if (content is not null && Parse(content) == paused) return;
        AtomicJson.Write(path, new { schemaVersion = 1, deviceId, paused, updatedAtUtc = DateTimeOffset.UtcNow }, AtomicJson.Signature(content));
    }

    private bool Parse(string content)
    {
        using var document = JsonDocument.Parse(content);
        var root = document.RootElement;
        if (root.GetProperty("schemaVersion").GetInt32() != 1 || root.GetProperty("deviceId").GetString() != deviceId)
            throw new FormatException("Unknown or mismatched device control record.");
        return root.GetProperty("paused").GetBoolean();
    }
}
