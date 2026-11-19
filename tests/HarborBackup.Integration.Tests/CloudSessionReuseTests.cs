using HarborBackup.Windows.Integration;
using static HarborBackup.Windows.Tests.CloudTransportTests;

namespace HarborBackup.Windows.Tests;

[TestClass]
[DoNotParallelize]
public sealed class CloudSessionReuseTests
{
    [TestMethod]
    public async Task ExistingRegistrationUsesTwoIndependentListingsAndOneDirectoryResolution()
    {
        using var fixture = new Fixture(); var device = fixture.Device("A");
        File.WriteAllText(Path.Combine(device.P.Source, "a.txt"), "data");
        await Apply(device.S);
        var repository = new CloudRepository(fixture.Drive, fixture.Account, new DataPaths(Path.Combine(fixture.Root, "probe")));
        var target = (await fixture.Drive.FindAsync("drive-root", "Documents", default))!;
        var registry = new CloudProjectRegistry(repository, fixture.Drive, fixture.Account.DriveId!);
        var listings = fixture.Drive.Enumerations;
        var finds = fixture.Drive.Finds;
        await registry.ValidateAsync(device.P, target, default);
        await registry.RegisterAsync(device.P, target, default);
        Assert.AreEqual(2, fixture.Drive.Enumerations - listings);
        Assert.AreEqual(4, fixture.Drive.Finds - finds);
        repository.ResetObservation();
        await registry.ValidateAsync(device.P, target, default);
        Assert.AreEqual(8, fixture.Drive.Finds - finds, "A new observation must resolve paths again.");
    }
}
