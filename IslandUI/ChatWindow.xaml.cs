using System.Collections.ObjectModel;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using IslandUI.Bots;
using IslandUI.Controls;
using IslandUI.Services;

namespace IslandUI;

public sealed class ChatMessage
{
    public required string Text { get; init; }
    public required bool FromUser { get; init; }
    public HorizontalAlignment Align => FromUser ? HorizontalAlignment.Right : HorizontalAlignment.Left;
}

/// <summary>Single focused chat surrounded by the rest of the user's Bot pods.</summary>
public partial class ChatWindow : Window
{
    private const double DesignWidth = 1036;
    private const double DesignHeight = 800;
    private const double BaseCardWidth = 716;
    private const double BaseCardHeight = 540;
    private const double BaseCardOffsetX = 25;
    private const double BaseCardOffsetY = 10;

    private static ChatWindow? Current;
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(60) };

    private AgentBot _bot;
    private readonly LongIslandWindow? _sourceWindow;
    private readonly ObservableCollection<ChatMessage> _messages = [];
    private readonly List<object> _history = [];
    private readonly HashSet<AgentBot> _subscribedBots = [];

    public static void OpenFor(AgentBot bot, LongIslandWindow? sourceWindow = null)
    {
        if (Current is { IsLoaded: true } existing)
        {
            _ = existing.UnfoldChatAsync();
            existing.SelectBot(bot);
            existing.RevealCarouselBot(bot);
            existing.Activate();
            return;
        }

        var window = new ChatWindow(bot, sourceWindow);
        Current = window;
        sourceWindow?.Hide();
        window.Show();
    }

    private ChatWindow(AgentBot bot, LongIslandWindow? sourceWindow)
    {
        _bot = bot;
        _sourceWindow = sourceWindow;
        InitializeComponent();
        Width = Math.Min(DesignWidth, SystemParameters.WorkArea.Width);
        Height = Math.Min(DesignHeight, SystemParameters.WorkArea.Height);
        Messages.ItemsSource = _messages;
        ApplyBot(bot);
        UpdateEmptyState();
        RefreshOrbitBots();
        AgentStore.Instance.Bots.CollectionChanged += OnBotsCollectionChanged;
        Loaded += ChatWindow_Loaded;
        SizeChanged += ChatWindow_SizeChanged;
    }

    private void ApplyBot(AgentBot bot)
    {
        _bot = bot;
        TitleText.Text = bot.Config.Name;
        StatusText.Text = bot.IsWorking
            ? (string.IsNullOrEmpty(bot.Activity) ? "正在处理…" : bot.Activity)
            : "随时准备着";
        HeaderBot.BodyColor = Brush(bot.Config.ColorHex);
        HeaderBot.State = bot.BotStateName;
        HeaderBot.GazeYaw = 18;
        HeaderBot.GazePitch = 4;
        ApplyStudioAvatar();
        bot.Notification = null;
    }

    private static Brush Brush(string hex)
    {
        var brush = new SolidColorBrush(
            (Color)ColorConverter.ConvertFromString(hex));
        brush.Freeze();
        return brush;
    }


    private void SyncBotSubscriptions(IReadOnlyCollection<AgentBot> bots)
    {
        var current = bots.ToHashSet();
        foreach (var bot in _subscribedBots.Where(bot => !current.Contains(bot)).ToArray())
        {
            bot.PropertyChanged -= OnBotChanged;
            _subscribedBots.Remove(bot);
        }
        foreach (var bot in current)
        {
            if (_subscribedBots.Add(bot)) bot.PropertyChanged += OnBotChanged;
        }
    }

    private void OnBotChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(AgentBot.State) or nameof(AgentBot.Activity)
            or nameof(AgentBot.RingFraction) or null or "")
            Dispatcher.BeginInvoke(RefreshOrbitBots);

        if (ReferenceEquals(sender, _bot)
            && (e.PropertyName is nameof(AgentBot.State) or nameof(AgentBot.Activity) or null or ""))
        {
            Dispatcher.BeginInvoke(() =>
            {
                StatusText.Text = _bot.IsWorking
                    ? (string.IsNullOrEmpty(_bot.Activity) ? "正在处理…" : _bot.Activity)
                    : "随时准备着";
                HeaderBot.State = _bot.BotStateName;
                StudioBotAvatar.State = _bot.BotStateName;
            });
        }
    }

    private void OnBotsCollectionChanged(object? sender,
        System.Collections.Specialized.NotifyCollectionChangedEventArgs e)
        => Dispatcher.BeginInvoke(RefreshOrbitBots);

    private void ChatWindow_Loaded(object sender, RoutedEventArgs e)
    {
        RefreshOrbitBots();
        SyncStudioBounds();
        _ = OpenStudioAsync();
    }

    private void ChatWindow_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (IsLoaded && _foldPhase == FoldPhase.Expanded) RefreshOrbitBots();
    }

    private void OrbitBot_Click(object sender, MouseButtonEventArgs e)
    {
        if (sender is not FrameworkElement { DataContext: AgentBot bot }) return;
        e.Handled = true;
        SelectBot(bot);
    }

    private void SelectBot(AgentBot bot)
    {
        if (ReferenceEquals(_bot, bot)) return;
        _messages.Clear();
        _history.Clear();
        ApplyBot(bot);
        UpdateEmptyState();
        RefreshOrbitBots();
        _ = SelectStudioBotAsync();
    }

    private void UpdateEmptyState()
    {
        if (ChatEmptyState is null) return;
        ChatEmptyState.Visibility = _messages.Count == 0
            && ThinkingPill?.Visibility != Visibility.Visible
            ? Visibility.Visible
            : Visibility.Collapsed;
    }

    // ---- composer -----------------------------------------------------------------------

    private async void Send_Click(object sender, MouseButtonEventArgs e) => await SendAsync();

    private async void Input_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter && Keyboard.Modifiers == ModifierKeys.None)
        {
            e.Handled = true;
            await SendAsync();
        }
    }

    private async Task SendAsync()
    {
        var text = InputBox.Text.Trim();
        if (text.Length == 0) return;
        InputBox.Clear();

        _messages.Add(new ChatMessage { Text = text, FromUser = true });
        UpdateEmptyState();
        ScrollToEnd();
        SetThinking(true);

        _bot.State = AgentState.Working;
        _bot.Activity = "正在思考…";
        StatusText.Text = "正在处理…";
        HeaderBot.State = "thinking";
        try
        {
            var reply = await CallModelAsync(text);
            _messages.Add(new ChatMessage { Text = reply, FromUser = false });
            _bot.State = AgentState.Notifying;
            _bot.Notification = reply.Length > 80 ? reply[..80] + "…" : reply;
            HeaderBot.State = "notify";
            StatusText.Text = "准备好了";
        }
        catch (Exception ex)
        {
            _messages.Add(new ChatMessage { Text = $"出错了：{ex.Message}", FromUser = false });
            _bot.State = AgentState.Error;
            _bot.Notification = "调用失败了，点我看看";
            HeaderBot.State = "alert";
            StatusText.Text = "出了点问题";
        }
        SetThinking(false);
        UpdateEmptyState();
        ScrollToEnd();
    }

    private void SetThinking(bool on)
    {
        ThinkingPill.Visibility = on ? Visibility.Visible : Visibility.Collapsed;
        foreach (var dot in new[] { ThinkDot1, ThinkDot2, ThinkDot3 })
            dot.BeginAnimation(OpacityProperty, null);
        if (!on)
        {
            UpdateEmptyState();
            return;
        }

        UpdateEmptyState();
        var dots = new[] { ThinkDot1, ThinkDot2, ThinkDot3 };
        for (int i = 0; i < dots.Length; i++)
        {
            var animation = new DoubleAnimationUsingKeyFrames
            {
                RepeatBehavior = RepeatBehavior.Forever,
            };
            animation.KeyFrames.Add(new EasingDoubleKeyFrame(0.3,
                KeyTime.FromTimeSpan(TimeSpan.Zero)));
            animation.KeyFrames.Add(new EasingDoubleKeyFrame(1.0,
                KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(300 + i * 200))));
            animation.KeyFrames.Add(new EasingDoubleKeyFrame(0.3,
                KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(600 + i * 200))));
            dots[i].BeginAnimation(OpacityProperty, animation);
        }
    }

    private async Task<string> CallModelAsync(string text)
    {
        _history.Add(new { role = "user", content = text });
        var payload = new { model = _bot.Config.Model, messages = _history };
        using var request = new HttpRequestMessage(HttpMethod.Post,
            _bot.Config.BaseUrl.TrimEnd('/') + "/chat/completions");
        request.Headers.Authorization =
            new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", _bot.Config.ApiKey);
        request.Content = new StringContent(
            JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json");

        using var response = await Http.SendAsync(request);
        var body = await response.Content.ReadAsStringAsync();
        response.EnsureSuccessStatusCode();
        using var doc = JsonDocument.Parse(body);
        var content = doc.RootElement.GetProperty("choices")[0]
            .GetProperty("message").GetProperty("content").GetString() ?? "";
        _history.Add(new { role = "assistant", content });
        return content;
    }

    private void ScrollToEnd() => MessageScroll.ScrollToEnd();

    protected override void OnClosed(EventArgs e)
    {
        DisposeFold();
        DisposeStudio();
        DisposeCarousel();
        AgentStore.Instance.Bots.CollectionChanged -= OnBotsCollectionChanged;
        Loaded -= ChatWindow_Loaded;
        SizeChanged -= ChatWindow_SizeChanged;
        foreach (var bot in _subscribedBots) bot.PropertyChanged -= OnBotChanged;
        _subscribedBots.Clear();
        if (ReferenceEquals(Current, this)) Current = null;
        if (_sourceWindow is { IsVisible: false, IsDisposed: false })
        {
            _sourceWindow.Show();
            _sourceWindow.Activate();
        }
        SetThinking(false);
        base.OnClosed(e);
    }

    private async void Close_Click(object sender, MouseButtonEventArgs e) => await FoldChatAsync();
}
