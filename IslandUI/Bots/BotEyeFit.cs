using System.Globalization;

namespace IslandUI.Bots;

/// <summary>
/// Ou poser le visage sur une forme du personnalisateur — port de <c>eyefit.ts</c>.
///
/// Les yeux vivent sur une sphere, et <see cref="Shape2D.RadiusAtAngle"/> les recolle au
/// contour reel au prorata du rayon local. Ce prorata place bien leur CENTRE, mais l'oeil a
/// une taille : la marge devant le bord est multipliee par le meme facteur, et une
/// silhouette etroite dans sa direction le pousse contre le bord.
///
/// Le probleme est resolu UNE FOIS, a la premiere utilisation, en une table de decalages
/// (une entree par forme x etat a corps de base x expression). Le moteur ne fait ensuite
/// qu'interpoler entre deux entrees — sur les BORNES de chaque morph, jamais sur la valeur
/// interpolee. Resoudre dans la boucle de rendu reagit a tout ce qui bouge a 60 i/s
/// (derive, pointeur, expression en morph) : sept versions ont ete ecrites ainsi dans
/// l'original et toutes produisaient un artefact de mouvement visible. C'est le meme
/// principe que la pose space deformation (Lewis, Cordner &amp; Fong, SIGGRAPH 2000).
///
/// Tests d'origine (skins.test.ts) : aucune forme ne laisse un oeil sortir de la
/// silhouette (680 combinaisons x 60 instants) ; sur le cercle le decalage est (0, 0) par
/// construction, ce qui protege la reference ; la correction ne change pas la taille des
/// yeux et ne les fait pas osciller.
/// </summary>
public static class BotEyeFit
{
    /// <summary>Rayon de reference du solveur. Le decalage rendu est en unites de ce rayon.</summary>
    private const double R = 100;

    // Amplitudes maximales de la vie au repos : LoopNoise est borne a 1 en valeur absolue,
    // donc ces sommes sont des bornes exactes (5.5+1.6 et 4.2+1.3, cf. BotFace).
    private const double DeriveYaw = 5.5 + 1.6;
    private const double DerivePitch = 4.2 + 1.3;
    private const double DeriveX = 0.006, DeriveY = 0.007;

    /// <summary>Le visage d'une pose, ce dont le solveur a besoin pour placer ses gelules.</summary>
    private readonly record struct Visage(HeadGaze Gaze, double Split, EyeCfg[] Eyes);

    /// <summary>
    /// Une gelule prete a etre mesuree : le segment de son axe, et de quoi calculer le rayon
    /// a degager DANS UNE DIRECTION donnee. Une gelule est un segment epaissi d'un disque de
    /// rayon r ; son image par la matrice tangente est un segment epaissi d'une ellipse, et
    /// le rayon a degager est la fonction d'appui de cette ellipse, r * |A^T u|.
    /// </summary>
    private readonly record struct Empreinte(
        double X, double Y,
        double Ax, double Ay, // demi-vecteur de l'axe
        double R,             // rayon du disque local, avant transformation
        double M0, double M1, double M2, double M3); // colonnes de la matrice tangente

    private readonly record struct Epreuve(Empreinte[] Empreintes, Empreinte[] Reference, Vec2[] Contour, Vec2[] CalContour);

    /// <summary>Flottement du centre au repos, en unites de viewBox, absorbe dans le rayon de la gelule.</summary>
    private static readonly double Flottement = Math.Sqrt(DeriveX * DeriveX + DeriveY * DeriveY) * R;

    private static Empreinte[] Empreintes(Visage visage, Silhouette sil, double[] radii)
    {
        var outp = new List<Empreinte>(2);
        var poses = BotFace.EyePoses(visage.Gaze, R, visage.Split);
        for (int i = 0; i < 2; i++)
        {
            EyePose e = poses[i];
            if (e.Depth <= 0.02) continue;
            EyeCfg cfg = visage.Eyes[i];
            double phi = cfg.Tilt * Math.PI / 180;
            double cp = Math.Cos(phi), sp = Math.Sin(phi);
            double ax = e.A * cp + e.C * sp;
            double ay = e.B * cp + e.D * sp;
            double cx = -e.A * sp + e.C * cp;
            double cy = -e.B * sp + e.D * cp;

            double hw = Math.Max(cfg.W * R, 0.01) / 2;
            double hh = Math.Max(cfg.H * R, 0.01) / 2;
            double r = Math.Min(hw, hh);
            // l'axe est celui de la plus grande dimension
            bool along = hh > hw;
            double demi = along ? hh - r : hw - r;
            // le prorata du rayon local, exactement comme le fait le moteur
            double fit = Shape2D.RadiusAtAngle(radii, Math.Atan2(e.Y, e.X) - sil.Rot);
            outp.Add(new Empreinte(
                e.X * fit, e.Y * fit,
                (along ? cx : ax) * demi, (along ? cy : ay) * demi,
                r, ax, ay, cx, cy));
        }
        return [.. outp];
    }

