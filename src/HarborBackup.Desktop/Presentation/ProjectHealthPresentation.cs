using HarborBackup.Desktop.Localization;
using HarborBackup.Windows.Persistence;

namespace HarborBackup.Desktop.Presentation;

internal static class ProjectHealthPresentation
{
    internal static string Time(DateTimeOffset? value) => value?.ToLocalTime().ToString("yyyy-MM-dd HH:mm", UiText.Culture)
        ?? UiText.Text("尚无核验记录");

    internal static string Backlog(ProjectHealthSnapshot value) => value.OldestPendingUtc is null
        ? UiText.Text("无已知积压")
        : UiText.Text("已知 {0} 项{1}；始于 {2}", value.KnownBacklogCount,
            value.PendingCountIncomplete ? UiText.Text("（数量可能不完整）") : "", Time(value.OldestPendingUtc));

    internal static string RestoreVerification(ProjectHealthSnapshot value) => value.LastRestoreVerifiedUtc is null
        ? UiText.Text("尚无核验记录") : UiText.Text("{0} · {1} · {2} 个文件（选中范围）", Time(value.LastRestoreVerifiedUtc),
            UiText.Text(value.RestoreScope == "Archive" ? "上一份归档" : "云端当前文件"), value.RestoreVerifiedFiles);

    internal static string Summary(ProjectHealthSnapshot value) => UiText.Text("完整成功：{0}\n还原核验：{1}\n{2}",
        value.LastFullSuccessUtc is null ? UiText.Text("尚无完整成功记录") : Time(value.LastFullSuccessUtc), RestoreVerification(value), Backlog(value));

    internal static string Compact(ProjectHealthSnapshot value, DateTimeOffset now) => UiText.Text(
        value.OldestPendingUtc is not null || value.KnownBacklogCount > 0 || value.PendingCountIncomplete ? "有待处理项" :
        value.LastFullSuccessUtc is null ? "状态未知" :
        value.NeedsAttention(now) ? "记录较旧" : "无已知积压");

    internal static bool SameDetails(IReadOnlyList<DetailRow> first, IReadOnlyList<DetailRow> second) => first.Count == second.Count &&
        first.Zip(second).All(pair => (pair.First with { DetailArguments = null }) == (pair.Second with { DetailArguments = null }) &&
            (pair.First.DetailArguments ?? []).SequenceEqual(pair.Second.DetailArguments ?? []));

    internal static IReadOnlyList<DetailRow> Attention(IReadOnlyList<ProjectRow> projects, DateTimeOffset now) => projects
        .Where(p => p.Health?.NeedsAttention(now) == true).Select(p =>
        {
            var health = p.Health!;
            var pending = health.OldestPendingUtc is { } since && now - since >= TimeSpan.FromHours(24);
            // Stable timestamps, rather than a changing age, keep notifications deduplicated.
            return new DetailRow(p.Destination, "", pending ? "备份积压已超过 24 小时" : "完整成功已超过 24 小时",
                health.Binding, true, SystemRow: true,
                DetailKey: pending ? "已知积压至少 {0} 项，首次记录于 {1}。请核对项目和运行详情；未观察到的文件不计入数量。"
                    : "此映射最近一次完整成功为 {0}。请运行备份或查看暂停、网络与预算设置。",
                DetailArguments: pending ? [health.KnownBacklogCount, Time(health.OldestPendingUtc)] : [Time(health.LastFullSuccessUtc)]);
        }).ToArray();
}
