using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Effects;
using System.Windows.Shapes;

namespace DashConnect.App.Controls;

/// <summary>
/// A monochrome particle field (white dots drifting up on black). Runs a per-frame render loop.
/// Idle: slow upward drift. <see cref="Burst"/> shoots every particle fast upward for ~2 seconds,
/// then eases back to the idle drift — fired when a connection succeeds.
/// </summary>
public sealed class ParticleCanvas : Canvas
{
    private sealed class Particle
    {
        public required Ellipse Dot;
        public double X, Y, VX, BaseVY, Size;
    }

    private readonly List<Particle> _particles = new();
    private readonly Random _rnd = new();
    private bool _running;
    private DateTime _last;
    private double _burstMs;

    private const int Count = 130;
    private const double BurstDurationMs = 2000;

    public ParticleCanvas()
    {
        IsHitTestVisible = false;
        ClipToBounds = true;
        // Soft frosted-glass haze: the whole field is lightly blurred so the translucent cards on top
        // read as glass with particles diffusing behind them. Performance bias keeps the per-frame
        // re-raster cheap even with a dense field.
        Effect = new BlurEffect { Radius = 3.5, KernelType = KernelType.Gaussian, RenderingBias = RenderingBias.Performance };
        Loaded += (_, _) => Start();
        Unloaded += (_, _) => Stop();
        IsVisibleChanged += (_, e) => { if ((bool)e.NewValue) Start(); else Stop(); };
    }

    /// <summary>Kick a 2-second upward burst.</summary>
    public void Burst() => _burstMs = BurstDurationMs;

    private void Start()
    {
        if (_running || !IsLoaded) return;
        if (_particles.Count == 0) Build();
        _running = true;
        _last = DateTime.Now;
        CompositionTarget.Rendering += OnRender;
    }

    private void Stop()
    {
        if (!_running) return;
        _running = false;
        CompositionTarget.Rendering -= OnRender;
    }

    private void Build()
    {
        for (int i = 0; i < Count; i++)
        {
            double size = 1.4 + _rnd.NextDouble() * 3.0;
            var dot = new Ellipse
            {
                Width = size,
                Height = size,
                Fill = Brushes.White,
                // Brighter than before so the dots stay visible through the translucent glass cards.
                Opacity = 0.08 + _rnd.NextDouble() * 0.30,
            };
            Children.Add(dot);
            var p = new Particle { Dot = dot, Size = size };
            Seed(p, anywhere: true);
            _particles.Add(p);
        }
    }

    private void Seed(Particle p, bool anywhere)
    {
        double w = W, h = H;
        p.X = _rnd.NextDouble() * w;
        p.Y = anywhere ? _rnd.NextDouble() * h : h + p.Size;
        p.BaseVY = -(4 + _rnd.NextDouble() * 11); // px/s slow upward drift
        p.VX = (_rnd.NextDouble() - 0.5) * 6;
    }

    private double W => ActualWidth > 0 ? ActualWidth : 420;
    private double H => ActualHeight > 0 ? ActualHeight : 720;

    private void OnRender(object? sender, EventArgs e)
    {
        var now = DateTime.Now;
        double dt = (now - _last).TotalSeconds;
        _last = now;
        if (dt <= 0 || dt > 0.1) dt = 0.016;

        double w = W, h = H;

        double burst = 0;
        if (_burstMs > 0)
        {
            _burstMs -= dt * 1000;
            burst = Math.Max(0, _burstMs) / BurstDurationMs; // 1 → 0 over 2s
            burst *= burst; // ease-out so it decays smoothly
        }

        foreach (var p in _particles)
        {
            double vy = p.BaseVY - burst * 520;   // strong upward during burst
            double vx = p.VX * (1 - burst);
            p.X += vx * dt;
            p.Y += vy * dt;

            if (p.Y < -p.Size) { p.Y = h + p.Size; p.X = _rnd.NextDouble() * w; }
            else if (p.Y > h + p.Size) { p.Y = -p.Size; }
            if (p.X < -p.Size) p.X += w + p.Size; else if (p.X > w + p.Size) p.X -= w + p.Size;

            SetLeft(p.Dot, p.X);
            SetTop(p.Dot, p.Y);
        }
    }
}
