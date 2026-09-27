using System.Text.Json;
using System.Windows;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
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

    private async Task OpenStudioAsync()
    {
        if (_openingStudio || _studioClosed || _legacyMode || !IsLoaded) return;
        _openingStudio = true;
        StudioLoading.Visibility = Visibility.Visible;
        StudioRetry.Visibility = StudioFallback.Visibility = Visibility.Collapsed;
        StudioLoadMessage.Text = "正在打开 AI Studio…";
        try
        {
            _studioRuntimeRequested = true;
            var runtime = ChatStudioRuntime.Shared;
            var requestedBot = _bot;
            var sessionTask = runtime.SessionAsync(requestedBot, _studioLifetime.Token);
            _studioView?.Dispose();
            StudioWebHost.Children.Clear();
            var view = new WebView2CompositionControl
            {
                UseLayoutRounding = true,
                SnapsToDevicePixels = true,
                ZoomFactor = 1,
            };
            _studioView = view;
            StudioWebHost.Children.Add(view);
            async Task InitializeBrowserAsync()
            {
                var environment = await runtime.BrowserEnvironmentAsync();
                _studioLifetime.Token.ThrowIfCancellationRequested();
                await view.EnsureCoreWebView2Async(environment);
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
                else StudioLoading.Visibility = Visibility.Visible;
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
                _studioLoaded = true;
                _studioBotId = requestedBot.Config.Id;
                if (!ReferenceEquals(requestedBot, _bot)) await SelectStudioBotAsync();
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
        StudioLoading.Visibility = Visibility.Visible;
        StudioLoadMessage.Text = $"正在打开 {requestedBot.Config.Name}…";
        try
        {
            var session = await ChatStudioRuntime.Shared.SessionAsync(requestedBot, _studioLifetime.Token);
            if (_studioClosed || version != _studioSelectionVersion) return;
            _studioBotId = requestedBot.Config.Id;
            _studioView?.CoreWebView2.PostWebMessageAsJson(JsonSerializer.Serialize(
                new { type = "select-bot", session }, StudioJson));
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
                    _ = UnfoldChatAsync();
                    break;
                case "bot-avatar-bounds":
                    PositionStudioAvatar(message);
                    break;
                case "page-ready":
                    if (message.TryGetProperty("botId", out var readyBot) && readyBot.GetString() == _bot.Config.Id)
                        StudioLoading.Visibility = Visibility.Collapsed;
                    break;
                case "drag-window":
                    BeginStudioDrag();
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
        StudioLoading.Visibility = Visibility.Visible;
        StudioLoadMessage.Text = message;
        StudioRetry.Visibility = StudioFallback.Visibility = Visibility.Visible;
    }

    private void StudioViewport_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        double radius = 28 * StudioShell.Width / (BaseCardWidth - 28);
        if (!double.IsFinite(radius)) radius = 28;
        StudioViewport.Clip = new RectangleGeometry(new Rect(new Point(), e.NewSize), radius, radius);
        // The page's ResizeObserver supplies the new CSS bounds after reflow.
        if (StudioBotAvatar != null) StudioBotAvatar.Visibility = Visibility.Collapsed;
    }

    private void StudioDesign_SizeChanged(object sender, SizeChangedEventArgs e)
        => Dispatcher.BeginInvoke(SyncStudioBounds, System.Windows.Threading.DispatcherPriority.Loaded);

    private void SyncStudioBounds()
    {
        if (_studioClosed || _foldPhase != FoldPhase.Expanded || StudioOverlay == null || ChatCard.ActualWidth <= 0) return;
        LayoutExpandedStudio();
    }

    private void LayoutExpandedStudio()
    {
        var transform = ChatCard.TransformToVisual(StudioOverlay);
        var start = transform.Transform(new Point(14, 14));
        var end = transform.Transform(new Point(ChatCard.ActualWidth - 14, ChatCard.ActualHeight - 14));
        var dpi = VisualTreeHelper.GetDpi(StudioOverlay);
        double left = Math.Round(start.X * dpi.DpiScaleX) / dpi.DpiScaleX;
        double top = Math.Round(start.Y * dpi.DpiScaleY) / dpi.DpiScaleY;
        System.Windows.Controls.Canvas.SetLeft(StudioShell, left);
        System.Windows.Controls.Canvas.SetTop(StudioShell, top);
        StudioShell.Width = Math.Max(1, Math.Round(end.X * dpi.DpiScaleX) / dpi.DpiScaleX - left);
        StudioShell.Height = Math.Max(1, Math.Round(end.Y * dpi.DpiScaleY) / dpi.DpiScaleY - top);
        StudioHeaderRow.Height = new GridLength(32 * StudioShell.Width / (BaseCardWidth - 28));
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
        if (_foldPhase is FoldPhase.Folding or FoldPhase.Unfolding or FoldPhase.Returning) return;
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

    private async void StudioClose_Click(object sender, RoutedEventArgs e)
    {
        if (_foldPhase == FoldPhase.Folded) await UnfoldChatAsync();
        else await FoldChatAsync();
    }
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
