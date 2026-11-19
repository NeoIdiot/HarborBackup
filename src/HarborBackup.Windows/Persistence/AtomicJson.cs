using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using HarborBackup.Windows.FileSystem;

namespace HarborBackup.Windows.Persistence;

public static class AtomicJson
{
    public static JsonSerializerOptions Options { get; } = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() }
    };

    public static string? ReadText(string path)
    {
        SafePath.Validate(path);
        try { return File.ReadAllText(path); }
        catch (FileNotFoundException) { return null; }
        catch (DirectoryNotFoundException) { return null; }
    }

    public static string? Signature(string? content) => content is null ? null :
        Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(content)));

    public static string Write(string path, object value, string? expectedSignature)
    {
        SafePath.Validate(path);
        var directory = Path.GetDirectoryName(path)!;
        Directory.CreateDirectory(directory);
        var id = Guid.NewGuid().ToString("N");
        var temporary = Path.Combine(directory, $"write-{id}.tmp");
        var previous = Path.Combine(directory, $"write-{id}.previous");
        var text = JsonSerializer.Serialize(value, Options);
        using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
        {
            var bytes = new UTF8Encoding(true).GetPreamble().Concat(Encoding.UTF8.GetBytes(text)).ToArray();
            stream.Write(bytes);
            stream.Flush(true);
        }
        // Known concurrent changes abort before replacement; actual displaced JSON is also retained on a race.
        if (Signature(ReadText(path)) != expectedSignature)
            throw new IOException($"State changed externally; preserving pending JSON: {temporary}");
        SafePath.Validate(path);
        if (expectedSignature is null) File.Move(temporary, path);
        else
        {
            File.Replace(temporary, path, previous);
            if (Signature(ReadText(previous)) != expectedSignature)
                throw new IOException($"State changed during replacement; review preserved JSON: {previous}");
            File.Delete(previous);
        }
        return Signature(text)!;
    }
}