    /// <summary>
    /// Approche la plus courte entre un contour et un segment : la distance, et le vecteur
    /// unitaire qui va du contour vers le segment — le sens qui degage.
    /// </summary>
    private static (double D, double Ux, double Uy) Approche(Vec2[] pts, double x0, double y0, double x1, double y1)
    {
        double sx = x1 - x0, sy = y1 - y0;
        double len2 = sx * sx + sy * sy;
        double best = double.PositiveInfinity, vx = 0, vy = 0;
        foreach (Vec2 p in pts)
        {
            double t = len2 > 0 ? ((p.X - x0) * sx + (p.Y - y0) * sy) / len2 : 0;
            t = t < 0 ? 0 : t > 1 ? 1 : t;
            double ex = x0 + t * sx - p.X;
            double ey = y0 + t * sy - p.Y;
            double d2 = ex * ex + ey * ey;
            if (d2 < best)
            {
                best = d2;
                vx = ex;
                vy = ey;
            }
        }
        double d = Math.Sqrt(best);
        return (d, d > 1e-9 ? vx / d : 0, d > 1e-9 ? vy / d : 0);
    }

    /// <summary>Marge de la gelule la plus serree, et le sens qui la degage.</summary>
    private static (double Marge, double Ux, double Uy) Pire(Vec2[] pts, Empreinte[] emps, double tx, double ty)
    {
        double marge = double.PositiveInfinity, ux = 0, uy = 0;
        foreach (var e in emps)
        {
            double x = e.X + tx, y = e.Y + ty;
            var a = Approche(pts, x - e.Ax, y - e.Ay, x + e.Ax, y + e.Ay);
            // fonction d'appui de l'ellipse dans la direction de l'approche
            double rayon = e.R * Math.Sqrt(
                Math.Pow(e.M0 * a.Ux + e.M1 * a.Uy, 2) + Math.Pow(e.M2 * a.Ux + e.M3 * a.Uy, 2)) + Flottement;
            if (a.D - rayon < marge)
            {
                marge = a.D - rayon;
                ux = a.Ux;
                uy = a.Uy;
            }
        }
        return (marge, ux, uy);
    }

    /// <summary>Directions sondees et pas de la dichotomie.</summary>
    private const int Directions = 12;
    private const int Dichotomie = 8;

