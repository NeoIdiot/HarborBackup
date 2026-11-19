namespace HarborBackup.Core.Domain;

public sealed record ExclusionPreset(string Id, IReadOnlyList<string> Files);

/// <summary>Published IDs and their patterns are immutable. Changed patterns require a new ID.</summary>
public static class ExclusionPresets
{
    public static IReadOnlyList<ExclusionPreset> All { get; } = Array.AsReadOnly(new[]
    {
        new ExclusionPreset("word-locks-v1", Array.AsReadOnly(new[] { "~$*.docx", "~$*.doc" })),
        new ExclusionPreset("excel-locks-v1", Array.AsReadOnly(new[] { "~$*.xlsx", "~$*.xls" })),
        new ExclusionPreset("powerpoint-locks-v1", Array.AsReadOnly(new[] { "~$*.pptx", "~$*.ppt" })),
        new ExclusionPreset("windows-thumbnails-v1", Array.AsReadOnly(new[] { "Thumbs.db" })),
        new ExclusionPreset("windows-folder-settings-v1", Array.AsReadOnly(new[] { "desktop.ini" })),
        new ExclusionPreset("macos-folder-info-v1", Array.AsReadOnly(new[] { ".DS_Store" }))
    });

    public static IReadOnlyList<ExclusionPreset> Resolve(IEnumerable<string> ids)
    {
        var selected = ids.ToHashSet(StringComparer.Ordinal);
        foreach (var id in selected)
            if (!All.Any(p => p.Id == id)) throw new FormatException($"Unknown built-in exclusion: {id}");
        // Catalog order makes equivalent checkbox selections produce the same fingerprint.
        return All.Where(p => selected.Contains(p.Id)).ToArray();
    }
}

public sealed record ExclusionRuleSources(IReadOnlyList<string> BuiltInPresetIds,
    IReadOnlyList<string> GlobalDirectories, IReadOnlyList<string> GlobalFiles,
    IReadOnlyList<string> ProjectDirectories, IReadOnlyList<string> ProjectFiles);
