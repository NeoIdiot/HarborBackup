using HarborBackup.Core.Domain;

namespace HarborBackup.Core.Tests;

[TestClass]
public sealed class ConflictDecisionTests
{
    private static FileObservation File(char hash, int hours = 0) => FileObservation.Present(new(new string(hash, 64), 1,
        DateTimeOffset.Parse("2026-10-01T00:00:00Z").AddHours(hours), DateTimeOffset.Parse("2026-10-01T00:00:00Z")), true);
    private static FileBaseline Common(char hash) => new(File(hash).Version, File(hash).Version, new string(hash, 64));

    [TestMethod]
    [DataRow(SyncDirection.Backup)]
    [DataRow(SyncDirection.Pull)]
    public void ThreeWayDecisionsIgnoreAllClockOrders(SyncDirection direction)
    {
        foreach (var offset in new[] { -3, 0, 3 })
        {
            var original = File('a'); var changed = File('b', offset);
            var source = direction == SyncDirection.Backup ? changed : original;
            var target = direction == SyncDirection.Backup ? original : changed;
            var decision = ConflictDecision.Resolve(direction, source, target, Common('a'));
            Assert.AreEqual(FileAction.Copy, decision.Action);
            Assert.AreEqual(ProtectionKind.Archive, decision.Protection);
            Assert.AreEqual(direction == SyncDirection.Backup ? FileSide.Target : FileSide.Source, decision.WriteSide);
            Assert.AreEqual(decision.WriteSide, decision.ProtectSide);
            var opposite = ConflictDecision.Resolve(direction, target, source, Common('a'));
            Assert.AreEqual(direction == SyncDirection.Backup ? FileAction.NeedsPull : FileAction.NeedsPush, opposite.Action);
            Assert.AreEqual(FileSide.None, opposite.WriteSide);
            foreach (var baseline in new FileBaseline?[] { null, Common('c') })
            {
                var conflict = ConflictDecision.Resolve(direction, source, target, baseline);
                Assert.AreEqual(FileAction.ConflictUnresolved, conflict.Action);
                Assert.AreEqual(FileSide.None, conflict.WriteSide);
                Assert.AreEqual(FileSide.Both, conflict.ProtectSide);
            }
        }
    }

    [TestMethod]
    [DataRow(ObservationKind.Unavailable, FileAction.Deferred)]
    [DataRow(ObservationKind.Unstable, FileAction.Deferred)]
    [DataRow(ObservationKind.Unsafe, FileAction.TypeConflict)]
    public void FailedObservationNeverBecomesMissing(ObservationKind kind, FileAction action)
    {
        foreach (var direction in Enum.GetValues<SyncDirection>())
        foreach (var other in new[] { FileObservation.Missing, FileObservation.Directory, File('a') })
        {
            var problem = FileObservation.Problem(kind, "unreadable");
            foreach (var result in new[] { ConflictDecision.Resolve(direction, problem, other), ConflictDecision.Resolve(direction, other, problem) })
            {
                Assert.AreEqual(action, result.Action);
                Assert.AreEqual(FileSide.None, result.WriteSide);
            }
        }
    }

    [TestMethod]
    [DataRow(SyncDirection.Backup)]
    [DataRow(SyncDirection.Pull)]
    public void MissingFilesAndDirectoriesRespectDirectionAndNeverDelete(SyncDirection direction)
    {
        var missing = FileObservation.Missing;
        var directory = FileObservation.Directory;
        var receivingSide = direction == SyncDirection.Backup ? FileSide.Target : FileSide.Source;
        var sending = direction == SyncDirection.Backup ? File('a') : missing;
        var receiving = direction == SyncDirection.Backup ? missing : File('a');
        var copy = ConflictDecision.Resolve(direction, sending, receiving);
        Assert.AreEqual(FileAction.Copy, copy.Action);
        Assert.AreEqual(receivingSide, copy.WriteSide);
        Assert.AreEqual(ProtectionKind.None, copy.Protection);
        var preserved = ConflictDecision.Resolve(direction, receiving, sending, Common('a'));
        Assert.AreEqual(direction == SyncDirection.Backup ? FileAction.TargetOnly : FileAction.SourceOnly, preserved.Action);
        Assert.AreEqual(FileSide.None, preserved.WriteSide);
        Assert.AreEqual(FileAction.Absent, ConflictDecision.Resolve(direction, missing, missing).Action);
        Assert.AreEqual(FileAction.Directory, ConflictDecision.Resolve(direction, directory, directory).Action);
        Assert.AreEqual(FileAction.TypeConflict, ConflictDecision.Resolve(direction, directory, File('a')).Action);
        Assert.AreEqual(FileAction.TypeConflict, ConflictDecision.Resolve(direction, File('a'), directory).Action);
        var create = ConflictDecision.Resolve(direction,
            direction == SyncDirection.Backup ? directory : missing,
            direction == SyncDirection.Backup ? missing : directory);
        Assert.AreEqual(FileAction.CreateDirectory, create.Action);
        Assert.AreEqual(receivingSide, create.WriteSide);
        Assert.AreEqual(FileAction.Directory, ConflictDecision.Resolve(direction,
            direction == SyncDirection.Backup ? missing : directory,
            direction == SyncDirection.Backup ? directory : missing).Action);
    }

    [TestMethod]
    public void EqualContentNeedsNoWinnerAndInvalidBaselineBlocksWrites()
    {
        foreach (var direction in Enum.GetValues<SyncDirection>())
        {
            Assert.AreEqual(FileAction.Equal, ConflictDecision.Resolve(direction, File('a'), File('a', 3)).Action);
            var invalid = Common('a') with { LastCommonHash = new string('b', 64) };
            Assert.AreEqual(FileAction.Deferred, ConflictDecision.Resolve(direction, File('a'), File('b'), invalid).Action);
        }
    }
}
