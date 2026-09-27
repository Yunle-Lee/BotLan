using IslandUI.Bots;

int failures = 0;
void Check(bool ok, string name, string detail = "")
{
    Console.WriteLine($"{(ok ? "PASS" : "FAIL")}  {name}{(ok ? "" : "  -- " + detail)}");
    if (!ok) failures++;
}

(double W, double H) Footprint(IReadOnlyList<Vec2> pts)
{
    double x0 = pts.Min(p => p.X), x1 = pts.Max(p => p.X);
    double y0 = pts.Min(p => p.Y), y1 = pts.Max(p => p.Y);
    return ((x1 - x0) / 100, (y1 - y0) / 100);
}

// --- empreintes mesurees (engine.test.ts) ----------------------------------
(string Id, double T, double W, double H, double Tol)[] empreintes =
[
    ("idle", 0.5, 2.0, 2.0, 0.05),
    ("egg", 0.9, 1.653, 2.0, 0.06),
    ("hexagon", 0.9, 1.82, 2.01, 0.07),
    ("exclaim", 0.9, 0.263, 0.842, 0.03),
    ("alert", 0.8, 0.421, 0.753, 0.04),
    ("sleep", 0.6, 0.317, 0.317, 0.03),
    ("comet", 1.0, 0.258, 0.258, 0.03),
];
foreach (var (id, t, w, h, tol) in empreintes)
{
    var f = new BotAnimationEngine(100, id).Sample(t);
    var (gw, gh) = Footprint(f.BodyPoints);
    Check(Math.Abs(gw - w) < tol && Math.Abs(gh - h) < tol, $"empreinte {id}",
        $"got {gw:F3} x {gh:F3}, attendu {w} x {h} +- {tol}");
}

{
    var (w, h) = Footprint(new BotAnimationEngine(100, "play").Sample(0.9).BodyPoints);
    Check(w > h && Math.Abs(w - 1.99) < 0.08, "triangle plus large que haut", $"{w:F3} x {h:F3}");
}

// --- purete du temps --------------------------------------------------------
{
    var a = new BotAnimationEngine(100, "orbit");
    var b = new BotAnimationEngine(100, "orbit");
    Check(a.Sample(1.3).BodyPoints.SequenceEqual(b.Sample(1.3).BodyPoints), "deux moteurs, meme image");
    var first = a.Sample(0.7).BodyPoints;
    a.Sample(2.5);
    Check(a.Sample(0.7).BodyPoints.SequenceEqual(first), "relecture d'une date passee");

    var e = new BotAnimationEngine(100, "idle");
    e.SetState("egg", 1);
    var pendant = e.Sample(1.2).BodyPoints;
    e.Sample(3);
    Check(e.Sample(1.2).BodyPoints.SequenceEqual(pendant), "rejouable pendant un fondu");

    var e2 = new BotAnimationEngine(100, "idle");
    var avant = e2.Sample(0.5).BodyPoints;
    e2.SetState("egg", 1);
    Check(e2.Sample(0.5).BodyPoints.SequenceEqual(avant), "date anterieure au changement");
}

// --- morph de transition sans saut -----------------------------------------
{
    var e = new BotAnimationEngine(100, "idle");
    e.SetState("egg", 1);
    var largeurs = new[] { 1, 1.1, 1.2, 1.3, 1.4 }.Select(t => Footprint(e.Sample(t).BodyPoints).W).ToArray();
    bool decroissant = true;
    for (int i = 1; i < largeurs.Length; i++) if (largeurs[i] >= largeurs[i - 1]) decroissant = false;
    Check(decroissant && Math.Abs(largeurs[0] - 2) < 0.05 && Math.Abs(Footprint(e.Sample(2).BodyPoints).W - 1.65) < 0.05,
        "morph idle -> egg strictement decroissant", string.Join(", ", largeurs.Select(x => x.ToString("F3"))));
}

// --- jamais hors du viewBox --------------------------------------------------
{
    bool ok = true;
    foreach (var s in BotStates.States)
    {
        var e = new BotAnimationEngine(100, s.Id);
        foreach (var t in new[] { 0.2, 0.9, 1.8, 3.0 })
            foreach (var p in e.Sample(t).BodyPoints)
                if (Math.Abs(p.X) >= 158 || Math.Abs(p.Y) >= 158) { ok = false; break; }
    }
    Check(ok, "le corps ne depasse jamais le viewBox");
}

