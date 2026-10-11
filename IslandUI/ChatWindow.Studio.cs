using System.Diagnostics;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using IslandUI.Services;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.Wpf;

namespace IslandUI;

public partial class ChatWindow
{
    private static readonly JsonSerializerOptions StudioJson = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };
    private readonly CancellationTokenSource _studioLifetime = new();
    private WebView2CompositionControl? _studioView;
    private bool _openingStudio;
    private bool _studioLoaded;
    private bool _studioClosed;
    private bool _legacyMode;
    private int _studioSelectionVersion;
    private string? _studioBotId;
    private string? _studioOrigin;
    private bool _studioNavigationPending;
    private bool _studioRuntimeRequested;
    private bool _studioReady;
    private int _readyFallbackVersion;
    // Open -> page-ready timeline, written to Debug output and read by the harness.
    private readonly Stopwatch _openClock = new();
    internal readonly List<(string Phase, long Ms)> StudioTimeline = [];

    private void StudioTiming(string phase)
    {
        long ms = _openClock.ElapsedMilliseconds;
        StudioTimeline.Add((phase, ms));
        Debug.WriteLine($"[ChatWindow] {ms,6} ms  {phase}");
    }

    private async Task OpenStudioAsync()
    {
        if (_openingStudio || _studioClosed || _legacyMode || !IsLoaded) return;
        _openingStudio = true;
        ShowStudioSkeleton();
        StudioErrorPanel.Visibility = Visibility.Collapsed;
        StudioRetry.Visibility = StudioFallback.Visibility = Visibility.Collapsed;
        try
        {
            _studioRuntimeRequested = true;
            var runtime = ChatStudioRuntime.Shared;
            var requestedBot = _bot;
            async Task<StudioSession> SessionTimedAsync()
            {
                var result = await runtime.SessionAsync(requestedBot, _studioLifetime.Token);
                StudioTiming("backend session ready");
                return result;
            }
            var sessionTask = SessionTimedAsync();
            _studioView?.Dispose();
            StudioWebHost.Children.Clear();
            var view = new WebView2CompositionControl
            {
                UseLayoutRounding = true,
                SnapsToDevicePixels = true,
                ZoomFactor = 1,
                // No white flash: the browser paints the card color until the page draws.
                DefaultBackgroundColor = DrawingColor(ComicTheme.Current.Card),
            };
            _studioView = view;
            StudioWebHost.Children.Add(view);
            async Task InitializeBrowserAsync()
            {
                var environment = await runtime.BrowserEnvironmentAsync();
                StudioTiming("WebView2 environment ready");
                _studioLifetime.Token.ThrowIfCancellationRequested();
                await view.EnsureCoreWebView2Async(environment);
                StudioTiming("WebView2 controller ready");
            }
            await Task.WhenAll(sessionTask, InitializeBrowserAsync());
            _studioLifetime.Token.ThrowIfCancellationRequested();
            var session = await sessionTask;
            var core = view.CoreWebView2;
            _studioOrigin = session.ApiUrl;
            core.Settings.AreDefaultContextMenusEnabled = false;
            core.Settings.IsStatusBarEnabled = false;
            core.Settings.IsZoomControlEnabled = false;
            await core.AddScriptToExecuteOnDocumentCreatedAsync(
                "if (window.top === window.self) window.__ISLAND_HOST__ = " + JsonSerializer.Serialize(session, StudioJson) + ";");
            core.WebMessageReceived += StudioMessageReceived;
            core.NavigationStarting += (_, e) =>
            {
                if (!IsStudioAddress(e.Uri))
                {
                    e.Cancel = true;
                    if (e.IsUserInitiated) OpenStudioLink(e.Uri);
                }
                else { _studioReady = false; ShowStudioSkeleton(); }
            };
            core.NewWindowRequested += (_, e) =>
            {
                e.Handled = true;
                if (e.IsUserInitiated) OpenStudioLink(e.Uri);
            };
            core.PermissionRequested += (_, e) => e.State = CoreWebView2PermissionState.Deny;
            core.NavigationCompleted += async (_, e) =>
            {
                if (_studioClosed) return;
                _studioNavigationPending = false;
                if (!e.IsSuccess) { ShowStudioError("页面加载失败，请重新连接。"); return; }
                StudioTiming("navigation complete");
                _studioLoaded = true;
                _studioBotId = requestedBot.Config.Id;
                if (!ReferenceEquals(requestedBot, _bot)) await SelectStudioBotAsync();
                else RevealStudioPageLater();
            };
            _studioNavigationPending = true;
            core.Navigate(session.ApiUrl + "/index.html");
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { if (!_studioClosed) ShowStudioError(ex.Message); }
        finally { _openingStudio = false; }
    }

    private async Task SelectStudioBotAsync()
    {
        if (_studioClosed || _legacyMode || !IsLoaded) return;
        StudioBotAvatar.Visibility = Visibility.Collapsed;
        if (!_studioLoaded) { if (!_openingStudio && !_studioNavigationPending) await OpenStudioAsync(); return; }
        var version = ++_studioSelectionVersion;
        var requestedBot = _bot;
        _studioReady = false;
        ShowStudioSkeleton();
        try
        {
            var session = await ChatStudioRuntime.Shared.SessionAsync(requestedBot, _studioLifetime.Token);
            if (_studioClosed || version != _studioSelectionVersion) return;
            _studioBotId = requestedBot.Config.Id;
            _studioView?.CoreWebView2.PostWebMessageAsJson(JsonSerializer.Serialize(
                new { type = "select-bot", session }, StudioJson));
            RevealStudioPageLater();
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { if (!_studioClosed && version == _studioSelectionVersion) ShowStudioError(ex.Message); }
    }

    private void StudioMessageReceived(object? sender, CoreWebView2WebMessageReceivedEventArgs e)
    {
        if (_studioClosed || !IsStudioAddress(e.Source)) return;
        try
        {
            using var document = JsonDocument.Parse(e.WebMessageAsJson);
            var message = document.RootElement;
            if (!message.TryGetProperty("type", out var type)) return;
            switch (type.GetString())
            {
                case "expand-chat":
                    break;   // Chat no longer has a folded state to expand from.
                case "bot-avatar-bounds":
                    PositionStudioAvatar(message);
                    break;
                case "page-ready":
                    if (message.TryGetProperty("botId", out var readyBot) && readyBot.GetString() == _bot.Config.Id)
                    {
                        if (!_studioReady) StudioTiming("page-ready");
                        RevealStudioPage();
                    }
                    break;
                case "drag-window":
                    BeginStudioDrag();
                    break;
                case "spark-zone-created":
                    if (message.TryGetProperty("claimId", out var claim) && claim.GetString() is { } claimId)
                        _ = AddSparkZoneBotAsync(claimId,
                            message.TryGetProperty("color", out var zc) ? zc.GetString() : null);
                    break;
                case "bot-state":
                    if (!message.TryGetProperty("botId", out var id) || !message.TryGetProperty("state", out var state)) return;
                    var bot = AgentStore.Instance.Bots.FirstOrDefault(b => b.Config.Id == id.GetString());
                    if (bot == null) return;
                    bot.State = state.GetString() switch
                    {
                        "working" => AgentState.Working,
                        "error" => AgentState.Error,
                        "done" => AgentState.Notifying,
                        _ => AgentState.Idle,
                    };
                    bot.Activity = bot.IsWorking ? "正在思考…" : "";
                    break;
            }
        }
        catch (JsonException) { }
    }

    private static readonly string[] ZonePalette = ["#76B900", "#54A8FF", "#FF7A59", "#C792EA", "#FFD866", "#FF6188"];

    /// <summary>A zone made on the Spark becomes its own Bot in the island's Bot switcher.</summary>
    private async Task AddSparkZoneBotAsync(string claimId, string? color)
    {
        var zone = await ChatStudioRuntime.Shared.ClaimSparkZoneAsync(claimId, _studioLifetime.Token);
        if (zone == null) return;
        var hex = color != null && System.Text.RegularExpressions.Regex.IsMatch(color, "^#[0-9A-Fa-f]{6}$")
            ? color : ZonePalette[AgentStore.Instance.Bots.Count % ZonePalette.Length];
        AgentStore.Instance.AddAgent(new AgentConfig
        {
            Name = zone.Name,
            BaseUrl = zone.BaseUrl,
            Model = zone.Model,
            ApiKey = zone.ApiKey,
            ColorHex = hex,
            BotShape = _bot.Config.BotShape,
        });
    }

    private bool IsStudioAddress(string uri) => Uri.TryCreate(uri, UriKind.Absolute, out var value)
        && value.GetLeftPart(UriPartial.Authority) == _studioOrigin
        && value.AbsolutePath is "/" or "/index.html";

    private static void OpenStudioLink(string address)
    {
        if (!Uri.TryCreate(address, UriKind.Absolute, out var uri)
            || uri.Scheme is not ("http" or "https") || uri.UserInfo.Length != 0) return;
        try { System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(uri.AbsoluteUri) { UseShellExecute = true }); }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException) { }
    }

    private void ShowStudioError(string message)
    {
        _studioLoaded = false;
        _studioNavigationPending = false;
        _studioReady = false;
        ShowStudioSkeleton();
        StudioErrorPanel.Visibility = Visibility.Visible;
        StudioLoadMessage.Text = message;
        StudioRetry.Visibility = StudioFallback.Visibility = Visibility.Visible;
    }

    /// <summary>The skeleton covers the browser at once (no fade-in, so no half-painted page shows through).</summary>
    private void ShowStudioSkeleton()
    {
        StudioLoading.BeginAnimation(OpacityProperty, null);
        StudioLoading.Opacity = 1;
        StudioLoading.Visibility = Visibility.Visible;
        StudioLoading.IsHitTestVisible = true;
    }

    /// <summary>Cross-fades the skeleton away to the already painted page.</summary>
    private void RevealStudioPage()
    {
        if (_studioClosed || _studioReady || StudioErrorPanel.Visibility == Visibility.Visible) return;
        _studioReady = true;
        _readyFallbackVersion++;
        StudioLoading.IsHitTestVisible = false;
        var fade = new DoubleAnimation(StudioLoading.Opacity, 0, TimeSpan.FromMilliseconds(180))
        {
            EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseOut },
        };
        fade.Completed += (_, _) =>
        {
            if (_studioReady) StudioLoading.Visibility = Visibility.Collapsed;
        };
        StudioLoading.BeginAnimation(OpacityProperty, fade);
    }

    /// <summary>A page that never reports ready (an older build) is still revealed shortly after loading.</summary>
    private async void RevealStudioPageLater()
    {
        int version = ++_readyFallbackVersion;
        await Task.Delay(1500);
        if (version == _readyFallbackVersion && !_studioReady && _studioLoaded) RevealStudioPage();
    }

    private static System.Drawing.Color DrawingColor(Brush brush)
        => brush is SolidColorBrush { Color: var c } ? System.Drawing.Color.FromArgb(255, c.R, c.G, c.B) : System.Drawing.Color.White;

    private void StudioViewport_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        const double radius = 28;   // the card's 44 px corner minus the 14 px inset, never scaled
        StudioViewport.Clip = new RectangleGeometry(new Rect(new Point(), e.NewSize), radius, radius);
        // The page's ResizeObserver supplies the new CSS bounds after reflow.
        if (StudioBotAvatar != null) StudioBotAvatar.Visibility = Visibility.Collapsed;
    }

    private void SyncStudioBounds()
    {
        if (_studioClosed || _foldPhase != FoldPhase.Expanded || StudioOverlay == null) return;
        LayoutExpandedStudio();
    }

    private void LayoutExpandedStudio()
    {
        // The page sits 14 px inside the card at its real size; nothing is scaled.
        var card = CardRect;
        var start = new Point(card.Left + 14, card.Top + 14);
        var end = new Point(card.Right - 14, card.Bottom - 14);
        var dpi = VisualTreeHelper.GetDpi(StudioOverlay);
        double left = Math.Round(start.X * dpi.DpiScaleX) / dpi.DpiScaleX;
        double top = Math.Round(start.Y * dpi.DpiScaleY) / dpi.DpiScaleY;
        System.Windows.Controls.Canvas.SetLeft(StudioShell, left);
        System.Windows.Controls.Canvas.SetTop(StudioShell, top);
        StudioShell.Width = Math.Max(1, Math.Round(end.X * dpi.DpiScaleX) / dpi.DpiScaleX - left);
        StudioShell.Height = Math.Max(1, Math.Round(end.Y * dpi.DpiScaleY) / dpi.DpiScaleY - top);
        StudioHeaderRow.Height = new GridLength(32);
    }

    protected override void OnDpiChanged(DpiScale oldDpi, DpiScale newDpi)
    {
        base.OnDpiChanged(oldDpi, newDpi);
        Dispatcher.BeginInvoke(SyncStudioBounds, System.Windows.Threading.DispatcherPriority.Loaded);
    }

    private void ApplyStudioAvatar()
    {
        StudioBotAvatar.BodyColor = Brush(_bot.Config.ColorHex);
        StudioBotAvatar.Shape = _bot.Config.BotShape;
        StudioBotAvatar.Expression = _bot.Config.BotExpression;
        StudioBotAvatar.State = _bot.BotStateName;
    }

    private void PositionStudioAvatar(JsonElement message)
    {
        if (_foldPhase != FoldPhase.Expanded) return;
        if (!message.TryGetProperty("botId", out var id) || id.GetString() != _bot.Config.Id) return;
        StudioBotAvatar.Visibility = Visibility.Collapsed;
        if (!message.TryGetProperty("visible", out var visible) || visible.ValueKind != JsonValueKind.True) return;
        bool Number(string name, out double result)
        {
            result = 0;
            return message.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Number
                && value.TryGetDouble(out result) && double.IsFinite(result);
        }
        if (!Number("x", out var x) || !Number("y", out var y)
            || !Number("width", out var width) || !Number("height", out var height)
            || !Number("viewportWidth", out var vw) || !Number("viewportHeight", out var vh)
            || vw <= 0 || vh <= 0 || width <= 0 || height <= 0 || width > 200 || height > 200
            || x < 0 || y < 0 || x + width > vw + 1 || y + height > vh + 1) return;
        double sx = StudioViewport.ActualWidth / vw, sy = StudioViewport.ActualHeight / vh;
        StudioBotAvatar.Width = width * sx;
        StudioBotAvatar.Height = height * sy;
        System.Windows.Controls.Canvas.SetLeft(StudioBotAvatar, x * sx);
        System.Windows.Controls.Canvas.SetTop(StudioBotAvatar, y * sy);
        StudioBotAvatar.Visibility = Visibility.Visible;
    }

    private void StudioDragBar_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        for (DependencyObject? node = e.OriginalSource as DependencyObject; node != null && node != StudioDragBar;
            node = VisualTreeHelper.GetParent(node))
            if (node is ButtonBase) return;
        e.Handled = true;
        BeginStudioDrag();
    }

    private void BeginStudioDrag()
    {
        if (Mouse.LeftButton != MouseButtonState.Pressed) return;
        // Chat and the orbit share this Window's coordinate system. Moving the HWND
        // cannot change any rail position, offset, geometry, or selection.
        try { DragMove(); }
        catch (InvalidOperationException) { /* Mouse was released before the web message arrived. */ }
    }

    private async void StudioClose_Click(object sender, RoutedEventArgs e) => await DismissToLongIslandAsync();
    private async void StudioRetry_Click(object sender, RoutedEventArgs e) => await OpenStudioAsync();
    private void StudioFallback_Click(object sender, RoutedEventArgs e)
    {
        _legacyMode = true;
        StudioBotAvatar.Visibility = Visibility.Collapsed;
        StudioShell.Visibility = Visibility.Collapsed;
        LegacyChatContent.Visibility = Visibility.Visible;
        _studioView?.Dispose();
        _studioView = null;
        StudioWebHost.Children.Clear();
        _ = ChatStudioRuntime.Shared.StopAsync();
    }

    private void DisposeStudio()
    {
        _studioClosed = true;
        StudioBotAvatar.Visibility = Visibility.Collapsed;
        _studioSelectionVersion++;
        _studioLifetime.Cancel();
        _studioView?.Dispose();
        _studioView = null;
        StudioWebHost.Children.Clear();
        // Offscreen geometry tests never start the backend and need no runtime instance.
        if (_studioRuntimeRequested) ChatStudioRuntime.Shared.ReleaseWhenIdle();
    }
}
