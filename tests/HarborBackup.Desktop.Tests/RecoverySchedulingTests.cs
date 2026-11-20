using System.Collections.Concurrent;
using HarborBackup.Core.Domain;
using HarborBackup.Desktop.Controllers;
using HarborBackup.OneDrive.Transport;
using HarborBackup.Windows.Integration;
using HarborBackup.Windows.Persistence;

namespace HarborBackup.Windows.Tests;

[TestClass]
[DoNotParallelize]
public sealed class RecoverySchedulingTests
{
    [TestMethod]
    public Task OfflineDueRunsOnceAfterStableReconnect() => OnUiAsync(async f =>
    {
        f.Online = false;
        await f.Controller.TickAsync();
        f.Clock.Advance(TimeSpan.FromHours(1));
        await f.Controller.TickAsync();
        Assert.IsEmpty(f.Requests);
        f.Online = true;
        await f.Controller.TickAsync();
        Assert.IsEmpty(f.Requests, "A connectivity edge is debounced before starting work.");
        f.Clock.Advance(TimeSpan.FromSeconds(2));
        await f.Controller.TickAsync();
        Assert.HasCount(1, f.Requests);
        Assert.IsFalse(f.Requests.Single().RetryOnly);
        await f.Controller.TickAsync();
        Assert.HasCount(1, f.Requests);
        Assert.AreEqual(f.Clock.GetUtcNow().AddMinutes(30), f.Controller.State.NextDue);
    });

    [TestMethod]
    public Task BusyAndPausedDoNotConsumeScheduledWork() => OnUiAsync(async f =>
    {
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var exclusive = f.Controller.ExclusiveAsync(_ =>
        {
            started.TrySetResult();
            release.Task.WaitAsync(TimeSpan.FromSeconds(5)).GetAwaiter().GetResult();
            return true;
        });
        await started.Task;
        try { await f.Controller.TickAsync(); Assert.IsEmpty(f.Requests); }
        finally { release.TrySetResult(); await exclusive; }
        await f.Controller.SetPausedAsync(true);
        await f.Controller.TickAsync();
        Assert.IsEmpty(f.Requests);
        await f.Controller.SetPausedAsync(false);
        await f.Controller.TickAsync();
        Assert.HasCount(1, f.Requests);
    });

    [TestMethod]
    public Task ReconnectRetriesOnlyTheAuthorizedProjectAndDoesNotResetTheRegularInterval() => OnUiAsync(async f =>
    {
        f.Run = (_, _) =>
        {
            if (f.Requests.Count == 1)
            {
                f.Candidates = [f.Candidate()];
                return Task.FromResult<object?>(f.NetworkFailure());
            }
            f.Candidates = [];
            return Task.FromResult<object?>(f.Success());
        };
        var originalDue = f.Clock.GetUtcNow().AddMinutes(30);
        await f.Controller.TickAsync();
        Assert.AreEqual("等待自动重试", f.Controller.State.Status);
        Assert.HasCount(1, f.Requests);
        f.Online = false; await f.Controller.TickAsync();
        f.Online = true; await f.Controller.TickAsync();
        Assert.HasCount(1, f.Requests);
        f.Clock.Advance(TimeSpan.FromSeconds(2));
        await f.Controller.TickAsync();
        Assert.HasCount(2, f.Requests);
        var retry = f.Requests.Last();
        Assert.IsTrue(retry.Automatic); Assert.IsTrue(retry.RetryOnly);
        Assert.AreEqual(f.Project.ProjectId, retry.ProjectId);
        await f.Controller.TickAsync();
        Assert.HasCount(2, f.Requests);
        Assert.AreEqual(originalDue, f.Controller.State.NextDue);
    });

    [TestMethod]
    public Task ServiceFailureRetriesWithoutAnOperatingSystemConnectivityEdge() => OnUiAsync(async f =>
    {
        f.Run = (_, _) =>
        {
            f.Candidates = f.Requests.Count < 3 ? [f.Candidate()] : [];
            return Task.FromResult<object?>(f.Candidates.Count > 0 ? f.NetworkFailure() : f.Success());
        };
        await f.Controller.TickAsync();
        f.Clock.Advance(TimeSpan.FromSeconds(14)); await f.Controller.TickAsync();
        Assert.HasCount(1, f.Requests);
        f.Clock.Advance(TimeSpan.FromSeconds(1)); await f.Controller.TickAsync();
        Assert.HasCount(2, f.Requests);
        f.Clock.Advance(TimeSpan.FromSeconds(29)); await f.Controller.TickAsync();
        Assert.HasCount(2, f.Requests);
        f.Clock.Advance(TimeSpan.FromSeconds(1)); await f.Controller.TickAsync();
        Assert.HasCount(3, f.Requests);
        Assert.IsTrue(f.Requests.Skip(1).All(r => r.RetryOnly));
    });

