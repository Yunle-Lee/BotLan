namespace IslandUI.Bots;

/// <summary>Un point de decor (points "...", particules, point du "!") — port de DotRender.</summary>
public sealed class BotDot
{
    public double X, Y, R, Opacity;
    /// <summary>Couleur explicite ; par defaut le rendu prend celle du corps.</summary>
    public string? Color;
    /// <summary>Brume de profondeur : 0 = fondu dans le fond, 1 = couleur du corps pleine.</summary>
    public double? Depth;
    /// <summary>
    /// Forme non circulaire, en unites de rayon de boule, centree sur l'origine (le point du
    /// "!" penche est une goutte, pas un disque). Quand elle est fournie, R n'est plus utilise.
    /// </summary>
    public IReadOnlyList<Vec2>? Polygon;
    /// <summary>Rotation appliquee a <see cref="Polygon"/>, en degres.</summary>
    public double Rot;

    public BotDot Clone() => (BotDot)MemberwiseClone();
}

/// <summary>
/// Ce qu'un etat declare : la geometrie de l'arc reste en unites de rayon de boule,
/// c'est le moteur (seul a connaitre l'echelle du viewBox) qui la rasterise.
/// </summary>
public sealed class ArcSeed
{
    /// <summary>Demi-grand axe, en unites de rayon de boule.</summary>
    public double A;
    /// <summary>Aplatissement b/a : mesure &lt;= 0.45, les plans d'orbite sont vus par la tranche.</summary>
    public double K;
    /// <summary>Inclinaison du grand axe a l'ecran, radians.</summary>
    public double Tilt;
    /// <summary>Tours par seconde.</summary>
    public double Speed;
    public double Phase;
    /// <summary>Fraction du tour reellement tracee.</summary>
    public double Sweep;
    public double Hue, HueSpan, Width, Cx, Cy;

    public ArcSeed Clone() => (ArcSeed)MemberwiseClone();
}

/// <summary>Arc declare par un etat, a un temps local t.</summary>
public sealed class ArcSpec
{
    public string Id = "";
    public ArcSeed Seed = new();
    public double T, Opacity;

    public ArcSpec Clone() => (ArcSpec)MemberwiseClone();
}

/// <summary>
/// Arc rasterise : polylignes avant/arriere (la composante z coupe l'arc en deux, la moitie
/// arriere est dessinee avant le corps donc occultee par lui), epaisseur, opacite, degrade.
/// </summary>
public sealed class BotArc
{
    public string Id = "";
    public List<List<Vec2>> Front = [];
    public List<List<Vec2>> Back = [];
    public double Width, Opacity;
    public double GradX1, GradY1, GradX2, GradY2;
    public string[] GradStops = [];
}

/// <summary>Decor : anneaux, swoosh, points, particules, comete, pastille — port de <c>decor.ts</c>.</summary>
public static class BotDecor
{
    /// <summary>
    /// Les anneaux ne sont pas des couleurs plates : la video montre une roue de teintes a
    /// luminosite constante, avec un degrade le long de chaque trace (S 45-62 %, L 50-67 %).
    /// </summary>
    public static string Wheel(double hue, double s = 0.55, double l = 0.62)
    {
        double h = (hue % 360 + 360) % 360;
        double c = (1 - Math.Abs(2 * l - 1)) * s;
        double x = c * (1 - Math.Abs(h / 60 % 2 - 1));
        double m = l - c / 2;
        double r, g, b;
        if (h < 60) (r, g, b) = (c, x, 0);
        else if (h < 120) (r, g, b) = (x, c, 0);
        else if (h < 180) (r, g, b) = (0, c, x);
        else if (h < 240) (r, g, b) = (0, x, c);
        else if (h < 300) (r, g, b) = (x, 0, c);
        else (r, g, b) = (c, 0, x);
        int Hex(double v) => (int)Math.Round((v + m) * 255, MidpointRounding.AwayFromZero);
        return $"#{Hex(r):x2}{Hex(g):x2}{Hex(b):x2}";
    }

