namespace IslandUI.Bots;

/// <summary>
/// Ou le bot porte son regard quand quelque chose d'exterieur le pilote — port de Look.
///
/// Yaw et Pitch sont des directions ABSOLUES qui remplacent celles de la pose a mesure que
/// Mix monte (0 = pas du tout). Wander dit, separement, ce qui reste de derive automatique.
/// Spin est un tour a parcourir EN CHEMIN, en degres, qui fond vers 0 avec l'arrivee —
/// -360 deg etant le meme angle que 0, il ne change pas la destination.
/// </summary>
public readonly record struct BotLook(double Yaw, double Pitch, double Mix, double Spin, double Wander);

/// <summary>Options du personnalisateur : forme, couleur, expression de repos.</summary>
public sealed record BotOptions(
    /// <summary>Id de forme (cercle, galet, squircle, capsule, triangle, hexagone, nuage, goutte). null = cercle.</summary>
    string? Shape = null,
    /// <summary>Id de couleur (encre, bleu, ...) ou hex "#rrggbb". null = encre.</summary>
    string? Color = null,
    /// <summary>Id d'expression (neutre, attentif, ...). null = neutre.</summary>
    string? Expression = null);

/// <summary>
/// Un oeil rendu : capsule (Width x Height, en unites de viewBox, avant transformation)
/// passee par la matrice tangente 2x2 [A B C D] au sens SVG matrix(a,b,c,d), centree en
/// (CenterX, CenterY). Le clignement est deja compose dans B et D (ecrasement vertical
/// ecran).
/// </summary>
public sealed class BotEye
{
    public double CenterX, CenterY;
    /// <summary>Dimensions de la capsule avant transformation (cfg.W * R, cfg.H * R).</summary>
    public double Width, Height;
    public double A, B, C, D;
    public double Alpha;

    /// <summary>Largeur ecran (axe court projete).</summary>
    public double ScreenWidth => Math.Sqrt(A * A + B * B) * Width;
    /// <summary>Longueur ecran (axe long projete).</summary>
    public double ScreenLength => Math.Sqrt(C * C + D * D) * Height;
    /// <summary>Angle du grand axe a l'ecran, en degres.</summary>
    public double AngleDeg => Math.Atan2(D, C) * 180 / Math.PI - 90;
}

/// <summary>
/// Ce que <see cref="BotAnimationEngine.Sample(double)"/> rend a un instant : le corps comme points
/// (a lisser en Catmull-Rom) et comme echantillons polaires, les deux yeux en descripteurs
/// de capsule, le decor (points, arcs, pastille) et les opacites.
/// </summary>
public sealed class BotFrame
{
    /// <summary>Contour du corps en unites de viewBox (64 points, a fermer et lisser).</summary>
    public IReadOnlyList<Vec2> BodyPoints = [];
    /// <summary>Le meme contour en echantillons polaires (angle, rayon), theta = 0 a droite, sens horaire.</summary>
    public IReadOnlyList<(double Angle, double Radius)> BodyPolar = [];
    /// <summary>Pose de la silhouette qui a produit BodyPoints (rotation, centre, squash).</summary>
    public double BodyRot, BodyCx, BodyCy, BodySx, BodySy;
    public double BodyAlpha = 1;
    /// <summary>Couleur du corps resolue (hex "#rrggbb").</summary>
    public string BodyColor = BotSkins.DefaultColorHex;
    public IReadOnlyList<BotEye> Eyes = [];
    public IReadOnlyList<BotDot> Dots = [];
    /// <summary>true = les points passent derriere le corps (particules de l'eclatement).</summary>
    public bool DotsBehind;
    public IReadOnlyList<BotArc> Arcs = [];
    public (double X, double Y, double R)? Notif;
    public (double X, double Y, double R)? Notch;
    /// <summary>Echelle du moteur : rayon de la boule au repos en unites de viewBox.</summary>
    public double Scale = BotRepere.Rayon;
}

