using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Effects;
using IslandUI.Controls;

namespace IslandUI;

public partial class ChatWindow
{
    // × closes straight into Long Island: the rail slides away along its track while the card
    // shrinks into the capsule at its upper-right corner. No snapshot, fold or folded state.
    private enum FoldPhase { Expanded, Closing, Closed }
    private FoldPhase _foldPhase;
    private (int BotIndex, OrbitTabSlot Slot)[] _departingTabs = [];
    private TaskCompletionSource? _foldMotion;
    private LongIslandWindow? _returnIsland;
    private Rect _returnFrom, _returnTo;
    private Border? _returnSurface;
    private CornerRadius _returnCorner;
    private double _returnFromCorner, _returnFromBorder, _returnToBorder;
    private const int DismissMilliseconds = 560;

    private static readonly DependencyProperty FoldProgressProperty = DependencyProperty.Register(
        "FoldProgress", typeof(double), typeof(ChatWindow), new PropertyMetadata(0d,
            (d, e) => ((ChatWindow)d).RenderDismiss((double)e.NewValue)));

    private async Task DismissToLongIslandAsync()
    {
        if (_foldPhase != FoldPhase.Expanded || _studioClosed) return;
        _foldPhase = FoldPhase.Closing;
        EndChatResize();
        StudioShell.IsHitTestVisible = OrbitLayer.IsHitTestVisible = DesignCanvas.IsHitTestVisible = false;
        StudioBotAvatar.Visibility = Visibility.Collapsed;
        try
        {
            // Only the tabs already on screen leave; the belt never feeds replacements
            // or runs its automatic Bot selection while dismissing.
            double offset = CarouselOffset;
            ++_scrollGeneration;
            StopOpenAnimation();
            BeginAnimation(CarouselOffsetProperty, null);
            CarouselOffset = _targetOffset = offset;
            _departingTabs = _carouselLayout?.VisibleSlots(offset).ToArray() ?? [];

            _returnIsland = _sourceWindow is { IsDisposed: false } ? _sourceWindow : (Application.Current as App)?.PrepareLongIslandReturn();
            var card = ChatCard.TransformToVisual(FoldLayer).TransformBounds(new Rect(ChatCard.RenderSize));
            _returnFrom = card;
            _returnFromCorner = ChatCard.CornerRadius.TopLeft;
            _returnFromBorder = ChatCard.BorderThickness.Left;
            if (_returnIsland != null)
            {
                _returnIsland.Opacity = 0;
                if (!_returnIsland.IsVisible) _returnIsland.Show();
                _returnIsland.UpdateLayout();
                var target = (Border)_returnIsland.FindName("Island");
                var origin = target.TranslatePoint(new Point(), _returnIsland);
                // The capsule takes the card's upper-right corner, kept inside this window
                // so the whole morph stays visible, then inside the work area.
                double capsuleLeft = card.Right - target.ActualWidth;
                double capsuleTop = Math.Max(0, Math.Min(card.Top, ActualHeight - target.ActualHeight - 8));
                var area = SystemParameters.WorkArea;
                double screenLeft = Math.Clamp(Left + capsuleLeft, area.Left, area.Right - target.ActualWidth);
                double screenTop = Math.Clamp(Top + capsuleTop, area.Top, Math.Max(area.Top, area.Bottom - target.ActualHeight));
                _returnIsland.Left = screenLeft - origin.X;
                _returnIsland.Top = screenTop - origin.Y;
                _returnTo = new Rect(screenLeft - Left, screenTop - Top, target.ActualWidth, target.ActualHeight);
                _returnCorner = target.CornerRadius;
                _returnToBorder = target.BorderThickness.Left;
            }
            else
            {
                // No Long Island to return to: just collapse toward the corner and close.
                _returnTo = new Rect(card.Right - 64, card.Top, 64, Math.Min(card.Height, 320));
                _returnCorner = new CornerRadius(32);
                _returnToBorder = 2.2;
            }
            _returnSurface = new Border
            {
                Background = ComicTheme.Current.Card,
                BorderBrush = ComicTheme.Current.Outline,
                Effect = new DropShadowEffect { BlurRadius = 10, ShadowDepth = 3, Opacity = .16, Color = Colors.Black },
            };
            FoldLayer.Children.Clear();
            FoldLayer.Children.Add(_returnSurface);
            ChatCard.Visibility = ChatShadow.Visibility = Visibility.Hidden;
            await AnimateFoldAsync(0, 1, DismissMilliseconds);
            if (_studioClosed) return;
            if (_returnIsland != null)
            {
                _returnIsland.Opacity = 1;
                _returnIsland.Activate();
            }
            Close();
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) when (ex is InvalidOperationException or COMException)
        {
            if (_returnIsland != null) _returnIsland.Opacity = 1;
            if (!_studioClosed) Close();
        }
    }

