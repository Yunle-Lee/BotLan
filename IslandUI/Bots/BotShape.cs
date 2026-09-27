namespace IslandUI.Bots;

/// <summary>Point 2D en unites de viewBox (x a droite, y vers le bas).</summary>
public readonly record struct Vec2(double X, double Y);

/// <summary>
/// Une silhouette = un profil radial r(theta) plus une pose — port de <c>shape.ts</c>.
/// Tous les profils sont echantillonnes aux MEMES angles (<see cref="BotProfiles.Samples"/>),
/// donc deux formes ont des points qui se correspondent un a un et le morphing se reduit a
/// une interpolation lineaire des rayons.
/// </summary>
public sealed class Silhouette
{
    public double[] Radii = [];
    /// <summary>Rotation du profil, en radians.</summary>
    public double Rot;
    /// <summary>Decalage du centre, en unites de rayon de boule.</summary>
    public double Cx, Cy;
    /// <summary>Squash &amp; stretch, applique en repere ecran (apres rotation).</summary>
    public double Sx = 1, Sy = 1;

    /// <summary>Copie superficielle : le tableau de rayons est partage (jamais mute en place).</summary>
    public Silhouette Clone() => (Silhouette)MemberwiseClone();

    public Silhouette CloneWith(double[] radii)
    {
        var c = Clone();
        c.Radii = radii;
        return c;
    }
}

public static class Shape2D
{
    private static readonly double[] Cos;
    private static readonly double[] Sin;

    static Shape2D()
    {
        Cos = new double[BotProfiles.Samples];
        Sin = new double[BotProfiles.Samples];
        for (int i = 0; i < BotProfiles.Samples; i++)
        {
            double a = (double)i / BotProfiles.Samples * BotMath.Tau;
            Cos[i] = Math.Cos(a);
            Sin[i] = Math.Sin(a);
        }
    }

    /// <summary>Silhouette issue d'un profil mesure (egg, hexagon, triangle).</summary>
    public static Silhouette FromProfile(string name)
    {
        var s = new Silhouette { Radii = BotProfiles.Profile(name) };
        return s;
    }

    /// <summary>Cercle parfait : base neutre (point, bulle, cible de fondu).</summary>
    public static Silhouette Circle(double radius)
    {
        var radii = new double[BotProfiles.Samples];
        Array.Fill(radii, radius);
        return new Silhouette { Radii = radii };
    }

    /// <summary>Interpolation de deux silhouettes ; alloue une silhouette neuve.</summary>
    public static Silhouette Blend(Silhouette a, Silhouette b, double t)
    {
        var dst = new Silhouette { Radii = new double[BotProfiles.Samples] };
        for (int i = 0; i < BotProfiles.Samples; i++)
        {
            double ra = i < a.Radii.Length ? a.Radii[i] : 1;
            double rb = i < b.Radii.Length ? b.Radii[i] : 1;
            dst.Radii[i] = BotMath.Lerp(ra, rb, t);
        }
        // Rotation par le chemin le plus court : evite un tour complet entre +170deg et -170deg.
        double dRot = b.Rot - a.Rot;
        while (dRot > Math.PI) dRot -= BotMath.Tau;
        while (dRot < -Math.PI) dRot += BotMath.Tau;
        dst.Rot = a.Rot + dRot * t;
        dst.Cx = BotMath.Lerp(a.Cx, b.Cx, t);
        dst.Cy = BotMath.Lerp(a.Cy, b.Cy, t);
        dst.Sx = BotMath.Lerp(a.Sx, b.Sx, t);
        dst.Sy = BotMath.Lerp(a.Sy, b.Sy, t);
        return dst;
    }

    /// <summary>Projette la silhouette en points ecran. <paramref name="scale"/> = rayon de la boule en unites de viewBox.</summary>
    public static Vec2[] ToPoints(Silhouette s, double scale)
    {
        double cr = Math.Cos(s.Rot), sr = Math.Sin(s.Rot);
        var outp = new Vec2[BotProfiles.Samples];
        for (int i = 0; i < BotProfiles.Samples; i++)
        {
            double r = i < s.Radii.Length ? s.Radii[i] : 1;
            double x = r * Cos[i];
            double y = r * Sin[i];
            // rotation puis squash en repere ecran, puis translation
            double rx = x * cr - y * sr;
            double ry = x * sr + y * cr;
            outp[i] = new Vec2((rx * s.Sx + s.Cx) * scale, (ry * s.Sy + s.Cy) * scale);
        }
        return outp;
    }

