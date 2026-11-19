using System.Drawing.Drawing2D;
using HarborBackup.Desktop.Localization;
using HarborBackup.Desktop.Presentation;
using HarborBackup.Desktop.Styling;
using HarborBackup.OneDrive.Transport;
using HarborBackup.Windows.Persistence.Configuration;

namespace HarborBackup.Windows.Tests;

[TestClass]
[DoNotParallelize]
public sealed class TrayIconTests
{
    private static DesktopState State() => new("空闲", false, false, false, null, null, null, null, [], [], [], null);
    private static readonly CloudAccountSettings Connected = new(Enabled: true);

    [TestMethod]
    public void BadgePrioritySeparatesFailuresActivityAttentionPauseAndConnection()
    {
        UiText.SetLanguage("zh-CN");
        var basis = State();
        var pending = new DetailRow("Demo", "a.txt", "延期处理", "PendingFileOperation", true);
        var cases = new (DesktopState State, CloudAccountSettings Account, TrayBadge Expected)[]
        {
            (basis, Connected, TrayBadge.None),
            (basis with { Paused = true }, Connected, TrayBadge.Paused),
            (basis with { Busy = true, OperationBusy = true, Paused = true }, Connected, TrayBadge.Running),
            (basis with { Busy = true }, Connected, TrayBadge.None),
            (basis with { Busy = true, Error = "broken configuration" }, Connected, TrayBadge.Failed),
            (basis with { Status = "请检查配置" }, new(), TrayBadge.Failed),
            (basis with { Status = "失败" }, Connected, TrayBadge.Failed),
            (basis with { Status = "运行失败" }, Connected, TrayBadge.Failed),
            (basis with { Details = [pending], Paused = true }, Connected, TrayBadge.Attention),
            (basis with { Busy = true, OperationBusy = true, Details = [pending] }, Connected, TrayBadge.Running),
            (basis with { Busy = true, OperationBusy = true, Projects = [new("Demo", "", "", "失败")] }, Connected, TrayBadge.Running),
            (basis with { Projects = [new("Demo", "", "", "失败")] }, Connected, TrayBadge.Failed),
            (basis with { Projects = [new("Demo", "", "", "有待处理问题")] }, Connected, TrayBadge.Attention),
            (basis with { Status = "等待网络", NetworkOffline = true, Details = [pending] }, Connected, TrayBadge.Waiting),
            (basis with { Details = [pending with { Details = CloudNetworkAvailability.Unreachable }] }, Connected, TrayBadge.Attention),
            (basis with { NeedsSetup = true, Error = "needs setup" }, new(), TrayBadge.Waiting),
            (basis, Connected with { RequiresAuthorization = true }, TrayBadge.Waiting),
            (basis with { Paused = true }, Connected with { RequiresAuthorization = true }, TrayBadge.Waiting),
            (basis, new(), TrayBadge.Waiting)
        };
        foreach (var item in cases) Assert.AreEqual(item.Expected, TrayPresentation.Create(item.State, item.Account).Badge);
    }

    [TestMethod]
    [DataRow("zh-CN")]
    [DataRow("en-US")]
    public void TooltipsCountFilesOnceAndKeepPauseVisibleWithinShellLimit(string language)
    {
        UiText.SetLanguage(language);
        var file = new DetailRow("Demo", "a.txt", "延期处理", "PendingFileOperation", true);
        var basis = State() with { Details = [file, file with { Path = "A.TXT" }, new("Demo", "", "PreviousMappingPending", "pending", true)] };
        var warning = TrayPresentation.Create(basis, Connected);
        Assert.AreEqual("HarborBackup · " + UiText.Text("托盘单个文件待处理"), warning.ToolTip);
        var busy = TrayPresentation.Create(basis with { Busy = true, OperationBusy = true, CanCancel = true, Status = "正在上传", Paused = true }, Connected);
        Assert.AreEqual(TrayBadge.Running, busy.Badge);
        Assert.Contains(UiText.Text("托盘自动备份已暂停"), busy.ToolTip);
        Assert.Contains("1", busy.ToolTip);
        Assert.IsLessThanOrEqualTo(63, busy.ToolTip.Length);
        Assert.DoesNotContain("a.txt", busy.ToolTip);
    }

