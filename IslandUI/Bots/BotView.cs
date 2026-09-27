using System.Diagnostics;
using System.Windows;
using System.Windows.Media;

namespace IslandUI.Bots;

/// <summary>
/// Controle WPF qui rend un <see cref="BotFrame"/>. Le corps est une forme pleine lisse (Catmull-Rom a travers les 64
/// echantillons polaires), les yeux sont des capsules blanches dessinees par-dessus
/// (l'arriere-plan du controle reste transparent : le bot montre ce qu'il y a derriere lui),
/// l'encoche de la pastille est un vrai trou dans le corps (Combine/Exclude).
///
/// Horloge : <see cref="CompositionTarget.Rendering"/>, cadence a ~30 fps. Le moteur reste
/// une fonction pure du temps : le controle ne fait que le dater et le dessiner.
/// </summary>
public class BotView : FrameworkElement
{
    private const double FrameInterval = 1.0 / 30;
    private const double ViewBox = BotRepere.DemiViewBox;

    private readonly BotAnimationEngine _engine = new(BotRepere.Rayon, "idle");
    private Stopwatch? _clock;
    private double _lastFrame = double.NegativeInfinity;

    private Brush _bodyBrush;
    private string _bodyHex = BotSkins.DefaultColorHex;

    private static readonly Brush EyeBrush = Freeze(new SolidColorBrush(Colors.White));
    private static readonly Lazy<Task> EyeFitWarmup = new(() => Task.Run(BotEyeFit.WarmUp));

    /// <summary>Eye colour (default white).</summary>
    public static readonly DependencyProperty EyeColorProperty = DependencyProperty.Register(
        nameof(EyeColor), typeof(Brush), typeof(BotView),
        new FrameworkPropertyMetadata(EyeBrush, FrameworkPropertyMetadataOptions.AffectsRender));

    public Brush EyeColor
    {
        get => (Brush)GetValue(EyeColorProperty);
        set => SetValue(EyeColorProperty, value);
    }
    private static readonly Brush NotifBrush = Freeze(new SolidColorBrush((Color)ColorConverter.ConvertFromString(BotDecor.NotifBlue)));

    private readonly Dictionary<(double, double), StreamGeometry> _capsuleCache = new();
    private readonly Dictionary<string, SolidColorBrush> _dotBrushCache = new();
    private readonly Dictionary<string, LinearGradientBrush> _arcBrushCache = new();

    static BotView()
    {
        // Le controle n'a pas de fond : ce qui entoure la silhouette est transparent.
    }

    public BotView()
    {
        _bodyBrush = Freeze(new SolidColorBrush((Color)ColorConverter.ConvertFromString(_bodyHex)));
        _playOnceTimer = new System.Windows.Threading.DispatcherTimer();
        _playOnceTimer.Tick += OnPlayOnceElapsed;
        _livelinessTimer = new System.Windows.Threading.DispatcherTimer();
        _livelinessTimer.Tick += OnLivelinessTick;
        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
        IsVisibleChanged += OnIsVisibleChanged;
    }

    /* ---------------------------------------------------------- proprietes */

    /// <summary>Etat courant (idle, thinking, wink, wide, alert, notify, exclaim, sleep, egg, hexagon, play, orbit, swirl, burst, comet).</summary>
    public static readonly DependencyProperty StateProperty = DependencyProperty.Register(
        nameof(State), typeof(string), typeof(BotView),
        new FrameworkPropertyMetadata("idle", FrameworkPropertyMetadataOptions.AffectsRender, OnStateChanged));

    public string State
    {
        get => (string)GetValue(StateProperty);
        set => SetValue(StateProperty, value);
    }

