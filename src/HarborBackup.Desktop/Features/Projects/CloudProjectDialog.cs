using HarborBackup.Desktop.Styling;
using HarborBackup.Windows.Integration;

namespace HarborBackup.Desktop.Views.Dialogs;

internal sealed class CloudProjectDialog : Form
{
    private readonly AppListBox projects = new() { Name = "CloudProjects", Dock = DockStyle.Fill };
    internal CloudProjectTarget? Selected => projects.SelectedItem as CloudProjectTarget;
    internal CloudProjectDialog(IReadOnlyList<CloudProjectTarget> items, bool retrieve)
    {
        AppStyle.Configure(this, retrieve ? "取回云端项目…" : "加入云端项目", new Size(560, 380));
        MinimumSize = new Size(400, 280); Padding = new Padding(20);
        projects.DataSource = items;
        var choose = AppStyle.Button("确认选择", (_, _) =>
        { if (Selected is not null) DialogResult = DialogResult.OK; }, true);
        choose.Name = "ChooseCloudProject";
        choose.Enabled = items.Count != 0;
        var cancel = AppStyle.Button("取消", (_, _) => DialogResult = DialogResult.Cancel);
        AcceptButton = choose; CancelButton = cancel;
        Controls.Add(AppStyle.Input(projects));
        Controls.Add(AppStyle.DialogActions(choose, cancel));
        ResumeLayout(true);
    }
}
