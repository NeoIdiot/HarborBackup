using HarborBackup.Core.Domain;
using HarborBackup.Desktop;
using HarborBackup.Desktop.Presentation;
using HarborBackup.Desktop.Views.Dialogs;
using HarborBackup.Windows.Integration;
using HarborBackup.Windows.Persistence;
using HarborBackup.Windows.Persistence.Configuration;

namespace HarborBackup.Windows.Tests;

[TestClass]
[DoNotParallelize]
public sealed class ProjectIdentityUiTests
{
    private static readonly string First = new('a', 64);
    private static readonly string Second = new('b', 64);

    [TestMethod]
    public void ReportDetailsKeepProjectIdsAndUnidentifiedHistoryCannotAuthorizeActions()
    {
        var now = DateTimeOffset.UtcNow;
        var report = new BackupReport("Backup", now, now,
            [new("Shared", [new("a.txt", FileAction.ConflictUnresolved, false)], [new("pending", "review", false)], new()) { ProjectId = First },
             new("Shared", [new("a.txt", FileAction.Deferred, false)], [], new()) { ProjectId = Second }]);
        var rows = ReportPresentation.Details(report);
        CollectionAssert.AreEqual(new[] { First, First, Second }, rows.Select(r => r.ProjectId).ToArray());
        Assert.AreEqual(First, rows[0].ActionProjectId);
        Assert.AreEqual(Second, rows[2].ActionProjectId);
        Assert.IsNull((rows[0] with { ProjectId = "" }).ActionProjectId);
        Assert.IsNull((rows[0] with { ProjectId = "Shared" }).ActionProjectId);
        Assert.IsNull((rows[0] with { SystemRow = true }).ActionProjectId);
        var plan = new PreviewPlan("a.txt", new(FileAction.Deferred, true, FileSide.None, FileSide.None,
            ProtectionKind.None, "CloudMetadataRequired"), true, false);
        var preview = new PreviewReport([new("Renamed", [plan], [new("pending", "review", false)], new()) { ProjectId = First }]);
        Assert.IsTrue(ReportPresentation.Details(preview).All(r => r.ProjectId == First));
    }

    [TestMethod]
    public void NotificationAndTrayCountsFollowIdentityAcrossRenameAndSameNamedProjects()
    {
        var file = new DetailRow("PersonalProjects", "same.txt", "内容冲突", "BothChanged", true) { ProjectId = First };
        var basis = new DesktopState("空闲", false, false, false, null, null, null, null, [], [file], [], null);
        var policy = new ProblemNotificationPolicy();
        Assert.IsTrue(policy.ShouldNotify(basis));
        Assert.IsFalse(policy.ShouldNotify(basis with { Details = [file with { Project = "MyProjects" }] }));
        Assert.IsTrue(policy.ShouldNotify(basis with { Details = [file with { ProjectId = Second }] }));
        var tray = TrayPresentation.Create(basis with { Details = [file, file with { ProjectId = Second }] }, new(Enabled: true));
        Assert.AreEqual("HarborBackup · " + HarborBackup.Desktop.Localization.UiText.Text("托盘待处理文件：{0}", 2), tray.ToolTip);
    }

    [TestMethod]
    public Task PreviewCannotSelectAnotherProjectsFileWithTheSameRelativePath() => OnUiAsync(host =>
    {
        var copy = new FileDecision(FileAction.Copy, false, FileSide.Source, FileSide.None, ProtectionKind.None, "TargetOnly");
        var report = new PreviewReport([new("Shared", [new("same.txt", copy, true, true)], [], new()) { ProjectId = First },
            new("Shared", [new("same.txt", copy, true, true)], [], new()) { ProjectId = Second }])
        { PullSelection = new("Shared", false, [new("same.txt", "token", true, 12)]) { ProjectId = First } };
        using var dialog = new PreviewDialog(report, true);
        dialog.Show(host);
        var grid = (DataGridView)dialog.Controls.Find("PullPreviewGrid", true).Single();
        Assert.IsTrue((bool)grid.Rows[0].Cells["Selected"].Value!);
        Assert.IsFalse((bool)grid.Rows[1].Cells["Selected"].Value!);
        Assert.IsTrue(grid.Rows[1].Cells["Selected"].ReadOnly);
        // A programmatic check cannot smuggle an unrelated row into a confirmation either.
        grid.Rows[1].Cells["Selected"].Value = true;
        ((Button)dialog.Controls.Find("ExecuteSelectedPull", true).Single()).PerformClick();
        Assert.AreEqual(First, dialog.SelectedPull!.ProjectId);
        Assert.HasCount(1, dialog.SelectedPull.Files);
        return Task.CompletedTask;
    });