    /// <summary>
    /// Segments cubiques Catmull-Rom (tangentes centrees, tension 1/6) pour une polyligne
    /// fermee : c'est le <c>closedPath</c> de l'original, en geometrie plutot qu'en chaine SVG.
    /// Avec 64 points le contour est lisse au pixel pres meme affiche en 600 px.
    /// </summary>
    public static List<(Vec2 C1, Vec2 C2, Vec2 P)> ClosedCubics(IReadOnlyList<Vec2> pts, double tension = 1.0 / 6)
    {
        int n = pts.Count;
        var outp = new List<(Vec2, Vec2, Vec2)>(n);
        for (int i = 0; i < n; i++)
        {
            Vec2 p0 = pts[(i - 1 + n) % n];
            Vec2 p1 = pts[i];
            Vec2 p2 = pts[(i + 1) % n];
            Vec2 p3 = pts[(i + 2) % n];
            var c1 = new Vec2(p1.X + (p2.X - p0.X) * tension, p1.Y + (p2.Y - p0.Y) * tension);
            var c2 = new Vec2(p2.X - (p3.X - p1.X) * tension, p2.Y - (p3.Y - p1.Y) * tension);
            outp.Add((c1, c2, p2));
        }
        return outp;
    }

    /// <summary>
    /// Polygone quelconque -&gt; profil radial, par lancer de rayon depuis (cx, cy).
    /// Calcule une seule fois au chargement, jamais dans la boucle de rendu.
    /// </summary>
    public static double[] ProfileFromPolygon(IReadOnlyList<Vec2> poly, double cx, double cy)
    {
        var radii = new double[BotProfiles.Samples];
        int n = poly.Count;
        for (int k = 0; k < BotProfiles.Samples; k++)
        {
            double dx = Cos[k], dy = Sin[k];
            double best = 0;
            for (int i = 0; i < n; i++)
            {
                Vec2 a = poly[i];
                Vec2 b = poly[(i + 1) % n];
                double ex = b.X - a.X, ey = b.Y - a.Y;
                double den = dx * ey - dy * ex;
                if (Math.Abs(den) < 1e-9) continue;
                double px = a.X - cx, py = a.Y - cy;
                double t = (px * ey - py * ex) / den; // distance le long du rayon
                double u = (px * dy - py * dx) / den; // position sur le segment
                if (t > best && u >= 0 && u <= 1) best = t;
            }
            radii[k] = best;
        }
        return radii;
    }

    /// <summary>Enveloppe convexe de deux cercles : la barre tronconique du "!" vertical.</summary>
    public static List<Vec2> HullOfCircles(double x1, double y1, double r1, double x2, double y2, double r2, int steps = 96)
    {
        double dx = x2 - x1, dy = y2 - y1;
        double dist = Math.Sqrt(dx * dx + dy * dy);
        if (dist == 0) dist = 1e-6;
        // angle des tangentes externes communes
        double baseAngle = Math.Atan2(dy, dx);
        double spread = Math.Acos(Math.Max(-1, Math.Min(1, (r1 - r2) / dist)));
        var pts = new List<Vec2>();
        // arc du grand cercle
        for (int i = 0; i <= steps / 2; i++)
        {
            double a = baseAngle + spread + (BotMath.Tau - 2 * spread) * i / (steps / 2);
            pts.Add(new Vec2(x1 + Math.Cos(a) * r1, y1 + Math.Sin(a) * r1));
        }
        // arc du petit cercle
        for (int i = 0; i <= steps / 2; i++)
        {
            double a = baseAngle - spread + (2 * spread) * i / (steps / 2);
            pts.Add(new Vec2(x2 + Math.Cos(a) * r2, y2 + Math.Sin(a) * r2));
        }
        return pts;
    }

