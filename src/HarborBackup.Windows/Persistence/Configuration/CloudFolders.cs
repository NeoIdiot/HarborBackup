using HarborBackup.Windows.FileSystem;
namespace HarborBackup.Windows.Persistence.Configuration;

[System.Text.Json.Serialization.JsonUnmappedMemberHandling(System.Text.Json.Serialization.JsonUnmappedMemberHandling.Disallow)]
public sealed record CloudFolders(string Archives = "archives",
    string Conflicts = "conflicts", string Metadata = "metadata")
{
    public void Validate()
    {
        var all = new[] { Archives, Conflicts, Metadata }.Select(p => PathRules.Relative(p, true)).ToArray();
        if (all.Any(p => new[] { "repository.json", ".harborbackup-repository-v1.json" }.Contains(p.Split('/')[0], StringComparer.OrdinalIgnoreCase)))
            throw new FormatException("Cloud folder overlaps repository identity.");
        for (var i=0;i<all.Length;i++)for(var j=i+1;j<all.Length;j++)
            if (all[i].Equals(all[j],StringComparison.OrdinalIgnoreCase) || all[i].StartsWith(all[j]+"/",StringComparison.OrdinalIgnoreCase) ||
                all[j].StartsWith(all[i]+"/",StringComparison.OrdinalIgnoreCase)) throw new FormatException("Cloud folders must not overlap.");
    }
    public string Resolve(string logical)
    {
        Validate();
        var parts = PathRules.Relative(logical).Split('/', 2);
        var root = parts[0] switch { "archives" => Archives, "conflicts" => Conflicts, "metadata" => Metadata,
            _ => throw new ArgumentException("Unknown cloud repository area.") };
        return PathRules.Relative(root, true) + (parts.Length == 2 ? "/" + parts[1] : "");
    }
}
