using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;
using IslandUI.Controls;

namespace IslandUI;

public partial class ChatWindow
{
    // Dragging the card's edges changes the card's real width and height (free aspect).
    // Nothing is scaled: border, corner radius, header, rail and the page keep their size;
    // the page simply gets more or less room and reflows.
    internal const double DefaultCardWidth = 716;
    internal const double DefaultCardHeight = 540;
    private const double MinCardWidth = 640;    // page keeps its docks/composer apart (web breakpoints 600–900)
    private const double MinCardHeight = 480;   // raised to the rail's own minimum (BotOrbitLayout.MinimumCard)
    private const double GripOutside = 6, GripInside = 12, GripCorner = 44;

    // Fixed window margins around the card: grips + drop shadow on the left/bottom, the rail
    // band (gap + band + clip slack) on the top/right where the rail rides the corner.
    private const double RailMargin = BotOrbitLayout.CardGap + 64 / 2.0 + (64 + 12) / 2.0 + 4;
    private const double CardMarginLeft = 18, CardMarginBottom = 22;
    private const double CardMarginTop = RailMargin, CardMarginRight = RailMargin;

    private Size _cardSize = new(DefaultCardWidth, DefaultCardHeight);
    // Fully transparent pixels of a layered window are click-through; alpha 1 keeps the grips hittable.
    private static readonly Brush GripFill = Freeze(new SolidColorBrush(Color.FromArgb(1, 0, 0, 0)));
    private static Brush Freeze(Brush brush) { brush.Freeze(); return brush; }
    private readonly List<Rectangle> _grips = [];
    private FrameworkElement? _activeGrip;
    private ResizeEdges _resizeEdges;
    private Rect _resizeStartCard;     // screen DIPs
    private Point _resizeStartMouse;

    [Flags]
    private enum ResizeEdges { None = 0, Left = 1, Top = 2, Right = 4, Bottom = 8 }

    /// <summary>The card inside this window (window DIPs). Its position never changes; only its size.</summary>
    private Rect CardRect => new(CardMarginLeft, CardMarginTop, _cardSize.Width, _cardSize.Height);

    private static Size MinCardSize => new(
        Math.Max(MinCardWidth, BotOrbitLayout.MinimumCard.Width),
        Math.Max(MinCardHeight, BotOrbitLayout.MinimumCard.Height));

    /// <summary>Largest card whose window (card + margins) still fits the work area.</summary>
    private static Size MaxCardSize
    {
        get
        {
            var area = SystemParameters.WorkArea;
            var min = MinCardSize;
            return new Size(Math.Max(min.Width, Math.Floor(area.Width - CardMarginLeft - CardMarginRight)),
                Math.Max(min.Height, Math.Floor(area.Height - CardMarginTop - CardMarginBottom)));
        }
    }

    private static Size ClampCardSize(Size size)
    {
        var (min, max) = (MinCardSize, MaxCardSize);
        double w = double.IsFinite(size.Width) && size.Width > 0 ? size.Width : DefaultCardWidth;
        double h = double.IsFinite(size.Height) && size.Height > 0 ? size.Height : DefaultCardHeight;
        return new Size(Math.Clamp(Math.Round(w), min.Width, max.Width), Math.Clamp(Math.Round(h), min.Height, max.Height));
    }

    private static Size WindowSizeFor(Size card)
        => new(card.Width + CardMarginLeft + CardMarginRight, card.Height + CardMarginTop + CardMarginBottom);

    /// <summary>Lays the card, grips, rail and page out for <paramref name="size"/> (no window move).</summary>
    private void ApplyCardSize(Size size)
    {
        _cardSize = size;
        var window = WindowSizeFor(size);
        DesignCanvas.Width = window.Width;
        DesignCanvas.Height = window.Height;
        var card = CardRect;
        Canvas.SetLeft(ChatCard, card.Left);
        Canvas.SetTop(ChatCard, card.Top);
        ChatCard.Width = card.Width;
        ChatCard.Height = card.Height;
        ChatCard.CornerRadius = new CornerRadius(BotOrbitLayout.CardRadius);
        PositionGrips();
        RefreshOrbitBots();
        SyncStudioBounds();
    }

    private void CreateResizeGrips()
    {
        void Add(ResizeEdges edges, Cursor cursor)
        {
            var grip = new Rectangle { Fill = GripFill, Cursor = cursor, Tag = edges };
            grip.MouseLeftButtonDown += Grip_MouseLeftButtonDown;
            grip.MouseMove += Grip_MouseMove;
            grip.MouseLeftButtonUp += (_, _) => EndChatResize();
            grip.LostMouseCapture += (_, _) => EndChatResize();
            DesignCanvas.Children.Add(grip);
            _grips.Add(grip);
        }
        Add(ResizeEdges.Left, Cursors.SizeWE);
        Add(ResizeEdges.Right, Cursors.SizeWE);
        Add(ResizeEdges.Top, Cursors.SizeNS);
        Add(ResizeEdges.Bottom, Cursors.SizeNS);
        // Corners last so they sit above the edges.
        Add(ResizeEdges.Left | ResizeEdges.Top, Cursors.SizeNWSE);
        Add(ResizeEdges.Right | ResizeEdges.Bottom, Cursors.SizeNWSE);
        Add(ResizeEdges.Right | ResizeEdges.Top, Cursors.SizeNESW);
        Add(ResizeEdges.Left | ResizeEdges.Bottom, Cursors.SizeNESW);
    }