    /// <summary>
    /// Rayon du profil dans une direction quelconque, par interpolation entre les deux
    /// echantillons voisins. Recolle sur le contour reel ce qui est pose "sur" le corps
    /// (les yeux, la pastille de notification).
    ///
    /// Tests d'origine (shape.test.ts) : enroule les angles negatifs ET les multi-tours
    /// (orbit pousse rot jusqu'a environ -30 rad — le double modulo est obligatoire), et
    /// est continue au passage par zero (radiusAtAngle(triangle, 0) == triangle[0]).
    /// </summary>
    public static double RadiusAtAngle(double[] radii, double angle)
    {
        int n = radii.Length;
        double t = ((angle / BotMath.Tau % 1 + 1) % 1) * n;
        int i = (int)Math.Floor(t);
        return BotMath.Lerp(radii[i % n], radii[(i + 1) % n], t - i);
    }

    /// <summary>Superellipse : |x/sx|^n + |y/sy|^n = 1. n = 2 ellipse, n ~ 4 squircle.</summary>
    public static double[] SuperellipseProfile(double n, double sx = 1, double sy = 1)
    {
        var outp = new double[BotProfiles.Samples];
        for (int i = 0; i < BotProfiles.Samples; i++)
        {
            double c = Math.Pow(Math.Abs(Cos[i] / sx), n);
            double s = Math.Pow(Math.Abs(Sin[i] / sy), n);
            outp[i] = Math.Pow(c + s, -1 / n);
        }
        return outp;
    }

    /// <summary>
    /// Profil radial de l'UNION de disques : r(theta) = la plus lointaine des intersections
    /// rayon/cercle. Exact tant que l'origine est dans l'union.
    /// </summary>
    public static double[] UnionOfCirclesProfile(IReadOnlyList<(double X, double Y, double R)> circles)
    {
        var outp = new double[BotProfiles.Samples];
        for (int i = 0; i < BotProfiles.Samples; i++)
        {
            double dx = Cos[i], dy = Sin[i];
            double best = 0;
            foreach (var c in circles)
            {
                double b = dx * c.X + dy * c.Y;
                double disc = b * b - (c.X * c.X + c.Y * c.Y - c.R * c.R);
                if (disc < 0) continue;
                double t = b + Math.Sqrt(disc);
                if (t > best) best = t;
            }
            outp[i] = best;
        }
        return outp;
    }

    /// <summary>
    /// Polygone a coins arrondis, par somme de Minkowski avec un disque : chaque arete est
    /// poussee de <paramref name="rc"/> vers l'exterieur, chaque sommet devient un arc.
    /// Attend un polygone en sens horaire (repere ecran, y vers le bas).
    /// </summary>
    private static List<Vec2> RoundedPolygon(IReadOnlyList<Vec2> verts, double rc, int arcSteps = 10)
    {
        int n = verts.Count;
        var outp = new List<Vec2>();
        double Normal(Vec2 a, Vec2 b)
        {
            double dx = b.X - a.X, dy = b.Y - a.Y;
            double len = Math.Sqrt(dx * dx + dy * dy);
            if (len == 0) len = 1;
            // sens horaire + y vers le bas : la normale sortante est (dy, -dx)
            return Math.Atan2(-dx / len, dy / len);
        }
        for (int i = 0; i < n; i++)
        {
            Vec2 prev = verts[(i - 1 + n) % n];
            Vec2 cur = verts[i];
            Vec2 next = verts[(i + 1) % n];
            double a0 = Normal(prev, cur);
            double a1 = Normal(cur, next);
            double d = a1 - a0;
            while (d > Math.PI) d -= BotMath.Tau;
            while (d < -Math.PI) d += BotMath.Tau;
            for (int k = 0; k <= arcSteps; k++)
            {
                double a = a0 + d * k / arcSteps;
                outp.Add(new Vec2(cur.X + Math.Cos(a) * rc, cur.Y + Math.Sin(a) * rc));
            }
        }
        return outp;
    }

    /// <summary>Polygone regulier a coins arrondis, inscrit dans <paramref name="radius"/>.</summary>
    public static double[] RegularPolygonProfile(int sides, double radius, double rc, double rotationDeg = 0)
    {
        double rot = rotationDeg * Math.PI / 180;
        var verts = new Vec2[sides];
        for (int i = 0; i < sides; i++)
        {
            // sens horaire a l'ecran : theta croissant avec y vers le bas
            double a = rot + (double)i / sides * BotMath.Tau;
            verts[i] = new Vec2(Math.Cos(a) * (radius - rc), Math.Sin(a) * (radius - rc));
        }
        return ProfileFromPolygon(RoundedPolygon(verts, rc), 0, 0);
    }
}
