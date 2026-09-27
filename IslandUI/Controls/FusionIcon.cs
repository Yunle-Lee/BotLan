using System.Windows;
using System.Windows.Media;

namespace IslandUI.Controls;

/// <summary>
/// Frame-measured replication of UI/WIFIBattery.mp4 (3.27s).
/// The whole animation is a keyframed timeline in a 640×260 canonical space
/// measured off the video frame by frame: signal bars shrink into dots that
/// trail along the swoosh into a smile under the ring, while the battery pill
/// narrows, glides to the ring's bottom-left, and melts into the growing arc.
/// <see cref="Fusion"/> is normalised video time: 0 = first frame, 1 = last.
/// </summary>
public sealed class FusionIcon : FrameworkElement
{
    public static readonly DependencyProperty FusionProperty =
        DependencyProperty.Register(nameof(Fusion), typeof(double), typeof(FusionIcon),
            new FrameworkPropertyMetadata(1.0, FrameworkPropertyMetadataOptions.AffectsRender));

    /// <summary>0 = first frame of the video (separate row), 1 = last frame (fused).</summary>
    public double Fusion
    {
        get => (double)GetValue(FusionProperty);
        set => SetValue(FusionProperty, value);
    }

    public static readonly DependencyProperty BatteryFractionProperty =
        DependencyProperty.Register(nameof(BatteryFraction), typeof(double), typeof(FusionIcon),
            new FrameworkPropertyMetadata(1.0, FrameworkPropertyMetadataOptions.AffectsRender));

    public double BatteryFraction
    {
        get => (double)GetValue(BatteryFractionProperty);
        set => SetValue(BatteryFractionProperty, value);
    }

    public static readonly DependencyProperty SignalFractionProperty =
        DependencyProperty.Register(nameof(SignalFraction), typeof(double), typeof(FusionIcon),
            new FrameworkPropertyMetadata(1.0, FrameworkPropertyMetadataOptions.AffectsRender));

    public double SignalFraction
    {
        get => (double)GetValue(SignalFractionProperty);
        set => SetValue(SignalFractionProperty, value);
    }

    public static readonly DependencyProperty IsChargingProperty =
        DependencyProperty.Register(nameof(IsCharging), typeof(bool), typeof(FusionIcon),
            new FrameworkPropertyMetadata(false, FrameworkPropertyMetadataOptions.AffectsRender));

    public bool IsCharging
    {
        get => (bool)GetValue(IsChargingProperty);
        set => SetValue(IsChargingProperty, value);
    }

    public static readonly DependencyProperty ForegroundProperty =
        DependencyProperty.Register(nameof(Foreground), typeof(Brush), typeof(FusionIcon),
            new FrameworkPropertyMetadata(Brushes.Black,
                FrameworkPropertyMetadataOptions.AffectsRender | FrameworkPropertyMetadataOptions.Inherits));

    public Brush Foreground
    {
        get => (Brush)GetValue(ForegroundProperty);
        set => SetValue(ForegroundProperty, value);
    }

    // ======================= measured timeline (640 x 260) =======================

    private const double CanonW = 640, CanonH = 260;
    private static readonly Point WifiC = new(300, 130);
    // Row state: the wifi dot stands on the same baseline as bars & battery
    // (reference crops put the glyph slightly higher; aligned per feedback).
    private const double WifiRowY = 172;
    // Fused: nudge the glyph down inside the ring — clears the battery number
    // at the top and centres the glyph in the ring's lower half.
    private const double WifiDrop = 26;
    private static readonly double[] BarX = [50, 95, 140, 185];
    private static readonly double[] BarH0 = [55, 90, 125, 160];
    private const double BarBottom0 = 205, BarW = 26;

    // Measured keyframe times (video seconds / 3.27s).
    private const double T1 = 1.0 / 3.27;   // bars -10%, battery narrowed
    private const double T2 = 1.5 / 3.27;   // bars -> dots, battery thin pill
    private const double T3 = 2.0 / 3.27;   // swoosh at ring bottom-left, dot trail
    private const double T4 = 2.6 / 3.27;   // ring nearly closed

    // Dot journey: bar bottoms -> lifted dots -> trail -> smile under the ring.
    private static readonly Point[] DotLift = [new(50, 135), new(95, 135), new(140, 135), new(185, 135)];
    private static readonly Point[] DotTrail = [new(50, 150), new(95, 170), new(140, 190), new(185, 210)];
    // 底部点阵:围绕圆环底部成圈的微笑弧(外高内低),补全圆的形态
    private static readonly Point[] DotSmile = [new(248, 217), new(290, 230), new(325, 230), new(367, 217)];

