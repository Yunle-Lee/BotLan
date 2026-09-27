using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using IslandUI.Controls;
using Microsoft.Web.WebView2.Core;

namespace IslandUI;

public partial class ChatWindow
{
    private enum FoldPhase { Expanded, Preparing, Folding, Folded, Unfolding, Returning, Closed }
    private FoldPhase _foldPhase;
    private Rect _expandedWindow, _foldCard;
    private const double FoldDockWidth = 98;
    private ChatBookFold? _bookFold;
    private BitmapSource? _foldSnapshot;
    private (int BotIndex, OrbitTabSlot Slot)[] _departingTabs = [];
    private DispatcherTimer? _foldHoverTimer;
    private long _foldLeaveAt;
    private TaskCompletionSource? _foldMotion;
    private readonly CancellationTokenSource _foldLifetime = new();
    private LongIslandWindow? _returnIsland;
    private Rect _returnFrom, _returnTo;
    private Border? _returnSurface;
    private CornerRadius _returnCorner;
#if DEBUG
    // Tests exercise hover transitions without moving or controlling the user's mouse.
    internal Func<bool>? FoldHoverOverride { get; set; }
#endif
    private static readonly DependencyProperty FoldProgressProperty = DependencyProperty.Register(
        "FoldProgress", typeof(double), typeof(ChatWindow), new PropertyMetadata(0d,
            (d, e) => ((ChatWindow)d).RenderFold((double)e.NewValue)));

    private async Task FoldChatAsync()
    {
        if (_foldPhase != FoldPhase.Expanded || _studioClosed) return;
        _foldPhase = FoldPhase.Preparing;
        StudioShell.IsHitTestVisible = false;
        OrbitLayer.IsHitTestVisible = false;
        try
        {
            _expandedWindow = new Rect(Left, Top, ActualWidth, ActualHeight);
            _foldCard = ChatCard.TransformToVisual(StudioOverlay).TransformBounds(new Rect(ChatCard.RenderSize));
            _foldSnapshot = await CaptureCardAsync();
            if (_studioClosed) return;
            double offset = CarouselOffset;
            ++_scrollGeneration;
            BeginAnimation(CarouselOffsetProperty, null);
            CarouselOffset = _targetOffset = offset;
            _departingTabs = _carouselLayout?.VisibleSlots(offset).ToArray() ?? [];
            _bookFold = new ChatBookFold(_foldSnapshot, _foldCard.Width, _foldCard.Height, FoldDockWidth);
            Canvas.SetLeft(_bookFold, _foldCard.Left); Canvas.SetTop(_bookFold, _foldCard.Top);
            FoldLayer.Children.Add(_bookFold);
            ChatCard.Visibility = ChatShadow.Visibility = StudioShell.Visibility = Visibility.Hidden;
            _foldPhase = FoldPhase.Folding;
            await AnimateFoldAsync(0, 1, 780);
            if (_studioClosed) return;
            EnterFolded();
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) when (ex is InvalidOperationException or COMException or IOException or TimeoutException)
        {
            // A browser capture failure must leave the original Chat usable.
            if (!_studioClosed) RestoreExpanded();
        }
    }

    private void RenderFold(double progress)
    {
        if (_foldPhase == FoldPhase.Returning) { RenderReturn(progress); return; }
        if (_foldPhase is not (FoldPhase.Folding or FoldPhase.Unfolding)) return;
        _bookFold?.SetProgress(progress);
        if (_carouselLayout == null) return;
        // Only the tabs already on screen leave. Never feed replacement tabs into
        // the belt or run its automatic Bot-selection callback while dismissing.
        double travel = (_carouselLayout.ViewportEnd - _carouselLayout.ViewportStart + _carouselLayout.TabLength)
            * ChatBookFold.Smooth(0, .9, progress);
        foreach (var item in _departingTabs)
            if (item.BotIndex < _orbitBots.Count && _carouselTabs.TryGetValue(_orbitBots[item.BotIndex], out var tab))
                tab.Place(_carouselLayout, item.Slot with { Start = item.Slot.Start + travel, AvatarAt = item.Slot.AvatarAt + travel },
                    ReferenceEquals(_orbitBots[item.BotIndex], _bot));
        OrbitLayer.Opacity = 1 - ChatBookFold.Smooth(.08, .72, progress);
    }

    private Task AnimateFoldAsync(double from, double to, int milliseconds)
    {
        _foldMotion?.TrySetCanceled();
        var completion = _foldMotion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        BeginAnimation(FoldProgressProperty, null);
        SetValue(FoldProgressProperty, from);
        RenderFold(from);
        var animation = new DoubleAnimation(from, to, TimeSpan.FromMilliseconds(milliseconds));
        animation.Completed += (_, _) => completion.TrySetResult();
        BeginAnimation(FoldProgressProperty, animation, HandoffBehavior.SnapshotAndReplace);
        return completion.Task;
    }

