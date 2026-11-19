using System.Diagnostics;
using HarborBackup.Core.Domain;
using HarborBackup.Desktop.Controllers;
using HarborBackup.Desktop.Localization;
using HarborBackup.Desktop.Presentation;
using HarborBackup.Desktop.Views.Dialogs;
using HarborBackup.Windows.Integration;
using HarborBackup.Windows.Persistence;
using HarborBackup.Windows.Persistence.Configuration;

namespace HarborBackup.Windows.Tests;

[TestClass]
[DoNotParallelize]
public sealed class DraftPreviewTests
{
    [TestMethod]
    [DataRow("zh-CN", 1f)]
    [DataRow("zh-CN", 1.75f)]
    [DataRow("zh-CN", 2f)]
    [DataRow("en-US", 1f)]
    [DataRow("en-US", 1.75f)]
    [DataRow("en-US", 2f)]
    public async Task PathPickersShareEdgesAndCenterButtonsAfterResizing(string language, float scale)
    {
        using var ws = new TestWorkspace();
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            try
            {
                UiText.SetLanguage(language);
                var paths = new RuntimePaths(Path.Combine(ws.Config, "projects.json"), Path.Combine(ws.Config, "backup-settings.json"));
                var original = File.ReadAllText(paths.ProjectConfig);
                var editor = new ConfigurationEditor(paths, ws.Device);
                var presenter = new SavingPresenter(editor);
                using var dialog = new ProjectEditorDialog(presenter, editor.Load(), null, "OneDrive/");
                dialog.Show(); dialog.Scale(new SizeF(scale, scale));
                Control Find(string name) => dialog.Controls.Find(name, true).Single();
                Rectangle Bounds(Control control) => new(control.PointToScreen(Point.Empty), control.Size);
                foreach (var width in new[] { 650, 780, 1000 })
                {
                    dialog.Size = new((int)(width * scale), (int)(740 * scale));
                    dialog.PerformLayout(); Application.DoEvents();
                    var local = Bounds(Find("SourcePathFrame")); var cloud = Bounds(Find("DestinationFrame"));
                    var localButton = Bounds(Find("SourcePathBrowse")); var cloudButton = Bounds(Find("DestinationBrowse"));
                    Assert.AreEqual(local.Left, cloud.Left, "Both path fields must share their left edge.");
                    Assert.AreEqual(local.Right, cloud.Right, "Both path fields must share their right edge.");
                    Assert.AreEqual(localButton.Left, cloudButton.Left);
                    Assert.AreEqual(localButton.Right, cloudButton.Right);
                    Assert.AreEqual(Bounds(Find("ResolvedTargetFrame")).Right, cloudButton.Right);
                    foreach (var (field, button) in new[] { (local, localButton), (cloud, cloudButton) })
                    {
                        Assert.AreEqual(field.Top, button.Top, "Input frame and browse button must share their top edge.");
                        Assert.AreEqual(field.Bottom, button.Bottom, "Input frame and browse button must share their bottom edge.");
                        Assert.IsGreaterThan(field.Right, button.Left);
                    }
                    foreach (var name in new[] { "SourcePathBrowse", "DestinationBrowse" })
                    {
                        var button = Find(name);
                        Assert.IsGreaterThanOrEqualTo(button.GetPreferredSize(Size.Empty).Width, button.Width);
                    }
                    if (width == 780 && Environment.GetEnvironmentVariable("HARBORBACKUP_SCREENSHOT_DIR") is { Length: > 0 } folder)
                    {
                        Directory.CreateDirectory(folder);
                        using var picture = new Bitmap(dialog.Width, dialog.Height);
                        dialog.DrawToBitmap(picture, new Rectangle(Point.Empty, dialog.Size));
                        picture.Save(Path.Combine(folder, $"path-pickers-{language}-{scale}.png"));
                    }
                }
                Assert.AreEqual(original, File.ReadAllText(paths.ProjectConfig));
                Assert.AreEqual(0, presenter.Saves);
                dialog.Close(); completion.TrySetResult();
            }
            catch (Exception ex) { completion.TrySetException(ex); }
        }) { IsBackground = true };
        thread.SetApartmentState(ApartmentState.STA); thread.Start();
        await completion.Task.WaitAsync(TimeSpan.FromSeconds(30));
        Assert.IsTrue(thread.Join(TimeSpan.FromSeconds(3)));
    }

    private sealed class SavingPresenter(ConfigurationEditor editor) : IConfigurationPresenter
    {
        public Task<CloudProjectOnboarding> InspectNewProjectAsync(ProjectDefinition project, CancellationToken token) => throw new AssertFailedException("Editing an existing project must remain offline.");
        public int Saves;
        public Task<CloudFolderListing> BrowseCloudAsync(string? folderId) => throw new AssertFailedException("Saving must not browse the cloud.");
        public Task<ConfigurationDraft> LoadAsync() => Task.FromResult(editor.Load());
        public Task<ConfigurationSnapshot> ValidateAsync(ConfigurationDraft draft) => Task.FromResult(editor.Validate(draft));
        public Task<bool> SaveAsync(ConfigurationDraft draft, ConfigurationPart part)
        { editor.Save(draft, part); Saves++; return Task.FromResult(true); }
        public Task<PreviewReport> PreviewAsync(ConfigurationDraft draft, string? destination = null, CancellationToken cancellationToken = default, IProgress<BackupProgress>? progress = null)
            => throw new AssertFailedException("Saving must not run a preview.");
    }

    [TestMethod]
    public async Task ChangingSourceSavesDirectlyWithoutPreviewOrMappingConfirmation()
    {
        using var ws = new TestWorkspace();
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            try
            {
                UiText.SetLanguage("zh-CN");
                var editor = new ConfigurationEditor(new(Path.Combine(ws.Config, "projects.json"), Path.Combine(ws.Config, "backup-settings.json")), ws.Device);
                var presenter = new SavingPresenter(editor);
                using var dialog = new ProjectEditorDialog(presenter, editor.Load(), 0, "OneDrive/");
                dialog.Show();
                var identity = (TextBox)dialog.Controls.Find("ProjectId", true).Single();
                Assert.IsTrue(identity.ReadOnly);
                Assert.IsGreaterThan(dialog.ClientSize.Width / 2, identity.Width, "The copyable project ID should use the full field width.");
                Assert.AreEqual(ws.Load().Projects.Single().ProjectId, identity.Text);
                if (Environment.GetEnvironmentVariable("HARBORBACKUP_SCREENSHOT_DIR") is { Length: > 0 } screenshots)
                {
                    Directory.CreateDirectory(screenshots);
                    using var picture = new Bitmap(dialog.Width, dialog.Height);
                    dialog.DrawToBitmap(picture, new Rectangle(Point.Empty, dialog.Size));
                    picture.Save(Path.Combine(screenshots, "project-id.png"));
                }
                var missing = Path.Combine(ws.Root, "new-missing-source");
                dialog.Controls.Find("SourcePath", true).Single().Text = missing;
                ((Button)dialog.Controls.Find("保存配置", true).Single()).PerformClick();
                Assert.AreEqual(1, presenter.Saves);
                Assert.AreEqual(DialogResult.OK, dialog.DialogResult);
                Assert.AreEqual(missing, ws.Load().Projects.Single().Source);
                Assert.IsFalse(Directory.Exists(missing));
                completion.TrySetResult();
            }
            catch (Exception ex) { completion.TrySetException(ex); }
        }) { IsBackground = true };
        thread.SetApartmentState(ApartmentState.STA); thread.Start();
        await completion.Task.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.IsTrue(thread.Join(TimeSpan.FromSeconds(3)));
    }

    [TestMethod]
    [DataRow("zh-CN")]
    [DataRow("en-US")]
    public async Task UploadPreviewConfirmsOnlyCheckedFilesAndShowsDirection(string language)
    {
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            try
            {
                UiText.SetLanguage(language);
                var copy = new FileDecision(FileAction.Copy, true, FileSide.Target, FileSide.Target, ProtectionKind.Archive, "ExplicitLocalUpload");
                var report = new PreviewReport([new("Demo", [new("a.txt", copy, true, false), new("b.txt", copy, true, false)], [], new())])
                { UploadSelection = new("Demo", true, [new("a.txt", "a", false, 1), new("b.txt", "b", false, 1)]) { ProjectId = DeviceIdentity.Digest("demo") } };
                using var dialog = new PreviewDialog(report, source: @"C:\Example", target: "OneDrive/Demo", allowUpload: true);
                dialog.Show();
                StringAssert.Contains(dialog.Text, language == "zh-CN" ? "上传" : "Upload");
                var grid = (DataGridView)dialog.Controls.Find("PullPreviewGrid", true).Single();
                grid.Rows[1].Cells["Selected"].Value = false;
                ((Button)dialog.Controls.Find("ExecuteSelectedPull", true).Single()).PerformClick();
                Assert.IsNull(dialog.SelectedPull);
                Assert.IsNotNull(dialog.SelectedUpload);
                Assert.HasCount(1, dialog.SelectedUpload.Files);
                Assert.AreEqual("a.txt", dialog.SelectedUpload.Files[0].Path);
                Assert.AreEqual(DeviceIdentity.Digest("demo"), dialog.SelectedUpload.ProjectId);
                completion.TrySetResult();
            }
            catch (Exception ex) { completion.TrySetException(ex); }
        }) { IsBackground = true };
        thread.SetApartmentState(ApartmentState.STA); thread.Start();
        await completion.Task.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.IsTrue(thread.Join(TimeSpan.FromSeconds(3)));
    }

    private sealed class SlowPresenter(ConfigurationEditor editor, bool scanning) : IConfigurationPresenter
    {
        public Task<CloudProjectOnboarding> InspectNewProjectAsync(ProjectDefinition project, CancellationToken token) => throw new AssertFailedException("Draft observation must not prepare a save.");
        public TaskCompletionSource Started { get; private set; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public int Saves;
        public int Calls;
        public ConfigurationSnapshot? LastPreview;
        public Task<CloudFolderListing> BrowseCloudAsync(string? folderId) => throw new NotSupportedException();
        public Task<ConfigurationDraft> LoadAsync() => Task.FromResult(editor.Load());
        public Task<ConfigurationSnapshot> ValidateAsync(ConfigurationDraft draft) => Task.Run(() => editor.Validate(draft));
        public Task<bool> SaveAsync(ConfigurationDraft draft, ConfigurationPart part) { Saves++; return Task.FromResult(true); }
        public Task<PreviewReport> PreviewAsync(ConfigurationDraft draft, string? destination = null,
            CancellationToken cancellationToken = default, IProgress<BackupProgress>? progress = null) => Task.Run(async () =>
        {
            LastPreview = editor.Validate(draft);
            Interlocked.Increment(ref Calls);
            for (var i = 0; i < 20000; i++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                progress?.Report(new(scanning ? BackupPhase.Scanning : BackupPhase.ReadingMetadata,"Projects/Demo","file.txt",i,20000,1024));
            }
            Started.TrySetResult();
            await Task.Delay(Timeout.Infinite, cancellationToken);
            return new PreviewReport([]);
        }, cancellationToken);
        public void PrepareRetry() => Started = new(TaskCreationOptions.RunContinuationsAsynchronously);
    }

    [TestMethod]
    [DataRow("zh-CN", true, false)]
    [DataRow("en-US", false, false)]
    [DataRow("zh-CN", false, true)]
    public async Task SlowDraftPreviewRemainsResponsiveAndCanStopOrClose(string language, bool scanning, bool close)
    {
        using var ws = new TestWorkspace();
        var paths = new RuntimePaths(Path.Combine(ws.Config,"projects.json"),Path.Combine(ws.Config,"backup-settings.json"));
        var original = File.ReadAllText(paths.ProjectConfig);
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            using var host = new Form();
            async Task CheckAsync()
            {
                try
                {
                    UiText.SetLanguage(language);
                    var presenter = new SlowPresenter(new ConfigurationEditor(paths), scanning);
                    using var editor = new ProjectEditorDialog(presenter,await presenter.LoadAsync(),null,"OneDrive/");
                    editor.Show(host);
                    var source = (TextBox)editor.Controls.Find("SourcePath",true).Single();
                    var destination = (TextBox)editor.Controls.Find("Destination",true).Single();
                    source.Text = ws.Source; destination.Text = "Projects/Unsaved";
                    editor.Controls.Find("ExcludedDirectories",true).Single().Text = "newdata_process_environment";
                    var preview = (Button)editor.Controls.Find("预览草稿",true).Single();
                    var cancel = (Button)editor.Controls.Find("CancelDraftOperation",true).Single();
                    using var heartbeat = new System.Windows.Forms.Timer { Interval = 20 };
                    var ticks = 0; heartbeat.Tick += (_,_) => ticks++; heartbeat.Start();
                    preview.PerformClick();
                    await presenter.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));
                    CollectionAssert.AreEqual(new[] { "newdata_process_environment" },presenter.LastPreview!.Projects.Last().ExcludedDirectories.ToArray());
                    await Task.Delay(600);
                    Assert.IsGreaterThan(5,ticks,"The UI message loop must keep processing while the preview waits.");
                    Assert.IsTrue(cancel.Enabled);
                    Assert.IsFalse(preview.Enabled);
                    Assert.IsFalse(source.Enabled);
                    var feedback = editor.Controls.Find("DraftPreviewFeedback",true).Single();
                    StringAssert.Contains(feedback.Text,language == "en-US" ? "Elapsed" : "已用");
                    if (Environment.GetEnvironmentVariable("HARBORBACKUP_SCREENSHOT_DIR") is { } folder)
                    {
                        Directory.CreateDirectory(folder);
                        using var image = new Bitmap(editor.Width,editor.Height);
                        editor.DrawToBitmap(image,new Rectangle(0,0,image.Width,image.Height));
                        image.Save(Path.Combine(folder,language+"-draft-"+(scanning ? "scanning" : "network")+".png"));
                    }
                    if (close) editor.Close(); else cancel.PerformClick();
                    var wait = Stopwatch.StartNew();
                    while (close ? !editor.IsDisposed && editor.Visible : !preview.Enabled)
                    { if (wait.Elapsed > TimeSpan.FromSeconds(5)) Assert.Fail("Cancellation did not restore the dialog."); await Task.Delay(20); }
                    Assert.AreEqual(0,presenter.Saves);
                    Assert.AreEqual(original,File.ReadAllText(paths.ProjectConfig));
                    if (!close)
                    {
                        Assert.AreEqual("Projects/Unsaved",destination.Text);
                        Assert.IsTrue(source.Enabled);
                        StringAssert.Contains(feedback.Text,language == "en-US" ? "preserved" : "已保留");
                        presenter.PrepareRetry(); preview.PerformClick();
                        await presenter.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));
                        cancel.PerformClick();
                        wait.Restart();
                        while (!preview.Enabled)
                        { if (wait.Elapsed > TimeSpan.FromSeconds(5)) Assert.Fail("Retry did not cancel."); await Task.Delay(20); }
                        Assert.AreEqual(2,presenter.Calls);
                        editor.Close();
                    }
                    completion.TrySetResult();
                }
                catch (Exception ex) { completion.TrySetException(ex); }
                finally { host.Close(); }
            }
            host.Shown += async (_, _) => await CheckAsync();
            Application.Run(host);
        }) { IsBackground = true };
        thread.SetApartmentState(ApartmentState.STA); thread.Start();
        await completion.Task.WaitAsync(TimeSpan.FromSeconds(20));
        Assert.IsTrue(thread.Join(TimeSpan.FromSeconds(3)));
    }

    [TestMethod]
    [DataRow("add")]
    [DataRow("edit")]
    [DataRow("global")]
    public async Task CollectedDraftKeepsEnteredExclusions(string mode)
    {
        using var ws = new TestWorkspace();
        var paths = new RuntimePaths(Path.Combine(ws.Config,"projects.json"),Path.Combine(ws.Config,"backup-settings.json"));
        var original = File.ReadAllText(paths.ProjectConfig);
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            try
            {
                var validator = new ConfigurationEditor(paths);
                var presenter = new SlowPresenter(validator, true);
                using var dialog = new ProjectEditorDialog(presenter,validator.Load(),mode == "edit" ? 0 : null,"OneDrive/",mode == "global");
                const System.Reflection.BindingFlags flags = System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic;
                TextBox Field(string name) => (TextBox)typeof(ProjectEditorDialog).GetField(name,flags)!.GetValue(dialog)!;
                if (mode == "add") { Field("source").Text = ws.Source; Field("destination").Text = "Projects/New"; }
                Field("directories").Text = "newdata_process_environment\r\ncache";
                Field("files").Text = "*.tmp";
                var draft = (ConfigurationDraft)typeof(ProjectEditorDialog).GetMethod("Collect",flags)!.Invoke(dialog,null)!;
                var snapshot = validator.Validate(draft);
                var project = snapshot.Projects.Last();
                CollectionAssert.AreEqual(new[] { "newdata_process_environment", "cache" },project.ExcludedDirectories.ToArray());
                CollectionAssert.AreEqual(new[] { "*.tmp" },project.ExcludedFiles.ToArray());
                Assert.AreEqual(original,File.ReadAllText(paths.ProjectConfig));
                completion.TrySetResult();
            }
            catch (Exception ex) { completion.TrySetException(ex); }
        }) { IsBackground = true };
        thread.SetApartmentState(ApartmentState.STA); thread.Start();
        await completion.Task.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.IsTrue(thread.Join(TimeSpan.FromSeconds(3)));
    }

    [TestMethod]
    public async Task ExclusiveCancellationReleasesRunLockAndAllowsNextOperation()
    {
        using var ws = new TestWorkspace();
        var paths = new RuntimePaths(Path.Combine(ws.Config,"projects.json"),Path.Combine(ws.Config,"backup-settings.json"));
        var controller = new DesktopController(paths);
        using var cancel = new CancellationTokenSource();
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var task = controller.ExclusiveAsync(token =>
        {
            using var runLock = new RunLock();
            started.SetResult();
            token.WaitHandle.WaitOne();
            token.ThrowIfCancellationRequested();
            return 1;
        },cancel.Token);
        await started.Task.WaitAsync(TimeSpan.FromSeconds(3));
        cancel.Cancel();
        await Assert.ThrowsAsync<OperationCanceledException>(async () => await task.WaitAsync(TimeSpan.FromSeconds(3)));
        Assert.IsFalse(controller.Busy);
        Assert.AreEqual(2,await controller.ExclusiveAsync(() => { using var runLock = new RunLock(); return 2; }));
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task LargePreviewDoesNotRepeatedlyRescanEveryRow(bool pull)
    {
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            try
            {
                var decision = new FileDecision(FileAction.Copy,false,FileSide.Source,FileSide.None,ProtectionKind.None,"TargetOnly");
                var plans = Enumerable.Range(0,20000).Select(i => new PreviewPlan(i+".txt",decision,true,false)).ToArray();
                var report = new PreviewReport([new("Projects/Demo",plans,[],new())])
                { PullSelection = pull ? new("Projects/Demo",false,plans.Select(p => new PullFileSelection(p.Path,p.Path,true,1)).ToArray()) : null };
                var watch = Stopwatch.StartNew();
                using var preview = new PreviewDialog(report,pull,"C:\\Example","OneDrive/Projects/Demo");
                preview.Show();
                var milliseconds = watch.Elapsed.TotalMilliseconds;
                if (Environment.GetEnvironmentVariable("HARBORBACKUP_SCREENSHOT_DIR") is { } folder)
                {
                    Directory.CreateDirectory(folder);
                    File.WriteAllText(Path.Combine(folder,"large-preview-"+pull+".txt"),milliseconds.ToString("F1",System.Globalization.CultureInfo.InvariantCulture));
                }
                Assert.IsLessThan(5000d,milliseconds,"20,000 rows must not perform a full recount for each initialized checkbox.");
                preview.Close();
                completion.TrySetResult();
            }
            catch(Exception ex) { completion.TrySetException(ex); }
        }) { IsBackground = true };
        thread.SetApartmentState(ApartmentState.STA); thread.Start();
        await completion.Task.WaitAsync(TimeSpan.FromSeconds(15));
        Assert.IsTrue(thread.Join(TimeSpan.FromSeconds(3)));
    }
}
