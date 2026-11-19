namespace HarborBackup.Desktop.Styling;

// Shared border for native multiline editors and lists; the native control keeps
// selection, scrolling, IME and accessibility behavior.
internal sealed class ContentFrame : Panel
{
    internal ContentFrame(Control content)
    {
        Name = content.Name + "Frame";
        Dock = content.Dock;
        Size = new Size(content.Width + 16, content.Height + 16);
        Padding = new Padding(8); Margin = new Padding(0, 0, 8, 8);
        TabStop = false; BackColor = AppStyle.Surface;
        SetStyle(ControlStyles.UserPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
        switch (content)
        {
            case TextBox text: text.BorderStyle = BorderStyle.None; break;
            case RichTextBox rich: rich.BorderStyle = BorderStyle.None; break;
            case ListBox list: list.BorderStyle = BorderStyle.None; list.IntegralHeight = false; break;
        }
        content.BackColor = AppStyle.Surface; content.ForeColor = AppStyle.Ink;
        content.Dock = DockStyle.Fill; content.Margin = Padding.Empty;
        Controls.Add(content);
        content.Enter += (_, _) => Invalidate();
        content.Leave += (_, _) => Invalidate();
        MouseDown += (_, _) => content.Focus();
    }
    protected override void OnPaintBackground(PaintEventArgs e) =>
        SurfacePanel.PaintSurface(e.Graphics, ClientSize, Parent?.BackColor ?? AppStyle.Background,
            AppStyle.Surface, ContainsFocus ? AppStyle.Accent : AppStyle.InputBorder, 4 * DeviceDpi / 96f);
}
