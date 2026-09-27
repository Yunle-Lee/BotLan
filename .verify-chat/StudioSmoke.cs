using System.IO;
using System.Reflection;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using IslandUI;
using IslandUI.Bots;
using IslandUI.Services;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.Wpf;

internal static class StudioSmoke
{
    private const BindingFlags Private = BindingFlags.NonPublic | BindingFlags.Instance;
    private static int _failures;

    public static int Run(App app, string output)
    {
        // Separate backend database/browser profile. No real configuration is saved,
        // no real model is contacted, and no external app is controlled.
        Environment.SetEnvironmentVariable("ISLANDUI_TEST_DATA_DIR", Path.Combine(output, "data"));
        Environment.SetEnvironmentVariable("TASK_WORKER_ENABLED", "false");
        var frame = new DispatcherFrame();
        app.Dispatcher.BeginInvoke(async () =>
        {
            ChatWindow? window = null;
            try
            {
                AgentStore.Instance.Bots.Clear();
                foreach (var name in new[] { "Fixture A", "Fixture B", "很长的中文Bot名称测试" })
                    AgentStore.Instance.Bots.Add(new AgentBot { Config = new AgentConfig
                    { Id = name.Replace(" ", "-"), Name = name, ApiKey = "unused-test-key", BaseUrl = "http://127.0.0.1:1/v1",
                        ColorHex = name == "Fixture A" ? "#F45E93" : "#4C9DFF", BotShape = name == "Fixture A" ? "cercle" : "triangle" } });
                window = (ChatWindow)Activator.CreateInstance(typeof(ChatWindow), Private, null,
                    [AgentStore.Instance.Bots[0], null], null)!;
                window.Show();
                window.Left = 60;
                window.Top = 40;
                var view = await WaitForView(window);
                await WaitForText(view, "Fixture A");
                Check(!await ScriptBool(view, "JSON.stringify(window.__ISLAND_HOST__).includes('unused-test-key')"), "API key never enters the webpage");
                Check(await ScriptBool(view, "document.querySelectorAll('[role=tab]').length >= 6"), "full Chat and Code navigation is embedded");
                await Task.Delay(1200);
                var avatar = (BotView)window.FindName("StudioBotAvatar");
                Check(avatar.IsVisible && avatar.Liveliness && avatar.Shape == "cercle"
                    && ((SolidColorBrush)avatar.BodyColor).Color == (Color)ColorConverter.ConvertFromString("#F45E93"),
                    "page uses selected Bot's real animated color and shape");
                Check(await ScriptBool(view, "!!document.querySelector('#island-left-dock #island-bot-avatar') && !document.querySelector('#island-left-dock img')"), "static mascot is replaced in the left identity dock");
                Check(await ScriptBool(view, "(() => { const buttons=[...document.querySelectorAll('#island-left-dock [role=button]')].map(x=>x.getBoundingClientRect()); return buttons.length===5 && buttons.every(r=>Math.abs(r.x+r.width/2-(buttons[0].x+buttons[0].width/2))<1) && buttons.every((r,i)=>!i || r.y>=buttons[i-1].bottom); })()"), "menu, identity, language, bell and computer are vertically aligned on the left");
                Check(await ScriptBool(view, "document.querySelector('#island-main-content').getBoundingClientRect().top < 16 && document.querySelector('#island-composer').getBoundingClientRect().width < window.innerWidth - 158"), "central content gains header height and the composer is narrower");
                Check(UnscaledPage(window, view), "browser has no Viewbox, shadow effect or fractional scaling ancestor");
                var firstFrame = AvatarPixels(avatar);
                AgentStore.Instance.Bots[0].State = AgentState.Working;
                await Task.Delay(450);
                Check(avatar.State == "thinking", "Bot work state reaches the native icon");
                var thinkingFrame = AvatarPixels(avatar);
                Check(firstFrame.Any(b => b != 0) && thinkingFrame.Any(b => b != 0)
                    && !firstFrame.SequenceEqual(thinkingFrame), "native Bot renders different idle and working animation frames");
                AgentStore.Instance.Bots[0].State = AgentState.Idle;
                await Capture(view, Path.Combine(output, "studio-page.png"));
                Render(window, Path.Combine(output, "studio-window.png"));

                var layer = (Canvas)window.FindName("OrbitLayer");
                var items = (Canvas)window.FindName("OrbitItems");
                string before = Shapes(items);
                var clipBefore = layer.Clip.ToString();
                var initialPoint = layer.TranslatePoint(new Point(), (FrameworkElement)window.Content);
                var card = (Border)window.FindName("ChatCard");
                var initialCardPoint = card.TranslatePoint(new Point(), (FrameworkElement)window.Content);
                window.Left += 63;
                window.Top += 37;
                await Task.Delay(250);
                Check(before == Shapes(items) && clipBefore == layer.Clip.ToString(), "moving the native window preserves all orbit shapes and clipping");
                Check(initialPoint == layer.TranslatePoint(new Point(), (FrameworkElement)window.Content)
                    && initialCardPoint == card.TranslatePoint(new Point(), (FrameworkElement)window.Content), "Chat and rail retain their exact relative positions after movement");
                layer.RaiseEvent(new MouseWheelEventArgs(Mouse.PrimaryDevice, Environment.TickCount, -120)
                    { RoutedEvent = Mouse.PreviewMouseWheelEvent });
                await WaitForText(view, "Fixture B");
                Check(((TextBlock)window.FindName("TitleText")).Text == "Fixture B", "wheel selects the same Bot in native rail and embedded page");
                Check(ReferenceEquals(view, typeof(ChatWindow).GetField("_studioView", Private)!.GetValue(window)), "Bot switch reuses one WebView instance");
                await Task.Delay(350);
                Check(avatar.IsVisible && avatar.Shape == "triangle"
                    && ((SolidColorBrush)avatar.BodyColor).Color == (Color)ColorConverter.ConvertFromString("#4C9DFF"),
                    "wheel switch also changes the native animated icon identity");
                await Capture(view, Path.Combine(output, "studio-bot-b.png"));
                Render(window, Path.Combine(output, "studio-bot-b-window.png"));
                await view.CoreWebView2.ExecuteScriptAsync("document.querySelector('#island-left-dock [role=button]').click()");
                await Task.Delay(400);
                Check(!avatar.IsVisible, "native avatar does not cover an open menu sheet");
                // Close the sheet through its own app handler, without touching other applications.
                await view.CoreWebView2.ExecuteScriptAsync("document.querySelector('[aria-label=\"Close details\"]')?.click()");
                await Task.Delay(350);
                Check(avatar.IsVisible, "animated icon returns after closing the menu");
                // A responsive viewport must reflow the page, not resample a large browser bitmap.
                window.Width = 836;
                window.Height = 640;
                await Task.Delay(650);
                Check(UnscaledPage(window, view), "smaller window still renders the browser at actual size");
                Check(await ScriptBool(view, "document.querySelector('#island-right-dock').getBoundingClientRect().bottom <= window.innerHeight"), "all six right-side actions fit the smaller viewport");
                await Capture(view, Path.Combine(output, "studio-small-page.png"));
                Render(window, Path.Combine(output, "studio-small-window.png"));
                window.Width = 1036;
                window.Height = 800;
                await Task.Delay(650);
                await CheckLanguages(view, output);
                // A functional test of this embedded app only; never sends a command/model request.
                await view.CoreWebView2.ExecuteScriptAsync("Array.from(document.querySelectorAll('[role=tab]')).find(x => /Code|代码/.test(x.getAttribute('aria-label') || ''))?.click()");
                await Task.Delay(900);
                Check(await ScriptBool(view, "/代码工作台|Code Workbench/.test(document.body.innerText) && /工作区文件|Workspace files/i.test(document.body.innerText)"), "provided Code workbench remains available");
                await Capture(view, Path.Combine(output, "studio-code.png"));
                Console.WriteLine("PAGE: " + await view.CoreWebView2.ExecuteScriptAsync("document.body.innerText.slice(0, 900)"));
                window.Close();
                Check(!(bool)typeof(BotView).GetField("_attached", Private)!.GetValue(avatar)!, "closing Chat detaches the new avatar's animation callback");
                window = null;
                var runtimeType = typeof(ChatWindow).Assembly.GetType("IslandUI.Services.ChatStudioRuntime")!;
                var runtime = runtimeType.GetProperty("Shared", BindingFlags.Public | BindingFlags.Static)!.GetValue(null)!;
                await (Task)runtimeType.GetMethod("StopAsync")!.Invoke(runtime, null)!;
                Check(runtimeType.GetField("_process", Private)!.GetValue(runtime) == null, "explicit runtime stop releases its owned backend process");
            }
            catch (Exception ex)
            {
                _failures++;
                Console.WriteLine("FAIL " + ex);
                if (window != null)
                {
                    Console.WriteLine("LOAD MESSAGE: " + ((TextBlock)window.FindName("StudioLoadMessage")).Text);
                    Render(window, Path.Combine(output, "studio-failure.png"));
                }
            }
            finally
            {
                window?.Close();
                Console.WriteLine($"Studio failures: {_failures}");
                frame.Continue = false;
            }
        });
        Dispatcher.PushFrame(frame);
        return _failures == 0 ? 0 : 1;
    }

