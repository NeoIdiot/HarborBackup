using System.Text.RegularExpressions;

namespace HarborBackup.Windows.FileSystem;

public static class PathRules
{
    private static readonly Regex Reserved = new(@"^(CON|PRN|AUX|NUL|COM[1-9]|LPT[1-9])(?:\.|$)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    public static string Normalize(string path) => Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
    public static bool Within(string path, string root) => Normalize(path).Equals(Normalize(root), StringComparison.OrdinalIgnoreCase) ||
        Normalize(path).StartsWith(Path.EndsInDirectorySeparator(Normalize(root)) ? Normalize(root) : Normalize(root) + '\\', StringComparison.OrdinalIgnoreCase);
    public static bool Overlaps(string first, string second) => Within(first, second) || Within(second, first);

    public static string Absolute(string value)
    {
        if (!Regex.IsMatch(value, @"^(?:[a-zA-Z]:[\\/]|[\\/]{2}[^\\/]+[\\/][^\\/]+(?:[\\/]|$))"))
            throw new FormatException($"Expected a fully qualified drive or UNC path: {value}");
        return Normalize(value);
    }

    public static string Expand(string value, Func<string, string?>? environment = null)
    {
        environment ??= Environment.GetEnvironmentVariable;
        var expanded = Regex.Replace(value, "%([^%]+)%", match =>
        {
            var replacement = environment(match.Groups[1].Value);
            return !string.IsNullOrWhiteSpace(replacement) ? replacement :
                throw new FormatException($"Undefined or empty environment variable: {match.Value}");
        });
        if (Regex.IsMatch(expanded, "%[^%]+%")) throw new FormatException("Unexpanded environment variable.");
        return expanded;
    }

    public static string Source(string value, string desktopRoot, Func<string, string?>? environment = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value);
        var expanded = Expand(value, environment);
        if (Path.IsPathRooted(expanded) || Regex.IsMatch(expanded, "^[a-zA-Z]:")) return Absolute(expanded);
        if (expanded is "." or ".." || expanded.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 || expanded.Contains('/'))
            throw new FormatException("Relative source must be a single desktop folder name.");
        return Normalize(Path.Combine(Absolute(desktopRoot), expanded));
    }

    public static string Relative(string value, bool trimTrailingSeparator = false)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value);
        var relative = value.Replace('\\', '/');
        if (trimTrailingSeparator) relative = relative.TrimEnd('/');
        foreach (var part in relative.Split('/'))
        {
            if (string.IsNullOrWhiteSpace(part) || part is "." or ".." || part.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 ||
                part.EndsWith(' ') || part.EndsWith('.') || Reserved.IsMatch(part))
                throw new FormatException($"Unsafe relative path: {value}");
        }
        return relative;
    }

    public static IReadOnlyList<string> Exclusions(string source, IEnumerable<string?> patterns, bool directory)
    {
        var output = new List<string>();
        foreach (var value in patterns)
        {
            if (string.IsNullOrWhiteSpace(value)) continue;
            var relative = value.Trim().Replace('/', '\\');
            if (Path.IsPathRooted(relative)) throw new FormatException("Exclusions must be relative.");
            var parts = relative.Split('\\');
            for (var i = 0; i < parts.Length; i++)
            {
                var part = parts[i];
                if (part is "" or "." or ".." || part.IndexOfAny(['<', '>', ':', '"', '|']) >= 0 ||
                    ((directory || i < parts.Length - 1) && part.IndexOfAny(['*', '?']) >= 0))
                    throw new FormatException($"Invalid exclusion: {value}");
            }
            var resolved = parts.Length == 1 ? relative : Path.Combine(source, relative);
            // PowerShell Select-Object -Unique preserves case-distinct entries and input order.
            if (!output.Contains(resolved, StringComparer.Ordinal)) output.Add(resolved);
        }
        return output.AsReadOnly();
    }

    public static bool Excluded(string relative, IReadOnlyList<string> patterns, string source)
    {
        foreach (var pattern in patterns)
        {
            var scoped = Path.IsPathRooted(pattern);
            var candidate = scoped ? relative : relative.Split('/')[^1];
            var match = scoped ? Path.GetRelativePath(source, pattern).Replace('\\', '/') : pattern;
            if (Wildcard(candidate, match)) return true;
        }
        return false;
    }

    public static bool Wildcard(string value, string pattern) => Regex.IsMatch(value,
        "^" + Regex.Escape(pattern).Replace(@"\*", ".*").Replace(@"\?", ".") + "$",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1));
}
