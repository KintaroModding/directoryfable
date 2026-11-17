using System.Windows;
using Drawing = System.Drawing;
using Forms = System.Windows.Forms;

namespace DirectoryFable.Infrastructure;

/// <summary>
/// The notification-area (tray) icon. The app has no taskbar button; this icon
/// shows/hides the window and exits the app.
/// </summary>
public sealed class TrayIcon : IDisposable
{
    private readonly Forms.NotifyIcon _icon;

    public TrayIcon(Action leftClick, Action show, Action exit)
    {
        var menu = new Forms.ContextMenuStrip
        {
            Renderer = new DarkMenuRenderer(),
            BackColor = Drawing.Color.FromArgb(0x2B, 0x2B, 0x2B),
            ForeColor = Drawing.Color.White,
            ShowImageMargin = false,
            Font = new Drawing.Font("Segoe UI", 9f),
        };
        var showItem = new Forms.ToolStripMenuItem("Show DirectoryFable", null, (_, _) => show())
        {
            Font = new Drawing.Font("Segoe UI", 9f, Drawing.FontStyle.Bold),
            ForeColor = Drawing.Color.White,
        };
        menu.Items.Add(showItem);
        menu.Items.Add(new Forms.ToolStripSeparator());
        menu.Items.Add(new Forms.ToolStripMenuItem("Exit", null, (_, _) => exit()) { ForeColor = Drawing.Color.White });

        _icon = new Forms.NotifyIcon
        {
            Icon = LoadIcon(),
            Text = "DirectoryFable",
            ContextMenuStrip = menu,
            Visible = true,
        };
        _icon.MouseClick += (_, e) =>
        {
            if (e.Button == Forms.MouseButtons.Left)
                leftClick();
        };
    }

    private static Drawing.Icon LoadIcon()
    {
        var resource = Application.GetResourceStream(new Uri("pack://application:,,,/Assets/app.ico"));
        using var stream = resource.Stream;
        // SmallIconSize follows the display scaling (16 px at 100 %, 20 px at 125 %).
        return new Drawing.Icon(stream, Forms.SystemInformation.SmallIconSize);
    }

    public void Dispose()
    {
        _icon.Visible = false; // Otherwise a dead icon stays in the tray until the mouse passes over it.
        _icon.Dispose();
    }

    private static readonly Drawing.Color Background = Drawing.Color.FromArgb(0x2B, 0x2B, 0x2B);
    private static readonly Drawing.Color Hover = Drawing.Color.FromArgb(0x3D, 0x3D, 0x3D);
    private static readonly Drawing.Color Line = Drawing.Color.FromArgb(0x45, 0x45, 0x45);

    /// <summary>
    /// Draws the highlighted item and the text itself. The color table alone is not enough:
    /// for keyboard-selected items WinForms fell back to the light-blue system highlight
    /// with white text.
    /// </summary>
    private sealed class DarkMenuRenderer() : Forms.ToolStripProfessionalRenderer(new DarkMenuColors())
    {
        protected override void OnRenderMenuItemBackground(Forms.ToolStripItemRenderEventArgs e)
        {
            var bounds = new Drawing.Rectangle(2, 0, e.Item.Width - 4, e.Item.Height);
            using var brush = new Drawing.SolidBrush(e.Item.Selected || e.Item.Pressed ? Hover : Background);
            e.Graphics.FillRectangle(brush, bounds);
        }

        protected override void OnRenderItemText(Forms.ToolStripItemTextRenderEventArgs e)
        {
            e.TextColor = e.Item.Enabled ? Drawing.Color.White : Drawing.Color.Gray;
            base.OnRenderItemText(e);
        }
    }

    /// <summary>Dark colors for the tray menu, close to the Windows 11 dark menus.</summary>
    private sealed class DarkMenuColors : Forms.ProfessionalColorTable
    {
        public override Drawing.Color ToolStripDropDownBackground => Background;
        public override Drawing.Color ImageMarginGradientBegin => Background;
        public override Drawing.Color ImageMarginGradientMiddle => Background;
        public override Drawing.Color ImageMarginGradientEnd => Background;
        public override Drawing.Color MenuBorder => Line;
        public override Drawing.Color MenuItemBorder => Hover;
        public override Drawing.Color MenuItemSelected => Hover;
        public override Drawing.Color SeparatorDark => Line;
        public override Drawing.Color SeparatorLight => Background;
    }
}
