using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Shapes;
using System.Windows.Threading;
using IslandUI.Services;

namespace IslandUI;

/// <summary>
/// The long island (islandUI.png): a vertical capsule at the right edge with
/// the agent bots stacked inside. A bot with something to say pops a speech
/// bubble out to the left of the rail.
/// </summary>
public partial class LongIslandWindow : Window
{
    internal bool IsDisposed { get; private set; }
    private readonly DispatcherTimer _bubbleTimer;
    private DispatcherTimer? _topmostTimer;
    private readonly Dictionary<string, Border> _bubbles = [];
    private readonly Dictionary<string, DispatcherTimer> _bubbleFades = [];
    private readonly HashSet<AgentBot> _subscribedBots = [];

    public LongIslandWindow()
    {
        InitializeComponent();
        PositionAtEdge();

        AgentStore.Instance.Bots.CollectionChanged += OnBotsCollectionChanged;
        RefreshBots();

        _bubbleTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(500) };
        _bubbleTimer.Tick += (_, _) => RefreshBubbles();
        _bubbleTimer.Start();
    }

    private void PositionAtEdge()
    {
        Left = SystemParameters.PrimaryScreenWidth - Width - 14;
        Top = SystemParameters.PrimaryScreenHeight / 2 + 40;
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        var hwnd = new WindowInteropHelper(this).Handle;
        int exStyle = GetWindowLong(hwnd, GWL_EXSTYLE);
        SetWindowLong(hwnd, GWL_EXSTYLE, exStyle | WS_EX_NOACTIVATE | WS_EX_TOOLWINDOW);

        ReassertTopmost();
        _topmostTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(500) };
        _topmostTimer.Tick += (_, _) => ReassertTopmost();
        _topmostTimer.Start();
    }

    private void RefreshBots()
    {
        var current = AgentStore.Instance.Bots.ToHashSet();
        foreach (var bot in _subscribedBots.Where(bot => !current.Contains(bot)).ToArray())
        {
            bot.PropertyChanged -= OnBotChanged;
            _subscribedBots.Remove(bot);
        }
        foreach (var bot in current)
        {
            if (_subscribedBots.Add(bot))
                bot.PropertyChanged += OnBotChanged;
        }
    }

    private void OnBotsCollectionChanged(object? sender, System.Collections.Specialized.NotifyCollectionChangedEventArgs e)
        => Dispatcher.Invoke(RefreshBots);

    protected override void OnClosed(EventArgs e)
    {
        IsDisposed = true;
        _bubbleTimer.Stop();
        _topmostTimer?.Stop();
        AgentStore.Instance.Bots.CollectionChanged -= OnBotsCollectionChanged;
        foreach (var bot in _subscribedBots)
            bot.PropertyChanged -= OnBotChanged;
        _subscribedBots.Clear();
        foreach (var timer in _bubbleFades.Values)
            timer.Stop();
        _bubbleFades.Clear();
        _bubbles.Clear();
        BubbleLayer.Children.Clear();
        if (CapsuleArea.IsMouseCaptured) CapsuleArea.ReleaseMouseCapture();
        base.OnClosed(e);
    }

    private void OnBotChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(AgentBot.Notification) or null or "")
            Dispatcher.Invoke(RefreshBubbles);
    }

    // ---- speech bubbles ("Show me Status!" — bot speaking) ---------------------------

    private void RefreshBubbles()
    {
        foreach (var bot in AgentStore.Instance.Bots)
        {
            bool has = !string.IsNullOrEmpty(bot.Notification);
            bool shown = _bubbles.ContainsKey(bot.Config.Id);
            if (has && !shown) ShowBubble(bot);
            else if (!has && shown) HideBubble(bot);
        }
    }

    private void ShowBubble(AgentBot bot)
    {
        var container = BotColumn.ItemContainerGenerator.ContainerFromItem(bot) as FrameworkElement;
        if (container == null || !container.IsLoaded) return;

        var bubble = new Border
        {
            CornerRadius = new CornerRadius(12),
            Background = new SolidColorBrush(Color.FromRgb(0x1A, 0x1A, 0x1A)),
            Padding = new Thickness(12, 8, 12, 8),
            MaxWidth = 220,
            Child = new TextBlock
            {
                Text = bot.Notification,
                Foreground = Brushes.White,
                FontSize = 12,
                TextWrapping = TextWrapping.Wrap,
            },
            Cursor = System.Windows.Input.Cursors.Hand,
        };
        bubble.MouseLeftButtonUp += (_, _) => bot.Notification = null;
        bubble.Measure(new Size(220, double.PositiveInfinity));

        var botCenter = container.TranslatePoint(
            new Point(0, container.ActualHeight / 2), this);

        Canvas.SetLeft(bubble, Width - Island.ActualWidth - Island.Margin.Right
            - 12 - bubble.DesiredSize.Width);
        Canvas.SetTop(bubble, Math.Clamp(
            botCenter.Y - bubble.DesiredSize.Height / 2,
            8, Height - bubble.DesiredSize.Height - 8));

        BubbleLayer.Children.Add(bubble);
        _bubbles[bot.Config.Id] = bubble;
        BubbleLayer.IsHitTestVisible = true;

        // The bubble fades on its own after a while; clicking dismisses sooner.
        var fade = new DispatcherTimer { Interval = TimeSpan.FromSeconds(8) };
        fade.Tick += (_, _) =>
        {
            fade.Stop();
            _bubbleFades.Remove(bot.Config.Id);
            if (string.IsNullOrEmpty(bot.Notification)) return;
            bot.Notification = null;
        };
        _bubbleFades[bot.Config.Id] = fade;
        fade.Start();
    }

    private void HideBubble(AgentBot bot)
    {
        if (_bubbleFades.Remove(bot.Config.Id, out var fade))
            fade.Stop();
        if (!_bubbles.Remove(bot.Config.Id, out var bubble)) return;
        BubbleLayer.Children.Remove(bubble);
        if (BubbleLayer.Children.Count == 0) BubbleLayer.IsHitTestVisible = false;
    }

    // ---- interactions -----------------------------------------------------------------

    private void Bot_Click(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: AgentBot bot })
            ChatWindow.OpenFor(bot, this);
    }

    private void AddAgent_Click(object sender, System.Windows.Input.MouseButtonEventArgs e)
        => AgentConfigWindow.ShowSingleton();

    private void Gear_Click(object sender, System.Windows.Input.MouseButtonEventArgs e)
        => IslandSettingsWindow.ShowSingleton();

    private void AddAgentMenuItem_Click(object sender, RoutedEventArgs e)
        => AgentConfigWindow.ShowSingleton();

    private void SwitchToSmall_Click(object sender, RoutedEventArgs e)
        => IslandSettings.Instance.Mode = "small";

    // ---- 拖拽 & 双击变身 ------------------------------------------------------------

    private System.Windows.Point _dragOffset;
    private bool _dragging;

    private void Capsule_MouseLeftButtonDown(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        // "+" 键、bot 图标、明暗滑块不参与拖拽/变身
        if (e.OriginalSource is FrameworkElement el)
        {
            if (el.DataContext is AgentBot) return;
            if (el is Controls.ThemeSlider) return;
            if (el is TextBlock tb && tb.Text == "+") return;
        }

        // 双击空白处变回小岛
        if (e.ClickCount == 2)
        {
            IslandSettings.Instance.Mode = "small";
            return;
        }

        _dragging = true;
        _dragOffset = e.GetPosition(CapsuleArea);
        CapsuleArea.CaptureMouse();
    }

    private void Capsule_MouseMove(object sender, System.Windows.Input.MouseEventArgs e)
    {
        if (!_dragging || e.LeftButton != System.Windows.Input.MouseButtonState.Pressed)
        {
            _dragging = false;
            return;
        }
        var pos = e.GetPosition(CapsuleArea);
        Left += pos.X - _dragOffset.X;
        Top += pos.Y - _dragOffset.Y;
    }

    private void Capsule_MouseLeftButtonUp(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        _dragging = false;
        CapsuleArea.ReleaseMouseCapture();
    }

    private void ExitMenuItem_Click(object sender, RoutedEventArgs e) => Application.Current.Shutdown();

    private void ReassertTopmost()
    {
        var hwnd = new WindowInteropHelper(this).Handle;
        if (hwnd != IntPtr.Zero)
            SetWindowPos(hwnd, HWND_TOPMOST, 0, 0, 0, 0, SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE);
    }

    private const int GWL_EXSTYLE = -20;
    private const int WS_EX_NOACTIVATE = 0x08000000;
    private const int WS_EX_TOOLWINDOW = 0x00000080;
    private static readonly IntPtr HWND_TOPMOST = new(-1);
    private const uint SWP_NOMOVE = 0x0002;
    private const uint SWP_NOSIZE = 0x0001;
    private const uint SWP_NOACTIVATE = 0x0010;

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetWindowPos(IntPtr hWnd, IntPtr hWndInsertAfter,
        int X, int Y, int cx, int cy, uint uFlags);

    [DllImport("user32.dll")]
    private static extern int GetWindowLong(IntPtr hWnd, int nIndex);

    [DllImport("user32.dll")]
    private static extern int SetWindowLong(IntPtr hWnd, int nIndex, int dwNewLong);
}
