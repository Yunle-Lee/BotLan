using System.Windows;
using System.Windows.Input;
using System.Windows.Media;

namespace IslandUI.Controls;

/// <summary>
/// The corner-orbit brightness slider (明暗角调节滑块.mp4): a knob travelling
/// along an arc around the top-right corner, progressively interpolating the
/// theme between light (top) and dark (bottom of the arc).
/// </summary>
public sealed class ThemeSlider : FrameworkElement
{
    private const double ArcRadius = 34;
    private const double KnobR = 9;

    public static readonly DependencyProperty ValueProperty =
        DependencyProperty.Register(nameof(Value), typeof(double), typeof(ThemeSlider),
            new FrameworkPropertyMetadata(0.0, FrameworkPropertyMetadataOptions.AffectsRender |
                FrameworkPropertyMetadataOptions.BindsTwoWayByDefault, OnValueChanged));

    public double Value
    {
        get => (double)GetValue(ValueProperty);
        set => SetValue(ValueProperty, value);
    }

    private bool _dragging;

    public ThemeSlider()
    {
        Cursor = Cursors.Hand;
    }

    private static void OnValueChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        // Settings live outside the control; the binding writes back.
    }

    // 弧心与胶囊右上角的圆角圆心完全重合(胶囊右上圆角半径 34),
    // 轨道就是圆角曲线本身,旋钮骑在边框线上滑动.
    private Point ArcCenter => new(30, 34);

    private Point KnobCenter()
    {
        double angle = (-90 + Value * 90) * Math.PI / 180;
        var c = ArcCenter;
        return new Point(c.X + ArcRadius * Math.Cos(angle), c.Y + ArcRadius * Math.Sin(angle));
    }

    protected override void OnRender(DrawingContext dc)
    {
        var ink = ComicTheme.Current.Ink;
        var line = ComicTheme.Current.Line;

        // Arc track: top edge -> around the corner (bulging top-right) -> right edge.
        var trackPen = new Pen(line, 2.5) { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round };
        trackPen.Freeze();
        dc.DrawGeometry(null, trackPen, Arc(ArcCenter, ArcRadius, -90, 90));

        // Travelled part (progressively inked)
        if (Value > 0.02)
        {
            var pen = new Pen(ink, 3.5) { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round };
            pen.Freeze();
            dc.DrawGeometry(null, pen, Arc(ArcCenter, ArcRadius, -90, 90 * Value));
        }

        // Knob: filled circle + sun/moon glyph
        var kc = KnobCenter();
        var knobFill = ComicTheme.Current.Card;
        dc.DrawEllipse(knobFill, new Pen(ink, 1.8), kc, KnobR, KnobR);
        DrawGlyph(dc, kc);
    }

    private void DrawGlyph(DrawingContext dc, Point c)
    {
        var ink = ComicTheme.Current.Ink;
        if (Value < 0.5)
        {
            // Sun: disc + rays
            dc.DrawEllipse(ink, null, c, 3, 3);
            var pen = new Pen(ink, 1.3) { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round };
            pen.Freeze();
            for (int i = 0; i < 8; i++)
            {
                double a = i * Math.PI / 4;
                dc.DrawLine(pen,
                    new Point(c.X + 4.5 * Math.Cos(a), c.Y + 4.5 * Math.Sin(a)),
                    new Point(c.X + 6.2 * Math.Cos(a), c.Y + 6.2 * Math.Sin(a)));
            }
        }
        else
        {
            // Moon: crescent via two overlapping circles punched out.
            var bg = ComicTheme.Current.Card;
            dc.DrawEllipse(ink, null, c, 4, 4);
            dc.DrawEllipse(bg, null, new Point(c.X + 2.2, c.Y - 1.6), 3.4, 3.4);
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
            ctx.ArcTo(to, new Size(radius, radius), 0, Math.Abs(sweepDeg) > 180,
                sweepDeg >= 0 ? SweepDirection.Clockwise : SweepDirection.Counterclockwise, true, true);
        }
        geo.Freeze();
        return geo;
    }

    // ---- drag along the arc ---------------------------------------------------------

    protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonDown(e);
        _dragging = true;
        CaptureMouse();
        SetFromPoint(e.GetPosition(this));
        e.Handled = true;
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        if (_dragging && e.LeftButton == MouseButtonState.Pressed)
            SetFromPoint(e.GetPosition(this));
    }

    protected override void OnMouseLeftButtonUp(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonUp(e);
        _dragging = false;
        ReleaseMouseCapture();
    }

    private void SetFromPoint(Point p)
    {
        var c = ArcCenter;
        double angle = Math.Atan2(p.Y - c.Y, p.X - c.X) * 180 / Math.PI; // -180..180
        // 轨道角域 -90(顶,浅色)..0(右,深色)
        double v = (angle + 90) / 90.0;
        Value = Math.Clamp(v, 0, 1);
    }
}