    [TestMethod]
    public Task UnidentifiedPreviewRemainsReadOnly() => OnUiAsync(host =>
    {
        var copy = new FileDecision(FileAction.Copy, false, FileSide.Source, FileSide.None, ProtectionKind.None, "TargetOnly");
        var report = new PreviewReport([new("Shared", [new("same.txt", copy, true, true)], [], new())])
        { PullSelection = new("Shared", false, [new("same.txt", "token", true, 12)]) };
        using var dialog = new PreviewDialog(report, true);
        dialog.Show(host);
        var grid = (DataGridView)dialog.Controls.Find("PullPreviewGrid", true).Single();
        Assert.IsTrue(grid.Rows[0].Cells["Selected"].ReadOnly);
        Assert.IsFalse(dialog.Controls.Find("ExecuteSelectedPull", true).Single().Enabled);
        return Task.CompletedTask;
    });

    [TestMethod]
    public Task RunDetailsGroupsRenamedRowsByIdAndSeparatesSameNamedProjects() => OnUiAsync(host =>
    {
        var rows = new DetailRow[]
        {
            new("PersonalProjects/" + Second, "old.txt", "内容冲突", "review", true) { ProjectId = First },
            new("MyProjects", "new.txt", "内容冲突", "review", true) { ProjectId = First },
            new("PersonalProjects/" + Second, "other.txt", "内容冲突", "review", true) { ProjectId = Second }
        };
        using var dialog = new RunDetailsDialog(rows, projectFilter: Second);
        dialog.Show(host);
        var project = (ComboBox)dialog.Controls.Find("RunDetailsProject", true).Single();
        var grid = (DataGridView)dialog.Controls.Find("RunDetailsGrid", true).Single();
        Assert.HasCount(3, project.Items);
        Assert.AreEqual(2, project.SelectedIndex);
        Assert.AreEqual(Second, ((DetailRow)grid.Rows[0].DataBoundItem!).ProjectId);
        project.SelectedIndex = 1;
        Assert.AreEqual(2, grid.RowCount);
        Assert.IsTrue(grid.Rows.Cast<DataGridViewRow>().All(r => ((DetailRow)r.DataBoundItem!).ProjectId == First));
        return Task.CompletedTask;
    });

    private sealed class PreviewPresenter(ConfigurationEditor editor) : IConfigurationPresenter
    {
        internal readonly TaskCompletionSource<string?> Selector = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal CancellationToken Token;
        public Task<ConfigurationDraft> LoadAsync() => Task.FromResult(editor.Load());
        public Task<ConfigurationSnapshot> ValidateAsync(ConfigurationDraft draft) => Task.FromResult(editor.Validate(draft));
        public Task<bool> SaveAsync(ConfigurationDraft draft, ConfigurationPart part) => throw new AssertFailedException();
        public Task<CloudProjectOnboarding> InspectNewProjectAsync(ProjectDefinition project, CancellationToken token) => throw new AssertFailedException();
        public Task<CloudFolderListing> BrowseCloudAsync(string? folderId, CancellationToken token = default, bool refresh = false) => throw new AssertFailedException();
        public async Task<PreviewReport> PreviewAsync(ConfigurationDraft draft, string? destination = null,
            CancellationToken cancellationToken = default, IProgress<BackupProgress>? progress = null)
        {
            Token = cancellationToken; Selector.TrySetResult(destination);
            await Task.Delay(Timeout.Infinite, cancellationToken);
            throw new AssertFailedException();
        }
    }

    [TestMethod]
    public Task EditingDestinationPreviewsTheOriginalProjectId() => OnUiAsync(async host =>
    {
        using var ws = new TestWorkspace();
        var editor = new ConfigurationEditor(new(Path.Combine(ws.Config, "projects.json"), Path.Combine(ws.Config, "backup-settings.json")), ws.Device);
        var projectId = ws.Load().Projects.Single().ProjectId;
        var presenter = new PreviewPresenter(editor);
        using var dialog = new ProjectEditorDialog(presenter, editor.Load(), 0, "OneDrive/");
        dialog.Show(host);
        dialog.Controls.Find("Destination", true).Single().Text = "MyProjects/Demo";
        ((Button)dialog.Controls.Find("预览草稿", true).Single()).PerformClick();
        Assert.AreEqual(projectId, await presenter.Selector.Task.WaitAsync(TimeSpan.FromSeconds(5)));
        ((Button)dialog.Controls.Find("CancelDraftOperation", true).Single()).PerformClick();
        Assert.IsTrue(presenter.Token.IsCancellationRequested);
    });

    private static async Task OnUiAsync(Func<Form, Task> check)
    {
        var finished = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            using var host = new Form();
            host.Shown += async (_, _) =>
            {
                try { await check(host); finished.TrySetResult(); }
                catch (Exception ex) { finished.TrySetException(ex); }
                finally { host.Close(); }
            };
            Application.Run(host);
        }) { IsBackground = true };
        thread.SetApartmentState(ApartmentState.STA); thread.Start();
        await finished.Task.WaitAsync(TimeSpan.FromSeconds(20));
        Assert.IsTrue(thread.Join(TimeSpan.FromSeconds(3)));
    }
}
