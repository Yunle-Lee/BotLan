using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Globalization;
using IslandUI.Bots;
using IslandUI.Services;
using ShapePath = System.Windows.Shapes.Path;

namespace IslandUI.Controls;

/// <summary>A reusable Bot tab whose silhouette and contents bend as it moves along the belt.</summary>
internal sealed class CurvedBotTab : Canvas
{
    private readonly AgentBot _bot;
    private readonly ShapePath _surface;
    private readonly ShapePath _neutralWave;
    private readonly ShapePath _accentWave;
    private readonly ShapePath _nameLabel;
    private readonly List<(Geometry Geometry, double Advance)> _nameGlyphs = [];
    private double _nameWidth;
    private double _nameHeight;
    private double _labelDpi;
    private readonly Border _avatar;
    private readonly BotView _botView;
    private OrbitTabSlot? _lastSlot;
    private bool _lastWorking;
    private bool _pulsing;
    private string? _lastName;
    public bool IsSelected { get; private set; }

    public CurvedBotTab(AgentBot bot)
    {
        _bot = bot;
        DataContext = bot;
        Cursor = Cursors.Hand;
        _surface = new ShapePath
        {
            Name = "TabSurface",
            StrokeLineJoin = PenLineJoin.Round,
            Stretch = Stretch.None,
        };
        _neutralWave = Wave(ComicTheme.Current.Muted, 0.62);
        _accentWave = Wave(ComicTheme.Current.BlueDark, 0.65);
        _nameLabel = new ShapePath { Name = "BotNameLabel", Stretch = Stretch.None, IsHitTestVisible = false };
        var bodyBrush = new SolidColorBrush((Color)ColorConverter.ConvertFromString(bot.Config.ColorHex));
        bodyBrush.Freeze();
        _botView = new BotView
        {
            Margin = new Thickness(2.5),
            BodyColor = bodyBrush,
            Shape = bot.Config.BotShape,
            Expression = bot.Config.BotExpression,
            GazeYaw = -12,
            GazePitch = 4,
            Liveliness = false,
        };
        _avatar = new Border
        {
            Background = ComicTheme.Current.SkyTint,
            BorderThickness = new Thickness(1.6),
            IsHitTestVisible = false,
            Child = _botView,
        };
        Children.Add(_surface);
        Children.Add(_nameLabel);
        Children.Add(_neutralWave);
        Children.Add(_accentWave);
        Children.Add(_avatar);
        Loaded += (_, _) => UpdatePulse();
        Unloaded += (_, _) =>
        {
            _accentWave.BeginAnimation(OpacityProperty, null);
            _pulsing = false;
        };
    }

    public void Place(BotOrbitLayout layout, OrbitTabSlot slot, bool selected)
    {
        if (IsSelected != selected || _lastName != _bot.Config.Name)
        {
            if (_lastName != _bot.Config.Name) _nameGlyphs.Clear();
            _lastName = _bot.Config.Name;
            ToolTip = selected ? _lastName + " · 当前对话" : "切换到 " + _lastName;
            System.Windows.Automation.AutomationProperties.SetName(this, _lastName);
        }
        IsSelected = selected;
        _botView.State = _bot.BotStateName;
        ApplyStyle(layout, slot);
        UpdatePulse();
        double dpi = VisualTreeHelper.GetDpi(this).PixelsPerDip;
        bool labelChanged = _nameGlyphs.Count == 0 || _labelDpi != dpi;
        if (labelChanged) MeasureName(slot.AvatarAt - slot.Start - 42, dpi);
        _nameLabel.Fill = ComicTheme.Current.Ink;
        if (_lastSlot == slot && _lastWorking == _bot.IsWorking && !labelChanged) return;
        _lastSlot = slot;
        _lastWorking = _bot.IsWorking;
        _surface.Data = layout.Body(slot);
        _surface.StrokeThickness = layout.OutlineWidth;

        double size = 46;
        _avatar.Width = _avatar.Height = size;
        _avatar.CornerRadius = new CornerRadius(size / 2);
        var center = layout.Track.At(slot.AvatarAt).Point;
        SetLeft(_avatar, center.X - size / 2);
        SetTop(_avatar, center.Y - size / 2);

        // Text occupies the leading (left on the top straight) end of each tab.
        // Each glyph follows the same tangent as the lane, including around its corner.
        var label = new GeometryGroup();
        double textAt = slot.Start + 4;
        foreach (var glyph in _nameGlyphs)
        {
            var pose = layout.Track.At(textAt + glyph.Advance / 2);
            var matrix = new Matrix();
            matrix.Translate(-glyph.Advance / 2, -_nameHeight / 2);
            matrix.Rotate(Math.Atan2(pose.Tangent.Y, pose.Tangent.X) * 180 / Math.PI);
            matrix.Translate(pose.Point.X, pose.Point.Y);
            label.Children.Add(new GeometryGroup
            {
                Children = new GeometryCollection { glyph.Geometry },
                Transform = new MatrixTransform(matrix),
            });
            textAt += glyph.Advance;
        }
        label.Freeze();
        _nameLabel.Data = label;

        var neutral = new GeometryGroup();
        var accent = new GeometryGroup();
        double[] heights = [10, 20, 15, 29, 22, 35, 18, 27, 14, 31, 24, 16];
        for (int i = 0; i * 8 + 6 <= slot.Length - 6; i++)
        {
            double distance = slot.Start + i * 8 + 6;
            if (distance < slot.Start + 4 + _nameWidth + 10) continue;
            if (Math.Abs(distance - slot.AvatarAt) < size / 2 + 12) continue;
            var pose = layout.Track.At(distance);
            var normal = new Vector(-pose.Tangent.Y, pose.Tangent.X);
            double half = heights[i % heights.Length] / 2 * (_bot.IsWorking ? 1.1 : 0.88);
            var line = new LineGeometry(pose.Point - normal * half, pose.Point + normal * half);
            (i % 3 == 0 ? accent : neutral).Children.Add(line);
        }
        neutral.Freeze();
        accent.Freeze();
        _neutralWave.Data = neutral;
        _accentWave.Data = accent;
    }

