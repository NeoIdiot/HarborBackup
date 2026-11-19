using HarborBackup.Desktop.Localization;
using HarborBackup.Desktop.Styling;

namespace HarborBackup.Desktop.Views;

internal sealed partial class MainForm
{
    private readonly Panel navigationSidebar = new() { Name = "NavigationSidebar", Dock = DockStyle.Left,
        Width = 184, Padding = new Padding(14, 20, 14, 12), BackColor = AppStyle.Sidebar };

    [System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)]
    internal int SidebarWidth
    {
        get => (int)Math.Round(navigationSidebar.Width * 96d / DeviceDpi);
        set => navigationSidebar.Width = (int)Math.Round(Math.Clamp(value, 64, 280) * DeviceDpi / 96d);
    }

    private Panel BuildSidebar()
    {
        var sidebar = navigationSidebar;
        var brand = new Panel { Name = "SidebarBrand", Dock = DockStyle.Top, Height = 64 };
        var brandImage = AppStyle.BrandIcon.ToBitmap();
        var picture = new PictureBox { Name = "SidebarBrandIcon", Image = brandImage, Location = new Point(4, 10), Size = new Size(38, 38),
            SizeMode = PictureBoxSizeMode.Zoom, AccessibleName = "HarborBackup" };
        brand.Controls.Add(picture);
        var nameLabel = new Label { Name = "SidebarBrandName", Text = "HarborBackup", Font = new Font("Segoe UI", 10, FontStyle.Bold),
            Location = new Point(50, 10), Size = new Size(106, 38), AutoEllipsis = true, TextAlign = ContentAlignment.MiddleLeft };
        brand.Controls.Add(nameLabel);
        var tips = new ToolTip();
        Disposed += (_, _) => { brandImage.Dispose(); tips.Dispose(); };
        var nav = new FlowLayoutPanel { Dock = DockStyle.Top, Height = 240,
            FlowDirection = FlowDirection.TopDown, WrapContents = false };
        foreach (var (name, glyph) in new[] { ("概览", "\uE80F"), ("备份项目", "\uE8B7"), ("运行记录", "\uE81C"), ("待处理问题", "\uE7BA"), ("设置", "\uE713") })
        {
            var button = AppStyle.Navigation(name, glyph, async (_, _) => await NavigateAsync(name));
            navigation.Add(name, button);
            nav.Controls.Add(button);
        }
        var bottom = new Panel { Dock = DockStyle.Bottom, Height = 52 };
        var exit = AppStyle.Navigation("退出程序", "\uE8AC", async (_, _) => { if (ExitRequested is { } handler) await handler(); });
        exit.Dock = DockStyle.Bottom;
        bottom.Controls.Add(exit);
        sidebar.Controls.Add(nav); sidebar.Controls.Add(brand); sidebar.Controls.Add(bottom);
        void FitBrand()
        {
            var compact = SidebarWidth < 150;
            nameLabel.Visible = !compact;
            picture.Width = Math.Min(picture.Height, brand.ClientSize.Width);
            picture.Left = compact ? Math.Max(0, (brand.ClientSize.Width - picture.Width) / 2) : (int)Math.Round(4 * DeviceDpi / 96d);
            nameLabel.SetBounds(picture.Right + (int)Math.Round(8 * DeviceDpi / 96d), picture.Top,
                Math.Max(0, brand.ClientSize.Width - picture.Right - (int)Math.Round(8 * DeviceDpi / 96d)), picture.Height);
        }
        void Fit()
        {
            FitBrand();
            var compact = SidebarWidth < 150;
            foreach (var button in navigation.Values.Append(exit))
            {
                ((SurfaceButton)button).IconOnly = compact;
                button.Width = nav.ClientSize.Width;
                tips.SetToolTip(button, button.Text);
                button.Invalidate();
            }
        }
        sidebar.SizeChanged += (_, _) => Fit();
        nav.SizeChanged += (_, _) => Fit();
        brand.Layout += (_, _) => FitBrand();
        Shown += (_, _) => Fit();
        DpiChanged += (_, _) => BeginInvoke((Action)(() => { if (!IsDisposed) Fit(); }));
        UiText.Watch(sidebar, Fit);
        Fit();
        return sidebar;
    }
}
