using HarborBackup.Core.Domain;
using HarborBackup.Desktop.Localization;
using HarborBackup.Desktop.Presentation;
using HarborBackup.Desktop.Views.Dialogs;
using HarborBackup.Windows.Integration;
using HarborBackup.Windows.Persistence;
using HarborBackup.Windows.Persistence.Configuration;

namespace HarborBackup.Windows.Tests;

[TestClass]
[DoNotParallelize]
public sealed class ProjectOnboardingTests
{
    private sealed class Presenter(ConfigurationEditor editor, CloudProjectOnboarding inspection, bool failedReview) : IConfigurationPresenter
    {
        public int Saves, Reviews, Inspections;
        public Task<CloudFolderListing> BrowseCloudAsync(string? folderId) => throw new AssertFailedException("Unexpected browse.");
        public Task<ConfigurationDraft> LoadAsync() => Task.FromResult(editor.Load());
        public Task<ConfigurationSnapshot> ValidateAsync(ConfigurationDraft draft) => Task.FromResult(editor.Validate(draft));
        public Task<CloudProjectOnboarding> InspectNewProjectAsync(ProjectDefinition project, CancellationToken token)
        { Inspections++; return failedReview && inspection.ExistingProject is not null
            ? Task.FromException<CloudProjectOnboarding>(new IOException("Synthetic cloud failure")) : Task.FromResult(inspection); }
        public Task<bool> SaveAsync(ConfigurationDraft draft, ConfigurationPart part)
        { editor.Save(draft, part); Saves++; return Task.FromResult(true); }
        public Task<PreviewReport> PreviewAsync(ConfigurationDraft draft, string? destination = null,
            CancellationToken cancellationToken = default, IProgress<BackupProgress>? progress = null)
        {
            Reviews++;
            var project = editor.Validate(draft).Projects.Single(p => p.ProjectId == destination);
            return Task.FromResult(new PreviewReport([new(project.Destination,
                [new("different.txt", new(FileAction.ConflictUnresolved, true, FileSide.None, FileSide.Both, ProtectionKind.Conflict, "NoCommonCloudBaseline"), true, false)],
                failedReview ? [new("CloudPreviewFailed", "Synthetic observation failure")] : [], new())]));
        }
    }

    [TestMethod]
    [DataRow(true, true, true, false, "zh-CN", false)]
    [DataRow(true, true, false, false, "en-US", false)]
    [DataRow(false, true, true, false, "en-US", false)]
    [DataRow(false, true, false, false, "zh-CN", false)]
    [DataRow(false, true, true, true, "zh-CN", false)]
    [DataRow(false, false, true, false, "zh-CN", false)]
    [DataRow(true, true, true, true, "zh-CN", false)]
    [DataRow(true, true, true, false, "en-US", true)]
    public async Task AddProjectGuidesJoiningOrInitialReviewWithoutTransferringFiles(bool registered, bool contents, bool accept, bool failedReview, string language, bool alreadyJoined)
    {
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            try
            {
                using var ws = new TestWorkspace(); UiText.SetLanguage(language);
                var paths = new RuntimePaths(Path.Combine(ws.Config, "projects.json"), Path.Combine(ws.Config, "backup-settings.json"));
                var original = File.ReadAllText(paths.ProjectConfig);
                var source = Path.Combine(ws.Root, "existing-local"); Directory.CreateDirectory(source);
                var file = Path.Combine(source, "different.txt"); File.WriteAllText(file, "keep local content");
                var originalId = alreadyJoined ? ws.Load().Projects.Single().ProjectId : DeviceIdentity.Digest("registered-cloud-project");
                var inspection = new CloudProjectOnboarding(registered ? new("NewProject", "cloud-folder", originalId) : null, "cloud-folder", contents);
                var editor = new ConfigurationEditor(paths, ws.Device);
                var presenter = new Presenter(editor, inspection, failedReview);
                using var dialog = new ProjectEditorDialog(presenter, editor.Load(), null, "OneDrive/"); dialog.Show();
                var newId = dialog.Controls.Find("ProjectId", true).Single().Text;
                dialog.Controls.Find("SourcePath", true).Single().Text = source;
                dialog.Controls.Find("Destination", true).Single().Text = "NewProject";
                dialog.Controls.Find("ExcludedFiles", true).Single().Text = "*.tmp";
                var seen = false;
                using var response = new System.Windows.Forms.Timer { Interval = 25 };
                response.Tick += (_, _) =>
                {
                    var prompt = Application.OpenForms.Cast<Form>().FirstOrDefault(f => f.Name == (registered ? "ConfirmExistingProject" : "InitialProjectReview"));
                    if (prompt is null) return;
                    seen = true; response.Stop();
                    var button = (Button)prompt.Controls.Find(registered ? "ContinueCloudProject" : "ConfirmInitialReview", true).Single();
                    if (failedReview) Assert.IsFalse(button.Enabled);
                    if (accept && !failedReview) button.PerformClick(); else prompt.Close();
                };
                response.Start(); ((Button)dialog.Controls.Find("保存配置", true).Single()).PerformClick();
                Assert.AreEqual(1, presenter.Inspections);
                Assert.AreEqual(!registered && contents ? 1 : 0, presenter.Reviews);
                Assert.AreEqual((registered || contents) && !alreadyJoined && !(registered && failedReview), seen);
                var saved = accept && !failedReview && !alreadyJoined;
                Assert.AreEqual(saved ? 1 : 0, presenter.Saves);
                if (saved)
                {
                    var project = ws.Load().Projects.Single(p => p.Destination == "NewProject");
                    Assert.AreEqual(registered ? originalId : newId, project.ProjectId);
                    Assert.AreEqual(source, project.Source); Assert.AreEqual("cloud-folder", project.CloudFolderId);
                    CollectionAssert.Contains(project.ExcludedFiles.ToArray(), "*.tmp");
                    Assert.IsTrue(CloudMapping.NeedsFirstOperation(project, ws.Load()));
                }
                else Assert.AreEqual(original, File.ReadAllText(paths.ProjectConfig));
                Assert.AreEqual("keep local content", File.ReadAllText(file));
                completion.TrySetResult();
            }
            catch (Exception ex) { completion.TrySetException(ex); }
        }) { IsBackground = true };
        thread.SetApartmentState(ApartmentState.STA); thread.Start();
        await completion.Task.WaitAsync(TimeSpan.FromSeconds(15)); Assert.IsTrue(thread.Join(TimeSpan.FromSeconds(3)));
    }
}
