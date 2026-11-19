using HarborBackup.Core.Domain;
namespace HarborBackup.Core.Tests;

[TestClass]
public sealed class CloudRevisionTests
{
    private static CloudRevision R(string hash, params string[] parents) => new(1, Guid.NewGuid().ToString("N"), new('a',64),
        "document.txt", new('b',64), parents, Guid.NewGuid().ToString(), "etag",
        new(hash,1,DateTimeOffset.UtcNow,DateTimeOffset.UtcNow),DateTimeOffset.UtcNow);
    [TestMethod]
    public void DifferentCloudVersionsRequireExplicitChoice()
    {
        var a=R(new('a',64));var b=R(new('b',64),a.Id);var c=R(new('c',64),a.Id);
        Assert.AreEqual(FileAction.ConflictUnresolved,CloudRevisions.Decide(SyncDirection.Backup,
            FileObservation.Present(b.Version,true),[b,c],a.Version.Sha256).Action);
    }
    [TestMethod]
    public void UnknownContentNeverAuthorizesReplacement()
    {
        var remote = R(new('a',64)) with { ContentHashKnown = false };
        var decision = CloudRevisions.Decide(SyncDirection.Backup, FileObservation.Present(remote.Version, true), [remote], null);
        Assert.AreEqual(FileAction.Deferred, decision.Action);
        Assert.AreEqual(FileSide.None, decision.WriteSide);
    }
    [TestMethod]
    public void FirstContactDoesNotChooseByClock()
    {
        var remote=R(new('a',64));var local=R(new('b',64));
        Assert.AreEqual(FileAction.ConflictUnresolved,CloudRevisions.Decide(SyncDirection.Backup,
            FileObservation.Present(local.Version,true),[remote],null).Action);
    }
}
