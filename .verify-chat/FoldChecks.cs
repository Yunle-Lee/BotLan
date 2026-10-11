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
using Microsoft.Web.WebView2.Wpf;

/// <summary>Chat resize limits and the × → Long Island dismiss, on the real window and page.</summary>
internal static class FoldChecks
{
    private const BindingFlags Private = BindingFlags.Instance | BindingFlags.NonPublic;
    private static int _failures;
    public static int Run(App app, string output)
    {
        Environment.SetEnvironmentVariable("ISLANDUI_TEST_DATA_DIR", Path.Combine(output, "data"));
        Environment.SetEnvironmentVariable("TASK_WORKER_ENABLED", "false");
        var frame = new DispatcherFrame();
        double savedWidth = IslandSettings.Instance.ChatWidth, savedHeight = IslandSettings.Instance.ChatHeight;
        app.Dispatcher.BeginInvoke(async () =>
        {
            ChatWindow? window = null;
            LongIslandWindow? source = null;
            try
            {
                AgentStore.Instance.Bots.Clear();
                for (int i = 0; i < 6; i++) AgentStore.Instance.Bots.Add(new AgentBot { Config = new AgentConfig {
                    Id = "fold-fixture-" + i, Name = "关闭测试 " + (i + 1), ColorHex = i == 0 ? "#F45E93" : "#4C9DFF",
                    ApiKey = "unused-fold-key", BaseUrl = "http://127.0.0.1:1/v1" } });
                IslandSettings.Instance.SetChatSize(716, 540);
                source = new LongIslandWindow { Left = 800, Top = 80 };
                source.Show(); source.UpdateLayout(); source.Hide();
                window = (ChatWindow)Activator.CreateInstance(typeof(ChatWindow), Private, null,
                    [AgentStore.Instance.Bots[0], source], null)!;
                window.Left = 60; window.Top = 50; window.Show();
                await Wait(async () => Field(window, "_studioView") is WebView2CompositionControl v && v.CoreWebView2 != null
                    && await Bool(v, "!!document.querySelector('#island-composer') && !!document.querySelector('#island-right-dock')"), 100000);
                var view = (WebView2CompositionControl)Field(window, "_studioView")!;
                await Task.Delay(400);
                Render(window, Path.Combine(output, "01-expanded.png"));
                var originalBot = Field(window, "_bot");

                // ---- resize: real card size, free aspect, nothing scaled ----
                var card = (Border)window.FindName("ChatCard");
                var orbit = (Canvas)window.FindName("OrbitLayer");
                var shellElement = (FrameworkElement)window.FindName("StudioShell");
                var minSize = (Size)typeof(ChatWindow).GetProperty("MinCardSize", BindingFlags.Static | BindingFlags.NonPublic)!.GetValue(null)!;
                var maxSize = (Size)typeof(ChatWindow).GetProperty("MaxCardSize", BindingFlags.Static | BindingFlags.NonPublic)!.GetValue(null)!;
                Console.WriteLine($"Card size range: {minSize} - {maxSize}");
                Rect CardOnScreen()
                {
                    var r = card.TransformToVisual(window).TransformBounds(new Rect(card.RenderSize));
                    r.Offset(window.Left, window.Top);
                    return r;
                }
                Rect RailOnScreen()
                {
                    var r = orbit.TransformToVisual(window).TransformBounds(orbit.Clip.Bounds);
                    r.Offset(window.Left, window.Top);
                    return r;
                }
                var edgesType = typeof(ChatWindow).GetNestedType("ResizeEdges", BindingFlags.NonPublic)!;
                async Task Drag(string edges, Vector by)
                {
                    var c = CardOnScreen();
                    var e = Enum.Parse(edgesType, edges);
                    var from = new Point(edges.Contains("Left") ? c.Left : c.Right, edges.Contains("Top") ? c.Top : c.Bottom);
                    typeof(ChatWindow).GetMethod("StartChatResize", Private)!.Invoke(window, [e, from]);
                    // Several moves, like a real drag.
                    for (int i = 1; i <= 4; i++)
                        typeof(ChatWindow).GetMethod("ResizeChatTo", Private)!.Invoke(window, [from + by * (i / 4.0)]);
                    typeof(ChatWindow).GetMethod("EndChatResize", Private)!.Invoke(window, null);
                    await Task.Delay(350); window.UpdateLayout();
                }
                bool Same(double a, double b, double tolerance = 1.01) => Math.Abs(a - b) <= tolerance;
                var styleBefore = (card.BorderThickness, card.CornerRadius, ((RowDefinition)window.FindName("StudioHeaderRow")).ActualHeight,
                    ((Button)window.FindName("StudioCloseButton")).ActualWidth);
                var before = CardOnScreen();
                var railBefore = RailOnScreen();
                var railOffsetBefore = new Vector(railBefore.Right - before.Right, railBefore.Top - before.Top);
                Check(Same(before.Width, 716) && Same(before.Height, 540), $"opens at the default card size ({before.Width}x{before.Height})");

                void Park() { window.Left = SystemParameters.WorkArea.Left + 10; window.Top = SystemParameters.WorkArea.Top + 10; window.UpdateLayout(); }
                Park(); before = CardOnScreen();
                // Right edge: only the width changes; left/top/bottom stay.
                await Drag("Right", new Vector(180, 40));
                var wider = CardOnScreen();
                Check(Same(wider.Width, before.Width + 180) && Same(wider.Height, before.Height), $"right edge changes only the width ({wider.Width}x{wider.Height})");
                Check(Same(wider.Left, before.Left) && Same(wider.Top, before.Top), "right edge keeps the left/top fixed");
                // Bottom edge: only the height.
                Park(); wider = CardOnScreen();
                await Drag("Bottom", new Vector(60, 120));
                var taller = CardOnScreen();
                Check(Same(taller.Height, wider.Height + 120) && Same(taller.Width, wider.Width), $"bottom edge changes only the height ({taller.Width}x{taller.Height})");
                var shell = new Rect(Canvas.GetLeft(shellElement), Canvas.GetTop(shellElement), shellElement.Width, shellElement.Height);
                var cardInWindow = new Rect(taller.Left - window.Left, taller.Top - window.Top, taller.Width, taller.Height);
                Check(Same(shell.Width, cardInWindow.Width - 28, 1.5) && Same(shell.Height, cardInWindow.Height - 28, 1.5)
                    && Same(shell.Left, cardInWindow.Left + 14, 1.5), "page gets the card's real size minus the fixed inset");
                Check(UnscaledBrowser(view) && !HasViewbox(window), "page is not scaled: no Viewbox or transform ancestor");
                var railLarge = RailOnScreen();
                Check(Same(railLarge.Width, railBefore.Width, .01) && Same(railLarge.Height, railBefore.Height, .01),
                    $"rail viewport keeps its size ({railBefore.Width:0.0}x{railBefore.Height:0.0} -> {railLarge.Width:0.0}x{railLarge.Height:0.0})");
                Check(Same(railLarge.Right - taller.Right, railOffsetBefore.X, .01) && Same(railLarge.Top - taller.Top, railOffsetBefore.Y, .01),
                    "rail stays attached at the card's upper-right corner");
                Check((card.BorderThickness, card.CornerRadius, ((RowDefinition)window.FindName("StudioHeaderRow")).ActualHeight,
                    ((Button)window.FindName("StudioCloseButton")).ActualWidth) == styleBefore, "border, corner radius, header bar and close button keep their size");
                await Task.Delay(400);
                Check(await Bool(view, "innerWidth >= " + (int)(shell.Width - 1)), "page reflows to the new width");
                Render(window, Path.Combine(output, "resize-large.png"));

                // Top-left corner to the minimum: the lower-right corner stays.
                var preMin = CardOnScreen();
                await Drag("Left, Top", new Vector(4000, 4000));
                var smallest = CardOnScreen();
                Check(Same(smallest.Width, minSize.Width) && Same(smallest.Height, minSize.Height), $"shrinking stops at the minimum ({smallest.Width}x{smallest.Height})");
                Check(Same(smallest.Right, preMin.Right) && Same(smallest.Bottom, preMin.Bottom), "dragging the upper-left corner keeps the lower-right corner fixed");
                var railSmall = RailOnScreen();
                Check(Same(railSmall.Width, railBefore.Width, .01) && Same(railSmall.Height, railBefore.Height, .01)
                    && Same(railSmall.Right - smallest.Right, railOffsetBefore.X, .01) && Same(railSmall.Top - smallest.Top, railOffsetBefore.Y, .01),
                    "rail is identical and attached at the minimum size");
                await Task.Delay(600);
                Check(await Bool(view, "(() => { const l=document.querySelector('#island-left-dock').getBoundingClientRect(), r=document.querySelector('#island-right-dock').getBoundingClientRect(), c=document.querySelector('#island-composer').getBoundingClientRect(); return r.bottom <= innerHeight + 1 && c.bottom <= innerHeight + 1 && l.right < r.left && l.right <= c.left + 1 && c.right <= r.left + 1 && l.bottom <= innerHeight + 1; })()"),
                    "page lays out at the minimum: left dock, right dock and composer don't overlap");
                Console.WriteLine("MIN PAGE " + await view.CoreWebView2.ExecuteScriptAsync("JSON.stringify(['#island-left-dock','#island-right-dock','#island-composer'].map(s=>{const r=document.querySelector(s).getBoundingClientRect();return [s,Math.round(r.left),Math.round(r.top),Math.round(r.right),Math.round(r.bottom)]}).concat([[innerWidth,innerHeight]]))"));
                Render(window, Path.Combine(output, "resize-small.png"));
                using (var pageShot = File.Create(Path.Combine(output, "resize-small-page.png")))
                    await view.CoreWebView2.CapturePreviewAsync(Microsoft.Web.WebView2.Core.CoreWebView2CapturePreviewImageFormat.Png, pageShot);
                Check(Same(IslandSettings.Instance.ChatWidth, smallest.Width) && Same(IslandSettings.Instance.ChatHeight, smallest.Height),
                    "chosen card size is remembered");

                // Bottom-right corner to beyond the screen: stops at the work area.
                Park(); smallest = CardOnScreen();
                await Drag("Right, Bottom", new Vector(6000, 6000));
                var largest = CardOnScreen();
                var area = SystemParameters.WorkArea;
                Check(window.Left + window.Width <= area.Right + 1 && window.Top + window.Height <= area.Bottom + 1
                    && largest.Width <= maxSize.Width + 1 && largest.Height <= maxSize.Height + 1,
                    $"growing stops inside the work area ({largest.Width}x{largest.Height})");
                Check(Same(largest.Left, smallest.Left) && Same(largest.Top, smallest.Top), "dragging the lower-right corner keeps the upper-left fixed");
                Render(window, Path.Combine(output, "resize-max.png"));

                // A new window opens at the remembered size.
                Park(); largest = CardOnScreen();
                await Drag("Right, Bottom", new Vector(before.Width + 40 - largest.Width, before.Height + 30 - largest.Height));
                var remembered = CardOnScreen();
                var probe = (ChatWindow)Activator.CreateInstance(typeof(ChatWindow), Private, null, [AgentStore.Instance.Bots[0], null], null)!;
                var probeSize = (Size)Field(probe, "_cardSize")!;
                Check(Same(probeSize.Width, remembered.Width) && Same(probeSize.Height, remembered.Height), $"next open uses the stored size ({probeSize})");
                probe.Close();
                await Drag("Right, Bottom", new Vector(before.Width - remembered.Width, before.Height - remembered.Height));
                Check(Same(CardOnScreen().Width, before.Width) && Same(CardOnScreen().Height, before.Height), "dragging back restores the original size");

                Park();
                // ---- close: rail slides away, card becomes Long Island, nothing in between ----
                ((Button)window.FindName("StudioCloseButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                await Wait(() => Task.FromResult(Phase(window) == "Closing" && Field(window, "_returnSurface") != null), 3000);
                var outline = (Border)Field(window, "_returnSurface")!;
                var from = (Rect)Field(window, "_returnFrom")!;
                var to = (Rect)Field(window, "_returnTo")!;
                Check(Math.Abs(to.Right - from.Right) < 1 && Math.Abs(to.Top - from.Top) < 1, "capsule forms at the card's upper-right corner");
                bool monotonic = true;
                double lastWidth = from.Width, lastRail = 1;
                for (int i = 0; i <= 100; i++)
                {
                    typeof(ChatWindow).GetMethod("RenderDismiss", Private)!.Invoke(window, [i / 100d]);
                    double x = Canvas.GetLeft(outline);
                    monotonic &= outline.Width <= lastWidth + .001 && outline.Width >= to.Width - .001
                        && x >= Math.Min(from.Left, to.Left) - .001 && x <= Math.Max(from.Left, to.Left) + .001
                        && orbit.Opacity <= lastRail + .001;
                    lastWidth = outline.Width; lastRail = orbit.Opacity;
                }
                Check(monotonic, "card shrinks and rail fades monotonically, no overshoot");
                Check(orbit.Opacity == 0 && outline.Child == null, "rail is gone and only a vector outline morphs (no screenshots)");
                var target = (Border)source.FindName("Island");
                Check(Math.Abs(outline.Width - target.ActualWidth) < .01 && outline.CornerRadius == target.CornerRadius,
                    "outline ends exactly at the Long Island capsule");
                Check(ReferenceEquals(originalBot, Field(window, "_bot")), "departing rail never auto-selects another Bot");
                foreach (var p in new[] { .1, .3, .5, .7, .9 })
                {
                    typeof(ChatWindow).GetMethod("RenderDismiss", Private)!.Invoke(window, [p]);
                    window.UpdateLayout();
                    Render(window, Path.Combine(output, $"dismiss-{p:0.00}.png"));
                }
                double winLeft = window.Left, winTop = window.Top;
                await Wait(() => Task.FromResult(Phase(window) == "Closed"), 3000);
                var capsule = target.TransformToVisual(source).TransformBounds(new Rect(target.RenderSize));
                Check(source.IsVisible && source.Opacity == 1, "Long Island is shown directly after close");
                Check(Math.Abs(source.Left + capsule.Right - (winLeft + to.Right)) < 1.5
                    && Math.Abs(source.Top + capsule.Top - (winTop + to.Top)) < 1.5,
                    "Long Island appears where the outline ended (no jump)");
                Check(Field(window, "_studioView") == null && ((Canvas)window.FindName("FoldLayer")).Children.Count == 0,
                    "close disposes the browser and the morph layer");
                Render(source, Path.Combine(output, "05-long-island.png"));

                window = null;
                source.Hide();
                window = (ChatWindow)Activator.CreateInstance(typeof(ChatWindow), Private, null,
                    [AgentStore.Instance.Bots[0], source], null)!;
                window.Show();
                await Wait(async () => Field(window, "_studioView") is WebView2CompositionControl v && v.CoreWebView2 != null
                    && await Bool(v, "!!document.querySelector('#island-left-dock')"), 30000);
                var interrupted = Invoke(window, "DismissToLongIslandAsync");
                window.Close();
                await interrupted.WaitAsync(TimeSpan.FromSeconds(5));
                Check(Phase(window) == "Closed" && Field(window, "_studioView") == null && source.Opacity == 1,
                    "closing the window mid-animation cancels it and leaves Long Island visible");
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
                IslandSettings.Instance.SetChatSize(savedWidth, savedHeight);
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
    private static bool HasViewbox(DependencyObject node)
    {
        if (node is Viewbox) return true;
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(node); i++)
            if (HasViewbox(VisualTreeHelper.GetChild(node, i))) return true;
        return false;
    }
    private static bool UnscaledBrowser(DependencyObject view)
    {
        for (var node = VisualTreeHelper.GetParent(view); node != null; node = VisualTreeHelper.GetParent(node))
        {
            if (node is Viewbox) return false;
            if (node is UIElement { RenderTransform: { } t } && !t.Value.IsIdentity) return false;
        }
        return true;
    }
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
