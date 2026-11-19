using System.Drawing;
using HarborBackup.Desktop.Styling;

namespace HarborBackup.Windows.Tests;

[TestClass]
[DoNotParallelize]
public sealed class NavigationGlyphTests
{
    [TestMethod]
    [DataRow(96)]
    [DataRow(120)]
    [DataRow(144)]
    [DataRow(192)]
    [DataRow(240)]
    public void EveryNavigationIconMatchesUnclippedReference(int dpi)
    {
        var scale = dpi / 96f;
        var height = (int)Math.Round(42 * scale);
        var width = (int)Math.Round(184 * scale);
        var glyphs = new[] { "", "", "", "", "", "" };
        using var sheet = new Bitmap(width, height * glyphs.Length);
        sheet.SetResolution(dpi, dpi);
        using var sheetGraphics = Graphics.FromImage(sheet);
        sheetGraphics.Clear(Color.White);
        for (var index = 0; index < glyphs.Length; index++)
        {
            using var actual = new Bitmap(width, height);
            using var reference = new Bitmap(width, height);
            actual.SetResolution(dpi, dpi); reference.SetResolution(dpi, dpi);
            using var graphics = Graphics.FromImage(actual);
            using var referenceGraphics = Graphics.FromImage(reference);
            graphics.Clear(Color.White); referenceGraphics.Clear(Color.White);
            var text = SurfaceButton.PaintGlyph(graphics, actual.Size, glyphs[index], Color.Black, dpi);
            // A whole button width, without clipping/padding, is the independent reference.
            using var font = new Font("Segoe MDL2 Assets", 18 * scale, FontStyle.Regular, GraphicsUnit.Pixel);
            TextRenderer.DrawText(referenceGraphics, glyphs[index], font, new Rectangle(Point.Empty, reference.Size),
                Color.Black, TextFormatFlags.NoPadding | TextFormatFlags.NoPrefix | TextFormatFlags.SingleLine |
                TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
            var observed = Ink(actual);
            var expected = Ink(reference);
            Assert.IsGreaterThan(0, expected.Count);
            Assert.AreEqual(expected.Count, observed.Count, $"Icon {index} at {dpi} DPI lost painted pixels.");
            Assert.AreEqual(expected.Bounds.Size, observed.Bounds.Size, $"Icon {index} was clipped.");
            Assert.IsGreaterThanOrEqualTo((int)(8 * scale), observed.Bounds.Left);
            Assert.IsLessThanOrEqualTo(text.Left - (int)(8 * scale), observed.Bounds.Right);
            Assert.IsGreaterThan(0, observed.Bounds.Top);
            Assert.IsLessThan(height, observed.Bounds.Bottom);
            sheetGraphics.DrawImageUnscaled(actual, 0, index * height);
        }
        if (Environment.GetEnvironmentVariable("HARBORBACKUP_SCREENSHOT_DIR") is { } folder)
        {
            Directory.CreateDirectory(folder);
            sheet.Save(Path.Combine(folder, $"navigation-icons-{dpi}dpi.png"));
        }
    }

    private static (Rectangle Bounds, int Count) Ink(Bitmap image)
    {
        var left = image.Width; var top = image.Height; var right = -1; var bottom = -1; var count = 0;
        for (var y = 0; y < image.Height; y++)
        for (var x = 0; x < image.Width; x++)
        {
            var pixel = image.GetPixel(x, y);
            if (pixel.R == 255 && pixel.G == 255 && pixel.B == 255) continue;
            count++; left = Math.Min(left, x); top = Math.Min(top, y);
            right = Math.Max(right, x); bottom = Math.Max(bottom, y);
        }
        return (count == 0 ? Rectangle.Empty : Rectangle.FromLTRB(left, top, right + 1, bottom + 1), count);
    }
}