/// <summary>
/// Moteur sans horloge. <see cref="Sample(double)"/>
/// est une fonction pure du temps : pause, reprise, ralenti et saut a une date arbitraire
/// donnent exactement la meme image. L'etat externe entre par des setters DATES, jamais par
/// une variable lue pendant l'echantillonnage.
///
/// Tests d'origine (engine.test.ts), empreintes largeur x hauteur en diametres de boule :
/// idle 2.0 x 2.0 a t=0.5 ; egg 1.653 x 2.0 a t=0.9 ; hexagon 1.82 x 2.01 ; exclaim
/// 0.263 x 0.842 ; alert 0.421 x 0.753 a t=0.8 ; sleep 0.317 x 0.317 ; comet 0.258 x 0.258.
/// Un changement d'etat en plein fondu melange depuis la pose composite figee (saut mesure :
/// 8.0 px contre 35.9 px avant le correctif). Un setLook a NaN est refuse et le moteur garde
/// la derniere cible saine.
/// </summary>
public sealed class BotAnimationEngine
{
    /// <summary>Rayon de la boule au repos, en unites de viewBox.</summary>
    public readonly double Scale;

    private string _cur;
    private string? _prev;

    /// <summary>
    /// Pose de depart FIGEE, posee seulement quand un changement d'etat arrive alors qu'un
    /// fondu est deja en cours (cf. SetState).
    /// </summary>
    private Pose? _departFige;
    private double _tCur, _tPrev;
    private double _blinkAt = -10;
    private double[]? _shape;
    private double[]? _shapePrev;
    private double _shapeAt = -10;
    private BotExpression? _expr;
    private BotExpression? _exprPrev;
    private double _exprAt = -10;
    private BotLook _look = NoLook;
    private BotLook _lookPrev = NoLook;
    private double _lookAt = -10;
    /// <summary>Duree de rattrapage en cours ; voir LookMorph, sa valeur par defaut.</summary>
    private double _lookMorph = LookMorph;

    /// <summary>Couleur du corps (hex "#rrggbb"), recopiee sur chaque frame.</summary>
    public string BodyColor { get; set; } = BotSkins.DefaultColorHex;

    private static readonly BotLook NoLook = new(0, 0, 0, 0, 1);

    /// <summary>Duree du morph quand on change la forme du corps.</summary>
    public const double ShapeMorph = 0.45;

    /// <summary>
    /// Duree de rattrapage du regard vers la cible. Plus court que ShapeMorph : un regard
    /// qui suit doit paraitre attentif, pas visqueux. Comme la cible est reposee a chaque
    /// mouvement de souris, c'est cette duree qui donne au suivi son inertie.
    /// </summary>
    public const double LookMorph = 0.24;

    public BotAnimationEngine(double scale = BotRepere.Rayon, string initial = "idle",
        double[]? shape = null, BotExpression? expression = null)
    {
        Scale = scale;
        if (!BotStates.ById.ContainsKey(initial)) initial = "idle";
        _cur = initial;
        _shape = shape;
        _expr = expression;
    }

    /* -------------------------------------------------------- reglages dates */

    /// <summary>
    /// Expression de repos choisie dans le personnalisateur. Comme la forme, elle glisse
    /// vers la nouvelle valeur au lieu de sauter. Comparaison par reference, comme
    /// l'original : passez les instances de <see cref="BotExpressions.ExpressionById"/>.
    /// </summary>
    public void SetExpression(BotExpression? expression, double now = 0)
    {
        if (ReferenceEquals(expression, _expr)) return;
        _exprPrev = _expr;
        _expr = expression;
        _exprAt = now;
    }

    /// <summary>Expression effective a l'instant <paramref name="now"/>, morph en cours compris.</summary>
    private BotExpression? ExprAtTime(double now)
    {
        var to = _expr;
        var from = _exprPrev;
        if (to == null || from == null) return to;
        double k = (now - _exprAt) / ShapeMorph;
        if (k >= 1) return to;
        return BotExpressions.BlendExpression(from, to, BotMath.EaseOutQuint(BotMath.Clamp(k)));
    }

