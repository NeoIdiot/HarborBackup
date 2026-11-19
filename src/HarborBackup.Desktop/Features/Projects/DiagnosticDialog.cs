using HarborBackup.Desktop.Styling;

namespace HarborBackup.Desktop.Views.Dialogs;

internal sealed class DiagnosticDialog : Form
{
    internal DiagnosticDialog(string message)
    {
        Name = "CloudCatalogDiagnostic";
        AppStyle.Configure(this, "云端项目详情", new Size(720, 420));
        MinimumSize = new Size(460, 280);
        Padding = new Padding(16);
        var text = new TextBox { Name = "DiagnosticText", Text = message, Multiline = true, ReadOnly = true,
            ScrollBars = ScrollBars.Vertical, WordWrap = true, Dock = DockStyle.Fill, BorderStyle = BorderStyle.None };
        var frame = AppStyle.Input(text); frame.Dock = DockStyle.Fill;
        Controls.Add(frame);
        var close = AppStyle.Button("关闭", (_, _) => Close());
        Controls.Add(AppStyle.DialogActions(close));
        AcceptButton = CancelButton = close;
        ResumeLayout(true);
    }
}
