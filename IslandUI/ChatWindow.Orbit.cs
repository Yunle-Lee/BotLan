using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using IslandUI.Controls;
using IslandUI.Services;

namespace IslandUI;

public partial class ChatWindow
{
    /// <summary>One belt entry: a real Bot, or a "待创建" placeholder (Bot == null) filling an empty slot.</summary>
    private sealed record OrbitItem(AgentBot? Bot, int Slot);

    /// <summary>Opens the Bot configuration window. Replaceable so tests never show a real window.</summary>
    internal static Action OpenBotConfig = AgentConfigWindow.ShowSingleton;

    private readonly List<OrbitItem> _orbitItems = [];
    private readonly Dictionary<OrbitItem, CurvedBotTab> _carouselTabs = [];
    private BotOrbitLayout? _carouselLayout;
    private Rect _carouselCard;
    private bool _updatingOrbit;
    private bool _carouselClosed;
    private double _targetOffset;
    private int _scrollGeneration;

    private static readonly DependencyProperty CarouselOffsetProperty = DependencyProperty.Register(
        nameof(CarouselOffset), typeof(double), typeof(ChatWindow),
        new PropertyMetadata(0d, (d, _) => ((ChatWindow)d).RenderCarousel()));

    private double CarouselOffset
    {
        get => (double)GetValue(CarouselOffsetProperty);
        set => SetValue(CarouselOffsetProperty, value);
    }

    /// <summary>Real Bots first, then placeholders until <see cref="BotOrbitLayout.VisibleTabs"/> slots are filled.
    /// A belt that short is repeated once, so the wheel turns it like a longer one and a tab never
    /// has to leave one end while entering the other.</summary>
    private static List<OrbitItem> BuildOrbitItems(IReadOnlyList<AgentBot> bots)
    {
        var items = bots.Select((bot, i) => new OrbitItem(bot, i)).ToList();
        for (int i = items.Count; i < BotOrbitLayout.VisibleTabs; i++) items.Add(new OrbitItem(null, i));
        int count = items.Count;
        if (count <= BotOrbitLayout.VisibleTabs)
            items.AddRange(items.Select(item => item with { Slot = item.Slot + count }).ToArray());
        return items;
    }

    private void RefreshOrbitBots()
    {
        if (!Dispatcher.CheckAccess())
        {
            Dispatcher.BeginInvoke(RefreshOrbitBots);
            return;
        }
        if (_carouselClosed || _foldPhase != FoldPhase.Expanded) return;

        var bots = AgentStore.Instance.Bots.ToList();
        SyncBotSubscriptions(bots);
        var items = BuildOrbitItems(bots);
        // Selection changes Chat, never the order or position-based brightness of the labels.
        bool changed = !_orbitItems.SequenceEqual(items);
        var card = CardRect;
        bool resized = _carouselCard != card;
        _updatingOrbit = true;
        try
        {
            if (_carouselLayout == null || changed || resized)
            {
                _carouselCard = card;
                if (changed)
                {
                    _scrollGeneration++;
                    BeginAnimation(CarouselOffsetProperty, null);
                    _orbitItems.Clear();
                    _orbitItems.AddRange(items);
                    ClearCarouselTabs();
                }
                // The viewport is defined from the card's upper-right corner, so a resize keeps
                // its size; only the belt behind it is rebuilt.
                _carouselLayout = new BotOrbitLayout(card, _orbitItems.Count);
                OrbitLayer.Width = OrbitFadeLeading.Width = OrbitItems.Width = DesignCanvas.Width;
                OrbitLayer.Height = OrbitFadeLeading.Height = OrbitItems.Height = DesignCanvas.Height;
                OrbitLayer.Clip = _carouselLayout.ViewportClip;
                OrbitWheelSurface.Data = _carouselLayout.ViewportClip;
                OrbitWheelSurface.Visibility = _carouselLayout.Scrolls ? Visibility.Visible : Visibility.Collapsed;

                // Soft ends only where tabs enter and leave; resting tabs stay fully opaque.
                var entry = _carouselLayout.Track.At(_carouselLayout.ViewportStart).Point;
                var exit = _carouselLayout.Track.At(_carouselLayout.ViewportEnd).Point;
                OrbitFadeLeading.OpacityMask = new LinearGradientBrush(Colors.Transparent, Colors.White,
                    new Point(entry.X - 8, 0), new Point(entry.X + BotOrbitLayout.FadeLength, 0))
                    { MappingMode = BrushMappingMode.Absolute };
                OrbitItems.OpacityMask = new LinearGradientBrush(Colors.White, Colors.Transparent,
                    new Point(0, exit.Y - BotOrbitLayout.FadeLength), new Point(0, exit.Y + 8))
                    { MappingMode = BrushMappingMode.Absolute };

                if (changed)
                {
                    int selectedIndex = Math.Max(0, _orbitItems.FindIndex(item => ReferenceEquals(item.Bot, _bot)));
                    _targetOffset = _carouselLayout.NormalizeOffset(-selectedIndex * _carouselLayout.Pitch);
                    CarouselOffset = _targetOffset;
                }
            }
        }
        finally { _updatingOrbit = false; }
        RenderCarousel();
        UpdateEmptyState();
    }

