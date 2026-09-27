using System.ComponentModel;
using System.Windows.Media;

namespace IslandUI;

/// <summary>
/// App-wide theme, progressively interpolable between light (white, black
/// outline, comic style) and dark. Driven by IslandSettings.ThemeValue (0..1).
/// </summary>
public sealed class ComicTheme : INotifyPropertyChanged
{
    public static ComicTheme Current { get; } = new();
    private readonly Dictionary<(uint Light, uint Dark), SolidColorBrush> _brushes = [];
    private double _cachedThemeValue = double.NaN;

    public const string Font = "Comic Sans MS, Segoe UI";

    public event PropertyChangedEventHandler? PropertyChanged;

    private ComicTheme()
    {
        IslandSettings.Instance.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName is nameof(IslandSettings.ThemeValue)
                or nameof(IslandSettings.DarkTheme) or null or "")
            {
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(null));
            }
        };
    }

    private static double T => IslandSettings.Instance.ThemeValue;

    private SolidColorBrush Mix(uint light, uint dark)
    {
        double t = T;
        lock (_brushes)
        {
            // Keep only the current palette; a continuously dragged theme slider
            // must not create an ever-growing cache of intermediate colours.
            if (_cachedThemeValue != t)
            {
                _brushes.Clear();
                _cachedThemeValue = t;
            }
            if (_brushes.TryGetValue((light, dark), out var cached)) return cached;
            var c = Color.FromArgb(255,
                (byte)(((light >> 16) & 0xFF) + (((dark >> 16) & 0xFF) - ((light >> 16) & 0xFF)) * t),
                (byte)(((light >> 8) & 0xFF) + (((dark >> 8) & 0xFF) - ((light >> 8) & 0xFF)) * t),
                (byte)((light & 0xFF) + ((dark & 0xFF) - (light & 0xFF)) * t));
            var b = new SolidColorBrush(c);
            b.Freeze();
            _brushes.Add((light, dark), b);
            return b;
        }
    }

    // Surfaces
    public Brush Canvas => Mix(0xFCFCFC, 0x141414);
    public Brush Card => Mix(0xFFFFFF, 0x1E1E1E);
    public Brush CardSoft => Mix(0xEEEEF0, 0x2A2A2A);
    public Brush SkyTint => Mix(0xEDF7FD, 0x1C2B36);

    // Lines & text
    public Brush Ink => Mix(0x11191C, 0xF5F5F5);
    public Brush Outline => Mix(0x111111, 0x000000);
    public Brush Muted => Mix(0x697176, 0x9AA0A3);
    public Brush Line => Mix(0xEEEEF0, 0x333333);

    // Chat accent palette
    public Brush Blue => Mix(0xC8E7FF, 0x1473C8);
    public Brush BlueDark => Mix(0x1473C8, 0x7AB8F5);
    public Brush ComposerLine => Mix(0xC7E4F9, 0x2A4A66);
    public Brush SendDisabled => Mix(0xF3F5F6, 0x333333);
    public Brush SendDisabledIcon => Mix(0x9CB5C5, 0x666666);
}
