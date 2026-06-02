using System;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;

namespace MarathonAudio.App.Controls;

/// <summary>Draws an audio waveform from peak data with an optional playback marker.</summary>
public sealed class WaveformControl : Control
{
    public static readonly StyledProperty<float[]?> PeaksProperty =
        AvaloniaProperty.Register<WaveformControl, float[]?>(nameof(Peaks));
    public static readonly StyledProperty<double> ProgressProperty =
        AvaloniaProperty.Register<WaveformControl, double>(nameof(Progress));
    public static readonly StyledProperty<bool> DrawBackgroundProperty =
        AvaloniaProperty.Register<WaveformControl, bool>(nameof(DrawBackground), true);
    public static readonly StyledProperty<bool> ShowPlayheadProperty =
        AvaloniaProperty.Register<WaveformControl, bool>(nameof(ShowPlayhead), true);
    public static readonly StyledProperty<Color> BarColorProperty =
        AvaloniaProperty.Register<WaveformControl, Color>(nameof(BarColor), Color.FromRgb(0x56, 0x56, 0x5c));

    public float[]? Peaks { get => GetValue(PeaksProperty); set => SetValue(PeaksProperty, value); }
    public double Progress { get => GetValue(ProgressProperty); set => SetValue(ProgressProperty, value); }
    public bool DrawBackground { get => GetValue(DrawBackgroundProperty); set => SetValue(DrawBackgroundProperty, value); }
    public bool ShowPlayhead { get => GetValue(ShowPlayheadProperty); set => SetValue(ShowPlayheadProperty, value); }
    /// <summary>Color for the (unplayed) bars — used to tint the mini waveform on light cards.</summary>
    public Color BarColor { get => GetValue(BarColorProperty); set => SetValue(BarColorProperty, value); }

    static WaveformControl()
    {
        AffectsRender<WaveformControl>(PeaksProperty, ProgressProperty, DrawBackgroundProperty, ShowPlayheadProperty, BarColorProperty);
    }

    public override void Render(DrawingContext ctx)
    {
        var bounds = Bounds;
        double w = bounds.Width, h = bounds.Height;
        if (DrawBackground)
            ctx.FillRectangle(new SolidColorBrush(Color.FromRgb(0x0e, 0x0e, 0x13)), new Rect(0, 0, w, h));

        var peaks = Peaks;
        if (peaks == null || peaks.Length == 0 || w <= 0 || h <= 0) return;

        double mid = h / 2.0;
        var played = new SolidColorBrush(Color.FromRgb(0xc8, 0xf0, 0x00));   // accent
        var unplayed = new SolidColorBrush(BarColor);
        double progressX = Progress * w;

        for (int x = 0; x < w; x++)
        {
            int pi = (int)((double)x / w * peaks.Length);
            if (pi >= peaks.Length) pi = peaks.Length - 1;
            double amp = Math.Clamp(peaks[pi], 0, 1) * (mid - 1);
            var brush = ShowPlayhead && x <= progressX ? played : unplayed;
            ctx.DrawLine(new Pen(brush, 1), new Point(x + 0.5, mid - amp), new Point(x + 0.5, mid + amp));
        }

        if (ShowPlayhead && Progress > 0)
            ctx.DrawLine(new Pen(played, 1.4), new Point(progressX, 0), new Point(progressX, h));
    }
}
