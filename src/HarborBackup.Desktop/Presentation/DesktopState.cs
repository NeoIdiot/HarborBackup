using HarborBackup.Core.Domain;
using HarborBackup.Desktop.Localization;
using HarborBackup.Windows.Persistence;
using HarborBackup.Windows.Integration;
using HarborBackup.Windows.Persistence.Configuration;

namespace HarborBackup.Desktop.Presentation;

internal sealed record DetailRow(string Project, string Path, string Action, string Details, bool Attention,
    bool SystemRow = false, string? DetailKey = null, object?[]? DetailArguments = null)
{
    public string ProjectId { get; init; } = "";
    internal string? ActionProjectId => !SystemRow && Recovery is not (RecoveryDisposition.AutomaticRetry or RecoveryDisposition.RepairRequired or RecoveryDisposition.Stopped) &&
        FileVersion.IsHash(ProjectId) ? ProjectId : null;
    public string DisplayProject => SystemRow ? UiText.Text(Project) : Project;
    public string DisplayAction => UiText.Text(Recovery == RecoveryDisposition.None ? Action : ReportPresentation.RecoveryCaption(Recovery));
    public string DisplayDetails => (Recovery != RecoveryDisposition.None ? UiText.Text(ReportPresentation.RecoveryCaption(Recovery)) + Environment.NewLine :
        FailureKind == FileFailureKind.None ? "" : UiText.Text(ReportPresentation.FailureCaption(FailureKind)) + Environment.NewLine) +
        CloudAccountPresentation.Message(UiText.Text(DetailKey ?? Details, DetailArguments ?? []));
    public FileFailureKind FailureKind { get; init; }
    public RecoveryDisposition Recovery { get; init; }
    public bool IsAutomaticRetry => Recovery == RecoveryDisposition.AutomaticRetry &&
        FailureKind is not (FileFailureKind.Authorization or FileFailureKind.RecoveryRequired);
    public bool IsOfflineIssue => Attention && Recovery == RecoveryDisposition.None && CloudRunStatus.IsOfflineIssue(Action, Details);
    public VerificationCategory? Verification { get; init; }
    public string DisplayVerification => Verification is { } category ? UiText.Text(ReportPresentation.VerificationCaption(category)) : "";
}
internal sealed record ProjectRow(string Destination, string Source, string Target, string Result,
    bool IsLocal = true, string LocalStatus = "", string? CloudFolderId = null, string ProjectId = "")
{
    public ProjectHealthSnapshot? Health { get; init; }
    public AutomaticBackupMode AutomaticMode { get; init; }
    public bool BackupPaused { get; init; }
    public bool NeedsInitialization { get; init; }
    public RecoveryDisposition Recovery { get; init; }
    public string DisplayAutomaticMode => !IsLocal ? "—" : BackupPaused ? UiText.Text("项目备份已暂停") : UiText.Text(AutomaticMode switch
    { AutomaticBackupMode.Bidirectional => "双向备份", AutomaticBackupMode.DownloadOnly => "仅下载", _ => "仅上传" });
    public string DisplayResult => UiText.Text(Recovery == RecoveryDisposition.None ? Result : ReportPresentation.RecoveryCaption(Recovery));
    public string DisplaySource => IsLocal ? Source : "—";
    public string DisplayLocalStatus => UiText.Text(LocalStatus);
    public string DisplayProtection => !IsLocal ? "—" : Health is null ? UiText.Text("状态未知") :
        NeedsInitialization ? UiText.Text("待初始化") : ProjectHealthPresentation.Compact(Health, DateTimeOffset.UtcNow);
    public string DisplayProtectionDetails => Health is null ? UiText.Text("状态未知") : ProjectHealthPresentation.Summary(Health);
    public string DisplayLastFullSuccess => ProjectHealthPresentation.Time(Health?.LastFullSuccessUtc);
    public string DisplayLastRestoreVerification => Health is null ? "—" : ProjectHealthPresentation.RestoreVerification(Health);
    public string DisplayBacklog => Health is null ? "—" : ProjectHealthPresentation.Backlog(Health);
}
internal sealed record DesktopState(string Status, bool Busy, bool CanCancel, bool Paused, DateTimeOffset? NextDue, DateTimeOffset? LastCompleted,
    string? Error, ConfigurationSnapshot? Configuration, IReadOnlyList<ProjectRow> Projects, IReadOnlyList<DetailRow> Details,
    IReadOnlyList<HistoryRecord> History, BackupProgress? Progress, bool NeedsSetup = false)
{
    public bool OperationBusy { get; init; }
    public bool NetworkOffline { get; init; }
    public RecoveryDisposition Recovery { get; init; }
}

