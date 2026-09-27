using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using IslandUI.Controls;
using IslandUI.Services;

namespace IslandUI;

/// <summary>
/// The small island: a vertical capsule at the right screen edge —
/// clock on top, the fused WiFi/battery icon in the middle, settings gear at
/// the bottom (islandUI.png). Hover unfuses the icon in place (WIFIBattery.mp4)
/// and widens the capsule for one line of values.
/// </summary>
public partial class SmallIslandWindow : Window
{
    private const double CompactWidth = 64, CompactHeight = 210;
    private const double ExpandedWidth = 210, ExpandedHeight = 226;

    private readonly DispatcherTimer _clockTimer;
    private DispatcherTimer? _topmostTimer;
    private bool _expanded;
    private bool _feedSubscribed;

    public SmallIslandWindow()
    {
        InitializeComponent();
        PositionAtEdge();

        _clockTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _clockTimer.Tick += (_, _) => ClockText.Text = DateTime.Now.ToString("H:mm");
        _clockTimer.Start();
        ClockText.Text = DateTime.Now.ToString("H:mm");
    }

    private void PositionAtEdge()
    {
        Left = SystemParameters.PrimaryScreenWidth - Width - 14;
        Top = (SystemParameters.PrimaryScreenHeight - Height) / 3;
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

    // ---- expand / collapse ------------------------------------------------------

    private void Pill_MouseEnter(object sender, System.Windows.Input.MouseEventArgs e) => Expand();

    private void Pill_MouseLeave(object sender, System.Windows.Input.MouseEventArgs e)
    {
        if (!Pill.IsMouseOver) Collapse();
    }

    // ---- 桌面拖拽 ---------------------------------------------------------------

    private System.Windows.Point _dragOffset;
    private bool _dragging;

    private void Pill_MouseLeftButtonDown(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        // 齿轮键和明暗滑块有自己的交互,不参与拖拽/变身
        if (e.OriginalSource is FrameworkElement el
            && (el == GearButton || el.Parent == GearButton || el is Controls.ThemeSlider))
            return;

        if (e.ClickCount == 2)
        {
            Pill_MouseDoubleClick();
            return;
        }

        _dragging = true;
        _dragOffset = e.GetPosition(Pill);
        Pill.CaptureMouse();
    }

    private void Pill_MouseMove(object sender, System.Windows.Input.MouseEventArgs e)
    {
        if (!_dragging || e.LeftButton != System.Windows.Input.MouseButtonState.Pressed)
        {
            _dragging = false;
            return;
        }
        var pos = e.GetPosition(Pill);
        Left += pos.X - _dragOffset.X;
        Top += pos.Y - _dragOffset.Y;
    }

    private void Pill_MouseLeftButtonUp(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        _dragging = false;
        Pill.ReleaseMouseCapture();
    }

    private void Expand()
    {
        if (_expanded) return;
        _expanded = true;

        // Unfuse in place: the ring unwraps back into the battery pill, dots grow into bars.
        SpringAnimator.Animate(Icon, FusionIcon.FusionProperty, 0, response: 0.85, dampingFraction: 0.85);
        SpringAnimator.Animate(Pill, WidthProperty, ExpandedWidth, response: 0.5, dampingFraction: 0.82);
        SpringAnimator.Animate(Pill, HeightProperty, ExpandedHeight, response: 0.5, dampingFraction: 0.82);
        // The slot grows too — same icon, no state jump.
        SpringAnimator.Animate(Icon, WidthProperty, 168, response: 0.55, dampingFraction: 0.85);
        SpringAnimator.Animate(Icon, HeightProperty, 56, response: 0.55, dampingFraction: 0.85);

        ValuesText.BeginAnimation(OpacityProperty,
            new DoubleAnimation(1, new Duration(TimeSpan.FromMilliseconds(250)))
            {
                BeginTime = TimeSpan.FromMilliseconds(200),
                EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut },
            });
    }

