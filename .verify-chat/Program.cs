using System.Collections;
using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using IslandUI;
using IslandUI.Services;
using ShapePath = System.Windows.Shapes.Path;

internal static class Program
{
    private const BindingFlags Private = BindingFlags.NonPublic | BindingFlags.Instance;
    private static readonly DependencyProperty Offset = (DependencyProperty)typeof(ChatWindow)
        .GetField("CarouselOffsetProperty", BindingFlags.NonPublic | BindingFlags.Static)!.GetValue(null)!;
    private static int _failures;

    [STAThread]
    private static int Main(string[] args)
    {
        var app = new App();
        app.InitializeComponent(); // Resources only. Never show desktop windows or run startup.
        app.ShutdownMode = ShutdownMode.OnExplicitShutdown;
        string output = Path.GetFullPath(args.FirstOrDefault() ?? ".verify-chat/carousel-renders");
        Directory.CreateDirectory(output);
        if (args.Contains("--startup")) return StartupChecks.Run(app, output, args.Contains("--idle"));
        if (args.Contains("--fold")) return FoldChecks.Run(app, output);
        if (args.Contains("--studio")) return StudioSmoke.Run(app, output);
        string[] colors = ["#F45E93", "#3ECF8E", "#4C9DFF", "#FFD166", "#A78BFA", "#46C5C3"];
        var configured = AgentStore.Instance.Bots.ToList();
        foreach (int count in new[] { 1, 2, 3, 6, 24, 40 })
        {
            // Only this fresh process's in-memory collection changes. Never save test Bots.
            AgentStore.Instance.Bots.Clear();
            for (int i = 0; i < count; i++)
                AgentStore.Instance.Bots.Add(new AgentBot
                {
                    Config = new AgentConfig { Name = $"Bot {i + 1}",
                        ColorHex = colors[i % colors.Length], Id = $"preview-{i}" },
                });
            var selected = AgentStore.Instance.Bots[0];
            var window = Create(selected);
            var root = Arrange(window);
            var layer = (Canvas)window.FindName("OrbitLayer");
            var items = (Canvas)window.FindName("OrbitItems");
            object layout = typeof(ChatWindow).GetField("_carouselLayout", Private)!.GetValue(window)!;
            double pitch = Read<double>(layout, "Pitch");
            double cycle = Read<double>(layout, "CycleLength");
            var seen = new HashSet<AgentBot>();
            int maximumControls = 0;
            bool geometryOk = true, unique = true, appearanceOk = true;
            bool labelsOk = true;
            double focus = Read<double>(layout, "FocusDistance");
            double fadeDistance = Read<double>(layout, "TabLength") + Read<double>(layout, "BandWidth") + Read<double>(layout, "Gap");
            var card = (Border)window.FindName("ChatCard");
            var cardGeometry = new RectangleGeometry(new Rect(Canvas.GetLeft(card), Canvas.GetTop(card),
                card.Width, card.Height), card.CornerRadius.TopLeft, card.CornerRadius.TopLeft);
            Rect fixedViewport = layer.Clip.Bounds;
            Check(fixedViewport.Left > 380 && fixedViewport.Top > 40 && fixedViewport.Bottom < 560,
                $"{count} Bots: viewport stays at upper-right");
            for (int sample = 0; sample < count * 4; sample++)
            {
                window.SetValue(Offset, -pitch * sample / 4);
                root.UpdateLayout();
                var placements = ((IEnumerable)layout.GetType().GetMethod("VisibleSlots")!
                    .Invoke(layout, [-pitch * sample / 4])!).Cast<object>().ToArray();
                var ids = placements.Select(p => (int)p.GetType().GetField("Item1")!.GetValue(p)!).ToArray();
                var positions = placements.ToDictionary(
                    p => (int)p.GetType().GetField("Item1")!.GetValue(p)!,
                    p => Read<double>(p.GetType().GetField("Item2")!.GetValue(p)!, "AvatarAt"));
                unique &= ids.Distinct().Count() == ids.Length;
                maximumControls = Math.Max(maximumControls, items.Children.Count);
                var visible = items.Children.OfType<Canvas>().ToArray();
                var shapes = visible.Select(Surface).ToArray();
                for (int i = 0; i < shapes.Length; i++)
                {
                    var shape = shapes[i];
                    var name = visible[i].Children.OfType<ShapePath>().Single(p => p.Name == "BotNameLabel");
                    labelsOk &= !name.Data.Bounds.IsEmpty && shape.Data.FillContains(name.Data.Bounds.TopLeft)
                        && Geometry.Combine(name.Data, shape.Data, GeometryCombineMode.Exclude, null).GetArea() < .1;
                    var avatar = visible[i].Children.OfType<Border>().Single();
                    var avatarBounds = new Rect(Canvas.GetLeft(avatar), Canvas.GetTop(avatar), avatar.Width, avatar.Height);
                    labelsOk &= Geometry.Combine(name.Data, new RectangleGeometry(avatarBounds), GeometryCombineMode.Intersect, null).GetArea() < .1;
                    var clipped = Geometry.Combine(shape.Data, layer.Clip, GeometryCombineMode.Intersect, null);
                    geometryOk &= Geometry.Combine(clipped, cardGeometry, GeometryCombineMode.Intersect, null)
                        .GetArea() < .1;
                    geometryOk &= fixedViewport.Contains(clipped.Bounds) || clipped.Bounds.IsEmpty;
                    for (int j = 0; j < i; j++)
                        geometryOk &= Geometry.Combine(shape.Data, shapes[j].Data, GeometryCombineMode.Intersect, null)
                            .GetArea() < .1;
                    if (clipped.GetArea() > 100) seen.Add((AgentBot)visible[i].DataContext);
                    bool isSelected = ReferenceEquals(visible[i].DataContext, selected);
                    appearanceOk &= Read<bool>(visible[i], "IsSelected") == isSelected;
                    int botIndex = AgentStore.Instance.Bots.IndexOf((AgentBot)visible[i].DataContext);
                    double distance = Math.Abs(positions[botIndex] - focus);
                    double t = Math.Clamp(distance / fadeDistance, 0, 1);
                    double expectedOpacity = .38 + .62 * (1 - t * t * (3 - 2 * t));
                    appearanceOk &= Math.Abs(visible[i].Opacity - expectedOpacity) < .001;
                    appearanceOk &= ColorOf(shape.Stroke) == ColorOf(ComicTheme.Current.Muted);
                    if (distance < .001) appearanceOk &= ColorOf(shape.Fill) == ColorOf(ComicTheme.Current.Card);
                    if (distance >= fadeDistance) appearanceOk &= ColorOf(shape.Fill) == ColorOf(ComicTheme.Current.CardSoft);
                }
            }
            Check(unique, $"{count} Bots: no duplicated Bot at opposite ends");
            Check(geometryOk, $"{count} Bots: constant lane, no overlapping tabs or Chat");
            Check(labelsOk, $"{count} Bots: names stay in their own lane without covering avatars");
            Check(appearanceOk, $"{count} Bots: neutral color, bright corner and dim sides depend only on position");
            Check(seen.Count == count, $"{count} Bots: every Bot reachable through one loop");
            Check(maximumControls <= 4, $"{count} Bots: bounded controls (peak {maximumControls})");
            window.SetValue(Offset, 0d);
            root.UpdateLayout();
            Render(root, Path.Combine(output, $"bots-{count:00}.png"));

            if (count == 6)
            {
                // Real routed wheel handler + WPF animation clock, not a direct offset assignment.
                var wheel = new MouseWheelEventArgs(Mouse.PrimaryDevice, Environment.TickCount, -120)
                    { RoutedEvent = Mouse.PreviewMouseWheelEvent };
                Surface(items.Children.OfType<Canvas>().First()).RaiseEvent(wheel);
                Pump(100);
                double halfway = (double)window.GetValue(Offset);
                Check(wheel.Handled && halfway < 0 && halfway > -pitch, "wheel animates through intermediate positions");
                Check(((TextBlock)window.FindName("TitleText")).Text == selected.Config.Name,
                    "Chat does not flicker between Bots during the animation");
                Render(root, Path.Combine(output, "wheel-midpoint.png"));
                Pump(260);
                Check(Math.Abs((double)window.GetValue(Offset) - (cycle - pitch)) < .01,
                    "wheel completes one step and normalizes its offset");
                var cornerBot = AgentStore.Instance.Bots[1];
                Check(((TextBlock)window.FindName("TitleText")).Text == cornerBot.Config.Name,
                    "wheel selects the Bot arriving at the corner without a click");
                Check(items.Children.OfType<Canvas>().All(t => Read<bool>(t, "IsSelected") == ReferenceEquals(t.DataContext, cornerBot)),
                    "automatic Chat selection still follows the corner Bot");
                Check(items.Children.OfType<Canvas>().Where(t => ReferenceEquals(t.DataContext, cornerBot))
                    .All(t => Math.Abs(t.Opacity - 1) < .001 && ColorOf(Surface(t).Fill) == ColorOf(ComicTheme.Current.Card)),
                    "corner Bot is naturally opaque without the blue selected fill");
                root.UpdateLayout();
                Render(root, Path.Combine(output, "wheel-after.png"));
                var before = items.Children.OfType<Canvas>().ToDictionary(t => (AgentBot)t.DataContext,
                    t => Surface(t).Data.ToString());
                var appearanceBefore = items.Children.OfType<Canvas>().ToDictionary(t => (AgentBot)t.DataContext,
                    t => (t.Opacity, ColorOf(Surface(t).Fill), ColorOf(Surface(t).Stroke)));
                var clickedTab = items.Children.OfType<Canvas>().First(t => !ReferenceEquals(t.DataContext, cornerBot));
                var clicked = (AgentBot)clickedTab.DataContext;
                Surface(clickedTab).RaiseEvent(new MouseButtonEventArgs(Mouse.PrimaryDevice,
                    Environment.TickCount, MouseButton.Left) { RoutedEvent = Mouse.MouseUpEvent });
                root.UpdateLayout();
                Check(((TextBlock)window.FindName("TitleText")).Text == clicked.Config.Name,
                    "clicking a tab changes Chat identity");
                Check(items.Children.OfType<Canvas>().All(t => before[(AgentBot)t.DataContext] == Surface(t).Data.ToString()),
                    "click selection preserves order and all tab positions");
                Check(items.Children.OfType<Canvas>().All(t => Read<bool>(t, "IsSelected") == ReferenceEquals(t.DataContext, clicked)),
                    "optional click still changes the Chat selection");
                Check(items.Children.OfType<Canvas>().All(t => appearanceBefore[(AgentBot)t.DataContext]
                    == (t.Opacity, ColorOf(Surface(t).Fill), ColorOf(Surface(t).Stroke))),
                    "selection does not override corner brightness or add a highlight");
                Render(root, Path.Combine(output, "clicked-no-highlight.png"));
                layer.RaiseEvent(new MouseWheelEventArgs(Mouse.PrimaryDevice, Environment.TickCount, 120)
                    { RoutedEvent = Mouse.PreviewMouseWheelEvent });
                Pump(340);
                Check(Math.Abs((double)window.GetValue(Offset)) < .01, "reverse wheel returns to the original placement");
                Check(((TextBlock)window.FindName("TitleText")).Text == selected.Config.Name,
                    "reverse wheel automatically reselects the original Bot");
                // Partial detents and rapidly interrupted animations remain cumulative.
                layer.RaiseEvent(new MouseWheelEventArgs(Mouse.PrimaryDevice, Environment.TickCount, -30)
                    { RoutedEvent = Mouse.PreviewMouseWheelEvent });
                Pump(40);
                layer.RaiseEvent(new MouseWheelEventArgs(Mouse.PrimaryDevice, Environment.TickCount, -90)
                    { RoutedEvent = Mouse.PreviewMouseWheelEvent });
                Pump(340);
                Check(Math.Abs((double)window.GetValue(Offset) - (cycle - pitch)) < .01,
                    "rapid/partial wheel inputs accumulate without snapping");
                Check(((TextBlock)window.FindName("TitleText")).Text == cornerBot.Config.Name,
                    "interrupted animations select only the final corner Bot");

                layer.RaiseEvent(new MouseWheelEventArgs(Mouse.PrimaryDevice, Environment.TickCount, -120 * 8)
                    { RoutedEvent = Mouse.PreviewMouseWheelEvent });
                Pump(340);
                Check(((TextBlock)window.FindName("TitleText")).Text == AgentStore.Instance.Bots[3].Config.Name,
                    "automatic selection wraps across the end of the Bot list");
                layer.RaiseEvent(new MouseWheelEventArgs(Mouse.PrimaryDevice, Environment.TickCount, -30)
                    { RoutedEvent = Mouse.PreviewMouseWheelEvent });
                Pump(340);
                Check(((TextBlock)window.FindName("TitleText")).Text == AgentStore.Instance.Bots[3].Config.Name,
                    "small wheel detent keeps the nearest corner Bot selected");
                layer.RaiseEvent(new MouseWheelEventArgs(Mouse.PrimaryDevice, Environment.TickCount, -60)
                    { RoutedEvent = Mouse.PreviewMouseWheelEvent });
                Pump(340);
                Check(((TextBlock)window.FindName("TitleText")).Text == AgentStore.Instance.Bots[4].Config.Name,
                    "partial wheel motion selects the next Bot once it is nearest the corner");
            }

            // Closing during a movement must invalidate its pending selection callback.
            layer.RaiseEvent(new MouseWheelEventArgs(Mouse.PrimaryDevice, Environment.TickCount, -120)
                { RoutedEvent = Mouse.PreviewMouseWheelEvent });
            window.Close();
            Pump(320);
            Check(items.Children.Count == 0 && !window.HasAnimatedProperties, "closing Chat releases tabs and carousel animation");
        }

        // Actual configured Bots, when accessible, are loaded read-only and rendered as-is.
        AgentStore.Instance.Bots.Clear();
        foreach (var bot in configured) AgentStore.Instance.Bots.Add(bot);
        if (configured.Count > 0)
        {
            var window = Create(configured[0]);
            var root = Arrange(window);
            var layout = typeof(ChatWindow).GetField("_carouselLayout", Private)!.GetValue(window)!;
            double pitch = Read<double>(layout, "Pitch");
            Render(root, Path.Combine(output, "configured-bots.png"));
            var frames = Path.Combine(output, "frames");
            Directory.CreateDirectory(frames);
            var orbit = (Canvas)window.FindName("OrbitLayer");
            for (int frame = 0; frame < 120; frame++)
            {
                // Capture actual wheel events and the production animation/selection path.
                if (frame % 30 == 8)
                    orbit.RaiseEvent(new MouseWheelEventArgs(Mouse.PrimaryDevice, Environment.TickCount, -120)
                        { RoutedEvent = Mouse.PreviewMouseWheelEvent });
                Pump(40);
                root.UpdateLayout();
                Render(root, Path.Combine(frames, $"frame-{frame:000}.png"));
                if (frame == 29) Render(root, Path.Combine(output, "configured-after-wheel.png"));
            }
            window.Close();
        }
        Console.WriteLine($"Configured Bots rendered read-only: {configured.Count}");
        Console.WriteLine($"Carousel QA failures: {_failures}");
        return _failures == 0 ? 0 : 1;
    }