    [TestMethod]
    [DataRow(16)]
    [DataRow(20)]
    [DataRow(24)]
    [DataRow(28)]
    [DataRow(32)]
    [DataRow(48)]
    public void EveryBadgeKeepsBrandPixelsOutsideBottomRightAndCachesNativeIcons(int size)
    {
        using var icons = new TrayIcons();
        using var brand = TrayIcons.Render(TrayBadge.None, size, false);
        using var sheet = new Bitmap(660, 300);
        using var graphics = Graphics.FromImage(sheet);
        using var font = new Font("Segoe UI", 9);
        for (var row = 0; row < 3; row++)
        {
            var background = row == 1 ? Color.FromArgb(30, 30, 30) : Color.White;
            using var backdrop = new SolidBrush(background);
            using var label = new SolidBrush(row == 1 ? Color.White : Color.Black);
            graphics.FillRectangle(backdrop, 0, row * 100, sheet.Width, 100);
            foreach (var badge in Enum.GetValues<TrayBadge>())
            {
                var icon = icons.Get(badge, size, row == 2);
                Assert.AreSame(icon, icons.Get(badge, size, row == 2));
                Assert.AreEqual(new Size(size, size), icon.Size);
                using var bitmap = icon.ToBitmap();
                using var rendered = TrayIcons.Render(badge, size, row == 2);
                var differences = 0;
                for (var y = 0; y < size; y++)
                for (var x = 0; x < size; x++)
                {
                    if (brand.GetPixel(x, y) == rendered.GetPixel(x, y)) continue;
                    differences++;
                    var untouched = badge == TrayBadge.Running ? .34 : .45;
                    Assert.IsGreaterThanOrEqualTo(size * untouched, x, "The badge must stay in the bottom-right corner.");
                    Assert.IsGreaterThanOrEqualTo(size * untouched, y);
                }
                if (badge != TrayBadge.None) Assert.IsGreaterThan(0, differences);
                if (badge == TrayBadge.Running)
                {
                    Assert.AreEqual(0, (int)rendered.GetPixel(size - 1, size - 1).A,
                        "The original blue corner must not peek out beyond the circular badge.");
                    Assert.AreEqual(0, (int)bitmap.GetPixel(size - 1, size - 1).A,
                        "Converting to a native icon must preserve the transparent corner, without a white square.");
                    if (row != 2)
                    {
                        var bluePixels = 0;
                        var whiteBadgePixels = 0;
                        var diameter = (float)Math.Round(size * .56);
                        var centre = size - diameter / 2;
                        var outerRadius = diameter / 2 + .75f;
                        for (var y = 0; y < size; y++)
                        for (var x = 0; x < size; x++)
                        {
                            var pixel = rendered.GetPixel(x, y);
                            if (pixel.A > 200 && pixel.R < 40 && pixel.G is > 80 and < 160 && pixel.B > 180) bluePixels++;
                            static bool White(Color color) => color.A > 200 && color.R > 230 && color.G > 230 && color.B > 230;
                            foreach (var sample in new[] { pixel, bitmap.GetPixel(x, y) })
                            {
                                if (!White(sample) || White(brand.GetPixel(x, y))) continue;
                                Assert.IsLessThanOrEqualTo(outerRadius * outerRadius,
                                    (x + .5f - centre) * (x + .5f - centre) + (y + .5f - centre) * (y + .5f - centre),
                                    "New white pixels must stay inside the circular badge, in both bitmap and native icon.");
                            }
                            if (x >= size / 2 && y >= size / 2 && White(pixel)) whiteBadgePixels++;
                        }
                        Assert.IsGreaterThanOrEqualTo(8, bluePixels, "The running badge needs a visible OneDrive-blue circular background at native shell resolution.");
                        Assert.IsGreaterThanOrEqualTo(4, whiteBadgePixels, "The running badge needs visible white arrows.");
                    }
                    // Exercise the native icon drawing route, rather than only ToBitmap:
                    // a broken alpha mask can otherwise hide a rectangular white backing.
                    using var nativeSurface = new Bitmap(size, size);
                    using (var nativeGraphics = Graphics.FromImage(nativeSurface))
                    {
                        nativeGraphics.Clear(background);
                        nativeGraphics.DrawIcon(icon, new Rectangle(0, 0, size, size));
                    }
                    var badgeRadius = Math.Round(size * .56) / 2;
                    var centrePixel = size - badgeRadius;
                    for (var y = (int)Math.Ceiling(centrePixel); y < size; y++)
                    for (var x = (int)Math.Ceiling(centrePixel); x < size; x++)
                        if (Math.Pow(x + .5 - centrePixel, 2) + Math.Pow(y + .5 - centrePixel, 2) > Math.Pow(badgeRadius + 1, 2))
                            Assert.AreEqual(background.ToArgb(), nativeSurface.GetPixel(x, y).ToArgb(),
                                $"Native tray drawing must leave the outside of the circle transparent: {size}px, row {row}, ({x},{y}), raw={rendered.GetPixel(x,y)}.");
                }
                var left = (int)badge * 110;
                graphics.DrawIcon(icon, new Rectangle(left + 4, row * 100 + 24, size, size));
                graphics.InterpolationMode = InterpolationMode.NearestNeighbor;
                graphics.PixelOffsetMode = PixelOffsetMode.Half;
                graphics.DrawImage(bitmap, new Rectangle(left + 50, row * 100 + 12, 56, 56));
                graphics.DrawString(badge.ToString(), font, label, left + 4, row * 100 + 78);
            }
        }
        if (Environment.GetEnvironmentVariable("HARBORBACKUP_SCREENSHOT_DIR") is { } folder)
        {
            Directory.CreateDirectory(folder);
            sheet.Save(Path.Combine(folder, $"tray-badges-{size}.png"));
        }
    }
}