    /// <summary>
    /// Forme choisie dans le personnalisateur (tableau de rayons). Elle ne remplace le corps
    /// que sur les etats <c>baseBody</c> ; partout ailleurs la silhouette EST l'animation.
    /// Comparaison par reference : passez les tableaux de <see cref="BotSkins.ShapeById"/>.
    /// </summary>
    public void SetShape(double[]? radii, double now = 0)
    {
        if (ReferenceEquals(radii, _shape)) return;
        _shapePrev = _shape;
        _shape = radii;
        _shapeAt = now;
    }

    /// <summary>
    /// Forme effective a l'instant <paramref name="now"/>, morph en cours compris. Ne remet
    /// PAS ShapePrev a null en fin de morph : Sample doit rester une fonction pure du temps.
    /// </summary>
    private double[]? ShapeAtTime(double now)
    {
        var to = _shape;
        var from = _shapePrev;
        if (to == null || from == null) return to;
        double k = (now - _shapeAt) / ShapeMorph;
        if (k >= 1) return to;
        double t = BotMath.EaseOutQuint(BotMath.Clamp(k));
        // alloue seulement pendant le morph ; hors morph on rend le tableau tel quel
        var outp = new double[to.Length];
        for (int i = 0; i < to.Length; i++)
            outp[i] = BotMath.Lerp(i < from.Length ? from[i] : to[i], to[i], t);
        return outp;
    }

    /// <summary>
    /// Nouvelle cible de regard, null pour revenir a celui de l'etat. Elle repart de la
    /// valeur COURANTE, et non de la cible precedente : appelee a chaque mouvement de
    /// pointeur, repartir de l'ancienne cible ferait reculer le regard d'un cran avant
    /// chaque rattrapage.
    /// </summary>
    public void SetLook(BotLook? look, double now, double morph = LookMorph)
    {
        // Une cible non finie est refusee. Le moteur GARDE la derniere : un NaN pose une
        // seule fois se propagerait a chaque image et le bot ne se reposerait plus jamais.
        if (look is { } l && !double.IsFinite(l.Yaw + l.Pitch + l.Mix + l.Spin + l.Wander))
            return;
        _lookPrev = LookAtTime(now);
        _look = look ?? NoLook;
        _lookAt = now;
        _lookMorph = morph;
    }

    /// <summary>Regard effectif a l'instant <paramref name="now"/>, rattrapage en cours compris.</summary>
    private BotLook LookAtTime(double now)
    {
        double k = (now - _lookAt) / _lookMorph;
        if (k >= 1) return _look;
        double t = BotMath.EaseOutQuint(BotMath.Clamp(k));
        return new BotLook(
            BotMath.Lerp(_lookPrev.Yaw, _look.Yaw, t),
            BotMath.Lerp(_lookPrev.Pitch, _look.Pitch, t),
            BotMath.Lerp(_lookPrev.Mix, _look.Mix, t),
            BotMath.Lerp(_lookPrev.Spin, _look.Spin, t),
            BotMath.Lerp(_lookPrev.Wander, _look.Wander, t));
    }

    /* ------------------------------------------------------------- interne */

    private static EyeCfg LerpEye(EyeCfg a, EyeCfg b, double t) => new(
        BotMath.Lerp(a.W, b.W, t),
        BotMath.Lerp(a.H, b.H, t),
        BotMath.Lerp(a.Open, b.Open, t),
        BotMath.Lerp(a.Tilt, b.Tilt, t));

