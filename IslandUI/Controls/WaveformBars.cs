using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Animation;

namespace IslandUI.Controls;

/// <summary>
/// Little audio-waveform bars (Circle island in islandUI.png): five vertical
/// bars pulsing while the bot is working.
/// </summary>
public sealed class WaveformBars : FrameworkElement
{
    private static readonly DependencyProperty PhaseProperty =
        DependencyProperty.Register(nameof(Phase), typeof(double), typeof(WaveformBars),
            new FrameworkPropertyMetadata(0.0, FrameworkPropertyMetadataOptions.AffectsRender));

    private double Phase
    {
        get => (double)GetValue(PhaseProperty);
        set => SetValue(PhaseProperty, value);
    }

    public static readonly DependencyProperty ActiveProperty =
        DependencyProperty.Register(nameof(Active), typeof(bool), typeof(WaveformBars),
            new FrameworkPropertyMetadata(false, OnActiveChanged));

    public bool Active
    {
        get => (bool)GetValue(ActiveProperty);
        set => SetValue(ActiveProperty, value);
    }

    public static readonly DependencyProperty TintProperty =
        DependencyProperty.Register(nameof(Tint), typeof(Brush), typeof(WaveformBars),
            new FrameworkPropertyMetadata(new SolidColorBrush(Color.FromRgb(0x14, 0x73, 0xC8)),
                FrameworkPropertyMetadataOptions.AffectsRender));

    public Brush Tint
    {
        get => (Brush)GetValue(TintProperty);
        set => SetValue(TintProperty, value);
    }

    private static void OnActiveChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        var bars = (WaveformBars)d;
        bars.BeginAnimation(PhaseProperty, null);
        if ((bool)e.NewValue)
        {
            bars.BeginAnimation(PhaseProperty,
                new DoubleAnimation(6.28, new Duration(TimeSpan.FromSeconds(1.2)))
                {
                    RepeatBehavior = RepeatBehavior.Forever,
                });
        }
    }

    private static readonly double[] Speeds = [1.0, 1.7, 1.3, 2.1, 0.8];
    private static readonly double[] Offsets = [0.0, 1.1, 2.3, 0.6, 1.9];

    protected override void OnRender(DrawingContext dc)
    {
        double w = ActualWidth, h = ActualHeight;
        if (w <= 0 || h <= 0) return;
        int count = Speeds.Length;
        double barW = w / (count * 2 - 1);

        for (int i = 0; i < count; i++)
        {
            double amp = Active
                ? 0.25 + 0.75 * (0.5 + 0.5 * Math.Sin(Phase * Speeds[i] + Offsets[i]))
                : 0.15;
            double bh = h * amp;
            var geo = new RectangleGeometry(
                new Rect(i * barW * 2, (h - bh) / 2, barW, bh), barW / 2, barW / 2);
            dc.DrawGeometry(Tint, null, geo);
        }
    }
}