    private static ChatWindow Create(AgentBot bot) => (ChatWindow)Activator.CreateInstance(typeof(ChatWindow),
        Private, null, [bot, null], null)!;
    private static T Read<T>(object obj, string name) => (T)obj.GetType().GetProperty(name)!.GetValue(obj)!;
    private static Color ColorOf(Brush brush) => ((SolidColorBrush)brush).Color;
    private static ShapePath Surface(Canvas tab) => tab.Children.OfType<ShapePath>().First();
    private static FrameworkElement Arrange(ChatWindow window)
    {
        var root = (FrameworkElement)window.Content;
        root.Measure(new Size(1036, 800));
        root.Arrange(new Rect(0, 0, 1036, 800));
        root.UpdateLayout();
        return root;
    }
    private static void Check(bool ok, string label)
    {
        if (!ok) _failures++;
        Console.WriteLine($"{(ok ? "PASS" : "FAIL")} {label}");
    }
    private static void Pump(int milliseconds)
    {
        var frame = new DispatcherFrame();
        var timer = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromMilliseconds(milliseconds) };
        timer.Tick += (_, _) => { timer.Stop(); frame.Continue = false; };
        timer.Start();
        Dispatcher.PushFrame(frame);
    }
    private static void Render(FrameworkElement root, string path)
    {
        var raw = new RenderTargetBitmap(1036, 800, 96, 96, PixelFormats.Pbgra32);
        raw.Render(root);
        var backdrop = new DrawingVisual();
        using (var dc = backdrop.RenderOpen())
        {
            // Only the preview has a gray backdrop. Production gaps stay transparent.
            dc.DrawRectangle(new SolidColorBrush(Color.FromRgb(232, 234, 239)), null, new Rect(0, 0, 1036, 800));
            dc.DrawImage(raw, new Rect(0, 0, 1036, 800));
        }
        var result = new RenderTargetBitmap(1036, 800, 96, 96, PixelFormats.Pbgra32);
        result.Render(backdrop);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(result));
        using var stream = File.Create(path);
        encoder.Save(stream);
    }
}