    /// <summary>Interpolation de deux poses. Le decor se croise en opacite, pas en geometrie.</summary>
    private static Pose BlendPose(Pose a, Pose b, double t)
    {
        double fadeOut = 1 - t;
        var outp = new Pose
        {
            Sil = Shape2D.Blend(a.Sil, b.Sil, t),
            OffX = BotMath.Lerp(a.OffX, b.OffX, t),
            OffY = BotMath.Lerp(a.OffY, b.OffY, t),
            Gaze = new HeadGaze(
                BotMath.Lerp(a.Gaze.Yaw, b.Gaze.Yaw, t),
                BotMath.Lerp(a.Gaze.Pitch, b.Gaze.Pitch, t),
                BotMath.Lerp(a.Gaze.Roll, b.Gaze.Roll, t)),
            Split = BotMath.Lerp(a.Split, b.Split, t),
            Eyes = [LerpEye(a.Eyes[0], b.Eyes[0], t), LerpEye(a.Eyes[1], b.Eyes[1], t)],
            EyeAlpha = BotMath.Lerp(a.EyeAlpha, b.EyeAlpha, t),
            BodyAlpha = BotMath.Lerp(a.BodyAlpha, b.BodyAlpha, t),
            // la pastille appartient a un seul des deux etats, elle ne se melange pas
            Notif = t < 0.5 ? a.Notif : b.Notif,
            DotsBehind = t < 0.5 ? a.DotsBehind : b.DotsBehind
        };
        foreach (var d in a.Dots) outp.Dots.Add(new BotDot { X = d.X, Y = d.Y, R = d.R, Opacity = d.Opacity * fadeOut, Color = d.Color, Depth = d.Depth, Polygon = d.Polygon, Rot = d.Rot });
        foreach (var d in b.Dots) outp.Dots.Add(new BotDot { X = d.X, Y = d.Y, R = d.R, Opacity = d.Opacity * t, Color = d.Color, Depth = d.Depth, Polygon = d.Polygon, Rot = d.Rot });
        foreach (var r in a.Arcs) outp.Arcs.Add(new ArcSpec { Id = "a" + r.Id, Seed = r.Seed, T = r.T, Opacity = r.Opacity * fadeOut });
        foreach (var r in b.Arcs) outp.Arcs.Add(new ArcSpec { Id = "b" + r.Id, Seed = r.Seed, T = r.T, Opacity = r.Opacity * t });
        return outp;
    }

    private Pose Posed(StateDef def, double t, double[]? shape, BotExpression? expr)
    {
        Pose pose = def.PoseAt(t);
        if (def.BaseBody && shape != null)
        {
            // on garde la pose (rotation, decalage, squash) et on n'echange que le profil
            pose = pose.Clone();
            pose.Sil = pose.Sil.CloneWith(shape);
        }
        if (def.BaseFace && expr != null)
        {
            pose = pose.Clone();
            pose.Gaze = expr.Gaze;
            pose.Split = expr.Split;
            pose.Eyes = expr.Eyes;
        }
        return pose;
    }

    /// <summary>
    /// Decalage des yeux a l'instant <paramref name="now"/> pour un etat donne, en unites de
    /// rayon de boule. LU dans la table de <see cref="BotEyeFit"/> et interpole, jamais
    /// recalcule — sur les BORNES de chaque morph (shapePrev/shape, exprPrev/expr), jamais
    /// sur la valeur interpolee, qui n'a pas d'identite et n'existe dans aucune table.
    /// </summary>
    private Vec2 DecalageAtTime(double now, string state)
    {
        // Un axe de morph : on lit la table sur ses deux bornes et on interpole avec sa courbe.
        Vec2 SurAxe(double debut, double duree, Vec2 a, Vec2 b)
        {
            if (a.Equals(b)) return b;
            double k = (now - debut) / duree;
            if (k >= 1) return b;
            double t = BotMath.EaseOutQuint(BotMath.Clamp(k));
            return new Vec2(BotMath.Lerp(a.X, b.X, t), BotMath.Lerp(a.Y, b.Y, t));
        }

        // axe de l'expression, pour chacune des deux formes en presence
        Vec2 ParForme(double[]? radii) => SurAxe(
            _exprAt, ShapeMorph,
            BotEyeFit.EyeOffset(radii, state, _exprPrev?.Id),
            BotEyeFit.EyeOffset(radii, state, _expr?.Id));

        // puis axe de la forme
        return SurAxe(_shapeAt, ShapeMorph, ParForme(_shapePrev), ParForme(_shape));
    }

    /// <summary>Etat courant.</summary>
    public string State => _cur;

