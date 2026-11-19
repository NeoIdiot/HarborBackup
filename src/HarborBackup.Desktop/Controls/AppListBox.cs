namespace HarborBackup.Desktop.Styling;

internal sealed class AppListBox : ListBox
{
    internal AppListBox()
    {
        BorderStyle = BorderStyle.None;
        IntegralHeight = false;
        DrawMode = DrawMode.OwnerDrawFixed;
        BackColor = AppStyle.Surface; ForeColor = AppStyle.Ink;
        UpdateRowHeight();
    }
    private void UpdateRowHeight() =>
        ItemHeight = Math.Max((int)Math.Ceiling(32 * DeviceDpi / 96f),
            TextRenderer.MeasureText("国Agyp", Font).Height + (int)Math.Ceiling(10 * DeviceDpi / 96f));
    protected override void OnFontChanged(EventArgs e) { base.OnFontChanged(e); UpdateRowHeight(); }
    protected override void OnDpiChangedAfterParent(EventArgs e) { base.OnDpiChangedAfterParent(e); UpdateRowHeight(); }
    protected override void OnDrawItem(DrawItemEventArgs e)
    {
        if (e.Index < 0 || e.Index >= Items.Count) return;
        var selected = (e.State & DrawItemState.Selected) != 0;
        var background = selected ? AppStyle.AccentSoft : AppStyle.Surface;
        var foreground = !Enabled ? AppStyle.Muted : selected && SystemInformation.HighContrast ? AppStyle.OnAccent : AppStyle.Ink;
        using var brush = new SolidBrush(background);
        e.Graphics.FillRectangle(brush, e.Bounds);
        var bounds = Rectangle.Inflate(e.Bounds, -(int)Math.Ceiling(8 * DeviceDpi / 96f), 0);
        TextRenderer.DrawText(e.Graphics, GetItemText(Items[e.Index]), Font, bounds, foreground,
            TextFormatFlags.SingleLine | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix);
        if ((e.State & DrawItemState.Focus) != 0 && ShowFocusCues) e.DrawFocusRectangle();
    }
}
