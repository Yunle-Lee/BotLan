using System.Drawing;
using System.Windows;
using System.Windows.Forms;
using Application = System.Windows.Application;

namespace IslandUI;

public partial class App : Application
{
    private NotifyIcon? _trayIcon;
    private Window? _islandWindow;

    protected override void OnStartup(StartupEventArgs e)
    {
        ShutdownMode = ShutdownMode.OnExplicitShutdown;
        base.OnStartup(e);
        SetupTrayIcon();
        OpenCurrentMode();

#if DEBUG
        // Let a development restart open the changed Chat directly, without UI automation.
        if (e.Args.Contains("--chat") && Services.AgentStore.Instance.Bots.FirstOrDefault() is { } bot)
            Dispatcher.BeginInvoke(() => ChatWindow.OpenFor(bot, _islandWindow as LongIslandWindow));
#endif

        IslandSettings.Instance.PropertyChanged += (_, args) =>
        {
            if (args.PropertyName is nameof(IslandSettings.Mode))
                Dispatcher.Invoke(OpenCurrentMode);
        };
    }

    /// <summary>四种形态同一时刻只显示一个岛;变身时胶囊保持原位(只有内容和长度变化).</summary>
    private void OpenCurrentMode()
    {
        // 记录旧窗口里胶囊的屏幕位置
        double? capsuleX = null;
        double? top = null;
        if (_islandWindow is SmallIslandWindow s) { capsuleX = s.Left + 88; top = s.Top; }
        else if (_islandWindow is LongIslandWindow l) { capsuleX = l.Left + 302; top = l.Top; }

        _islandWindow?.Close();
        _islandWindow = IslandSettings.Instance.Mode switch
        {
            "long" => new LongIslandWindow(),
            _ => new SmallIslandWindow(),
        };

        // 新窗口的胶囊放回同一屏幕位置
        if (capsuleX != null)
        {
            _islandWindow.Left = _islandWindow is SmallIslandWindow
                ? capsuleX.Value - 88
                : capsuleX.Value - 302;
            _islandWindow.Top = top!.Value;
        }
        _islandWindow.Show();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        if (_trayIcon != null)
        {
            _trayIcon.Visible = false;
            _trayIcon.Dispose();
        }
        base.OnExit(e);
    }

    internal LongIslandWindow? PrepareLongIslandReturn()
    {
        if (_islandWindow is LongIslandWindow existing) return existing;
        // A normal Bot opens from Long Island. This covers the DEBUG --chat entry
        // (or a mode change during Chat), returning through the existing mode system.
        if (_islandWindow == null) return null;
        IslandSettings.Instance.Mode = "long";
        return _islandWindow as LongIslandWindow;
    }

    private void SetupTrayIcon()
    {
        var menu = new ContextMenuStrip();
        var smallItem = new ToolStripMenuItem("小岛（WiFi/电池）");
        smallItem.Click += (_, _) => Dispatcher.Invoke(() => IslandSettings.Instance.Mode = "small");
        var longItem = new ToolStripMenuItem("长岛（Agents）");
        longItem.Click += (_, _) => Dispatcher.Invoke(() => IslandSettings.Instance.Mode = "long");
        var settingsItem = new ToolStripMenuItem("设置");
        settingsItem.Click += (_, _) => Dispatcher.Invoke(IslandSettingsWindow.ShowSingleton);
        var exitItem = new ToolStripMenuItem("退出");
        exitItem.Click += (_, _) => Shutdown();

        menu.Items.Add(smallItem);
        menu.Items.Add(longItem);
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add(settingsItem);
        menu.Items.Add(exitItem);

        _trayIcon = new NotifyIcon
        {
            Text = "IslandUI — 灵动岛",
            Icon = CreateIcon(),
            Visible = true,
            ContextMenuStrip = menu,
        };
        _trayIcon.DoubleClick += (_, _) => Dispatcher.Invoke(IslandSettingsWindow.ShowSingleton);
    }

    private static Icon CreateIcon()
    {
        const int size = 32;
        var bmp = new Bitmap(size, size);
        using (var g = Graphics.FromImage(bmp))
        {
            g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
            g.Clear(System.Drawing.Color.Transparent);
            using var bg = new SolidBrush(System.Drawing.Color.Black);
            g.FillEllipse(bg, 2, 10, size - 4, size - 22);
            using var fg = new SolidBrush(System.Drawing.Color.White);
            g.FillEllipse(fg, 10, 15, 4, 4);
            g.FillEllipse(fg, 19, 15, 4, 4);
        }
        return Icon.FromHandle(bmp.GetHicon());
    }
}
