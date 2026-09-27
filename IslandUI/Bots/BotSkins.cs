namespace IslandUI.Bots;

/// <summary>Forme du personnalisateur — port de <c>skins.ts</c>.</summary>
public sealed record BotShape(string Id, double[] Radii);

public sealed record BotColor(string Id, string Hex);

/// <summary>
/// Formes et couleurs du personnalisateur. A la difference des silhouettes d'animation
/// (<see cref="BotProfiles"/>), elles sont construites analytiquement, pas relevees sur la
/// video. Une forme choisie ne remplace le corps que sur les etats <c>baseBody</c>
/// (idle, wink, wide, notify, swirl).
///
/// Tests d'origine (shape.test.ts) : chaque forme a exactement 64 echantillons, finis,
/// positifs, min &gt; 0.3, max &lt; 1.6.
/// </summary>
public static class BotSkins
{
    private static double[] Normalize(double[] radii, double max = 1)
    {
        double peak = radii.Max();
        if (peak <= 0) return radii;
        double k = max / peak;
        for (int i = 0; i < radii.Length; i++) radii[i] *= k;
        return radii;
    }

    private static double[] BuildPebble()
    {
        // Galet : cercle deforme par deux harmoniques basses, irregulier mais lisse.
        var radii = new double[BotProfiles.Samples];
        for (int i = 0; i < BotProfiles.Samples; i++)
        {
            double a = (double)i / BotProfiles.Samples * Math.PI * 2;
            radii[i] = 1 + 0.075 * Math.Cos(2 * a + 0.5) + 0.035 * Math.Cos(3 * a + 2.1);
        }
        return Normalize(radii, 1.02);
    }

    private static double[] BuildCloud() => Normalize(
        // Nuage : union de bosses, large en bas, deux lobes en haut.
        Shape2D.UnionOfCirclesProfile(
        [
            (-0.44, 0.2, 0.54),
            (0.46, 0.2, 0.5),
            (0.02, 0.3, 0.6),
            (-0.24, -0.3, 0.48),
            (0.3, -0.24, 0.44)
        ]),
        1.02);

    private static double[] BuildDroplet() => Normalize(
        // Goutte : gros disque en bas, pointe effilee en haut.
        Shape2D.ProfileFromPolygon(Shape2D.HullOfCircles(0, 0.28, 0.66, 0, -0.96, 0.05), 0, 0),
        1.04);

    public static readonly IReadOnlyList<BotShape> Shapes =
    [
        new BotShape("cercle", [.. Enumerable.Repeat(1.0, BotProfiles.Samples)]),
        new BotShape("galet", BuildPebble()),
        // 1.15 et pas 1.02 : sur une superellipse le rayon maximal est la diagonale,
        // normaliser dessus donnerait une forme qui parait plus petite que le cercle.
        new BotShape("squircle", Normalize(Shape2D.SuperellipseProfile(4.2), 1.15)),
        // Capsule couchee : enveloppe de deux disques cote a cote.
        new BotShape("capsule", Shape2D.ProfileFromPolygon(Shape2D.HullOfCircles(-0.42, 0, 0.62, 0.42, 0, 0.62), 0, 0)),
        // -90 deg : un sommet vers le haut de l'ecran (y est oriente vers le bas)
        new BotShape("triangle", Shape2D.RegularPolygonProfile(3, 1.12, 0.34, -90)),
        // 0 deg : sommets a gauche et a droite, aretes du haut et du bas plates
        new BotShape("hexagone", Shape2D.RegularPolygonProfile(6, 1.04, 0.26, 0)),
        new BotShape("nuage", BuildCloud()),
        new BotShape("goutte", BuildDroplet())
    ];

    public static readonly IReadOnlyDictionary<string, BotShape> ShapeById =
        Shapes.ToDictionary(s => s.Id);

    public const string DefaultShape = "cercle";

    /// <summary>Palette du personnalisateur d'origine. <c>encre</c> est le noir du bot, mesure.</summary>
    public static readonly IReadOnlyList<BotColor> Colors =
    [
        new("encre", "#0a0a0c"),
        new("brun", "#8b5e3c"),
        new("rouge", "#e8483f"),
        new("orange", "#f08a24"),
        new("ambre", "#f0b429"),
        new("vert", "#3ecf8e"),
        new("turquoise", "#2fbfa0"),
        new("bleu", "#3b93f0"),
        new("violet", "#8b5cf6"),
        new("rose", "#e152b0"),
        new("gris", "#a3a3a3"),
        new("creme", "#f1efe9")
    ];

    public static readonly IReadOnlyDictionary<string, BotColor> ColorById =
        Colors.ToDictionary(c => c.Id);

    public const string DefaultColor = "encre";
    public const string DefaultColorHex = "#0a0a0c";

    /// <summary>Melange deux couleurs hex. Sert a la brume de profondeur des particules.</summary>
    public static string MixHex(string from, string to, double t)
    {
        static int[] Parse(string h)
        {
            int v = Convert.ToInt32(h[1..], 16);
            return [(v >> 16) & 255, (v >> 8) & 255, v & 255];
        }
        int[] a = Parse(from), b = Parse(to);
        int Chan(int i) => (int)Math.Round(a[i] + (b[i] - a[i]) * t, MidpointRounding.AwayFromZero);
        return $"#{Chan(0):x2}{Chan(1):x2}{Chan(2):x2}";
    }
}
