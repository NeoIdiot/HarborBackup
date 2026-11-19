using HarborBackup.Desktop.Localization;

namespace HarborBackup.Desktop.Styling;

internal static class AppStyle
{
    public static Icon BrandIcon { get; } = LoadIcon();
    private static Icon LoadIcon()
    {
        using var stream = typeof(AppStyle).Assembly.GetManifestResourceStream("HarborBackup.Desktop.Assets.HarborBackup.ico")!;
        using var icon = new Icon(stream);
        return (Icon)icon.Clone();
    }
    public static Color Background => SystemInformation.HighContrast ? SystemColors.Window : Color.FromArgb(248, 249, 251);
    public static Color Surface => SystemColors.Window;
    public static Color Ink => SystemInformation.HighContrast ? SystemColors.WindowText : Color.FromArgb(30, 41, 59);
    public static Color Muted => SystemInformation.HighContrast ? SystemColors.WindowText : Color.FromArgb(100, 116, 139);
    public static Color Border => SystemInformation.HighContrast ? SystemColors.WindowText : Color.FromArgb(203, 213, 225);
    public static Color InputBorder => SystemInformation.HighContrast ? SystemColors.WindowText : Color.FromArgb(175, 185, 199);
    public static Control Input(Control control) => control is ListBox or RichTextBox or TextBox { Multiline: true }
        ? new ContentFrame(control) : new InputFrame(control);
    public static FlowLayoutPanel DialogActions(params Control[] controls)
    {
        var bar = Toolbar(controls.Reverse().ToArray());
        bar.Dock = DockStyle.Bottom;
        bar.FlowDirection = FlowDirection.RightToLeft;
        for (var i = 0; i < controls.Length; i++) controls[i].TabIndex = i;
        return bar;
    }
    public static Color Sidebar => SystemInformation.HighContrast ? SystemColors.Window : Color.FromArgb(240, 243, 247);
    public static Color AccentSoft => SystemInformation.HighContrast ? SystemColors.Highlight : Color.FromArgb(227, 239, 249);
    public static Color Hover => SystemInformation.HighContrast ? SystemColors.Highlight : Color.FromArgb(235, 240, 246);
    public static Color Disabled => SystemInformation.HighContrast ? SystemColors.Control : Color.FromArgb(237, 240, 244);
    public static Color Accent => SystemInformation.HighContrast ? SystemColors.Highlight : Color.FromArgb(0, 103, 176);
    public static Color OnAccent => SystemInformation.HighContrast ? SystemColors.HighlightText : Color.White;
    public static Button Button(string text, EventHandler? click = null, bool primary = false)
    {
        var button = new SurfaceButton { Primary = primary, Name = text, Text = text, AccessibleName = text, AutoSize = true, MinimumSize = new Size(88, 34), Padding = new Padding(12, 3, 12, 3),
            FlatStyle = FlatStyle.Flat, BackColor = primary ? Accent : Surface, ForeColor = primary ? OnAccent : Ink, Margin = new Padding(0, 0, 8, 8) };
        button.FlatAppearance.BorderColor = Border;
        if (click is not null) button.Click += click;
        return UiText.Bind(button, text);
    }
    public static Button Navigation(string text, string glyph, EventHandler click)
    {
        var button = new SurfaceButton { Navigation = true, Glyph = glyph, Name = "Navigate" + text, Text = text, AccessibleName = text,
            AutoSize = false, Width = 184, Height = 42, FlatStyle = FlatStyle.Flat, BackColor = Sidebar,
            TextAlign = ContentAlignment.MiddleLeft, Padding = new Padding(52, 0, 8, 0), Margin = new Padding(0, 0, 0, 4) };
        button.FlatAppearance.BorderSize = 0;
        button.Click += click;
        return UiText.Bind(button, text);
    }
    public static void SelectNavigation(Button button, bool selected)
    {
        ((SurfaceButton)button).Selected = selected;
        button.BackColor = selected ? AccentSoft : Sidebar;
        button.ForeColor = selected ? (SystemInformation.HighContrast ? OnAccent : Accent) : Ink;
        button.Invalidate();
    }
    public static FlowLayoutPanel Toolbar(params Control[] controls)
    {
        var panel = new FlowLayoutPanel { Dock = DockStyle.Top, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, WrapContents = true, Padding = new Padding(0, 6, 0, 6) };
        panel.Controls.AddRange(controls);
        return panel;
    }
    public static Label Label(string text, int height = 32) => UiText.Bind(new Label
    { AutoSize = false, Height = height, Dock = DockStyle.Top, ForeColor = Ink, AutoEllipsis = true, TextAlign = ContentAlignment.MiddleLeft }, text);
    public static DataGridView Grid(params (string Property, string Title, float Weight)[] columns)
    {
        var grid = new AppGrid { Dock = DockStyle.Fill, ReadOnly = true, AutoGenerateColumns = false, AllowUserToAddRows = false,
            AllowUserToDeleteRows = false, RowHeadersVisible = false, BackgroundColor = Surface, BorderStyle = BorderStyle.None,
            SelectionMode = DataGridViewSelectionMode.FullRowSelect, MultiSelect = false, AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.Fill,
            EnableHeadersVisualStyles = SystemInformation.HighContrast, ColumnHeadersHeight = 40, RowTemplate = { Height = 46 },
            CellBorderStyle = DataGridViewCellBorderStyle.SingleHorizontal, ColumnHeadersBorderStyle = DataGridViewHeaderBorderStyle.None,
            GridColor = Border, AllowUserToResizeRows = false,
            DefaultCellStyle = { Padding = new Padding(10, 5, 10, 5), BackColor = Surface, ForeColor = Ink,
                SelectionBackColor = AccentSoft, SelectionForeColor = SystemInformation.HighContrast ? OnAccent : Ink } };
        grid.ColumnHeadersDefaultCellStyle.BackColor = Background;
        grid.ColumnHeadersDefaultCellStyle.ForeColor = Muted;
        grid.ColumnHeadersDefaultCellStyle.Padding = new Padding(10, 0, 10, 0);
        grid.ColumnHeadersDefaultCellStyle.SelectionBackColor = Background;
        grid.ColumnHeadersDefaultCellStyle.SelectionForeColor = Ink;
        foreach (var column in columns) grid.Columns.Add(new DataGridViewTextBoxColumn
        { DataPropertyName = column.Property, HeaderText = column.Title, FillWeight = column.Weight, MinimumWidth = 75, SortMode = DataGridViewColumnSortMode.NotSortable });
        UiText.Watch(grid, () =>
        {
            for (var index = 0; index < columns.Length; index++) grid.Columns[index].HeaderText = UiText.Text(columns[index].Title);
            grid.Invalidate();
        });
        return grid;
    }
    public static TextBox Lines(string text = "") => new() { BorderStyle = BorderStyle.FixedSingle, Multiline = true, ScrollBars = ScrollBars.Vertical, Text = text,
        Height = 100, Dock = DockStyle.Top, AcceptsReturn = true, WordWrap = false };
    public static TextBox Text(string text = "") => new() { BorderStyle = BorderStyle.FixedSingle, Text = text, Dock = DockStyle.Top };
    public static void Configure(Form form, string title, Size size)
    {
        form.SuspendLayout();
        UiText.Set(form, title);
        form.Icon = BrandIcon;
        form.Font = new Font("Microsoft YaHei UI", 10F);
        form.AutoScaleDimensions = new SizeF(96F, 96F);
        form.AutoScaleMode = AutoScaleMode.Dpi;
        form.BackColor = Background;
        form.ForeColor = Ink;
        form.Size = size;
        form.StartPosition = FormStartPosition.CenterParent;
    }
}