    private void EnterFolded()
    {
        _foldPhase = FoldPhase.Folded;
        BeginAnimation(FoldProgressProperty, null);
        DesignViewbox.Visibility = Visibility.Collapsed;
        // Shrink the actual HWND too: the vacated Chat area must not intercept clicks.
        MinWidth = MinHeight = 0;
        WindowStartupLocation = WindowStartupLocation.Manual;
        // Resize before moving: moving the old full-width HWND to the capsule's
        // right-side position can push it off-screen and trigger OS repositioning.
        Width = FoldDockWidth * 2 + 16;
        Height = Math.Max(_foldCard.Height, 440) + 16;
        Left = _expandedWindow.Left + _foldCard.Right - FoldDockWidth * 2 - 8;
        Top = _expandedWindow.Top + _foldCard.Top - 8;
        FoldLayer.Children.Clear();
        Canvas.SetLeft(FoldCapsule, 8); Canvas.SetTop(FoldCapsule, 8);
        FoldCapsule.Width = Width - 16; FoldCapsule.Height = Height - 16;
        FoldCapsule.CornerRadius = new CornerRadius(FoldCapsule.Width / 2);
        FoldCapsule.Visibility = Visibility.Visible;
        Canvas.SetLeft(StudioShell, 22); Canvas.SetTop(StudioShell, 22);
        StudioShell.Width = Width - 44; StudioShell.Height = Height - 44;
        StudioHeaderRow.Height = new GridLength(32);
        StudioShell.Clip = new RectangleGeometry(new Rect(0, 0, StudioShell.Width, StudioShell.Height), 78, 78);
        StudioCaption.Visibility = StudioDragHint.Visibility = Visibility.Collapsed;
        StudioCloseButton.Content = "↗"; StudioCloseButton.ToolTip = "展开 Chat";
        StudioCloseButton.FontSize = 17; StudioCloseButton.Foreground = ComicTheme.Current.Muted;
        StudioCloseButton.HorizontalAlignment = HorizontalAlignment.Center;
        SetPageFolded(true);
        StudioShell.Visibility = Visibility.Visible;
        StudioShell.IsHitTestVisible = true;
        _foldLeaveAt = 0;
        _foldHoverTimer ??= new DispatcherTimer(DispatcherPriority.Background)
            { Interval = TimeSpan.FromMilliseconds(100) };
        _foldHoverTimer.Tick -= FoldHoverTick;
        _foldHoverTimer.Tick += FoldHoverTick;
        _foldHoverTimer.Start();
    }

    private void SetPageFolded(bool folded)
        => _studioView?.CoreWebView2?.PostWebMessageAsJson(JsonSerializer.Serialize(new { type = "fold-layout", folded }));

    private async Task UnfoldChatAsync()
    {
        if (_foldPhase != FoldPhase.Folded || _studioClosed) return;
        _foldHoverTimer?.Stop();
        _foldPhase = FoldPhase.Unfolding;
        StudioShell.IsHitTestVisible = false;
        // Preserve the new right edge if the user dragged the folded capsule.
        _expandedWindow.X = Left + 8 + FoldDockWidth * 2 - _foldCard.Right;
        _expandedWindow.Y = Top + 8 - _foldCard.Top;
        SetPageFolded(false);
        StudioShell.Visibility = FoldCapsule.Visibility = Visibility.Hidden;
        Width = _expandedWindow.Width; Height = _expandedWindow.Height;
        Left = _expandedWindow.Left; Top = _expandedWindow.Top;
        DesignViewbox.Visibility = Visibility.Visible;
        StudioShell.Clip = null;
        UpdateLayout(); LayoutExpandedStudio();
        if (_bookFold != null) FoldLayer.Children.Add(_bookFold);
        try
        {
            await AnimateFoldAsync(1, 0, 620);
            if (!_studioClosed) RestoreExpanded();
        }
        catch (OperationCanceledException) { }
    }

    private void RestoreExpanded()
    {
        _foldPhase = FoldPhase.Expanded;
        BeginAnimation(FoldProgressProperty, null);
        _foldHoverTimer?.Stop();
        FoldLayer.Children.Clear();
        _bookFold = null; _foldSnapshot = null; _departingTabs = [];
        DesignViewbox.Visibility = ChatCard.Visibility = ChatShadow.Visibility = StudioShell.Visibility = Visibility.Visible;
        FoldCapsule.Visibility = Visibility.Collapsed;
        StudioShell.Clip = null;
        StudioCaption.Visibility = StudioDragHint.Visibility = Visibility.Visible;
        StudioCloseButton.Content = "✕"; StudioCloseButton.ToolTip = "折叠 Chat";
        StudioCloseButton.FontSize = 12; StudioCloseButton.Foreground = new SolidColorBrush(Color.FromRgb(207, 56, 56));
        StudioCloseButton.HorizontalAlignment = HorizontalAlignment.Right;
        StudioShell.IsHitTestVisible = OrbitLayer.IsHitTestVisible = true;
        OrbitLayer.Opacity = 1;
        MinWidth = 640; MinHeight = 500;
        UpdateLayout(); SyncStudioBounds(); RefreshOrbitBots();
        SetPageFolded(false);
    }

