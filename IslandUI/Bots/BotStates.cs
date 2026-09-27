namespace IslandUI.Bots;

/// <summary>
/// Configuration d'un oeil (gelule) — port de EyeCfg (<c>states.ts</c>).
/// </summary>
public readonly record struct EyeCfg(
    /// <summary>Largeur locale (axe court de la gelule), en unites de rayon de boule.</summary>
    double W,
    /// <summary>Hauteur locale (axe long).</summary>
    double H,
    /// <summary>1 = ouvert, 0 = ferme.</summary>
    double Open = 1,
    /// <summary>
    /// Inclinaison propre de la gelule, en degres, positif = le haut part a droite.
    /// Appliquee APRES le repere tangent de la sphere — c'est elle qui permet les
    /// inclinaisons en miroir (colere, tristesse).
    /// </summary>
    double Tilt = 0);

/// <summary>La pastille de notification : centre pose sur le contour, rayon, rayon de l'encoche.</summary>
public sealed record NotifSpec(double X, double Y, double R, double Notch);

/// <summary>Pose complete d'un etat a un instant — port de Pose (<c>states.ts</c>).</summary>
public sealed class Pose
{
    /// <summary>Silhouette du corps, en unites de rayon de boule.</summary>
    public Silhouette Sil = Shape2D.Circle(1);
    /// <summary>Decalage global du corps ET des yeux.</summary>
    public double OffX, OffY;
    public HeadGaze Gaze;
    /// <summary>Demi-ecart des yeux sur la sphere, en degres.</summary>
    public double Split;
    /// <summary>[oeil interieur, oeil exterieur].</summary>
    public EyeCfg[] Eyes = new EyeCfg[2];
    /// <summary>Opacite des yeux : sert aux etats sans visage.</summary>
    public double EyeAlpha = 1;
    public double BodyAlpha = 1;
    public List<BotDot> Dots = [];
    public List<ArcSpec> Arcs = [];
    public NotifSpec? Notif;
    /// <summary>true = le decor passe derriere le corps (particules de l'eclatement).</summary>
    public bool DotsBehind;

    /// <summary>Copie superficielle : les listes et le tableau de rayons sont partages.</summary>
    public Pose Clone() => (Pose)MemberwiseClone();
}

/// <summary>Definition d'un etat du catalogue — port de StateDef (<c>states.ts</c>).</summary>
public sealed class StateDef
{
    public required string Id;
    /// <summary>Duree de maintien quand la sequence complete est jouee.</summary>
    public required double Duration;
    /// <summary>
    /// Duree sous laquelle l'animation est coupee avant d'aboutir ; lue dans les constantes
    /// de la pose, pas choisie. Absente = l'etat ignore le temps ou boucle.
    /// </summary>
    public double? MinDuration;
    /// <summary>Duree du morph d'entree (ease-out exponentiel, jamais d'overshoot).</summary>
    public required double Morph;
    /// <summary>true = l'entree est masquee par un clignement, comme dans la video.</summary>
    public required bool BlinkIn;
    /// <summary>true = corps remplacable par la forme du personnalisateur.</summary>
    public required bool BaseBody;
    /// <summary>true = visage remplacable par l'expression du personnalisateur (idle, swirl).</summary>
    public required bool BaseFace;
    public required Func<double, Pose> PoseAt;
}

/// <summary>
/// Les 15 etats (14 mesures sur la video + swirl, choisi) — port de <c>states.ts</c>.
/// Toutes les constantes sont des mesures ; ne pas les arrondir.
/// </summary>
public static class BotStates
{
    private static EyeCfg[] Pair(double w, double h) => [new EyeCfg(w, h), new EyeCfg(w, h)];

    /// <summary>Pose de base : boule unite, regard de repos, yeux de repos.</summary>
    private static Pose Base(Action<Pose>? apply = null)
    {
        var p = new Pose
        {
            Sil = Shape2D.Circle(1),
            Gaze = BotFace.RestGaze,
            Split = BotFace.EyeSplit,
            Eyes = Pair(BotFace.EyeW, BotFace.EyeH)
        };
        apply?.Invoke(p);
        return p;
    }

    /* --------------------------------------------------- formes non radiales */

    /// <summary>Centre vertical de la barre du "!" vertical.</summary>
    private const double BarUprightCy = -0.1875;