    private void PositionGrips()
    {
        var card = CardRect;
        double band = GripOutside + GripInside;
        double l = card.Left - GripOutside, t = card.Top - GripOutside;
        double r = card.Right - GripInside, b = card.Bottom - GripInside;
        double w = card.Width + GripOutside * 2, h = card.Height + GripOutside * 2;
        double cr = card.Right + GripOutside - GripCorner, cb = card.Bottom + GripOutside - GripCorner;
        foreach (var grip in _grips)
        {
            var (x, y, gw, gh) = (ResizeEdges)grip.Tag switch
            {
                ResizeEdges.Left => (l, t, band, h),
                ResizeEdges.Right => (r, t, band, h),
                ResizeEdges.Top => (l, t, w, band),
                ResizeEdges.Bottom => (l, b, w, band),
                ResizeEdges.Left | ResizeEdges.Top => (l, t, GripCorner, GripCorner),
                ResizeEdges.Right | ResizeEdges.Bottom => (cr, cb, GripCorner, GripCorner),
                ResizeEdges.Right | ResizeEdges.Top => (cr, t, GripCorner, GripCorner),
                _ => (l, cb, GripCorner, GripCorner),
            };
            Canvas.SetLeft(grip, x); Canvas.SetTop(grip, y);
            grip.Width = gw; grip.Height = gh;
        }
    }

    private void Grip_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (sender is not FrameworkElement { Tag: ResizeEdges edges } grip || _foldPhase != FoldPhase.Expanded) return;
        e.Handled = true;
        StartChatResize(edges, CursorScreenDip());
        _activeGrip = grip;
        grip.CaptureMouse();
    }

    private void Grip_MouseMove(object sender, MouseEventArgs e)
    {
        if (_activeGrip == null || !ReferenceEquals(sender, _activeGrip)) return;
        ResizeChatTo(CursorScreenDip());
    }

    /// <summary>The edge (or corner) opposite the dragged one stays put on screen.</summary>
    private void StartChatResize(ResizeEdges edges, Point mouse)
    {
        _resizeEdges = edges;
        _resizeStartMouse = mouse;
        _resizeStartCard = new Rect(Left + CardMarginLeft, Top + CardMarginTop, _cardSize.Width, _cardSize.Height);
    }

    private void ResizeChatTo(Point mouse)
    {
        if (_resizeEdges == ResizeEdges.None) return;
        var start = _resizeStartCard;
        var delta = mouse - _resizeStartMouse;
        var area = SystemParameters.WorkArea;
        var (min, max) = (MinCardSize, MaxCardSize);
        // A moving edge never pushes the window past the work area (unless it already was).
        static double Limit(double startSize, double room) => Math.Max(startSize, room);
        double l = start.Left, t = start.Top, r = start.Right, b = start.Bottom;
        if (_resizeEdges.HasFlag(ResizeEdges.Left))
        {
            double most = Math.Min(max.Width, Limit(start.Width, r - (area.Left + CardMarginLeft)));
            l = r - Math.Clamp(start.Width - delta.X, min.Width, Math.Max(min.Width, most));
        }
        else if (_resizeEdges.HasFlag(ResizeEdges.Right))
        {
            double most = Math.Min(max.Width, Limit(start.Width, area.Right - CardMarginRight - l));
            r = l + Math.Clamp(start.Width + delta.X, min.Width, Math.Max(min.Width, most));
        }
        if (_resizeEdges.HasFlag(ResizeEdges.Top))
        {
            double most = Math.Min(max.Height, Limit(start.Height, b - (area.Top + CardMarginTop)));
            t = b - Math.Clamp(start.Height - delta.Y, min.Height, Math.Max(min.Height, most));
        }
        else if (_resizeEdges.HasFlag(ResizeEdges.Bottom))
        {
            double most = Math.Min(max.Height, Limit(start.Height, area.Bottom - CardMarginBottom - t));
            b = t + Math.Clamp(start.Height + delta.Y, min.Height, Math.Max(min.Height, most));
        }
        var size = new Size(Math.Round(r - l), Math.Round(b - t));
        if (Math.Abs(size.Width - _cardSize.Width) < .5 && Math.Abs(size.Height - _cardSize.Height) < .5) return;
        // Keep the anchored edge exact after rounding.
        if (_resizeEdges.HasFlag(ResizeEdges.Left)) l = r - size.Width;
        if (_resizeEdges.HasFlag(ResizeEdges.Top)) t = b - size.Height;
        ApplyCardSize(size);
        var window = WindowSizeFor(size);
        // Move + resize the HWND in one step, in the same frame as the new layout.
        SetWindowBounds(l - CardMarginLeft, t - CardMarginTop, window.Width, window.Height);
        UpdateLayout();
    }

    private void EndChatResize()
    {
        if (_resizeEdges == ResizeEdges.None) return;
        _resizeEdges = ResizeEdges.None;
        var grip = _activeGrip;
        _activeGrip = null;
        if (grip?.IsMouseCaptured == true) grip.ReleaseMouseCapture();
        IslandSettings.Instance.SetChatSize(_cardSize.Width, _cardSize.Height);
    }
}
