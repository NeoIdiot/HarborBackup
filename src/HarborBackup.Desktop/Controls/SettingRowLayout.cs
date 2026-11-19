namespace HarborBackup.Desktop.Styling;

// A settings row has one caption, one action/value and an optional flexible field.
// Measure these directly instead of recursively measuring nested autosizing tables.
internal sealed class SettingRowLayout : Panel
{
    private readonly Control caption;
    private readonly Control value;
    private readonly Control? field;
    private readonly bool stretchValue;
    private readonly int initialValueWidth;
    private readonly int initialDpi;
    private int measuredHeight;

    internal SettingRowLayout(Control caption, Control value, Control? field, bool stretchValue)
    {
        this.caption = caption; this.value = value; this.field = field; this.stretchValue = stretchValue;
        initialValueWidth = value.Width; initialDpi = DeviceDpi;
        Dock = DockStyle.Fill;
        Margin = Padding.Empty;
        foreach (var child in new[] { caption, field, value }.OfType<Control>())
        {
            child.Dock = DockStyle.None; child.Anchor = AnchorStyles.Top | AnchorStyles.Left;
            child.Margin = Padding.Empty; Controls.Add(child);
        }
    }

    private (Size Caption, Size Value, int FieldWidth, int Gap, int Height) Measure(int width)
    {
        var gap = (int)Math.Round(12 * DeviceDpi / 96d);
        var valueSize = value.GetPreferredSize(Size.Empty);
        // InputFrame reports its current width. Retain the intended editor width
        // across the narrow, intermediate sizes used during construction/layout.
        if (value is InputFrame && !stretchValue)
            valueSize.Width = (int)Math.Round(initialValueWidth * DeviceDpi / (double)initialDpi);
        var captionWidth = caption.GetPreferredSize(Size.Empty).Width;
        var flexible = field is not null || stretchValue;
        captionWidth = Math.Min(captionWidth, Math.Max(1, width - (flexible ? 40 * DeviceDpi / 96 : valueSize.Width) - gap));
        var captionSize = caption.GetPreferredSize(new Size(captionWidth, 0));
        captionSize.Width = captionWidth;
        if (stretchValue) valueSize.Width = Math.Max(1, width - captionWidth - gap);
        else valueSize.Width = Math.Min(valueSize.Width, Math.Max(1, width - captionWidth - gap));
        var fieldWidth = field is null ? 0 : Math.Max(1, width - captionWidth - valueSize.Width - gap * 2);
        var fieldHeight = field?.GetPreferredSize(new Size(fieldWidth, 0)).Height ?? 0;
        return (captionSize, valueSize, fieldWidth, gap,
            Math.Max(captionSize.Height + (int)Math.Round(8 * DeviceDpi / 96d), Math.Max(valueSize.Height, fieldHeight)));
    }

    public override Size GetPreferredSize(Size proposedSize)
    {
        var width = Math.Max(1, proposedSize.Width > 0 ? proposedSize.Width : Width);
        return new Size(width, Measure(width).Height);
    }

    protected override void OnLayout(LayoutEventArgs levent)
    {
        if (caption is null) { base.OnLayout(levent); return; }
        var sizes = Measure(ClientSize.Width);
        caption.Bounds = new Rectangle(0, (sizes.Height - sizes.Caption.Height) / 2, sizes.Caption.Width, sizes.Caption.Height);
        value.Bounds = new Rectangle(Math.Max(0, ClientSize.Width - sizes.Value.Width),
            (sizes.Height - sizes.Value.Height) / 2, sizes.Value.Width, sizes.Value.Height);
        if (field is not null)
        {
            var height = field.GetPreferredSize(new Size(sizes.FieldWidth, 0)).Height;
            field.Bounds = new Rectangle(sizes.Caption.Width + sizes.Gap, (sizes.Height - height) / 2, sizes.FieldWidth, height);
        }
        base.OnLayout(levent);
        if (measuredHeight != sizes.Height)
        {
            measuredHeight = sizes.Height;
            Parent?.Parent?.PerformLayout(this, nameof(PreferredSize));
        }
    }
}

internal sealed class SettingsStack : Panel
{
    private bool arranging;
    protected override void OnLayout(LayoutEventArgs levent)
    {
        if (arranging) return;
        arranging = true;
        try
        {
            var y = Padding.Top;
            foreach (Control child in Controls)
            {
                var width = Math.Max(1, ClientSize.Width - Padding.Horizontal - child.Margin.Horizontal);
                var height = child is SurfacePanel card
                    ? card.Controls[0].GetPreferredSize(new Size(Math.Max(1, width - card.Padding.Horizontal), 0)).Height + card.Padding.Vertical
                    : child.GetPreferredSize(new Size(width, 0)).Height;
                child.SetBounds(Padding.Left + child.Margin.Left, y + child.Margin.Top, width, height);
                y += height + child.Margin.Vertical;
            }
            Height = y + Padding.Bottom;
        }
        finally { arranging = false; }
        base.OnLayout(levent);
    }
}

internal sealed class SettingsInlineLayout : Panel
{
    private Size measuredSize;
    internal SettingsInlineLayout(params Control[] controls)
    {
        Margin = Padding.Empty;
        foreach (var control in controls)
        {
            control.Dock = DockStyle.None; control.Anchor = AnchorStyles.Top | AnchorStyles.Left;
            control.Margin = Padding.Empty; Controls.Add(control);
        }
    }
    public override Size GetPreferredSize(Size proposedSize)
    {
        var sizes = Controls.Cast<Control>().Select(c => c.GetPreferredSize(Size.Empty)).ToArray();
        return new Size(sizes.Sum(s => s.Width) + Math.Max(0, sizes.Length - 1) * (int)Math.Round(8 * DeviceDpi / 96d),
            sizes.Select(s => s.Height).DefaultIfEmpty().Max());
    }
    protected override void OnLayout(LayoutEventArgs levent)
    {
        var x = 0;
        foreach (Control child in Controls)
        {
            var size = child.GetPreferredSize(Size.Empty);
            child.Bounds = new Rectangle(x, Math.Max(0, (Height - size.Height) / 2), size.Width, size.Height);
            x += size.Width + (int)Math.Round(8 * DeviceDpi / 96d);
        }
        base.OnLayout(levent);
        var preferred = GetPreferredSize(Size.Empty);
        if (measuredSize != preferred)
        {
            measuredSize = preferred;
            Parent?.PerformLayout(this, nameof(PreferredSize));
        }
    }
}
