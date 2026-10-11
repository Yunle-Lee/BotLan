using System.Windows;
using System.Windows.Media;

namespace IslandUI.Controls;

internal readonly record struct OrbitPose(Point Point, Vector Tangent);
internal readonly record struct OrbitTabSlot(double Start, double Length, double AvatarAt);

/// <summary>
/// A fixed upper-right viewport onto a cyclic belt of equal-width Bot tabs. The viewport is
/// defined relative to the card's upper-right corner only, so it keeps its exact size and
/// shape when the card is resized. At rest it shows exactly <see cref="VisibleTabs"/> tabs:
/// one before the corner, one at the corner and one after it.
/// </summary>
internal sealed class BotOrbitLayout
{
    public const double CardRadius = 44;
    public const double CardGap = 16;
    public const int VisibleTabs = 3;
    public double BandWidth => 64;
    public double OutlineWidth => 3;
    public double TabLength => 224;
    public double Gap => 16;
    /// <summary>Number of belt items (Bots plus "待创建" placeholders).</summary>
    public int BotCount { get; }
    public RoundedOrbit Track { get; }
    public double ViewportStart { get; }
    public double ViewportEnd { get; }
    public double FocusDistance => Track.CornerCenters[1];
    /// <summary>Constant spacing: a tab, its two round caps and one gap.</summary>
    public double Pitch => TabLength + BandWidth + Gap;
    public double CycleLength => Math.Max(1, BotCount) * Pitch;
    /// <summary>A belt with more items than visible slots can turn without a tab jumping ends.</summary>
    public bool Scrolls => BotCount > VisibleTabs;
    public PathGeometry ViewportClip { get; }

    /// <summary>Length of the fade at each end of the viewport, beyond the resting tabs.</summary>
    public const double FadeLength = 16;
    /// <summary>Half the viewport length: the neighbour tab, its round cap and the fade.</summary>
    public double ViewportHalf => Pitch + TabLength / 2 + BandWidth / 2 + FadeLength;

    /// <summary>Smallest card for which the viewport stays on the top and right straights.</summary>
    public static Size MinimumCard
    {
        get
        {
            double offset = CardGap + 64 / 2.0, radius = CardRadius + offset;
            double straight = (224 + 64 + 16) + 224 / 2.0 + 64 / 2.0 + FadeLength - radius * Math.PI / 4;
            double side = Math.Ceiling(straight + radius * 2 - offset * 2);
            return new Size(side, side);
        }
    }

    public BotOrbitLayout(Rect card, int count)
    {
        BotCount = count;
        double offset = CardGap + BandWidth / 2;
        var bounds = card;
        bounds.Inflate(offset, offset);
        Track = new RoundedOrbit(bounds, CardRadius + offset);
        ViewportStart = FocusDistance - ViewportHalf;
        ViewportEnd = FocusDistance + ViewportHalf;
        ViewportClip = Widen(Track.Slice(ViewportStart, ViewportEnd - ViewportStart), BandWidth + 12);
    }

    public IEnumerable<(int BotIndex, OrbitTabSlot Slot)> VisibleSlots(double offset)
    {
        if (BotCount == 0) yield break;
        offset = NormalizeOffset(offset);
        // Only slots whose body (tab plus round caps) can reach the viewport.
        double margin = TabLength / 2 + BandWidth / 2 + 2;
        long first = (long)Math.Ceiling((ViewportStart - margin - FocusDistance - offset) / Pitch);
        long last = (long)Math.Floor((ViewportEnd + margin - FocusDistance - offset) / Pitch);
        // With fewer items than slots the same item could appear twice; keep the one nearest the corner.
        var nearest = new Dictionary<int, double>();
        for (long ordinal = first; ordinal <= last; ordinal++)
        {
            int botIndex = (int)((ordinal % BotCount + BotCount) % BotCount);
            double center = ordinal * Pitch + offset;
            if (!nearest.TryGetValue(botIndex, out var existing) || Math.Abs(center) < Math.Abs(existing))
                nearest[botIndex] = center;
        }
        foreach (var (botIndex, center) in nearest.OrderBy(pair => pair.Value))
            yield return (botIndex, SlotAt(FocusDistance + center));
    }

    private OrbitTabSlot SlotAt(double center) => new(center - TabLength / 2, TabLength, center);

    public double NormalizeOffset(double offset)
    {
        double result = (offset % CycleLength + CycleLength) % CycleLength;
        return Math.Abs(result - CycleLength) < 0.000001 || result < 0.000001 ? 0 : result;
    }

