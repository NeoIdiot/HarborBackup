using System.Text.Json;

namespace HarborBackup.Windows.Persistence;

internal static class JsonInput
{
    public static JsonDocument Read(string path)
    {
        var text = File.ReadAllText(path); // StreamReader detects the legacy UTF-8 BOM.
        return Parse(text);
    }

    public static JsonDocument Parse(string text)
    {
        var document = JsonDocument.Parse(text);
        try { ValidateKeys(document.RootElement); return document; }
        catch { document.Dispose(); throw; }
    }

    private static void ValidateKeys(JsonElement element)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            var keys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var property in element.EnumerateObject())
            {
                if (!keys.Add(property.Name)) throw new FormatException($"Duplicate JSON property: {property.Name}");
                ValidateKeys(property.Value);
            }
        }
        else if (element.ValueKind == JsonValueKind.Array)
            foreach (var item in element.EnumerateArray()) ValidateKeys(item);
    }

    public static JsonElement Get(JsonElement parent, string name)
    {
        if (parent.ValueKind is JsonValueKind.Undefined or JsonValueKind.Null) return default;
        if (parent.ValueKind != JsonValueKind.Object) throw new FormatException("Expected JSON object.");
        foreach (var property in parent.EnumerateObject())
            if (property.Name.Equals(name, StringComparison.OrdinalIgnoreCase)) return property.Value;
        return default;
    }
    public static bool Has(JsonElement element) => element.ValueKind is not (JsonValueKind.Undefined or JsonValueKind.Null);
    public static string Text(JsonElement element) => element.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(element.GetString())
        ? element.GetString()! : throw new FormatException("Expected a nonempty JSON string.");
    public static bool Boolean(JsonElement element, bool fallback) => !Has(element) ? fallback :
        element.ValueKind is JsonValueKind.True or JsonValueKind.False ? element.GetBoolean() : throw new FormatException("Expected JSON boolean.");
    public static int Integer(JsonElement element, int fallback, int min, int max) => !Has(element) ? fallback :
        element.ValueKind == JsonValueKind.Number && element.TryGetInt32(out var n) && n >= min && n <= max
            ? n : throw new FormatException($"Expected integer in [{min}, {max}].");
    public static void Fields(JsonElement element, params string[] allowed)
    {
        if (!Has(element)) return;
        if (element.ValueKind != JsonValueKind.Object) throw new FormatException("Expected JSON object.");
        foreach (var property in element.EnumerateObject())
            if (!allowed.Contains(property.Name, StringComparer.OrdinalIgnoreCase)) throw new FormatException($"Unknown field: {property.Name}");
    }
}