    // Battery keyframes: (center x, center y, width, height, corner radius)
    private static readonly (double cx, double cy, double w, double h, double r) BatK0 = (492.5, 140, 155, 130, 45);
    private static readonly (double cx, double cy, double w, double h, double r) BatK1 = (452.5, 140, 75, 130, 35);
    private static readonly (double cx, double cy, double w, double h, double r) BatK2 = (452.5, 205, 25, 80, 12);
    // Landing point: the ring's bottom-left arc start (160° on the circle).
    private static readonly (double cx, double cy, double w, double h, double r) BatK3 = (192, 169, 22, 22, 11);

    // Ring: centre = wifi centre, R = 115, stroke 24.
    // Track spans 135°..45° (270°) — it stops above the bottom dot zone and
    // never reaches the dots. Battery arc starts at the bottom-left (140°).
    private const double RingR = 115, RingStroke = 24, RingStartDeg = 140, RingSweepMax = 270;
    private const double TrackStartDeg = 135;

    protected override void OnRender(DrawingContext dc)
    {
        double t = Math.Clamp(Fusion, 0, 1);
        int lit = (int)Math.Ceiling(Math.Clamp(SignalFraction, 0, 1) * 4);

        // The fused result only uses the middle of the canonical space; zoom into
        // it as T grows so the ring fills the slot like the video's final frame.
        double zoomT = Smooth((t - 0.55) / 0.45);
        double fitRow = Math.Min(ActualWidth / CanonW, ActualHeight / CanonH);
        double fitFused = Math.Min(ActualWidth / 300.0, ActualHeight / CanonH);
        double fit = Lerp(fitRow, fitFused, zoomT);
        double cx = Lerp(CanonW / 2, WifiC.X, zoomT);
        double cy = Lerp(CanonH / 2, WifiC.Y + 4, zoomT);

        dc.PushTransform(new TranslateTransform(ActualWidth / 2, ActualHeight / 2));
        dc.PushTransform(new ScaleTransform(fit, fit));
        dc.PushTransform(new TranslateTransform(-cx, -cy));

        DrawWifi(dc, t);
        DrawBars(dc, t, lit);
        DrawRing(dc, t);
        DrawBattery(dc, t);

        dc.Pop();
        dc.Pop();
        dc.Pop();
    }

    // ---- WiFi glyph: static through the whole video ------------------------------

    // ---- WiFi glyph: two arcs + dot (design image: three elements only) --------

    private void DrawWifi(DrawingContext dc, double t)
    {
        // Row: dot on the shared baseline; Fused: dropped into the ring's middle.
        var c = new Point(WifiC.X, Lerp(WifiRowY, WifiC.Y + WifiDrop, Smooth(t)));
        var pen = new Pen(Foreground, 20)
        {
            StartLineCap = PenLineCap.Round,
            EndLineCap = PenLineCap.Round,
        };
        pen.Freeze();
        foreach (double r in new[] { 28.0, 55.0 })
            dc.DrawGeometry(null, pen, Arc(c, r, -135, 90));
        dc.DrawEllipse(Foreground, null, c, 15, 15);
    }

    // ---- signal bars -> lifted dots -> trail -> smile ------------------------------

    private void DrawBars(DrawingContext dc, double t, int lit)
    {
        for (int i = 0; i < 4; i++)
        {
            // height: full -> -10% (T1) -> 22 round dot (T2) -> dash (end)
            double h = Seg(t, 0, T1, T2,
                BarH0[i], BarH0[i] * 0.9, 22);
            if (t > T2) h = Lerp(22, 20, Smooth((t - T4) / (1 - T4)));
            double w2 = t < T2 ? BarW : Lerp(22, 24, Smooth((t - T4) / (1 - T4)));

            // position: bar x/bottom -> lifted -> trail -> smile
            var (x, bottom) = DotPath(i, t);

            double r = Math.Min(w2, h) / 2;
            double alpha = i < lit ? 1.0 : 0.25;
            dc.DrawGeometry(BrushAt(alpha), null,
                new RectangleGeometry(new Rect(x - w2 / 2, bottom - h, w2, h), r, r));
        }
    }