    /// <summary>
    /// Le decalage a poser sur les deux yeux pour cette forme, cet etat et cette expression.
    ///
    /// Une TRANSLATION commune aux deux yeux, donc une isometrie : ecart, tailles et
    /// inclinaisons sont conserves au pixel. La marge visee est celle du profil D'ORIGINE
    /// (sur le cercle l'oeil exterieur frole deja le bord, et c'est voulu), plafonnee par ce
    /// que la forme offre en son centre. Recherche directionnelle : une couronne de
    /// directions est sondee, la distance est dichotomisee le long de chacune — une descente
    /// de gradient ne converge pas (degager d'un bord rapproche de l'autre). Quand rien ne
    /// rentre (wide sur un triangle), on vise le moins pire, on ne renonce pas.
    /// </summary>
    private static Vec2 Resous(List<Epreuve> epreuves)
    {
        if (epreuves.Count == 0) return new Vec2(0, 0);

        double Marge(double tx, double ty)
        {
            double m = double.PositiveInfinity;
            foreach (var ep in epreuves)
                m = Math.Min(m, Pire(ep.Contour, ep.Empreintes, tx, ty).Marge);
            return m;
        }

        // Marge exigee : la plus serree que le profil d'origine tolere, sur toutes les epreuves.
        double requis = double.PositiveInfinity;
        foreach (var ep in epreuves)
            requis = Math.Min(requis, Pire(ep.CalContour, ep.Reference, 0, 0).Marge);

        // La course doit pouvoir atteindre le centre du corps : wide a des gelules de 87
        // unites de long, et sur un triangle elles ne tiennent que vers le milieu.
        double mx = 0, my = 0;
        var emps = epreuves[0].Empreintes;
        foreach (var e in emps)
        {
            mx -= e.X / emps.Length;
            my -= e.Y / emps.Length;
        }
        double course = Math.Max(0.35 * R, Math.Sqrt(mx * mx + my * my) * 1.25);

        // Plafond de la demande : ce que la forme offre en son centre, toujours atteignable.
        requis = Math.Min(requis, Marge(mx, my));

        // Deja bon : le cas du cercle, et de toute forme assez large. La gelule doit RENTRER
        // en plus de n'etre pas plus serree que sur le profil d'origine.
        double depart = Marge(0, 0);
        if (depart >= requis && depart >= 0) return new Vec2(0, 0);
        double cible = Math.Max(requis, 0);

        double meilleurX = 0, meilleurY = 0, meilleureNorme = double.PositiveInfinity;
        // repli quand rien ne rentre : la translation qui degage le plus, sondee au passage
        double secoursX = 0, secoursY = 0, secours = depart;

        for (int d = 0; d < Directions; d++)
        {
            double a = (double)d / Directions * Math.PI * 2;
            double ux = Math.Cos(a), uy = Math.Sin(a);
            if (Marge(ux * course, uy * course) < cible)
            {
                // cette direction ne mene nulle part ; on garde quand meme le meilleur degagement
                foreach (double k in new[] { 0.3, 0.6, 1.0 })
                {
                    double m = Marge(ux * course * k, uy * course * k);
                    if (m > secours)
                    {
                        secours = m;
                        secoursX = ux * course * k;
                        secoursY = uy * course * k;
                    }
                }
                continue;
            }
            // la plus courte distance qui tient, le long de cette direction
            double bas = 0, haut = course;
            for (int i = 0; i < Dichotomie; i++)
            {
                double mid = (bas + haut) / 2;
                if (Marge(ux * mid, uy * mid) >= cible) haut = mid;
                else bas = mid;
            }
            if (haut < meilleureNorme)
            {
                meilleureNorme = haut;
                meilleurX = ux * haut;
                meilleurY = uy * haut;
            }
        }

        double x = double.IsPositiveInfinity(meilleureNorme) ? secoursX : meilleurX;
        double y = double.IsPositiveInfinity(meilleureNorme) ? secoursY : meilleurY;
        // rendu en unites de RAYON DE BOULE : le moteur le remet a son echelle
        return new Vec2(
            Math.Round(x / R, 6, MidpointRounding.AwayFromZero),
            Math.Round(y / R, 6, MidpointRounding.AwayFromZero));
    }

    /// <summary>Le visage a couvrir : celui de l'expression si l'etat l'accepte, le sien sinon.</summary>
    private static Visage VisageDe(StateDef def, Pose pose, BotExpression? expr) =>
        def.BaseFace && expr != null
            ? new Visage(expr.Gaze, expr.Split, expr.Eyes)
            : new Visage(pose.Gaze, pose.Split, pose.Eyes);

    /// <summary>Les dates a echantillonner dans un etat : une seule si sa pose ne bouge pas.</summary>
    private static double[] Dates(StateDef def)
    {
        // Tout ce dont le solveur se sert : si rien ne bouge, une date suffit.
        static string Signature(Pose p) => string.Join(';',
            p.Gaze.Yaw.ToString("R", CultureInfo.InvariantCulture),
            p.Gaze.Pitch.ToString("R", CultureInfo.InvariantCulture),
            p.Gaze.Roll.ToString("R", CultureInfo.InvariantCulture),
            p.Split.ToString("R", CultureInfo.InvariantCulture),
            string.Join(',', p.Eyes.Select(e => $"{e.W:R};{e.H:R};{e.Open:R};{e.Tilt:R}")),
            p.Sil.Rot.ToString("R", CultureInfo.InvariantCulture),
            p.Sil.Cx.ToString("R", CultureInfo.InvariantCulture),
            p.Sil.Cy.ToString("R", CultureInfo.InvariantCulture),
            p.Sil.Sx.ToString("R", CultureInfo.InvariantCulture),
            p.Sil.Sy.ToString("R", CultureInfo.InvariantCulture));
        if (Signature(def.PoseAt(0)) == Signature(def.PoseAt(def.Duration))) return [0];
        const int n = 3;
        var outp = new double[n];
        for (int i = 0; i < n; i++) outp[i] = (double)i / (n - 1) * def.Duration;
        return outp;
    }

