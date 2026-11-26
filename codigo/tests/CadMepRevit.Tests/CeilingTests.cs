using CadMepRevit.Core;

/// <summary>Regla «sigue el techo» (CeilingProfile): escalones, vigas, uniones y redes sin forjado.</summary>
static class CeilingTests
{
    // Cota máxima del eje (mm sobre el nivel) a lo largo de un tramo, como la calcula Revit desde el forjado.
    private static CeilingSegmentInput Segment(int index, double length, Func<double, double?> maxAxis, double step = 200) => new()
    {
        Segment = index,
        LengthMm = length,
        Samples = Enumerable.Range(0, (int)Math.Ceiling(length / step) + 1)
            .Select(i => Math.Min(length, i * step)).Distinct().Select(a => (a, maxAxis(a))).ToList(),
        MinRiseMm = 250, MinPieceMm = 800, StepMarginMm = 150
    };

    // Ningún trozo queda por encima de lo permitido en ninguna muestra (contando los extremos de cada trozo).
    private static bool Safe(CeilingSegmentInput input, List<CeilingPiece> pieces) =>
        input.Samples.Where(s => s.MaxAxisMm != null).All(s =>
            pieces.Where(p => s.AlongMm >= p.FromMm - 1e-6 && s.AlongMm <= p.ToMm + 1e-6).All(p => p.AxisMm <= s.MaxAxisMm!.Value + 1e-6));

    private static bool Continuous(CeilingSegmentInput input, List<CeilingPiece> pieces) =>
        Math.Abs(pieces[0].FromMm) < 1e-6 && Math.Abs(pieces[^1].ToMm - input.LengthMm) < 1e-6 &&
        pieces.Zip(pieces.Skip(1)).All(p => Math.Abs(p.First.ToMm - p.Second.FromMm) < 1e-6) &&
        pieces.All(p => p.LengthMm > 0);

