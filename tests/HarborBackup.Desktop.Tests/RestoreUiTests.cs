using HarborBackup.Desktop.Controllers;
using HarborBackup.Desktop.Localization;
using HarborBackup.Desktop.Views.Dialogs;
using HarborBackup.Windows.Integration;

namespace HarborBackup.Windows.Tests;

[TestClass]
[DoNotParallelize]
public sealed class RestoreUiTests
{
    [TestMethod]
    [DataRow("zh-CN")]
    [DataRow("en-US")]
    public async Task RestoreSelectionBudgetAndOutputRemainVisibleInBothLanguages(string language)
    {
        var finished = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            try
            {
                UiText.SetLanguage(language); using var ws = new TestWorkspace();
                var controller = new DesktopController(new RuntimePaths(Path.Combine(ws.Config, "projects.json"), Path.Combine(ws.Config, "backup-settings.json")));
                using var dialog = new RestoreDialog(controller);
                dialog.Show(); Application.DoEvents();
                Assert.AreEqual(UiText.Text("独立目录恢复"), dialog.Text);
                var grid = (DataGridView)dialog.Controls.Find("RestoreCandidates", true).Single();
                Assert.IsTrue(grid.MultiSelect); Assert.HasCount(5, grid.Columns); Assert.IsGreaterThan(90, grid.Height);
                foreach (var name in new[] { "RestoreProject", "RestoreScope", "RestorePathFilter", "RestoreOutput", "RestoreMaxMiB", "RestoreGroups" })
                {
                    var control = dialog.Controls.Find(name, true).Single();
                    Assert.IsTrue(control.Visible, name); Assert.IsFalse(string.IsNullOrWhiteSpace(control.AccessibleName), name);
                    Assert.IsTrue(control.Width > 30 && control.Height > 10, name);
                    var bounds = dialog.RectangleToClient(control.RectangleToScreen(control.ClientRectangle));
                    Assert.IsTrue(dialog.ClientRectangle.Contains(bounds), name + " must fit inside the dialog.");
                }
                var budget = (NumericUpDown)dialog.Controls.Find("RestoreMaxMiB", true).Single();
                Assert.AreEqual(0m, budget.Minimum); Assert.AreEqual(100m, budget.Value);
                var groups = (NumericUpDown)dialog.Controls.Find("RestoreGroups", true).Single(); Assert.AreEqual(256m, groups.Maximum);
                foreach (var width in new[] { 940, 1120 })
                {
                    dialog.Width = width; Application.DoEvents();
                    Assert.AreEqual(budget.PointToScreen(Point.Empty).Y, groups.PointToScreen(Point.Empty).Y,
                        "Budget and rotation inputs must stay together at minimum and default widths.");
                }
                grid.Rows.Add("Demo", "Current", "a.txt", 3, "SHA-256");
                ((ComboBox)dialog.Controls.Find("RestoreScope", true).Single()).SelectedIndex = 1;
                Assert.HasCount(0, grid.Rows);
                grid.DataSource = null; grid.Rows.Add("Demo", "Current", "a.txt", 3, "SHA-256");
                ((TextBox)dialog.Controls.Find("RestorePathFilter", true).Single()).Text = "changed";
                Assert.HasCount(0, grid.Rows);
                Assert.IsFalse(dialog.Controls.Find("恢复选中文件", true).Single().Enabled);
                if (Environment.GetEnvironmentVariable("HARBORBACKUP_SCREENSHOT_DIR") is { Length: > 0 } screenshots)
                {
                    Directory.CreateDirectory(screenshots); using var bitmap = new Bitmap(dialog.Width, dialog.Height);
                    dialog.DrawToBitmap(bitmap, new Rectangle(Point.Empty, bitmap.Size)); bitmap.Save(Path.Combine(screenshots, "restore-" + language + ".png"));
                }
                dialog.Close(); finished.SetResult();
            }
            catch (Exception ex) { finished.SetException(ex); }
        }) { IsBackground = true };
        thread.SetApartmentState(ApartmentState.STA); thread.Start();
        await finished.Task.WaitAsync(TimeSpan.FromSeconds(30)); Assert.IsTrue(thread.Join(TimeSpan.FromSeconds(5)));
    }
}
