using System.Text.Json;
using HarborBackup.Core.Domain;

namespace HarborBackup.Core.Tests;

[TestClass]
public sealed class FileResultTests
{
    [TestMethod]
    public void LegacyResultsOmitDirectionAndRemainReadable()
    {
        const string legacy = "{\"Path\":\"a.txt\",\"Action\":2,\"Changed\":true,\"Error\":null,\"FailureKind\":0}";
        var result = JsonSerializer.Deserialize<FileResult>(legacy)!;
        Assert.IsNull(result.Direction);
        Assert.AreEqual(legacy, JsonSerializer.Serialize(result));
    }

    [TestMethod]
    [DataRow(SyncDirection.Backup)]
    [DataRow(SyncDirection.Pull)]
    public void BidirectionalResultsRetainPerFileDirection(SyncDirection direction)
    {
        var result = new FileResult("a.txt", FileAction.Copy, true) { Direction = direction };
        Assert.AreEqual(result, JsonSerializer.Deserialize<FileResult>(JsonSerializer.Serialize(result)));
    }
}
