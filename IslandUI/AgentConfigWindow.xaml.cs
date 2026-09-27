using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using IslandUI.Services;

namespace IslandUI;

public partial class AgentConfigWindow : Window
{
    private static readonly string[] Palette =
        ["#3ECF8E", "#54A8FF", "#FF7A59", "#C792EA", "#FFD866", "#FF6188"];

    private static AgentConfigWindow? _instance;
    private string _color = Palette[0];
    private string _shape = "cercle";
    private string _expression = "neutre";

    public static void ShowSingleton()
    {
        if (_instance is { IsLoaded: true })
        {
            _instance.Activate();
            return;
        }
        _instance = new AgentConfigWindow();
        _instance.Show();
    }

    public AgentConfigWindow()
    {
        InitializeComponent();
        BuildPalette();
        UpdatePreview();
    }

    private void BuildPalette()
    {
        foreach (var hex in Palette)
        {
            var swatch = new Border
            {
                Width = 26,
                Height = 26,
                CornerRadius = new CornerRadius(13),
                Background = new SolidColorBrush((Color)ColorConverter.ConvertFromString(hex)),
                Margin = new Thickness(0, 0, 8, 0),
                Cursor = System.Windows.Input.Cursors.Hand,
                Tag = hex,
            };
            swatch.MouseLeftButtonUp += (_, _) =>
            {
                _color = hex;
                foreach (Border child in ColorRow.Children)
                    child.Effect = null;
                swatch.Effect = new System.Windows.Media.Effects.DropShadowEffect
                {
                    Color = (Color)ColorConverter.ConvertFromString(hex),
                    BlurRadius = 12,
                    ShadowDepth = 0,
                    Opacity = 0.9,
                };
            };
            ColorRow.Children.Add(swatch);
        }
    }

    private void Customize_Click(object sender, RoutedEventArgs e)
    {
        BotCustomizeWindow.OpenFor(_shape, _color, _expression, (shape, color, expression) =>
        {
            _shape = shape;
            _color = color;
            _expression = expression;
            UpdatePreview();
        });
    }

    private void UpdatePreview()
    {
        var brush = new SolidColorBrush((Color)ColorConverter.ConvertFromString(_color));
        brush.Freeze();
        Preview.BodyColor = brush;
        Preview.Shape = _shape;
        Preview.Expression = _expression;
    }

    private void Add_Click(object sender, RoutedEventArgs e)
    {
        AgentStore.Instance.AddAgent(new AgentConfig
        {
            Name = string.IsNullOrWhiteSpace(NameBox.Text) ? "Agent" : NameBox.Text.Trim(),
            BaseUrl = BaseUrlBox.Text.Trim(),
            Model = ModelBox.Text.Trim(),
            ApiKey = KeyBox.Password.Trim(),
            ColorHex = _color,
            BotShape = _shape,
            BotExpression = _expression,
            RingMode = RingQuota.IsChecked == true ? "quota" : "status",
        });
        Close();
    }

    private void Close_Click(object sender, System.Windows.Input.MouseButtonEventArgs e) => Close();
}
