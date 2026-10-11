using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using IslandUI.Services;

namespace IslandUI;

/// <summary>
/// "连接 DGX Spark": SSH in once with a password, pair the Spark's Bots, install a
/// DPAPI-protected key, and keep a local tunnel. All network work runs off the UI thread.
/// </summary>
public partial class SparkConnectWindow : Window
{
    private static SparkConnectWindow? _instance;

    private static readonly (SparkStep Step, string Label)[] Steps =
    [
        (SparkStep.Ssh, "连接 SSH"),
        (SparkStep.Check, "检查 BotLan"),
        (SparkStep.Install, "安装 BotLan"),
        (SparkStep.Pair, "配对"),
        (SparkStep.Tunnel, "建立隧道"),
        (SparkStep.AddBots, "添加 Bot"),
    ];

    private readonly Dictionary<SparkStep, (TextBlock Icon, TextBlock Detail, FrameworkElement Row)> _rows = [];
    private CancellationTokenSource? _cts;
    private bool _busy;

    public static void ShowSingleton()
    {
        if (_instance is { IsLoaded: true })
        {
            _instance.Activate();
            return;
        }
        _instance = new SparkConnectWindow();
        _instance.Show();
    }

    public SparkConnectWindow()
    {
        InitializeComponent();
        BuildSteps();
        if (SparkConnectionStore.Load().LastOrDefault() is { } last)
        {
            AddressBox.Text = $"{last.User}@{last.Host}";
            PortBox.Text = last.Port.ToString();
            PasswordLabel.Text = "密码（已保存密钥，可留空）";
        }
        Loaded += (_, _) =>
        {
            AddressBox.Focus();
            AddressBox.CaretIndex = AddressBox.Text.Length;
        };
    }

    protected override void OnClosed(EventArgs e)
    {
        _cts?.Cancel();
        PasswordBox.Clear();
        base.OnClosed(e);
    }

    private void BuildSteps()
    {
        foreach (var (step, label) in Steps)
        {
            var icon = new TextBlock
            {
                Width = 22, FontFamily = new FontFamily("Comic Sans MS"), FontSize = 13,
                Text = "○", VerticalAlignment = VerticalAlignment.Center,
            };
            icon.SetBinding(TextBlock.ForegroundProperty, ThemeBinding(nameof(ComicTheme.Muted)));
            var name = new TextBlock
            {
                Text = label, FontFamily = new FontFamily("Comic Sans MS"), FontSize = 12.5,
                Width = 96, VerticalAlignment = VerticalAlignment.Center,
            };
            name.SetBinding(TextBlock.ForegroundProperty, ThemeBinding(nameof(ComicTheme.Ink)));
            var detail = new TextBlock
            {
                FontFamily = new FontFamily("Comic Sans MS"), FontSize = 11,
                TextTrimming = TextTrimming.CharacterEllipsis, VerticalAlignment = VerticalAlignment.Center,
            };
            detail.SetBinding(TextBlock.ForegroundProperty, ThemeBinding(nameof(ComicTheme.Muted)));
            var row = new DockPanel { Margin = new Thickness(0, 3, 0, 3) };
            DockPanel.SetDock(icon, Dock.Left);
            DockPanel.SetDock(name, Dock.Left);
            row.Children.Add(icon);
            row.Children.Add(name);
            row.Children.Add(detail);
            // Install only shows when needed.
            if (step == SparkStep.Install) row.Visibility = Visibility.Collapsed;
            StepList.Children.Add(row);
            _rows[step] = (icon, detail, row);
        }
    }

    private static System.Windows.Data.Binding ThemeBinding(string path)
        => new(path) { Source = ComicTheme.Current };

    private void SetStep(SparkStep step, SparkStepState state, string? detail)
    {
        var (icon, text, row) = _rows[step];
        if (step == SparkStep.Install && state != SparkStepState.Skipped && state != SparkStepState.Pending)
            row.Visibility = Visibility.Visible;
        (icon.Text, icon.Foreground) = state switch
        {
            SparkStepState.Running => ("◔", Brush("#1473C8")),
            SparkStepState.Done => ("✓", Brush("#2E9E6A")),
            SparkStepState.Failed => ("✗", Brush("#D8453B")),
            SparkStepState.Skipped => ("–", ComicTheme.Current.Muted),
            _ => ("○", ComicTheme.Current.Muted),
        };
        text.Text = state == SparkStepState.Failed ? "失败" : detail ?? "";
    }

    private static Brush Brush(string hex)
    {
        var b = new SolidColorBrush((Color)ColorConverter.ConvertFromString(hex));
        b.Freeze();
        return b;
    }

    private void AppendLog(string line)
    {
        if (LogBox.Text.Length > 200_000) LogBox.Text = LogBox.Text[^100_000..];
        LogBox.AppendText($"{DateTime.Now:HH:mm:ss}  {line}\n");
        LogBox.ScrollToEnd();
    }

