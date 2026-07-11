using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using WpfImaging = System.Windows.Interop.Imaging;
using WpfMedia = System.Windows.Media;
using WpfBitmap = System.Windows.Media.Imaging;

namespace DashConnect.App.Tray;

/// <summary>
/// Draws the app / tray icon at runtime (a lightning bolt on a rounded gradient tile), avoiding any
/// shipped binary .ico asset. Two states: active (violet→blue, white bolt) and idle (muted grey).
/// GDI+ types are used unqualified; the few WPF imaging types are aliased to avoid ambiguity.
/// </summary>
public static class IconFactory
{
    [DllImport("user32.dll", SetLastError = true)] private static extern bool DestroyIcon(IntPtr handle);
    [DllImport("gdi32.dll")] private static extern bool DeleteObject(IntPtr hObject);

    public static Icon CreateAppIcon(bool active)
    {
        using var bmp = Render(active, 32);
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

    public static WpfMedia.ImageSource CreateImageSource(bool active)
    {
        using var bmp = Render(active, 64);
        var hbmp = bmp.GetHbitmap();
        try
        {
            var source = WpfImaging.CreateBitmapSourceFromHBitmap(
                hbmp, IntPtr.Zero, System.Windows.Int32Rect.Empty,
                WpfBitmap.BitmapSizeOptions.FromEmptyOptions());
            source.Freeze();
            return source;
        }
        finally
        {
            DeleteObject(hbmp);
        }
    }

    private static Bitmap Render(bool active, int size)
    {
        var bmp = new Bitmap(size, size, PixelFormat.Format32bppArgb);
        using var g = Graphics.FromImage(bmp);
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.Clear(Color.Transparent);

        var rect = new Rectangle(1, 1, size - 2, size - 2);
        using var path = RoundedRect(rect, size / 4);
        var c1 = active ? Color.FromArgb(240, 240, 240) : Color.FromArgb(52, 52, 52);
        var c2 = active ? Color.FromArgb(200, 200, 200) : Color.FromArgb(30, 30, 30);
        using var brush = new LinearGradientBrush(rect, c1, c2, 55f);
        g.FillPath(brush, path);

        // Lightning bolt, scaled to the tile.
        float s = size / 32f;
        var bolt = new[]
        {
            new PointF(18 * s, 5 * s), new PointF(9 * s, 18 * s), new PointF(15 * s, 18 * s),
            new PointF(13 * s, 27 * s), new PointF(23 * s, 13 * s), new PointF(16 * s, 13 * s),
        };
        using var boltBrush = new SolidBrush(active ? Color.FromArgb(12, 12, 12) : Color.FromArgb(150, 150, 150));
        g.FillPolygon(boltBrush, bolt);
        return bmp;
    }

    private static GraphicsPath RoundedRect(Rectangle r, int radius)
    {
        int d = radius * 2;
        var path = new GraphicsPath();
        path.AddArc(r.X, r.Y, d, d, 180, 90);
        path.AddArc(r.Right - d, r.Y, d, d, 270, 90);
        path.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
        path.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
        path.CloseFigure();
        return path;
    }
}
