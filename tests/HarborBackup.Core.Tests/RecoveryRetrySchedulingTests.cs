using HarborBackup.Core.Application;

namespace HarborBackup.Core.Tests;

[TestClass]
public sealed class RecoveryRetrySchedulingTests
{
    private static readonly RecoveryRetryKey Key = new(new string('a', 64), "mapping-a");

    [TestMethod]
    public void InspectingDueWhileOfflineOrBusyDoesNotConsumeTheMergedTrigger()
    {
        var clock = new Clock();
        var schedule = new BackupSchedule(TimeSpan.FromMinutes(30), TimeSpan.FromMinutes(2), clock);
        clock.Advance(TimeSpan.FromHours(8));
        for (var i = 0; i < 5; i++) Assert.IsTrue(schedule.IsDue);
        Assert.AreEqual(clock.GetUtcNow(), schedule.NextDueUtc);
        Assert.IsTrue(schedule.AcceptDue());
        Assert.IsFalse(schedule.IsDue);
        Assert.IsFalse(schedule.AcceptDue(), "There is one merged due event, not a backlog of missed runs.");
        Assert.AreEqual(clock.GetUtcNow().AddMinutes(30), schedule.NextDueUtc);
    }

    [TestMethod]
    public void RediscoveringTheSameCandidateDoesNotPostponeItsRetry()
    {
        var clock = new Clock(); var retries = new RecoveryRetrySchedule(clock, () => .5);
        retries.Synchronize([new(Key)]);
        var due = retries.NextDueUtc;
        clock.Advance(TimeSpan.FromSeconds(10));
        retries.Synchronize([new(Key), new(Key)]);
        Assert.AreEqual(1, retries.Count);
        Assert.AreEqual(due, retries.NextDueUtc);
        clock.Advance(TimeSpan.FromSeconds(5));
        Assert.IsTrue(retries.TryGetDue(out var key)); Assert.AreEqual(Key, key);
    }

    [TestMethod]
    public void RepeatedFailureUsesBoundedBackoffWithoutSleepingOrHoldingAWorker()
    {
        var clock = new Clock(); var retries = new RecoveryRetrySchedule(clock, () => .5);
        retries.Failed(Key);
        foreach (var seconds in new[] { 15, 30, 60, 120, 240, 300, 300 })
        {
            Assert.AreEqual(clock.GetUtcNow().AddSeconds(seconds), retries.NextDueUtc);
            Assert.IsFalse(retries.TryGetDue(out _));
            clock.Advance(TimeSpan.FromSeconds(seconds));
            Assert.IsTrue(retries.TryGetDue(out var key));
            Assert.IsTrue(retries.Accepted(key!));
            Assert.IsFalse(retries.TryGetDue(out _), "Accepted attempts are not queued again while running.");
            retries.Failed(Key);
        }
    }

    [TestMethod]
    public void ReconnectDebouncesOnceAndDoesNotResetFailureHistory()
    {
        var clock = new Clock(); var retries = new RecoveryRetrySchedule(clock, () => .5);
        retries.Failed(Key);
        retries.Connected();
        Assert.AreEqual(clock.GetUtcNow().AddSeconds(2), retries.NextDueUtc);
        clock.Advance(TimeSpan.FromSeconds(1)); retries.Connected();
        Assert.AreEqual(clock.GetUtcNow().AddSeconds(1), retries.NextDueUtc);
        clock.Advance(TimeSpan.FromSeconds(1));
        Assert.IsTrue(retries.TryGetDue(out _));
        Assert.IsTrue(retries.Accepted(Key)); retries.Failed(Key);
        Assert.AreEqual(clock.GetUtcNow().AddSeconds(30), retries.NextDueUtc);
    }

    [TestMethod]
    public void ProviderDeadlineSurvivesReconnectAndRediscovery()
    {
        var clock = new Clock(); var retries = new RecoveryRetrySchedule(clock, () => .5);
        var deadline = clock.GetUtcNow().AddMinutes(10);
        retries.Synchronize([new(Key, deadline)]);
        retries.Connected();
        clock.Advance(TimeSpan.FromSeconds(30)); retries.Synchronize([new(Key)]);
        Assert.AreEqual(deadline, retries.NextDueUtc);
        Assert.IsFalse(retries.TryGetDue(out _));
        clock.Advance(TimeSpan.FromMinutes(10));
        Assert.IsTrue(retries.TryGetDue(out _));
    }

    [TestMethod]
    public void BindingChangesRemoveOldRetryInsteadOfReusingItsAuthorization()
    {
        var clock = new Clock(); var retries = new RecoveryRetrySchedule(clock, () => .5);
        retries.Synchronize([new(Key)]); clock.Advance(TimeSpan.FromMinutes(1));
        var relocated = Key with { Binding = "mapping-b" };
        retries.Synchronize([new(relocated)]);
        Assert.AreEqual(1, retries.Count); Assert.IsFalse(retries.TryGetDue(out _));
        clock.Advance(TimeSpan.FromSeconds(15));
        Assert.IsTrue(retries.TryGetDue(out var key)); Assert.AreEqual(relocated, key);
    }

    [TestMethod]
    public void WallClockChangesCannotSkipMonotonicBackoffAndStopClearsTheWakeup()
    {
        var clock = new Clock(); var retries = new RecoveryRetrySchedule(clock, () => .5);
        retries.Failed(Key); clock.Utc = clock.Utc.AddDays(1);
        Assert.IsFalse(retries.TryGetDue(out _));
        clock.Advance(TimeSpan.FromSeconds(15)); Assert.IsTrue(retries.TryGetDue(out _));
        retries.Clear(); Assert.IsFalse(retries.TryGetDue(out _)); Assert.IsNull(retries.NextDueUtc);
    }

    [TestMethod]
    public void AReportlessFailureReleasesOnlyAnExistingAcceptedAttempt()
    {
        var clock = new Clock(); var retries = new RecoveryRetrySchedule(clock, () => .5);
        Assert.IsFalse(retries.ReleaseFailedAttempt(Key)); Assert.AreEqual(0, retries.Count);
        retries.Synchronize([new(Key)]);
        var originalDue = retries.NextDueUtc;
        Assert.IsFalse(retries.ReleaseFailedAttempt(Key)); Assert.AreEqual(originalDue, retries.NextDueUtc);
        clock.Advance(TimeSpan.FromSeconds(15));
        Assert.IsTrue(retries.Accepted(Key));
        retries.Synchronize([new(Key)]);
        Assert.IsTrue(retries.ReleaseFailedAttempt(Key));
        Assert.AreEqual(clock.GetUtcNow().AddSeconds(30), retries.NextDueUtc);
        Assert.IsFalse(retries.ReleaseFailedAttempt(Key), "A completed failure is not counted twice.");
        clock.Advance(TimeSpan.FromSeconds(30)); Assert.IsTrue(retries.Accepted(Key));
        retries.Synchronize([]);
        Assert.IsFalse(retries.ReleaseFailedAttempt(Key), "A removed or stopped task cannot be reauthorized by its old attempt.");
        Assert.AreEqual(0, retries.Count);
    }

    private sealed class Clock : TimeProvider
    {
        internal DateTimeOffset Utc = DateTimeOffset.Parse("2026-11-20T00:00:00Z");
        private TimeSpan elapsed;
        public override DateTimeOffset GetUtcNow() => Utc;
        public override long TimestampFrequency => TimeSpan.TicksPerSecond;
        public override long GetTimestamp() => elapsed.Ticks;
        internal void Advance(TimeSpan duration) { elapsed += duration; Utc += duration; }
    }
}
