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
    private readonly List<AgentBot> _orbitBots = [];
    private readonly Dictionary<AgentBot, CurvedBotTab> _carouselTabs = [];
    private BotOrbitLayout? _carouselLayout;
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
        // Selection changes Chat, never the order or position-based brightness of the labels.
        bool changed = !_orbitBots.SequenceEqual(bots);
        _updatingOrbit = true;
        try
        {
            var card = new Rect((DesignWidth - BaseCardWidth) / 2 + BaseCardOffsetX,
                (DesignHeight - BaseCardHeight) / 2 + BaseCardOffsetY,
                BaseCardWidth, BaseCardHeight);
            ChatCard.Width = card.Width;
            ChatCard.Height = card.Height;
            ChatCard.CornerRadius = new CornerRadius(BotOrbitLayout.CardRadius);
            Canvas.SetLeft(ChatCard, card.Left);
            Canvas.SetTop(ChatCard, card.Top);

            if (_carouselLayout == null || changed)
            {
                _scrollGeneration++;
                BeginAnimation(CarouselOffsetProperty, null);
                _orbitBots.Clear();
                _orbitBots.AddRange(bots);
                ClearCarouselTabs();
                _carouselLayout = new BotOrbitLayout(card, bots.Count);
                OrbitLayer.Clip = _carouselLayout.ViewportClip;
                OrbitWheelSurface.Data = _carouselLayout.ViewportClip;
                OrbitWheelSurface.Visibility = bots.Count > 1 ? Visibility.Visible : Visibility.Collapsed;

                var entry = _carouselLayout.Track.At(_carouselLayout.ViewportStart).Point;
                var exit = _carouselLayout.Track.At(_carouselLayout.ViewportEnd).Point;
                OrbitFadeLeading.OpacityMask = new LinearGradientBrush(Colors.Transparent, Colors.White,
                    new Point(entry.X - 38, 0), new Point(entry.X + 30, 0))
                    { MappingMode = BrushMappingMode.Absolute };
                OrbitItems.OpacityMask = new LinearGradientBrush(Colors.White, Colors.Transparent,
                    new Point(0, exit.Y - 30), new Point(0, exit.Y + 38))
                    { MappingMode = BrushMappingMode.Absolute };

                int selectedIndex = Math.Max(0, _orbitBots.IndexOf(_bot));
                _targetOffset = -selectedIndex * _carouselLayout.Pitch;
                CarouselOffset = _targetOffset;
            }
        }
        finally { _updatingOrbit = false; }
        RenderCarousel();
        UpdateEmptyState();
    }

    private void RenderCarousel()
    {
        if (_updatingOrbit || _carouselClosed || _carouselLayout == null || _foldPhase != FoldPhase.Expanded) return;
        var visible = new HashSet<AgentBot>();
        foreach (var placement in _carouselLayout.VisibleSlots(CarouselOffset))
        {
            var bot = _orbitBots[placement.BotIndex];
            visible.Add(bot);
            if (!_carouselTabs.TryGetValue(bot, out var tab))
            {
                tab = new CurvedBotTab(bot) { Width = DesignWidth, Height = DesignHeight };
                tab.MouseLeftButtonUp += OrbitBot_Click;
                _carouselTabs.Add(bot, tab);
                OrbitItems.Children.Add(tab);
            }
            tab.Place(_carouselLayout, placement.Slot, ReferenceEquals(bot, _bot));
        }

        foreach (var bot in _carouselTabs.Keys.Where(bot => !visible.Contains(bot)).ToArray())
        {
            var tab = _carouselTabs[bot];
            tab.MouseLeftButtonUp -= OrbitBot_Click;
            OrbitItems.Children.Remove(tab);
            _carouselTabs.Remove(bot);
        }
    }

    private void Orbit_MouseWheel(object sender, MouseWheelEventArgs e)
    {
        e.Handled = true;
        if (_foldPhase != FoldPhase.Expanded || _carouselLayout == null || _orbitBots.Count < 2 || e.Delta == 0) return;
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
            if (generation != _scrollGeneration || _carouselClosed) return;
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
        if (_carouselClosed || _carouselLayout == null || _orbitBots.Count == 0) return;
        // The existing belt is unchanged: only commit selection when its latest
        // movement settles. Partial wheel detents choose the nearest corner tab.
        var closest = _carouselLayout.VisibleSlots(CarouselOffset)
            .MinBy(placement => Math.Abs(placement.Slot.AvatarAt - _carouselLayout.FocusDistance));
        SelectBot(_orbitBots[closest.BotIndex]);
    }

    private void RevealCarouselBot(AgentBot bot)
    {
        if (_carouselLayout == null || _orbitBots.Count < 2) return;
        int index = _orbitBots.IndexOf(bot);
        if (index < 0) return;
        double target = -index * _carouselLayout.Pitch;
        double cycle = _carouselLayout.CycleLength;
        target += Math.Round((CarouselOffset - target) / cycle) * cycle;
        AnimateCarouselTo(target);
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