    private void ShowMessage(string text, bool error)
    {
        Message.Text = text;
        Message.Foreground = error ? Brush("#D8453B") : ComicTheme.Current.Ink;
        Message.Visibility = Visibility.Visible;
    }

    // ---- actions ------------------------------------------------------------------------

    private void Connect_Click(object sender, RoutedEventArgs e) => _ = RunAsync(allowInstall: false);

    private void Install_Click(object sender, RoutedEventArgs e)
    {
        var answer = MessageBox.Show(this,
            "将在 Spark 上克隆 BotLan-CLI 并运行 scripts/08_botlan.sh --install。\n构建/下载模型可能需要 15 分钟以上。继续吗？",
            "在 Spark 上安装", MessageBoxButton.OKCancel, MessageBoxImage.Question);
        if (answer == MessageBoxResult.OK) _ = RunAsync(allowInstall: true);
    }

    private void PasswordBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter && !_busy) _ = RunAsync(allowInstall: false);
    }

    private void Done_Click(object sender, RoutedEventArgs e) => Close();

    private async Task RunAsync(bool allowInstall)
    {
        if (_busy) return;
        SparkTarget target;
        try
        {
            if (!int.TryParse(PortBox.Text.Trim(), out var port)) throw new SparkException("SSH 端口应是数字。");
            target = SparkTarget.Parse(AddressBox.Text, port);
        }
        catch (SparkException ex)
        {
            ShowMessage(ex.Message, error: true);
            return;
        }

        _busy = true;
        Form.IsEnabled = false;
        ActionRow.Visibility = Visibility.Collapsed;
        InstallButton.Visibility = Visibility.Collapsed;
        Message.Visibility = Visibility.Collapsed;
        foreach (var (step, _) in Steps) SetStep(step, SparkStepState.Pending, null);
        _rows[SparkStep.Install].Row.Visibility = allowInstall ? Visibility.Visible : Visibility.Collapsed;
        _cts = new CancellationTokenSource();

        var flow = new SparkConnectFlow(new SparkFlowOptions { AllowInstall = allowInstall });
        flow.StepChanged += (s, st, d) => Dispatcher.BeginInvoke(() => SetStep(s, st, d));
        flow.Log += line => Dispatcher.BeginInvoke(() => AppendLog(line));
        var password = PasswordBox.Password;
        var existing = AgentStore.Instance.Bots.Select(b => b.Config).ToList();

        try
        {
            AppendLog($"连接 {target} …");
            var result = await Task.Run(() => flow.RunAsync(target, password, () => existing, _cts.Token));
            foreach (var config in result.NewBots) AgentStore.Instance.AddAgent(config);
            PasswordBox.Clear();

            var names = result.NewBots.Select(b => b.Name).ToList();
            SetStep(SparkStep.AddBots, SparkStepState.Done, names.Count > 0 ? string.Join("、", names) : "没有新 Bot");
            var text = names.Count > 0
                ? $"已添加 {names.Count} 个 Bot：{string.Join("、", names)}。"
                : "这些 Bot 已经在长岛上了。";
            if (result.SkippedDuplicates.Count > 0)
                text += $"\n跳过 {result.SkippedDuplicates.Count} 个已存在的：{string.Join("、", result.SkippedDuplicates.Select(b => b.Name))}。";
            text += $"\n隧道 127.0.0.1:{result.Connection.LocalPort} 会在 BotLan 启动时自动连接。";
            ShowMessage(text, error: false);
            ActionRow.Visibility = Visibility.Visible;
            DoneButton.Visibility = Visibility.Visible;
            AppendLog("完成。");
            PasswordLabel.Text = "密码（已保存密钥，可留空）";
            // Leave the summary up briefly, then close on success.
            await Task.Delay(TimeSpan.FromSeconds(4));
            if (IsLoaded && IsActive) Close();
        }
        catch (SparkNotInstalledException ex)
        {
            ShowMessage(ex.Message + "\n可以一键安装（克隆 BotLan-CLI 并运行 08_botlan.sh --install），装好后在 Spark 上运行 sh scripts/09_setup.sh 定义 Bot、目录范围和后端。", error: true);
            ActionRow.Visibility = Visibility.Visible;
            InstallButton.Visibility = Visibility.Visible;
            DoneButton.Visibility = Visibility.Collapsed;
        }
        catch (Exception ex)
        {
            ShowMessage(ex.Message, error: true);
            AppendLog("错误：" + ex.Message);
        }
        finally
        {
            _busy = false;
            if (IsLoaded) Form.IsEnabled = true;
        }
    }

    // ---- chrome -------------------------------------------------------------------------

    private void Header_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.OriginalSource is TextBox || e.OriginalSource is PasswordBox || e.OriginalSource is Button) return;
        if (e.ButtonState == MouseButtonState.Pressed) try { DragMove(); } catch (InvalidOperationException) { }
    }

    private void Close_Click(object sender, MouseButtonEventArgs e) => Close();
}
