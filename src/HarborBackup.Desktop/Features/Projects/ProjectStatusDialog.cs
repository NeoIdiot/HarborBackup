using HarborBackup.Desktop.Localization;
using HarborBackup.Desktop.Presentation;
using HarborBackup.Desktop.Styling;

namespace HarborBackup.Desktop.Views.Dialogs;

internal sealed class ProjectStatusDialog : Form
{
    internal ProjectStatusDialog(ProjectRow project)
    {
        AppStyle.Configure(this, "项目状态", new Size(700, 440));
        MinimumSize = new Size(520, 340);
        Padding = new Padding(20);
        var details = new TextBox
        {
            Name = "ProjectStatusDetails", ReadOnly = true, Multiline = true, Dock = DockStyle.Fill,
            ScrollBars = ScrollBars.Vertical, WordWrap = true, BorderStyle = BorderStyle.FixedSingle,
            AccessibleName = UiText.Text("项目状态")
        };
        UiText.Watch(details, () => details.Text = string.Join(Environment.NewLine,
            UiText.Text("项目 / 云端目录") + ": " + project.Target,
            UiText.Text("本地目录") + ": " + project.DisplaySource,
            UiText.Text("备份模式") + ": " + project.DisplayAutomaticMode,
            UiText.Text("保护状态") + ": " + project.DisplayProtection,
            "", project.DisplayProtectionDetails).ReplaceLineEndings(Environment.NewLine));
        var close = AppStyle.Button("关闭", (_, _) => Close());
        var actions = AppStyle.DialogActions(close);
        actions.Dock = DockStyle.Bottom;
        var frame = AppStyle.Input(details);
        frame.Dock = DockStyle.Fill;
        Controls.Add(frame);
        Controls.Add(actions);
        AcceptButton = CancelButton = close;
        Shown += (_, _) => { details.SelectionLength = 0; close.Focus(); };
        ResumeLayout(true);
    }
}
