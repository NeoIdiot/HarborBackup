namespace HarborBackup.Desktop.Styling;

// The drag target remains generous; only its centre line is visible.
internal sealed class NavigationSplitter : Splitter
{
    private bool hovered;
    internal NavigationSplitter()
    {
        SetStyle(ControlStyles.UserPaint | ControlStyles.OptimizedDoubleBuffer, true);
        BackColor = AppStyle.Background;
    }
    protected override void OnMouseEnter(EventArgs e) { hovered = true; Invalidate(); base.OnMouseEnter(e); }
    protected override void OnMouseLeave(EventArgs e) { hovered = false; Invalidate(); base.OnMouseLeave(e); }
    protected override void OnPaint(PaintEventArgs e)
    {
        e.Graphics.Clear(AppStyle.Background);
        using var pen = new Pen(hovered ? AppStyle.Accent : AppStyle.Border);
        e.Graphics.DrawLine(pen, Width / 2, 0, Width / 2, Height);
        base.OnPaint(e);
    }
}
