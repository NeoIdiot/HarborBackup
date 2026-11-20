using HarborBackup.Core.Domain;

namespace HarborBackup.Core.Tests;

[TestClass]
public sealed class AutomaticBackupDecisionTests
{
    private static FileObservation File(char hash, int hours = 0) => FileObservation.Present(new(new string(hash, 64), 1,
        DateTimeOffset.Parse("2026-10-01T00:00:00Z").AddHours(hours), DateTimeOffset.Parse("2026-10-01T00:00:00Z")), true);
    private static FileBaseline Common(char hash) => new(File(hash).Version, File(hash).Version, new string(hash, 64));

    [TestMethod]
    public void OneSidedChangesChooseTheChangedSideRegardlessOfClockOrder()
    {
        foreach (var offset in new[] { -3, 0, 3 })
        foreach (var changedSide in new[] { SyncDirection.Backup, SyncDirection.Pull })
        {
            var source = changedSide == SyncDirection.Backup ? File('b', offset) : File('a');
            var target = changedSide == SyncDirection.Pull ? File('b', offset) : File('a');
            var backup = ConflictDecision.Resolve(SyncDirection.Backup, source, target, Common('a'));
            var direction = AutomaticBackupDecision.DirectionFor(backup, source, target);
            Assert.AreEqual(changedSide, direction);
            var copy = ConflictDecision.Resolve(direction, source, target, Common('a'));
            Assert.AreEqual(FileAction.Copy, copy.Action);
            Assert.AreEqual(ProtectionKind.Archive, copy.Protection);
        }
    }

    [TestMethod]
    public void MissingFilesAndDirectoriesAreFilledFromThePresentSide()
    {
        foreach (var present in new[] { File('a'), FileObservation.Directory })
        foreach (var direction in new[] { SyncDirection.Backup, SyncDirection.Pull })
        {
            var source = direction == SyncDirection.Backup ? present : FileObservation.Missing;
            var target = direction == SyncDirection.Pull ? present : FileObservation.Missing;
            var backup = ConflictDecision.Resolve(SyncDirection.Backup, source, target);
            Assert.AreEqual(direction, AutomaticBackupDecision.DirectionFor(backup, source, target));
            var result = ConflictDecision.Resolve(direction, source, target);
            Assert.AreEqual(present.Kind == ObservationKind.File ? FileAction.Copy : FileAction.CreateDirectory, result.Action);
        }
    }

    [TestMethod]
    public void ConflictsAndUnavailableEvidenceRetainTheirValidatedDecision()
    {
        var observations = new[]
        {
            (Source: File('b'), Target: File('c'), Baseline: Common('a')),
            (Source: File('b'), Target: File('c'), Baseline: (FileBaseline?)null),
            (Source: FileObservation.Directory, Target: File('a'), Baseline: (FileBaseline?)null),
            (Source: FileObservation.Problem(ObservationKind.Unavailable, "locked"), Target: File('a'), Baseline: Common('a')),
            (Source: File('a'), Target: File('b'), Baseline: Common('a') with { LastCommonHash = new string('c', 64) })
        };
        foreach (var (source, target, baseline) in observations)
        {
            var backup = ConflictDecision.Resolve(SyncDirection.Backup, source, target, baseline);
            Assert.AreEqual(SyncDirection.Backup, AutomaticBackupDecision.DirectionFor(backup, source, target));
            Assert.AreEqual(FileSide.None, backup.WriteSide);
        }
        var untrusted = new FileDecision(FileAction.Deferred, false, FileSide.None, FileSide.None, ProtectionKind.None, "CloudMetadataRequired");
        Assert.AreEqual(SyncDirection.Backup, AutomaticBackupDecision.DirectionFor(untrusted, FileObservation.Missing, File('b')));
        Assert.AreEqual(SyncDirection.Backup, AutomaticBackupDecision.DirectionFor(untrusted, FileObservation.Missing, FileObservation.Directory));
    }

    [TestMethod]
    public void EqualAndAbsentFilesDoNotAcquireAWriteDirection()
    {
        foreach (var observation in new[] { File('a'), FileObservation.Directory, FileObservation.Missing })
        {
            var decision = ConflictDecision.Resolve(SyncDirection.Backup, observation, observation);
            Assert.AreEqual(SyncDirection.Backup, AutomaticBackupDecision.DirectionFor(decision, observation, observation));
            Assert.AreEqual(FileSide.None, decision.WriteSide);
            Assert.AreEqual(ProtectionKind.None, decision.Protection);
        }
    }
}
