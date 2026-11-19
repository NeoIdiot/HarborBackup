using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using HarborBackup.Desktop.Presentation;

namespace HarborBackup.Desktop.Styling;

internal sealed class TrayIcons : IDisposable
{
    private readonly Dictionary<(TrayBadge Badge, int Size, bool Contrast), Icon> cache = [];
    internal Icon Get(TrayBadge badge, int size, bool highContrast)
    {
        size = Math.Clamp(size, 16, 64);
        var key = (badge, size, highContrast);
        if (cache.TryGetValue(key, out var icon)) return icon;
        using var bitmap = Render(badge, size, highContrast);
        var handle = bitmap.GetHicon();
        try
        {
            using var borrowed = Icon.FromHandle(handle);
            icon = (Icon)borrowed.Clone();
            cache.Add(key, icon);
            return icon;
        }
        finally { DestroyIcon(handle); }
    }

    internal static Bitmap Render(TrayBadge badge, int size, bool highContrast)
    {
        var bitmap = new Bitmap(size, size, PixelFormat.Format32bppArgb);
        using var g = Graphics.FromImage(bitmap);
        using var stream = typeof(AppStyle).Assembly.GetManifestResourceStream("HarborBackup.Desktop.Assets.HarborBackup.ico")!;
        using var brand = new Icon(stream, size, size);
        g.DrawIcon(brand, new Rectangle(0, 0, size, size));
        if (badge == TrayBadge.None) return bitmap;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        var diameter = (float)Math.Round(size * (badge == TrayBadge.Running ? .56 : .46));
        var border = Math.Max(1f, size * .04f);
        var margin = badge == TrayBadge.Running ? 0 : border / 2 + .35f;
        var box = new RectangleF(size - diameter - margin, size - diameter - margin, diameter, diameter);
        if (badge == TrayBadge.Running)
        {
            // Remove the old brand corner behind and beyond the circular badge. Otherwise
            // its blue pixels peek out below the badge on the native 16px shell icon.
            var cutout = RectangleF.Inflate(box, border / 2, border / 2);
            using var corner = new GraphicsPath();
            corner.AddArc(cutout, 180, 90);
            corner.AddLine(cutout.Left + cutout.Width / 2, cutout.Top, size, cutout.Top);
            corner.AddLine(size, cutout.Top, size, size);
            corner.AddLine(size, size, cutout.Left, size);
            corner.CloseFigure();
            g.CompositingMode = CompositingMode.SourceCopy;
            // Erase the old corner completely before antialiasing the new circle.
            g.SmoothingMode = SmoothingMode.None;
            using var transparent = new SolidBrush(Color.FromArgb(0, 0, 0, 0));
            g.FillPath(transparent, corner);
            g.CompositingMode = CompositingMode.SourceOver;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            DrawRunningBadge(g, box, highContrast);
            return bitmap;
        }
        var fill = highContrast ? SystemColors.Window : badge switch
        {
            TrayBadge.Attention => Color.FromArgb(244, 183, 49),
            TrayBadge.Failed => Color.FromArgb(211, 47, 47),
            _ => Color.FromArgb(94, 104, 117)
        };
        var ink = highContrast ? SystemColors.WindowText : badge switch
        {
            TrayBadge.Attention => Color.FromArgb(55, 37, 0),
            _ => Color.White
        };
        using var background = new SolidBrush(fill);
        using var outline = new Pen(highContrast ? SystemColors.WindowText : Color.White, border);
        g.FillEllipse(background, box); g.DrawEllipse(outline, box);
        var cx = box.Left + diameter / 2; var cy = box.Top + diameter / 2;
        var stroke = Math.Max(1f, size * .045f);
        using var pen = new Pen(ink, stroke) { StartCap = LineCap.Round, EndCap = LineCap.Round };
        using var brush = new SolidBrush(ink);
        var r = diameter * .22f;
        switch (badge)
        {
            case TrayBadge.Failed:
                g.DrawLine(pen, cx - r, cy - r, cx + r, cy + r);
                g.DrawLine(pen, cx + r, cy - r, cx - r, cy + r);
                break;
            case TrayBadge.Attention:
                g.DrawLine(pen, cx, cy - diameter * .26f, cx, cy + diameter * .025f);
                g.FillEllipse(brush, cx - stroke / 2, cy + diameter * .20f, stroke, stroke);
                break;
            case TrayBadge.Paused:
                g.DrawLine(pen, cx - diameter * .14f, cy - r, cx - diameter * .14f, cy + r);
                g.DrawLine(pen, cx + diameter * .14f, cy - r, cx + diameter * .14f, cy + r);
                break;
            case TrayBadge.Waiting:
                g.DrawLine(pen, cx - r, cy, cx + r, cy);
                break;
        }
        return bitmap;
    }

