using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using IslandUI;
using IslandUI.Services;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.Wpf;

internal static class FoldChecks
{
    private const BindingFlags Private = BindingFlags.Instance | BindingFlags.NonPublic;
    private static int _failures;
    public static int Run(App app, string output)
    {
        Environment.SetEnvironmentVariable("ISLANDUI_TEST_DATA_DIR", Path.Combine(output, "data"));
        Environment.SetEnvironmentVariable("TASK_WORKER_ENABLED", "false");
        var frame = new DispatcherFrame();
        app.Dispatcher.BeginInvoke(async () =>
        {
            ChatWindow? window = null;
            LongIslandWindow? source = null;
            try
            {
                AgentStore.Instance.Bots.Clear();
                for (int i = 0; i < 6; i++) AgentStore.Instance.Bots.Add(new AgentBot { Config = new AgentConfig {
                    Id = "fold-fixture-" + i, Name = "折叠测试 " + (i + 1), ColorHex = i == 0 ? "#F45E93" : "#4C9DFF",
                    ApiKey = "unused-fold-key", BaseUrl = "http://127.0.0.1:1/v1" } });
                source = new LongIslandWindow { Left = 800, Top = 80 };
                source.Show(); source.UpdateLayout(); source.Hide();
                window = (ChatWindow)Activator.CreateInstance(typeof(ChatWindow), Private, null,
                    [AgentStore.Instance.Bots[0], source], null)!;
                window.Left = 60; window.Top = 50; window.Show();
                SetHover(window, true);
                await Wait(async () => Field(window, "_studioView") is WebView2CompositionControl v && v.CoreWebView2 != null
                    && await Bool(v, "!!document.querySelector('textarea[placeholder*=\"输入消息\"],textarea[placeholder*=\"Message or instruction\"]')"), 100000);
                var view = (WebView2CompositionControl)Field(window, "_studioView")!;
                await Task.Delay(300);
                await view.CoreWebView2.ExecuteScriptAsync("(() => {const t=document.querySelector('textarea'); Object.getOwnPropertyDescriptor(HTMLTextAreaElement.prototype,'value').set.call(t,'折叠后保留的草稿'); t.dispatchEvent(new Event('input',{bubbles:true}));})()");
                await Task.Delay(150);
                Render(window, Path.Combine(output, "01-expanded.png"));
                var originalSize = new Size(window.Width, window.Height);
                var originalBot = Field(window, "_bot");
                ((Button)window.FindName("StudioCloseButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                await Wait(() => Task.FromResult(Phase(window) == "Folding"), 5000);
                var book = (Canvas)Field(window, "_bookFold")!;
                var card = (Rect)Field(window, "_foldCard")!;
                double BookValue(string property) => (double)book.GetType().GetProperty(property)!.GetValue(book)!;
                typeof(ChatWindow).GetMethod("RenderFold", Private)!.Invoke(window, [.36]);
                Check(Math.Abs(BookValue("LeafWidth") - card.Width / 2) < .01
                    && Math.Abs(BookValue("HingeX") - card.Width / 2) < .01,
                    "fold bisects the complete Chat window, not just its central content");
                Check(!book.Children.OfType<Image>().Any() && BookValue("CapsuleOpacity") == 0,
                    "both docks belong to the turning sheets; no separate floating toolbar strips");
                typeof(ChatWindow).GetMethod("RenderFold", Private)!.Invoke(window, [.72]);
                Check(BookValue("AngleDegrees") == 180 && Math.Abs(BookValue("HingeX") - card.Width / 2) < .01
                    && BookValue("CapsuleOpacity") == 0,
                    "full half-window closes 180 degrees before the two-column capsule appears");
                // Render deterministic points from the real transition visual, retaining
                // the running clock. No desktop capture or other application control.
                foreach (var p in new[] { .05, .25, .36, .45, .6, .72, .78, .93 })
                {
                    typeof(ChatWindow).GetMethod("RenderFold", Private)!.Invoke(window, [p]);
                    window.UpdateLayout();
                    Render(window, Path.Combine(output, $"fold-{p:0.00}.png"));
                }
                await Wait(() => Task.FromResult(Phase(window) == "Folded"), 6000);
                await Task.Delay(350);
                Check(Phase(window) == "Folded" && window.Width < 230, "close control folds to a compact real window");
                var expanded = (Rect)Field(window, "_expandedWindow")!;
                Check(Math.Abs(window.Left + window.Width - 8 - (expanded.Left + card.Right)) < 1
                    && Math.Abs(window.Top + 8 - (expanded.Top + card.Top)) < 1,
                    "folded capsule stays at the animation endpoint without a position jump");
                Check(ReferenceEquals(originalBot, Field(window, "_bot")), "departing rail never auto-selects another Bot");
                Check(ReferenceEquals(view, Field(window, "_studioView")), "fold retains the same live WebView");
                Check(await Bool(view, "document.documentElement.dataset.islandFolded === 'true' && getComputedStyle(document.querySelector('#island-main-content')).visibility === 'hidden'"), "only the central content is hidden in folded mode");
                Check(await Bool(view, "document.querySelectorAll('#island-right-dock [role=tab]').length === 6 && document.querySelectorAll('#island-left-dock [role=button]').length === 5"), "both original button docks remain functional, no duplicate controls");
                Check(await Bool(view, "document.querySelector('#island-right-dock').getBoundingClientRect().right <= innerWidth && document.querySelector('#island-left-dock').getBoundingClientRect().right < document.querySelector('#island-right-dock').getBoundingClientRect().left"), "folded docks do not overlap or extend beyond the capsule");
                Check(((FrameworkElement)window.FindName("OrbitLayer")).Opacity == 0, "upper-right rail slides away and disappears");
                Render(window, Path.Combine(output, "02-folded.png"));
                using (var file = File.Create(Path.Combine(output, "02-folded-page.png")))
                    await view.CoreWebView2.CapturePreviewAsync(CoreWebView2CapturePreviewImageFormat.Png, file);
                // Crossing out and back during the grace period must not close the capsule.
                SetHover(window, false); await Task.Delay(250); SetHover(window, true); await Task.Delay(800);
                Check(Phase(window) == "Folded", "brief mouse leave/reentry cancels the return grace period");
                await view.CoreWebView2.ExecuteScriptAsync("[...document.querySelectorAll('#island-right-dock [role=tab]')].find(x=>/Code|代码/.test(x.getAttribute('aria-label'))).click()");
                await Wait(() => Task.FromResult(Phase(window) == "Expanded"), 6000);
                await Task.Delay(400);
                Check(window.Width == originalSize.Width && window.Height == originalSize.Height, "dock action restores the original Chat dimensions");
                Check(await Bool(view, "/代码工作台|Code Workbench/.test(document.body.innerText)"), "folded Code button opens the existing Code workbench");
                Check(await Bool(view, "[...document.querySelectorAll('textarea')].some(x => x.value === '折叠后保留的草稿')"), "unsent draft survives fold and unfold");
                Check(ReferenceEquals(view, Field(window, "_studioView")), "unfold does not reconnect or replace WebView");
                Render(window, Path.Combine(output, "03-restored.png"));
                await Invoke(window, "FoldChatAsync");
                await Task.Delay(250);
                // Return must use the capsule's current position, including a drag.
                window.Left += 32; window.Top += 24;
                SetHover(window, false);
                await Wait(() => Task.FromResult(Phase(window) == "Returning"), 3000);
                await Wait(() => Task.FromResult(Field(window, "_returnSurface") != null), 5000);
                var outline = (Border)Field(window, "_returnSurface")!;
                Check(outline.Child == null && outline.BorderBrush != null && outline.BorderThickness.Left > 0,
                    "return uses a vector capsule outline, not stretched screenshots");
                var from = (Rect)Field(window, "_returnFrom")!;
                var to = (Rect)Field(window, "_returnTo")!;
                Console.WriteLine($"Return anchor delta: centre={to.Left + to.Width / 2 - from.Left - from.Width / 2:F3}, top={to.Top - from.Top:F3} DIP");
                // Native HWND edges are quantized to physical screen pixels.
                var dpi = VisualTreeHelper.GetDpi(window);
                Check(Math.Abs(from.Left + from.Width / 2 - to.Left - to.Width / 2) <= 1 / dpi.DpiScaleX
                    && Math.Abs(from.Top - to.Top) <= 1 / dpi.DpiScaleY,
                    "Long Island reforms in place instead of travelling to its old desktop position");
                bool monotonic = true;
                double lastWidth = from.Width;
                for (int i = 0; i <= 100; i++)
                {
                    typeof(ChatWindow).GetMethod("RenderReturn", Private)!.Invoke(window, [i / 100d]);
                    double x = Canvas.GetLeft(outline), y = Canvas.GetTop(outline);
                    monotonic &= outline.Width <= lastWidth + .001 && outline.Width >= to.Width - .001
                        && x >= Math.Min(from.Left, to.Left) - .001 && x <= Math.Max(from.Left, to.Left) + .001
                        && y >= Math.Min(from.Top, to.Top) - .001 && y <= Math.Max(from.Top, to.Top) + .001;
                    lastWidth = outline.Width;
                }
                Check(monotonic, "capsule outline returns monotonically without overshoot or rebound");
                var target = (Border)source.FindName("Island");
                Check(Math.Abs(outline.Width - target.ActualWidth) < .01 && outline.CornerRadius == target.CornerRadius,
                    "return ends at the original Long Island outline and corner radii");
                foreach (var p in new[] { .05, .3, .6, .9 })
                {
                    typeof(ChatWindow).GetMethod("RenderReturn", Private)!.Invoke(window, [p]);
                    window.UpdateLayout();
                    Render(window, Path.Combine(output, $"return-{p:0.00}.png"));
                }
                await Wait(() => Task.FromResult(Phase(window) == "Closed"), 3000);
                Check(source.IsVisible && source.Opacity == 1, "mouse leave smoothly returns to the original Long Island");
                Check(Field(window, "_studioView") == null && Field(window, "_foldHoverTimer") == null
                    && ((Canvas)window.FindName("FoldLayer")).Children.Count == 0, "final return disposes browser, animation textures and hover timer");
                Render(source, Path.Combine(output, "05-long-island.png"));
                window = null;
                source.Hide();
                window = (ChatWindow)Activator.CreateInstance(typeof(ChatWindow), Private, null,
                    [AgentStore.Instance.Bots[0], source], null)!;
                window.Show();
                await Wait(async () => Field(window, "_studioView") is WebView2CompositionControl v && v.CoreWebView2 != null
                    && await Bool(v, "!!document.querySelector('#island-left-dock')"), 30000);
                var interrupted = Invoke(window, "FoldChatAsync");
                window.Close();
                await interrupted.WaitAsync(TimeSpan.FromSeconds(5));
                Check(Phase(window) == "Closed" && Field(window, "_studioView") == null
                    && Field(window, "_foldSnapshot") == null, "real window shutdown during capture cancels folding and frees its resources");
                window = null;
            }
            catch (Exception ex)
            {
                _failures++; Console.WriteLine("FAIL " + ex);
                if (window is { IsLoaded: true }) Render(window, Path.Combine(output, "failure.png"));
            }
            finally
            {
                window?.Close(); source?.Close();
                var runtimeType = typeof(ChatWindow).Assembly.GetType("IslandUI.Services.ChatStudioRuntime")!;
                await (Task)runtimeType.GetMethod("StopAsync")!.Invoke(runtimeType.GetProperty("Shared")!.GetValue(null), null)!;
                Console.WriteLine("Fold failures: " + _failures);
                frame.Continue = false;
            }
        });
        Dispatcher.PushFrame(frame);
        return _failures == 0 ? 0 : 1;
    }
    private static object? Field(object o, string name) => o.GetType().GetField(name, Private)!.GetValue(o);
    private static string Phase(ChatWindow window) => Field(window, "_foldPhase")!.ToString()!;
    private static Task Invoke(ChatWindow window, string name) => (Task)typeof(ChatWindow).GetMethod(name, Private)!.Invoke(window, null)!;
    private static void SetHover(ChatWindow window, bool value) => typeof(ChatWindow).GetProperty("FoldHoverOverride", Private)!.SetValue(window, (Func<bool>)(() => value));
    private static async Task<bool> Bool(WebView2CompositionControl view, string expression)
        => await view.CoreWebView2.ExecuteScriptAsync("Boolean(" + expression + ")") == "true";
    private static async Task Wait(Func<Task<bool>> condition, int timeout)
    {
        var watch = Stopwatch.StartNew();
        while (watch.ElapsedMilliseconds < timeout) { if (await condition()) return; await Task.Delay(35); }
        throw new TimeoutException("Fold check timed out.");
    }
    private static void Check(bool success, string message)
    { if (!success) _failures++; Console.WriteLine((success ? "PASS " : "FAIL ") + message); }
    private static void Render(Window window, string path)
    {
        var root = (FrameworkElement)window.Content;
        var bitmap = new RenderTargetBitmap((int)Math.Ceiling(root.ActualWidth), (int)Math.Ceiling(root.ActualHeight), 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(root);
        var background = new DrawingVisual();
        using (var dc = background.RenderOpen())
        { dc.DrawRectangle(new SolidColorBrush(Color.FromRgb(230, 233, 240)), null, new Rect(root.RenderSize)); dc.DrawImage(bitmap, new Rect(root.RenderSize)); }
        var final = new RenderTargetBitmap(bitmap.PixelWidth, bitmap.PixelHeight, 96, 96, PixelFormats.Pbgra32);
        final.Render(background);
        var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(final));
        using var output = File.Create(path); encoder.Save(output);
    }
}
