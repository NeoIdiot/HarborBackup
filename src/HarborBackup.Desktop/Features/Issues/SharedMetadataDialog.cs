using HarborBackup.Desktop.Presentation;
using HarborBackup.Desktop.Controllers;
using HarborBackup.Desktop.Localization;
using HarborBackup.Desktop.Styling;
using HarborBackup.Windows.Integration;
using HarborBackup.Windows.Persistence;

namespace HarborBackup.Desktop.Views.Dialogs;

internal sealed class SharedMetadataDialog : Form
{
    private sealed record Row(string Project, string Device, string Path, string Source, string Target, string Common, string Result, string Observed, string FullDetails);
    private readonly DesktopController controller;
    private readonly DataGridView grid = AppStyle.Grid(("Project", "项目", 15), ("Device", "设备", 12), ("Path", "相对路径", 23),
        ("Source", "源版本", 10), ("Target", "目标版本", 10), ("Common", "共同版本", 10), ("Result", "状态", 15), ("Observed", "观察时间", 18));
    private readonly TextBox search = new() { Width = 320, PlaceholderText = UiText.Text("筛选设备、项目或文件…") };
    private readonly Label feedback = new() { AutoSize = true, Dock = DockStyle.Bottom, Padding = new Padding(0, 8, 0, 8) };
    private readonly Button refresh;
    private IReadOnlyList<Row> rows = [];
    internal SharedMetadataDialog(DesktopController controller)
    {
        this.controller = controller;
        AppStyle.Configure(this, "设备文件状态", new Size(1120, 650)); MinimumSize = new Size(880, 480); Padding = new Padding(18);
        refresh = AppStyle.Button("刷新记录", async (_, _) => await LoadRowsAsync());
        var actions = AppStyle.Toolbar(refresh, AppStyle.Button("查看选中详情", (_, _) =>
        {
            if (grid.CurrentRow?.DataBoundItem is Row row) MessageBox.Show(this, row.FullDetails, UiText.Text("设备文件状态"));
        }), AppStyle.Input(search));
        search.AccessibleName = UiText.Text("筛选设备、项目或文件…");
        search.TextChanged += (_, _) => Filter();
        Controls.Add(grid); Controls.Add(actions); Controls.Add(feedback);
        Shown += async (_, _) => await LoadRowsAsync();
        ResumeLayout(true);
    }
    internal async Task LoadRowsAsync()
    {
        refresh.Enabled = false;
        try
        {
            var result = await Task.Run(() =>
            {
                var config = new BackupRuntime(controller.Paths).Load();
                var collected = new List<Row>(); var errors = new List<string>();
                    var revisions = new CloudBackupRuntime(config).RevisionsAsync(null, default).GetAwaiter().GetResult();
                    foreach (var revision in revisions)
                    {
                        var project = config.Projects.Single(p => CloudRepository.ProjectKey(p) == revision.Project);
                        collected.Add(new(project.Destination, revision.Device[..12], revision.Path, "—", revision.Version.Sha256[..10],
                            string.Join(", ", revision.Parents.Select(p => p[..8])), UiText.Text("云端版本记录"),
                            revision.CreatedUtc.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss"),
                            System.Text.Json.JsonSerializer.Serialize(revision, AtomicJson.Options)));
                    }
                return (Rows: collected, Errors: errors);
            });
            if (IsDisposed) return;
            rows = result.Rows; Filter();
            feedback.Text = result.Errors.Count == 0 ? UiText.Text("云端版本记录") : string.Join(Environment.NewLine, result.Errors);
        }
        catch (Exception ex) { if (!IsDisposed) { rows = []; Filter(); feedback.Text = CloudAccountPresentation.Error(ex); } }
        finally { if (!IsDisposed) refresh.Enabled = true; }
    }
    private void Filter() => grid.DataSource = rows.Where(r => (r.Project + " " + r.Device + " " + r.Path).Contains(search.Text, StringComparison.OrdinalIgnoreCase)).ToArray();
}