    /// <summary>
    /// Repart sur <paramref name="id"/> SANS etat precedent, comme un moteur neuf pose sur
    /// cet etat — c'est ce que veut dire "rembobiner" pour ce moteur.
    /// </summary>
    public void Reset(string id, double now)
    {
        if (!BotStates.ById.ContainsKey(id)) return;
        _cur = id;
        _prev = null;
        _departFige = null;
        _tCur = now;
        _tPrev = now;
        _blinkAt = -10;
    }

    /// <summary>
    /// Origine du fondu en cours : la pose figee s'il y en a une, sinon l'etat quitte evalue
    /// a son propre temps ecoule — donc encore en train de s'animer, ce qui est voulu.
    /// </summary>
    private Pose? Origine(double now, double[]? shape, BotExpression? expr)
    {
        if (_departFige != null) return _departFige;
        if (_prev == null) return null;
        var prevDef = BotStates.ById[_prev];
        return Posed(prevDef, Math.Max(0, now - _tPrev), shape, expr);
    }

    /// <summary>
    /// Pose composite a l'instant <paramref name="now"/>, fondu en cours compris :
    /// exactement ce que Sample melange, avant la couche de vie au repos et de regard.
    /// </summary>
    private Pose PoseComposee(double now)
    {
        var def = BotStates.ById[_cur];
        var shape = ShapeAtTime(now);
        var expr = ExprAtTime(now);
        var pose = Posed(def, Math.Max(0, now - _tCur), shape, expr);
        double since = now - _tCur;
        if (since >= def.Morph) return pose;
        var origine = Origine(now, shape, expr);
        if (origine == null) return pose;
        return BlendPose(origine, pose, BotMath.EaseOutQuint(BotMath.Clamp(since / def.Morph)));
    }

    /// <summary>
    /// Changement d'etat, date. Le moteur ne garde qu'UNE case d'historique : un changement
    /// qui arrive pendant un fondu fige la pose composite courante et melange depuis elle —
    /// continu par construction, quel que soit le nombre de changements enchaines. Et
    /// SEULEMENT dans ce cas : figer a chaque changement arreterait net l'animation de
    /// l'etat qu'on quitte pendant tout le fondu (le "!" d'alert se figerait en pleine
    /// course), alors qu'il n'y a rien a corriger hors morph.
    /// </summary>
    public void SetState(string id, double now)
    {
        if (id == _cur || !BotStates.ById.ContainsKey(id)) return;
        double morph = BotStates.ById[_cur].Morph;
        bool enPleinFondu = _prev != null && now - _tCur < morph;
        _departFige = enPleinFondu ? PoseComposee(now) : null;
        _prev = _cur;
        _tPrev = _tCur;
        _cur = id;
        _tCur = now;
        // Dans la video, chaque changement de forme est masque par un clignement.
        if (BotStates.ById[id].BlinkIn) _blinkAt = now;
    }

    /* -------------------------------------------------------------- sampling */

