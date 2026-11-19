using System.Text.Json;
using HarborBackup.Windows.Integration;
using HarborBackup.Windows.Persistence;

namespace HarborBackup.Desktop.Lifecycle;

internal sealed record DesktopPreferences(int SchemaVersion = 1, int Width = 1100, int Height = 720,
    bool CloseHintShown = false, string Language = "zh-CN", int SidebarWidth = 184);

internal sealed class DesktopPreferencesStore
{
    private readonly string path;
    private string? signature;
    public DesktopPreferencesStore(RuntimePaths paths, string? explicitPath = null)
    {
        path = explicitPath ?? paths.Data.DesktopSettings;
    }
    public DesktopPreferences Read()
    {
        var content = AtomicJson.ReadText(path);
        signature = AtomicJson.Signature(content);
        if (content is null) return new();
        using var document = JsonDocument.Parse(content);
        if (!document.RootElement.TryGetProperty("schemaVersion", out var version) || version.GetInt32() != 1) throw new FormatException("Unknown desktop settings version.");
        var value = JsonSerializer.Deserialize<DesktopPreferences>(content, AtomicJson.Options) ?? throw new FormatException("Invalid UI preferences.");
        if (value.SchemaVersion != 1) throw new FormatException("Unknown UI preference version.");
        return value with { Width = Math.Clamp(value.Width, 900, 3000), Height = Math.Clamp(value.Height, 600, 2000),
            SidebarWidth = Math.Clamp(value.SidebarWidth, 64, 280) };
    }
    public void Save(DesktopPreferences value) => signature = AtomicJson.Write(path, value, signature);
}