    private void RenderDismiss(double progress)
    {
        if (_foldPhase != FoldPhase.Closing) return;
        if (_carouselLayout != null)
        {
            double travel = (_carouselLayout.ViewportEnd - _carouselLayout.ViewportStart + _carouselLayout.TabLength)
                * Smooth(0, .9, progress);
            foreach (var item in _departingTabs)
                if (item.BotIndex < _orbitItems.Count && _carouselTabs.TryGetValue(_orbitItems[item.BotIndex], out var tab))
                    tab.Place(_carouselLayout, item.Slot with { Start = item.Slot.Start + travel, AvatarAt = item.Slot.AvatarAt + travel },
                        ReferenceEquals(_orbitItems[item.BotIndex].Bot, _bot));
            OrbitLayer.Opacity = 1 - Smooth(.08, .72, progress);
        }
        // The live page fades first so no browser pixels are ever stretched.
        StudioShell.Opacity = 1 - Smooth(0, .22, progress);
        StudioShell.Visibility = progress >= .22 ? Visibility.Hidden : Visibility.Visible;
        if (_returnSurface == null) return;
        double p = Smooth(.1, .86, progress);
        double Mix(double a, double b) => a + (b - a) * p;
        Canvas.SetLeft(_returnSurface, Mix(_returnFrom.Left, _returnTo.Left));
        Canvas.SetTop(_returnSurface, Mix(_returnFrom.Top, _returnTo.Top));
        _returnSurface.Width = Math.Max(8, Mix(_returnFrom.Width, _returnTo.Width));
        _returnSurface.Height = Math.Max(8, Mix(_returnFrom.Height, _returnTo.Height));
        _returnSurface.BorderThickness = new Thickness(Mix(_returnFromBorder, _returnToBorder));
        _returnSurface.CornerRadius = new CornerRadius(Mix(_returnFromCorner, _returnCorner.TopLeft),
            Mix(_returnFromCorner, _returnCorner.TopRight), Mix(_returnFromCorner, _returnCorner.BottomRight),
            Mix(_returnFromCorner, _returnCorner.BottomLeft));
        // Hand over to the real capsule once the outline matches it.
        double handoff = Smooth(.86, 1, progress);
        _returnSurface.Opacity = 1 - handoff;
        if (_returnIsland != null) _returnIsland.Opacity = handoff;
    }

    // ---- open: the reverse of the dismiss, short and subtle ----------------------------------
    // The card grows out of its upper-right corner (where the capsule sits) while fading in.
    // Opacity never blocks input, and the transforms are removed once it settles, so the page
    // is never left scaled.
    private const int OpenMilliseconds = 190;
    private ScaleTransform? _openScale;


