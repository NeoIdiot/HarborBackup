using HarborBackup.Desktop.Localization;
using HarborBackup.Desktop.Views.Dialogs;
using HarborBackup.OneDrive.Transport;
using HarborBackup.Windows.Integration;

namespace HarborBackup.Windows.Tests;

[TestClass]
[DoNotParallelize]
public sealed class CloudTargetDialogTests
{
    [TestMethod]
    public Task ReturningToParentRestoresSelectionAndScroll() => OnUiAsync(async host =>
    {
        var children = Enumerable.Range(0, 50).Select(i => Folder("folder-" + i, "Folder " + i, "root")).ToArray();
        var root = new CloudFolderListing(Folder("root", "OneDrive", path: ""), children);
        var selected = children[30];
        var nested = new CloudFolderListing(selected with { DisplayPath = selected.Name }, [Folder("leaf", "Leaf", selected.Id)]);
        using var dialog = new CloudTargetDialog((id, _, _) => Task.FromResult(id == selected.Id ? nested : root));
        dialog.Show(host); await dialog.Ready;
        var folders = Find<ListBox>(dialog, "CloudTargetFolders");
        folders.SelectedIndex = 30; folders.TopIndex = 25;
        var top = folders.TopIndex;

        await dialog.NavigateIntoSelectionAsync();
        Assert.AreEqual("OneDrive/" + selected.Name, Find<Label>(dialog, "CloudTargetLocation").Text);
        Assert.HasCount(1, folders.Items);
        Assert.AreEqual(-1, folders.SelectedIndex);

        await dialog.NavigateUpAsync();
        Assert.AreEqual("OneDrive/", Find<Label>(dialog, "CloudTargetLocation").Text);
        Assert.AreEqual(selected.Id, ((RemoteItem)folders.SelectedItem!).Id);
        Assert.AreEqual(top, folders.TopIndex);
        Assert.IsFalse(Find<Button>(dialog, "CloudTargetUp").Enabled);
    });

    [TestMethod]
    public Task FailedNavigationRetainsPositionAndDoesNotConsumeParentHistory() => OnUiAsync(async host =>
    {
        var alpha = Folder("alpha", "Alpha", "root", "Alpha");
        var leaf = Folder("leaf", "Leaf", "alpha");
        var root = new CloudFolderListing(Folder("root", "OneDrive", path: ""), [alpha]);
        var nested = new CloudFolderListing(alpha, [leaf]);
        var failChild = true; var failParent = false;
        using var dialog = new CloudTargetDialog((id, _, _) =>
        {
            if (id == "alpha" && failChild || id == "root" && failParent) throw new IOException("Synthetic browse failure");
            return Task.FromResult(id == "alpha" ? nested : root);
        });
        dialog.Show(host); await dialog.Ready;
        var folders = Find<ListBox>(dialog, "CloudTargetFolders");
        folders.SelectedIndex = 0;

        await dialog.NavigateIntoSelectionAsync();
        Assert.AreEqual("OneDrive/", Find<Label>(dialog, "CloudTargetLocation").Text);
        Assert.AreEqual(alpha.Id, ((RemoteItem)folders.SelectedItem!).Id);
        Assert.IsFalse(string.IsNullOrEmpty(Find<Label>(dialog, "CloudTargetError").Text));

        failChild = false;
        await dialog.NavigateIntoSelectionAsync();
        folders.SelectedIndex = 0;
        failParent = true;
        await dialog.NavigateUpAsync();
        Assert.AreEqual("OneDrive/Alpha", Find<Label>(dialog, "CloudTargetLocation").Text);
        Assert.AreEqual(leaf.Id, ((RemoteItem)folders.SelectedItem!).Id);
        Assert.IsTrue(Find<Button>(dialog, "CloudTargetUp").Enabled);

        failParent = false;
        await dialog.NavigateUpAsync();
        Assert.AreEqual("OneDrive/", Find<Label>(dialog, "CloudTargetLocation").Text);
        Assert.AreEqual(alpha.Id, ((RemoteItem)folders.SelectedItem!).Id);
        Assert.IsFalse(Find<Button>(dialog, "CloudTargetUp").Enabled);
        Assert.AreEqual("", Find<Label>(dialog, "CloudTargetError").Text);
    });

