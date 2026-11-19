namespace HarborBackup.Desktop.Styling;

// A small-radius frame around the actual Windows editor. Keyboard navigation,
// numeric validation and dropdown/calendar behavior remain with the native control.
internal sealed class InputFrame : Panel
{
    private readonly Control input;
    private readonly Panel viewport = new() { TabStop = false };
    private readonly bool clipNativeBorder;
    private int editorHeight;
    private int preferredHeight = 32;
    private bool arranging;

    internal InputFrame(Control input)
    {
        this.input = input;
        input.BackColor = AppStyle.Surface; input.ForeColor = AppStyle.Ink;
        Name = input.Name + "Frame";
        clipNativeBorder = input is ComboBox or DateTimePicker;
        if (input is TextBox text)
        {
            text.BorderStyle = BorderStyle.None;
            // Native single-line AutoSize uses font metrics that can be shorter than
            // the GDI-rendered placeholder (including CJK fallback and its top offset).
            if (!text.Multiline) text.AutoSize = false;
        }
        if (input is NumericUpDown number) number.BorderStyle = BorderStyle.None;
        input.Dock = DockStyle.None; input.Anchor = AnchorStyles.Top | AnchorStyles.Left; input.Margin = Padding.Empty;
        // The frame has a measured height, not content-driven AutoSize. A parent
        // TableLayoutPanel must not recursively measure native editors on every layout.
        TabStop = false; AutoSize = false;
        Size = new Size(input.Width + 16, 32); MinimumSize = new Size(0, 32);
        Margin = new Padding(0, 0, 8, 8);
        BackColor = viewport.BackColor = AppStyle.Surface;
        SetStyle(ControlStyles.UserPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
        viewport.Controls.Add(input); Controls.Add(viewport);
        input.FontChanged += (_, _) => UpdateMetrics();
        input.HandleCreated += (_, _) => UpdateMetrics();
        UpdateMetrics();
    }

    public override Size GetPreferredSize(Size proposedSize)
    {
        if (input is null) return base.GetPreferredSize(proposedSize);
        return new Size(Width, preferredHeight);
    }
    // Fixed-height native dropdowns can report a larger preferred size than the
    // height Windows actually accepts; clip their real border, not that estimate.
    private void UpdateMetrics()
    {
        if (input is null || arranging) return;
        editorHeight = clipNativeBorder ? input.Height : input.GetPreferredSize(Size.Empty).Height;
        if (input is TextBox { Multiline: false } text)
        {
            var sample = text.PlaceholderText.Length == 0 ? "国Agyp" : text.PlaceholderText + "国Agyp";
            var textHeight = TextRenderer.MeasureText(sample, text.Font, Size.Empty,
                TextFormatFlags.NoPadding | TextFormatFlags.SingleLine).Height;
            editorHeight = Math.Max(text.PreferredHeight, textHeight + Math.Max(2, (int)Math.Ceiling(2 * DeviceDpi / 96f)));
        }
        preferredHeight = Math.Max((int)(32 * DeviceDpi / 96f), editorHeight + (int)(8 * DeviceDpi / 96f));
        Height = preferredHeight;
        ArrangeEditor();
    }
    protected override void OnFontChanged(EventArgs e) { base.OnFontChanged(e); UpdateMetrics(); }
    protected override void OnDpiChangedAfterParent(EventArgs e) { base.OnDpiChangedAfterParent(e); UpdateMetrics(); }
    protected override void OnHandleCreated(EventArgs e) { base.OnHandleCreated(e); UpdateMetrics(); }
    protected override void OnSizeChanged(EventArgs e) { base.OnSizeChanged(e); ArrangeEditor(); }
    private void ArrangeEditor()
    {
        if (input is null || editorHeight == 0 || arranging) return;
        arranging = true;
        viewport.SuspendLayout();
        try
        {
            var inset = Math.Max(1, (int)(DeviceDpi / 96f));
            var padding = (int)(8 * DeviceDpi / 96f);
            var height = editorHeight;
            var visibleHeight = height - (clipNativeBorder ? inset * 2 : 0);
            viewport.Bounds = new Rectangle(padding, Math.Max(1, (Height - visibleHeight) / 2), Math.Max(1, Width - padding * 2), visibleHeight);
            input.Bounds = clipNativeBorder
                ? new Rectangle(-inset, -inset, viewport.Width + inset * 2, height)
                : new Rectangle(0, 0, viewport.Width, height);
        }
        finally { viewport.ResumeLayout(false); arranging = false; }
    }
    protected override void OnEnter(EventArgs e) { base.OnEnter(e); Invalidate(); }
    protected override void OnLeave(EventArgs e) { base.OnLeave(e); Invalidate(); }
    protected override void OnMouseDown(MouseEventArgs e) { base.OnMouseDown(e); input.Focus(); }
    protected override void OnPaintBackground(PaintEventArgs e)
    {
        SurfacePanel.PaintSurface(e.Graphics, ClientSize, Parent?.BackColor ?? AppStyle.Background, AppStyle.Surface,
            ContainsFocus ? AppStyle.Accent : AppStyle.InputBorder, 4 * DeviceDpi / 96f);
    }
}
