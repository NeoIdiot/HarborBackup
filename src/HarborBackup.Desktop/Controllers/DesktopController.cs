using System.Net.NetworkInformation;
using HarborBackup.Core.Application;
using HarborBackup.Core.Domain;
using HarborBackup.Desktop.Presentation;
using HarborBackup.OneDrive.Transport;
using HarborBackup.Windows.Integration;
using HarborBackup.Windows.Persistence;
using HarborBackup.Windows.Persistence.Configuration;

namespace HarborBackup.Desktop.Controllers;

internal enum DesktopCommand { Backup, Preview, UploadPreview, PullPreview, Pull, Verify, FullBackup, Recover, RunConfigured }
internal sealed record DesktopRunRequest(DesktopCommand Command, string? ProjectId, bool Automatic, bool RetryOnly);

internal sealed partial class DesktopController(RuntimePaths paths,
    Func<CloudAccountSettings, CancellationToken, Task<IReadOnlyList<CloudProjectTarget>>>? cloudCatalogReader = null,
    Func<bool>? networkAvailable = null,
    TimeProvider? timeProvider = null,
    Func<DesktopRunRequest, CancellationToken, Task<object?>>? operationRunner = null,
    Func<ConfigurationSnapshot, IReadOnlyList<CloudAutomaticRetryCandidate>>? resumeCandidateReader = null,
    Func<double>? retryJitter = null)
{
    private readonly RunCoordinator coordinator = new();
    private BackupSchedule? schedule;
    private bool loading;
    private TaskCompletionSource? refreshFinished;
    private bool stopping;
    private Task? controlWrite;
    private int ticks;
    private bool runningOperation;
    private bool needsSetup;
    private bool networkOffline;
    private string status = "正在加载配置";
    private string? error;
    private string? configurationError;
    private ConfigurationSnapshot? config;
    private DateTimeOffset? completed;
    private IReadOnlyList<ProjectRow> projects = [];
    private IReadOnlyList<DetailRow> details = [];
    private IReadOnlyList<DetailRow> stateDiagnostics = [];
    private IReadOnlyList<DetailRow> presentedDetails = [];
    private IReadOnlyList<HistoryRecord> history = [];
    private BackupProgress? progress;
    public CloudAccountSettings CloudAccount { get; private set; } = new();
    public RuntimePaths Paths { get; private set; } = paths;
    private CloudFolderBrowser? folderBrowser;
    internal CloudFolderBrowser FolderBrowser => folderBrowser ??= new(
        () => new CloudAccountSession(Paths.Data).Read(),
        (account, folderId, token) => new CloudAccountSession(Paths.Data).BrowseAsync(account, folderId, token));
    public bool Busy => loading || coordinator.IsBusy || controlWrite is { IsCompleted: false };
    public event Action? Changed;
    private bool WaitingForNetwork => networkOffline && CloudAccount.Enabled && !CloudAccount.RequiresAuthorization && !stopping;
    public DesktopState State => new(WaitingForNetwork && !runningOperation && status == "空闲" ? "等待网络" : status,
        Busy, runningOperation, coordinator.Paused, coordinator.Paused ? null : NextWorkDueUtc,
        completed, configurationError ?? error ?? stateDiagnostics.FirstOrDefault()?.Details, config, projects, presentedDetails, history, progress, needsSetup)
    { OperationBusy = coordinator.IsBusy || controlWrite is { IsCompleted: false }, NetworkOffline = networkOffline,
        Recovery = CurrentRecovery };
    public ConfigurationEditor Editor => new(Paths);
    private void Notify()
    {
        // A past network interruption belongs in the run record. Current connectivity is sampled separately.
        var combined = details.Where(d => !d.IsOfflineIssue).Concat(stateDiagnostics)
            .Concat(ProjectHealthPresentation.Attention(projects, DateTimeOffset.UtcNow)).ToArray();
        if (!ProjectHealthPresentation.SameDetails(presentedDetails, combined)) presentedDetails = combined;
        Changed?.Invoke();
    }

    public async Task RefreshAsync(bool includeHistory = false)
    {
        if (Busy || stopping) return;
        loading = true;
        var finished = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        refreshFinished = finished;
        // Loading serializes readers with writes, but is not a user operation. Publishing
        // this transient state every five seconds made the progress footer resize the page.
        try
        {
            var accountStore = new CloudAccountStore(Paths.Data);
            CloudAccount = await Task.Run(accountStore.Read);
            var loaded = await Task.Run(() =>
            {
                var c = new BackupRuntime(Paths).Load(forExecution: false);
                var diagnostics = new List<DetailRow>();
                T ReadState<T>(Func<T> read, T fallback, string caption)
                {
                    try { return read(); }
                    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or FormatException or
                        System.Text.Json.JsonException or InvalidOperationException or KeyNotFoundException or ArgumentException)
                    {
                        diagnostics.Add(new("状态", "", caption, caption + Environment.NewLine + CloudAccountPresentation.Error(ex), true, SystemRow: true));
                        return fallback;
                    }
                }
                var paused = ReadState(() => new DeviceControlStore(c.Preview).ReadPaused(), true, "自动备份状态无法读取，已暂停自动备份。");
                var saved = ReadState(() => RunStatusStore.Read(c.Preview), null, "运行状态无法读取，仍可编辑配置。");
                var savedHistory = includeHistory ? ReadState(() => RunHistory.Read(c.HistoryDirectory), history, "运行记录无法读取，仍可编辑配置。") : history;
                var results = c.Projects.Select(p => ReadState(() =>
                {
                    var record = saved?["projects"]?.AsArray().FirstOrDefault(r => r?["projectId"]?.GetValue<string>() == p.ProjectId);
                    if (record?["healthBinding"]?.GetValue<string>() != CloudMapping.Bind(p, c)) return null;
                    var lastResult = record?["lastResult"]?.GetValue<string>();
                    // Older summaries did not distinguish offline from other failed runs.
                    if (lastResult is "Failed" or "Warning" &&
                        DateTimeOffset.TryParse(record?["lastCompletedRunAt"]?.GetValue<string>(), out var lastAttempt) &&
                        savedHistory.Any(h => h.Time == lastAttempt && h.Report is { Cancelled: false } previous && previous.Projects.Any(r =>
                            r.ProjectId == p.ProjectId && CloudRunStatus.IsOfflineInterruption(r)))) return "OfflineInterrupted";
                    return lastResult;
                }, null, "运行状态无法读取，仍可编辑配置。")).ToArray();
                var lastCompleted = ReadState<DateTimeOffset?>(() => saved?["lastRun"]?["completedAtUtc"] is { } stamp &&
                    DateTimeOffset.TryParse(stamp.GetValue<string>(), out var value) ? value : null, null, "运行状态无法读取，仍可编辑配置。");
                var health = c.Projects.Select(p => ReadState<ProjectHealthSnapshot?>(() => ProjectHealthStore.Read(c, p, saved),
                    null, "项目保护状态无法读取，仍可编辑配置。")).ToArray();
                var recoveries = c.Projects.Select(p => ReadState(() =>
                {
                    var record = saved?["projects"]?.AsArray().FirstOrDefault(r => r?["projectId"]?.GetValue<string>() == p.ProjectId);
                    if (record?["healthBinding"]?.GetValue<string>() != CloudMapping.Bind(p, c)) return RecoveryDisposition.None;
                    return record?["recovery"] is { } value && Enum.TryParse<RecoveryDisposition>(value.GetValue<string>(), out var recovery) && Enum.IsDefined(recovery)
                        ? recovery : RecoveryDisposition.None;
                }, RecoveryDisposition.None, "运行状态无法读取，仍可编辑配置。")).ToArray();
                var retryCandidates = ReadState<IReadOnlyList<CloudAutomaticRetryCandidate>>(() =>
                    (resumeCandidateReader ?? CloudAutomaticRecovery.ReadCandidates)(c), [], "运行状态无法读取，仍可编辑配置。");
                return (Config: c, LocalStatuses: c.Projects.Select(p => ReadState(() =>
                    p.ConfigurationError is null && CloudMapping.NeedsFirstOperation(p, c) ? "待首次上传或拉取" : ProjectListPresentation.LocalStatus(p.Source),
                    "待核验", "目录状态无法读取，仍可编辑配置。")).ToArray(), Paused: paused, Results: results, LastCompleted: lastCompleted,
                    Health: health, History: savedHistory, Recoveries: recoveries, RetryCandidates: retryCandidates,
                    Diagnostics: diagnostics.ToArray());
            });
            config = loaded.Config;
            needsSetup = false;
            if (configurationError is not null) details = [];
            if ((configurationError is not null || status == "正在加载配置") && error is null) status = "空闲";
            configurationError = null;
            stateDiagnostics = loaded.Diagnostics;
            coordinator.Paused = loaded.Paused;
            history = loaded.History;
            if (schedule is null) schedule = new(TimeSpan.FromMinutes(config.IntervalMinutes), TimeSpan.FromMinutes(config.StartDelayMinutes), clock);
            else if (schedule.Interval.TotalMinutes != config.IntervalMinutes) schedule.Reset(TimeSpan.FromMinutes(config.IntervalMinutes));
            SynchronizeRetries(loaded.RetryCandidates);
            var refreshedProjects = config.Projects.Select((p, index) =>
            {
                var result = loaded.Results[index];
                var target = CloudAccountPresentation.Path(p.Destination);
                return new ProjectRow(p.Destination, p.Source, target, p.ConfigurationError ?? result switch
                { "Success" => "云端操作已完成", "Warning" => "有待处理问题", "BudgetLimited" => "预算已用完，下轮继续", "OfflineInterrupted" => "上次运行因断网中断",
                    "WaitingForRetry" => "等待自动重试", "AwaitingConfirmation" => "需要确认版本", "RepairRequired" => "需要修复后重试",
                    "Failed" => "失败", "Cancelled" or "Stopped" => "已停止", _ => "尚未运行" }, LocalStatus: loaded.LocalStatuses[index], CloudFolderId: p.CloudFolderId, ProjectId: p.ProjectId)
                { Health = loaded.Health[index], AutomaticMode = config.GetAutomaticBackupMode(p.ProjectId),
                    NeedsInitialization = loaded.LocalStatuses[index] == "待首次上传或拉取", Recovery = loaded.Recoveries[index] };
            }).ToArray();
            if (!projects.SequenceEqual(refreshedProjects)) projects = refreshedProjects;
            if (loaded.LastCompleted is { } value && (completed is null || value > completed)) completed = value;
            if (details.Count == 0 && history.FirstOrDefault()?.Report is { } report) details = ReportPresentation.Details(report);
        }
        catch (Exception ex)
        {
            stateDiagnostics = [];
            needsSetup = !File.Exists(Paths.ProjectConfig) && !File.Exists(Paths.SystemConfig);
            config = null;
            recoveryRetries.Clear();
            var message = CloudAccount.RequiresAuthorization ? "需要重新授权" : !CloudAccount.Enabled ? "请先连接 OneDrive 账户并选择云端仓库。" : needsSetup ? "账户已连接，请添加或加入备份项目。" : ex is FileNotFoundException or DirectoryNotFoundException
                ? "账户已连接，请添加或加入备份项目。" : CloudAccountPresentation.Error(ex);

            if (needsSetup) details = [];
            else if (configurationError != message) details = [new("配置", "", "配置错误", message, true, SystemRow: true)];
            configurationError = message;
            status = CloudAccount.RequiresAuthorization ? "需要重新授权" : CloudAccount.AccountId is not null && !CloudAccount.Enabled
                ? "待选择云端仓库" : needsSetup ? "尚未配置" : "请检查配置";
        }
        finally { loading = false; finished.TrySetResult(); Notify(); }
    }

    public async Task TickAsync()
    {
        if (stopping) return;
        UpdateNetworkAvailability();
        ticks++;
        if (ticks % 5 == 0) await RefreshAsync();
        var previousDue = schedule?.NextDueUtc;
        if (CanDispatchScheduled)
        {
            if (schedule?.IsDue == true) await RunAsync(DesktopCommand.Backup, automatic: true);
            else if (recoveryRetries.TryGetDue(out var retry))
                await RunAsync(DesktopCommand.Backup, retry!.ProjectId, automatic: true, retryOnly: true);
        }
        if (previousDue != schedule?.NextDueUtc) Notify();
    }

    private void UpdateNetworkAvailability()
    {
        bool offline;
        try { offline = !(networkAvailable ?? NetworkInterface.GetIsNetworkAvailable)(); }
        catch (NetworkInformationException) { return; }
        if (offline == networkOffline) return;
        networkOffline = offline;
        if (!offline)
        {
            connectedAt = clock.GetTimestamp();
            recoveryRetries.Connected();
        }
        Notify();
    }

    internal void ObserveOffline()
    {
        if (networkOffline || stopping) return;
        networkOffline = true;
        Notify();
    }

    public async Task<object?> RunAsync(DesktopCommand command, string? destination = null, bool automatic = false, PullSelection? confirmedPull = null, ConfigurationDraft? joinDraft = null, PullSelection? confirmedUpload = null, bool fullPreview = false, bool retryOnly = false)
    {
        if (Busy || stopping) return null;
        automatic |= command == DesktopCommand.RunConfigured;
        object? result = null;
        var updates = new ProgressMailbox();
        using var progressTimer = new System.Windows.Forms.Timer { Interval = 150 };
        progressTimer.Tick += (_, _) =>
        {
            if (!coordinator.IsBusy || stopping || updates.Take() is not { } p) return;
            progress = p;
            status = p.Phase switch { BackupPhase.Scanning => "正在扫描", BackupPhase.ReadingMetadata => "正在读取云端元数据", BackupPhase.Recovery => "恢复检查", BackupPhase.Uploading => "正在上传", BackupPhase.Verifying => "正在校验云端摘要", BackupPhase.Protecting => "正在保护旧版本", BackupPhase.Publishing => "正在提交备份", _ => "正在处理文件" };
            Notify();
        };
        var runtime = new BackupRuntime(Paths);
        var task = coordinator.TryStart(async token =>
        {
            if (joinDraft is not null)
            {
                if (command != DesktopCommand.Pull || confirmedPull is null) throw new InvalidOperationException("Joining requires a confirmed pull.");
                Editor.Save(joinDraft, ConfigurationPart.Projects);
            }
            result = operationRunner is not null ? await operationRunner(new(command, destination, automatic, retryOnly), token) :
                command is DesktopCommand.Preview or DesktopCommand.UploadPreview or DesktopCommand.PullPreview or DesktopCommand.Verify
                ? runtime.Preview(command is DesktopCommand.PullPreview ? SyncDirection.Pull : SyncDirection.Backup, destination, command == DesktopCommand.Verify || fullPreview, token, updates, prepareUpload: command == DesktopCommand.UploadPreview, verifyOnly: command == DesktopCommand.Verify)
                : runtime.Execute(command == DesktopCommand.Pull ? SyncDirection.Pull : SyncDirection.Backup, destination,
                    command == DesktopCommand.FullBackup, command == DesktopCommand.Recover, token: token, structuredProgress: updates, confirmedPull: confirmedPull, automatic: automatic, confirmedUpload: confirmedUpload, retryOnly: retryOnly);
        }, automatic);
        if (task is null) return null;
        if (automatic && !retryOnly && destination is null) schedule?.AcceptDue();
        var acceptedRetry = retryOnly ? RetryKey(destination) : null;
        if (acceptedRetry is not null && !recoveryRetries.Accepted(acceptedRetry)) acceptedRetry = null;
        DateTimeOffset? failedAttemptNotBefore = null;
        runningOperation = true;
        progressTimer.Start();
        status = command is DesktopCommand.Preview or DesktopCommand.UploadPreview or DesktopCommand.PullPreview or DesktopCommand.Verify ? "正在预览 / 核查" : "正在运行";
        error = null;
        Notify();
        try
        {
            await task;
            var code = result is BackupReport b ? b.ExitCode : ((PreviewReport)result!).ExitCode;
            details = ReportPresentation.Details(result!);
            status = result is BackupReport reportStatus ? ReportStatus(reportStatus) :
                code == 0 || code != 130 && ReportPresentation.IsOfflineOnly(details) ? "空闲" : ReportPresentation.Result(code);
            if (result is BackupReport report)
            {
                completed = report.CompletedAtUtc;
                ObserveRetryResult(report);
            }
        }
        catch (OperationCanceledException) { status = "已安全停止"; }
        catch (Exception ex)
        {
            failedAttemptNotBefore = ex is CloudRequestException request ? request.RetryNotBeforeUtc :
                ex is CloudCooldownException cooldown ? cooldown.RetryNotBeforeUtc : null;
            var offline = CloudNetworkAvailability.FriendlyMessage(ex) == CloudNetworkAvailability.Offline;
            error = offline ? null : CloudAccountPresentation.Error(ex);
            status = offline ? "空闲" : "运行失败";
            details = offline ? [] : [new("运行", "", status, error!, true, SystemRow: true)];
        }
        finally
        {
            progressTimer.Stop(); updates.Reset(); runningOperation = false; progress = null;
            UpdateNetworkAvailability();
            await RefreshAsync(includeHistory: true);
            if (acceptedRetry is not null) recoveryRetries.ReleaseFailedAttempt(acceptedRetry, failedAttemptNotBefore);
            Notify();
        }
        return result;
    }

    public Task<IReadOnlyList<CloudProjectTarget>> ReadCloudProjectsAsync(CancellationToken token)
    {
        var account = CloudAccount;
        if (!account.Enabled || account.RequiresAuthorization)
            throw new InvalidOperationException("请先连接并浏览云端目录。");
        if (stopping) throw new OperationCanceledException(token);
        return Task.Run(async () =>
        {
            if (cloudCatalogReader is not null) return await cloudCatalogReader(account, token);
            using var drive = new CloudAccountStore(Paths.Data).Open(account);
            return await CloudProjectCatalog.ReadAsync(Paths.Data, account, drive, token);
        }, token);
    }

    public async Task AccountOperationAsync(Func<Task> action)
    {
        await WaitForRefreshAsync(CancellationToken.None);
        if (Busy || stopping) throw new InvalidOperationException("Operation in progress.");
        loading = true; Notify();
        try { controlWrite = action(); await controlWrite; }
        finally { controlWrite = null; loading = false; await RefreshAsync(); }
    }
    public Task<T> ExclusiveAsync<T>(Func<T> operation) => ExclusiveAsync(_ => operation());

    public async Task<T> ExclusiveAsync<T>(Func<CancellationToken, T> operation, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        await WaitForRefreshAsync(cancellationToken);
        if (Busy || stopping) throw new InvalidOperationException("正在执行操作，请等待当前任务安全结束后再应用配置。");
        T result = default!;
        var task = coordinator.TryStart(token =>
        {
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(token, cancellationToken);
            linked.Token.ThrowIfCancellationRequested();
            result = operation(linked.Token);
            return Task.CompletedTask;
        }) ?? throw new InvalidOperationException("任务繁忙。");
        Notify();
        try { await task; return result; }
        finally { await RefreshAsync(includeHistory: true); Notify(); }
    }

    private async Task WaitForRefreshAsync(CancellationToken token)
    {
        if (loading && refreshFinished is { Task.IsCompleted: false } refresh)
            await refresh.Task.WaitAsync(token);
    }

    public async Task SetPausedAsync(bool paused)
    {
        if (config is null || stopping || controlWrite is { IsCompleted: false }) return;
        var previous = coordinator.Paused;
        coordinator.Paused = paused;
        var store = new DeviceControlStore(config.Preview);
        controlWrite = Task.Run(() => store.SetPaused(paused));
        Notify();
        try { await controlWrite; }
        catch (Exception ex) { coordinator.Paused = previous; error = CloudAccountPresentation.Error(ex); }
        finally { controlWrite = null; Notify(); }
    }

    public void Cancel() { recoveryRetries.Clear(); coordinator.CancelCurrent(); status = "正在停止，等待安全边界"; Notify(); }
    public async Task StopAsync()
    {
        stopping = true;
        recoveryRetries.Clear();
        status = "正在安全退出";
        Notify();
        try { await coordinator.StopAsync(); }
        finally { if (controlWrite is { } pending) await pending; }
    }
}