    /// <summary>
    /// Projette un cercle 3D incline en orthographique : u = (cos tilt, sin tilt, 0) dans
    /// l'ecran, v plonge dans la profondeur ; z &lt; 0 coupe l'arc en arriere/avant.
    /// </summary>
    public static BotArc ArcRender(ArcSeed seed, double t, double scale, string id, double opacity = 1)
    {
        double spin = seed.Phase + t * seed.Speed * BotMath.Tau;
        double cu = Math.Cos(seed.Tilt), su = Math.Sin(seed.Tilt);
        double kz = Math.Sqrt(Math.Max(0, 1 - seed.K * seed.K));

        const int n = 64;
        double span = seed.Sweep * BotMath.Tau;
        var front = new List<List<Vec2>>();
        var back = new List<List<Vec2>>();
        List<Vec2>? current = null;
        bool? prev = null;

        for (int i = 0; i <= n; i++)
        {
            double th = spin + (double)i / n * span;
            double ct = Math.Cos(th), st = Math.Sin(th);
            double x = seed.A * (ct * cu + st * -su * seed.K) + seed.Cx;
            double y = seed.A * (ct * su + st * cu * seed.K) + seed.Cy;
            double z = seed.A * st * kz;

            bool behind = z < 0;
            if (prev == null || behind != prev.Value)
            {
                current = [];
                (behind ? back : front).Add(current);
            }
            current!.Add(new Vec2(x * scale, y * scale));
            prev = behind;
        }

        double gx = Math.Cos(seed.Tilt) * seed.A * scale;
        double gy = Math.Sin(seed.Tilt) * seed.A * scale;
        return new BotArc
        {
            Id = id,
            Front = front,
            Back = back,
            Width = seed.Width * scale,
            Opacity = opacity,
            GradX1 = seed.Cx * scale - gx,
            GradY1 = seed.Cy * scale - gy,
            GradX2 = seed.Cx * scale + gx,
            GradY2 = seed.Cy * scale + gy,
            GradStops = [Wheel(seed.Hue), Wheel(seed.Hue + seed.HueSpan * 0.5), Wheel(seed.Hue + seed.HueSpan)]
        };
    }

    /* ------------------------------------------------------------------ anneaux */

    /// <summary>
    /// 6 anneaux, demi-grand axe 1.30-1.40 (nettement plus grands que la boule),
    /// aplatissement toujours &lt;= 0.45, epaisseur 0.055, ~3.3 tours/s.
    /// </summary>
    public static readonly ArcSeed[] Rings = BuildRings();

    private static ArcSeed[] BuildRings()
    {
        var rng = BotMath.CreateRng(0xa11ce);
        var outp = new ArcSeed[6];
        for (int i = 0; i < 6; i++)
        {
            outp[i] = new ArcSeed
            {
                A = 1.3 + rng() * 0.1,
                K = 0.05 + rng() * 0.4,
                Tilt = i / 6.0 * Math.PI + rng() * 0.5,
                Speed = 3 + rng() * 0.7,
                Phase = rng() * BotMath.Tau,
                Sweep = 0.6 + rng() * 0.25,
                Hue = i * 360 / 6.0 + rng() * 30,
                HueSpan = 60 + rng() * 60,
                Width = 0.05 + rng() * 0.012,
                Cx = 0,
                Cy = 0.1
            };
        }
        return outp;
    }

    /// <summary>
    /// Bouquet d'arcs emboites qui balaie le triangle juste avant les orbites.
    /// Vus quasiment par la tranche (forme en epingle a cheveux), rmax 1.37.
    /// </summary>
    public static readonly ArcSeed[] Swoosh = BuildSwoosh();

    private static ArcSeed[] BuildSwoosh()
    {
        var outp = new ArcSeed[4];
        for (int i = 0; i < 4; i++)
        {
            outp[i] = new ArcSeed
            {
                A = 0.78 + i * 0.2,
                K = 0.05 + i * 0.02,
                Tilt = -0.62 + i * 0.05,
                Speed = 0.3,
                Phase = 0.06 * i,
                Sweep = 0.4,
                Hue = 95 + i * 62,
                HueSpan = 100,
                Width = 0.05,
                Cx = 0,
                Cy = -0.12
            };
        }
        return outp;
    }

    /* ------------------------------------------------------------- 3 points */

