using System.Text;
using System.Text.Json;
using HarborBackup.Core.Domain;
using HarborBackup.Windows.Integration;
using HarborBackup.Windows.Persistence;
using static HarborBackup.Windows.Tests.CloudTransportTests;

namespace HarborBackup.Windows.Tests;

[TestClass]
[DoNotParallelize]
public sealed class AllProjectsBackupTests
{
    [TestMethod]
    [DataRow(true)]
    [DataRow(false)]
    public async Task AllProjectsBackupReachesChangedDocumentAfterThreeUnchangedProjects(bool continueOnError)
    {
        using var f = new Fixture(); var a = f.Device("A");
        var projects = Enumerable.Range(1, 4).Select(i => a.P with {
            Source = Path.Combine(f.Root, "source-" + i), Destination = "Projects/Project" + i,
            TaskId = DeviceIdentity.Digest("Project" + i)
        }).ToArray();
        const string document = "附件“AI+X”申报.doc";
        for (var i = 0; i < projects.Length; i++)
        {
            Directory.CreateDirectory(projects[i].Source);
            var name = i == 3 ? document : "readme.txt";
            File.WriteAllText(Path.Combine(projects[i].Source, name), "initial");
            File.SetLastWriteTimeUtc(Path.Combine(projects[i].Source, name), DateTime.UtcNow.AddDays(-1));
        }
        var c = a.C with { Projects = projects, Preview = a.C.Preview with { ContinueOnError = continueOnError } };
        var runtime = new CloudBackupRuntime(c, () => f.Drive);
        Assert.AreEqual(0, (await runtime.ExecuteAsync(SyncDirection.Backup, null, false, false, null, default)).ExitCode);
        f.Drive.UploadIntents.Clear();
        File.WriteAllText(Path.Combine(projects[3].Source, document), "changed");
        File.SetLastWriteTimeUtc(Path.Combine(projects[3].Source, document), DateTime.UtcNow.AddSeconds(-1));
        var result = await runtime.ExecuteAsync(SyncDirection.Backup, null, false, false, null, default);
        Assert.AreEqual(0, result.ExitCode, JsonSerializer.Serialize(result));
        Assert.HasCount(4, result.Projects);
        Assert.IsTrue(result.Projects.Take(3).All(p => p.Files.All(file => !file.Changed)));
        Assert.IsTrue(result.Projects[3].Files.Single().Changed);
        Assert.AreEqual(document, result.Projects[3].Files.Single().Path);
        Assert.HasCount(1, f.Drive.UploadIntents.Where(p => p.Name == document));
        var parent = await f.Drive.FindAsync("drive-root", "Projects", default);
        var lastProject = await f.Drive.FindAsync(parent!.Id, "Project4", default);
        var cloud = await f.Drive.FindAsync(lastProject!.Id, document, default);
        Assert.AreEqual("changed", Encoding.UTF8.GetString(f.Drive.Items[cloud!.Id].Bytes));
        var record = RunHistory.Read(c.HistoryDirectory).First();
        CollectionAssert.AreEqual(projects.Select(p => p.Destination).ToArray(), record.Projects.ToArray());
    }
}
