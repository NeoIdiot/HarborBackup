using HarborBackup.Core.Application;

namespace HarborBackup.Core.Tests;

[TestClass]
public sealed class SchedulingTests
{
    [TestMethod]
    public void ClockChangesAndSleepProduceOnlyOneDueTrigger()
    {
        var clock = new ManualClock();
        var schedule = new BackupSchedule(TimeSpan.FromMinutes(30), TimeSpan.FromMinutes(2), clock);
        clock.Utc = clock.Utc.AddDays(1);
        Assert.IsFalse(schedule.TakeDueTrigger(), "Wall-clock changes cannot trigger a monotonic timer.");
        clock.Elapsed = TimeSpan.FromHours(8);
        Assert.IsTrue(schedule.TakeDueTrigger());
        Assert.IsFalse(schedule.TakeDueTrigger(), "Missed runs are not queued.");
        Assert.AreEqual(clock.Utc.AddMinutes(30), schedule.NextDueUtc);
        clock.Elapsed += TimeSpan.FromMinutes(30);
        Assert.IsTrue(schedule.TakeDueTrigger());
    }

    [TestMethod]
    public async Task BusyPausedAndStoppingTriggersAreRejectedWithoutQueueing()
    {
        var coordinator = new RunCoordinator();
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var finish = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var run = coordinator.TryStart(async _ => { started.SetResult(); await finish.Task; });
        Assert.IsNotNull(run);
        await started.Task;
        Assert.IsNull(coordinator.TryStart(_ => Task.CompletedTask, automatic: true));
        coordinator.Paused = true;
        Assert.IsTrue(coordinator.IsBusy);
        finish.SetResult();
        await run;
        Assert.IsNull(coordinator.TryStart(_ => Task.CompletedTask, automatic: true));
        var manual = coordinator.TryStart(_ => Task.CompletedTask);
        Assert.IsNotNull(manual);
        await manual;
        await coordinator.StopAsync();
        Assert.IsNull(coordinator.TryStart(_ => Task.CompletedTask));
    }

    [TestMethod]
    public async Task StopWaitsForOperationToReachSafeBoundary()
    {
        var coordinator = new RunCoordinator();
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var boundary = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        CancellationToken observed = default;
        var run = coordinator.TryStart(async token => { observed = token; started.SetResult(); await boundary.Task; });
        await started.Task;
        var stop = coordinator.StopAsync();
        Assert.IsTrue(observed.IsCancellationRequested);
        Assert.IsFalse(stop.IsCompleted);
        boundary.SetResult();
        await stop;
        Assert.IsTrue(run!.IsCompleted);
    }

    [TestMethod]
    public async Task CancellingOneOperationDoesNotDisableLaterManualRuns()
    {
        var coordinator = new RunCoordinator();
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var boundary = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        CancellationToken observed = default;
        var first = coordinator.TryStart(async token => { observed = token; started.SetResult(); await boundary.Task; });
        await started.Task;
        coordinator.CancelCurrent();
        Assert.IsTrue(observed.IsCancellationRequested);
        Assert.IsFalse(coordinator.IsStopping);
        boundary.SetResult();
        await first!;
        var second = coordinator.TryStart(token => { Assert.IsFalse(token.IsCancellationRequested); return Task.CompletedTask; });
        Assert.IsNotNull(second);
        await second;
        await coordinator.StopAsync();
    }

    private sealed class ManualClock : TimeProvider
    {
        public DateTimeOffset Utc { get; set; } = DateTimeOffset.Parse("2026-10-28T00:00:00Z");
        public TimeSpan Elapsed { get; set; }
        public override DateTimeOffset GetUtcNow() => Utc;
        public override long TimestampFrequency => TimeSpan.TicksPerSecond;
        public override long GetTimestamp() => Elapsed.Ticks;
    }
}