    private static async Task CheckLanguages(WebView2CompositionControl view, string output)
    {
        if (await ScriptBool(view, "!document.querySelector('[role=tab][aria-label=\"动态\"]')"))
            await view.CoreWebView2.ExecuteScriptAsync("document.querySelector('[aria-label=\"Toggle language\"]').click()");
        await Task.Delay(250);
        var pages = new[] {
            (Tab: "动态", En: "Activity", Words: new[] { "任务状态", "全部", "进行中", "已结束", "审批与执行记录", "工作区时间线" }),
            (Tab: "灵感", En: "Ideas", Words: new[] { "建议灵感", "获取建议", "基于你已连接的应用推荐", "发现值得尝试的新灵感" }),
            (Tab: "目标", En: "Goals", Words: new[] { "长期目标", "持续监控", "创建目标", "健康", "人际关系", "财务" }),
            (Tab: "应用", En: "Apps", Words: new[] { "工具与应用", "搜索应用", "可用集成", "本地工具", "个性与记忆" }),
        };
        foreach (var page in pages)
        {
            await view.CoreWebView2.ExecuteScriptAsync("document.querySelector('[role=tab][aria-label=\"" + page.Tab + "\"]').click()");
            await WaitForText(view, page.Words[0]);
            await Task.Delay(300);
            Check(await ScriptBool(view, JsonSerializer.Serialize(page.Words) + ".every(s => document.body.innerText.includes(s))"), page.En + " page controls, empty state and labels are Chinese");
            await Capture(view, Path.Combine(output, "language-zh-" + page.En + ".png"));
            await view.CoreWebView2.ExecuteScriptAsync("document.querySelector('[aria-label=\"Toggle language\"]').click()");
            await Task.Delay(250);
            Check(await ScriptBool(view, "!!document.querySelector('[role=tab][aria-label=\"" + page.En + "\"]') && !document.body.innerText.includes('" + page.Words[0] + "')"), page.En + " page switches back to English without reload");
            await view.CoreWebView2.ExecuteScriptAsync("document.querySelector('[aria-label=\"Toggle language\"]').click()");
            await Task.Delay(250);
        }
        await view.CoreWebView2.ExecuteScriptAsync("document.querySelector('[role=tab][aria-label=\"目标\"]').click()");
        await Task.Delay(250);
        await view.CoreWebView2.ExecuteScriptAsync("document.querySelector('[aria-label=\"创建健康目标\"]').click()");
        await WaitForText(view, "阶段目标（每行一项）");
        Check(await ScriptBool(view, "document.body.innerText.includes('达到什么状态算完成？')"), "goal creation form is translated without changing its stored category");
        await Task.Delay(600); // Wait for the existing sheet fade before visual QA.
        await Capture(view, Path.Combine(output, "language-zh-GoalForm.png"));
        await view.CoreWebView2.ExecuteScriptAsync("document.querySelector('[aria-label=\"Close details\"]').click()");
        await Task.Delay(250);
    }

