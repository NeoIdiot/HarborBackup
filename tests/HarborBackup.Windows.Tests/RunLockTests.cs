using HarborBackup.Windows.Integration;

namespace HarborBackup.Windows.Tests;

[TestClass]
public sealed class RunLockTests
{
    [TestMethod]
    [DoNotParallelize]
    public void AnotherThreadCannotEnterAndLockIsReleased()
    {
        using (var held = new RunLock())
        {
            Exception? error = null;
            var worker = new Thread(() =>
            {
                try { using var other = new RunLock(); }
                catch (Exception ex) { error = ex; }
            });
            worker.Start();
            Assert.IsTrue(worker.Join(TimeSpan.FromSeconds(5)));
            Assert.IsInstanceOfType<InvalidOperationException>(error);
        }
        using var reacquired = new RunLock();
    }
}