    [TestMethod]
    public Task RefreshRequestsFreshListingAndKeepsSelectionOnFailureAndSuccess() => OnUiAsync(async host =>
    {
        var children = Enumerable.Range(0, 30).Select(i => Folder("folder-" + i, "Folder " + i, "root")).ToArray();
        var root = new CloudFolderListing(Folder("root", "OneDrive", path: ""), children);
        var refreshed = root with { Folders = children.Select(f => f.Id == "folder-15" ? f with { Name = "Renamed folder" } : f).ToArray() };
        var fail = true;
        var requests = new List<bool>();
        using var dialog = new CloudTargetDialog((_, _, refresh) =>
        {
            requests.Add(refresh);
            if (refresh && fail) throw new IOException("Synthetic refresh failure");
            return Task.FromResult(refresh ? refreshed : root);
        });
        dialog.Show(host); await dialog.Ready;
        var folders = Find<ListBox>(dialog, "CloudTargetFolders");
        folders.SelectedIndex = 15; folders.TopIndex = 10;
        var top = folders.TopIndex;

        await dialog.RefreshAsync();
        Assert.AreEqual("OneDrive/", Find<Label>(dialog, "CloudTargetLocation").Text);
        Assert.AreEqual("folder-15", ((RemoteItem)folders.SelectedItem!).Id);
        Assert.AreEqual("Folder 15", ((RemoteItem)folders.SelectedItem!).Name);
        Assert.AreEqual(top, folders.TopIndex);
        Assert.IsFalse(string.IsNullOrEmpty(Find<Label>(dialog, "CloudTargetError").Text));
        Assert.IsTrue(Find<Button>(dialog, "ChooseCloudTarget").Enabled);

        fail = false;
        await dialog.RefreshAsync();
        Assert.AreEqual("folder-15", ((RemoteItem)folders.SelectedItem!).Id);
        Assert.AreEqual("Renamed folder", ((RemoteItem)folders.SelectedItem!).Name);
        Assert.AreEqual(top, folders.TopIndex);
        Assert.AreEqual("", Find<Label>(dialog, "CloudTargetError").Text);
        CollectionAssert.AreEqual(new[] { false, true, true }, requests.ToArray());
    });

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public Task ClosingOrDisposingCancelsReadAndIgnoresLateResult(bool dispose) => OnUiAsync(async host =>
    {
        var started = new TaskCompletionSource<CancellationToken>(TaskCreationOptions.RunContinuationsAsynchronously);
        var late = new TaskCompletionSource<CloudFolderListing>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var dialog = new CloudTargetDialog((_, token, _) =>
        {
            started.TrySetResult(token);
            return late.Task; // Deliberately ignore cancellation to model an already-completing provider response.
        });
        dialog.Show(host);
        var token = await started.Task;
        var location = Find<Label>(dialog, "CloudTargetLocation");
        var before = location.Text;
        Assert.IsTrue(token.CanBeCanceled);
        Assert.IsFalse(Find<Button>(dialog, "ChooseCloudTarget").Enabled);

        if (dispose) dialog.Dispose(); else dialog.Close();
        Assert.IsTrue(token.IsCancellationRequested);
        late.SetResult(new(Folder("late-root", "OneDrive", path: "Late"), []));
        await dialog.Ready;
        Assert.AreEqual(before, location.Text, "A completed response cannot update a closed dialog.");
    });

    [TestMethod]
    public Task InitialConfiguredFolderCanNavigateUsingItsParentIdentity() => OnUiAsync(async host =>
    {
        var root = Folder("root", "OneDrive", path: "");
        var parent = Folder("projects", "Projects", "root", "Projects");
        var current = Folder("current", "Current", "projects", "Projects/Current");
        var requests = new List<string?>();
        using var dialog = new CloudTargetDialog((id, _, _) =>
        {
            requests.Add(id);
            return Task.FromResult(id switch
            {
                "current" => new CloudFolderListing(current, []),
                "projects" => new CloudFolderListing(parent, [current]),
                "root" => new CloudFolderListing(root, [parent]),
                _ => throw new AssertFailedException("Configured browsing must start at its selected folder ID.")
            });
        }, initialFolderId: current.Id, initialPath: "Projects/Current");
        dialog.Show(host); await dialog.Ready;
        Assert.AreEqual("OneDrive/Projects/Current", Find<Label>(dialog, "CloudTargetLocation").Text);

        await dialog.NavigateUpAsync();
        Assert.AreEqual("OneDrive/Projects", Find<Label>(dialog, "CloudTargetLocation").Text);
        await dialog.NavigateUpAsync();
        Assert.AreEqual("OneDrive/", Find<Label>(dialog, "CloudTargetLocation").Text);
        Assert.IsFalse(Find<Button>(dialog, "CloudTargetUp").Enabled);
        CollectionAssert.AreEqual(new[] { "current", "projects", "root" }, requests.ToArray());
    });

    [TestMethod]
    public Task TypedDestinationPrioritizesItsRootBranchForPrefetch() => OnUiAsync(async host =>
    {
        string? preferred = null;
        using var dialog = new CloudTargetDialog((_, _, _) => Task.FromResult(new CloudFolderListing(
            Folder("root", "OneDrive", path: ""), [Folder("other", "Other", "root"), Folder("projects", "Projects", "root")])),
            (_, _, id) => { preferred = id; return Task.CompletedTask; }, initialPath: "Projects/Current");
        dialog.Show(host); await dialog.Ready;
        Assert.AreEqual("projects", preferred);
        Assert.AreEqual("OneDrive/", Find<Label>(dialog, "CloudTargetLocation").Text);
    });

