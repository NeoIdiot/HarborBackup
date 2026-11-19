using System.Drawing.Imaging;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using HarborBackup.Core.Application;
using HarborBackup.Core.Domain;
using HarborBackup.Desktop.Controllers;
using HarborBackup.Desktop.Localization;
using HarborBackup.Desktop.Presentation;
using HarborBackup.Desktop.Views;
using HarborBackup.Desktop.Views.Dialogs;
using HarborBackup.Windows.Integration;
using HarborBackup.Windows.Persistence;
using HarborBackup.Windows.Persistence.Configuration;

namespace HarborBackup.Windows.Tests;

// Pure presentation fixtures: no configuration reads, account session, process lock or cloud calls.
// Optional export is explicit, so normal regression runs never rewrite tracked media.
[TestClass]
[DoNotParallelize]
public sealed class PublicShowcaseTests
{
    private const string ProjectId = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
    private const string DesignId = "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb";
    private static readonly DateTimeOffset DemoTime = new(2026, 11, 19, 10, 30, 0,
        TimeZoneInfo.Local.GetUtcOffset(new DateTime(2026, 11, 19)));
    private sealed class DemoClock : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => DemoTime;
        public override long GetTimestamp() => 0;
    }

    [ClassInitialize]
    public static void Initialize(TestContext _)
    {
        Application.SetHighDpiMode(HighDpiMode.DpiUnaware);
        Application.EnableVisualStyles();
    }

    [TestMethod]
    [TestCategory("PublicShowcase")]
    [DataRow("zh-CN")]
    [DataRow("en-US")]
    public async Task SyntheticControlsHaveSafePathsAndExportReadableShowcase(string language)
    {
        var finished = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            try
            {
                UiText.SetLanguage(language);
                var frames = new List<Bitmap>();
                try
                {
                    var controller = DemoController();
                    using (var overview = new MainForm(controller))
                    {
                        // The shell normally preloads settings after Shown. This fixture intentionally
                        // suppresses that unrelated I/O and injects only in-memory presentation state.
                        Set(overview, "pagePreparation", Task.CompletedTask);
                        Show(overview);
                        Assert.HasCount(2, ((DataGridView)overview.Controls.Find("OverviewGrid", true).Single()).Rows);
                        frames.Add(Capture(overview, language, "overview"));
                        overview.ClosePermanently();
                    }
                    using (var preview = new PreviewDialog(Preview(), source: @"C:\Example\Design", target: "OneDrive/Design", allowUpload: true))
                    {
                        Show(preview);
                        var grid = (DataGridView)preview.Controls.Find("PullPreviewGrid", true).Single();
                        // Keep the normal resizable table readable in the public capture.
                        // No control or behavior is replaced; only column proportions change.
                        foreach (DataGridViewColumn column in grid.Columns)
                            column.FillWeight = column.DataPropertyName switch
                            { "DisplayProject" => 12, "Path" => 20, "DisplayAction" => 12, "DisplayVerification" => 16, "DisplayDetails" => 40, _ => column.FillWeight };
                        preview.PerformLayout(); Application.DoEvents();
                        Assert.HasCount(4, grid.Rows);
                        Assert.IsTrue(preview.Controls.Find("RunPlanSummary", true).Single().Text.Contains("SHA-256", StringComparison.Ordinal));
                        frames.Add(Capture(preview, language, "preview"));
                        preview.Close();
                    }
                    foreach (var scope in new[] { RestoreScope.Current, RestoreScope.Archive })
                    {
                        using var restore = new RestoreDialog(controller);
                        FillRestore(restore, scope);
                        Show(restore);
                        typeof(RestoreDialog).GetMethod("Busy", BindingFlags.NonPublic | BindingFlags.Instance)!.Invoke(restore, [false]);
                        var grid = (DataGridView)restore.Controls.Find("RestoreCandidates", true).Single();
                        Assert.HasCount(2, grid.Rows);
                        Assert.IsTrue(Get<TextBox>(restore, "details").Text.Contains(UiText.Text("正文校验通过"), StringComparison.Ordinal));
                        frames.Add(Capture(restore, language, scope == RestoreScope.Current ? "restore" : "restore-archive"));
                        restore.Close();
                    }
                    var gif = AnimatedGif(frames, delayHundredths: 350);
                    using (var stream = new MemoryStream(gif))
                    using (var decoded = Image.FromStream(stream))
                        Assert.AreEqual(4, decoded.GetFrameCount(FrameDimension.Time));
                    if (Environment.GetEnvironmentVariable("HARBORBACKUP_PUBLIC_MEDIA_DIR") is { Length: > 0 } output)
                        File.WriteAllBytes(Path.Combine(output, "showcase." + language + ".gif"), gif);
                    finished.TrySetResult();
                }
                finally { foreach (var frame in frames) frame.Dispose(); }
            }
            catch (Exception ex) { finished.TrySetException(ex); }
        }) { IsBackground = true };
        thread.SetApartmentState(ApartmentState.STA); thread.Start();
        await finished.Task.WaitAsync(TimeSpan.FromSeconds(45));
        Assert.IsTrue(thread.Join(TimeSpan.FromSeconds(5)));
    }

    private static DesktopController DemoController()
    {
        var account = new CloudAccountSettings(Enabled: true, ClientId: "11111111-1111-1111-1111-111111111111",
            AccountId: "demo-account", AccountName: "Demo", DriveId: "demo-drive", RootId: "demo-management",
            RepositoryId: "11111111111111111111111111111111");
        var definitions = new[]
        {
            new ProjectDefinition(@"C:\Example\Projects", "Projects", "OneDrive/Projects", [], [], ProjectId, ProjectId),
            new ProjectDefinition(@"C:\Example\Design", "Design", "OneDrive/Design", [], [], DesignId, DesignId)
        };
        var config = new ConfigurationSnapshot(definitions,
            new(@"C:\Example\HarborBackup\data\state", new string('d', 64), new()), 30, 2,
            @"C:\Example\HarborBackup\data\config") { CloudAccount = account };
        var controller = new DesktopController(new(@"C:\Example\HarborBackup\data\config\projects.json",
            @"C:\Example\HarborBackup\data\config\backup-settings.json"),
            (_, _) => throw new AssertFailedException("Showcase must never query a cloud account."));
        Set(controller, "config", config);
        typeof(DesktopController).GetProperty(nameof(DesktopController.CloudAccount))!.SetValue(controller, account);
        Set(controller, "status", "空闲"); Set(controller, "completed", DemoTime);
        Set(controller, "schedule", new BackupSchedule(TimeSpan.FromMinutes(30), TimeSpan.FromMinutes(30), new DemoClock()));
        ProjectRow[] projects =
        [
            new("Projects", definitions[0].Source, definitions[0].Target, "云端操作已完成", LocalStatus: "本地目录存在", ProjectId: ProjectId)
            { Health = new("demo-projects", DemoTime.AddMinutes(-15), DemoTime.AddMinutes(-5), RestoreScope: "Current", RestoreVerifiedFiles: 2) },
            new("Design", definitions[1].Source, definitions[1].Target, "有待处理问题", LocalStatus: "本地目录存在", ProjectId: DesignId)
            { Health = new("demo-design", DemoTime.AddHours(-28), DemoTime.AddHours(-2), 2, DemoTime.AddHours(-25), true, "Archive", 2) }
        ];
        Set(controller, "projects", projects);
        Set(controller, "presentedDetails", new DetailRow[]
        {
            new("Design", "docs/roadmap.md", "内容冲突", "BothChanged", true),
            new("Design", "drafts/notes.md", "延期处理", "PendingFileOperation", true)
        });
        return controller;
    }

    private static PreviewReport Preview()
    {
        PreviewPlan[] plans =
        [
            new("Design", new(FileAction.Directory, false, FileSide.None, FileSide.None, ProtectionKind.None, "PreserveDirectory"), true, true)
                { Verification = VerificationCategory.Directory },
            new("docs/notes.md", new(FileAction.Copy, false, FileSide.Target, FileSide.Target, ProtectionKind.Archive, "OneSideChangedSinceBaseline"), true, true)
                { Verification = VerificationCategory.Different, TransferBytes = 36864 },
            new("assets/cover.png", new(FileAction.Copy, false, FileSide.Target, FileSide.None, ProtectionKind.None, "SourceOnly"), true, false)
                { Verification = VerificationCategory.MissingCloud, TransferBytes = 524288 },
            new("docs/roadmap.md", new(FileAction.ConflictUnresolved, true, FileSide.None, FileSide.Both, ProtectionKind.Conflict, "BothChanged"), true, true)
                { Verification = VerificationCategory.Different }
        ];
        return new([new("Design", plans, [], new()) { ProjectId = DesignId }])
        {
            UploadSelection = new("Design", false,
                [new("docs/notes.md", "demo-notes-confirmation", false, 36864), new("assets/cover.png", "demo-cover-confirmation", true, 524288)]) { ProjectId = DesignId }
        };
    }

    private static void FillRestore(RestoreDialog dialog, RestoreScope scope)
    {
        ((ComboBox)dialog.Controls.Find("RestoreScope", true).Single()).SelectedIndex = scope == RestoreScope.Current ? 0 : 1;
        ((TextBox)dialog.Controls.Find("RestorePathFilter", true).Single()).Text = "docs";
        ((TextBox)dialog.Controls.Find("RestoreOutput", true).Single()).Text = @"C:\Example\Restore\Review";
        var candidates = new[]
        {
            new RestoreCandidate("demo-roadmap", ProjectId, "Projects", "demo-binding", "docs/roadmap.md", scope,
                "demo-item-one", "demo-etag-one", "demo-parent", "roadmap.md", 24576, new string('a', 64), "demo-checksum", DemoTime),
            new RestoreCandidate("demo-notes", ProjectId, "Projects", "demo-binding", "docs/notes.md", scope,
                "demo-item-two", "demo-etag-two", "demo-parent", "notes.md", 8192, new string('b', 64), "demo-checksum", DemoTime)
        };
        var rowType = typeof(RestoreDialog).GetNestedType("CandidateRow", BindingFlags.NonPublic)!;
        var rows = Array.CreateInstance(rowType, candidates.Length);
        for (var i = 0; i < candidates.Length; i++)
            rows.SetValue(Activator.CreateInstance(rowType, "Projects",
                UiText.Text(scope == RestoreScope.Current ? "云端当前文件" : "上一份归档"), candidates[i].Path, candidates[i].Length, "SHA-256", candidates[i]), i);
        var grid = (DataGridView)dialog.Controls.Find("RestoreCandidates", true).Single();
        grid.Columns[1].FillWeight = 19; grid.Columns[2].FillWeight = 36;
        grid.DataSource = rows; grid.SelectAll();
        typeof(RestoreDialog).GetMethod("Busy", BindingFlags.NonPublic | BindingFlags.Instance)!.Invoke(dialog, [false]);
        var folder = scope.ToString().ToLowerInvariant();
        var results = candidates.Select(c => new RestoreFileResult(c.Id, c.ProjectId, c.Path, c.Scope,
            RestoreFileStatus.Verified, c.Length, @"C:\Example\Restore\Review\" + folder + "\\" + ProjectId + "\\" + c.Path.Replace('/', '\\'))).ToArray();
        var report = new RestoreReport("Restore", DemoTime.AddSeconds(-4), DemoTime, @"C:\Example\Restore\Review", results, []);
        Get<TextBox>(dialog, "details").Text = RestoreReportPresentation.Details(report);
        Get<Label>(dialog, "feedback").Text = UiText.Text("已完成 {0} 个文件正文校验，共 {1} 字节。报告目录：{2}",
            report.VerifiedFiles, report.VerifiedBytes, report.OutputRoot);
    }

    private static void Show(Form form)
    {
        form.Size = new(1180, 740); form.StartPosition = FormStartPosition.Manual;
        form.Location = new(-20000, -20000); form.ShowInTaskbar = false;
        form.Show(); form.PerformLayout(); Application.DoEvents();
    }

    private static Bitmap Capture(Form form, string language, string name)
    {
        // Settings contains a hidden, read-only application-location field resolved from
        // the test host. Give even that hidden field an explicitly fictional install path.
        foreach (var path in Descendants(form).OfType<TextBox>().Where(t => t.ReadOnly &&
                     t.Text.Equals(ApplicationPaths.InstallRoot(), StringComparison.OrdinalIgnoreCase)))
            path.Text = @"C:\Example\HarborBackup\app";
        ValidatePublicText(form);
        using var raw = new Bitmap(form.Width, form.Height);
        form.DrawToBitmap(raw, new Rectangle(Point.Empty, raw.Size));
        var bitmap = new Bitmap(raw.Width, raw.Height + 44, PixelFormat.Format24bppRgb);
        using (var graphics = Graphics.FromImage(bitmap))
        using (var font = new Font("Microsoft YaHei UI", 10f))
        {
            graphics.Clear(Color.FromArgb(244, 247, 250)); graphics.DrawImageUnscaled(raw, Point.Empty);
            var caption = language == "zh-CN" ? "合成界面演示 · 所有项目、路径与结果均为示例 · 未连接真实账户或执行云端传输"
                : "Synthetic UI demo · Example projects, paths and results · No real account or cloud transfer";
            graphics.DrawString(caption, font, Brushes.DimGray, new RectangleF(18, raw.Height + 10, raw.Width - 36, 28));
        }
        if (Environment.GetEnvironmentVariable("HARBORBACKUP_PUBLIC_MEDIA_DIR") is { Length: > 0 } output)
        {
            Directory.CreateDirectory(output);
            bitmap.Save(Path.Combine(output, name + "." + language + ".png"), ImageFormat.Png);
        }
        return bitmap;
    }

    private static void ValidatePublicText(Control root)
    {
        foreach (var control in Descendants(root))
        {
            var values = new List<string> { control.Text };
            if (control is DataGridView grid)
                values.AddRange(grid.Rows.Cast<DataGridViewRow>().SelectMany(r => r.Cells.Cast<DataGridViewCell>()).Select(c => c.FormattedValue?.ToString() ?? ""));
            foreach (var value in values)
                foreach (Match path in Regex.Matches(value, @"[A-Za-z]:\\[^\r\n]*"))
                    Assert.IsTrue(path.Value.StartsWith(@"C:\Example\", StringComparison.Ordinal), "Only explicitly fictional paths may be shown: " + path.Value);
        }
    }

    private static IEnumerable<Control> Descendants(Control root)
    {
        yield return root;
        foreach (Control child in root.Controls)
            foreach (var item in Descendants(child)) yield return item;
    }
    private static T Get<T>(object owner, string field) => (T)owner.GetType().GetField(field, BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(owner)!;
    private static void Set(object owner, string field, object value) => owner.GetType().GetField(field, BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(owner, value);

    // Reuse System.Drawing's per-frame GIF encoder; only the standard animation container
    // and local palettes are assembled here. No screen recording or external package is used.
    private static byte[] AnimatedGif(IReadOnlyList<Bitmap> frames, ushort delayHundredths)
    {
        using var output = new MemoryStream(); using var writer = new BinaryWriter(output, Encoding.ASCII, true);
        var width = frames.Max(f => f.Width); var height = frames.Max(f => f.Height);
        writer.Write(Encoding.ASCII.GetBytes("GIF89a")); writer.Write((ushort)width); writer.Write((ushort)height);
        writer.Write(new byte[] { 0x70, 0, 0 });
        writer.Write(new byte[] { 0x21, 0xFF, 11 }); writer.Write(Encoding.ASCII.GetBytes("NETSCAPE2.0")); writer.Write(new byte[] { 3, 1, 0, 0, 0 });
        foreach (var frame in frames)
        {
            // A full regression process may already have a different DPI mode. Preserve
            // the captured pixels, padding the animation canvas instead of scaling controls.
            using var canvas = new Bitmap(width, height, PixelFormat.Format24bppRgb);
            using (var graphics = Graphics.FromImage(canvas))
            { graphics.Clear(Color.FromArgb(244, 247, 250)); graphics.DrawImageUnscaled(frame, (width - frame.Width) / 2, 0); }
            using var indexed = IndexedFrame(canvas);
            using var encoded = new MemoryStream(); indexed.Save(encoded, ImageFormat.Gif); var bytes = encoded.ToArray();
            var globalBits = bytes[10] & 7; var globalLength = (bytes[10] & 128) != 0 ? 3 * (1 << (globalBits + 1)) : 0;
            var position = 13 + globalLength;
            while (bytes[position] == 0x21)
            {
                position += 2;
                while (bytes[position] != 0) position += 1 + bytes[position];
                position++;
            }
            Assert.AreEqual((byte)0x2C, bytes[position]);
            var descriptor = bytes.AsSpan(position + 1, 9).ToArray(); position += 10;
            var localBits = (descriptor[8] & 128) != 0 ? descriptor[8] & 7 : globalBits;
            var palette = (descriptor[8] & 128) != 0 ? bytes.AsSpan(position, 3 * (1 << (localBits + 1))).ToArray()
                : bytes.AsSpan(13, globalLength).ToArray();
            if ((descriptor[8] & 128) != 0) position += palette.Length;
            descriptor[8] = (byte)(128 | (descriptor[8] & 64) | localBits);
            writer.Write(new byte[] { 0x21, 0xF9, 4, 8 }); writer.Write(delayHundredths); writer.Write(new byte[] { 0, 0 });
            writer.Write((byte)0x2C); writer.Write(descriptor); writer.Write(palette);
            var start = position++; // LZW minimum code size followed by data sub-blocks.
            while (bytes[position] != 0) position += 1 + bytes[position];
            position++;
            writer.Write(bytes, start, position - start);
        }
        writer.Write((byte)0x3B); writer.Flush(); return output.ToArray();
    }

    private static Bitmap IndexedFrame(Bitmap frame)
    {
        // GDI+'s default RGB-to-GIF halftone palette adds colored noise to the UI's
        // nearly-white surfaces. Preserve common exact colors and map remaining edge
        // colors to their nearest entry without dithering. Text remains a real capture.
        var sourceBits = frame.LockBits(new Rectangle(Point.Empty, frame.Size), ImageLockMode.ReadOnly, PixelFormat.Format24bppRgb);
        var source = new byte[sourceBits.Stride * frame.Height]; var sourceStride = sourceBits.Stride;
        try { Marshal.Copy(sourceBits.Scan0, source, 0, source.Length); }
        finally { frame.UnlockBits(sourceBits); }
        var counts = new Dictionary<int, int>();
        for (var y = 0; y < frame.Height; y++)
            for (var x = 0; x < frame.Width; x++)
            {
                var offset = y * sourceStride + x * 3;
                var color = source[offset + 2] << 16 | source[offset + 1] << 8 | source[offset];
                counts[color] = counts.GetValueOrDefault(color) + 1;
            }
        var colors = counts.OrderByDescending(c => c.Value).ThenBy(c => c.Key).Take(256).Select(c => c.Key).ToArray();
        var paletteIndices = colors.Select((color, index) => (color, index)).ToDictionary(c => c.color, c => (byte)c.index);
        var indexed = new Bitmap(frame.Width, frame.Height, PixelFormat.Format8bppIndexed);
        var palette = indexed.Palette;
        for (var i = 0; i < palette.Entries.Length; i++)
        {
            var color = colors[Math.Min(i, colors.Length - 1)];
            palette.Entries[i] = Color.FromArgb(color >> 16, color >> 8 & 255, color & 255);
        }
        indexed.Palette = palette;
        var destinationBits = indexed.LockBits(new Rectangle(Point.Empty, indexed.Size), ImageLockMode.WriteOnly, PixelFormat.Format8bppIndexed);
        try
        {
            var destination = new byte[destinationBits.Stride * indexed.Height];
            for (var y = 0; y < frame.Height; y++)
                for (var x = 0; x < frame.Width; x++)
                {
                    var offset = y * sourceStride + x * 3;
                    var color = source[offset + 2] << 16 | source[offset + 1] << 8 | source[offset];
                    if (!paletteIndices.TryGetValue(color, out var index))
                    {
                        var nearest = 0; var distance = int.MaxValue;
                        for (var i = 0; i < colors.Length; i++)
                        {
                            var red = (color >> 16) - (colors[i] >> 16);
                            var green = (color >> 8 & 255) - (colors[i] >> 8 & 255);
                            var blue = (color & 255) - (colors[i] & 255);
                            var candidateDistance = red * red + green * green + blue * blue;
                            if (candidateDistance < distance) { distance = candidateDistance; nearest = i; }
                        }
                        index = (byte)nearest; paletteIndices[color] = index;
                    }
                    destination[y * destinationBits.Stride + x] = index;
                }
            Marshal.Copy(destination, 0, destinationBits.Scan0, destination.Length);
        }
        finally { indexed.UnlockBits(destinationBits); }
        return indexed;
    }
}
