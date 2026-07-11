using System.Windows.Media;
using DashConnect.App.Infra;
using DashConnect.Core.Models;

namespace DashConnect.App.ViewModels;

/// <summary>One service pill in the diagnostics strip (Discord, YouTube, …).</summary>
public sealed class HostChipViewModel : ViewModelBase
{
    private ServiceVerdict _verdict = ServiceVerdict.Unknown;
    private double _handshakeMs;

    public HostChipViewModel(string label, string host)
    {
        Label = label;
        Host = host;
    }

    public string Label { get; }
    public string Host { get; }

    public ServiceVerdict Verdict
    {
        get => _verdict;
        set
        {
            if (Set(ref _verdict, value))
            {
                OnPropertyChanged(nameof(VerdictText));
                OnPropertyChanged(nameof(VerdictBrush));
                OnPropertyChanged(nameof(Glyph));
            }
        }
    }

    public double HandshakeMs
    {
        get => _handshakeMs;
        set { if (Set(ref _handshakeMs, value)) OnPropertyChanged(nameof(LatencyText)); }
    }

    public string VerdictText => Verdict switch
    {
        ServiceVerdict.Open => "Работает",
        ServiceVerdict.Throttled => "Медленно",
        ServiceVerdict.Blocked => "Блокируется",
        ServiceVerdict.Unreachable => "Нет сети",
        _ => "—",
    };

    public string Glyph => Verdict switch
    {
        ServiceVerdict.Open => "✔",
        ServiceVerdict.Throttled => "⚠",
        ServiceVerdict.Blocked => "✖",
        ServiceVerdict.Unreachable => "∅",
        _ => "…",
    };

    public string LatencyText => HandshakeMs > 0 ? $"{HandshakeMs:F0} ms" : "";

    public Brush VerdictBrush => Verdict switch
    {
        ServiceVerdict.Open => Palette.Green,
        ServiceVerdict.Throttled => Palette.Amber,
        ServiceVerdict.Blocked => Palette.Red,
        ServiceVerdict.Unreachable => Palette.Muted,
        _ => Palette.Muted,
    };

    public void Apply(HostProbeResult r)
    {
        Verdict = r.Verdict;
        HandshakeMs = r.HandshakeMs;
    }
}

/// <summary>Shared frozen brushes for verdict colouring.</summary>
public static class Palette
{
    // Dash-os monochrome UI, but keep coloured status indicators (traffic-light semantics).
    public static readonly Brush Green = Freeze(0x2F, 0xD1, 0x6D); // Open / connected
    public static readonly Brush Amber = Freeze(0xF5, 0xA6, 0x23); // Throttled / working
    public static readonly Brush Red = Freeze(0xF3, 0x53, 0x53);   // Blocked / error
    public static readonly Brush Muted = Freeze(0x7A, 0x7A, 0x7A);
    public static readonly Brush Accent = Freeze(0xFF, 0xFF, 0xFF); // white accent stays monochrome
    public static readonly Brush SegActive = Freeze(0x30, 0x30, 0x30);

    private static Brush Freeze(byte r, byte g, byte b)
    {
        var brush = new SolidColorBrush(Color.FromRgb(r, g, b));
        brush.Freeze();
        return brush;
    }
}