    private void Collapse()
    {
        if (!_expanded) return;
        _expanded = false;

        SpringAnimator.Animate(Icon, FusionIcon.FusionProperty, 1, response: 0.85, dampingFraction: 0.85);
        SpringAnimator.Animate(Pill, WidthProperty, CompactWidth, response: 0.45, dampingFraction: 0.9);
        SpringAnimator.Animate(Pill, HeightProperty, CompactHeight, response: 0.45, dampingFraction: 0.9);
        SpringAnimator.Animate(Icon, WidthProperty, 54, response: 0.45, dampingFraction: 0.9);
        SpringAnimator.Animate(Icon, HeightProperty, 54, response: 0.45, dampingFraction: 0.9);

        ValuesText.BeginAnimation(OpacityProperty,
            new DoubleAnimation(0, new Duration(TimeSpan.FromMilliseconds(120)))
            {
                EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut },
            });
    }

    // ---- data feed -----------------------------------------------------------------

    protected override void OnContentRendered(EventArgs e)
    {
        base.OnContentRendered(e);
        var battery = BatteryService.Instance;
        var wifi = WifiService.Instance;

        void Feed()
        {
            Icon.BatteryFraction = battery.Percent >= 0 ? battery.Percent / 100.0 : 1;
            // 插电即绿:满电挂着电源时 Windows 不再上报"充电中"
            Icon.IsCharging = battery.IsCharging || battery.IsPluggedIn;
            Icon.SignalFraction = wifi.IsConnected ? wifi.SignalPercent / 100.0 : 0;

            string ssid = string.IsNullOrEmpty(wifi.Ssid) ? "WiFi" : wifi.Ssid;
            string charging = battery.IsCharging ? "⚡" : "";
            ValuesText.Text = $"{ssid} {wifi.SignalPercent}% · 电池 {battery.Percent}%{charging}";
        }

        if (!_feedSubscribed)
        {
            battery.PropertyChanged += OnBatteryPropertyChanged;
            wifi.PropertyChanged += OnWifiPropertyChanged;
            _feedSubscribed = true;
        }
        Feed();
    }

    private void OnBatteryPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
        => Dispatcher.Invoke(RefreshFeed);

    private void OnWifiPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
        => Dispatcher.Invoke(RefreshFeed);

    private void RefreshFeed()
    {
        var battery = BatteryService.Instance;
        var wifi = WifiService.Instance;
        Icon.BatteryFraction = battery.Percent >= 0 ? battery.Percent / 100.0 : 1;
        Icon.IsCharging = battery.IsCharging || battery.IsPluggedIn;
        Icon.SignalFraction = wifi.IsConnected ? wifi.SignalPercent / 100.0 : 0;
        string ssid = string.IsNullOrEmpty(wifi.Ssid) ? "WiFi" : wifi.Ssid;
        string charging = battery.IsCharging ? "⚡" : "";
        ValuesText.Text = $"{ssid} {wifi.SignalPercent}% · 电池 {battery.Percent}%{charging}";
    }

    protected override void OnClosed(EventArgs e)
    {
        _clockTimer.Stop();
        _topmostTimer?.Stop();
        if (_feedSubscribed)
        {
            BatteryService.Instance.PropertyChanged -= OnBatteryPropertyChanged;
            WifiService.Instance.PropertyChanged -= OnWifiPropertyChanged;
            _feedSubscribed = false;
        }
        if (Pill.IsMouseCaptured) Pill.ReleaseMouseCapture();
        base.OnClosed(e);
    }

    // ---- 双击变身:小岛 → 长岛 --------------------------------------------------------

    private void Pill_MouseDoubleClick()
    {
        IslandSettings.Instance.Mode = "long";
    }

    // ---- menu -----------------------------------------------------------------------

    private void Gear_Click(object sender, System.Windows.Input.MouseButtonEventArgs e)
        => IslandSettingsWindow.ShowSingleton();

    private void SettingsMenuItem_Click(object sender, RoutedEventArgs e)
        => IslandSettingsWindow.ShowSingleton();

    private void SwitchToLong_Click(object sender, RoutedEventArgs e)
        => IslandSettings.Instance.Mode = "long";

    private void ExitMenuItem_Click(object sender, RoutedEventArgs e) => Application.Current.Shutdown();

    private void ReassertTopmost()
    {
        var hwnd = new WindowInteropHelper(this).Handle;
        if (hwnd != IntPtr.Zero)
            SetWindowPos(hwnd, HWND_TOPMOST, 0, 0, 0, 0,
                SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE);
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
