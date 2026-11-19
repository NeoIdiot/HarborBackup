using System.Text;
using System.Text.Json;
using HarborBackup.OneDrive.Transport;
using HarborBackup.Windows.Integration;
using static HarborBackup.Windows.Tests.CloudTransportTests;

namespace HarborBackup.Windows.Tests;

[TestClass]
[DoNotParallelize]
public sealed class CloudMetadataWarmTests
{
    private sealed class BatchDrive : FakeDrive, ICloudMetadataReads
    {
        public bool Invalid;
        public int Calls;
        public Task<IReadOnlyDictionary<string, byte[]>> ReadMetadataAsync(IReadOnlyList<RemoteItem> items, CancellationToken token)
        {
            Calls++;
            return Task.FromResult<IReadOnlyDictionary<string, byte[]>>(items.ToDictionary(i => i.Id,
                i => Invalid && i.Name == "b.json" ? Encoding.UTF8.GetBytes("{") : Items[i.Id].Bytes));
        }
    }
    [TestMethod]
    public async Task WarmedMetadataIsReusableButMalformedBatchPublishesNoPartialCache()
    {
        using var fixture = new Fixture(); var drive = new BatchDrive();
        var data = new DataPaths(Path.Combine(fixture.Root, "warm"));
        var repository = new CloudRepository(drive, fixture.Account, data);
        var a = drive.Add("root", "a.json", Encoding.UTF8.GetBytes("{\"value\":1}"));
        var b = drive.Add("root", "b.json", Encoding.UTF8.GetBytes("{\"value\":2}"));
        drive.Invalid = true;
        await Assert.ThrowsAsync<JsonException>(() => repository.WarmMetadataAsync<Dictionary<string, int>>([a, b], default));
        Assert.IsFalse(Directory.Exists(data.Cache), "Invalid JSON must not publish part of the batch.");
        drive.Invalid = false;
        Assert.HasCount(2, await repository.WarmMetadataAsync<Dictionary<string, int>>([a, b], default));
        Assert.AreEqual(1, (await repository.ReadJsonAsync<Dictionary<string, int>>(a, default))["value"]);
        Assert.AreEqual(0, drive.Downloads);
        Assert.HasCount(0, await repository.WarmMetadataAsync<Dictionary<string, int>>([a, b], default));
        Assert.AreEqual(2, drive.Calls);
    }
}