    private void PlayOpenAnimation()
    {
        if (_foldPhase != FoldPhase.Expanded || SystemParameters.ClientAreaAnimation == false) return;
        var card = CardRect;
        _openScale = new ScaleTransform(.94, .94, card.Right, card.Top);
        DesignCanvas.RenderTransform = _openScale;
        StudioOverlay.RenderTransform = _openScale;
        var ease = new CubicEase { EasingMode = EasingMode.EaseOut };
        var duration = TimeSpan.FromMilliseconds(OpenMilliseconds);
        var grow = new DoubleAnimation(.94, 1, duration) { EasingFunction = ease };
        grow.Completed += (_, _) => StopOpenAnimation();
        RootGrid.BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, duration) { EasingFunction = ease });
        _openScale.BeginAnimation(ScaleTransform.ScaleXProperty, grow);
        _openScale.BeginAnimation(ScaleTransform.ScaleYProperty, new DoubleAnimation(.94, 1, duration) { EasingFunction = ease });
    }

    private void StopOpenAnimation()
    {
        RootGrid.BeginAnimation(OpacityProperty, null);
        RootGrid.Opacity = 1;
        if (_openScale == null) return;
        _openScale.BeginAnimation(ScaleTransform.ScaleXProperty, null);
        _openScale.BeginAnimation(ScaleTransform.ScaleYProperty, null);
        _openScale = null;
        DesignCanvas.RenderTransform = Transform.Identity;
        StudioOverlay.RenderTransform = Transform.Identity;
    }

    internal static double Smooth(double from, double to, double progress)
    {
        double t = Math.Clamp((progress - from) / (to - from), 0, 1);
        return t * t * (3 - 2 * t);
    }

    private Task AnimateFoldAsync(double from, double to, int milliseconds)
    {
        _foldMotion?.TrySetCanceled();
        var completion = _foldMotion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        BeginAnimation(FoldProgressProperty, null);
        SetValue(FoldProgressProperty, from);
        RenderDismiss(from);
        var animation = new DoubleAnimation(from, to, TimeSpan.FromMilliseconds(milliseconds));
        animation.Completed += (_, _) => completion.TrySetResult();
        BeginAnimation(FoldProgressProperty, animation, HandoffBehavior.SnapshotAndReplace);
        return completion.Task;
    }

    private void DisposeFold()
    {
        _foldPhase = FoldPhase.Closed;
        _foldMotion?.TrySetCanceled(); _foldMotion = null;
        BeginAnimation(FoldProgressProperty, null);
        FoldLayer.Children.Clear();
        _departingTabs = [];
        _returnSurface = null;
        if (_returnIsland != null) _returnIsland.Opacity = 1;
    }

    /// <summary>Moves and sizes the HWND in one step so a corner drag never shows a two-step jump.</summary>
    private void SetWindowBounds(double left, double top, double width, double height)
    {
        var hwnd = new WindowInteropHelper(this).Handle;
        if (hwnd == IntPtr.Zero)
        {
            Width = width; Height = height; Left = left; Top = top;
            return;
        }
        var dpi = VisualTreeHelper.GetDpi(this);
        SetWindowPos(hwnd, IntPtr.Zero, (int)Math.Round(left * dpi.DpiScaleX), (int)Math.Round(top * dpi.DpiScaleY),
            (int)Math.Round(width * dpi.DpiScaleX), (int)Math.Round(height * dpi.DpiScaleY),
            SWP_NOZORDER | SWP_NOACTIVATE);
        // Keep the WPF properties in step so a later layout pass never restores the old size.
        SetCurrentValue(WidthProperty, width);
        SetCurrentValue(HeightProperty, height);
    }

    private Point CursorScreenDip()
    {
        if (!GetCursorPos(out var position)) return new Point(Left, Top);
        var dpi = VisualTreeHelper.GetDpi(this);
        return new Point(position.X / dpi.DpiScaleX, position.Y / dpi.DpiScaleY);
    }

    private const uint SWP_NOZORDER = 0x0004, SWP_NOACTIVATE = 0x0010;
    [StructLayout(LayoutKind.Sequential)] private struct FoldCursor { public int X, Y; }
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetCursorPos(out FoldCursor point);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetWindowPos(IntPtr hwnd, IntPtr after, int x, int y, int cx, int cy, uint flags);
}