// --- visages -----------------------------------------------------------------
{
    string[] avec = ["idle", "wink", "wide", "notify", "egg", "hexagon"];
    string[] sans = ["thinking", "alert", "exclaim", "sleep"];
    Check(avec.All(id => new BotAnimationEngine(100, id).Sample(0.9).Eyes.Count == 2), "etats a visage : 2 yeux");
    Check(sans.All(id => new BotAnimationEngine(100, id).Sample(0.9).Eyes.Count == 0), "etats sans visage : 0 oeil");
}

// --- pastille notification ---------------------------------------------------
{
    var f = new BotAnimationEngine(100, "notify").Sample(1);
    Check(f.Notif != null && f.Notch != null
        && Math.Abs((f.Notch.Value.R - f.Notif.Value.R) / 100 - 0.054) < 0.005
        && Math.Abs(Math.Sqrt(f.Notif.Value.X * f.Notif.Value.X + f.Notif.Value.Y * f.Notif.Value.Y) / 100 - 1.003) < 0.05,
        "encoche et pastille de notification");
}

// --- anneaux et particules ----------------------------------------------------
{
    var f = new BotAnimationEngine(100, "orbit").Sample(1.4);
    Check(f.Arcs.Count > 3 && f.Arcs.Any(a => a.Back.Count > 0) && f.Arcs.Any(a => a.Front.Count > 0),
        "anneaux devant ET derriere");

    var e = new BotAnimationEngine(100, "burst");
    double Rayon(double t) { var d = e.Sample(t).Dots.FirstOrDefault(); return d == null ? 0 : Math.Sqrt(d.X * d.X + d.Y * d.Y); }
    Check(Rayon(0.15) > Rayon(0.45) && Rayon(0.45) > 0, "particules qui spiralent vers le centre");
}

// --- sphere des yeux (face.test.ts) ------------------------------------------
{
    double Court(EyePose e, double w) => Math.Sqrt(e.A * e.A + e.B * e.B) * w;
    double Long(EyePose e, double h) => Math.Sqrt(e.C * e.C + e.D * e.D) * h;

    var poses = BotFace.EyePoses(BotFace.RestGaze, 1);
    Check(poses.All(e => Math.Abs(e.A * e.D - e.B * e.C - e.Depth) < 1e-6), "det(tangente) == depth");
    Check(Math.Abs(poses[1].Depth / poses[0].Depth - 0.663) < 0.05, "ratio de profondeur ~0.663",
        (poses[1].Depth / poses[0].Depth).ToString("F3"));
    Check(Math.Abs(Court(poses[1], BotFace.EyeW) / Court(poses[0], BotFace.EyeW) - 0.674) < 0.05, "ratio de largeur ~0.674");
    Check(Math.Abs(Long(poses[0], BotFace.EyeH) - Long(poses[1], BotFace.EyeH)) < 1e-3, "meme longueur pour les deux yeux");

    foreach (var g in new[] { BotFace.RestGaze, new HeadGaze(-40, 10, 5), new HeadGaze(0, 0, 0) })
    {
        var p = BotFace.EyePoses(g, 1);
        double dot = p[0].X * p[1].X + p[0].Y * p[1].Y + p[0].Depth * p[1].Depth;
        Check(Math.Abs(Math.Acos(dot) * 180 / Math.PI - BotFace.EyeSplit * 2) < 1e-3, $"separation 31 deg (yaw {g.Yaw})");
    }
    Check(BotFace.EyePoses(new HeadGaze(80, 0, 0), 1)[1].Depth < 0, "oeil derriere la sphere a yaw 80");

    (HeadGaze G, double Split, double W, double H, (double X, double Y, double C, double L)[] Yeux)[] mesures =
    [
        (BotFace.RestGaze, BotFace.EyeSplit, BotFace.EyeW, BotFace.EyeH,
            [(0.189, -0.412, 0.178, 0.39), (0.614, -0.51, 0.12, 0.395)]),
        (new HeadGaze(6.92, -21.96, 11.6), 18.43, 0.356, 0.875,
            [(-0.198, 0.295, 0.353, 0.82), (0.412, 0.415, 0.315, 0.826)]),
        (new HeadGaze(-21.94, -5.82, -12.2), 18.89, 0.505, 0.498,
            [(-0.675, 0.172, 0.39, 0.495), (-0.059, 0.027, 0.495, 0.5)]),
    ];
    foreach (var m in mesures)
    {
        var ps = BotFace.EyePoses(m.G, 1, m.Split);
        for (int i = 0; i < 2; i++)
        {
            var att = m.Yeux[i];
            Check(Math.Abs(ps[i].X - att.X) < 0.04 && Math.Abs(ps[i].Y - att.Y) < 0.04
                && Math.Abs(Court(ps[i], m.W) - att.C) < 0.04 && Math.Abs(Long(ps[i], m.H) - att.L) < 0.04,
                $"pose mesuree ({m.G.Yaw},{m.G.Pitch}) oeil {i}",
                $"got ({ps[i].X:F3},{ps[i].Y:F3}) c {Court(ps[i], m.W):F3} l {Long(ps[i], m.H):F3}");
        }
    }
}

