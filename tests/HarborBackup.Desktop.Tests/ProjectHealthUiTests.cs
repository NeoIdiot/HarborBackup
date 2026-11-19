using HarborBackup.Core.Domain;
using HarborBackup.Desktop;
using HarborBackup.Desktop.Localization;
using HarborBackup.Desktop.Presentation;
using HarborBackup.Desktop.Views.Dialogs;
using HarborBackup.Windows.Integration;
using HarborBackup.Windows.Persistence;

namespace HarborBackup.Windows.Tests;

[TestClass]
[DoNotParallelize]
public sealed class ProjectHealthUiTests
{
    [TestMethod]
    public void OverdueHealthNotifiesOnceAndSelectedRestoreScopeIsExplicit()
    {
        UiText.SetLanguage("en-US"); var now = DateTimeOffset.UtcNow;
        var health = new ProjectHealthSnapshot("binding", now.AddHours(-30), now.AddHours(-1), 2, now.AddHours(-25), true, "Archive", 3);
        var row = new ProjectRow("Demo", "", "", "") { Health = health };
        StringAssert.Contains(row.DisplayProtection, "3 files (selected scope)");
        StringAssert.Contains(row.DisplayBacklog, "count may be incomplete");
        var details = ProjectHealthPresentation.Attention([row], now);
        Assert.HasCount(1, details);
        Assert.IsTrue(ProjectHealthPresentation.SameDetails(details, ProjectHealthPresentation.Attention([row], now.AddMinutes(2))));
        var policy = new ProblemNotificationPolicy();
        var state = new DesktopState("空闲", false, false, false, null, null, null, null, [row], details, [], null);
        Assert.IsTrue(policy.ShouldNotify(state)); Assert.IsFalse(policy.ShouldNotify(state));
        var clear = row with { Health = health with { LastFullSuccessUtc = now, OldestPendingUtc = null, KnownBacklogCount = 0 } };
        Assert.IsEmpty(ProjectHealthPresentation.Attention([clear], now));
        Assert.IsFalse(policy.ShouldNotify(state with { Details = [] }));
        Assert.IsTrue(policy.ShouldNotify(state));
    }

    [TestMethod]
    [DataRow("zh-CN")]
    [DataRow("en-US")]
    public async Task ConflictChoicesShowFullVersionMetadataWithoutDownloading(string language)
    {
        var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            using var host = new Form();
            host.Shown += (_, _) =>
            {
                try
                {
                    UiText.SetLanguage(language);
                    var stamp = new DateTimeOffset(2026, 1, 2, 3, 4, 5, TimeSpan.Zero);
                    var version = new FileVersion(new string('a', 64), 12345, stamp, stamp);
                    var device = new string('d', 64);
                    using var dialog = new CloudConflictDialog(new("Demo", "a.txt", version,
                        [new(1, "revision", "Demo", "a.txt", device, [], "item", "etag", version, stamp)], "confirmation"));
                    dialog.Show(host);
                    var list = (ListBox)dialog.Controls.Find("ConflictVersions", true).Single();
                    Assert.AreEqual(2, list.Items.Count); list.SelectedIndex = 1;
                    var details = (TextBox)dialog.Controls.Find("ConflictVersionDetails", true).Single();
                    StringAssert.Contains(details.Text, "12,345"); StringAssert.Contains(details.Text, device);
                    StringAssert.Contains(details.Text, version.Sha256); StringAssert.Contains(details.Text, "2026-01-02");
                    Assert.IsTrue(details.ReadOnly);
                    Assert.IsFalse(dialog.Controls.Find("ExportConflictVersion", true).Single().Enabled);
                    Assert.AreEqual("revision", dialog.Selected);
                    dialog.Close(); done.TrySetResult();
                }
                catch (Exception ex) { done.TrySetException(ex); }
                finally { host.Close(); }
            };
            Application.Run(host);
        }) { IsBackground = true };
        thread.SetApartmentState(ApartmentState.STA); thread.Start();
        await done.Task.WaitAsync(TimeSpan.FromSeconds(15)); Assert.IsTrue(thread.Join(TimeSpan.FromSeconds(3)));
    }
}
