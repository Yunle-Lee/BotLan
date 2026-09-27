namespace IslandUI.Bots;

/// <summary>Expression de repos du bot — port de BotExpression (<c>expressions.ts</c>).</summary>
public sealed class BotExpression
{
    public required string Id;
    public required HeadGaze Gaze;
    public required double Split;
    public required EyeCfg[] Eyes;
}

/// <summary>
/// Le catalogue des expressions — port de <c>expressions.ts</c>.
///
/// Le visage ne tient qu'a deux gelules : tout se joue sur l'orientation de la tete,
/// l'ecart des yeux, leurs proportions, et l'inclinaison propre de chaque oeil. Seuls les
/// etats <c>baseFace</c> (idle, swirl) portent cette expression ; les etats expressifs de la
/// video gardent la leur.
///
/// Test d'origine (expressions.test.ts) : 16 expressions, identifiants uniques ; un oeil
/// incline de 20 deg ou plus doit avoir un rapport w/h hors de [0.6, 1.7] (hors de
/// [0.8, 1.25] en dessous), sinon il est trop rond pour que l'inclinaison se voie.
/// </summary>
public static class BotExpressions
{
    private static EyeCfg Eye(double w, double h, double tilt = 0, double open = 1) => new(w, h, open, tilt);

    /// <summary>Les deux yeux identiques, inclinaisons en miroir si tilt est fourni.</summary>
    private static EyeCfg[] Pair(double w, double h, double tilt = 0, double open = 1) =>
        [Eye(w, h, tilt, open), Eye(w, h, -tilt, open)];

    public static readonly IReadOnlyList<BotExpression> Expressions =
    [
        // la pose relevee image par image sur la video de reference
        new BotExpression
        {
            Id = "neutre", Gaze = BotFace.RestGaze, Split = BotFace.EyeSplit,
            Eyes = Pair(BotFace.EyeW, BotFace.EyeH)
        },
        new BotExpression { Id = "attentif", Gaze = new HeadGaze(4, 5, -4), Split = 16, Eyes = Pair(0.21, 0.44) },
        new BotExpression { Id = "surpris", Gaze = new HeadGaze(3, -3, 0), Split = 19, Eyes = Pair(0.45, 0.47) },
        new BotExpression { Id = "excite", Gaze = new HeadGaze(6, -14, 0), Split = 19.5, Eyes = Pair(0.4, 0.56, -10) },
        // yeux plisses en arc : les hauts convergent legerement
        new BotExpression { Id = "heureux", Gaze = new HeadGaze(5, 9, 0), Split = 17, Eyes = Pair(0.27, 0.17, 14) },
        new BotExpression { Id = "hilare", Gaze = new HeadGaze(4, 14, 0), Split = 18, Eyes = Pair(0.34, 0.13, 20) },
        // hauts des yeux qui convergent fort vers le centre + yeux etrecis
        new BotExpression { Id = "colere", Gaze = new HeadGaze(3, 7, 0), Split = 17, Eyes = Pair(0.34, 0.15, 30) },
        // l'inverse : les hauts divergent, et le regard tombe
        new BotExpression { Id = "triste", Gaze = new HeadGaze(3, -13, 0), Split = 16, Eyes = Pair(0.22, 0.4, -28) },
        new BotExpression { Id = "effraye", Gaze = new HeadGaze(2, -20, 0), Split = 20.5, Eyes = Pair(0.4, 0.6) },
        // un oeil franchement plus ferme que l'autre
        new BotExpression
        {
            Id = "mefiant", Gaze = new HeadGaze(12, 6, -6), Split = 16,
            Eyes = [Eye(0.21, 0.4), Eye(0.22, 0.15)]
        },
        // asymetrique sur les deux axes : tailles ET inclinaisons depareillees ; l'oeil
        // plisse est volontairement plat (rapport 1,6) pour que l'inclinaison se voie
        new BotExpression
        {
            Id = "confus", Gaze = new HeadGaze(-14, 3, 8), Split = 16.5,
            Eyes = [Eye(0.2, 0.44, -18), Eye(0.28, 0.17, 14)]
        },
        // la tete penche : c'est le roulis qui porte la curiosite
        new BotExpression
        {
            Id = "curieux", Gaze = new HeadGaze(16, -9, -15), Split = 16.5,
            Eyes = [Eye(0.24, 0.46, -8), Eye(0.2, 0.38, -8)]
        },
        new BotExpression { Id = "fier", Gaze = new HeadGaze(5, 17, 0), Split = 17, Eyes = Pair(0.3, 0.15, 18) },
        new BotExpression { Id = "timide", Gaze = new HeadGaze(-19, -14, -7), Split = 14, Eyes = Pair(0.17, 0.3) },
        // fentes horizontales et regard qui part sur le cote
        new BotExpression { Id = "blase", Gaze = new HeadGaze(-22, 2, 0), Split = 16, Eyes = Pair(0.3, 0.12) },
        // paupieres a moitie tombees : on passe par Open, le meme mecanisme que le clignement
        new BotExpression { Id = "somnolent", Gaze = new HeadGaze(6, -9, -3), Split = 16, Eyes = Pair(0.2, 0.42, 0, 0.42) }
    ];

    public static readonly IReadOnlyDictionary<string, BotExpression> ExpressionById =
        Expressions.ToDictionary(e => e.Id);

    public const string DefaultExpression = "neutre";

    private static EyeCfg LerpEyeCfg(EyeCfg a, EyeCfg b, double t) => new(
        BotMath.Lerp(a.W, b.W, t),
        BotMath.Lerp(a.H, b.H, t),
        BotMath.Lerp(a.Open, b.Open, t),
        BotMath.Lerp(a.Tilt, b.Tilt, t));

    /// <summary>Interpolation de deux expressions : le changement se fait en glissant.</summary>
    public static BotExpression BlendExpression(BotExpression a, BotExpression b, double t) => new()
    {
        Id = b.Id,
        Gaze = new HeadGaze(
            BotMath.Lerp(a.Gaze.Yaw, b.Gaze.Yaw, t),
            BotMath.Lerp(a.Gaze.Pitch, b.Gaze.Pitch, t),
            BotMath.Lerp(a.Gaze.Roll, b.Gaze.Roll, t)),
        Split = BotMath.Lerp(a.Split, b.Split, t),
        Eyes = [LerpEyeCfg(a.Eyes[0], b.Eyes[0], t), LerpEyeCfg(a.Eyes[1], b.Eyes[1], t)]
    };
}