// --- radiusAtAngle (shape.test.ts) -------------------------------------------
{
    var tri = BotProfiles.Triangle;
    Check(Math.Abs(Shape2D.RadiusAtAngle(tri, -0.1) - Shape2D.RadiusAtAngle(tri, BotMath.Tau - 0.1)) < 1e-12, "enroule les angles negatifs");
    bool ok = true;
    foreach (var b in new[] { -30.0, -12.5, 7.3 })
        foreach (var tours in new[] { -3, -1, 1, 5 })
            if (Math.Abs(Shape2D.RadiusAtAngle(tri, b) - Shape2D.RadiusAtAngle(tri, b + tours * BotMath.Tau)) > 1e-10) ok = false;
    Check(ok, "enroule sur plusieurs tours");
    Check(Math.Abs(Shape2D.RadiusAtAngle(tri, -1e-9) - Shape2D.RadiusAtAngle(tri, 1e-9)) < 1e-8
        && Math.Abs(Shape2D.RadiusAtAngle(tri, 0) - tri[0]) < 1e-12, "continue au passage par zero");
}

// --- regard (engine.test.ts) --------------------------------------------------
{
    var sain = new BotAnimationEngine(100, "idle");
    var e = new BotAnimationEngine(100, "idle");
    e.SetLook(new BotLook(double.NaN, 10, 1, 0, 0), 0);
    var oeilSain = sain.Sample(1).Eyes[0];
    var oeilNaN = e.Sample(1).Eyes[0];
    Check(oeilNaN.CenterX == oeilSain.CenterX && oeilNaN.CenterY == oeilSain.CenterY, "cible NaN refusee");
    e.SetLook(new BotLook(-26, 10, 1, 0, 0), 1);
    Check(e.Sample(1 + BotAnimationEngine.LookMorph).Eyes[0].CenterX != sain.Sample(1 + BotAnimationEngine.LookMorph).Eyes[0].CenterX,
        "cible saine acceptee ensuite");

    var direct = new BotAnimationEngine(100, "idle");
    var tourne = new BotAnimationEngine(100, "idle");
    direct.SetLook(new BotLook(-26, 0, 1, 0, 0), 0);
    tourne.SetLook(new BotLook(-26, 0, 1, 360, 0), 0);
    var t = 1 + BotAnimationEngine.LookMorph;
    // Equivalent rotations differ only by floating-point trigonometric rounding.
    var fullTurnError = Math.Abs(direct.Sample(t).Eyes[0].CenterX - tourne.Sample(t).Eyes[0].CenterX);
    Check(fullTurnError < 1e-10, "un tour complet ne change pas l'arrivee", $"error={fullTurnError:R}");
    var mi = new BotAnimationEngine(100, "idle");
    mi.SetLook(new BotLook(-26, 0, 1, 180, 0), 0);
    Check(mi.Sample(t).Eyes.Count == 0, "mi-tour : la face est derriere");

    foreach (var yaw in new[] { -42, -26, -10 })
        foreach (var pitch in new[] { -3, 10, 23 })
        {
            var x = new BotAnimationEngine(100, "idle");
            x.SetLook(new BotLook(yaw, pitch, 1, 0, 0), 0);
            Check(x.Sample(1).Eyes.Count == 2, $"deux yeux visibles yaw {yaw} pitch {pitch}");
        }

    // derive eteinte quand le pointeur commande
    double OeilX(BotAnimationEngine en, double tt) => en.Sample(tt).Eyes[0].CenterX;
    double Amplitude(BotAnimationEngine en)
    {
        var xs = new List<double>();
        for (double tt = 1; tt <= 8; tt += 0.1) xs.Add(OeilX(en, tt));
        return xs.Max() - xs.Min();
    }
    var libre = new BotAnimationEngine(100, "idle");
    var tenu = new BotAnimationEngine(100, "idle");
    tenu.SetLook(new BotLook(BotFace.RestGaze.Yaw, 0, 1, 0, 0), 0);
    Check(Amplitude(tenu) < 2 && Amplitude(libre) > 5 * Amplitude(tenu), "la derive s'eteint sous le pointeur",
        $"tenu {Amplitude(tenu):F2}, libre {Amplitude(libre):F2}");
}