    /// <summary>Le decalage d'une forme sur un etat et une expression, derive comprise.</summary>
    private static Vec2 DecalagePour(StateDef def, double[] radii, BotExpression? expr)
    {
        var epreuves = new List<Epreuve>();
        foreach (double t in Dates(def))
        {
            Pose pose = def.PoseAt(t);
            Vec2[] contour = Shape2D.ToPoints(pose.Sil.CloneWith(radii), R);
            Vec2[] calContour = Shape2D.ToPoints(pose.Sil, R);
            Visage v = VisageDe(def, pose, expr);
            // Les quatre coins de la derive bornent la pose nominale, qui est leur centre.
            foreach (double dy in new[] { -DeriveYaw, DeriveYaw })
            foreach (double dp in new[] { -DerivePitch, DerivePitch })
            {
                var c = new Visage(
                    new HeadGaze(v.Gaze.Yaw + dy, v.Gaze.Pitch + dp, v.Gaze.Roll),
                    v.Split, v.Eyes);
                epreuves.Add(new Epreuve(
                    Empreintes(c, pose.Sil, radii),
                    Empreintes(c, pose.Sil, pose.Sil.Radii),
                    contour, calContour));
            }
        }
        return Resous(epreuves);
    }

    private static readonly Vec2 Nul = new(0, 0);

    private static string Clef(string state, string? expr) => $"{state}|{expr ?? ""}";

    /// <summary>
    /// Table des decalages : une entree par (forme, etat a corps de base, expression).
    /// Seuls idle et swirl portent le visage de repos, donc seuls eux se declinent par
    /// expression. Clef par REFERENCE du tableau de rayons, la convention du moteur :
    /// un profil inconnu, ou null, ne corrige rien.
    /// </summary>
    private static List<(double[] Radii, Dictionary<string, Vec2> Par)> Batir()
    {
        var outp = new List<(double[], Dictionary<string, Vec2>)>();
        foreach (var forme in BotSkins.Shapes)
        {
            var par = new Dictionary<string, Vec2>();
            foreach (var def in BotStates.States)
            {
                if (!def.BaseBody) continue;
                var expressions = def.BaseFace
                    ? new List<BotExpression?> { null }.Concat(BotExpressions.Expressions)
                    : [null];
                foreach (var expr in expressions)
                    par[Clef(def.Id, expr?.Id)] = DecalagePour(def, forme.Radii, expr);
            }
            outp.Add((forme.Radii, par));
        }
        return outp;
    }

    // Construite paresseusement, une seule fois, thread-safe (le test d'origine verrouille
    // une construction en moins de 200 ms).
    private static readonly Lazy<List<(double[] Radii, Dictionary<string, Vec2> Par)>> Table =
        new(Batir, System.Threading.LazyThreadSafetyMode.ExecutionAndPublication);

    /// <summary>Force la construction de la table (peut etre appele sur un thread de fond).</summary>
    public static void WarmUp() => _ = Table.Value;

    /// <summary>
    /// Decalage a appliquer aux deux yeux pour cette forme sur cet etat, en unites de rayon
    /// de boule — le moteur le remet a son echelle. Vaut zero des que la forme n'est pas au
    /// catalogue, ce qui couvre null et le cercle : sur le cercle les deux profils sont le
    /// meme, la marge est deja celle exigee, et la reference ne bouge pas.
    /// </summary>
    public static Vec2 EyeOffset(double[]? radii, string state, string? expr)
    {
        if (radii == null) return Nul;
        foreach (var (table, par) in Table.Value)
        {
            if (!ReferenceEquals(table, radii)) continue;
            // un etat sans visage de repos n'a qu'une entree, quelle que soit l'expression
            return par.TryGetValue(Clef(state, expr), out var v) ? v
                : par.TryGetValue(Clef(state, null), out var v2) ? v2
                : Nul;
        }
        return Nul;
    }
}
