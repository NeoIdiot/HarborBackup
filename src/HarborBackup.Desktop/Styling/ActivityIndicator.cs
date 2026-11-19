using System.Drawing.Drawing2D;

namespace HarborBackup.Desktop.Styling;

internal sealed class ActivityIndicator : Control
{
    private readonly System.Windows.Forms.Timer timer = new() { Interval = 100 };
    private float angle;
    internal ActivityIndicator()
    {
        SetStyle(ControlStyles.UserPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.SupportsTransparentBackColor, true);
        BackColor = Color.Transparent;
        timer.Tick += (_, _) => { angle = (angle + 30) % 360; Invalidate(); };
    }
    protected override void OnVisibleChanged(EventArgs e)
    {
        base.OnVisibleChanged(e);
        timer.Enabled = Visible;
    }
    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);
        e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
        var inset = Math.Max(2, DeviceDpi / 48f);
        using var pen = new Pen(AppStyle.Accent, inset) { StartCap = LineCap.Round, EndCap = LineCap.Round };
        e.Graphics.DrawArc(pen, inset, inset, Width - inset * 2 - 1, Height - inset * 2 - 1, angle, 260);
    }
    protected override void Dispose(bool disposing)
    {
        if (disposing) timer.Dispose();
        base.Dispose(disposing);
    }
}