    private void FoldHoverTick(object? sender, EventArgs args)
    {
        if (_foldPhase != FoldPhase.Folded) return;
        bool hover = false;
#if DEBUG
        if (FoldHoverOverride != null) hover = FoldHoverOverride();
        else
#endif
        if (GetCursorPos(out var position))
        {
            var point = PointFromScreen(new Point(position.X, position.Y));
            var shape = new RectangleGeometry(new Rect(8, 8, Width - 16, Height - 16), (Width - 16) / 2, (Width - 16) / 2);
            hover = shape.FillContains(point);
        }
        if (hover) { _foldLeaveAt = 0; return; }
        if (_foldLeaveAt == 0) _foldLeaveAt = Environment.TickCount64;
        // A short grace period allows crossing the gap between the retained docks.
        if (Environment.TickCount64 - _foldLeaveAt >= 650) _ = ReturnToLongIslandAsync();
    }

    private async Task ReturnToLongIslandAsync()
    {
        if (_foldPhase != FoldPhase.Folded || _studioClosed) return;
        _foldHoverTimer?.Stop();
        _foldPhase = FoldPhase.Returning;
        StudioShell.IsHitTestVisible = false;
        try
        {
            _returnIsland = _sourceWindow is { IsDisposed: false } ? _sourceWindow : (Application.Current as App)?.PrepareLongIslandReturn();
            if (_returnIsland == null) { Close(); return; }
            _returnIsland.Opacity = 0;
            if (!_returnIsland.IsVisible) _returnIsland.Show();
            _returnIsland.UpdateLayout();
            var target = (Border)_returnIsland.FindName("Island");
            var origin = target.TranslatePoint(new Point(), _returnIsland);
            var source = new Rect(Left + 8, Top + 8, Width - 16, Height - 16);
            // Morph in place, not toward the hidden Long Island's stale desktop
            // position. Preserve the visible capsule's centre line and top edge.
            _returnIsland.Left = source.Left + (source.Width - target.ActualWidth) / 2 - origin.X;
            _returnIsland.Top = source.Top - origin.Y;
            var destination = new Rect(_returnIsland.Left + origin.X, _returnIsland.Top + origin.Y,
                target.ActualWidth, target.ActualHeight);
            _returnCorner = target.CornerRadius;
            var bounds = Rect.Union(source, destination);
            bounds.Inflate(12, 12);
            FoldCapsule.Visibility = Visibility.Hidden;
            Width = bounds.Width; Height = bounds.Height; Left = bounds.Left; Top = bounds.Top;
            _returnFrom = new Rect(source.Left - bounds.Left, source.Top - bounds.Top, source.Width, source.Height);
            _returnTo = new Rect(destination.Left - bounds.Left, destination.Top - bounds.Top, destination.Width, destination.Height);
            // Keep the real controls at their original size while they fade away.
            // Only a vector outline morphs: no browser capture, stretched text or spring.
            Canvas.SetLeft(StudioShell, _returnFrom.Left + 14);
            Canvas.SetTop(StudioShell, _returnFrom.Top + 14);
            _returnSurface = new Border { Background = ComicTheme.Current.Card,
                BorderBrush = target.BorderBrush, BorderThickness = target.BorderThickness };
            FoldLayer.Children.Clear(); FoldLayer.Children.Add(_returnSurface);
            Panel.SetZIndex(StudioOverlay, 1);
            await AnimateFoldAsync(0, 1, 420);
            if (_studioClosed) return;
            _returnIsland.Opacity = 1;
            _returnIsland.Activate();
            Close();
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) when (ex is InvalidOperationException or COMException or IOException or TimeoutException)
        {
            if (_returnIsland != null) _returnIsland.Opacity = 1;
            if (!_studioClosed) Close();
        }
    }