    [TestMethod]
    public Task PrefetchDoesNotBlockSelectionAndIsCanceledWhenDialogCloses() => OnUiAsync(async host =>
    {
        var started = new TaskCompletionSource<CancellationToken>(TaskCreationOptions.RunContinuationsAsynchronously);
        var finished = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        async Task Prefetch(CloudFolderListing _, CancellationToken token, string? preferred)
        {
            started.TrySetResult(token);
            try { await Task.Delay(Timeout.Infinite, token); }
            finally { finished.TrySetResult(); }
        }
        using var dialog = new CloudTargetDialog((_, _, _) => Task.FromResult(new CloudFolderListing(
            Folder("root", "OneDrive", path: ""), [Folder("child", "Child", "root")])), Prefetch);
        dialog.Show(host); await dialog.Ready;
        var token = await started.Task;
        Assert.IsTrue(Find<Button>(dialog, "ChooseCloudTarget").Enabled,
            "Background prefetch must not hold the foreground loading state.");
        Assert.IsTrue(Find<ListBox>(dialog, "CloudTargetFolders").Enabled);
        dialog.Close();
        Assert.IsTrue(token.IsCancellationRequested);
        await finished.Task;
    });

    [TestMethod]
    [DataRow("zh-CN")]
    [DataRow("en-US")]
    public Task MinimumSizeKeepsAllActionsVisibleWithoutOverlap(string language) => OnUiAsync(async host =>
    {
        UiText.SetLanguage(language);
        using var dialog = new CloudTargetDialog((_, _, _) => Task.FromResult(new CloudFolderListing(
            Folder("root", "OneDrive", path: ""), [Folder("child", "Child", "root")])));
        dialog.Show(host); await dialog.Ready;
        dialog.Size = dialog.MinimumSize;
        dialog.PerformLayout();
        var cancel = dialog.Controls.Find("CloudTargetCancel", true).OfType<Button>().SingleOrDefault()
            ?? Find<Button>(dialog, "取消");
        var actions = new[] { Find<Button>(dialog, "CloudTargetUp"), Find<Button>(dialog, "CloudTargetRefresh"),
            Find<Button>(dialog, "ChooseCloudTarget"), cancel };
        var rectangles = actions.Select(action => dialog.RectangleToClient(action.RectangleToScreen(action.ClientRectangle))).ToArray();
        for (var i = 0; i < actions.Length; i++)
        {
            Assert.IsTrue(actions[i].Visible, actions[i].Name);
            Assert.IsTrue(dialog.ClientRectangle.Contains(rectangles[i]), actions[i].Name + " must fit inside the dialog.");
            for (var j = i + 1; j < actions.Length; j++)
                Assert.IsFalse(rectangles[i].IntersectsWith(rectangles[j]), actions[i].Name + " must not overlap " + actions[j].Name);
        }
        Assert.AreEqual(UiText.Text("刷新"), Find<Button>(dialog, "CloudTargetRefresh").Text);
        if (Environment.GetEnvironmentVariable("HARBORBACKUP_SCREENSHOT_DIR") is { Length: > 0 } screenshots)
        {
            Directory.CreateDirectory(screenshots);
            using var bitmap = new Bitmap(dialog.Width, dialog.Height);
            dialog.DrawToBitmap(bitmap, new Rectangle(Point.Empty, bitmap.Size));
            bitmap.Save(Path.Combine(screenshots, "cloud-target-minimum-" + language + ".png"));
        }
    });

    private static RemoteItem Folder(string id, string name, string? parent = null, string? path = null) =>
        new(id, name, "tag-" + id, 0, true, DisplayPath: path, ParentId: parent);

    private static T Find<T>(Control owner, string name) where T : Control => (T)owner.Controls.Find(name, true).Single();

    private static async Task OnUiAsync(Func<Form, Task> check)
    {
        var completed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            var language = UiText.Language;
            try
            {
                using var host = new Form();
                host.Shown += async (_, _) =>
                {
                    try { await check(host); completed.TrySetResult(); }
                    catch (Exception ex) { completed.TrySetException(ex); }
                    finally { host.Close(); }
                };
                Application.Run(host);
            }
            catch (Exception ex) { completed.TrySetException(ex); }
            finally { UiText.SetLanguage(language); }
        }) { IsBackground = true };
        thread.SetApartmentState(ApartmentState.STA); thread.Start();
        await completed.Task.WaitAsync(TimeSpan.FromSeconds(20));
        Assert.IsTrue(thread.Join(TimeSpan.FromSeconds(3)));
    }
}
