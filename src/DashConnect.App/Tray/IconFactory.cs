using System.Drawing;
using System.Runtime.InteropServices;
using WpfMedia = System.Windows.Media;
using WpfBitmap = System.Windows.Media.Imaging;

namespace DashConnect.App.Tray;

/// <summary>
/// Loads the shipped brand icons (embedded as WPF resources): the full-colour app mark for the
/// window/taskbar, and the on/off tray glyphs. GDI+ <see cref="Icon"/> is used for the tray
/// (WinForms NotifyIcon); WPF imaging types are aliased to avoid ambiguity with WinForms.
/// </summary>
public static class IconFactory
{
    [DllImport("user32.dll", SetLastError = true)] private static extern bool DestroyIcon(IntPtr handle);

    /// <summary>Tray icon — bright/glowing when active, dark when idle.</summary>
    public static Icon CreateAppIcon(bool active)
    {
        var uri = new Uri($"pack://application:,,,/Assets/tray-{(active ? "on" : "off")}-32.png");
        using var stream = System.Windows.Application.GetResourceStream(uri)!.Stream;
        using var bmp = new Bitmap(stream);
        var hicon = bmp.GetHicon();
        try
        {
            using var tmp = Icon.FromHandle(hicon);
            return (Icon)tmp.Clone();
        }
        finally
        {
            DestroyIcon(hicon);
        }
    }

    /// <summary>Window / taskbar icon — the full-colour app mark (state-independent).</summary>
    public static WpfMedia.ImageSource CreateImageSource(bool active)
    {
        var img = new WpfBitmap.BitmapImage();
        img.BeginInit();
        img.UriSource = new Uri("pack://application:,,,/Assets/app-256.png");
        img.CacheOption = WpfBitmap.BitmapCacheOption.OnLoad;
        img.EndInit();
        img.Freeze();
        return img;
    }
}
