using HarborBackup.Desktop.Localization;
using HarborBackup.Desktop.Styling;
using HarborBackup.Windows.Persistence.Configuration;

namespace HarborBackup.Desktop.Views.Dialogs;

internal sealed class CloudFoldersDialog : Form
{
    private readonly TextBox archives=new(){Width=340};
    private readonly TextBox conflicts=new(){Width=340};
    private readonly TextBox metadata=new(){Width=340};
    internal CloudFolders Selected => new(archives.Text.Trim(),conflicts.Text.Trim(),metadata.Text.Trim());
    internal CloudFoldersDialog(CloudFolders current)
    {
        AppStyle.Configure(this,"云端子目录",new Size(640,390));MinimumSize=new(580,340);Padding=new(18);
        var table=new TableLayoutPanel{Dock=DockStyle.Top,AutoSize=true,ColumnCount=2};
        table.ColumnStyles.Add(new(SizeType.AutoSize));table.ColumnStyles.Add(new(SizeType.Percent,100));
        archives.Text=current.Archives;conflicts.Text=current.Conflicts;metadata.Text=current.Metadata;
        foreach(var (name,input) in new[]{("历史版本目录",archives),("冲突目录",conflicts),("共享元信息目录",metadata)})
        {
            var row=table.RowCount++;table.RowStyles.Add(new(SizeType.AutoSize));
            var label=UiText.Bind(new Label{AutoSize=true,Anchor=AnchorStyles.Left,Margin=new(0,12,12,12)},name);
            var frame=AppStyle.Input(input);frame.Margin=new(0,8,0,8);frame.Anchor=AnchorStyles.Left|AnchorStyles.Right;
            table.Controls.Add(label,0,row);table.Controls.Add(frame,1,row);
        }
        var feedback = new Label { Dock = DockStyle.Top, AutoSize = true, ForeColor = AppStyle.Muted, Visible = false };
        var buttons=AppStyle.DialogActions(AppStyle.Button("确认选择",(_,_) =>
        {
            try { Selected.Validate(); DialogResult = DialogResult.OK; }
            catch (Exception ex) when (ex is FormatException or ArgumentException)
            {
                UiText.Set(feedback, ex.Message);
                feedback.Visible = true;
            }
        },true),
            AppStyle.Button("取消",(_,_)=>DialogResult=DialogResult.Cancel));buttons.Dock=DockStyle.Bottom;
        Controls.Add(feedback);Controls.Add(table);Controls.Add(buttons);ResumeLayout(true);
    }
}