    /// <summary>x mesures : -0.557 / -0.013 / +0.532, y = 0.</summary>
    public static readonly double[] DotX = [-0.557, -0.013, 0.532];
    public const double DotR = 0.165;
    public const double DotPeak = 1.25;

    /* ------------------------------------------------------------ particules */

    private static readonly (double Birth, double Angle, double Rho)[] ParticlesSeed = BuildParticlesSeed();

    private static (double, double, double)[] BuildParticlesSeed()
    {
        var rng = BotMath.CreateRng(0xbeef);
        var outp = new (double, double, double)[5];
        for (int i = 0; i < 5; i++)
            outp[i] = (i * 0.2, rng() * BotMath.Tau, 0.58 + rng() * 0.18);
        return outp;
    }

    /// <summary>
    /// Les particules ne partent pas en ligne droite : elles spiralent vers le centre
    /// (rayon x0.75 par frame, angle +100 deg/s) en grossissant, et passent derriere le
    /// noyau ou elles sont avalees. 5 particules, une nouvelle toutes les 0.2 s,
    /// duree de vie 0.55 s.
    /// </summary>
    public static List<BotDot> Particles(double t, double scale)
    {
        var outp = new List<BotDot>();
        foreach (var p in ParticlesSeed)
        {
            double u = t - p.Birth;
            if (u < 0 || u > 0.62) continue;
            double rho = p.Rho * Math.Pow(0.75, u * 10);
            double a = p.Angle + u * 100 * Math.PI / 180;
            outp.Add(new BotDot
            {
                X = Math.Cos(a) * rho * scale,
                Y = Math.Sin(a) * rho * scale,
                R = (0.04 + 0.028 * BotMath.Clamp(u / 0.55)) * scale,
                Depth = BotMath.Clamp(1 - rho / 0.8),
                Opacity = BotMath.Clamp(u / 0.06) * BotMath.Clamp((0.62 - u) / 0.08)
            });
        }
        return outp;
    }

    /* ------------------------------------------------------------------ comete */

    /// <summary>
    /// Contrairement a l'intuition, le point ne traverse pas l'ecran : il reste au centre
    /// et c'est la trainee qui l'orbite. Ellipse a = 0.85, b = 0.15, grand axe incline de
    /// +34 deg, 4 rubans, ~210 deg/s.
    /// </summary>
    public static readonly ArcSeed[] CometRibbons = BuildCometRibbons();

    private static ArcSeed[] BuildCometRibbons()
    {
        var rng = BotMath.CreateRng(0xc0e7);
        var outp = new ArcSeed[4];
        for (int i = 0; i < 4; i++)
        {
            double d = i - 1.5;
            outp[i] = new ArcSeed
            {
                A = 0.85 * (1 + d * 0.03),
                // meme aplatissement a +-5 % pres : les rubans forment un faisceau serre
                K = 0.15 / 0.85 * (1 + d * 0.16),
                Tilt = 34 * Math.PI / 180 + d * 0.035,
                Speed = 210.0 / 360,
                // dephasage mesure : 10 a 20 degres entre rubans, pas davantage
                Phase = -i * 0.045 + rng() * 0.012,
                Sweep = 0.34,
                Hue = i * 85 + rng() * 20,
                HueSpan = 80,
                Width = 0.095,
                Cx = 0,
                Cy = 0
            };
        }
        return outp;
    }

    /// <summary>Rayon du point de la comete, mesure a 0.129.</summary>
    public const double CometDot = 0.129;

    /* --------------------------------------------------- pastille notification */

    /// <summary>Bleu releve au pixel.</summary>
    public const string NotifBlue = "#2496e8";
    /// <summary>La pastille est posee exactement sur la circonference, a -42 deg.</summary>
    public const double NotifAngle = -42;
    public const double NotifDist = 1.003;
    /// <summary>Rayon au repos ; le pop culmine 14 % au-dessus.</summary>
    public const double NotifR = 0.15;
    public const double NotifPop = 1.14;
    /// <summary>
    /// L'encoche est un disque concentrique a la pastille, soustrait du corps.
    /// La marge est constante (0.054 R) et suit l'echelle du corps.
    /// Test d'origine (engine.test.ts) : (notch.r - notif.r) / 100 == 0.054.
    /// </summary>
    public const double NotifMargin = 0.054;
}