    /// <summary>
    /// Barre du "!" vertical : enveloppe convexe de deux cercles. Mesure : cercle haut
    /// (0, -0.505) r 0.132, cercle bas (0, +0.130) r 0.075, flancs rectilignes — donc
    /// tronconique (rapport haut/bas 1.76).
    /// </summary>
    private static readonly double[] BarUpright =
        Shape2D.ProfileFromPolygon(Shape2D.HullOfCircles(0, -0.505, 0.132, 0, 0.13, 0.075), 0, BarUprightCy);

    /// <summary>Barre du "!" penche : capsule pure (largeur constante 0.269, longueur 0.776).</summary>
    private static readonly double[] BarItalic =
        Shape2D.ProfileFromPolygon(Shape2D.HullOfCircles(0, -0.2535, 0.1345, 0, 0.2535, 0.1345), 0, 0);

    private static Silhouette SilUpright() => new() { Radii = (double[])BarUpright.Clone(), Cy = BarUprightCy };
    private static Silhouette SilItalic(double rot, double cx, double cy) =>
        new() { Radii = (double[])BarItalic.Clone(), Rot = rot, Cx = cx, Cy = cy };

    /// <summary>
    /// Le point du "!" penche n'est pas un disque : c'est une goutte, bout rond (r 0.118)
    /// du cote de la barre et pointe effilee a l'oppose, longueur 0.300 dans l'axe du glyphe.
    /// </summary>
    public static readonly IReadOnlyList<Vec2> Tear = Shape2D.HullOfCircles(0, 0, 0.118, 0, 0.172, 0.012);

    /// <summary>
    /// Le triangle ne tourne pas sur lui-meme : son centre decrit un cercle de rayon 0.213
    /// autour de l'origine (mesure) — c'est ce qui donne l'impression qu'il bascule.
    /// </summary>
    private const double TriOrbit = 0.213;

    private static Silhouette SpinningTriangle(double rot)
    {
        var s = Shape2D.FromProfile("triangle");
        s.Rot = rot;
        s.Cx = -TriOrbit * Math.Sin(rot);
        s.Cy = TriOrbit * Math.Cos(rot);
        return s;
    }

    /// <summary>Onde de pulsation qui parcourt les trois points de gauche a droite.</summary>
    private static double DotPulse(double t, int index)
    {
        double p = ((t - index * 0.5) / 1.5 % 1 + 1) % 1;
        double k = p < 0.5 ? 0.5 - 0.5 * Math.Cos(p * BotMath.Tau) : 0;
        return BotMath.Clamp(k * 2);
    }

    public static readonly IReadOnlyList<StateDef> States = BuildStates();

