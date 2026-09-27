namespace IslandUI.Bots;

/// <summary>Orientation de tete : lacet (yaw, + = a droite), tangage (pitch, + = en haut), roulis (roll), en degres.</summary>
public readonly record struct HeadGaze(double Yaw, double Pitch, double Roll);

/// <summary>
/// Repere d'un oeil projete : centre (x, y), matrice tangente 2x2 [a b c d] au sens SVG
/// matrix(a,b,c,d,e,f), et composante z de la normale (depth &gt; 0 = face visible).
/// </summary>
public readonly record struct EyePose(double X, double Y, double A, double B, double C, double D, double Depth);

/// <summary>
/// Vie au repos : derive lente du regard, clignements — port de <c>face.ts</c>.
/// Fonction pure du temps (aucun etat interne) : pause, reprise et saut a une date
/// arbitraire donnent toujours la meme image.
/// </summary>
public static class BotFace
{
    /// <summary>Demi-ecart des yeux sur la sphere, en degres (separation totale ~31 deg).</summary>
    public const double EyeSplit = 15.46;

    /// <summary>Taille de l'oeil au repos, en unites de rayon de boule.</summary>
    public const double EyeW = 0.186;
    public const double EyeH = 0.412;

    /// <summary>Orientation de tete au repos, ajustee sur les frames de reference (residuel ~1 px sur une boule de 190 px).</summary>
    public static readonly HeadGaze RestGaze = new(28.49, 28.62, -13);

    private static double Deg(double d) => d * Math.PI / 180;

    /// <summary>Fait tourner deux vecteurs d'un repere orthonorme dans leur plan commun.</summary>
    private static ((double, double, double), (double, double, double)) Spin(
        (double X, double Y, double Z) u, (double X, double Y, double Z) v, double angle)
    {
        double c = Math.Cos(angle), s = Math.Sin(angle);
        return (
            (u.X * c + v.X * s, u.Y * c + v.Y * s, u.Z * c + v.Z * s),
            (v.X * c - u.X * s, v.Y * c - u.Y * s, v.Z * c - u.Z * s)
        );
    }

    /// <summary>
    /// Repere de la tete puis des deux yeux. Repere ecran : x a droite, y vers le bas,
    /// z vers le spectateur. L'indice 0 est l'oeil interieur, l'indice 1 l'oeil exterieur.
    ///
    /// Tests d'origine (face.test.ts) — poses mesurees sur la video, reproduites a 0.04 rayon :
    ///   repos        : yeux (0.189, -0.412) et (0.614, -0.51)
    ///   ecarquilles  : (-0.198, 0.295) et (0.412, 0.415)
    ///   notification : (-0.675, 0.172) et (-0.059, 0.027)
    /// Invariants : det(tangente) == depth (aire = courbure, mesure 0.663) ; la separation
    /// angulaire reste 2 * EyeSplit quel que soit le regard ; a yaw = 80 l'oeil exterieur
    /// passe derriere la sphere (depth &lt; 0).
    /// </summary>
    public static EyePose[] EyePoses(HeadGaze gaze, double scale, double split = EyeSplit)
    {
        var f = (0.0, 0.0, 1.0);
        var right = (1.0, 0.0, 0.0);
        var down = (0.0, 1.0, 0.0);

        (f, right) = Spin(f, right, Deg(gaze.Yaw));   // lacet : forward bascule vers right
        (down, f) = Spin(down, f, Deg(gaze.Pitch));   // tangage : forward bascule vers le haut
        (right, down) = Spin(right, down, Deg(gaze.Roll)); // roulis dans le plan de la tete

        EyePose Build(double side)
        {
            var (ef, er) = Spin(f, right, Deg(split * side));
            return new EyePose(ef.Item1 * scale, ef.Item2 * scale, er.Item1, er.Item2, down.Item1, down.Item2, ef.Item3);
        }

        return [Build(-1), Build(1)];
    }

    /// <summary>Ecarts a ajouter a la pose de l'etat courant.</summary>
    public readonly record struct Liveliness(
        double DYaw, double DPitch, double DRoll,
        /// <summary>1 = oeil ouvert, 0 = ferme (ecrasement vertical en repere ecran).</summary>
        double Lid,
        double DriftX, double DriftY, double Breath);

    private static readonly Func<double> BlinkRng = BotMath.CreateRng(0x5eed);

    /// <summary>Calendrier de clignements pre-tire : deterministe et sans etat.</summary>
    private static readonly double[] Blinks = BuildBlinks();

    private static double[] BuildBlinks()
    {
        var outp = new List<double>();
        double t = 1.4;
        while (t < 900)
        {
            outp.Add(t);
            // 1.9 a 4.6 s entre deux clignements, plus un double clignement parfois
            t += 1.9 + BlinkRng() * 2.7;
            if (BlinkRng() < 0.18)
            {
                outp.Add(t);
                t += 0.24;
            }
        }
        return [.. outp];
    }

    /// <summary>Mesure : 1 a 2 frames a 10 fps.</summary>
    public const double BlinkDuration = 0.18;

    private static double BlinkLid(double t)
    {
        foreach (double start in Blinks)
        {
            if (t < start) break;
            double k = (t - start) / BlinkDuration;
            if (k is >= 0 and <= 1)
            {
                // fermeture rapide, reouverture un peu plus lente
                return k < 0.45 ? 1 - k / 0.45 : (k - 0.45) / 0.55;
            }
        }
        return 1;
    }

    /// <summary>
    /// Periodes premieres entre elles : la derive ne se repete jamais a l'oeil.
    /// Au repos la video est quasiment immobile (centre stable a +-0.003) : toute la vie
    /// passe par le regard et les clignements ; on garde juste un flottement de quelques
    /// millièmes de rayon et une respiration de 0.5 % pour ne pas figer l'image.
    /// </summary>
    public static Liveliness LivelinessAt(double t, double wander = 1, bool blink = true, bool @float = true) => new(
        DYaw: (BotMath.LoopNoise(t, 11.3, 0.4) * 5.5 + BotMath.LoopNoise(t, 3.7, 2.1) * 1.6) * wander,
        DPitch: (BotMath.LoopNoise(t, 9.1, 1.3) * 4.2 + BotMath.LoopNoise(t, 4.3, 0.7) * 1.3) * wander,
        DRoll: BotMath.LoopNoise(t, 13.7, 3.2) * 2.2 * wander,
        Lid: blink ? BlinkLid(t) : 1,
        DriftX: @float ? BotMath.LoopNoise(t, 7.9, 1.9) * 0.006 : 0,
        DriftY: @float ? BotMath.LoopNoise(t, 5.3, 0.3) * 0.007 : 0,
        // la largeur est constante, seule la hauteur respire tres legerement
        Breath: @float ? 1 + Math.Sin(t / 3.4 * Math.PI * 2) * 0.005 : 1);

    /// <summary>
    /// Le clignement est un ecrasement VERTICAL en repere ecran autour du centre de l'oeil
    /// (mesure : largeur de bbox conservee, hauteur a ~0.35), compose apres la matrice
    /// tangente, pas le long de l'axe incline de la gelule.
    /// </summary>
    public static double BlinkScale(double lid) => 0.06 + 0.94 * BotMath.Clamp(lid);
}
