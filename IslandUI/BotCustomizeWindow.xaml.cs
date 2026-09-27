using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using IslandUI.Bots;

namespace IslandUI;

/// <summary>Bot 定制器:形态 8 种 / 颜色 12 种 / 表情 16 种,实时预览.</summary>
public partial class BotCustomizeWindow : Window
{
    private static readonly (string Id, string Label)[] ShapeLabels =
    [
        ("cercle", "圆形"), ("galet", "鹅卵石"), ("squircle", "方圆"), ("capsule", "胶囊"),
        ("triangle", "三角"), ("hexagone", "六边"), ("nuage", "云朵"), ("goutte", "水滴"),
    ];

    private static readonly (string Id, string Label)[] ExpressionLabels =
    [
        ("neutre", "中性"), ("attentif", "专注"), ("surpris", "惊讶"), ("excite", "兴奋"),
        ("heureux", "开心"), ("hilare", "大笑"), ("colere", "生气"), ("triste", "难过"),
        ("effraye", "害怕"), ("mefiant", "狐疑"), ("confus", "困惑"), ("curieux", "好奇"),
        ("fier", "得意"), ("timide", "害羞"), ("blase", "无语"), ("somnolent", "困倦"),
    ];

    private readonly Action<string, string, string> _apply;
    private string _shape;
    private string _color;
    private string _expression;

    public static void OpenFor(string shape, string colorHex, string expression,
        Action<string, string, string> apply)
    {
        var w = new BotCustomizeWindow(shape, colorHex, expression, apply);
        w.Show();
    }

    private BotCustomizeWindow(string shape, string colorHex, string expression,
        Action<string, string, string> apply)
    {
        _apply = apply;
        _shape = shape;
        _color = colorHex;
        _expression = expression;
        InitializeComponent();
        BuildPickers();
        UpdatePreview();
    }

    private void BuildPickers()
    {
        foreach (var (id, label) in ShapeLabels)
        {
            var b = new Button
            {
                Content = label,
                Tag = id,
                Margin = new Thickness(0, 0, 6, 4),
                Padding = new Thickness(8, 3, 8, 3),
                FontSize = 11,
                FontFamily = new FontFamily("Comic Sans MS"),
                Cursor = System.Windows.Input.Cursors.Hand,
            };
            b.Click += (_, _) => { _shape = id; UpdatePreview(); };
            ShapeRow.Children.Add(b);
        }

        foreach (var c in BotSkins.Colors)
        {
            var swatch = new Border
            {
                Width = 22,
                Height = 22,
                CornerRadius = new CornerRadius(11),
                Background = new SolidColorBrush((Color)ColorConverter.ConvertFromString(c.Hex)),
                Margin = new Thickness(0, 0, 6, 4),
                Cursor = System.Windows.Input.Cursors.Hand,
                Tag = c.Hex,
                ToolTip = c.Id,
            };
            swatch.MouseLeftButtonUp += (_, _) => { _color = c.Hex; UpdatePreview(); };
            ColorRow.Children.Add(swatch);
        }

        foreach (var (id, label) in ExpressionLabels)
        {
            var b = new Button
            {
                Content = label,
                Tag = id,
                Margin = new Thickness(0, 0, 6, 4),
                Padding = new Thickness(8, 3, 8, 3),
                FontSize = 11,
                FontFamily = new FontFamily("Comic Sans MS"),
                Cursor = System.Windows.Input.Cursors.Hand,
            };
            b.Click += (_, _) => { _expression = id; UpdatePreview(); };
            ExpressionRow.Children.Add(b);
        }
    }

    private void UpdatePreview()
    {
        var brush = new SolidColorBrush((Color)ColorConverter.ConvertFromString(_color));
        brush.Freeze();
        Preview.BodyColor = brush;
        Preview.Shape = _shape;
        Preview.Expression = _expression;
    }

    private void Done_Click(object sender, RoutedEventArgs e)
    {
        _apply(_shape, _color, _expression);
        Close();
    }

    private void Close_Click(object sender, System.Windows.Input.MouseButtonEventArgs e) => Close();
}