    private static List<StateDef> BuildStates() =>
    [
        new StateDef
        {
            Id = "idle", Duration = 2.4, Morph = 0.45,
            BlinkIn = false, BaseBody = true, BaseFace = true,
            PoseAt = _ => Base()
        },

        new StateDef
        {
            Id = "thinking", Duration = 2.6, Morph = 0.4,
            BlinkIn = true, BaseBody = false, BaseFace = false,
            PoseAt = t => Base(p =>
            {
                double mid = DotPulse(t, 1);
                // Les points lateraux sortent des flancs de la boule : dans la video ils
                // restent fusionnes avec elle 1-2 frames avant de se detacher.
                double emerge = 0.3 + 0.7 * BotMath.EaseOutCubic(BotMath.Clamp(t / 0.3));
                foreach (int i in new[] { 0, 2 })
                {
                    double k = DotPulse(t, i);
                    p.Dots.Add(new BotDot
                    {
                        X = BotDecor.DotX[i] * emerge,
                        Y = 0,
                        R = BotDecor.DotR * (1 + (BotDecor.DotPeak - 1) * k),
                        Opacity = 0.55 + 0.45 * k
                    });
                }
                // la boule DEVIENT le point du milieu : le morph reste continu
                p.Sil = Shape2D.Circle(BotDecor.DotR * (1 + (BotDecor.DotPeak - 1) * mid));
                p.Sil.Cx = BotDecor.DotX[1];
                p.EyeAlpha = 0;
            })
        },

        new StateDef
        {
            Id = "wink", Duration = 1.6, Morph = 0.3,
            BlinkIn = true, BaseBody = true, BaseFace = false,
            PoseAt = _ => Base(p =>
            {
                p.Gaze = new HeadGaze(-5.37, 4.55, 6.7);
                p.Split = 16.25;
                // L'oeil ferme n'est pas l'oeil ouvert ecrase : c'est un tiret horizontal
                // PLUS LARGE que l'oeil ouvert (0.447 contre 0.236).
                p.Eyes = [new EyeCfg(0.236, 0.464), new EyeCfg(0.447, 0.089)];
            })
        },

        new StateDef
        {
            Id = "wide", Duration = 1.8, Morph = 0.55,
            BlinkIn = true, BaseBody = true, BaseFace = false,
            PoseAt = _ => Base(p =>
            {
                p.Gaze = new HeadGaze(6.92, -21.96, 11.6);
                p.Split = 18.43;
                p.Eyes = Pair(0.356, 0.875);
            })
        },

        new StateDef
        {
            Id = "alert", Duration = 2.4, MinDuration = 2, // le "!" revient en place a 1.6 + 0.4
            Morph = 0.45, BlinkIn = false, BaseBody = false, BaseFace = false,
            PoseAt = t => Base(p =>
            {
                // Course mesuree : -0.087 -> +0.732 en 1.5 s, ease-in-out, micro-overshoot.
                double k = BotMath.Clamp(t / 1.5);
                double travel = BotMath.EaseInOutCubic(k) * 0.82 - 0.087;
                double back = t > 1.6 ? BotMath.Clamp((t - 1.6) / 0.4) : 0;
                double x = travel * (1 - back) + 0.1 * back;
                // Vibration secondaire a 2.5 Hz, barre et point en opposition de phase.
                double buzz = Math.Sin(t * 2.5 * BotMath.Tau) * 0.005;
                double tilt = 17.7 * Math.PI / 180;
                p.Sil = SilItalic(tilt, x, -0.325 - buzz);
                p.EyeAlpha = 0;
                p.Dots.Add(new BotDot
                {
                    // le point suit l'axe du glyphe, a 0.580 du centre de la barre
                    X = x - Math.Sin(tilt) * 0.58,
                    Y = -0.325 + Math.Cos(tilt) * 0.58 + buzz * 2.8,
                    R = 0.118,
                    Polygon = Tear,
                    Rot = tilt * 180 / Math.PI,
                    Opacity = 1
                });
            })
        },

        new StateDef
        {
            Id = "notify", Duration = 2.2, Morph = 0.5,
            BlinkIn = true, BaseBody = true, BaseFace = false,
            PoseAt = t => Base(p =>
            {
                // Pop du point bleu : pic a +14 % vers 0.3 s puis stabilisation.
                double k = BotMath.Clamp(t / 0.45);
                double pop = 1 + (BotDecor.NotifPop - 1) * Math.Sin(k * Math.PI) * (1 - k * 0.35);
                double r = BotDecor.NotifR * (k < 1 ? pop : 1);
                double a = BotDecor.NotifAngle * Math.PI / 180;
                // le regard part a l'oppose de la pastille
                p.Gaze = new HeadGaze(-21.94, -5.82, -12.2);
                p.Split = 18.89;
                p.Eyes = Pair(0.505, 0.498);
                p.Notif = new NotifSpec(
                    Math.Cos(a) * BotDecor.NotifDist,
                    Math.Sin(a) * BotDecor.NotifDist,
                    r,
                    r + BotDecor.NotifMargin);
            })
        },

        new StateDef
        {
            Id = "exclaim", Duration = 2, Morph = 0.45,
            BlinkIn = false, BaseBody = false, BaseFace = false,
            PoseAt = _ => Base(p =>
            {
                p.Sil = SilUpright();
                p.EyeAlpha = 0;
                p.Dots.Add(new BotDot { X = -0.012, Y = 0.526, R = 0.113, Opacity = 1 });
            })
        },

        new StateDef
        {
            Id = "sleep", Duration = 2.4, Morph = 0.5,
            BlinkIn = false, BaseBody = false, BaseFace = false,
            PoseAt = t => Base(p =>
            {
                // Rebond vertical mesure : +-0.19 autour de +0.11, periode 0.6 s.
                p.Sil = Shape2D.Circle(0.1585);
                p.Sil.Cy = 0.11 + Math.Sin(t * (BotMath.Tau / 0.6)) * 0.19;
                p.EyeAlpha = 0;
            })
        },

        new StateDef
        {
            Id = "egg", Duration = 1.8, Morph = 0.4,
            BlinkIn = true, BaseBody = false, BaseFace = false,
            PoseAt = _ => Base(p =>
            {
                p.Sil = Shape2D.FromProfile("egg");
                p.Gaze = new HeadGaze(19.97, 26.01, -17.1);
                // les yeux se resserrent comme le corps
                p.Split = 11.07;
                p.Eyes = Pair(0.164, 0.385);
            })
        },

        new StateDef
        {
            Id = "hexagon", Duration = 1.6, Morph = 0.4,
            BlinkIn = true, BaseBody = false, BaseFace = false,
            PoseAt = _ => Base(p =>
            {
                p.Sil = Shape2D.FromProfile("hexagon");
                p.Gaze = new HeadGaze(23.11, 24.42, -13.3);
                p.Split = 13.37;
                p.Eyes = Pair(0.177, 0.411);
            })
        },

        new StateDef
        {
            Id = "play", Duration = 2, Morph = 0.5,
            BlinkIn = true, BaseBody = false, BaseFace = false,
            PoseAt = t => Base(p =>
            {
                // Le triangle reste quasi immobile pendant que le bouquet le traverse.
                double fade = BotMath.Clamp(t / 0.35) * BotMath.Clamp((2.2 - t) / 0.5);
                p.Sil = SpinningTriangle(0);
                p.Gaze = new HeadGaze(12, -8, -6);
                p.Split = 15;
                p.Eyes = Pair(0.18, 0.34);
                // le bouquet balaie de la droite vers la gauche par-dessus le triangle
                for (int i = 0; i < BotDecor.Swoosh.Length; i++)
                {
                    var seed = BotDecor.Swoosh[i].Clone();
                    seed.Cx = 0.45 - t * 0.42;
                    p.Arcs.Add(new ArcSpec { Id = $"sw{i}", Seed = seed, T = t, Opacity = fade });
                }
            })
        },

        new StateDef
        {
            Id = "orbit", Duration = 3.4, MinDuration = 2.5, // le corps s'est relache a 1.6 + 0.9
            Morph = 0.6, BlinkIn = false, BaseBody = false, BaseFace = false,
            PoseAt = t => Base(p =>
            {
                // Rotation mesuree : rampe sur 0.35 s puis 1.25 tour/s (sens antihoraire).
                double ramp = BotMath.EaseInOutCubic(BotMath.Clamp(t / 0.35));
                double rot = -BotMath.Tau * 1.25 * t * ramp;
                // Le corps se relache du triangle vers la boule pendant l'orbite.
                double back = BotMath.EaseInOutCubic(BotMath.Clamp((t - 1.6) / 0.9));
                var tri = SpinningTriangle(rot);
                p.Sil = new Silhouette
                {
                    Radii = new double[BotProfiles.Samples],
                    Rot = rot,
                    Cx = tri.Cx * (1 - back),
                    Cy = tri.Cy * (1 - back)
                };
                for (int i = 0; i < BotProfiles.Samples; i++)
                    p.Sil.Radii[i] = tri.Radii[i] + (1 - tri.Radii[i]) * back;
                double fade = BotMath.Clamp(t / 0.8) * BotMath.Clamp((3.6 - t) / 0.9);
                for (int i = 0; i < BotDecor.Rings.Length; i++)
                {
                    // les anneaux entrent un par un sur 0.8 s
                    p.Arcs.Add(new ArcSpec
                    {
                        Id = $"rg{i}",
                        Seed = BotDecor.Rings[i],
                        T = t,
                        Opacity = fade * BotMath.Clamp((t - i * 0.13) / 0.3)
                    });
                }
                // les yeux filent autour de la sphere ~3x plus vite que la silhouette
                p.Gaze = new HeadGaze(
                    BotFace.RestGaze.Yaw + Math.Sin(t * 6.5) * 65 * (1 - back),
                    -4 + back * 32,
                    -13);
                p.Eyes = Pair(0.18, 0.34 + back * 0.07);
            })
        },

        // swirl : SEUL etat qui n'est pas releve sur la video — il est CHOISI (transition
        // d'entree des reglages). Il emprunte les anneaux d'orbit mais coupe court : 1.3 s au
        // lieu de 3.4, la moitie des anneaux, aucun triangle. Volontairement HORS de Sequence.
        new StateDef
        {
            Id = "swirl", Duration = 1.3, MinDuration = 1.3, // un peu plus que le tour du regard (1.1 s)
            Morph = 0.3, BlinkIn = true, BaseBody = true, BaseFace = true,
            PoseAt = t => Base(p =>
            {
                for (int i = 0; i < 3; i++)
                {
                    // ils entrent l'un apres l'autre puis s'effacent avant la fin du bloc
                    p.Arcs.Add(new ArcSpec
                    {
                        Id = $"sw{i}",
                        Seed = BotDecor.Rings[i],
                        T = t,
                        Opacity = BotMath.Clamp((t - i * 0.06) / 0.14) * BotMath.Clamp((1.22 - t) / 0.34)
                    });
                }
            })
        },

        new StateDef
        {
            Id = "burst", Duration = 2.6, MinDuration = 2.4, // le corps est recompose a 1.7 + 0.7
            Morph = 0.4, BlinkIn = false, BaseBody = false, BaseFace = false,
            PoseAt = t => Base(p =>
            {
                // Effondrement mesure : 1.0 -> 0.166 en 0.7 s, ease-out, sans rebond.
                double collapse = 1 - 0.834 * BotMath.EaseOutQuint(BotMath.Clamp(t / 0.7));
                double regrow = BotMath.EaseOutQuint(BotMath.Clamp((t - 1.7) / 0.7));
                p.Sil = Shape2D.Circle(collapse + (1 - collapse) * regrow);
                p.EyeAlpha = BotMath.Clamp((t - 1.85) / 0.4);
                p.Dots = BotDecor.Particles(t, 1);
                p.DotsBehind = true;
            })
        },

        new StateDef
        {
            // le point se recompose a 1.85 + 0.6 = 2.45, soit 0.05 s apres la coupe de la
            // video : ce reliquat se termine pendant le fondu suivant, comme dans la reference.
            Id = "comet", Duration = 2.4, MinDuration = 2.4,
            Morph = 0.45, BlinkIn = false, BaseBody = false, BaseFace = false,
            PoseAt = t => Base(p =>
            {
                double collapse = 1 - (1 - BotDecor.CometDot) * BotMath.EaseOutQuint(BotMath.Clamp(t / 0.55));
                double regrow = BotMath.EaseOutQuint(BotMath.Clamp((t - 1.85) / 0.6));
                double fade = BotMath.Clamp((t - 0.15) / 0.25) * BotMath.Clamp((1.95 - t) / 0.3);
                // Le point derive de 0.035 vers le bas puis remonte (wobble mesure).
                p.Sil = Shape2D.Circle(collapse + (1 - collapse) * regrow);
                p.Sil.Cy = Math.Sin(BotMath.Clamp(t / 1.7) * Math.PI) * 0.035;
                p.EyeAlpha = BotMath.Clamp((t - 2) / 0.35);
                for (int i = 0; i < BotDecor.CometRibbons.Length; i++)
                    p.Arcs.Add(new ArcSpec { Id = $"cm{i}", Seed = BotDecor.CometRibbons[i], T = t, Opacity = fade });
            })
        }
    ];