    private (double x, double bottom) DotPath(int i, double t)
    {
        double x0 = BarX[i], y0 = BarBottom0;
        if (t <= T2)
        {
            double k = Smooth(t / T2);
            return (Lerp(x0, DotLift[i].X, k), Lerp(y0, DotLift[i].Y + 11, k));
        }
        if (t <= T3)
        {
            double k = Smooth((t - T2) / (T3 - T2));
            return (Lerp(DotLift[i].X, DotTrail[i].X, k), Lerp(DotLift[i].Y + 11, DotTrail[i].Y + 11, k));
        }
        double k2 = Smooth((t - T3) / (1 - T3));
        return (Lerp(DotTrail[i].X, DotSmile[i].X, k2), Lerp(DotTrail[i].Y + 11, DotSmile[i].Y + 11, k2));
    }

    // ---- battery tint: charging green, <=50% yellow, <20% red -----------------

    private static readonly System.Windows.Media.Color ChargingGreen =
        System.Windows.Media.Color.FromRgb(0x22, 0xC5, 0x5E);
    private static readonly System.Windows.Media.Color LowYellow =
        System.Windows.Media.Color.FromRgb(0xEA, 0xB3, 0x08);
    private static readonly System.Windows.Media.Color CriticalRed =
        System.Windows.Media.Color.FromRgb(0xE5, 0x48, 0x4D);

    private System.Windows.Media.Color BatteryTint()
    {
        if (IsCharging) return ChargingGreen;
        if (BatteryFraction < 0.2) return CriticalRed;
        if (BatteryFraction <= 0.5) return LowYellow;
        return (Foreground as SolidColorBrush)?.Color ?? Colors.Black;
    }

    private void DrawBattery(DrawingContext dc, double t)
    {
        var (cx, cy, w, h, r) = BatteryAt(t);
        double alpha = 1 - Smooth((t - T3) / 0.09);
        if (alpha <= 0.01) return;

        // 充电时电池块为绿色(其他状态保持墨色)
        Brush pillBrush = IsCharging
            ? ChargingBrush(alpha)
            : BrushAt(alpha);
        dc.DrawGeometry(pillBrush, null,
            new RectangleGeometry(new Rect(cx - w / 2, cy - h / 2, w, h), r, r));

        // cap nub, fades out by T1
        double capAlpha = 1 - Smooth(t / T1);
        if (capAlpha > 0.01)
        {
            dc.DrawGeometry(IsCharging ? ChargingBrush(capAlpha) : BrushAt(capAlpha), null,
                new RectangleGeometry(new Rect(BatK0.cx + BatK0.w / 2 + 5, BatK0.cy - 20, 15, 40), 7, 7));
        }
    }

    private static Brush ChargingBrush(double opacity)
    {
        var b = new SolidColorBrush(ChargingGreen) { Opacity = Math.Clamp(opacity, 0, 1) };
        b.Freeze();
        return b;
    }

    private static (double cx, double cy, double w, double h, double r) BatteryAt(double t)
    {
        if (t <= T1)
            return Mix(BatK0, BatK1, Smooth(t / T1));
        if (t <= T2)
            return Mix(BatK1, BatK2, Smooth((t - T1) / (T2 - T1)));
        return Mix(BatK2, BatK3, Smooth((t - T2) / (T3 - T2)));
    }

    private static (double cx, double cy, double w, double h, double r) Mix(
        (double cx, double cy, double w, double h, double r) a,
        (double cx, double cy, double w, double h, double r) b, double k)
        => (Lerp(a.cx, b.cx, k), Lerp(a.cy, b.cy, k), Lerp(a.w, b.w, k),
            Lerp(a.h, b.h, k), Lerp(a.r, b.r, k));

    // ---- the ring: gray track + battery arc, plus the battery % number ----------