// --- forme personnalisee -------------------------------------------------------
{
    double[] Radii(string id) => BotSkins.ShapeById[id].Radii;
    var rond = new BotAnimationEngine(100, "idle");
    var goutte = new BotAnimationEngine(100, "idle", Radii("goutte"));
    Check(!goutte.Sample(1).BodyPoints.SequenceEqual(rond.Sample(1).BodyPoints), "la forme remplace les etats au repos");
    foreach (var id in new[] { "exclaim", "alert", "sleep", "egg", "hexagon" })
    {
        var nu = new BotAnimationEngine(100, id);
        var habille = new BotAnimationEngine(100, id, Radii("goutte"));
        Check(habille.Sample(1).BodyPoints.SequenceEqual(nu.Sample(1).BodyPoints), $"la forme ne touche pas {id}");
    }

    var e = new BotAnimationEngine(100, "idle", Radii("cercle"));
    double Hauteur(double t) => Footprint(e.Sample(t).BodyPoints).H;
    e.SetShape(Radii("capsule"), 1);
    var etapes = new[] { 1.06, 1.14, 1.26 }.Select(Hauteur).ToArray();
    bool ok = true;
    for (int i = 1; i < etapes.Length; i++) if (etapes[i] >= etapes[i - 1]) ok = false;
    Check(ok && etapes[0] < 2 && etapes[^1] > 1.24 && Math.Abs(Hauteur(1 + BotAnimationEngine.ShapeMorph + 0.05) - 1.24) < 0.05,
        "morph de forme vers capsule", string.Join(", ", etapes.Select(x => x.ToString("F3"))));

    // yeux dans la silhouette sur les formes non circulaires
    foreach (var id in new[] { "nuage", "capsule", "goutte", "triangle", "squircle" })
    {
        var f = new BotAnimationEngine(100, "idle", Radii(id)).Sample(1);
        bool dedans = f.Eyes.Count == 2;
        foreach (var eye in f.Eyes)
        {
            double bord = Shape2D.RadiusAtAngle(Radii(id), Math.Atan2(eye.CenterY, eye.CenterX)) * 100;
            if (Math.Sqrt(eye.CenterX * eye.CenterX + eye.CenterY * eye.CenterY) >= bord) dedans = false;
        }
        Check(dedans, $"yeux dans la silhouette : {id}");
    }

    // cercle = ne rien choisir (decalage nul, reference protegee)
    foreach (var state in BotStates.States.Where(s => s.BaseBody))
    {
        Check(BotEyeFit.EyeOffset(Radii("cercle"), state.Id, null).Equals(new Vec2(0, 0)), $"decalage nul cercle/{state.Id}");
        var avec = new BotAnimationEngine(100, state.Id, Radii("cercle")).Sample(1);
        var sans = new BotAnimationEngine(100, state.Id).Sample(1);
        Check(avec.Eyes.Select(y => y.CenterX).SequenceEqual(sans.Eyes.Select(y => y.CenterX)),
            $"cercle == rien choisir : {state.Id}");
    }

    // la taille des yeux ne depend pas de la forme
    var tailles = BotSkins.Shapes
        .Select(f => string.Join("|", new BotAnimationEngine(100, "idle", f.Radii).Sample(1).Eyes.Select(y => $"{y.Width}x{y.Height}")))
        .Distinct()
        .ToList();
    Check(tailles.Count == 1, "taille des yeux independante de la forme");
}

