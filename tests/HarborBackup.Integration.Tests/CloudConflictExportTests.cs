using System.Text;
using HarborBackup.Windows.Integration;
using static HarborBackup.Windows.Tests.CloudTransportTests;

namespace HarborBackup.Windows.Tests;

[TestClass]
[DoNotParallelize]
public sealed class CloudConflictExportTests
{
    [TestMethod]
    public async Task LocalAndCloudCopiesAreIndependentVerifiedAndLeaveBusinessFilesUnchanged()
    {
        using var f = new Fixture(); var a = f.Device("A");
        var source = Path.Combine(a.P.Source, "a.txt"); File.WriteAllText(source, "cloud-original"); await Apply(a.S);
        File.WriteAllText(source, "local-edited");
        var expected = await CloudConflictResolution.InspectAsync(a.C, a.P.ProjectId, "a.txt", f.Drive, default);
        var output = Path.Combine(f.Root, "inspect");
        var local = await CloudConflictExport.ExportAsync(a.C, expected, "local", f.Drive, output, default);
        var cloud = await CloudConflictExport.ExportAsync(a.C, expected, expected.Versions.Single().Id, f.Drive, output, default);
        Assert.AreEqual("local-edited", File.ReadAllText(local)); Assert.AreEqual("cloud-original", File.ReadAllText(cloud));
        Assert.AreEqual("local-edited", File.ReadAllText(source)); Assert.AreNotEqual(local, cloud);
        var again = await CloudConflictExport.ExportAsync(a.C, expected, "local", f.Drive, output, default);
        Assert.AreNotEqual(local, again); Assert.AreEqual("local-edited", File.ReadAllText(local));
        var live = f.Drive.Items[expected.Versions.Single().ItemId];
        Assert.AreEqual("cloud-original", Encoding.UTF8.GetString(live.Bytes));
    }

    [TestMethod]
    public async Task ChangedConsentCorruptContentAndOverlappingOutputDoNotPublishCopies()
    {
        using var f = new Fixture(); var a = f.Device("A");
        var source = Path.Combine(a.P.Source, "a.txt"); File.WriteAllText(source, "original"); await Apply(a.S);
        var expected = await CloudConflictResolution.InspectAsync(a.C, a.P.ProjectId, "a.txt", f.Drive, default);
        File.WriteAllText(source, "changed");
        var output = Path.Combine(f.Root, "stale");
        await Assert.ThrowsExactlyAsync<IOException>(() => CloudConflictExport.ExportAsync(a.C, expected, "local", f.Drive, output, default));
        Assert.IsFalse(Directory.Exists(output));
        expected = await CloudConflictResolution.InspectAsync(a.C, a.P.ProjectId, "a.txt", f.Drive, default);
        await Assert.ThrowsExactlyAsync<HarborBackup.Windows.FileSystem.UnsafePathException>(() =>
            CloudConflictExport.ExportAsync(a.C, expected, "local", f.Drive, a.P.Source, default));
        var remote = expected.Versions.Single(); var saved = f.Drive.Items[remote.ItemId];
        f.Drive.Items[remote.ItemId] = (saved.Item, saved.Parent, Encoding.UTF8.GetBytes("corrupt!"));
        output = Path.Combine(f.Root, "corrupt");
        await Assert.ThrowsExactlyAsync<IOException>(() => CloudConflictExport.ExportAsync(a.C, expected, remote.Id, f.Drive, output, default));
        Assert.IsFalse(Directory.EnumerateFiles(output, "*", SearchOption.AllDirectories).Any());
    }
}