    /// <summary>Echantillonne le bot a la date <paramref name="now"/> (secondes). Fonction pure du temps.</summary>
    public BotFrame Sample(double now)
    {
        double R = Scale;
        var def = BotStates.ById[_cur];
        var shape = ShapeAtTime(now);
        var expr = ExprAtTime(now);
        Pose pose = Posed(def, Math.Max(0, now - _tCur), shape, expr);
        Vec2 decalage = DecalageAtTime(now, _cur);

        // --- transition -------------------------------------------------------
        double since = now - _tCur;
        // L'etat precedent n'est jamais purge : since < morph suffit a l'ignorer une fois le
        // fondu passe, et l'oublier rendrait le moteur non rejouable.
        var origine = since < def.Morph ? Origine(now, shape, expr) : null;
        if (origine != null)
        {
            // Ease-out exponentiel : la courbe mesuree sur la video. Le corps n'a pas
            // d'overshoot (seuls la pastille et l'ouverture des yeux en ont). Le ratio est
            // borne : relire une date ANTERIEURE au changement donnerait un ratio negatif,
            // que l'ease-out extrapole — la silhouette partirait trente fois trop loin.
            double ratio = BotMath.EaseOutQuint(BotMath.Clamp(since / def.Morph));
            pose = BlendPose(origine, pose, ratio);
            // Le decalage des yeux suit la MEME courbe que la silhouette qui le motive.
            if (_prev is { } quitte)
            {
                Vec2 avant = DecalageAtTime(now, quitte);
                decalage = new Vec2(
                    BotMath.Lerp(avant.X, decalage.X, ratio),
                    BotMath.Lerp(avant.Y, decalage.Y, ratio));
            }
        }

        // --- vie au repos -----------------------------------------------------
        bool alive = pose.EyeAlpha > 0.01;
        var look = LookAtTime(now);
        var life = BotFace.LivelinessAt(now, alive ? look.Wander : 0, alive);

        // Les deux visees REMPLACENT celles de la pose au lieu de s'y ajouter, et le tour se
        // retranche en chemin. La derive s'ajoute APRES le melange, sinon la cible
        // l'annulerait en meme temps que la pose — or elle doit survivre a une tete tournee
        // sans pointeur. Le roulis, lui, ne suit rien : la tete est penchee de -13 deg dans
        // la video, et la faire rouler avec le curseur casse cette signature.
        var gaze = new HeadGaze(
            BotMath.Lerp(pose.Gaze.Yaw, look.Yaw, look.Mix) + life.DYaw - look.Spin,
            BotMath.Lerp(pose.Gaze.Pitch, look.Pitch, look.Mix) + life.DPitch,
            pose.Gaze.Roll + life.DRoll);

        // clignement declenche par le changement d'etat, en plus du calendrier
        double forced = BotMath.Clamp((now - _blinkAt) / 0.2);
        double forcedLid = forced < 1 ? Math.Abs(forced * 2 - 1) : 1;
        double lid = Math.Min(life.Lid, forcedLid);

        double offX = pose.OffX + life.DriftX;
        double offY = pose.OffY + life.DriftY;

        // --- corps ------------------------------------------------------------
        var sil = pose.Sil.Clone();
        sil.Cx += offX;
        sil.Cy += offY;
        sil.Sy *= life.Breath;
        Vec2[] bodyPoints = Shape2D.ToPoints(sil, R);

        // --- yeux -------------------------------------------------------------
        // Les yeux vivent sur une sphere de rayon 1 ; des que la silhouette n'est plus un
        // cercle, on les ramene au prorata du rayon reel dans leur direction, sinon ils
        // debordent et le masque les coupe.
        double BodyRadius(double x, double y) =>
            Shape2D.RadiusAtAngle(pose.Sil.Radii, Math.Atan2(y, x) - pose.Sil.Rot);

        var eyes = new List<BotEye>(2);
        if (pose.EyeAlpha > 0.01)
        {
            var poses = BotFace.EyePoses(gaze, R, pose.Split);
            for (int i = 0; i < 2; i++)
            {
                EyePose e = poses[i];
                if (e.Depth <= 0.02) continue;
                EyeCfg cfg = pose.Eyes[i];
                double fit = BodyRadius(e.X, e.Y);
                // Inclinaison propre de l'oeil : on compose le repere tangent avec une
                // rotation dans le plan de l'oeil (Basis x Rot). C'est ce qui permet des
                // inclinaisons en miroir entre les deux yeux.
                double phi = cfg.Tilt * Math.PI / 180;
                double cp = Math.Cos(phi), sp = Math.Sin(phi);
                double ax = e.A * cp + e.C * sp;
                double ay = e.B * cp + e.D * sp;
                double cx2 = -e.A * sp + e.C * cp;
                double cy2 = -e.B * sp + e.D * cp;
                // Le clignement s'applique APRES tout ca : c'est un ecrasement vertical a
                // l'ecran, pas le long de l'axe de la gelule.
                double k = BotFace.BlinkScale(Math.Min(lid, cfg.Open));
                eyes.Add(new BotEye
                {
                    Width = cfg.W * R,
                    Height = cfg.H * R,
                    A = ax,
                    B = ay * k,
                    C = cx2,
                    D = cy2 * k,
                    CenterX = e.X * fit + (offX + decalage.X) * R,
                    CenterY = e.Y * fit + (offY + decalage.Y) * R,
                    Alpha = pose.EyeAlpha * BotMath.Clamp(e.Depth / 0.12)
                });
            }
        }

        // --- decor ------------------------------------------------------------
        var dots = new List<BotDot>();
        foreach (var p in pose.Dots)
        {
            if (p.Opacity <= 0.01 || p.R <= 0.0005) continue;
            var d = p.Clone();
            d.X = (p.X + offX) * R;
            d.Y = (p.Y + offY) * R;
            d.R = p.R * R;
            dots.Add(d);
        }

        // la pastille est posee sur le contour : elle suit donc la forme aussi
        (double X, double Y, double R)? notif = null, notch = null;
        if (pose.Notif is { } nsp)
        {
            double nFit = BodyRadius(nsp.X, nsp.Y);
            double nx = (nsp.X * nFit + offX) * R;
            double ny = (nsp.Y * nFit + offY) * R;
            notif = (nx, ny, nsp.R * R);
            notch = (nx, ny, nsp.Notch * R);
        }

        // Les etats declarent des arcs en unites de rayon de boule ; le moteur est le seul
        // a connaitre l'echelle du viewBox, donc c'est lui qui rasterise.
        var arcs = new List<BotArc>();
        foreach (var a in pose.Arcs)
        {
            if (a.Opacity <= 0.01) continue;
            arcs.Add(BotDecor.ArcRender(a.Seed, a.T, R, a.Id, a.Opacity));
        }

        var polar = new (double, double)[BotProfiles.Samples];
        for (int i = 0; i < BotProfiles.Samples; i++)
            polar[i] = ((double)i / BotProfiles.Samples * BotMath.Tau, sil.Radii[i]);

        return new BotFrame
        {
            BodyPoints = bodyPoints,
            BodyPolar = polar,
            BodyRot = sil.Rot,
            BodyCx = sil.Cx,
            BodyCy = sil.Cy,
            BodySx = sil.Sx,
            BodySy = sil.Sy,
            BodyAlpha = pose.BodyAlpha,
            BodyColor = BodyColor,
            Eyes = eyes,
            Dots = dots,
            DotsBehind = pose.DotsBehind,
            Arcs = arcs,
            Notif = notif,
            Notch = notch,
            Scale = R
        };
    }

