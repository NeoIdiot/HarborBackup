using HarborBackup.Core.Domain;
using HarborBackup.Desktop.Controllers;
using HarborBackup.Desktop.Presentation;
using HarborBackup.Desktop.Views.Pages;
using HarborBackup.Windows.Integration;
using HarborBackup.Windows.Persistence;
using HarborBackup.Windows.Persistence.Configuration;

namespace HarborBackup.Windows.Tests;

[TestClass]
[DoNotParallelize]
public sealed class SettingsLoadingTests
{
    private sealed class DelayedPresenter : IConfigurationPresenter
    {
        internal readonly TaskCompletionSource<ConfigurationDraft> Pending = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal int Reads;
        public Task<ConfigurationDraft> LoadAsync() { Reads++; return Pending.Task; }
        public Task<CloudProjectOnboarding> InspectNewProjectAsync(ProjectDefinition project, CancellationToken token) => throw new AssertFailedException();
        public Task<CloudFolderListing> BrowseCloudAsync(string? folderId) => throw new AssertFailedException();
        public Task<ConfigurationSnapshot> ValidateAsync(ConfigurationDraft draft) => throw new AssertFailedException();
        public Task<PreviewReport> PreviewAsync(ConfigurationDraft draft, string? destination = null,
            CancellationToken cancellationToken = default, IProgress<BackupProgress>? progress = null) => throw new AssertFailedException();
        public Task<bool> SaveAsync(ConfigurationDraft draft, ConfigurationPart part) => throw new AssertFailedException();
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task PreloadIsSharedAndPreservesDraftOrHandlesDisposal(bool disposeEarly)
    {
        using var ws = new TestWorkspace();
        var completed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            using var host = new Form();
            var paths = new RuntimePaths(Path.Combine(ws.Config, "projects.json"), Path.Combine(ws.Config, "backup-settings.json"));
            var controller = new DesktopController(paths); var presenter = new DelayedPresenter();
            using var page = new SettingsPage(controller, presenter); host.Controls.Add(page);
            async Task CheckAsync()
            {
                try
                {
                    var preload = page.LoadDraftAsync();
                    Assert.AreSame(preload, page.LoadDraftAsync(), "Navigation must await the pending preload rather than start a second read.");
                    var input = (NumericUpDown)page.Controls.Find("IntervalMinutes", true).Single();
                    Assert.IsFalse(input.Enabled, "Default values must not be editable while the real configuration is pending.");
                    if (disposeEarly) page.Dispose();
                    presenter.Pending.SetResult(controller.Editor.Load());
                    await preload;
                    Assert.AreEqual(1, presenter.Reads);
                    if (!disposeEarly)
                    {
                        Assert.IsTrue(input.Enabled); Assert.AreEqual(30m, input.Value);
                        input.Value = 47;
                        await page.LoadDraftAsync();
                        Assert.AreEqual(47m, input.Value); Assert.IsTrue(page.Dirty);
                        Assert.AreEqual(1, presenter.Reads);
                    }
                    completed.TrySetResult();
                }
                catch (Exception ex) { completed.TrySetException(ex); }
                finally { host.Close(); }
            }
            host.Shown += async (_, _) => await CheckAsync();
            Application.Run(host);
        }) { IsBackground = true };
        thread.SetApartmentState(ApartmentState.STA); thread.Start();
        await completed.Task.WaitAsync(TimeSpan.FromSeconds(20));
        Assert.IsTrue(thread.Join(TimeSpan.FromSeconds(3)));
    }
}
