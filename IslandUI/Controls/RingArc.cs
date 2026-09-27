using System.Windows;
using System.Windows.Media;

namespace IslandUI.Controls;

/// <summary>
/// The bot's ring: dashed hand-drawn segments in the bot's colour
/// (islandUI.png). Fraction of dashes lit = quota remaining or activity state.
/// </summary>
public sealed class RingArc : FrameworkElement
{
    private const int DashCount = 18;

    public static readonly DependencyProperty FractionProperty =
        DependencyProperty.Register(nameof(Fraction), typeof(double), typeof(RingArc),
            new FrameworkPropertyMetadata(1.0, FrameworkPropertyMetadataOptions.AffectsRender));

    public double Fraction
    {
        get => (double)GetValue(FractionProperty);
        set => SetValue(FractionProperty, value);
    }

    public static readonly DependencyProperty TintProperty =
        DependencyProperty.Register(nameof(Tint), typeof(Color), typeof(RingArc),
            new FrameworkPropertyMetadata(Colors.MediumSeaGreen, FrameworkPropertyMetadataOptions.AffectsRender));

    public Color Tint
    {
        get => (Color)GetValue(TintProperty);
        set => SetValue(TintProperty, value);
    }

    public static readonly DependencyProperty StrokeWidthProperty =
        DependencyProperty.Register(nameof(StrokeWidth), typeof(double), typeof(RingArc),
            new FrameworkPropertyMetadata(3.5, FrameworkPropertyMetadataOptions.AffectsRender));

    public double StrokeWidth
    {
        get => (double)GetValue(StrokeWidthProperty);
        set => SetValue(StrokeWidthProperty, value);
    }

    protected override void OnRender(DrawingContext dc)
    {
        double d = Math.Min(ActualWidth, ActualHeight);
        if (d <= StrokeWidth) return;
        double radius = (d - StrokeWidth) / 2;
        var center = new Point(ActualWidth / 2, ActualHeight / 2);
        double fraction = Math.Clamp(Fraction, 0, 1);
        int lit = (int)Math.Round(fraction * DashCount);

        for (int i = 0; i < DashCount; i++)
        {
            double gap = 360.0 / DashCount * 0.28;
            double a0 = -90 + i * (360.0 / DashCount) + gap / 2;
            double a1 = a0 + 360.0 / DashCount - gap;

            bool isLit = i < lit;
            var brush = new SolidColorBrush(Tint) { Opacity = isLit ? 1 : 0.22 };
            var pen = new Pen(brush, StrokeWidth)
            {
                StartLineCap = PenLineCap.Round,
                EndLineCap = PenLineCap.Round,
            };
            pen.Freeze();
            dc.DrawGeometry(null, pen, Arc(center, radius, a0, a1 - a0));
        }
    }

    private static StreamGeometry Arc(Point center, double radius, double startDeg, double sweepDeg)
    {
        double a0 = startDeg * Math.PI / 180;
        double a1 = (startDeg + sweepDeg) * Math.PI / 180;
        var from = new Point(center.X + radius * Math.Cos(a0), center.Y + radius * Math.Sin(a0));
        var to = new Point(center.X + radius * Math.Cos(a1), center.Y + radius * Math.Sin(a1));
        var geo = new StreamGeometry();
        using (var ctx = geo.Open())
        {
            ctx.BeginFigure(from, false, false);
            ctx.ArcTo(to, new Size(radius, radius), 0, sweepDeg > 180, SweepDirection.Clockwise, true, true);
        }
        geo.Freeze();
        return geo;
    }
}
