using HarborBackup.Core.Domain;
using HarborBackup.Desktop.Controllers;
using HarborBackup.Desktop.Localization;
using HarborBackup.Desktop.Presentation;
using HarborBackup.Desktop.Views.Dialogs;
using HarborBackup.Desktop.Views.Pages;
using HarborBackup.Windows.Integration;
using HarborBackup.Windows.Persistence;
using HarborBackup.Windows.Persistence.Configuration;

namespace HarborBackup.Windows.Tests;

[TestClass]
[DoNotParallelize]
public sealed class InspectionUiTests
{
    private sealed class Presenter(ConfigurationEditor editor) : IConfigurationPresenter
    {
        public readonly TaskCompletionSource Saved = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public Task<ConfigurationDraft> LoadAsync() => Task.FromResult(editor.Load());
        public Task<ConfigurationSnapshot> ValidateAsync(ConfigurationDraft draft) => Task.FromResult(editor.Validate(draft));
        public Task<bool> SaveAsync(ConfigurationDraft draft, ConfigurationPart part)
        { editor.Save(draft, part); Saved.TrySetResult(); return Task.FromResult(true); }
        public Task<CloudProjectOnboarding> InspectNewProjectAsync(ProjectDefinition project, CancellationToken token) => throw new AssertFailedException();
        public Task<CloudFolderListing> BrowseCloudAsync(string? folderId) => throw new AssertFailedException();
        public Task<PreviewReport> PreviewAsync(ConfigurationDraft draft, string? destination = null, CancellationToken cancellationToken = default,
            IProgress<BackupProgress>? progress = null) => throw new AssertFailedException();
    }
    private static async Task OnSta(Func<Form, Task> check)
    {
        var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            using var host = new Form { Size = new(1000, 760) };
            host.Shown += async (_, _) =>
            {
                try { await check(host); done.TrySetResult(); }
                catch (Exception error) { done.TrySetException(error); }
                finally { host.Close(); }
            };
            Application.Run(host);
        }) { IsBackground = true };
        thread.SetApartmentState(ApartmentState.STA); thread.Start();
        await done.Task.WaitAsync(TimeSpan.FromSeconds(30));
        Assert.IsTrue(thread.Join(TimeSpan.FromSeconds(5)));
    }
    [TestMethod]
    public async Task ConfigurationSaveWaitsForBackgroundRefreshInsteadOfReportingBusy() => await OnSta(async _ =>
    {
        using var ws = new TestWorkspace();
        var paths = new RuntimePaths(Path.Combine(ws.Config, "projects.json"), Path.Combine(ws.Config, "backup-settings.json"));
        var controller = new DesktopController(paths);
        var refresh = controller.RefreshAsync();
        var applied = await controller.ExclusiveAsync(() =>
        {
            var draft = controller.Editor.Load(); draft.System["schedule"]!["automaticMaxTransferMiB"] = 8;
            controller.Editor.Save(draft, ConfigurationPart.System); return true;
        });
        await refresh; Assert.IsTrue(applied);
        Assert.AreEqual(8 * 1048576L, BackupSettingsReader.Parse(File.ReadAllText(paths.SystemConfig)).AutomaticBudget.MaxBytes);
    });
    [TestMethod]
    [DataRow("zh-CN")]
    [DataRow("en-US")]
    public async Task AutomaticBudgetsSaveAndReloadWithoutOverwritingOtherSettings(string language) => await OnSta(async host =>
    {
        UiText.SetLanguage(language); using var ws = new TestWorkspace();
        var paths = new RuntimePaths(Path.Combine(ws.Config, "projects.json"), Path.Combine(ws.Config, "backup-settings.json"));
        var editor = new ConfigurationEditor(paths, ws.Device); var presenter = new Presenter(editor);
        using var page = new SettingsPage(new DesktopController(paths), presenter); host.Controls.Add(page);
        // In the shell these actions are hosted in the footer; keep them visible for PerformClick.
        host.Controls.Add(page.Actions); page.Actions.Dock = DockStyle.Bottom;
        await page.LoadDraftAsync();
        var mib = (NumericUpDown)page.Controls.Find("AutomaticMaxTransferMiB", true).Single();
        var minutes = (NumericUpDown)page.Controls.Find("AutomaticMaxDurationMinutes", true).Single();
        Assert.AreEqual(0m, mib.Value); Assert.AreEqual(0m, minutes.Value);
        Assert.IsFalse(string.IsNullOrWhiteSpace(mib.AccessibleName));
        var save = page.Actions.Controls.OfType<Button>().First();
        var state = new DesktopController(paths).State;
        page.UpdateState(state with { Busy = true, OperationBusy = false });
        Assert.IsTrue(save.Enabled, "Background reads do not block configuration edits.");
        page.UpdateState(state with { Busy = true, OperationBusy = true });
        Assert.IsFalse(save.Enabled, "A real operation still blocks the save button.");
        page.UpdateState(state);
        var before = File.ReadAllText(paths.ProjectConfig); mib.Value = 24; minutes.Value = 4;
        page.Actions.Controls.OfType<Button>().First().PerformClick();
        await presenter.Saved.Task.WaitAsync(TimeSpan.FromSeconds(5)); await page.LoadDraftAsync(force: true);
        Assert.AreEqual(24m, mib.Value); Assert.AreEqual(4m, minutes.Value);
        Assert.AreEqual(before, File.ReadAllText(paths.ProjectConfig));
        Assert.AreEqual(30m, ((NumericUpDown)page.Controls.Find("IntervalMinutes", true).Single()).Value);
        var settings = BackupSettingsReader.Parse(File.ReadAllText(paths.SystemConfig));
        Assert.AreEqual(new AutomaticBackupBudget(24 * 1048576, 4), settings.AutomaticBudget);
    });
    [TestMethod]
    [DataRow("zh-CN")]
    [DataRow("en-US")]
    public async Task PreviewShowsPlanVerificationAndConfigurationImpact(string language) => await OnSta(_ =>
    {
        UiText.SetLanguage(language);
        var file = new PreviewPlan("a.txt", new(FileAction.Copy, false, FileSide.Target, FileSide.Target, ProtectionKind.Archive, "OneSideChangedSinceBaseline"), true, false)
        { Verification = VerificationCategory.Different, TransferBytes = 1048576 };
        var report = new PreviewReport([new("Demo", [file], [], new())]) { Mode = "Verify",
            ConfigurationImpact = [new("id", "Demo", false, true, 2, 1)] };
        using var dialog = new PreviewDialog(report); dialog.Show();
        var grid = (DataGridView)dialog.Controls.Find("PullPreviewGrid", true).Single();
        var category = grid.Columns.Cast<DataGridViewColumn>().Single(c => c.DataPropertyName == "DisplayVerification");
        Assert.AreEqual(language == "zh-CN" ? "内容不同" : "Different content", grid.Rows[0].Cells[category.Index].Value);
        var summary = dialog.Controls.Find("RunPlanSummary", true).Single();
        Assert.Contains("1.00", summary.Text);
        Assert.Contains(language == "zh-CN" ? "云端正文未下载" : "not downloaded", summary.Text);
        Assert.IsTrue(dialog.Controls.Find("ShowConfigurationImpact", true).Single().Visible);
        Assert.IsGreaterThan(70, grid.Height);
        dialog.Close(); return Task.CompletedTask;
    });
}
