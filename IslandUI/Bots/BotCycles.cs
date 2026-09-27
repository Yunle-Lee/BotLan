using System.Text.Json;

namespace IslandUI.Bots;

/// <summary>Un bloc de montage : un etat tenu pendant une duree choisie.</summary>
public sealed record BotBlock(string State, double Duration);

/// <summary>Un cycle est un montage : une suite de blocs. Donnees pures, aucune horloge.</summary>
public sealed record BotCycle(string Id, string Name, List<BotBlock> Blocks);

/// <summary>
/// Cycles / montages — port de <c>cycles.ts</c>. Un cycle etire un bloc en laissant l'etat
/// tourner plus longtemps et le raccourcit en coupant ; il ne multiplie jamais le temps
/// local par un facteur de vitesse, ce qui casserait toutes les durees mesurees.
///
/// Tests d'origine (cycles.test.ts) : MinDurationOf("alert") == 2, MinDurationOf("burst")
/// == 2.4, ClampDuration("orbit", 1) == 2.5, ClampDuration("idle", 999) == MaxBlock,
/// ClampDuration("idle", 2.44) == 2.4 et 2.46 == 2.5 ; BlockAt boucle au-dela du dernier
/// bloc ; ParseCycles rejette les etats hors Sequence (swirl compris) et borne les montages
/// relus a MaxBlocks blocs / MaxCycles cycles.
/// </summary>
public static class BotCycles
{
    /// <summary>
    /// Plancher commun a tous les blocs : le moteur ne garde qu'une case d'historique, donc
    /// un bloc plus court que le fondu d'entree du suivant sauterait au lieu de se fondre.
    /// DERIVE du catalogue (le plus long morph, celui d'orbit = 0.6), pas ecrit a la main.
    /// </summary>
    public static readonly double MinBlock = BotStates.States.Max(s => s.Morph);

    /// <summary>Garde-fou d'editeur, pas une mesure.</summary>
    public const double MaxBlock = 10;

    /// <summary>
    /// Bornes contre un stockage hostile : un seul cycle de 150 000 blocs (~4 Mo de JSON)
    /// donnait 1 500 000 s de duree et une piste de 29 700 000 px — l'onglet figeait.
    /// 200 blocs font une demi-heure de montage, largement au-dela de tout usage.
    /// </summary>
    public const int MaxBlocks = 200;
    public const int MaxCycles = 50;

    /// <summary>Pas de la molette et du redimensionnement, en secondes.</summary>
    public const double Step = 0.1;

    public const string DefaultCycleId = "defaut";

    /// <summary>Duree minimale d'un bloc : le plancher moteur, ou la mesure de l'etat.</summary>
    public static double MinDurationOf(string state) =>
        Math.Max(MinBlock, BotStates.ById.TryGetValue(state, out var def) ? def.MinDuration ?? MinBlock : MinBlock);

    /// <summary>Ramene une duree dans ses bornes et sur le pas, sans trainee de flottants.</summary>
    public static double ClampDuration(string state, double seconds)
    {
        double snapped = Math.Round(seconds / Step) * Step;
        double bounded = Math.Min(MaxBlock, Math.Max(MinDurationOf(state), snapped));
        return Math.Round(bounded * 100) / 100;
    }

    /// <summary>La duree de reference est celle relevee sur la video pour cet etat.</summary>
    public static BotBlock MakeBlock(string state) =>
        new(state, ClampDuration(state, BotStates.ById.TryGetValue(state, out var def) ? def.Duration : 2));

    /// <summary>
    /// Le montage releve sur la video : l'ordre de Sequence, chaque etat tenu sa duree
    /// mesuree. Nom vide = "jamais nomme par l'utilisateur", donc affiche dans la langue
    /// courante.
    /// </summary>
    public static BotCycle DefaultCycle() =>
        new(DefaultCycleId, "", [.. BotStates.Sequence.Select(MakeBlock)]);

    public static double TotalDuration(IReadOnlyList<BotBlock> blocks) => blocks.Sum(b => b.Duration);

    /// <summary>Date de debut d'un bloc dans le montage.</summary>
    public static double OffsetOf(IReadOnlyList<BotBlock> blocks, int index)
    {
        double acc = 0;
        for (int i = 0; i < index && i < blocks.Count; i++) acc += blocks[i].Duration;
        return acc;
    }

    /// <summary>
    /// Bloc joue a la date <paramref name="t"/> et temps ecoule dedans. Au-dela du dernier
    /// bloc on retombe au debut : la lecture boucle.
    /// </summary>
    public static (int Index, double Elapsed) BlockAt(IReadOnlyList<BotBlock> blocks, double t)
    {
        double total = TotalDuration(blocks);
        if (blocks.Count == 0 || total <= 0) return (0, 0);
        // le modulo n'est applique que s'il sert : sur une date deja dans le cycle il
        // n'ajouterait qu'une trainee de flottants au temps ecoule
        double wrapped = t >= 0 && t < total ? t : (t % total + total) % total;
        double acc = 0;
        for (int i = 0; i < blocks.Count; i++)
        {
            double end = acc + blocks[i].Duration;
            if (wrapped < end) return (i, wrapped - acc);
            acc = end;
        }
        return (blocks.Count - 1, 0);
    }