    [TestMethod]
    public Task ReconnectCannotBypassThePersistedProviderCooldown() => OnUiAsync(async f =>
    {
        var deadline = f.Clock.GetUtcNow().AddMinutes(10);
        f.Run = (_, _) =>
        {
            f.Candidates = f.Requests.Count == 1 ? [f.Candidate(deadline)] : [];
            return Task.FromResult<object?>(f.Candidates.Count > 0 ? f.NetworkFailure(deadline) : f.Success());
        };
        await f.Controller.TickAsync();
        f.Online = false; await f.Controller.TickAsync(); f.Online = true; await f.Controller.TickAsync();
        f.Clock.Advance(TimeSpan.FromSeconds(2)); await f.Controller.TickAsync();
        Assert.HasCount(1, f.Requests);
        Assert.AreEqual(deadline, f.Controller.State.NextDue);
        f.Clock.Advance(TimeSpan.FromMinutes(10)); await f.Controller.TickAsync();
        Assert.HasCount(2, f.Requests); Assert.IsTrue(f.Requests.Last().RetryOnly);
    });

    [TestMethod]
    public Task RetryStartupFailureWithoutAReportDoesNotLeaveTheCandidateRunningForever() => OnUiAsync(async f =>
    {
        f.Run = (_, _) =>
        {
            if (f.Requests.Count == 1)
            {
                f.Candidates = [f.Candidate()]; return Task.FromResult<object?>(f.NetworkFailure());
            }
            if (f.Requests.Count == 2) throw new IOException("Synthetic execution lease unavailable before a report.");
            f.Candidates = []; return Task.FromResult<object?>(f.Success());
        };
        await f.Controller.TickAsync();
        f.Clock.Advance(TimeSpan.FromSeconds(15)); await f.Controller.TickAsync();
        Assert.HasCount(2, f.Requests);
        Assert.AreEqual(f.Clock.GetUtcNow().AddSeconds(30), f.Controller.State.NextDue);
        f.Clock.Advance(TimeSpan.FromSeconds(29)); await f.Controller.TickAsync();
        Assert.HasCount(2, f.Requests);
        f.Clock.Advance(TimeSpan.FromSeconds(1)); await f.Controller.TickAsync();
        Assert.HasCount(3, f.Requests); Assert.IsTrue(f.Requests.Last().RetryOnly);
        Assert.AreEqual("空闲", f.Controller.State.Status);
    });

    [TestMethod]
    public Task AReportlessFailureCannotRecreateAStoppedDurableCandidate() => OnUiAsync(async f =>
    {
        var regularDue = f.Clock.GetUtcNow().AddMinutes(30);
        f.Run = (_, _) =>
        {
            if (f.Requests.Count == 1)
            {
                f.Candidates = [f.Candidate()]; return Task.FromResult<object?>(f.NetworkFailure());
            }
            f.Candidates = [];
            throw new IOException("Task was stopped while its execution could not start.");
        };
        await f.Controller.TickAsync();
        f.Clock.Advance(TimeSpan.FromSeconds(15)); await f.Controller.TickAsync();
        f.Clock.Advance(TimeSpan.FromMinutes(1)); await f.Controller.TickAsync();
        Assert.HasCount(2, f.Requests);
        Assert.AreEqual(regularDue, f.Controller.State.NextDue);
    });

    [TestMethod]
    public Task PausePreventsConnectivityWakeupUntilTheUserResumes() => OnUiAsync(async f =>
    {
        f.Run = (_, _) =>
        {
            f.Candidates = f.Requests.Count == 1 ? [f.Candidate()] : [];
            return Task.FromResult<object?>(f.Candidates.Count > 0 ? f.NetworkFailure() : f.Success());
        };
        await f.Controller.TickAsync();
        await f.Controller.SetPausedAsync(true);
        f.Online = false; await f.Controller.TickAsync(); f.Online = true; await f.Controller.TickAsync();
        f.Clock.Advance(TimeSpan.FromMinutes(1)); await f.Controller.TickAsync();
        Assert.HasCount(1, f.Requests);
        await f.Controller.SetPausedAsync(false);
        await f.Controller.TickAsync();
        Assert.HasCount(2, f.Requests); Assert.IsTrue(f.Requests.Last().RetryOnly);
    });