    private static async Task<WebView2CompositionControl> WaitForView(ChatWindow window)
    {
        for (int i = 0; i < 360; i++)
        {
            if (typeof(ChatWindow).GetField("_studioView", Private)!.GetValue(window) is WebView2CompositionControl view
                && view.CoreWebView2 != null) return view;
            var retry = (Button)window.FindName("StudioRetry");
            if (retry.IsVisible) throw new InvalidOperationException(((TextBlock)window.FindName("StudioLoadMessage")).Text);
            await Task.Delay(250);
        }
        throw new TimeoutException("WebView did not initialize.");
    }
    private static async Task WaitForText(WebView2CompositionControl view, string text)
    {
        for (int i = 0; i < 160; i++)
        {
            if (await ScriptBool(view, "document.body && document.body.innerText.includes(" + JsonSerializer.Serialize(text) + ")")) return;
            await Task.Delay(250);
        }
        throw new TimeoutException("Page did not show " + text + ": " + await view.CoreWebView2.ExecuteScriptAsync("document.body.innerText"));
    }
    private static async Task<bool> ScriptBool(WebView2CompositionControl view, string expression)
        => await view.CoreWebView2.ExecuteScriptAsync("Boolean(" + expression + ")") == "true";
    private static string Shapes(Canvas items) => string.Join("|", items.Children.OfType<Canvas>()
        .SelectMany(tab => tab.Children.OfType<System.Windows.Shapes.Path>()).Select(shape => shape.Data.ToString()));
    private static bool UnscaledPage(Window window, FrameworkElement view)
    {
        for (DependencyObject? parent = view; parent != null; parent = VisualTreeHelper.GetParent(parent))
            if (parent is Viewbox || parent is UIElement { Effect: not null }) return false;
        var t = view.TransformToAncestor((Visual)window.Content);
        var a = t.Transform(new Point());
        var b = t.Transform(new Point(100, 100));
        var dpi = VisualTreeHelper.GetDpi(view);
        return Math.Abs(b.X - a.X - 100) < .001 && Math.Abs(b.Y - a.Y - 100) < .001
            && Math.Abs(a.X * dpi.DpiScaleX - Math.Round(a.X * dpi.DpiScaleX)) < .01
            && Math.Abs(a.Y * dpi.DpiScaleY - Math.Round(a.Y * dpi.DpiScaleY)) < .01;
    }
    private static byte[] AvatarPixels(BotView avatar)
    {
        var image = new RenderTargetBitmap(64, 64, 96, 96, PixelFormats.Pbgra32);
        // Render its local visual, not its Canvas offset (which lies outside this 64px target).
        var visual = new DrawingVisual();
        using (var drawing = visual.RenderOpen())
            drawing.DrawRectangle(new VisualBrush(avatar) { Stretch = Stretch.Fill }, null, new Rect(0, 0, 64, 64));
        image.Render(visual);
        var pixels = new byte[64 * 64 * 4];
        image.CopyPixels(pixels, 64 * 4, 0);
        return pixels;
    }
    private static void Check(bool success, string label)
    {
        if (!success) _failures++;
        Console.WriteLine($"{(success ? "PASS" : "FAIL")} {label}");
    }
    private static async Task Capture(WebView2CompositionControl view, string path)
    {
        using var file = File.Create(path);
        await view.CoreWebView2.CapturePreviewAsync(CoreWebView2CapturePreviewImageFormat.Png, file);
    }
    private static void Render(Window window, string path)
    {
        var root = (FrameworkElement)window.Content;
        var image = new RenderTargetBitmap((int)root.ActualWidth, (int)root.ActualHeight, 96, 96, PixelFormats.Pbgra32);
        image.Render(root);
        var backdrop = new DrawingVisual();
        using (var drawing = backdrop.RenderOpen())
        {
            drawing.DrawRectangle(new SolidColorBrush(Color.FromRgb(232, 234, 239)), null,
                new Rect(0, 0, root.ActualWidth, root.ActualHeight));
            drawing.DrawImage(image, new Rect(0, 0, root.ActualWidth, root.ActualHeight));
        }
        var preview = new RenderTargetBitmap((int)root.ActualWidth, (int)root.ActualHeight, 96, 96, PixelFormats.Pbgra32);
        preview.Render(backdrop);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(preview));
        using var file = File.Create(path);
        encoder.Save(file);
    }
}
