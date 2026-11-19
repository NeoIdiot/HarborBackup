using System.Drawing.Drawing2D;

namespace HarborBackup.Desktop.Styling;

internal sealed class AppMenu : ContextMenuStrip
{
    public AppMenu(bool showChecks = false)
    {
        ShowImageMargin = false;
        ShowCheckMargin = showChecks;
        DropShadowEnabled = false;
        BackColor = AppStyle.Surface;
        ForeColor = AppStyle.Ink;
        Font = new Font("Microsoft YaHei UI", 10);
        Padding = new Padding(5);
        Renderer = SystemInformation.HighContrast ? new ToolStripSystemRenderer() : new MenuRenderer();
    }

    protected override void OnItemAdded(ToolStripItemEventArgs e)
    {
        base.OnItemAdded(e);
        if (e.Item is ToolStripMenuItem) e.Item.Padding = new Padding(6, 3, 10, 3);
    }

    protected override void OnOpening(System.ComponentModel.CancelEventArgs e)
    {
        var scale = DeviceDpi / 96f;
        Padding = new Padding((int)Math.Round(5 * scale));
        foreach (ToolStripItem item in Items)
            if (item is ToolStripMenuItem)
                item.Padding = new Padding((int)(6 * scale), (int)(3 * scale), (int)(10 * scale), (int)(3 * scale));
        base.OnOpening(e);
    }

    protected override void OnSizeChanged(EventArgs e)
    {
        base.OnSizeChanged(e);
        if (Width < 2 || Height < 2) return;
        var previous = Region;
        if (SystemInformation.HighContrast) Region = null;
        else
        {
            using var outline = SurfacePanel.Rounded(new RectangleF(0, 0, Width, Height), 8 * DeviceDpi / 96f);
            Region = new Region(outline);
        }
        previous?.Dispose();
    }

    private sealed class MenuRenderer : ToolStripProfessionalRenderer
    {
        protected override void OnRenderToolStripBackground(ToolStripRenderEventArgs e) => e.Graphics.Clear(AppStyle.Surface);
        // Keep the check mark gutter for pause state, without the default gray image stripe.
        protected override void OnRenderImageMargin(ToolStripRenderEventArgs e) { }
        protected override void OnRenderToolStripBorder(ToolStripRenderEventArgs e)
        {
            e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
            using var outline = SurfacePanel.Rounded(new RectangleF(.5f, .5f, e.ToolStrip.Width - 1, e.ToolStrip.Height - 1), 8 * e.ToolStrip.DeviceDpi / 96f);
            using var pen = new Pen(AppStyle.Border);
            e.Graphics.DrawPath(pen, outline);
        }
        protected override void OnRenderMenuItemBackground(ToolStripItemRenderEventArgs e)
        {
            if (!e.Item.Selected || !e.Item.Enabled) return;
            e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
            using var outline = SurfacePanel.Rounded(new RectangleF(1, 1, e.Item.Width - 2, e.Item.Height - 2), 4 * (e.ToolStrip?.DeviceDpi ?? 96) / 96f);
            using var brush = new SolidBrush(AppStyle.Hover);
            e.Graphics.FillPath(brush, outline);
        }
        protected override void OnRenderItemText(ToolStripItemTextRenderEventArgs e)
        {
            e.TextColor = e.Item.Enabled ? AppStyle.Ink : AppStyle.Muted;
            base.OnRenderItemText(e);
        }
        protected override void OnRenderItemCheck(ToolStripItemImageRenderEventArgs e)
        {
            var r = e.ImageRectangle;
            e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
            using var pen = new Pen(AppStyle.Accent, Math.Max(1.5f, (e.ToolStrip?.DeviceDpi ?? 96) / 96f));
            e.Graphics.DrawLines(pen, [new PointF(r.Left + r.Width * .18f, r.Top + r.Height * .5f),
                new PointF(r.Left + r.Width * .4f, r.Top + r.Height * .72f), new PointF(r.Left + r.Width * .85f, r.Top + r.Height * .25f)]);
        }
        protected override void OnRenderSeparator(ToolStripSeparatorRenderEventArgs e)
        {
            using var pen = new Pen(AppStyle.Border);
            e.Graphics.DrawLine(pen, 6, e.Item.Height / 2, e.Item.Width - 6, e.Item.Height / 2);
        }
    }
}
