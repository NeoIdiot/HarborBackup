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
public sealed class ExclusionEditorTests
{
    private sealed class Presenter(ConfigurationEditor editor) : IConfigurationPresenter
    {
        public Task<ConfigurationDraft> LoadAsync() => Task.FromResult(editor.Load());
        public Task<ConfigurationSnapshot> ValidateAsync(ConfigurationDraft draft) => Task.FromResult(editor.Validate(draft));
        public Task<bool> SaveAsync(ConfigurationDraft draft, ConfigurationPart part)
        { editor.Save(draft, part); return Task.FromResult(true); }
        public Task<CloudProjectOnboarding> InspectNewProjectAsync(ProjectDefinition project, CancellationToken token) => throw new AssertFailedException();
        public Task<CloudFolderListing> BrowseCloudAsync(string? folderId, CancellationToken token = default, bool refresh = false) => throw new AssertFailedException();
        public Task<PreviewReport> PreviewAsync(ConfigurationDraft draft, string? destination = null,
            CancellationToken cancellationToken = default, IProgress<BackupProgress>? progress = null) => throw new AssertFailedException();
    }

    private static async Task OnSta(Action action)
    {
        var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            try { action(); done.TrySetResult(); }
            catch (Exception ex) { done.TrySetException(ex); }
        }) { IsBackground = true };
        thread.SetApartmentState(ApartmentState.STA); thread.Start();
        await done.Task.WaitAsync(TimeSpan.FromSeconds(30));
        Assert.IsTrue(thread.Join(TimeSpan.FromSeconds(3)));
    }

    [TestMethod]
    [DataRow("zh-CN", 1f)]
    [DataRow("en-US", 1f)]
    [DataRow("zh-CN", 2f)]
    [DataRow("en-US", 2f)]
    public async Task GlobalOptionsShowPatternsAndFitBothLanguages(string language, float scale) => await OnSta(() =>
    {
        using var ws = new TestWorkspace();
        UiText.SetLanguage(language);
        var editor = new ConfigurationEditor(new(Path.Combine(ws.Config, "projects.json"), Path.Combine(ws.Config, "backup-settings.json")), ws.Device);
        using var dialog = new ProjectEditorDialog(new Presenter(editor), editor.Load(), null, null, global: true);
        dialog.Show(); dialog.Scale(new SizeF(scale, scale)); dialog.PerformLayout();
        var card = dialog.Controls.Find("BuiltInExclusions", true).Single();
        var checks = card.Controls.OfType<TableLayoutPanel>().Single().Controls.OfType<CheckBox>().ToArray();
        Assert.HasCount(6, checks); Assert.IsFalse(checks.Any(c => c.Checked));
        foreach (var width in new[] { 650, 1000, 780 })
        {
            dialog.Size = new((int)(width * scale), (int)(740 * scale));
            dialog.PerformLayout(); Application.DoEvents();
            foreach (var choice in checks)
            {
                Assert.IsFalse(string.IsNullOrWhiteSpace(choice.AccessibleName));
                Assert.IsFalse(string.IsNullOrWhiteSpace(choice.AccessibleDescription));
                Assert.IsGreaterThanOrEqualTo(choice.PreferredSize.Width, choice.Width);
                Assert.IsLessThanOrEqualTo(choice.Parent!.ClientSize.Width, choice.Right, choice.Text);
            }
            var patterns = card.Controls.OfType<TableLayoutPanel>().Single().Controls.OfType<Label>().ToArray();
            foreach (var (choice, pattern) in checks.Zip(patterns))
            {
                Assert.IsLessThanOrEqualTo(pattern.Parent!.ClientSize.Width, pattern.Right, pattern.Text);
                Assert.IsLessThanOrEqualTo(pattern.Left, choice.Right, choice.Text);
            }
        }
        var rules = card.Controls.OfType<TableLayoutPanel>().Single().Controls.OfType<Label>().ToArray();
        Assert.IsTrue(rules.Any(l => l.Text == "~$*.docx; ~$*.doc"));
        foreach (var label in rules)
            Assert.IsLessThanOrEqualTo(label.Parent!.ClientSize.Width, label.Right, label.Text);
        if (Environment.GetEnvironmentVariable("HARBORBACKUP_SCREENSHOT_DIR") is { Length: > 0 } folder)
        {
            Directory.CreateDirectory(folder);
            using var picture = new Bitmap(dialog.Width, dialog.Height);
            dialog.DrawToBitmap(picture, new Rectangle(Point.Empty, dialog.Size));
            picture.Save(Path.Combine(folder, $"global-exclusions-{language}-{scale}.png"));
        }
        dialog.Close();
    });

    [TestMethod]
    public async Task CancelAndSaveKeepPresetChoicesSeparateFromManualRules() => await OnSta(() =>
    {
        using var ws = new TestWorkspace(); UiText.SetLanguage("zh-CN");
        var paths = new RuntimePaths(Path.Combine(ws.Config, "projects.json"), Path.Combine(ws.Config, "backup-settings.json"));
        var editor = new ConfigurationEditor(paths, ws.Device); var presenter = new Presenter(editor);
        var before = File.ReadAllText(paths.ProjectConfig);
        using (var canceled = new ProjectEditorDialog(presenter, editor.Load(), null, null, global: true))
        {
            canceled.Show(); ((CheckBox)canceled.Controls.Find("Preset_word-locks-v1", true).Single()).Checked = true;
            ((Button)canceled.Controls.Find("CancelDraftOperation", true).Single()).PerformClick();
        }
        Assert.AreEqual(before, File.ReadAllText(paths.ProjectConfig));
        using (var selected = new ProjectEditorDialog(presenter, editor.Load(), null, null, global: true))
        {
            selected.Show(); ((CheckBox)selected.Controls.Find("Preset_word-locks-v1", true).Single()).Checked = true;
            selected.Controls.Find("ExcludedFiles", true).Single().Text = "~$*.docx";
            ((Button)selected.Controls.Find("保存配置", true).Single()).PerformClick();
            Assert.AreEqual(DialogResult.OK, selected.DialogResult);
        }
        Assert.HasCount(2, ws.Load().Projects.Single().ExcludedFiles);
        using (var project = new ProjectEditorDialog(presenter, editor.Load(), 0, null))
        {
            Assert.IsEmpty(project.Controls.Find("Preset_word-locks-v1", true));
            Assert.IsTrue(project.Controls.OfType<Control>().SelectMany(Descendants).Any(c => c.Text.Contains("Word 临时/锁定文件")));
            UiText.SetLanguage("en-US");
            Assert.IsTrue(project.Controls.OfType<Control>().SelectMany(Descendants).Any(c => c.Text.Contains("Word temporary / lock files")));
            UiText.SetLanguage("zh-CN");
        }
        using (var reopened = new ProjectEditorDialog(presenter, editor.Load(), null, null, global: true))
        {
            reopened.Show(); var word = (CheckBox)reopened.Controls.Find("Preset_word-locks-v1", true).Single();
            Assert.IsTrue(word.Checked); word.Checked = false;
            Assert.AreEqual("~$*.docx", reopened.Controls.Find("ExcludedFiles", true).Single().Text);
            ((Button)reopened.Controls.Find("保存配置", true).Single()).PerformClick();
            Assert.AreEqual(DialogResult.OK, reopened.DialogResult);
        }
        CollectionAssert.AreEqual(new[] { "~$*.docx" }, ws.Load().Projects.Single().ExcludedFiles.ToArray());
    });

    private static IEnumerable<Control> Descendants(Control control) => new[] { control }.Concat(control.Controls.Cast<Control>().SelectMany(Descendants));

    [TestMethod]
    public void PreviewSourceTextDistinguishesAllRuleOrigins()
    {
        var sources = new ExclusionRuleSources(["word-locks-v1"], ["cache"], ["*.tmp"], ["private"], ["secret.txt"]);
        foreach (var language in new[] { "zh-CN", "en-US" })
        {
            UiText.SetLanguage(language);
            var text = ExclusionPresentation.Sources(sources);
            foreach (var expected in new[] { "~$*.docx", "cache", "*.tmp", "private", "secret.txt" }) StringAssert.Contains(text, expected);
            StringAssert.Contains(text, language == "zh-CN" ? "全局自定义文件" : "Global custom files");
        }
    }
}
