using HarborBackup.Windows.Integration;
using HarborBackup.Windows.Persistence;
using HarborBackup.Windows.Persistence.Configuration;

namespace HarborBackup.Desktop.Presentation;

internal static class ProjectListPresentation
{
    public static string LocalStatus(string source)
    {
        try
        {
            return File.GetAttributes(source).HasFlag(FileAttributes.Directory)
                ? "本地目录存在" : "本地路径不是目录";
        }
        catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException) { return "本地目录缺失"; }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return "本地目录不可访问"; }
    }

    public static ProjectRow[] Merge(IReadOnlyList<ProjectRow> local, IReadOnlyList<CloudProjectTarget> cloud)
    {
        var rows = local.ToList();
        foreach (var item in cloud)
        {
            var index = rows.FindIndex(p => p.ProjectId == item.ProjectId);
            if (index >= 0) rows[index] = rows[index] with { CloudFolderId = item.FolderId };
            else rows.Add(new(item.Destination, "", CloudAccountPresentation.Path(item.Destination),
                "尚未在本机运行", false, "未关联本机目录", item.FolderId, item.ProjectId));
        }
        return rows.OrderBy(p => p.Destination, StringComparer.OrdinalIgnoreCase).ToArray();
    }

    public static IReadOnlyList<CloudProjectTarget> VisibleCloudProjects(ConfigurationSnapshot? config,
        IReadOnlyList<CloudProjectTarget> cloud)
    {
        if (config?.CloudAccount is not { } account) return cloud;
        return cloud.Where(item =>
            config.Projects.Any(p => p.ProjectId == item.ProjectId) ||
            !config.DismissedCloudProjects.Any(p => p.Matches(account, item.ProjectId))).ToArray();
    }

}