    /// <summary>Ajoute un bloc a la fin du montage, plafonne a MaxBlocks.</summary>
    public static List<BotBlock> BlocksWith(IReadOnlyList<BotBlock> blocks, string state)
    {
        if (blocks.Count >= MaxBlocks) return [.. blocks];
        return [.. blocks, MakeBlock(state)];
    }

    /// <summary>Deplace un bloc, en rendant une nouvelle liste.</summary>
    public static List<BotBlock> MoveBlock(IReadOnlyList<BotBlock> blocks, int from, int to)
    {
        var next = new List<BotBlock>(blocks);
        if (from < 0 || from >= next.Count) return [.. blocks];
        var moved = next[from];
        next.RemoveAt(from);
        next.Insert(Math.Min(Math.Max(to, 0), next.Count), moved);
        return next;
    }

    /// <summary>"Mon cycle", "Mon cycle 2", ... — jamais deux fois le meme nom.</summary>
    public static string UniqueName(string baseName, IReadOnlyList<BotCycle> cycles)
    {
        var taken = cycles.Select(c => c.Name).ToHashSet();
        if (!taken.Contains(baseName)) return baseName;
        int n = 2;
        while (taken.Contains($"{baseName} {n}")) n++;
        return $"{baseName} {n}";
    }

    /// <summary>Identifiant sans collision.</summary>
    public static string NextCycleId(IReadOnlyList<BotCycle> cycles)
    {
        var taken = cycles.Select(c => c.Id).ToHashSet();
        int n = 1;
        while (taken.Contains($"c{n}")) n++;
        return $"c{n}";
    }

    /* ------------------------------------------------------- lecture du stockage */

    private static BotBlock? ParseBlock(JsonElement raw)
    {
        if (raw.ValueKind != JsonValueKind.Object) return null;
        if (!raw.TryGetProperty("state", out var st) || st.ValueKind != JsonValueKind.String) return null;
        string? state = st.GetString();
        // Valide contre Sequence et non contre ById : swirl est deliberement hors du
        // catalogue (transition d'entree des reglages) et n'a rien a faire dans un montage.
        if (state == null || !BotStates.Sequence.Contains(state)) return null;
        if (!raw.TryGetProperty("duration", out var du) || du.ValueKind != JsonValueKind.Number) return null;
        double duration = du.GetDouble();
        if (!double.IsFinite(duration)) return null;
        return new BotBlock(state, ClampDuration(state, duration));
    }

    private static BotCycle? ParseCycle(JsonElement raw, List<BotCycle> seen)
    {
        if (raw.ValueKind != JsonValueKind.Object) return null;
        if (!raw.TryGetProperty("id", out var idEl) || idEl.ValueKind != JsonValueKind.String) return null;
        string id = idEl.GetString() ?? "";
        if (id.Length == 0) return null;
        // le nom peut etre vide — c'est le montage d'amorce, qui suit la langue
        if (!raw.TryGetProperty("name", out var nameEl) || nameEl.ValueKind != JsonValueKind.String) return null;
        if (!raw.TryGetProperty("blocks", out var blocksEl) || blocksEl.ValueKind != JsonValueKind.Array) return null;
        // on tronque AVANT de relire : valider 150 000 blocs pour n'en garder que 200 serait
        // faire le travail qu'on cherche justement a eviter
        var kept = new List<BotBlock>();
        foreach (var item in blocksEl.EnumerateArray())
        {
            if (kept.Count >= MaxBlocks) break;
            if (ParseBlock(item) is { } b) kept.Add(b);
        }
        if (kept.Count == 0) return null;
        if (seen.Any(c => c.Id == id)) return null;
        return new BotCycle(id, nameEl.GetString() ?? "", kept);
    }

    /// <summary>
    /// Le stockage est modifiable a la main : on ne lui fait pas confiance. Tout ce qui ne
    /// se relit pas est jete silencieusement plutot que de casser l'application au demarrage.
    /// </summary>
    public static List<BotCycle> ParseCycles(string? raw)
    {
        var outp = new List<BotCycle>();
        if (string.IsNullOrEmpty(raw)) return outp;
        JsonDocument doc;
        try
        {
            doc = JsonDocument.Parse(raw);
        }
        catch (JsonException)
        {
            return outp;
        }
        using (doc)
        {
            if (doc.RootElement.ValueKind != JsonValueKind.Array) return outp;
            foreach (var item in doc.RootElement.EnumerateArray())
            {
                if (outp.Count >= MaxCycles) break;
                if (ParseCycle(item, outp) is { } cycle) outp.Add(cycle);
            }
        }
        return outp;
    }
}