    private void RenderCarousel()
    {
        if (_updatingOrbit || _carouselClosed || _carouselLayout == null || _foldPhase != FoldPhase.Expanded) return;
        var visible = new HashSet<OrbitItem>();
        foreach (var placement in _carouselLayout.VisibleSlots(CarouselOffset))
        {
            var item = _orbitItems[placement.BotIndex];
            visible.Add(item);
            if (!_carouselTabs.TryGetValue(item, out var tab))
            {
                tab = new CurvedBotTab(item.Bot) { Width = DesignCanvas.Width, Height = DesignCanvas.Height };
                tab.MouseLeftButtonUp += OrbitBot_Click;
                _carouselTabs.Add(item, tab);
                OrbitItems.Children.Add(tab);
            }
            else
            {
                tab.Width = DesignCanvas.Width;
                tab.Height = DesignCanvas.Height;
            }
            tab.Place(_carouselLayout, placement.Slot, item.Bot != null && ReferenceEquals(item.Bot, _bot));
        }

        foreach (var item in _carouselTabs.Keys.Where(item => !visible.Contains(item)).ToArray())
        {
            var tab = _carouselTabs[item];
            tab.MouseLeftButtonUp -= OrbitBot_Click;
            OrbitItems.Children.Remove(tab);
            _carouselTabs.Remove(item);
        }
    }

    private void OrbitBot_Click(object sender, MouseButtonEventArgs e)
    {
        if (sender is not CurvedBotTab tab) return;
        e.Handled = true;
        if (tab.IsPlaceholder)
        {
            OpenBotConfig();
            return;
        }
        if (tab.DataContext is AgentBot bot) SelectBot(bot);
    }

    private void Orbit_MouseWheel(object sender, MouseWheelEventArgs e)
    {
        e.Handled = true;
        if (_foldPhase != FoldPhase.Expanded || _carouselLayout is not { Scrolls: true } || e.Delta == 0) return;
        AnimateCarouselTo(_targetOffset + e.Delta / 120.0 * _carouselLayout.Pitch);
    }

    private void AnimateCarouselTo(double target)
    {
        if (_carouselLayout == null || _carouselClosed) return;
        double from = CarouselOffset;
        _targetOffset = target;
        int generation = ++_scrollGeneration;
        var animation = new DoubleAnimation(from, target, TimeSpan.FromMilliseconds(260))
        {
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut },
        };
        animation.Completed += (_, _) =>
        {
            if (generation != _scrollGeneration || _carouselClosed || _carouselLayout == null) return;
            // Normalize after each turn without changing visible positions.
            _updatingOrbit = true;
            double normalized = _carouselLayout.NormalizeOffset(target);
            BeginAnimation(CarouselOffsetProperty, null);
            CarouselOffset = normalized;
            _targetOffset = normalized;
            _updatingOrbit = false;
            RenderCarousel();
            SelectCarouselBotAtCorner();
        };
        BeginAnimation(CarouselOffsetProperty, animation, HandoffBehavior.SnapshotAndReplace);
    }

    private void SelectCarouselBotAtCorner()
    {
        if (_carouselClosed || _carouselLayout == null || _orbitItems.Count == 0) return;
        // Only commit selection when the latest movement settles. Partial wheel detents choose
        // the nearest corner tab; placeholders are never selected.
        var closest = _carouselLayout.VisibleSlots(CarouselOffset)
            .Where(placement => _orbitItems[placement.BotIndex].Bot != null)
            .OrderBy(placement => Math.Abs(placement.Slot.AvatarAt - _carouselLayout.FocusDistance))
            .Select(placement => _orbitItems[placement.BotIndex].Bot)
            .FirstOrDefault();
        if (closest != null) SelectBot(closest);
    }

    private void RevealCarouselBot(AgentBot bot)
    {
        if (_carouselLayout is not { Scrolls: true }) return;
        // A repeated short belt holds the Bot twice; turn to whichever copy is nearer.
        double cycle = _carouselLayout.CycleLength;
        double? target = null;
        for (int index = 0; index < _orbitItems.Count; index++)
        {
            if (!ReferenceEquals(_orbitItems[index].Bot, bot)) continue;
            double candidate = -index * _carouselLayout.Pitch;
            candidate += Math.Round((CarouselOffset - candidate) / cycle) * cycle;
            if (target == null || Math.Abs(candidate - CarouselOffset) < Math.Abs(target.Value - CarouselOffset))
                target = candidate;
        }
        if (target == null) return;
        AnimateCarouselTo(target.Value);
    }

    private void ClearCarouselTabs()
    {
        foreach (var tab in _carouselTabs.Values) tab.MouseLeftButtonUp -= OrbitBot_Click;
        OrbitItems.Children.Clear();
        _carouselTabs.Clear();
    }

    private void DisposeCarousel()
    {
        _carouselClosed = true;
        _scrollGeneration++;
        BeginAnimation(CarouselOffsetProperty, null);
        ClearCarouselTabs();
    }
}