    private void RenderReturn(double progress)
    {
        if (_returnSurface == null || _returnIsland == null) return;
        double p = ChatBookFold.Smooth(.14, .9, progress);
        double Mix(double a, double b) => a + (b - a) * p;
        Canvas.SetLeft(_returnSurface, Mix(_returnFrom.Left, _returnTo.Left));
        Canvas.SetTop(_returnSurface, Mix(_returnFrom.Top, _returnTo.Top));
        _returnSurface.Width = Math.Max(8, Mix(_returnFrom.Width, _returnTo.Width));
        _returnSurface.Height = Math.Max(8, Mix(_returnFrom.Height, _returnTo.Height));
        _returnSurface.CornerRadius = new CornerRadius(Mix(_returnFrom.Width / 2, _returnCorner.TopLeft),
            Mix(_returnFrom.Width / 2, _returnCorner.TopRight), Mix(_returnFrom.Width / 2, _returnCorner.BottomRight),
            Mix(_returnFrom.Width / 2, _returnCorner.BottomLeft));
        StudioShell.Opacity = 1 - ChatBookFold.Smooth(0, .14, progress);
        StudioShell.Visibility = progress >= .14 ? Visibility.Hidden : Visibility.Visible;
        _returnSurface.Opacity = 1 - ChatBookFold.Smooth(.9, 1, progress);
        _returnIsland.Opacity = ChatBookFold.Smooth(.9, 1, progress);
    }

    private async Task<BitmapSource> CaptureCardAsync()
    {
        var drawing = new DrawingVisual();
        BitmapSource? page = await CapturePageAsync();
        using (var dc = drawing.RenderOpen())
        {
            dc.DrawRectangle(new VisualBrush(ChatCard) { Stretch = Stretch.Fill }, null, new Rect(0, 0, _foldCard.Width, _foldCard.Height));
            DrawStudio(dc, new Point(_foldCard.Left, _foldCard.Top), page);
        }
        return Rasterize(drawing, _foldCard.Width, _foldCard.Height);
    }

    private async Task<BitmapSource?> CapturePageAsync()
    {
        if (_studioView?.CoreWebView2 == null || StudioLoading.Visibility == Visibility.Visible) return null;
        using var stream = new MemoryStream();
        // WebView may never complete a capture if it is disposed mid-request.
        // Closing the window must release the transition immediately; an unusually
        // slow capture also falls back to the still-usable original Chat.
        await _studioView.CoreWebView2.CapturePreviewAsync(CoreWebView2CapturePreviewImageFormat.Png, stream)
            .WaitAsync(TimeSpan.FromSeconds(3), _foldLifetime.Token);
        stream.Position = 0;
        var image = new BitmapImage(); image.BeginInit(); image.CacheOption = BitmapCacheOption.OnLoad;
        image.StreamSource = stream; image.EndInit(); image.Freeze(); return image;
    }

    private void DrawStudio(DrawingContext dc, Point origin, BitmapSource? page)
    {
        Rect Bounds(FrameworkElement element)
        {
            var pos = element.TranslatePoint(new Point(), StudioOverlay) - (Vector)origin;
            return new Rect(pos, element.RenderSize);
        }
        dc.DrawRectangle(new VisualBrush(StudioShell) { Stretch = Stretch.Fill }, null, Bounds(StudioShell));
        if (page != null)
        {
            var viewport = Bounds(StudioViewport);
            dc.PushClip(new RectangleGeometry(viewport, 28, 28));
            dc.DrawImage(page, viewport); dc.Pop();
            if (StudioBotAvatar.IsVisible)
                dc.DrawRectangle(new VisualBrush(StudioBotAvatar) { Stretch = Stretch.Fill }, null, Bounds(StudioBotAvatar));
        }
    }

    private BitmapSource Rasterize(Visual visual, double width, double height)
    {
        var dpi = VisualTreeHelper.GetDpi(this);
        var image = new RenderTargetBitmap(Math.Max(1, (int)Math.Ceiling(width * dpi.DpiScaleX)),
            Math.Max(1, (int)Math.Ceiling(height * dpi.DpiScaleY)), 96 * dpi.DpiScaleX, 96 * dpi.DpiScaleY, PixelFormats.Pbgra32);
        image.Render(visual); image.Freeze(); return image;
    }

    private void DisposeFold()
    {
        _foldPhase = FoldPhase.Closed;
        _foldLifetime.Cancel();
        _foldHoverTimer?.Stop();
        if (_foldHoverTimer != null) _foldHoverTimer.Tick -= FoldHoverTick;
        _foldHoverTimer = null;
        _foldMotion?.TrySetCanceled(); _foldMotion = null;
        BeginAnimation(FoldProgressProperty, null);
        FoldLayer.Children.Clear();
        _foldSnapshot = null; _bookFold = null; _departingTabs = [];
        _returnSurface = null;
        if (_returnIsland != null) _returnIsland.Opacity = 1;
    }

    [StructLayout(LayoutKind.Sequential)] private struct FoldCursor { public int X, Y; }
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetCursorPos(out FoldCursor point);
}