    public PathGeometry Body(OrbitTabSlot slot)
        => Widen(Track.Slice(slot.Start, slot.Length), BandWidth - OutlineWidth);

    private static PathGeometry Widen(PathGeometry centerline, double width)
    {
        var pen = new Pen(Brushes.Black, width)
        {
            StartLineCap = PenLineCap.Round,
            EndLineCap = PenLineCap.Round,
            LineJoin = PenLineJoin.Round,
        };
        pen.Freeze();
        var body = centerline.GetWidenedPathGeometry(pen, 0.04, ToleranceType.Absolute);
        body.Freeze();
        return body;
    }
}

/// <summary>Clockwise line/arc geometry, with exact tangent sampling and slicing.</summary>
internal sealed class RoundedOrbit
{
    private readonly record struct Segment(Point Start, Point End, Point Center,
        double Radius, double Angle, double Length)
    {
        public OrbitPose At(double distance)
        {
            double t = Math.Clamp(distance / Length, 0, 1);
            if (Radius == 0)
            {
                var tangent = End - Start;
                tangent.Normalize();
                return new OrbitPose(Start + (End - Start) * t, tangent);
            }
            double a = Angle + t * Math.PI / 2;
            return new OrbitPose(new Point(Center.X + Radius * Math.Cos(a),
                Center.Y + Radius * Math.Sin(a)), new Vector(-Math.Sin(a), Math.Cos(a)));
        }
    }

    private readonly Segment[] _segments;
    public double Length { get; }
    public double ArcLength { get; }
    public double[] CornerCenters { get; } // top-left, top-right, bottom-right, bottom-left
    public double[] StraightStarts { get; }
    public double[] StraightLengths { get; }

    public RoundedOrbit(Rect bounds, double radius)
    {
        double l = bounds.Left, t = bounds.Top, r = bounds.Right, b = bounds.Bottom;
        double h = bounds.Width - radius * 2, v = bounds.Height - radius * 2;
        ArcLength = radius * Math.PI / 2;
        Segment Line(Point a, Point z) => new(a, z, default, 0, 0, (z - a).Length);
        Segment Arc(Point c, double angle) => new(default, default, c, radius, angle, ArcLength);
        _segments =
        [
            Line(new(l + radius, t), new(r - radius, t)),
            Arc(new(r - radius, t + radius), -Math.PI / 2),
            Line(new(r, t + radius), new(r, b - radius)),
            Arc(new(r - radius, b - radius), 0),
            Line(new(r - radius, b), new(l + radius, b)),
            Arc(new(l + radius, b - radius), Math.PI / 2),
            Line(new(l, b - radius), new(l, t + radius)),
            Arc(new(l + radius, t + radius), Math.PI),
        ];
        Length = _segments.Sum(s => s.Length);
        StraightLengths = [h, v, h, v];
        StraightStarts = [0, h + ArcLength, h + v + ArcLength * 2,
            h * 2 + v + ArcLength * 3];
        CornerCenters = [Length - ArcLength / 2, h + ArcLength / 2,
            h + v + ArcLength * 1.5, h * 2 + v + ArcLength * 2.5];
    }

    public double Normalize(double distance) => (distance % Length + Length) % Length;

    private (int Index, double Local) Locate(double distance)
    {
        distance = Normalize(distance);
        for (int i = 0; i < _segments.Length; i++)
        {
            if (distance < _segments[i].Length - 0.000001 || i == _segments.Length - 1)
                return (i, distance);
            distance -= _segments[i].Length;
        }
        return (0, 0);
    }

    public OrbitPose At(double distance)
    {
        var (index, local) = Locate(distance);
        return _segments[index].At(local);
    }

    public PathGeometry Slice(double start, double length)
    {
        var figure = new PathFigure { StartPoint = At(start).Point, IsFilled = false };
        var (index, local) = Locate(start);
        double remaining = length;
        while (remaining > 0.00001)
        {
            var segment = _segments[index];
            double take = Math.Min(remaining, segment.Length - local);
            var end = segment.At(local + take).Point;
            if (segment.Radius == 0) figure.Segments.Add(new LineSegment(end, true));
            else figure.Segments.Add(new ArcSegment(end, new Size(segment.Radius, segment.Radius),
                0, false, SweepDirection.Clockwise, true));
            remaining -= take;
            local = 0;
            index = (index + 1) % _segments.Length;
        }
        var geometry = new PathGeometry([figure]);
        geometry.Freeze();
        return geometry;
    }
}
