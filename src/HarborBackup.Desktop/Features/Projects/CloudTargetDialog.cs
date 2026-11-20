using HarborBackup.Desktop.Presentation;
using HarborBackup.Desktop.Localization;
using HarborBackup.Desktop.Styling;
using HarborBackup.OneDrive.Transport;
using HarborBackup.Windows.Integration;
using HarborBackup.Windows.FileSystem;

namespace HarborBackup.Desktop.Views.Dialogs;

internal sealed class CloudTargetDialog : Form
{
    private readonly Func<string?, CancellationToken, bool, Task<CloudFolderListing>> browse;
    private readonly Func<CloudFolderListing, CancellationToken, string?, Task>? prefetch;
    private readonly string? preferredFolderId;
    private readonly string? preferredRootName;
    private readonly CancellationTokenSource cancellation = new();
    private CancellationTokenSource? prefetchCancellation;
    private readonly ListBox folders = new AppListBox() { Name = "CloudTargetFolders", Dock = DockStyle.Fill, DisplayMember = "Name", IntegralHeight = false };
    private readonly Label location = new() { Name = "CloudTargetLocation", Dock = DockStyle.Top, AutoSize = true, Padding = new Padding(0, 8, 0, 12) };
    private readonly TextBox newName = AppStyle.Text();
    private readonly Label error = new() { Name = "CloudTargetError", AutoSize = true, Dock = DockStyle.Top };
    private readonly Stack<ViewState> parents = new();
    private readonly Dictionary<string, ViewState> views = new(StringComparer.Ordinal);
    private readonly Button up;
    private readonly Button refresh;
    private readonly Button choose;
    private CloudFolderListing? listing;
    private string currentPath = "";
    private bool busy;
    private bool stopped;
    private bool cancellationDisposed;
    private sealed record ViewState(string Id, string? SelectedId, int TopIndex);
    private readonly TaskCompletionSource ready = new(TaskCreationOptions.RunContinuationsAsynchronously);
    internal Task Ready => ready.Task;
    public string Destination { get; private set; } = "";
    public string? FolderId { get; private set; }
    internal CloudTargetDialog(Func<string?, CancellationToken, bool, Task<CloudFolderListing>> browse,
        Func<CloudFolderListing, CancellationToken, string?, Task>? prefetch = null,
        string? initialFolderId = null, string? initialPath = null)
    {
        this.browse = browse;
        this.prefetch = prefetch;
        preferredFolderId = initialFolderId;
        preferredRootName = initialPath?.Trim('/').Split('/')[0];
        AppStyle.Configure(this, "选择云端备份目录", new Size(660, 540));
        MinimumSize = new Size(540, 430); Padding = new Padding(20);
        var footer = new TableLayoutPanel { Dock = DockStyle.Bottom, AutoSize = true, ColumnCount = 1, Padding = new Padding(0, 10, 0, 0) };
        footer.ColumnStyles.Add(new(SizeType.Percent, 100));
        newName.Name = "NewCloudFolderName";
        newName.PlaceholderText = UiText.Text("可选：在当前位置新建子目录");
        var nameInput = AppStyle.Input(newName); nameInput.Dock = DockStyle.Top;
        footer.Controls.Add(nameInput); footer.Controls.Add(error);
        choose = AppStyle.Button("确认选择", (_, _) => Choose(), true);
        choose.Name = "ChooseCloudTarget";
        up = AppStyle.Button("上一级", async (_, _) => await NavigateUpAsync());
        up.Name = "CloudTargetUp";
        refresh = AppStyle.Button("刷新", async (_, _) => await RefreshAsync());
        refresh.Name = "CloudTargetRefresh";
        var cancel = AppStyle.Button("取消", (_, _) => Close());
        cancel.Name = "CloudTargetCancel";
        footer.Controls.Add(AppStyle.DialogActions(
            up, refresh, choose, cancel));
        Controls.Add(AppStyle.Input(folders)); Controls.Add(location); Controls.Add(footer);
        folders.DoubleClick += async (_, _) => await NavigateIntoSelectionAsync();
        Shown += async (_, _) =>
        {
            try { await OpenAsync(initialFolderId, initialPath); ready.TrySetResult(); }
            catch (Exception ex) { ready.TrySetException(ex); }
        };
        FormClosed += (_, _) => Stop();
        ClientSizeChanged += (_, _) => UpdateTextBounds();
        UpdateTextBounds();
        UpdateActions();
        ResumeLayout(true);
    }
    private static string Join(string left, string right) => left.Length == 0 ? right : left + "/" + right;
    private async Task OpenAsync(string? id, string? path)
    {
        // A configured folder opens directly. If it was removed, the root remains browsable.
        if (!await LoadAsync(id, id is null ? "" : path ?? "") && id is not null && !stopped)
            await LoadAsync(null, "");
    }
    internal async Task NavigateIntoSelectionAsync()
    {
        if (busy || stopped || listing is null || folders.SelectedItem is not RemoteItem item) return;
        var parent = CaptureView();
        if (await LoadAsync(item.Id, Join(currentPath, item.Name))) parents.Push(parent);
        UpdateActions();
    }
    internal async Task NavigateUpAsync()
    {
        if (busy || stopped || listing is null || currentPath.Length == 0) return;
        var hasParent = parents.TryPeek(out var parent) &&
            (listing.Current.ParentId is null || listing.Current.ParentId == parent.Id);
        var path = currentPath.Contains('/') ? currentPath[..currentPath.LastIndexOf('/')] : "";
        var id = hasParent ? parent!.Id : listing.Current.ParentId;
        if (await LoadAsync(id, path))
        {
            if (hasParent) parents.Pop();
            else parents.Clear();
        }
        UpdateActions();
    }
    internal async Task RefreshAsync()
    {
        if (busy || stopped) return;
        await LoadAsync(listing?.Current.Id, currentPath, refreshListing: true);
    }
    private ViewState CaptureView() => new(listing!.Current.Id,
        (folders.SelectedItem as RemoteItem)?.Id, folders.Items.Count == 0 ? 0 : folders.TopIndex);
    private void UpdateTextBounds()
    {
        var width = Math.Max(80, ClientSize.Width - Padding.Horizontal);
        location.MaximumSize = error.MaximumSize = new Size(width, 0);
    }
    private void UpdateActions()
    {
        if (IsDisposed) return;
        folders.Enabled = newName.Enabled = !busy && !stopped && listing is not null;
        choose.Enabled = !busy && !stopped && listing is not null;
        up.Enabled = !busy && !stopped && listing is not null && currentPath.Length != 0;
        refresh.Enabled = !busy && !stopped;
    }
    private async Task<bool> LoadAsync(string? id, string path, bool refreshListing = false)
    {
        if (stopped) return false;
        busy = true;
        UpdateActions();
        try
        {
            var pending = browse(id, cancellation.Token, refreshListing);
            StopPrefetch();
            if (!pending.IsCompleted) UiText.Set(error, "正在加载…");
            var loaded = await pending;
            if (stopped || IsDisposed) return false;
            var changedDirectory = listing?.Current.Id != loaded.Current.Id;
            if (listing is not null) views[listing.Current.Id] = CaptureView();
            listing = loaded;
            currentPath = loaded.Current.DisplayPath?.Trim('/') ?? path;
            location.Text = "OneDrive/" + currentPath;
            folders.DataSource = loaded.Folders.ToArray();
            folders.ClearSelected();
            if (views.TryGetValue(loaded.Current.Id, out var view))
            {
                if (view.SelectedId is not null)
                    folders.SelectedItem = loaded.Folders.FirstOrDefault(f => f.Id == view.SelectedId);
                if (folders.Items.Count > 0) folders.TopIndex = Math.Min(view.TopIndex, folders.Items.Count - 1);
            }
            if (changedDirectory) newName.Clear();
            UiText.Set(error, "");
            StartPrefetch(loaded);
            return true;
        }
        catch (OperationCanceledException) when (stopped || cancellation.IsCancellationRequested) { return false; }
        catch (Exception ex)
        {
            if (!stopped && !IsDisposed) UiText.Set(error, CloudAccountPresentation.Error(ex));
            return false;
        }
        finally { busy = false; if (!IsDisposed) UpdateActions(); }
    }
    private void StartPrefetch(CloudFolderListing loaded)
    {
        if (prefetch is null || stopped) return;
        var next = CancellationTokenSource.CreateLinkedTokenSource(cancellation.Token);
        prefetchCancellation = next;
        var preferred = loaded.Folders.FirstOrDefault(f => f.Id == preferredFolderId)?.Id ??
            loaded.Folders.FirstOrDefault(f => f.Name.Equals(preferredRootName, StringComparison.OrdinalIgnoreCase))?.Id;
        _ = PrefetchAsync(loaded, next, preferred);
    }
    private async Task PrefetchAsync(CloudFolderListing loaded, CancellationTokenSource source, string? preferred)
    {
        try { await prefetch!(loaded, source.Token, preferred); }
        catch (OperationCanceledException) { }
        catch (Exception) { /* Speculative browsing must not replace a successful navigation with an error. */ }
        finally
        {
            if (ReferenceEquals(prefetchCancellation, source)) prefetchCancellation = null;
            source.Dispose();
        }
    }
    private void StopPrefetch()
    {
        var previous = prefetchCancellation;
        prefetchCancellation = null;
        previous?.Cancel();
    }
    private void Stop()
    {
        if (stopped) return;
        stopped = true;
        cancellation.Cancel();
        StopPrefetch();
    }
    protected override void Dispose(bool disposing)
    {
        if (disposing && !cancellationDisposed)
        {
            Stop();
            cancellation.Dispose();
            cancellationDisposed = true;
        }
        base.Dispose(disposing);
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