    private static void OnStateChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        var v = (BotView)d;
        // An incoming working/notification state supersedes a temporary playful state.
        if (!v._settingPlayState && v._playingOnce)
        {
            v._playOnceTimer.Stop();
            v._playingOnce = false;
        }
        if (e.NewValue is string id) v._engine.SetState(id, v.Now);
    }

    /// <summary>Couleur du corps. Par defaut l'encre mesuree sur la video (#0a0a0c).</summary>
    public static readonly DependencyProperty BodyColorProperty = DependencyProperty.Register(
        nameof(BodyColor), typeof(Brush), typeof(BotView),
        new FrameworkPropertyMetadata(Freeze(new SolidColorBrush((Color)ColorConverter.ConvertFromString(BotSkins.DefaultColorHex))),
            FrameworkPropertyMetadataOptions.AffectsRender, OnBodyColorChanged));

    public Brush BodyColor
    {
        get => (Brush)GetValue(BodyColorProperty);
        set => SetValue(BodyColorProperty, value);
    }

    private static void OnBodyColorChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        var v = (BotView)d;
        var brush = e.NewValue as Brush ?? Brushes.Black;
        if (brush.CanFreeze) brush.Freeze();
        v._bodyBrush = brush;
        v._bodyHex = brush is SolidColorBrush scb ? ToHex(scb.Color) : BotSkins.DefaultColorHex;
        v._engine.BodyColor = v._bodyHex;
    }

    /// <summary>Forme du corps sur les etats au repos (cercle, galet, squircle, capsule, triangle, hexagone, nuage, goutte).</summary>
    public static readonly DependencyProperty ShapeProperty = DependencyProperty.Register(
        nameof(Shape), typeof(string), typeof(BotView),
        new FrameworkPropertyMetadata(BotSkins.DefaultShape, FrameworkPropertyMetadataOptions.AffectsRender, OnShapeChanged));

    public string Shape
    {
        get => (string)GetValue(ShapeProperty);
        set => SetValue(ShapeProperty, value);
    }

    private static void OnShapeChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        var v = (BotView)d;
        v._engine.SetShape(BotAnimationEngine.ResolveShape(e.NewValue as string), v.Now);
    }

    /// <summary>Expression de repos (neutre, attentif, surpris, ...) — ne s'applique qu'aux etats a visage de repos.</summary>
    public static readonly DependencyProperty ExpressionProperty = DependencyProperty.Register(
        nameof(Expression), typeof(string), typeof(BotView),
        new FrameworkPropertyMetadata(BotExpressions.DefaultExpression, FrameworkPropertyMetadataOptions.AffectsRender, OnExpressionChanged));

    public string Expression
    {
        get => (string)GetValue(ExpressionProperty);
        set => SetValue(ExpressionProperty, value);
    }

    private static void OnExpressionChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        var v = (BotView)d;
        v._engine.SetExpression(BotAnimationEngine.ResolveExpression(e.NewValue as string), v.Now);
    }

    /// <summary>
    /// Couleur du fond sur lequel le bot est pose : ne sert qu'a la brume de profondeur des
    /// particules (qui se fondent dans le fond en s'eloignant).
    /// </summary>
    public static readonly DependencyProperty PaperColorProperty = DependencyProperty.Register(
        nameof(PaperColor), typeof(Color), typeof(BotView),
        new FrameworkPropertyMetadata(Colors.White, FrameworkPropertyMetadataOptions.AffectsRender));

    public Color PaperColor
    {
        get => (Color)GetValue(PaperColorProperty);
        set => SetValue(PaperColorProperty, value);
    }

    /// <summary>Regard dirige : yaw en degres (- = gauche, + = droite), pitch en degres.</summary>
    public static readonly DependencyProperty GazeYawProperty = DependencyProperty.Register(
        nameof(GazeYaw), typeof(double), typeof(BotView),
        new FrameworkPropertyMetadata(double.NaN, FrameworkPropertyMetadataOptions.AffectsRender, OnGazeChanged));

    public double GazeYaw
    {
        get => (double)GetValue(GazeYawProperty);
        set => SetValue(GazeYawProperty, value);
    }

    public static readonly DependencyProperty GazePitchProperty = DependencyProperty.Register(
        nameof(GazePitch), typeof(double), typeof(BotView),
        new FrameworkPropertyMetadata(double.NaN, FrameworkPropertyMetadataOptions.AffectsRender, OnGazeChanged));

    public double GazePitch
    {
        get => (double)GetValue(GazePitchProperty);
        set => SetValue(GazePitchProperty, value);
    }

    private static void OnGazeChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        var v = (BotView)d;
        if (double.IsNaN(v.GazeYaw) || double.IsNaN(v.GazePitch))
            v._engine.SetLook(null, v.Now);
        else
            v._engine.SetLook(new BotLook(v.GazeYaw, v.GazePitch, 1.0, 0, 0), v.Now);
    }

    /* --------------------------------------------------- liveliness / play-once */

    // BotsUI 随机状态:待机时偶尔来一个俏皮小动画
    private static readonly string[] PlayfulStates = ["wink", "play", "orbit", "wide", "exclaim"];
    private static readonly Random Rng = new();

    private string _baseState = "idle";
    private readonly System.Windows.Threading.DispatcherTimer _playOnceTimer;
    private readonly System.Windows.Threading.DispatcherTimer _livelinessTimer;
    private bool _playingOnce;
    private bool _settingPlayState;

    /// <summary>True = idle 时随机播放俏皮动画(wink/play/orbit/...),让 bot 活跃起来.</summary>
    public static readonly DependencyProperty LivelinessProperty = DependencyProperty.Register(
        nameof(Liveliness), typeof(bool), typeof(BotView),
        new FrameworkPropertyMetadata(false, OnLivelinessChanged));

    public bool Liveliness
    {
        get => (bool)GetValue(LivelinessProperty);
        set => SetValue(LivelinessProperty, value);
    }

    private static void OnLivelinessChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        var v = (BotView)d;
        if ((bool)e.NewValue) v.StartLiveliness();
        else v.StopLiveliness();
    }

    /// <summary>播放一个一次性状态,结束后回到当前基础状态.</summary>
    public void PlayOnce(string state)
    {
        if (!BotStates.ById.ContainsKey(state)) return;
        if (!_playingOnce) _baseState = State;
        _playOnceTimer.Stop();
        _playingOnce = true;
        _settingPlayState = true;
        try { SetCurrentValue(StateProperty, state); }
        finally { _settingPlayState = false; }
        double back = BotStates.ById[state].Duration + BotStates.ById[state].Morph + 0.2;
        _playOnceTimer.Interval = TimeSpan.FromSeconds(back);
        _playOnceTimer.Start();
    }

    private void OnPlayOnceElapsed(object? sender, EventArgs e)
    {
        _playOnceTimer.Stop();
        if (!_playingOnce) return;
        _playingOnce = false;
        SetCurrentValue(StateProperty, _baseState);
    }

    private void StartLiveliness()
    {
        StopLiveliness();
        if (IsLoaded && IsVisible && Liveliness) ScheduleNextPlay();
    }

    private void StopLiveliness()
    {
        _livelinessTimer.Stop();
    }

    private void ScheduleNextPlay()
    {
        // Reuse exactly one timer. The old implementation left every previous
        // periodic timer running, multiplying callbacks and retaining unloaded Bots.
        _livelinessTimer.Stop();
        _livelinessTimer.Interval = TimeSpan.FromSeconds(6 + Rng.NextDouble() * 9);
        _livelinessTimer.Start();
    }

    private void OnLivelinessTick(object? sender, EventArgs e)
    {
        _livelinessTimer.Stop();
        if (!IsLoaded || !IsVisible || !Liveliness) return;
        // 只在待机状态插播,工作中的 bot 不打扰
        if (State is "idle" or "swirl")
            PlayOnce(PlayfulStates[Rng.Next(PlayfulStates.Length)]);
        ScheduleNextPlay();
    }

    /* ------------------------------------------------------------- horloge */

    private double Now => _clock?.Elapsed.TotalSeconds ?? 0;

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        _clock ??= Stopwatch.StartNew();
        // La table des decalages des yeux se construit une fois (~dizaines de ms) : autant
        // la prechauffer hors du thread d'interface.
        _ = EyeFitWarmup.Value;
        // Applique les reglages poses avant le chargement (XAML).
        _engine.SetShape(BotAnimationEngine.ResolveShape(Shape), Now);
        _engine.SetExpression(BotAnimationEngine.ResolveExpression(Expression), Now);
        if (BotStates.ById.ContainsKey(State) && State != _engine.State) _engine.Reset(State, Now);
        Attach();
        if (Liveliness) StartLiveliness();
    }

    private void OnUnloaded(object sender, RoutedEventArgs e)
    {
        Detach();
        StopLiveliness();
        OnPlayOnceElapsed(this, EventArgs.Empty);
    }

    private void OnIsVisibleChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if ((bool)e.NewValue)
        {
            Attach();
            if (Liveliness) StartLiveliness();
        }
        else
        {
            Detach();
            StopLiveliness();
        }
    }

    private bool _attached;

    private void Attach()
    {
        if (_attached || !IsLoaded || !IsVisible) return;
        CompositionTarget.Rendering += OnRendering;
        _attached = true;
    }

    private void Detach()
    {
        if (!_attached) return;
        CompositionTarget.Rendering -= OnRendering;
        _attached = false;
    }

    private void OnRendering(object? sender, EventArgs e)
    {
        double now = Now;
        // ~30 fps : le moteur est une fonction pure du temps, sauter des images ne coute rien.
        if (now - _lastFrame < FrameInterval - 0.001) return;
        _lastFrame = now;
        InvalidateVisual();
    }

    /* -------------------------------------------------------------- rendu */

    protected override void OnRender(DrawingContext dc)
    {
        base.OnRender(dc);
        double w = ActualWidth, h = ActualHeight;
        if (w <= 0 || h <= 0) return;

        var frame = _engine.Sample(Now);
        double k = Math.Min(w, h) / (2 * ViewBox);

        dc.PushTransform(new TranslateTransform(w / 2, h / 2));
        dc.PushTransform(new ScaleTransform(k, k));

        // moitie arriere des orbites : dessinee avant le corps, donc occultee
        DrawArcs(dc, frame, front: false);
        if (frame.DotsBehind) DrawDots(dc, frame);

        DrawBody(dc, frame);
        DrawEyes(dc, frame);

        if (!frame.DotsBehind) DrawDots(dc, frame);

        if (frame.Notif is { } notif)
            dc.DrawEllipse(NotifBrush, null, new Point(notif.X, notif.Y), notif.R, notif.R);

        // moitie avant des orbites
        DrawArcs(dc, frame, front: true);

        dc.Pop();
        dc.Pop();
    }

    private void DrawBody(DrawingContext dc, BotFrame frame)
    {
        Geometry body = BuildBodyGeometry(frame.BodyPoints);
        // L'encoche de la pastille est un vrai trou dans le corps : elle montre ce qui est
        // derriere, comme le faisait le masque de l'original.
        if (frame.Notch is { } notch)
        {
            body = new CombinedGeometry(
                GeometryCombineMode.Exclude,
                body,
                new EllipseGeometry(new Point(notch.X, notch.Y), notch.R, notch.R),
                null);
        }
        dc.PushOpacity(frame.BodyAlpha);
        dc.DrawGeometry(_bodyBrush, null, body);
        dc.Pop();
    }

    private void DrawEyes(DrawingContext dc, BotFrame frame)
    {
        foreach (var eye in frame.Eyes)
        {
            var capsule = GetCapsule(eye.Width, eye.Height);
            // Matrice tangente + clignement, au sens SVG matrix(a,b,c,d,e,f).
            dc.PushTransform(new MatrixTransform(eye.A, eye.B, eye.C, eye.D, eye.CenterX, eye.CenterY));
            dc.PushOpacity(eye.Alpha * frame.BodyAlpha);
            dc.DrawGeometry(EyeColor, null, capsule);
            dc.Pop();
            dc.Pop();
        }
    }

    private void DrawDots(DrawingContext dc, BotFrame frame)
    {
        string paperHex = ToHex(PaperColor);
        foreach (var dot in frame.Dots)
        {
            // La couleur suit celle du corps par defaut ; la profondeur sert aux particules,
            // qui se fondent dans le fond a mesure qu'elles s'eloignent.
            string hex = dot.Color
                ?? (dot.Depth is { } depth ? BotSkins.MixHex(paperHex, _bodyHex, depth) : _bodyHex);
            var brush = GetDotBrush(hex);
            dc.PushOpacity(dot.Opacity);
            if (dot.Polygon is { } polygon)
            {
                // forme en unites de rayon de boule, centree sur l'origine (la goutte du "!")
                var g = BuildPolygonGeometry(polygon);
                var group = new TransformGroup();
                group.Children.Add(new ScaleTransform(frame.Scale, frame.Scale));
                group.Children.Add(new RotateTransform(dot.Rot));
                group.Children.Add(new TranslateTransform(dot.X, dot.Y));
                dc.PushTransform(group);
                dc.DrawGeometry(brush, null, g);
                dc.Pop();
            }
            else
            {
                dc.DrawEllipse(brush, null, new Point(dot.X, dot.Y), dot.R, dot.R);
            }
            dc.Pop();
        }
    }

    private void DrawArcs(DrawingContext dc, BotFrame frame, bool front)
    {
        foreach (var arc in frame.Arcs)
        {
            var runs = front ? arc.Front : arc.Back;
            if (runs.Count == 0) continue;
            var brush = GetArcBrush(arc);
            var pen = new Pen(brush, arc.Width)
            {
                StartLineCap = PenLineCap.Round,
                EndLineCap = PenLineCap.Round,
                LineJoin = PenLineJoin.Round
            };
            dc.PushOpacity(arc.Opacity);
            foreach (var run in runs)
            {
                if (run.Count < 2) continue;
                var g = new StreamGeometry();
                using (var ctx = g.Open())
                {
                    ctx.BeginFigure(new Point(run[0].X, run[0].Y), false, false);
                    ctx.PolyLineTo(run.Skip(1).Select(p => new Point(p.X, p.Y)).ToList(), true, false);
                }
                dc.DrawGeometry(null, pen, g);
            }
            dc.Pop();
        }
    }

    /* ------------------------------------------------------------- caches */

    private static T Freeze<T>(T freezable) where T : Freezable
    {
        freezable.Freeze();
        return freezable;
    }

    private static string ToHex(Color c) => $"#{c.R:x2}{c.G:x2}{c.B:x2}";

    /// <summary>Contour du corps : Catmull-Rom ferme a travers les echantillons polaires.</summary>
    private static StreamGeometry BuildBodyGeometry(IReadOnlyList<Vec2> pts)
    {
        var g = new StreamGeometry();
        if (pts.Count >= 3)
        {
            using var ctx = g.Open();
            ctx.BeginFigure(new Point(pts[0].X, pts[0].Y), true, true);
            foreach (var (c1, c2, p) in Shape2D.ClosedCubics(pts))
                ctx.BezierTo(new Point(c1.X, c1.Y), new Point(c2.X, c2.Y), new Point(p.X, p.Y), true, false);
        }
        return g;
    }

    private static StreamGeometry BuildPolygonGeometry(IReadOnlyList<Vec2> pts)
    {
        var g = new StreamGeometry();
        if (pts.Count >= 3)
        {
            using var ctx = g.Open();
            ctx.BeginFigure(new Point(pts[0].X, pts[0].Y), true, true);
            ctx.PolyLineTo(pts.Skip(1).Select(p => new Point(p.X, p.Y)).ToList(), true, false);
        }
        return g;
    }

    /// <summary>Capsule (stade) verticale centree sur l'origine : la forme exacte des yeux.</summary>
    private StreamGeometry GetCapsule(double w, double h)
    {
        // Les dimensions glissent continument pendant un morph : on arrondit au demi-pixel
        // de viewBox pour que le cache reste borne sans difference visible.
        var key = (Math.Round(w * 2) / 2, Math.Round(h * 2) / 2);
        if (_capsuleCache.TryGetValue(key, out var cached)) return cached;
        if (_capsuleCache.Count > 256) _capsuleCache.Clear();
        double hw = Math.Max(key.Item1, 0.01) / 2, hh = Math.Max(key.Item2, 0.01) / 2;
        double r = Math.Min(hw, hh);
        var g = new StreamGeometry();
        using (var ctx = g.Open())
        {
            ctx.BeginFigure(new Point(-hw, -hh + r), true, true);
            ctx.ArcTo(new Point(-hw + r, -hh), new Size(r, r), 0, false, SweepDirection.Clockwise, true, false);
            ctx.LineTo(new Point(hw - r, -hh), true, false);
            ctx.ArcTo(new Point(hw, -hh + r), new Size(r, r), 0, false, SweepDirection.Clockwise, true, false);
            ctx.LineTo(new Point(hw, hh - r), true, false);
            ctx.ArcTo(new Point(hw - r, hh), new Size(r, r), 0, false, SweepDirection.Clockwise, true, false);
            ctx.LineTo(new Point(-hw + r, hh), true, false);
            ctx.ArcTo(new Point(-hw, hh - r), new Size(r, r), 0, false, SweepDirection.Clockwise, true, false);
        }
        g.Freeze();
        _capsuleCache[key] = g;
        return g;
    }

    private SolidColorBrush GetDotBrush(string hex)
    {
        if (_dotBrushCache.TryGetValue(hex, out var cached)) return cached;
        if (_dotBrushCache.Count > 256) _dotBrushCache.Clear();
        var brush = Freeze(new SolidColorBrush((Color)ColorConverter.ConvertFromString(hex)));
        _dotBrushCache[hex] = brush;
        return brush;
    }

    private LinearGradientBrush GetArcBrush(BotArc arc)
    {
        // Le degrade ne depend que de la graine de l'arc : il est constant, seul le trace bouge.
        string key = string.Join('|', arc.GradX1, arc.GradY1, arc.GradX2, arc.GradY2, string.Join(',', arc.GradStops));
        if (_arcBrushCache.TryGetValue(key, out var cached)) return cached;
        if (_arcBrushCache.Count > 64) _arcBrushCache.Clear();
        var brush = new LinearGradientBrush
        {
            MappingMode = BrushMappingMode.Absolute,
            StartPoint = new Point(arc.GradX1, arc.GradY1),
            EndPoint = new Point(arc.GradX2, arc.GradY2)
        };
        for (int i = 0; i < arc.GradStops.Length; i++)
        {
            brush.GradientStops.Add(new GradientStop(
                (Color)ColorConverter.ConvertFromString(arc.GradStops[i]),
                arc.GradStops.Length > 1 ? (double)i / (arc.GradStops.Length - 1) : 0));
        }
        brush.Freeze();
        _arcBrushCache[key] = brush;
        return brush;
    }
}