// --- expressions ---------------------------------------------------------------
{
    Check(BotExpressions.Expressions.Count == 16 && BotExpressions.Expressions.Select(x => x.Id).Distinct().Count() == 16,
        "16 expressions uniques");
    bool tiltsOk = true;
    foreach (var ex in BotExpressions.Expressions)
        foreach (var oeil in ex.Eyes)
        {
            double tilt = Math.Abs(oeil.Tilt);
            if (tilt < 1) continue;
            double rapport = oeil.W / oeil.H;
            var seuil = tilt >= 20 ? (0.6, 1.7) : (0.8, 1.25);
            if (rapport >= seuil.Item1 && rapport <= seuil.Item2) tiltsOk = false;
        }
    Check(tiltsOk, "inclinaisons seulement sur des yeux assez allonges");

    // colere et triste en miroir, dans des sens opposes
    double[] Angles(string id)
    {
        var ex = BotExpressions.ExpressionById[id];
        return new BotAnimationEngine(100, "idle", BotSkins.ShapeById["cercle"].Radii, ex).Sample(1)
            .Eyes.Select(y => y.AngleDeg).ToArray();
    }
    var colere = Angles("colere");
    var triste = Angles("triste");
    Check(Math.Sign(colere[0]) == -Math.Sign(colere[1]) && Math.Sign(triste[0]) == -Math.Sign(triste[1])
        && Math.Sign(colere[0]) == -Math.Sign(triste[0]), "colere/triste en miroir oppose");

    // l'expression ne s'applique qu'a l'etat de repos
    var exprEffraye = BotExpressions.ExpressionById["effraye"];
    var nuWink = new BotAnimationEngine(100, "wink", BotSkins.ShapeById["cercle"].Radii).Sample(1);
    var habWink = new BotAnimationEngine(100, "wink", BotSkins.ShapeById["cercle"].Radii, exprEffraye).Sample(1);
    Check(habWink.Eyes[0].Width == nuWink.Eyes[0].Width, "l'expression ne touche pas wink");
    var repos = new BotAnimationEngine(100, "idle", BotSkins.ShapeById["cercle"].Radii, exprEffraye).Sample(1);
    var reposNu = new BotAnimationEngine(100, "idle", BotSkins.ShapeById["cercle"].Radii).Sample(1);
    Check(repos.Eyes[0].Width != reposNu.Eyes[0].Width, "l'expression s'applique a idle");

    // blend monotone
    var de = BotExpressions.ExpressionById["neutre"];
    var vers = BotExpressions.ExpressionById["effraye"];
    var hauteurs = new[] { 0, 0.25, 0.5, 0.75, 1.0 }.Select(tt => BotExpressions.BlendExpression(de, vers, tt).Eyes[0].H).ToArray();
    bool mono = true;
    for (int i = 1; i < hauteurs.Length; i++) if (hauteurs[i] <= hauteurs[i - 1]) mono = false;
    Check(mono, "blendExpression monotone");
}

// --- changement d'etat en plein fondu -------------------------------------------
{
    const double Image = 1.0 / 60;
    double SautApres((string Id, double At)[] changements, double at)
    {
        var e = new BotAnimationEngine(100, "idle");
        var distances = new List<(double T, double[] D)>();
        int i = 0;
        for (double t = 0; t <= at + 4 * Image; t += Image)
        {
            while (i < changements.Length && changements[i].At <= t + 1e-9) { e.SetState(changements[i].Id, changements[i].At); i++; }
            distances.Add((t, e.Sample(t).Eyes.Select(y => Math.Sqrt(y.CenterX * y.CenterX + y.CenterY * y.CenterY)).ToArray()));
        }
        double pire = 0;
        for (int k = 1; k < distances.Count; k++)
        {
            var a = distances[k - 1]; var b = distances[k];
            if (a.D.Length != b.D.Length || b.T <= at || b.T > at + 2.5 * Image) continue;
            for (int j = 0; j < a.D.Length; j++) pire = Math.Max(pire, Math.Abs(a.D[j] - b.D[j]));
        }
        return pire;
    }
    double espace = SautApres([("wide", 0.5), ("idle", 2)], 2);
    double pleinFondu = SautApres([("wide", 0.5), ("idle", 0.6)], 0.6);
    Check(espace < 14 && pleinFondu <= espace + 1e-9, "pas de saut en plein fondu", $"espace {espace:F1}, fondu {pleinFondu:F1}");
}

