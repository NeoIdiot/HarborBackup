using HarborBackup.Core.Domain;
using HarborBackup.OneDrive.Transport;
using HarborBackup.Windows.Persistence;
using HarborBackup.Windows.Persistence.Configuration;

namespace HarborBackup.Windows.Integration;

public sealed class CloudBackupRuntime(ConfigurationSnapshot config, Func<ICloudDrive>? driveFactory = null, int fileConcurrency = 8, TimeProvider? timeProvider = null)
{
    private readonly int concurrency = fileConcurrency is >= 1 and <= 8 ? fileConcurrency : throw new ArgumentOutOfRangeException(nameof(fileConcurrency));
    private CloudAccountSettings Account => config.CloudAccount ?? throw new InvalidOperationException("Missing account binding.");
    private DataPaths Data => DataPaths.FromState(config.Preview.StateDirectory);
    private ICloudDrive Open() => driveFactory?.Invoke() ?? new CloudAccountStore(Data).Open(Account,
        new GraphTransferOptions(UploadBytesPerSecond: config.UploadKiBPerSecond * 1024L, DownloadBytesPerSecond: config.DownloadKiBPerSecond * 1024L));
    public async Task<PreviewReport> PreviewAsync(SyncDirection direction, string? destination, bool full, CancellationToken token, IProgress<BackupProgress>? progress = null, bool prepareUpload = false)
    {
        using var drive = Open(); var projects = new List<ProjectPreview>();
        var selections = new List<PullFileSelection>();
        var explicitChoice = destination is not null && (direction == SyncDirection.Pull || prepareUpload);
        var recovery = CloudMapping.ScanPending(config);
        var pending = recovery.Files;
        foreach (var project in BackupRuntime.Select(config, destination))
        {
            var metrics = new ReadMetrics();
            var networkBefore = (drive as ICloudDiagnostics)?.NetworkMetrics;
            try
            {
                if (project.ConfigurationError is not null) throw new FormatException(project.ConfigurationError);
                var session = new CloudBackupSession(project, config, Account, drive) { Metrics = metrics };
                var problems = recovery.Problems.Where(p => p.Affects(project)).ToArray();
                var uncertainPending = problems.Length > 0;
                var affected = pending.Where(p => p.Affects(project)).ToArray();
                var needsFirstOperation = CloudMapping.NeedsFirstOperation(project, config);
                var read = await session.ReadAsync(full, token, progress, metrics, allowMissingRoot: direction == SyncDirection.Pull);
                var blocked = affected.Where(p => !read.Snapshot.Files.Any(f => p.CanRestartPull(project, f, direction))).ToArray();
                var issues = uncertainPending
                    ? new[] { new PreviewIssue("PendingOperationsUnverified", "恢复记录暂时无法核验；仍可查看文件，写入前需处理恢复记录。" + Environment.NewLine +
                        string.Join(Environment.NewLine, problems.Select(p => p.Description)), false) }
                    : blocked.Length == 0 ? [] : new[] { new PreviewIssue("PendingOperationsRetained", "未完成操作涉及的文件暂不可写入；其他文件仍可选择，旧恢复材料会保留。", false) };
                var plans = read.Snapshot.Files.Select(f =>
                {
                    token.ThrowIfCancellationRequested();
                    var heads = read.Heads.GetValueOrDefault(f.RelativePath) ?? [];
                    var restarts = affected.Where(p => p.CanRestartPull(project, f, direction)).ToArray();
                    var decision = uncertainPending || affected.Any(p => p.Blocks(project, f, direction))
                        ? new FileDecision(FileAction.Deferred, false, FileSide.None, FileSide.None, ProtectionKind.None, "PendingFileOperation")
                        : PendingCloudFile.Describe(session.Decide(f, heads, direction, explicitChoice), restarts);
                    if ((direction == SyncDirection.Pull || prepareUpload) && (decision.Action is FileAction.Copy or FileAction.CreateDirectory ||
                        decision.Action == FileAction.Equal && needsFirstOperation))
                        selections.Add(PendingCloudFile.Confirm(session.ConfirmPull(f, heads, decision, explicitChoice, direction), restarts));
                    return new PreviewPlan(f.RelativePath, decision, f.Source.Verified, f.Target.Verified, f.Source.Error, f.Target.Error)
                    {
                        Verification = VerificationSummary.Classify(f.Source, f.Target, heads.All(h => h.ContentHashKnown)),
                        TransferBytes = direction == SyncDirection.Backup ? f.Source.Version?.Length ?? 0 : f.Target.Version?.Length ?? 0
                    };
                }).ToArray();
                string DisplayRule(string rule) => Path.IsPathRooted(rule) ? Path.GetRelativePath(project.Source, rule).Replace('\\', '/') : rule;
                projects.Add(new(project.Destination, plans, issues, read.Snapshot.Metrics)
                { ProjectId = project.ProjectId, ExcludedDirectories = project.ExcludedDirectories.Select(DisplayRule).ToArray(),
                  ExcludedFiles = project.ExcludedFiles.Select(DisplayRule).ToArray(), ExclusionSources = project.ExclusionSources });
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex) { projects.Add(new(project.Destination, [], [new("CloudPreviewFailed", ex.Message)], metrics) { ProjectId = project.ProjectId }); }
            finally { CaptureNetworkMetrics(drive, networkBefore, metrics); }
        }
        return new(projects) { CloudUploadStatus = "ReadOnlyCloudObservation",
            PullSelection = direction == SyncDirection.Pull && destination is not null && projects.Count == 1
                ? new(projects[0].Destination, explicitChoice, selections) { ProjectId = BackupRuntime.Select(config, destination).Single().ProjectId } : null,
            UploadSelection = direction == SyncDirection.Backup && prepareUpload && destination is not null && projects.Count == 1
                ? new(projects[0].Destination, explicitChoice, selections) { ProjectId = BackupRuntime.Select(config, destination).Single().ProjectId } : null };
    }
    public async Task<BackupReport> ExecuteAsync(SyncDirection direction, string? destination, bool full, bool recoverOnly,
        IProgress<BackupProgress>? progress, CancellationToken token, PullSelection? confirmedPull = null,
        bool automatic = false, PullSelection? confirmedUpload = null)
    {
        if (automatic && (direction != SyncDirection.Backup || recoverOnly || confirmedPull is not null || confirmedUpload is not null))
            throw new ArgumentException("Automatic execution requires an unconfirmed backup.");
        if (confirmedPull is not null && (destination is null || direction != SyncDirection.Pull || recoverOnly ||
            BackupRuntime.Select(config, destination).Single().ProjectId != confirmedPull.ProjectId ||
            confirmedPull.Files.Select(f => f.Path).Distinct(StringComparer.OrdinalIgnoreCase).Count() != confirmedPull.Files.Count))
            throw new ArgumentException("Invalid confirmed pull selection.");
        if (confirmedUpload is not null && (destination is null || direction != SyncDirection.Backup || automatic || recoverOnly || confirmedPull is not null ||
            BackupRuntime.Select(config, destination).Single().ProjectId != confirmedUpload.ProjectId ||
            confirmedUpload.Files.Select(f => f.Path).Distinct(StringComparer.OrdinalIgnoreCase).Count() != confirmedUpload.Files.Count))
            throw new ArgumentException("Invalid confirmed upload selection.");
        var confirmation = confirmedPull ?? confirmedUpload;
        var started = DateTimeOffset.UtcNow; var runs = new List<ProjectRun>(); var cancelled = false;
        var budget = new AutomaticRunBudget(automatic ? config.AutomaticBudget : new(), timeProvider ?? TimeProvider.System);
        var recovery = CloudMapping.ScanPending(config);
        var pendingFiles = recovery.Files;
        var pending = pendingFiles.Select(p => p.Mapping).DistinctBy(m => m.Binding).ToArray();
        var selectedProjects = recoverOnly ? config.Projects.Concat(pending.Select(m => m.Project))
            .Where(p => destination is null || p.ProjectId == destination || p.Destination.Equals(destination, StringComparison.OrdinalIgnoreCase))
            .DistinctBy(p => CloudMapping.Bind(p, config)).ToArray() : BackupRuntime.Select(config, destination);
        if (destination is not null && selectedProjects.Count == 0) throw new ArgumentException("Unknown destination.");
        using var drive = Open();
        foreach (var project in selectedProjects)
        {
            var files = new List<FileResult>(); var issues = new List<PreviewIssue>(); var metrics = new ReadMetrics();
            var completedMapping = false;
            var coverageComplete = false;
            var networkBefore = (drive as ICloudDiagnostics)?.NetworkMetrics;
            try
            {
                token.ThrowIfCancellationRequested();
                if (budget.StopReason is not null)
                {
                    runs.Add(new(project.Destination, [], [new(budget.StopReason, budget.StopReason, false)], metrics) { ProjectId = project.ProjectId });
                    continue;
                }
                // Include transactions created by a prior project in this run.
                recovery = CloudMapping.ScanPending(config);
                pendingFiles = recovery.Files;
                if (project.ConfigurationError is not null) throw new FormatException(project.ConfigurationError);
                var problems = recovery.Problems.Where(p => p.Affects(project)).ToArray();
                if (problems.Length > 0) throw new IOException("恢复记录未通过核验，写入已暂停；配置仍可编辑。" + Environment.NewLine +
                    string.Join(Environment.NewLine, problems.Select(p => p.Description)));
                if (!recoverOnly && automatic && CloudMapping.NeedsFirstOperation(project, config))
                {
                    runs.Add(new(project.Destination, [], [new("MappingAwaitingOperation", "本地目录已切换，请先手动上传或拉取。", false)], metrics) { ProjectId = project.ProjectId });
                    continue;
                }
                var session = new CloudBackupSession(project, config, Account, drive) { Metrics = metrics };
                progress?.Report(new(BackupPhase.Recovery, project.Destination));
                if (recoverOnly) await session.RecoverAsync(token, files.Add,
                    retryUploads: config.Projects.Any(p => CloudMapping.Bind(p, config) == CloudMapping.Bind(project, config)));
                if (!recoverOnly)
                {
                    if (!automatic && direction == SyncDirection.Backup && confirmation is null && session.Pending)
                        await session.RecoverAsync(token, files.Add, retryUploads: true, backupOnly: true);
                    var retryResults = files.GroupBy(f => f.Path, StringComparer.OrdinalIgnoreCase)
                        .ToDictionary(g => g.Key, g => g.Last(), StringComparer.OrdinalIgnoreCase);
                    var affected = pendingFiles.Where(p => p.Affects(project) &&
                        (p.Mapping.Binding != CloudMapping.Bind(project, config) || session.Pending)).ToArray();
                    var retainedCurrentTransactions = affected.Any(p => p.Mapping.Binding == CloudMapping.Bind(project, config));
                    progress?.Report(new(BackupPhase.Scanning, project.Destination));
                    var read = await session.ReadAsync(full, token, progress, metrics, deferContentReads: direction == SyncDirection.Backup,
                        allowMissingRoot: direction == SyncDirection.Pull); metrics = read.Snapshot.Metrics;
                    var blocked = affected.Where(p => !read.Snapshot.Files.Any(f => p.CanRestartPull(project, f, direction))).ToArray();
                    if (blocked.Length > 0) issues.Add(new("PreviousMappingPending",
                        "未完成操作涉及的文件已跳过；其他文件可继续。恢复仍使用原目录，旧材料已保留。", false));
                    session.Progress = (phase, path) => progress?.Report(new(phase, project.Destination, path, files.Count, read.Snapshot.Files.Count, metrics.HashBytes));
                    var selected = confirmation?.Files.ToDictionary(f => f.Path, StringComparer.OrdinalIgnoreCase);
                    var incompleteSelection = blocked.Length > 0;
                    if (selected is not null)
                        foreach (var missing in selected.Keys.Except(read.Snapshot.Files.Select(f => f.RelativePath), StringComparer.OrdinalIgnoreCase))
                            files.Add(new(missing, FileAction.Deferred, false, "PullPreviewChanged"));
                    // Bounded workers continuously refill; directories remain dependency barriers.
                    // Only proven file-local failures may continue; in-flight transactions are always drained.
                    var batch = new List<Task<(FileResult Result, bool Cancelled)>>(concurrency);
                    var window = new CloudConcurrencyWindow(concurrency, networkBefore?.ThrottledRequests ?? 0);
                    window.Observe((drive as ICloudDiagnostics)?.NetworkMetrics.ThrottledRequests ?? 0, 0, true);
                    metrics.CloudConcurrencyReductions = window.Reductions;
                    var stopDispatch = 0;
                    async Task<(FileResult Result, bool Cancelled)> ProcessAsync(PreviewFile file)
                    {
                        var action = FileAction.Deferred;
                        var preparingLocal = false;
                        var elapsed = System.Diagnostics.Stopwatch.StartNew();
                        try
                        {
                            token.ThrowIfCancellationRequested();
                            if (!budget.TryStart(0, false))
                            {
                                Interlocked.Exchange(ref stopDispatch, 1);
                                return (new(file.RelativePath, FileAction.Deferred, false, budget.StopReason) { FailureKind = FileFailureKind.Budget }, false);
                            }
                            if (retryResults.TryGetValue(file.RelativePath, out var retry)) return (retry, false);
                            if (affected.Any(p => p.Blocks(project, file, direction)))
                                return (new(file.RelativePath, FileAction.Deferred, false, "PendingFileOperation"), false);
                            progress?.Report(new(BackupPhase.Processing, project.Destination, file.RelativePath, files.Count, read.Snapshot.Files.Count, metrics.HashBytes));
                            var heads = read.Heads.GetValueOrDefault(file.RelativePath) ?? [];
                            preparingLocal = true;
                            await using var prepared = direction == SyncDirection.Backup
                                ? await session.PrepareBackupAsync(file, heads, token, force: confirmedUpload is not null) : null;
                            if (prepared is not null) file = prepared.File;
                            if (direction == SyncDirection.Pull) file = await session.RefreshPullLocalAsync(file, token);
                            preparingLocal = false;
                            if (affected.Any(p => p.Blocks(project, file, direction)))
                                return (new(file.RelativePath, FileAction.Deferred, false, "PendingFileOperation"), false);
                            var restarts = affected.Where(p => p.CanRestartPull(project, file, direction)).ToArray();
                            var decision = PendingCloudFile.Describe(session.Decide(file, heads, direction, confirmation?.EstablishBaseline ?? false), restarts);
                            action = decision.Action;
                            if (decision.Action == FileAction.Deferred && file.Source.Kind is ObservationKind.Unavailable or ObservationKind.Unstable)
                                return (new(file.RelativePath, decision.Action, false, file.Source.Error) { FailureKind = FileFailureKind.FileUnavailable }, false);
                            if (selected is not null)
                            {
                                if (decision.Action is not (FileAction.Copy or FileAction.CreateDirectory or FileAction.Equal) ||
                                    PendingCloudFile.Confirm(session.ConfirmPull(file, heads, decision, confirmation!.EstablishBaseline, direction), restarts).Confirmation != selected[file.RelativePath].Confirmation)
                                    return (new(file.RelativePath, FileAction.Deferred, false, "PullPreviewChanged"), false);
                            }
                            if (!budget.TryStart(file.Source.Version?.Length ?? 0,
                                decision.Action is FileAction.Copy or FileAction.CreateDirectory || decision.Protection != ProtectionKind.None))
                            {
                                Interlocked.Exchange(ref stopDispatch, 1);
                                return (new(file.RelativePath, FileAction.Deferred, false, budget.StopReason) { FailureKind = FileFailureKind.Budget }, false);
                            }
                            if (decision.Action == FileAction.Copy)
                                foreach (var pendingFile in restarts) pendingFile.Restart!.Retain(config);
                            await session.ApplyAsync(file, heads, direction, decision, token);
                            return (new(file.RelativePath, decision.Action, decision.Action is FileAction.Copy or FileAction.CreateDirectory,
                                decision.Reason == "CloudMetadataRequired" ? "CloudMetadataRequired" : null), false);
                        }
                        catch (OperationCanceledException)
                        { Interlocked.Exchange(ref stopDispatch, 1); return (new(file.RelativePath, action, false, "Cancelled") { FailureKind = FileFailureKind.Cancelled }, true); }
                        catch (Exception ex)
                        {
                            var kind = FileFailurePolicy.Classify(ex, preparingLocal);
                            if (kind != FileFailureKind.FileUnavailable) Interlocked.Exchange(ref stopDispatch, 1);
                            return (new(file.RelativePath, action, false, ex.Message) { FailureKind = kind }, false);
                        }
                        finally { lock (metrics) metrics.FileProcessingMilliseconds += elapsed.ElapsedMilliseconds; }
                    }
                    async Task<bool> DrainAsync(bool all = true)
                    {
                        if (batch.Count == 0) return Volatile.Read(ref stopDispatch) == 0 && (retainedCurrentTransactions || !session.Pending);
                        if (all) await Task.WhenAll(batch);
                        else await Task.WhenAny(batch);
                        var completed = batch.Where(t => t.IsCompleted).ToArray();
                        var results = await Task.WhenAll(completed);
                        window.Observe((drive as ICloudDiagnostics)?.NetworkMetrics.ThrottledRequests ?? 0, results.Length, results.All(r => r.Result.Error is null));
                        metrics.CloudConcurrencyReductions = window.Reductions;
                        foreach (var task in completed) batch.Remove(task);
                        files.AddRange(results.Select(r => r.Result).Where(r => !retryResults.ContainsKey(r.Path)));
                        if (results.Any(r => r.Cancelled) || token.IsCancellationRequested) throw new OperationCanceledException(token);
                        return Volatile.Read(ref stopDispatch) == 0 && (batch.Count != 0 || retainedCurrentTransactions || !session.Pending);
                    }
                    try
                    {
                        foreach (var file in read.Snapshot.Files)
                        {
                            if (selected is not null && !selected.ContainsKey(file.RelativePath))
                            {
                                var decision = session.Decide(file, read.Heads.GetValueOrDefault(file.RelativePath) ?? [], direction);
                                if (decision.Action is not (FileAction.Equal or FileAction.Directory or FileAction.Absent or FileAction.SourceOnly or FileAction.TargetOnly))
                                    incompleteSelection = true;
                                continue;
                            }
                            var directory = file.Source.Kind == ObservationKind.Directory || file.Target.Kind == ObservationKind.Directory;
                            if (directory && !await DrainAsync()) break;
                            var dispatch = true;
                            while (!directory && batch.Count >= window.Limit)
                                if (!await DrainAsync(all: false)) { dispatch = false; break; }
                            if (!dispatch) break;
                            if (token.IsCancellationRequested || Volatile.Read(ref stopDispatch) != 0) break;
                            batch.Add(ProcessAsync(file));
                            if (directory && !await DrainAsync()) break;
                        }
                    }
                    finally
                    {
                        // Cancellation or failure cannot dispose the drive while another transfer uses it.
                        try { if (batch.Count > 0) await DrainAsync(); }
                        finally { session.FlushLocalCache(); }
                    }
                    token.ThrowIfCancellationRequested();
                    if (budget.StopReason is not null) issues.Add(new(budget.StopReason, budget.StopReason, false));
                    if (files.Any(f => f.Error == CloudNetworkAvailability.Offline) && session.Pending)
                        issues.Add(new("PendingOperationsRetained", "未完成操作涉及的文件暂不可写入；其他文件仍可选择，旧恢复材料会保留。", false));
                    if (!automatic && !incompleteSelection && !session.Pending && files.All(f => f.Error is null &&
                        f.Action is FileAction.Copy or FileAction.CreateDirectory or FileAction.Equal or FileAction.Directory or FileAction.Absent or FileAction.SourceOnly or FileAction.TargetOnly))
                    {
                        if (direction == SyncDirection.Pull && read.Snapshot.Files.Count == 0)
                        {
                            FileSystem.SafePath.Validate(project.Source);
                            Directory.CreateDirectory(project.Source);
                        }
                        await session.CompleteMappingAsync(direction, token);
                        completedMapping = true;
                    }
                    coverageComplete = !incompleteSelection && budget.StopReason is null && !session.Pending &&
                        issues.Count == 0 && files.All(f => !f.NeedsAttention()) && files.Count >= read.Snapshot.Files.Count;
                    if (coverageComplete)
                    {
                        try { await session.PublishCheckpointAsync(token); }
                        catch (OperationCanceledException) { throw; }
                        catch (Exception ex) when (ex is IOException or HttpRequestException or UnauthorizedAccessException)
                        { issues.Add(new("MetadataCheckpointPending", "文件操作已完成，元数据检查点暂未发布；原始文件证据保留。" + Environment.NewLine + ex.Message, false)); }
                    }
                }
                files.Sort((a, b) => StringComparer.OrdinalIgnoreCase.Compare(a.Path, b.Path));
                if (completedMapping) CloudMapping.MarkReady(project, config);
                // Immutable per-device observations. Failed publication stays in the local outbox.
                progress?.Report(new(BackupPhase.Publishing, project.Destination, HashedBytes: metrics.HashBytes));
                try { await new CloudObservationPublisher(config, drive, sharedRepository: session.Repository).PublishAsync(project, files, metrics, token); }
                catch (OperationCanceledException) { throw; }
                catch (Exception ex) { issues.Add(new("DeviceStatusPending", "文件操作结果已记录；设备状态暂未上报，下次运行会重试。" + Environment.NewLine + ex.Message, false)); }
            }
            catch (OperationCanceledException) { cancelled = true; }
            catch (Exception ex) { issues.Add(new("CloudOperationFailed", ex.Message)); }
            CaptureNetworkMetrics(drive, networkBefore, metrics);
            runs.Add(new(project.Destination, files, issues, metrics) { ProjectId = project.ProjectId,
                CoverageComplete = coverageComplete && !cancelled && issues.Count == 0 && !recoverOnly });
            if (cancelled || (!config.Preview.ContinueOnError && (issues.Any(i => i.IsFailure) || files.Any(f => f.Error is not null)))) break;
        }
        var result = new BackupReport(recoverOnly ? "Recover" : direction.ToString(), started, DateTimeOffset.UtcNow, runs, cancelled)
        { BudgetStopReason = budget.StopReason };
        result = result with { CloudUploadStatus = result.Completion switch
            { "Cancelled" => "Incomplete", "BudgetLimited" => "BudgetLimited", "Completed" => "CloudOperationsConfirmed", _ => "NeedsAttention" } };
        return RunReportPersistence.Save(result, value => RunStatusStore.Write(config, value), value => RunStatusStore.Log(config, value));
    }
    private static void CaptureNetworkMetrics(ICloudDrive drive, CloudNetworkMetrics? before, ReadMetrics metrics)
    {
        if (before is null || drive is not ICloudDiagnostics diagnostics) return;
        var after = diagnostics.NetworkMetrics;
        metrics.CloudRequests = after.Requests - before.Requests;
        metrics.CloudRetries = after.Retries - before.Retries;
        metrics.CloudRetryWaitMilliseconds = after.RetryWaitMilliseconds - before.RetryWaitMilliseconds;
        metrics.CloudThrottledRequests = after.ThrottledRequests - before.ThrottledRequests;
    }
    public async Task<IReadOnlyList<CloudRevision>> RevisionsAsync(string? destination, CancellationToken token)
    {
        using var drive = Open(); var repository = new CloudRepository(drive, Account, Data);
        await repository.ValidateAsync(token);
        var result = new List<CloudRevision>();
        foreach (var project in BackupRuntime.Select(config, destination))
        {
            var key = CloudRepository.ProjectKey(project);
            var folder = await repository.FolderAsync("metadata/projects/" + key + "/files", false, token);
            if (folder is null) continue;
            foreach (var item in await drive.ChildrenAsync(folder.Id, token))
            {
                var metadata = await repository.ReadJsonAsync<CloudFileMetadata>(item, token);
                if (metadata.SchemaVersion != 2 || metadata.Revision.Project != key || item.Name != metadata.Revision.Id + ".json")
                    throw new IOException("Invalid shared file metadata.");
                result.Add(metadata.Revision);
            }
        }
        return result;
    }
}