    [TestMethod]
    public Task AutomaticBudgetStopWaitsForTheRegularCycle() => OnUiAsync(async f =>
    {
        f.Run = (_, _) => Task.FromResult<object?>(new BackupReport("AutomaticUpload", f.Clock.GetUtcNow(), f.Clock.GetUtcNow(),
            [new(f.Project.Destination, [new("a.txt", FileAction.Deferred, false, "AutomaticTransferBudgetReached") { FailureKind = FileFailureKind.Budget }],
                [new("AutomaticTransferBudgetReached", "AutomaticTransferBudgetReached", false)], new()) { ProjectId = f.Project.ProjectId }])
                { BudgetStopReason = "AutomaticTransferBudgetReached" });
        await f.Controller.TickAsync();
        Assert.AreEqual("预算已用完，下轮继续", f.Controller.State.Status);
        f.Online = false; await f.Controller.TickAsync(); f.Online = true; await f.Controller.TickAsync();
        f.Clock.Advance(TimeSpan.FromMinutes(1)); await f.Controller.TickAsync();
        Assert.HasCount(1, f.Requests);
        f.Clock.Advance(TimeSpan.FromMinutes(30)); await f.Controller.TickAsync();
        Assert.HasCount(2, f.Requests); Assert.IsFalse(f.Requests.Last().RetryOnly);
    });

    [TestMethod]
    public Task CancelingAnAttemptDoesNotGetRestartedByReconnect() => OnUiAsync(async f =>
    {
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        f.Run = async (_, token) =>
        {
            if (f.Requests.Count == 1)
            {
                f.Candidates = [f.Candidate()]; return f.NetworkFailure();
            }
            started.TrySetResult();
            try { await Task.Delay(Timeout.Infinite, token); return f.Success(); }
            finally { f.Candidates = []; }
        };
        await f.Controller.TickAsync();
        f.Clock.Advance(TimeSpan.FromSeconds(15));
        var attempt = f.Controller.TickAsync();
        await started.Task;
        f.Controller.Cancel();
        await attempt;
        f.Online = false; await f.Controller.TickAsync(); f.Online = true; await f.Controller.TickAsync();
        f.Clock.Advance(TimeSpan.FromSeconds(20)); await f.Controller.TickAsync();
        Assert.HasCount(2, f.Requests);
        Assert.AreEqual("已安全停止", f.Controller.State.Status);
    });

    [TestMethod]
    public Task CurrentTaskCandidatesAreRediscoveredAfterRestartAndStaleBindingsAreIgnored() => OnUiAsync(async f =>
    {
        f.Workspace.WriteSystemConfig(new { sync = new { enabled = true }, schedule = new { intervalMinutes = 30, startDelayMinutes = 2 } });
        // A fresh controller has no in-memory report or retry history. Its trusted
        // state reader supplies the original project's pending network task.
        f.Candidates = [f.Candidate() with { Binding = "stale-mapping" }];
        var controller = f.CreateController();
        await controller.RefreshAsync();
        f.Clock.Advance(TimeSpan.FromSeconds(30)); await controller.TickAsync();
        Assert.IsEmpty(f.Requests);
        f.Candidates = [f.Candidate()];
        await controller.RefreshAsync();
        f.Clock.Advance(TimeSpan.FromSeconds(15)); await controller.TickAsync();
        Assert.HasCount(1, f.Requests); Assert.IsTrue(f.Requests.Single().RetryOnly);
        f.Clock.Advance(TimeSpan.FromSeconds(20)); await controller.TickAsync();
        Assert.HasCount(1, f.Requests, "Completed state is not rediscovered as another retry.");
    });