    private void MeasureName(double available, double dpi)
    {
        _labelDpi = dpi;
        _nameGlyphs.Clear();
        var typeface = new Typeface(new FontFamily("Segoe UI, Microsoft YaHei UI"),
            FontStyles.Normal, FontWeights.SemiBold, FontStretches.Normal);
        FormattedText Format(string text) => new(text, CultureInfo.CurrentUICulture,
            FlowDirection.LeftToRight, typeface, 13, Brushes.Black, dpi);
        var parts = new List<FormattedText>();
        var elements = StringInfo.GetTextElementEnumerator(string.IsNullOrWhiteSpace(_lastName) ? "Bot" : _lastName);
        double width = 0;
        bool truncated = false;
        while (elements.MoveNext())
        {
            var part = Format(elements.GetTextElement());
            if (width + part.WidthIncludingTrailingWhitespace > available) { truncated = true; break; }
            parts.Add(part);
            width += part.WidthIncludingTrailingWhitespace;
        }
        if (truncated)
        {
            var ellipsis = Format("…");
            while (parts.Count > 0 && width + ellipsis.Width > available)
            {
                width -= parts[^1].WidthIncludingTrailingWhitespace;
                parts.RemoveAt(parts.Count - 1);
            }
            parts.Add(ellipsis);
        }
        _nameWidth = 0;
        _nameHeight = parts.Max(p => p.Height);
        foreach (var part in parts)
        {
            var geometry = part.BuildGeometry(new Point());
            geometry.Freeze();
            _nameGlyphs.Add((geometry, part.WidthIncludingTrailingWhitespace));
            _nameWidth += part.WidthIncludingTrailingWhitespace;
        }
    }

    private void ApplyStyle(BotOrbitLayout layout, OrbitTabSlot slot)
    {
        // Brightness belongs to the corner, not to the selected Bot. Use a fixed
        // falloff distance so sparse Bot lists do not widen the bright region.
        double fadeDistance = layout.TabLength + layout.BandWidth + layout.Gap;
        double t = Math.Clamp(Math.Abs(slot.AvatarAt - layout.FocusDistance) / fadeDistance, 0, 1);
        double brightness = 1 - t * t * (3 - 2 * t);
        Opacity = 0.38 + 0.62 * brightness;

        var dim = ((SolidColorBrush)ComicTheme.Current.CardSoft).Color;
        var bright = ((SolidColorBrush)ComicTheme.Current.Card).Color;
        var color = Color.FromRgb(
            (byte)Math.Round(dim.R + (bright.R - dim.R) * brightness),
            (byte)Math.Round(dim.G + (bright.G - dim.G) * brightness),
            (byte)Math.Round(dim.B + (bright.B - dim.B) * brightness));
        if (_surface.Fill is not SolidColorBrush currentFill || currentFill.Color != color)
        {
            var fill = new SolidColorBrush(color);
            fill.Freeze();
            _surface.Fill = fill;
        }
        _surface.Stroke = ComicTheme.Current.Muted;
        _avatar.BorderBrush = ComicTheme.Current.Muted;
    }

    private void UpdatePulse()
    {
        if (!IsLoaded) return;
        bool working = _bot.IsWorking;
        if (working == _pulsing) return;
        _pulsing = working;
        _accentWave.BeginAnimation(OpacityProperty, working
            ? new DoubleAnimation(0.45, 1, TimeSpan.FromMilliseconds(550))
                { AutoReverse = true, RepeatBehavior = RepeatBehavior.Forever }
            : null);
    }

    private static ShapePath Wave(Brush brush, double opacity) => new()
    {
        Stroke = brush,
        StrokeThickness = 3.2,
        StrokeStartLineCap = PenLineCap.Round,
        StrokeEndLineCap = PenLineCap.Round,
        Opacity = opacity,
        Stretch = Stretch.None,
        IsHitTestVisible = false,
    };
}
