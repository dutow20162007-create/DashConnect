using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;

namespace DashConnect.App.Controls;

/// <summary>
/// A monochrome particle field (soft white dots drifting up on black).
///
/// Rendered cheaply for low GPU use: all dots are painted in a SINGLE <see cref="OnRender"/> pass
/// (not one UIElement per dot) driven by a ~30&#160;fps timer (not the display's 60–144&#160;Hz vsync),
/// with softness baked into a frozen radial-gradient brush instead of a per-frame blur shader. The
/// loop pauses whenever the window is minimized or hidden to the tray, so an idle app costs nothing.
///
/// <see cref="Burst"/> shoots every particle up for ~2&#160;s (fired when a connection succeeds).
/// </summary>
public sealed class ParticleCanvas : Canvas
{
    private struct Particle
    {
        public double X, Y, VX, BaseVY, Size, Opacity;
    }

    private Particle[] _particles = Array.Empty<Particle>();
    private readonly Random _rnd = new();
    private readonly DispatcherTimer _timer;
    private Window? _window;
    private DateTime _last;
    private double _burstMs;

    private const int Count = 90;
    private const double FrameMs = 33;          // ~30 fps — smooth for a slow drift, ~5x cheaper than 144 Hz vsync
    private const double BurstDurationMs = 2000;

    /// <summary>Soft glow baked into the brush (bright core → transparent edge) so we never need a
    /// per-frame blur effect. Frozen + shared across every dot.</summary>
    private static readonly Brush DotBrush = CreateDotBrush();

    public ParticleCanvas()
    {
        IsHitTestVisible = false;
        ClipToBounds = true;
        _timer = new DispatcherTimer(DispatcherPriority.Render) { Interval = TimeSpan.FromMilliseconds(FrameMs) };
        _timer.Tick += OnTick;

        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
        IsVisibleChanged += (_, e) => { if ((bool)e.NewValue) Start(); else Stop(); };
    }

    /// <summary>Kick a 2-second upward burst.</summary>
    public void Burst()
    {
        _burstMs = BurstDurationMs;
        Start(); // ensure the loop is running to play it
    }

    private void OnLoaded(object? sender, RoutedEventArgs e)
    {
        if (_particles.Length == 0) Build();
        _window = Window.GetWindow(this);
        if (_window is not null) _window.StateChanged += OnWindowStateChanged;
        Start();
    }

    private void OnUnloaded(object? sender, RoutedEventArgs e)
    {
        Stop();
        if (_window is not null) { _window.StateChanged -= OnWindowStateChanged; _window = null; }
    }

    // Don't animate a minimized window — it's invisible, so the frames are pure waste.
    private void OnWindowStateChanged(object? sender, EventArgs e)
    {
        if (_window?.WindowState == WindowState.Minimized) Stop();
        else Start();
    }

    private void Start()
    {
        if (_timer.IsEnabled || !IsLoaded || !IsVisible) return;
        if (_window?.WindowState == WindowState.Minimized) return;
        _last = DateTime.Now;
        _timer.Start();
    }

    private void Stop() => _timer.Stop();

    private void Build()
    {
        _particles = new Particle[Count];
        double w = W, h = H;
        for (int i = 0; i < Count; i++)
        {
            _particles[i] = new Particle
            {
                Size = 1.3 + _rnd.NextDouble() * 2.6,          // radius
                X = _rnd.NextDouble() * w,
                Y = _rnd.NextDouble() * h,
                BaseVY = -(4 + _rnd.NextDouble() * 11),        // px/s slow upward drift
                VX = (_rnd.NextDouble() - 0.5) * 6,
                Opacity = 0.10 + _rnd.NextDouble() * 0.34,
            };
        }
    }

    private double W => ActualWidth > 0 ? ActualWidth : 420;
    private double H => ActualHeight > 0 ? ActualHeight : 720;

    private void OnTick(object? sender, EventArgs e)
    {
        var now = DateTime.Now;
        double dt = (now - _last).TotalSeconds;
        _last = now;
        if (dt <= 0 || dt > 0.2) dt = FrameMs / 1000.0;

        double w = W, h = H, burst = 0;
        if (_burstMs > 0)
        {
            _burstMs -= dt * 1000;
            burst = Math.Max(0, _burstMs) / BurstDurationMs; // 1 → 0 over 2s
            burst *= burst;                                  // ease-out
        }

        var particles = _particles;
        for (int i = 0; i < particles.Length; i++)
        {
            ref var p = ref particles[i];
            double vy = p.BaseVY - burst * 520;              // strong upward during burst
            double vx = p.VX * (1 - burst);
            p.X += vx * dt;
            p.Y += vy * dt;

            if (p.Y < -p.Size) { p.Y = h + p.Size; p.X = _rnd.NextDouble() * w; }
            else if (p.Y > h + p.Size) p.Y = -p.Size;
            if (p.X < -p.Size) p.X += w + p.Size; else if (p.X > w + p.Size) p.X -= w + p.Size;
        }

        InvalidateVisual(); // one render pass for the whole field
    }

    protected override void OnRender(DrawingContext dc)
    {
        base.OnRender(dc);
        var particles = _particles;
        for (int i = 0; i < particles.Length; i++)
        {
            var p = particles[i];
            dc.PushOpacity(p.Opacity);
            dc.DrawEllipse(DotBrush, null, new Point(p.X, p.Y), p.Size, p.Size);
            dc.Pop();
        }
    }

    private static Brush CreateDotBrush()
    {
        var b = new RadialGradientBrush
        {
            GradientStops =
            {
                new GradientStop(Color.FromArgb(255, 255, 255, 255), 0.0),
                new GradientStop(Color.FromArgb(150, 255, 255, 255), 0.5),
                new GradientStop(Color.FromArgb(0, 255, 255, 255), 1.0),
            },
        };
        b.Freeze();
        return b;
    }
}