    public static readonly IReadOnlyDictionary<string, StateDef> ById =
        States.ToDictionary(s => s.Id);

    /// <summary>
    /// Date, en temps local, ou chaque etat est le plus lisible (vignettes, planche).
    /// </summary>
    public static readonly IReadOnlyDictionary<string, double> Poses = new Dictionary<string, double>
    {
        ["idle"] = 1,
        ["thinking"] = 1.1,
        ["wink"] = 0.8,
        ["wide"] = 0.8,
        ["alert"] = 0.75,
        ["notify"] = 0.9,
        ["exclaim"] = 0.8,
        ["sleep"] = 0.45,
        ["egg"] = 0.8,
        ["hexagon"] = 0.8,
        ["play"] = 0.9,
        ["orbit"] = 1.2,
        ["swirl"] = 0.5,
        ["burst"] = 0.45,
        ["comet"] = 1.15
    };

    /// <summary>
    /// Ordre de lecture de la sequence complete, calque sur la video de reference.
    /// Test d'origine (engine.test.ts) : exactement ces 14 etats, dans cet ordre — swirl en
    /// est exclu (transition d'interface, pas une animation du catalogue).
    /// </summary>
    public static readonly string[] Sequence =
    [
        "idle", "thinking", "wink", "wide", "alert", "notify", "exclaim",
        "sleep", "egg", "hexagon", "play", "orbit", "burst", "comet"
    ];
}
