using HarborBackup.Desktop.Presentation;
using HarborBackup.Desktop.Localization;
using HarborBackup.Desktop.Styling;
using HarborBackup.OneDrive.Transport;
using HarborBackup.Windows.Integration;
using HarborBackup.Windows.FileSystem;

namespace HarborBackup.Desktop.Views.Dialogs;

internal sealed class CloudTargetDialog : Form
{
    private readonly Func<string?, Task<CloudFolderListing>> browse;
    private readonly ListBox folders = new AppListBox() { Name = "CloudTargetFolders", Dock = DockStyle.Fill, DisplayMember = "Name", IntegralHeight = false };
    private readonly Label location = new() { Dock = DockStyle.Top, AutoSize = true, Padding = new Padding(0, 8, 0, 12) };
    private readonly TextBox newName = AppStyle.Text();
    private readonly Label error = new() { AutoSize = true, Dock = DockStyle.Top };
    private readonly Stack<(string Id, string Path)> parents = new();
    private CloudFolderListing? listing;
    private string currentPath = "";
    private bool busy;
    public string Destination { get; private set; } = "";
    public string? FolderId { get; private set; }
    internal CloudTargetDialog(Func<string?, Task<CloudFolderListing>> browse)
    {
        this.browse = browse;
        AppStyle.Configure(this, "选择云端备份目录", new Size(660, 540));
        MinimumSize = new Size(540, 430); Padding = new Padding(20);
        var footer = new TableLayoutPanel { Dock = DockStyle.Bottom, AutoSize = true, ColumnCount = 1, Padding = new Padding(0, 10, 0, 0) };
        footer.ColumnStyles.Add(new(SizeType.Percent, 100));
        newName.Name = "NewCloudFolderName";
        newName.PlaceholderText = UiText.Text("可选：在当前位置新建子目录");
        var nameInput = AppStyle.Input(newName); nameInput.Dock = DockStyle.Top;
        footer.Controls.Add(nameInput); footer.Controls.Add(error);
        var choose = AppStyle.Button("确认选择", (_, _) => Choose(), true);
        choose.Name = "ChooseCloudTarget";
        footer.Controls.Add(AppStyle.DialogActions(
            AppStyle.Button("上一级", async (_, _) =>
            { if (!busy && parents.TryPop(out var parent)) await LoadAsync(parent.Id, parent.Path); }),
            choose,
            AppStyle.Button("取消", (_, _) => Close())));
        Controls.Add(AppStyle.Input(folders)); Controls.Add(location); Controls.Add(footer);
        folders.DoubleClick += async (_, _) =>
        {
            if (busy || listing is null || folders.SelectedItem is not RemoteItem item) return;
            parents.Push((listing.Current.Id, currentPath));
            await LoadAsync(item.Id, Join(currentPath, item.Name));
        };
        Shown += async (_, _) => await LoadAsync(null, "");
        ResumeLayout(true);
    }
    private static string Join(string left, string right) => left.Length == 0 ? right : left + "/" + right;
    private async Task LoadAsync(string? id, string path)
    {
        busy = true; folders.Enabled = false; UiText.Set(error, "正在加载…");
        try
        {
            listing = await browse(id);
            if (IsDisposed) return;
            currentPath = listing.Current.DisplayPath?.Trim('/') ?? path;
            location.Text = "OneDrive/" + currentPath;
            folders.DataSource = listing.Folders.ToArray(); folders.ClearSelected();
            UiText.Set(error, "");
        }
        catch (Exception ex) { if (!IsDisposed) UiText.Set(error, CloudAccountPresentation.Error(ex)); }
        finally { busy = false; if (!IsDisposed) folders.Enabled = true; }
    }
    private void Choose()
    {
        if (busy || listing is null) return;
        try
        {
            if (newName.Text.Trim() is { Length: > 0 } name)
            {
                GraphDrive.ValidateName(name); Destination = Join(currentPath, name); FolderId = null;
            }
            else if (folders.SelectedItem is RemoteItem selected)
            { Destination = Join(currentPath, selected.Name); FolderId = selected.Id; }
            else { Destination = currentPath; FolderId = listing.Current.Id; }
            Destination = PathRules.Relative(Destination, true);
            DialogResult = DialogResult.OK;
        }
        catch (Exception ex) { UiText.Set(error, CloudAccountPresentation.Error(ex)); }
    }
}
