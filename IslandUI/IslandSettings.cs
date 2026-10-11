using System.ComponentModel;
using System.IO;
using System.Runtime.CompilerServices;
using System.Text.Json;

namespace IslandUI;

/// <summary>User settings, persisted as JSON in %LocalAppData%\IslandUI.</summary>
public sealed class IslandSettings : INotifyPropertyChanged
{
    private static readonly string StoreDir =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "IslandUI");
    private static readonly string StorePath = Path.Combine(StoreDir, "settings.json");

    // Fields must initialise before Instance (static init order).
    private bool _showBattery = true;
    private bool _showWifi = true;
    private bool _showCpu;
    private bool _showMemory;
    private bool _showClock;
    private double _themeValue;   // 0=浅色 1=深色,渐进调节(明暗滑块)
    private string _mode = "small";  // 四种形态同一时刻只显示一个: small / long
    private double _chatWidth = 716;   // Chat 卡片的实际宽高(拖拽卡片边缘),由 ChatWindow 按屏幕限定
    private double _chatHeight = 540;

    public static IslandSettings Instance { get; } = new();

    public event PropertyChangedEventHandler? PropertyChanged;

    private IslandSettings() => Load();

    public bool ShowBattery { get => _showBattery; set => Set(ref _showBattery, value); }
    public bool ShowWifi { get => _showWifi; set => Set(ref _showWifi, value); }
    public bool ShowCpu { get => _showCpu; set => Set(ref _showCpu, value); }
    public bool ShowMemory { get => _showMemory; set => Set(ref _showMemory, value); }
    public bool ShowClock { get => _showClock; set => Set(ref _showClock, value); }

    /// <summary>0 = 浅色, 1 = 深色, 连续可调(右上角明暗滑块).</summary>
    public double ThemeValue
    {
        get => _themeValue;
        set => Set(ref _themeValue, Math.Clamp(value, 0, 1));
    }

    /// <summary>布尔视图(设置窗口单选钮用):>=0.5 视为深色.</summary>
    public bool DarkTheme
    {
        get => _themeValue >= 0.5;
        set => ThemeValue = value ? 1 : 0;
    }
    public string Mode { get => _mode; set => Set(ref _mode, value); }

    /// <summary>Chat 卡片宽度(DIP);ChatWindow 按屏幕再夹到 [最小, 最大] 之间.</summary>
    public double ChatWidth { get => _chatWidth; set => Set(ref _chatWidth, ClampSize(value, 716)); }
    /// <summary>Chat 卡片高度(DIP).</summary>
    public double ChatHeight { get => _chatHeight; set => Set(ref _chatHeight, ClampSize(value, 540)); }

    /// <summary>Stores both card dimensions with a single save.</summary>
    public void SetChatSize(double width, double height)
    {
        width = ClampSize(width, 716);
        height = ClampSize(height, 540);
        if (width == _chatWidth && height == _chatHeight) return;
        _chatWidth = width;
        _chatHeight = height;
        Save();
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(ChatWidth)));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(ChatHeight)));
    }

    private static double ClampSize(double value, double fallback)
        => double.IsFinite(value) ? Math.Clamp(Math.Round(value), 200, 10000) : fallback;

    private void Set<T>(ref T field, T value, [CallerMemberName] string? name = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return;
        field = value;
        Save();
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }

    private void Load()
    {
        try
        {
            if (!File.Exists(StorePath)) return;
            var d = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(File.ReadAllText(StorePath));
            if (d == null) return;
            if (Get(d, nameof(ShowBattery)) is { } b) _showBattery = b;
            if (Get(d, nameof(ShowWifi)) is { } w) _showWifi = w;
            if (Get(d, nameof(ShowCpu)) is { } c) _showCpu = c;
            if (Get(d, nameof(ShowMemory)) is { } m) _showMemory = m;
            if (Get(d, nameof(ShowClock)) is { } k) _showClock = k;
            if (d.TryGetValue(nameof(ThemeValue), out var tv) && tv.ValueKind == JsonValueKind.Number)
                _themeValue = tv.GetDouble();
            else if (Get(d, nameof(DarkTheme)) is { } t) _themeValue = t ? 1 : 0;
            if (d.TryGetValue(nameof(Mode), out var m2) && m2.ValueKind == JsonValueKind.String)
                _mode = m2.GetString() ?? "small";
            if (d.TryGetValue(nameof(ChatWidth), out var cw) && cw.ValueKind == JsonValueKind.Number
                && d.TryGetValue(nameof(ChatHeight), out var ch) && ch.ValueKind == JsonValueKind.Number)
            {
                _chatWidth = ClampSize(cw.GetDouble(), 716);
                _chatHeight = ClampSize(ch.GetDouble(), 540);
            }
            // Older builds stored a uniform zoom of the 716x540 card: keep the same on-screen size.
            else if (d.TryGetValue("ChatScale", out var cs) && cs.ValueKind == JsonValueKind.Number
                && double.IsFinite(cs.GetDouble()))
            {
                double scale = Math.Clamp(cs.GetDouble(), 0.3, 3);
                _chatWidth = ClampSize(716 * scale, 716);
                _chatHeight = ClampSize(540 * scale, 540);
            }
        }
        catch
        {
        }
    }

    private static bool? Get(Dictionary<string, JsonElement> d, string key)
        => d.TryGetValue(key, out var v) && v.ValueKind is JsonValueKind.True or JsonValueKind.False
            ? v.GetBoolean()
            : null;

    private void Save()
    {
        try
        {
            Directory.CreateDirectory(StoreDir);
            var d = new Dictionary<string, object>
            {
                [nameof(ShowBattery)] = ShowBattery,
                [nameof(ShowWifi)] = ShowWifi,
                [nameof(ShowCpu)] = ShowCpu,
                [nameof(ShowMemory)] = ShowMemory,
                [nameof(ShowClock)] = ShowClock,
                [nameof(ThemeValue)] = ThemeValue,
                [nameof(Mode)] = Mode,
                [nameof(ChatWidth)] = ChatWidth,
                [nameof(ChatHeight)] = ChatHeight,
            };
            File.WriteAllText(StorePath, JsonSerializer.Serialize(d));
        }
        catch
        {
        }
    }
}