    [TestMethod]
    public Task SummaryPersistsRecoverySeparatelyFromFailureAndRetryDeadline() => OnUiAsync(f =>
    {
        var deadline = f.Clock.GetUtcNow().AddMinutes(10);
        var report = f.NetworkFailure(deadline);
        RunStatusStore.Write(f.Config, report);
        var status = RunStatusStore.Read(f.Config.Preview)!;
        var row = status["projects"]!.AsArray().Single()!;
        Assert.AreEqual("WaitingForRetry", row["lastResult"]!.GetValue<string>());
        Assert.AreEqual("AutomaticRetry", row["recovery"]!.GetValue<string>());
        Assert.AreEqual(deadline, row["retryNotBeforeUtc"]!.GetValue<DateTimeOffset>());
        Assert.AreEqual(0, row["consecutiveFailures"]!.GetValue<int>());
        return Task.CompletedTask;
    });

    private sealed class Fixture : IDisposable
    {
        internal readonly TestWorkspace Workspace = new();
        internal readonly Clock Clock = new();
        internal bool Online = true;
        internal IReadOnlyList<CloudAutomaticRetryCandidate> Candidates = [];
        internal readonly ConcurrentQueue<DesktopRunRequest> Requests = new();
        internal Func<DesktopRunRequest, CancellationToken, Task<object?>>? Run;
        internal DesktopController Controller = null!;
        internal ConfigurationSnapshot Config => Controller.State.Configuration!;
        internal ProjectDefinition Project => Config.Projects.Single();
        internal RuntimePaths Paths => new(Path.Combine(Workspace.Config, "projects.json"), Path.Combine(Workspace.Config, "backup-settings.json"));
        internal DesktopController CreateController() => new(Paths, networkAvailable: () => Online, timeProvider: Clock,
            operationRunner: (request, token) =>
            {
                Requests.Enqueue(request);
                if (Run is not null) return Run(request, token);
                Candidates = [];
                return Task.FromResult<object?>(Success());
            },
            resumeCandidateReader: _ => Candidates, retryJitter: () => .5);
        internal async Task InitializeAsync()
        {
            Workspace.WriteSystemConfig(new { sync = new { enabled = true }, schedule = new { intervalMinutes = 30, startDelayMinutes = 1 } });
            Controller = CreateController(); await Controller.RefreshAsync();
            Assert.IsNotNull(Controller.State.Configuration, Controller.State.Error);
            Clock.Advance(TimeSpan.FromMinutes(1));
        }
        internal CloudAutomaticRetryCandidate Candidate(DateTimeOffset? deadline = null) =>
            new(Project.ProjectId, CloudMapping.Bind(Project, Config), deadline);
        internal BackupReport Success() => new("AutomaticUpload", Clock.GetUtcNow(), Clock.GetUtcNow(),
            [new(Project.Destination, [], [], new()) { ProjectId = Project.ProjectId, CoverageComplete = true }]);
        internal BackupReport NetworkFailure(DateTimeOffset? deadline = null) => new("AutomaticUpload", Clock.GetUtcNow(), Clock.GetUtcNow(),
            [new(Project.Destination, [new("a.txt", FileAction.Deferred, false, CloudNetworkAvailability.Unreachable)
                { FailureKind = FileFailureKind.Network, Recovery = RecoveryDisposition.AutomaticRetry }], [], new())
                { ProjectId = Project.ProjectId, Recovery = RecoveryDisposition.AutomaticRetry, RetryNotBeforeUtc = deadline }]);
        public void Dispose() => Workspace.Dispose();
    }

    private sealed class Clock : TimeProvider
    {
        private DateTimeOffset utc = DateTimeOffset.Parse("2026-11-20T00:00:00Z");
        private TimeSpan elapsed;
        public override DateTimeOffset GetUtcNow() => utc;
        public override long TimestampFrequency => TimeSpan.TicksPerSecond;
        public override long GetTimestamp() => elapsed.Ticks;
        internal void Advance(TimeSpan duration) { elapsed += duration; utc += duration; }
    }

    private static async Task OnUiAsync(Func<Fixture, Task> check)
    {
        var completed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            using var host = new Form();
            host.Shown += async (_, _) =>
            {
                try { using var fixture = new Fixture(); await fixture.InitializeAsync(); await check(fixture); completed.TrySetResult(); }
                catch (Exception ex) { completed.TrySetException(ex); }
                finally { host.Close(); }
            };
            Application.Run(host);
        }) { IsBackground = true };
        thread.SetApartmentState(ApartmentState.STA); thread.Start();
        await completed.Task.WaitAsync(TimeSpan.FromSeconds(30));
        Assert.IsTrue(thread.Join(TimeSpan.FromSeconds(3)));
    }
}