internal static class ReportPresentation
{
    public static string RecoveryCaption(RecoveryDisposition recovery) => recovery switch
    {
        RecoveryDisposition.AutomaticRetry => "等待自动重试",
        RecoveryDisposition.AwaitingConfirmation => "需要确认版本",
        RecoveryDisposition.RepairRequired => "需要修复后重试",
        RecoveryDisposition.Stopped => "已停止",
        _ => ""
    };
    public static string FailureCaption(FileFailureKind kind) => kind switch
    {
        FileFailureKind.FileUnavailable => "文件暂不可用", FileFailureKind.Network => "网络暂不可用",
        FileFailureKind.Authorization => "需要重新授权", FileFailureKind.RecoveryRequired => "需要核对恢复状态",
        FileFailureKind.Budget => "预算停止", FileFailureKind.Cancelled => "已安全停止", _ => ""
    };
    public static string VerificationCaption(VerificationCategory category) => category switch
    {
        VerificationCategory.Match => "元信息支持一致", VerificationCategory.Different => "内容不同",
        VerificationCategory.MissingLocal => "本地缺失", VerificationCategory.MissingCloud => "云端缺失",
        VerificationCategory.Directory => "目录", VerificationCategory.Absent => "两端缺失",
        VerificationCategory.TypeConflict => "类型冲突", _ => "证据不足，无法确认"
    };
    public static bool OfflineOnly(PreviewReport report) => report.Projects.Any(p => p.Issues.Count > 0) &&
        report.Projects.All(p => p.Issues.All(i => CloudRunStatus.IsOfflineIssue(i.Code, i.Message)) && !p.Plans.Any(f => f.Decision.NeedsAttention));
    public static bool IsOfflineOnly(IEnumerable<DetailRow> details)
    {
        var attention = details.Where(d => d.Attention).ToArray();
        return attention.Length > 0 && attention.All(d => d.IsOfflineIssue);
    }
    public static bool IsAutomaticRetryOnly(IEnumerable<DetailRow> details)
    {
        var attention = details.Where(d => d.Attention && d.FailureKind is not (FileFailureKind.Budget or FileFailureKind.Cancelled) &&
            d.Recovery != RecoveryDisposition.Stopped).ToArray();
        return attention.Length > 0 && attention.All(d => d.IsAutomaticRetry);
    }
    public static string Action(FileAction action, SyncDirection? direction = null) => action switch
    {
        FileAction.Copy when direction == SyncDirection.Backup => "上传",
        FileAction.Copy when direction == SyncDirection.Pull => "下载",
        FileAction.CreateDirectory when direction == SyncDirection.Backup => "创建云端目录",
        FileAction.CreateDirectory when direction == SyncDirection.Pull => "创建本地目录",
        FileAction.Copy => "复制", FileAction.Equal => "内容一致", FileAction.CreateDirectory => "创建目录",
        FileAction.Directory => "目录", FileAction.Absent => "两端缺失", FileAction.NeedsPull => "待取回",
        FileAction.NeedsPush => "待备份", FileAction.ConflictUnresolved => "内容冲突", FileAction.TypeConflict => "类型冲突",
        FileAction.Deferred => "延期处理", _ => action.ToString()
    };
    public static string Result(int code) => code switch { 0 => "操作完成", 2 => "有待处理问题", 130 => "已安全停止", _ => "失败" };
    public static string Mode(string mode) => mode switch
    {
        "Backup" => "备份", "Pull" => "取回", "Recover" => "事务恢复", "AutomaticBidirectional" => "自动双向备份",
        "AutomaticUpload" => "自动上传备份", "AutomaticDownload" => "自动下载备份", "AutomaticMixed" => "自动混合备份", _ => mode
    };
    private static DetailRow PreviewRow(string projectId, string project, PreviewPlan file, bool pull, bool upload)
    {
        var decision = file.Decision;
        var description = decision.Reason == "RetryInterruptedPull" ? decision.Reason : decision.Action == FileAction.Copy && pull
            ? decision.Protection == ProtectionKind.None ? "下载到本地" : "下载到本地，覆盖前保留本地旧内容"
            : decision.Action == FileAction.Copy && upload
                ? decision.Protection == ProtectionKind.None ? "上传到云端" : "上传到云端，覆盖前保留云端旧内容"
                : decision.Reason;
        var errors = string.Join(Environment.NewLine, new[] { file.SourceError, file.TargetError }.Where(e => !string.IsNullOrEmpty(e)));
        return new(project, file.Path, Action(decision.Action), description, decision.NeedsAttention,
            DetailKey: errors.Length == 0 ? description : "{0}\n{1}",
            DetailArguments: errors.Length == 0 ? null : [UiText.Text(description), errors]) { ProjectId = projectId, Verification = file.Verification };
    }
    private static IEnumerable<DetailRow> RunDetails(ProjectRun project)
    {
        var rows = project.Files.Select(f => new DetailRow(project.Destination, f.Path, Action(f.Action, f.Direction),
            f.Error ?? (f.Changed ? "已完成写入并校验" : "未改写业务文件"), f.NeedsAttention())
            { ProjectId = project.ProjectId, FailureKind = f.FailureKind, Recovery = CloudRunStatus.Recovery(f) })
            .Concat(project.Issues.Select(i => new DetailRow(project.Destination, "", i.Code, i.Message, true)
                { ProjectId = project.ProjectId, FailureKind = i.FailureKind, Recovery = CloudRunStatus.Recovery(i) })).ToArray();
        foreach (var row in rows) yield return row;
        var recovery = CloudRunStatus.Recovery(project);
        if (project.Recovery != RecoveryDisposition.None && !rows.Any(r => r.Attention))
            yield return new(project.Destination, "", RecoveryCaption(recovery), RecoveryCaption(recovery), true)
                { ProjectId = project.ProjectId, Recovery = recovery };
    }
    public static DetailRow[] Details(object report) => report switch
    {
        BackupReport b => b.Projects.SelectMany(RunDetails)
            .Concat(b.Diagnostics.Select(i => new DetailRow("运行", "", i.Code, i.Message, true, SystemRow: true,
                DetailKey: "{0}\n{1}", DetailArguments: [UiText.Text("文件传输结果已保留，但运行摘要或历史记录未完整保存。"), i.Message]) { FailureKind = i.FailureKind, Recovery = CloudRunStatus.Recovery(i) })).ToArray(),
        PreviewReport p => p.Projects.SelectMany(r => r.Plans.Select(f => PreviewRow(r.ProjectId, r.Destination, f, p.PullSelection is not null, p.UploadSelection is not null))
            .Concat(r.Issues.Select(i => new DetailRow(r.Destination, "", i.Code, i.Message, true) { ProjectId = r.ProjectId, FailureKind = i.FailureKind, Recovery = CloudRunStatus.Recovery(i) }))).ToArray(),
        _ => []
    };
}
