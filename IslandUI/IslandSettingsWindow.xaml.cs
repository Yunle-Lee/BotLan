using System.Windows;

namespace IslandUI;

public partial class IslandSettingsWindow : Window
{
    private static IslandSettingsWindow? _instance;

    public static void ShowSingleton()
    {
        if (_instance is { IsLoaded: true })
        {
            _instance.Activate();
            return;
        }
        _instance = new IslandSettingsWindow();
        _instance.Show();
    }

    public IslandSettingsWindow()
    {
        InitializeComponent();
        DataContext = IslandSettings.Instance;
        MouseLeftButtonDown += (_, e) =>
        {
            // 控件(复选框/单选钮/关闭键)自己吃点击,背景区域按住即可拖拽
            if (e.OriginalSource is System.Windows.Controls.Primitives.ButtonBase) return;
            if (e.OriginalSource == CloseButton) return;
            if (e.LeftButton == System.Windows.Input.MouseButtonState.Pressed) DragMove();
        };
    }

    private void Close_Click(object sender, System.Windows.Input.MouseButtonEventArgs e) => Close();
}
