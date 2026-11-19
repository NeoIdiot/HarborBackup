using HarborBackup.Core.Domain;
using HarborBackup.Desktop.Localization;

namespace HarborBackup.Desktop.Presentation;

internal static class ExclusionPresentation
{
    internal static string PresetName(string id) => UiText.Text(id switch
    {
        "word-locks-v1" => "Word 临时/锁定文件",
        "excel-locks-v1" => "Excel 临时/锁定文件",
        "powerpoint-locks-v1" => "PowerPoint 临时/锁定文件",
        "windows-thumbnails-v1" => "Windows 缩略图缓存",
        "windows-folder-settings-v1" => "Windows 文件夹设置",
        "macos-folder-info-v1" => "macOS 文件夹信息",
        _ => throw new FormatException($"Unknown built-in exclusion: {id}")
    });

    internal static string Presets(IEnumerable<string> ids)
    {
        var selected = ExclusionPresets.Resolve(ids);
        return selected.Count == 0 ? UiText.Text("无") : string.Join(Environment.NewLine,
            selected.Select(p => PresetName(p.Id) + " · " + string.Join("; ", p.Files)));
    }

    internal static string Sources(ExclusionRuleSources? sources)
    {
        if (sources is null) return "";
        string Rules(IReadOnlyList<string> values) => values.Count == 0 ? UiText.Text("无") : string.Join("; ", values);
        return UiText.Text("内置规则：{0}", Presets(sources.BuiltInPresetIds)) + Environment.NewLine +
            UiText.Text("全局自定义目录：{0}", Rules(sources.GlobalDirectories)) + Environment.NewLine +
            UiText.Text("全局自定义文件：{0}", Rules(sources.GlobalFiles)) + Environment.NewLine +
            UiText.Text("项目自定义目录：{0}", Rules(sources.ProjectDirectories)) + Environment.NewLine +
            UiText.Text("项目自定义文件：{0}", Rules(sources.ProjectFiles)) + Environment.NewLine;
    }
}