    /* ------------------------------------------------- convenances statiques */

    /// <summary>Resout un id de forme du personnalisateur en profil (null si inconnu).</summary>
    public static double[]? ResolveShape(string? shapeId) =>
        shapeId != null && BotSkins.ShapeById.TryGetValue(shapeId, out var s) ? s.Radii : null;

    /// <summary>Resout un id d'expression (null si inconnu).</summary>
    public static BotExpression? ResolveExpression(string? expressionId) =>
        expressionId != null && BotExpressions.ExpressionById.TryGetValue(expressionId, out var e) ? e : null;

    /// <summary>Resout une couleur : id de la palette, ou hex "#rrggbb" passe tel quel.</summary>
    public static string ResolveColor(string? colorId)
    {
        if (colorId == null) return BotSkins.DefaultColorHex;
        if (BotSkins.ColorById.TryGetValue(colorId, out var c)) return c.Hex;
        return colorId.StartsWith('#') ? colorId : BotSkins.DefaultColorHex;
    }

    /// <summary>
    /// Echantillonne un etat a une date donnee, comme un moteur neuf pose sur cet etat.
    /// Equivalent a <c>new BotAnimationEngine(...).Sample(t)</c> avec les options resolues.
    /// </summary>
    public static BotFrame Sample(double timeSeconds, string state, BotOptions? options = null)
    {
        var engine = new BotAnimationEngine(
            BotRepere.Rayon, state,
            ResolveShape(options?.Shape),
            ResolveExpression(options?.Expression))
        {
            BodyColor = ResolveColor(options?.Color)
        };
        return engine.Sample(timeSeconds);
    }
}
