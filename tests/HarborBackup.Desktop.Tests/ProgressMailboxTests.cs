using HarborBackup.Core.Domain;
using HarborBackup.Desktop.Presentation;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace HarborBackup.Desktop.Tests;

[TestClass]
public sealed class ProgressMailboxTests
{
    private sealed class Context : SynchronizationContext
    {
        public int Posts;
        public override void Post(SendOrPostCallback d, object? state) => Posts++;
    }

    [TestMethod]
    public void LargeScanKeepsLatestProgressWithoutPostingThousandsOfUiMessages()
    {
        var saved = SynchronizationContext.Current; var context = new Context();
        SynchronizationContext.SetSynchronizationContext(context);
        try
        {
            var progress = new ProgressMailbox();
            for (var i = 0; i < 100000; i++) progress.Report(new(BackupPhase.Scanning, "project", Processed: i));
            Assert.AreEqual(0, context.Posts);
            Assert.AreEqual(99999, progress.Take()!.Processed);
            Assert.IsNull(progress.Take());
            progress.Report(new(BackupPhase.Publishing, "project")); progress.Reset();
            Assert.IsNull(progress.Latest);
        }
        finally { SynchronizationContext.SetSynchronizationContext(saved); }
    }

    [TestMethod]
    public void ConcurrentFileProducersCanShareOneMailbox()
    {
        var progress = new ProgressMailbox();
        Parallel.For(0, 10000, i => progress.Report(new(BackupPhase.Uploading, "project", i.ToString())));
        Assert.IsNotNull(progress.Take()); Assert.IsNull(progress.Take());
        progress.Report(new(BackupPhase.Publishing, "project", "last"));
        Assert.AreEqual("last", progress.Latest!.Path);
    }
}