    private static void DrawRunningBadge(Graphics graphics, RectangleF box, bool highContrast)
    {
        // Redraw the circular OneDrive sync silhouette in a 9x9 coordinate system.
        // No rectangular backing or white separator; the glyph is clipped to the circle.
        using var circle = new GraphicsPath(); circle.AddEllipse(box);
        using var fill = new SolidBrush(highContrast ? SystemColors.Window : Color.FromArgb(0, 120, 212));
        var state = graphics.Save();
        try
        {
            // Match SVG pixel centres, so the circle ends inside the icon canvas
            // instead of leaving antialiased pixels at its square bottom/right corner.
            graphics.PixelOffsetMode = PixelOffsetMode.Half;
            graphics.FillPath(fill, circle);
            graphics.SetClip(circle, CombineMode.Intersect);
            graphics.TranslateTransform(box.Left, box.Top);
            graphics.ScaleTransform(box.Width / 9, box.Height / 9);
            using var arrows = new GraphicsPath();
            var point = PointF.Empty;
            void Move(float x, float y) { arrows.StartFigure(); point = new(x, y); }
            void Line(float x, float y) { arrows.AddLine(point, new PointF(x, y)); point = new(x, y); }
            void Curve(float x1, float y1, float x2, float y2, float x, float y)
            { arrows.AddBezier(point, new(x1, y1), new(x2, y2), new(x, y)); point = new(x, y); }
            Move(6f, 2f);
            Curve(6f, 1.72386f, 6.2239f, 1.5f, 6.5f, 1.5f);
            Curve(6.7761f, 1.5f, 7f, 1.72386f, 7f, 2f);
            Line(7f, 3.5f);
            Curve(7f, 3.7761f, 6.7761f, 4f, 6.5f, 4f);
            Line(5f, 4f);
            Curve(4.7239f, 4f, 4.5f, 3.7761f, 4.5f, 3.5f);
            Curve(4.5f, 3.2239f, 4.7239f, 3f, 5f, 3f);
            Line(5.4682f, 3f);
            Curve(5.4179f, 2.97215f, 5.3663f, 2.94644f, 5.3135f, 2.92296f);
            Curve(5.0682f, 2.81372f, 4.8034f, 2.755f, 4.535f, 2.75031f);
            Curve(4.2665f, 2.74561f, 3.9998f, 2.79503f, 3.7508f, 2.89562f);
            Curve(3.5018f, 2.99621f, 3.2756f, 3.1459f, 3.0857f, 3.3358f);
            Curve(2.89048f, 3.531f, 2.5739f, 3.531f, 2.37864f, 3.3358f);
            Curve(2.18338f, 3.1405f, 2.18338f, 2.82394f, 2.37864f, 2.62868f);
            Curve(2.66346f, 2.34386f, 3.0027f, 2.11931f, 3.3762f, 1.96842f);
            Curve(3.7497f, 1.81754f, 4.1497f, 1.74341f, 4.5524f, 1.75046f);
            Curve(4.9552f, 1.75751f, 5.3524f, 1.84558f, 5.7203f, 2.00944f);
            Curve(5.8162f, 2.05211f, 5.9095f, 2.0997f, 6f, 2.15195f);
            Line(6f, 2f);
            arrows.CloseFigure();
            Move(4.4476f, 7.2495f);
            Curve(4.8503f, 7.2566f, 5.2503f, 7.1825f, 5.6238f, 7.0316f);
            Curve(5.9973f, 6.8807f, 6.3365f, 6.6561f, 6.6214f, 6.3713f);
            Curve(6.8166f, 6.1761f, 6.8166f, 5.8595f, 6.6214f, 5.6642f);
            Curve(6.4261f, 5.469f, 6.1095f, 5.469f, 5.9143f, 5.6642f);
            Curve(5.7244f, 5.8541f, 5.4982f, 6.0038f, 5.2492f, 6.1044f);
            Curve(5.0002f, 6.205f, 4.7335f, 6.2544f, 4.465f, 6.2497f);
            Curve(4.1966f, 6.245f, 3.9318f, 6.1863f, 3.6865f, 6.077f);
            Curve(3.6337f, 6.0536f, 3.5821f, 6.0278f, 3.5318f, 6f);
            Line(4f, 6f);
            Curve(4.2761f, 6f, 4.5f, 5.7761f, 4.5f, 5.5f);
            Curve(4.5f, 5.2239f, 4.2761f, 5f, 4f, 5f);
            Line(2.5f, 5f);
            Curve(2.22386f, 5f, 2f, 5.2239f, 2f, 5.5f);
            Line(2f, 7f);
            Curve(2f, 7.2761f, 2.22386f, 7.5f, 2.5f, 7.5f);
            Curve(2.77614f, 7.5f, 3f, 7.2761f, 3f, 7f);
            Line(3f, 6.8481f);
            Curve(3.0905f, 6.9003f, 3.1838f, 6.9479f, 3.2797f, 6.9906f);
            Curve(3.6476f, 7.1544f, 4.0448f, 7.2425f, 4.4476f, 7.2495f);
            arrows.CloseFigure();
            using var ink = new SolidBrush(highContrast ? SystemColors.WindowText : Color.White);
            graphics.FillPath(ink, arrows);
        }
        finally { graphics.Restore(state); }
    }

    internal static int TaskbarIconSize()
    {
        var taskbar = FindWindow("Shell_TrayWnd", null);
        var dpi = taskbar == 0 ? 96u : GetDpiForWindow(taskbar);
        return Math.Clamp(GetSystemMetricsForDpi(49, dpi == 0 ? 96u : dpi), 16, 64); // SM_CXSMICON
    }
    internal void Clear() { foreach (var icon in cache.Values) icon.Dispose(); cache.Clear(); }
    public void Dispose() => Clear();
    [DllImport("user32.dll")] private static extern bool DestroyIcon(nint icon);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern nint FindWindow(string name, string? title);
    [DllImport("user32.dll")] private static extern uint GetDpiForWindow(nint window);
    [DllImport("user32.dll")] private static extern int GetSystemMetricsForDpi(int index, uint dpi);
}
