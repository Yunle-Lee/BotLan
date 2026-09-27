using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Effects;

namespace IslandUI.Controls;

/// <summary>
/// The glowing pixel-block loader from UI/Workinganimation.mp4: a 2×2 group of
/// pink blocks pulsing in sequence with a soft glow.
/// </summary>
public sealed class PixelLoader : FrameworkElement
{
    private static readonly DependencyProperty PhaseProperty =
        DependencyProperty.Register(nameof(Phase), typeof(double), typeof(PixelLoader),
            new FrameworkPropertyMetadata(0.0, FrameworkPropertyMetadataOptions.AffectsRender));

    private double Phase
    {
        get => (double)GetValue(PhaseProperty);
        set => SetValue(PhaseProperty, value);
    }

    public static readonly DependencyProperty TintProperty =
        DependencyProperty.Register(nameof(Tint), typeof(Color), typeof(PixelLoader),
            new FrameworkPropertyMetadata(Color.FromRgb(0xF4, 0x6F, 0x8E),
                FrameworkPropertyMetadataOptions.AffectsRender));

    public Color Tint
    {
        get => (Color)GetValue(TintProperty);
        set => SetValue(TintProperty, value);
    }

    public PixelLoader()
    {
        var anim = new DoubleAnimation(4, new Duration(TimeSpan.FromSeconds(1.6)))
        {
            RepeatBehavior = RepeatBehavior.Forever,
        };
        BeginAnimation(PhaseProperty, anim);
    }

    protected override void OnRender(DrawingContext dc)
    {
        double d = Math.Min(ActualWidth, ActualHeight);
        if (d <= 0) return;
        double cell = d / 2.6;
        double gap = cell * 0.18;
        double ox = (ActualWidth - cell * 2 - gap) / 2;
        double oy = (ActualHeight - cell * 2 - gap) / 2;

        for (int i = 0; i < 4; i++)
        {
            // Blocks light up in sequence and fade with a soft tail.
            double local = (Phase - i + 8) % 4;
            double alpha = local < 1 ? 0.35 + 0.65 * local : 1 - (local - 1) * 0.35;
            alpha = Math.Clamp(alpha, 0.3, 1);

            double px = ox + (i % 2) * (cell + gap);
            double py = oy + (i / 2) * (cell + gap);
            double grow = cell * (0.9 + 0.1 * alpha);

            var brush = new SolidColorBrush(Tint) { Opacity = alpha };
            double corner = cell * 0.28;
            dc.DrawGeometry(brush, null, new RectangleGeometry(
                new Rect(px + (cell - grow) / 2, py + (cell - grow) / 2, grow, grow),
                corner, corner));
        }
    }
}
