using HarborBackup.Core.Domain;
using HarborBackup.Desktop.Localization;
using HarborBackup.Desktop.Styling;
using HarborBackup.Windows.Integration;

namespace HarborBackup.Desktop.Views.Dialogs;

internal sealed class CloudConflictDialog : Form
{
    private sealed record Choice(string Id, string Caption, string Details) { public override string ToString() => Caption; }
    private readonly ListBox choices = new AppListBox { Name = "ConflictVersions", Dock = DockStyle.Fill };
    private readonly TextBox details = new() { Name = "ConflictVersionDetails", ReadOnly = true, Multiline = true,
        Dock = DockStyle.Fill, ScrollBars = ScrollBars.Vertical };
    private readonly CancellationTokenSource lifetime = new();
    private bool exporting;
    internal string? Selected => (choices.SelectedItem as Choice)?.Id;
    internal CloudConflictDialog(CloudConflictInspection inspection,
        Func<string, string, CancellationToken, Task<string>>? export = null)
    {
        AppStyle.Configure(this, "选择要保留的版本", new Size(960, 620)); Padding = new Padding(18);
        MinimumSize = new Size(760, 500);
        var description = new TextBox { ReadOnly = true, Multiline = true, Height = 78, Dock = DockStyle.Top,
            Text = inspection.Destination + "/" + inspection.Path + Environment.NewLine +
                UiText.Text("未选择的版本会保留到冲突目录。版本变化时操作将停止。") };
        void Add(string id, string origin, string device, FileVersion version, DateTimeOffset? recorded)
        {
            var caption = UiText.Text("{0} · {1:N0} 字节 · 修改于 {2}", origin, version.Length,
                version.LastWriteTimeUtc.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss", UiText.Culture));
            if (device.Length > 0) caption += " · " + device;
            choices.Items.Add(new Choice(id, caption, UiText.Text("来源：{0}\n设备：{1}\n大小：{2:N0} 字节\n文件修改时间：{3}\n版本记录时间：{4}\nSHA-256：{5}",
                origin, device.Length == 0 ? UiText.Text("本机") : device, version.Length,
                version.LastWriteTimeUtc.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss", UiText.Culture),
                recorded?.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss", UiText.Culture) ?? "—", version.Sha256)));
        }
        if (inspection.Local is { } local) Add("local", UiText.Text("本机版本"), "", local, null);
        foreach (var version in inspection.Versions)
            Add(version.Id, UiText.Text("云端版本"), version.Device, version.Version, version.CreatedUtc);
        choices.SelectedIndexChanged += (_, _) => details.Text = (choices.SelectedItem as Choice)?.Details ?? "";
        if (choices.Items.Count > 0) choices.SelectedIndex = 0;
        var confirm = AppStyle.Button("确认选择", (_, _) => { if (!exporting && Selected is not null) DialogResult = DialogResult.OK; }, true);
        var save = AppStyle.Button("另存一份…"); save.Name = "ExportConflictVersion"; save.Enabled = export is not null;
        save.Click += async (_, _) =>
        {
            if (exporting || Selected is not { } choice || export is null) return;
            using var picker = new FolderBrowserDialog { Description = UiText.Text("选择独立的查看目录，不覆盖现有文件") };
            if (picker.ShowDialog(this) != DialogResult.OK) return;
            exporting = true; choices.Enabled = confirm.Enabled = save.Enabled = false;
            try
            {
                var path = await export(choice, picker.SelectedPath, lifetime.Token);
                if (!IsDisposed) details.Text += Environment.NewLine + Environment.NewLine + UiText.Text("已核验并另存：{0}", path);
            }
            catch (OperationCanceledException) { }
            catch (Exception ex) { if (!IsDisposed) details.Text += Environment.NewLine + Environment.NewLine + UiText.Text("另存失败：{0}", ex.Message); }
            finally
            {
                exporting = false;
                if (!IsDisposed) choices.Enabled = confirm.Enabled = save.Enabled = true;
            }
        };
        var buttons = AppStyle.DialogActions(save, confirm, AppStyle.Button("取消", (_, _) => Close()));
        buttons.Dock = DockStyle.Bottom;
        var body = new TableLayoutPanel { Dock = DockStyle.Fill, RowCount = 2, ColumnCount = 1 };
        body.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        body.RowStyles.Add(new RowStyle(SizeType.Percent, 45)); body.RowStyles.Add(new RowStyle(SizeType.Percent, 55));
        body.Controls.Add(AppStyle.Input(choices), 0, 0); body.Controls.Add(AppStyle.Input(details), 0, 1);
        Controls.Add(body); Controls.Add(description); Controls.Add(buttons);
        FormClosing += (_, _) => lifetime.Cancel();
        ResumeLayout(true);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) lifetime.Cancel();
        base.Dispose(disposing);
    }
}
