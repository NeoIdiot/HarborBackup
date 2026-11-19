using HarborBackup.Desktop.Localization;
using HarborBackup.Desktop.Styling;

namespace HarborBackup.Desktop.Views.Dialogs;

internal sealed class HelpDialog : Form
{
    public HelpDialog()
    {
        AppStyle.Configure(this, "帮助", new Size(760, 680));
        MinimumSize = new Size(560, 440);
        Padding = new Padding(20);
        var body = new RichTextBox { Name = "HelpContent", Dock = DockStyle.Fill, ReadOnly = true, BorderStyle = BorderStyle.None,
            BackColor = AppStyle.Surface, ForeColor = AppStyle.Ink, DetectUrls = false, WordWrap = true, ScrollBars = RichTextBoxScrollBars.Vertical,
            Font = Font, TabStop = true, AccessibleName = UiText.Text("帮助") };
        UiText.Bind(body, "HelpContent");
        var actions = AppStyle.DialogActions(AppStyle.Button("关闭", (_, _) => Close()));
        actions.Dock = DockStyle.Bottom;
        Controls.Add(AppStyle.Input(body)); Controls.Add(actions);
        ResumeLayout(true);
    }
}