    public static void Run(Action<bool, string> check)
    {
        Console.WriteLine("2q) Altura según el techo (escalones, vigas, uniones)");

        var flat = Segment(0, 6000, _ => 2900);
        var p = CeilingProfile.Profile(flat)!;
        check(p.Count == 1 && p[0].AxisMm == 2900, "Forjado plano: un solo tramo a la cota bajo el forjado");

        var up = Segment(0, 10000, a => a < 5000 ? 2600 : 3200);
        p = CeilingProfile.Profile(up)!;
        check(p.Count == 2 && p[0].AxisMm == 2600 && p[1].AxisMm == 3200 && Safe(up, p) && Continuous(up, p),
            "Forjado que sube: el tramo sube con un escalón y nunca lo toca");
        check(p[0].ToMm >= 5000 + 150, "La subida se hace ya bajo el forjado alto (con margen para el codo)");

        var down = Segment(0, 10000, a => a < 5000 ? 3200 : 2600);
        p = CeilingProfile.Profile(down)!;
        check(p.Count == 2 && p[0].AxisMm == 3200 && p[1].AxisMm == 2600 && p[0].ToMm <= 5000 - 150 && Safe(down, p),
            "Forjado que baja: el tramo baja antes de llegar al escalón");

        var small = Segment(0, 10000, a => a < 5000 ? 3000 : 3100);
        p = CeilingProfile.Profile(small)!;
        check(p.Count == 1 && p[0].AxisMm == 3000, "Escalón menor que el mínimo: sin codos, por la cota más baja");

        var beam = Segment(0, 12000, a => a >= 6000 && a <= 6300 ? 2500 : 3100);
        p = CeilingProfile.Profile(beam)!;
        check(p.Count == 3 && p[1].AxisMm == 2500 && p[1].LengthMm >= 800 - 1e-6 && p[0].AxisMm == 3100 && p[2].AxisMm == 3100 && Safe(beam, p) && Continuous(beam, p),
            "Viga estrecha: baja solo bajo la viga (con sitio para los codos) y vuelve a subir");

        var bump = Segment(0, 12000, a => a >= 6000 && a <= 6200 ? 3400 : 2800);
        p = CeilingProfile.Profile(bump)!;
        check(p.Count == 1 && p[0].AxisMm == 2800, "Hueco alto muy corto: no se sube, sigue recto");

        var ramp = Segment(0, 10000, a => 2500 + a * 0.1);
        p = CeilingProfile.Profile(ramp)!;
        check(p.Count > 1 && Safe(ramp, p) && Continuous(ramp, p) && p.Zip(p.Skip(1)).All(x => x.Second.AxisMm > x.First.AxisMm),
            $"Rampa: escalera de {p.Count} tramos bajo la rampa, siempre por debajo");

        var partial = Segment(0, 8000, a => a < 3000 ? null : 2700);
        p = CeilingProfile.Profile(partial)!;
        check(p.Count == 1 && p[0].AxisMm == 2700, "Parte sin forjado encima: toma la cota del forjado más próximo");
        check(CeilingProfile.Profile(Segment(0, 5000, _ => null)) is null, "Sin forjado en todo el tramo: sin cota propia");

        // Uniones: un codo entre un tramo bajo forjado alto y otro bajo forjado bajo. Los dos llegan a la misma cota.
        var a1 = Segment(1, 6000, _ => 3200);
        var a2 = Segment(2, 6000, _ => 2700);
        var corner = new List<(int, bool)> { (1, false), (2, true) };
        var r = CeilingProfile.Solve(new[] { a1, a2 }, new[] { corner }, 2500);
        check(r.Pieces[1][^1].AxisMm == 2700 && r.Pieces[2][0].AxisMm == 2700, "Codo entre dos alturas: los dos extremos a la cota más baja");
        check(r.Pieces[1].Count == 2 && r.Pieces[1][0].AxisMm == 3200 && r.Pieces[1][^1].LengthMm >= 800 - 1e-6,
            "El tramo alto solo baja cerca de la unión (con una bajada y sus codos)");

        var shortHigh = Segment(1, 1200, _ => 3200);
        r = CeilingProfile.Solve(new[] { shortHigh, a2 }, new[] { corner }, 2500);
        check(r.Pieces[1].Count == 1 && r.Pieces[1][0].AxisMm == 2700, "Tramo alto corto unido a uno bajo: baja entero, sin escalón");

        // Te: tres tramos, uno sin forjado encima.
        var t1 = Segment(1, 5000, _ => 3000);
        var t2 = Segment(2, 5000, _ => 2800);
        var t3 = Segment(3, 4000, _ => null);
        var tee = new List<(int, bool)> { (1, false), (2, true), (3, true) };
        r = CeilingProfile.Solve(new[] { t1, t2, t3 }, new[] { tee }, 2500);
        check(new[] { r.Pieces[1][^1].AxisMm, r.Pieces[2][0].AxisMm, r.Pieces[3][0].AxisMm }.All(z => z == 2800) && r.WithoutSlab.Contains(3),
            "Te: los tres extremos a la misma cota; el ramal sin forjado toma la de la te");

        var n1 = Segment(1, 5000, _ => null);
        var n2 = Segment(2, 5000, _ => null);
        r = CeilingProfile.Solve(new[] { n1, n2 }, new[] { new List<(int, bool)> { (1, false), (2, true) } }, 2500);
        check(r.Fallback.SetEquals(new[] { 1, 2 }) && r.Pieces.Values.All(x => x.Single().AxisMm == 2500),
            "Red sin ningún forjado encima: queda a la altura supuesta (2500 mm)");

        // Red larga con escalones y uniones: siempre segura y continua, y cada unión a una sola cota.
        var rnd = new Random(7);
        var segs = Enumerable.Range(0, 8).Select(i =>
        {
            double l = 3000 + rnd.Next(0, 9000);
            double b = 2600 + rnd.Next(0, 6) * 100, h = b + 300 + rnd.Next(0, 6) * 100, at = rnd.Next(500, (int)l - 500);
            return Segment(i, l, x => x < at ? h : b);
        }).ToList();
        var chain = Enumerable.Range(0, 7).Select(i => (IReadOnlyList<(int, bool)>)new List<(int, bool)> { (i, false), (i + 1, true) }).ToList();
        r = CeilingProfile.Solve(segs, chain, 2500);
        check(segs.All(s => Safe(s, r.Pieces[s.Segment]) && Continuous(s, r.Pieces[s.Segment])) &&
              chain.All(n => Math.Abs(r.Pieces[n[0].Item1][^1].AxisMm - r.Pieces[n[1].Item1][0].AxisMm) < 1e-6),
            $"Red de 8 tramos con escalones: segura, continua y con cada unión a una cota ({r.Steps} subidas o bajadas)");

        Console.WriteLine("2r) Familias de uniones con nombres de otras oficinas");
        var yes = new (string Name, JunctionKind Kind, FittingShape Shape)[]
        {
            ("M_Codo rectangular - Radio 1", JunctionKind.Codo, FittingShape.Rectangular),
            ("M_Codo rectangular - Radio", JunctionKind.Codo, FittingShape.Rectangular),
            ("XX_Codo_Chapa_Rectangular_L3", JunctionKind.Codo, FittingShape.Rectangular),
            ("M_Codo redondo 1", JunctionKind.Codo, FittingShape.Redonda),
            ("M_Te rectangular - Recto", JunctionKind.Te, FittingShape.Rectangular),
            ("M_Te rectangular 1", JunctionKind.Te, FittingShape.Rectangular),
            ("M_Te redonda - Recto", JunctionKind.Te, FittingShape.Redonda),
            ("M_Cruz rectangular -  Recta", JunctionKind.Cruz, FittingShape.Rectangular),
            ("M_Cruz redonda - Recto", JunctionKind.Cruz, FittingShape.Redonda),
            ("M_Transición rectangular - Ángulo 2", JunctionKind.Transicion, FittingShape.Rectangular),
            ("M_Transición redonda - Ángulo", JunctionKind.Transicion, FittingShape.Redonda),
            ("M_Codo - Genérico", JunctionKind.Codo, FittingShape.Tuberia),
            ("UNT_Te", JunctionKind.Te, FittingShape.Tuberia),
            ("M_Transición - Genérica", JunctionKind.Transicion, FittingShape.Tuberia),
        };
        foreach (var (name, kind, shape) in yes)
            check(FittingNames.LooksLike(name, kind, shape), $"'{name}' es {kind} {shape}");
        var no = new (string Name, JunctionKind Kind, FittingShape Shape)[]
        {
            ("M_Transición de rectangular a redonda - Ángulo 1", JunctionKind.Transicion, FittingShape.Rectangular),
            ("M_Codo redondo 1", JunctionKind.Codo, FittingShape.Rectangular),
            ("M_Codo rectangular - Radio 1", JunctionKind.Codo, FittingShape.Tuberia),
            ("M_Codo de tubo - Acero", JunctionKind.Codo, FittingShape.Tuberia),
            ("M_Codo horizontal de canal", JunctionKind.Codo, FittingShape.Tuberia),
            ("M_Remate rectangular", JunctionKind.Codo, FittingShape.Rectangular),
            ("M_Conducto rectangular - Te pantalón", JunctionKind.Te, FittingShape.Rectangular),
            ("XX_TE_Rejilla admisión conducto cuadrado", JunctionKind.Te, FittingShape.Tuberia),
            ("M_Termometro", JunctionKind.Te, FittingShape.Tuberia),
        };
        foreach (var (name, kind, shape) in no)
            check(!FittingNames.LooksLike(name, kind, shape), $"'{name}' NO es {kind} {shape}");
    }
}
