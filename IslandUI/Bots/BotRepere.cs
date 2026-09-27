namespace IslandUI.Bots;

/// <summary>
/// Le repere de tout ce que le moteur rend. Attribution: THIRD_PARTY_NOTICES.md.
/// </summary>
public static class BotRepere
{
    /// <summary>
    /// Rayon de la boule au repos, en unites de viewBox (RAYON = 100). Choisi et non mesure :
    /// tout le reste s'exprime en fractions de ce rayon.
    /// </summary>
    public const double Rayon = 100;

    /// <summary>
    /// Demi-cote du viewBox affiche (DEMI_VIEWBOX = 158). La marge au-dela du rayon loge les
    /// anneaux : ceux de l'orbite et le swoosh montent a 1,4 fois le rayon, soit 140.
    /// </summary>
    public const double DemiViewBox = 158;
}