// --- catalogue -------------------------------------------------------------------
{
    Check(BotStates.Sequence.Length == 14 && BotStates.Sequence.All(id => BotStates.ById.ContainsKey(id)),
        "14 etats dans la sequence");
    Check(BotStates.States.Where(s => !BotStates.Sequence.Contains(s.Id)).Select(s => s.Id).SequenceEqual(new[] { "swirl" }),
        "seul swirl est hors sequence");
}

// --- cycles ----------------------------------------------------------------------
{
    Check(BotCycles.MinDurationOf("alert") == 2 && BotCycles.MinDurationOf("burst") == 2.4
        && BotCycles.ClampDuration("orbit", 1) == 2.5 && BotCycles.MinDurationOf("idle") == BotCycles.MinBlock,
        "durees minimales des etats");
    Check(BotCycles.ClampDuration("idle", 999) == BotCycles.MaxBlock
        && BotCycles.ClampDuration("idle", 2.44) == 2.4 && BotCycles.ClampDuration("idle", 2.46) == 2.5
        && BotCycles.ClampDuration("idle", 0.1) == BotCycles.MinBlock,
        "clampDuration");
    Check(BotCycles.MinBlock == BotStates.States.Max(s => s.Morph), "MIN_BLOCK derive du plus long morph");

    var cycle = new BotCycle("c1", "Test", [new BotBlock("idle", 2), new BotBlock("wink", 1), new BotBlock("egg", 3)]);
    Check(BotCycles.TotalDuration(cycle.Blocks) == 6
        && BotCycles.BlockAt(cycle.Blocks, 2) == (1, 0)
        && BotCycles.BlockAt(cycle.Blocks, 3.5) == (2, 0.5)
        && BotCycles.BlockAt(cycle.Blocks, 6) == (0, 0)
        && BotCycles.BlockAt(cycle.Blocks, 8) == (1, 0),
        "blockAt");
    Check(BotCycles.DefaultCycle().Blocks.Select(b => b.State).SequenceEqual(BotStates.Sequence)
        && BotCycles.DefaultCycle().Blocks.All(b => b.Duration == BotStates.ById[b.State].Duration),
        "cycle par defaut = sequence mesuree");

    var rejetes = BotCycles.ParseCycles("[{\"id\":\"c1\",\"name\":\"A\",\"blocks\":[{\"state\":\"swirl\",\"duration\":2}]}]");
    Check(rejetes.Count == 0, "swirl refuse dans un montage");
    var gardes = BotCycles.ParseCycles("[{\"id\":\"c1\",\"name\":\"A\",\"blocks\":[{\"state\":\"idle\",\"duration\":2},{\"state\":\"disparu\",\"duration\":2}]}]");
    Check(gardes.Count == 1 && gardes[0].Blocks.Select(b => b.State).SequenceEqual(new[] { "idle" }), "blocs inconnus jetes");
}

// --- statique Sample -------------------------------------------------------------
{
    var f = BotAnimationEngine.Sample(1, "idle", new BotOptions(Shape: "galet", Color: "bleu", Expression: "heureux"));
    Check(f.BodyColor == "#3b93f0" && f.Eyes.Count == 2, "Sample statique avec options");
    Check(f.BodyPolar.Count == BotProfiles.Samples && Math.Abs(f.BodyPolar[0].Angle) < 1e-12, "echantillons polaires");
}

Console.WriteLine(failures == 0 ? "\nTOUT EST VERT" : $"\n{failures} ECHEC(S)");
return failures == 0 ? 0 : 1;
