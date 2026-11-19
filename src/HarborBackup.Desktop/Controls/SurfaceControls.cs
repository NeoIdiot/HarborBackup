using System.Drawing.Drawing2D;
using System.ComponentModel;

namespace HarborBackup.Desktop.Styling;

// Native controls retain keyboard, focus and accessibility behavior. Only their surface is drawn here.
internal sealed class SurfaceButton : Button
{
    private bool hovered;
    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    internal bool Primary { get; init; }
    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    internal bool Navigation { get; init; }
    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    internal bool Selected { get; set; }
    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    internal bool IconOnly { get; set; }
    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    internal string Glyph { get; init; } = "";
    public SurfaceButton()
    {
        SetStyle(ControlStyles.UserPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
    }
    public override Size GetPreferredSize(Size proposedSize)
    {
        // Font, Padding and MinimumSize have already been scaled by WinForms.
        // Native Button's themed sizing adds a second DPI-dependent minimum to our owner-drawn surface.
        var text = TextRenderer.MeasureText(Text, Font, Size.Empty, TextFormatFlags.SingleLine);
        return new Size(Math.Max(MinimumSize.Width, text.Width + Padding.Horizontal),
            Math.Max(MinimumSize.Height, text.Height + Padding.Vertical));
    }
    protected override void OnMouseEnter(EventArgs e) { hovered = true; Invalidate(); base.OnMouseEnter(e); }
    protected override void OnMouseLeave(EventArgs e) { hovered = false; Invalidate(); base.OnMouseLeave(e); }
    protected override void OnPaint(PaintEventArgs e)
    {
        if (SystemInformation.HighContrast && !IconOnly) { base.OnPaint(e); return; }
        var scale = DeviceDpi / 96f;
        var g = e.Graphics;
        var fill = !Enabled ? AppStyle.Disabled : Primary ? AppStyle.Accent : Selected ? AppStyle.AccentSoft : hovered ? AppStyle.Hover : Navigation ? BackColor : AppStyle.Surface;
        SurfacePanel.PaintSurface(g, ClientSize, Parent?.BackColor ?? AppStyle.Background, fill,
            !Navigation && !Primary ? AppStyle.Border : null, 4 * scale);
        if (Navigation && Selected)
        {
            using var marker = new SolidBrush(AppStyle.Accent);
            using var strip = SurfacePanel.Rounded(new RectangleF(3 * scale, Height / 2f - 9 * scale, 3 * scale, 18 * scale), 1.5f * scale);
            g.FillPath(marker, strip);
        }
        var ink = !Enabled ? AppStyle.Muted : Primary ? AppStyle.OnAccent : Selected ? AppStyle.Accent : AppStyle.Ink;
        var rect = ClientRectangle;
        if (Glyph.Length > 0)
            rect = PaintGlyph(g, ClientSize, Glyph, ink, DeviceDpi, IconOnly);
        if (!IconOnly) TextRenderer.DrawText(g, Text, Font, rect, ink, TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis |
            (Navigation ? TextFormatFlags.Left : TextFormatFlags.HorizontalCenter));
        if (Focused && ShowFocusCues) ControlPaint.DrawFocusRectangle(g, Rectangle.Inflate(ClientRectangle, -(int)(4 * scale), -(int)(4 * scale)), ink, fill);
    }
    internal static Rectangle PaintGlyph(Graphics graphics, Size size, string glyph, Color ink, int dpi, bool iconOnly = false)
    {
        var scale = dpi / 96f;
        int Px(int value) => (int)Math.Round(value * scale);
        // Pixel units scale exactly once, with the same DPI as the icon column.
        // NoPadding prevents GDI's text overhang padding from clipping wide icon glyphs.
        using var icon = new Font("Segoe MDL2 Assets", 18 * scale, FontStyle.Regular, GraphicsUnit.Pixel);
        var slot = new Rectangle(Px(12), 0, Px(32), size.Height);
        if (iconOnly) slot = new Rectangle(0, 0, size.Width, size.Height);
        TextRenderer.DrawText(graphics, glyph, icon, slot, ink,
            TextFormatFlags.NoPadding | TextFormatFlags.SingleLine | TextFormatFlags.NoPrefix |
            TextFormatFlags.VerticalCenter | TextFormatFlags.HorizontalCenter);
        var textLeft = Px(52);
        return new Rectangle(textLeft, 0, Math.Max(0, size.Width - textLeft - Px(8)), size.Height);
    }
}

internal sealed class SurfacePanel : Panel
{
    public SurfacePanel()
    {
        BackColor = AppStyle.Surface;
        SetStyle(ControlStyles.UserPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
    }
    internal static GraphicsPath Rounded(RectangleF r, float radius)
    {
        var path = new GraphicsPath();
        var d = radius * 2;
        path.AddArc(r.Left, r.Top, d, d, 180, 90);
        path.AddArc(r.Right - d, r.Top, d, d, 270, 90);
        path.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
        path.AddArc(r.Left, r.Bottom - d, d, d, 90, 90);
        path.CloseFigure();
        return path;
    }
    protected override void OnPaintBackground(PaintEventArgs e)
    {
        PaintSurface(e.Graphics, ClientSize, Parent?.BackColor ?? AppStyle.Background, BackColor, AppStyle.Border, 6 * DeviceDpi / 96f);
    }
    internal static void PaintSurface(Graphics graphics, Size size, Color background, Color fill, Color? border, float radius)
    {
        graphics.Clear(background);
        if (size.Width < 4 || size.Height < 4) return;
        graphics.SmoothingMode = SmoothingMode.AntiAlias;
        // Pixel centers are symmetric around the client rectangle: do not put the
        // right/bottom half of an antialiased stroke outside the drawable area.
        using var path = Rounded(new RectangleF(1, 1, size.Width - 3, size.Height - 3), radius);
        if (border is not null && !SystemInformation.HighContrast)
        {
            // An even, one-pixel outer halo. No directional offset or inset bevel.
            using var halo = new Pen(Color.FromArgb(10, AppStyle.Ink), 3);
            graphics.DrawPath(halo, path);
        }
        using var brush = new SolidBrush(fill);
        graphics.FillPath(brush, path);
        if (border is { } color)
        {
            using var pen = new Pen(color);
            graphics.DrawPath(pen, path);
        }
    }
}

internal sealed class EmptyState : Panel
{
    private readonly Label heading;
    private readonly Label description;
    public EmptyState(string glyph, string title, string body)
    {
        Dock = DockStyle.Fill;
        BackColor = AppStyle.Surface;
        var stack = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 5, Padding = new Padding(24) };
        stack.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        stack.RowStyles.Add(new RowStyle(SizeType.Percent, 45));
        stack.RowStyles.Add(new RowStyle(SizeType.Absolute, 52));
        stack.RowStyles.Add(new RowStyle(SizeType.Absolute, 38));
        stack.RowStyles.Add(new RowStyle(SizeType.Absolute, 52));
        stack.RowStyles.Add(new RowStyle(SizeType.Percent, 55));
        stack.Controls.Add(new Label { Text = glyph, Font = new Font("Segoe MDL2 Assets", 27), ForeColor = AppStyle.Accent, TextAlign = ContentAlignment.MiddleCenter, Dock = DockStyle.Fill }, 0, 1);
        heading = AppStyle.Label(title); heading.Dock = DockStyle.Fill; heading.TextAlign = ContentAlignment.MiddleCenter; heading.Font = new Font("Microsoft YaHei UI", 12, FontStyle.Bold);
        description = AppStyle.Label(body); description.Dock = DockStyle.Fill; description.TextAlign = ContentAlignment.TopCenter; description.ForeColor = AppStyle.Muted;
        stack.Controls.Add(heading, 0, 2); stack.Controls.Add(description, 0, 3);
        Controls.Add(stack);
    }
    public void SetText(string title, string body) { heading.Text = title; description.Text = body; }
}

internal sealed class AppGrid : DataGridView
{
    protected override void OnHandleCreated(EventArgs e) { base.OnHandleCreated(e); ScaleRows(); }
    protected override void OnDpiChangedAfterParent(EventArgs e) { base.OnDpiChangedAfterParent(e); ScaleRows(); }
    protected override void OnDataBindingComplete(DataGridViewBindingCompleteEventArgs e) { ScaleRows(); base.OnDataBindingComplete(e); }
    private void ScaleRows()
    {
        var scale = DeviceDpi / 96f;
        ColumnHeadersHeight = (int)(40 * scale);
        RowTemplate.Height = (int)(46 * scale);
        DefaultCellStyle.Padding = new Padding((int)(10 * scale), (int)(5 * scale), (int)(10 * scale), (int)(5 * scale));
        ColumnHeadersDefaultCellStyle.Padding = new Padding((int)(10 * scale), 0, (int)(10 * scale), 0);
        foreach (DataGridViewRow row in Rows) row.Height = RowTemplate.Height;
    }
}
