using System.Windows.Forms;
using Drawing = System.Drawing;

namespace DashConnect.App.Tray;

/// <summary>
/// Wraps a WinForms NotifyIcon so the WPF app can live in the system tray: minimize-to-tray,
/// context menu (Open / Connect-Disconnect / Exit), state-aware icon and balloon notifications.
/// </summary>
public sealed class TrayIconManager : IDisposable
{
    private readonly NotifyIcon _icon;
    private readonly ToolStripMenuItem _toggleItem;
    private readonly Drawing.Icon _iconActive;
    private readonly Drawing.Icon _iconIdle;

    public event Action? OpenRequested;
    public event Action? ToggleRequested;
    public event Action? ExitRequested;

    public TrayIconManager()
    {
        _iconIdle = IconFactory.CreateAppIcon(active: false);
        _iconActive = IconFactory.CreateAppIcon(active: true);

        var menu = new ContextMenuStrip();
        var open = new ToolStripMenuItem("Открыть Dash Connect");
        open.Click += (_, _) => OpenRequested?.Invoke();
        _toggleItem = new ToolStripMenuItem("Подключить");
        _toggleItem.Click += (_, _) => ToggleRequested?.Invoke();
        var exit = new ToolStripMenuItem("Выход");
        exit.Click += (_, _) => ExitRequested?.Invoke();
        menu.Items.AddRange(new ToolStripItem[] { open, _toggleItem, new ToolStripSeparator(), exit });

        _icon = new NotifyIcon
        {
            Icon = _iconIdle,
            Text = "Dash Connect — отключено",
            Visible = true,
            ContextMenuStrip = menu,
        };
        _icon.DoubleClick += (_, _) => OpenRequested?.Invoke();
    }

    public void SetState(bool active, string tooltip)
    {
        _icon.Icon = active ? _iconActive : _iconIdle;
        _icon.Text = Trim($"Dash Connect — {tooltip}", 63);
        _toggleItem.Text = active ? "Отключить" : "Подключить";
    }

    public void Notify(string title, string text)
    {
        try { _icon.ShowBalloonTip(3000, title, text, ToolTipIcon.Info); }
        catch { /* balloon tips are best-effort */ }
    }

    public void Dispose()
    {
        _icon.Visible = false;
        _icon.Dispose();
        _iconActive.Dispose();
        _iconIdle.Dispose();
    }

    private static string Trim(string s, int max) => s.Length <= max ? s : s[..max];
}
