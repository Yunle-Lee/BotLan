namespace IslandUI.Bots;

/// <summary>
/// Utilitaires numeriques. Attribution: THIRD_PARTY_NOTICES.md.
/// </summary>
public static class BotMath
{
    public const double Tau = Math.PI * 2;

    public static double Clamp(double v, double lo = 0, double hi = 1) => v < lo ? lo : v > hi ? hi : v;

    public static double Lerp(double a, double b, double t) => a + (b - a) * t;

    // Mesure sur la video : les transitions sont des ease-out exponentiels, sans
    // depassement du corps. Les seuls effets de ressort sont locaux (le pop de la
    // pastille de notification, l'ouverture des yeux) et ecrits dans l'etat concerne.
    public static double EaseOutCubic(double t)
    {
        double u = 1 - t;
        return 1 - u * u * u;
    }

    public static double EaseInOutCubic(double t) =>
        t < 0.5 ? 4 * t * t * t : 1 - Math.Pow(-2 * t + 2, 3) / 2;

    public static double EaseOutQuint(double t)
    {
        double u = 1 - t;
        return 1 - u * u * u * u * u;
    }

    /// <summary>Bruit 1D periodique : boucle sans couture sur <paramref name="period"/>.</summary>
    public static double LoopNoise(double t, double period, double seed = 0)
    {
        double p = t / period * Tau;
        return
            0.55 * Math.Sin(p + seed) +
            0.3 * Math.Sin(2 * p + seed * 1.7 + 1.1) +
            0.15 * Math.Sin(3 * p + seed * 2.3 + 2.4);
    }

    /// <summary>
    /// PRNG deterministe (mulberry32) : meme sequence qu'en JavaScript a seed egal
    /// (verrouille le calendrier des clignements, les anneaux, les particules).
    /// </summary>
    public static Func<double> CreateRng(uint seed)
    {
        uint a = seed;
        return () =>
        {
            unchecked
            {
                a += 0x6d2b79f5u;
                uint t = (a ^ (a >> 15)) * (1u | a);
                t = (t + (t ^ (t >> 7)) * (61u | t)) ^ t;
                return (t ^ (t >> 14)) / 4294967296.0;
            }
        };
    }
}