    private void DrawRing(DrawingContext dc, double t)
    {
        if (t <= T2 + 0.02) return;

        double grow = Smooth((t - T2 - 0.02) / (1 - T2 - 0.02));
        double fraction = Math.Clamp(BatteryFraction, 0.04, 1);

        double alpha = Smooth((t - T3) / 0.09);

        // Gray track across the full circle, with a gap at the top for the number
        // (design image: the ring line breaks around the number, never covers it).
        var trackPen = new Pen(BrushAt(alpha * 0.25), RingStroke)
        {
            StartLineCap = PenLineCap.Round,
            EndLineCap = PenLineCap.Round,
        };
        trackPen.Freeze();
        double gapHalf = (NumberWidth() / 2 + 14) / RingR * 180 / Math.PI;
        ArcWithGap(trackPen, WifiC, RingR, TrackStartDeg, RingSweepMax, gapHalf, dc);

        // Battery arc on top — ink normally, red when low (design image: red "16").
        double sweep = RingSweepMax * grow * fraction;
        if (sweep > 0.01)
        {
            var tint = BatteryTint();
            var tintBrush = new SolidColorBrush(tint) { Opacity = alpha };
            tintBrush.Freeze();
            var pen = new Pen(tintBrush, RingStroke)
            {
                StartLineCap = PenLineCap.Round,
                EndLineCap = PenLineCap.Round,
            };
            pen.Freeze();
            ArcWithGap(pen, WifiC, RingR, RingStartDeg, sweep, gapHalf, dc);
        }

        // Battery % number inside the top of the ring, Maple Mono CN Bold
        // (design image: "50" / "16"). Other text keeps Comic Sans MS.
        if (BatteryFraction >= 0)
        {
            int percent = (int)Math.Round(fraction * 100);
            if (alpha > 0.01)
            {
                var ft = new System.Windows.Media.FormattedText(
                    percent.ToString(),
                    System.Globalization.CultureInfo.InvariantCulture,
                    FlowDirection.LeftToRight,
                    new Typeface(
                        new FontFamily(new Uri("pack://application:,,,"), "./Resources/#Maple Mono CN"),
                        FontStyles.Normal, FontWeights.Bold, FontStretches.Normal),
                    48, BrushAt(alpha), 1.0);
                // 数字骑在圆环线上:圆环正上方,数字中线与环线重合(设计图)
                double numberCenterY = WifiC.Y - RingR + 4;
                dc.DrawText(ft, new Point(WifiC.X - ft.Width / 2, numberCenterY - ft.Height / 2));
            }
        }
    }

    private double NumberWidth()
    {
        int percent = (int)Math.Round(Math.Clamp(BatteryFraction, 0.04, 1) * 100);
        var ft = new System.Windows.Media.FormattedText(
            percent.ToString(),
            System.Globalization.CultureInfo.InvariantCulture,
            FlowDirection.LeftToRight,
            new Typeface(
                new FontFamily(new Uri("pack://application:,,,"), "./Resources/#Maple Mono CN"),
                FontStyles.Normal, FontWeights.Bold, FontStretches.Normal),
            48, Brushes.Black, 1.0);
        return ft.Width;
    }

    /// <summary>Draw an arc, skipping the top gap [270±gapHalfDeg] reserved for the number.</summary>
    private static void ArcWithGap(Pen pen, Point c, double r,
        double startDeg, double sweepDeg, double gapHalfDeg, DrawingContext dc)
    {
        double gapStart = 270 - gapHalfDeg, gapEnd = 270 + gapHalfDeg;
        double end = startDeg + sweepDeg;

        double a = startDeg;
        if (a < gapStart && end > gapStart)
        {
            dc.DrawGeometry(null, pen, Arc(c, r, a, gapStart - a));
            a = gapEnd;
        }
        if (a < gapEnd) a = gapEnd;
        if (end > a)
            dc.DrawGeometry(null, pen, Arc(c, r, a, end - a));
    }

    // ---- helpers ------------------------------------------------------------------

    private Brush BrushAt(double opacity)
    {
        var b = Foreground.Clone();
        b.Opacity = Math.Clamp(opacity, 0, 1);
        b.Freeze();
        return b;
    }

    private static StreamGeometry Arc(Point center, double radius, double startDeg, double sweepDeg)
    {
        sweepDeg = Math.Clamp(sweepDeg, 0.01, 359.99);
        double a0 = startDeg * Math.PI / 180;
        double a1 = (startDeg + sweepDeg) * Math.PI / 180;
        var from = new Point(center.X + radius * Math.Cos(a0), center.Y + radius * Math.Sin(a0));
        var to = new Point(center.X + radius * Math.Cos(a1), center.Y + radius * Math.Sin(a1));
        var geo = new StreamGeometry();
        using (var ctx = geo.Open())
        {
            ctx.BeginFigure(from, false, false);
            ctx.ArcTo(to, new Size(radius, radius), 0, sweepDeg > 180,
                SweepDirection.Clockwise, true, true);
        }
        geo.Freeze();
        return geo;
    }

    private static double Seg(double t, double t0, double t1, double t2, double v0, double v1, double v2)
    {
        if (t <= t1) return Lerp(v0, v1, Smooth((t - t0) / (t1 - t0)));
        return Lerp(v1, v2, Smooth((t - t1) / (t2 - t1)));
    }

    private static double Lerp(double a, double b, double t) => a + (b - a) * Math.Clamp(t, 0, 1);

    private static double Smooth(double x)
    {
        x = Math.Clamp(x, 0, 1);
        return x * x * (3 - 2 * x);
    }
}
