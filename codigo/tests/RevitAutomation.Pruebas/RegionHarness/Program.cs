using RevitAutomation.Addin.Commands.Regiones;

// Pure-logic checks of REGION. Pieces are described as the Revit adapter builds them from the CAD link geometry
// (PolyLine → one piece with its vertices; Line/Arc → one piece each). Reading the link and creating filled regions
// still need a test inside Revit.
internal static class Program
{
    private static int _pass, _fail, _id;
    private const double M = 1 / 0.3048; // one metre in feet

    private static void Check(bool condition, string name)
    {
        if (condition) { _pass++; Console.WriteLine("PASS: " + name); }
        else { _fail++; Console.WriteLine("FAIL: " + name); }
    }

    private static RegionPoint P(double x, double y) => new(x * M, y * M);
    private static RegionPiece Poly(string layer, params RegionPoint[] pts) => new(_id++, layer, pts, true);
    private static RegionPiece Seg(string layer, RegionPoint a, RegionPoint b) => new(_id++, layer, new[] { a, b }, false);
    private static RegionPiece[] Square(string layer, double x, double y, double s) => new[]
    {
        Seg(layer, P(x, y), P(x + s, y)), Seg(layer, P(x + s, y + s), P(x + s, y)), // second one drawn backwards
        Seg(layer, P(x + s, y + s), P(x, y + s)), Seg(layer, P(x, y + s), P(x, y))
    };

    private static string ReferenceSignature(IReadOnlyList<RegionPoint> outline, double tol)
    {
        double g = Math.Max(tol, 1e-9) * 2;
        var keys = new List<string>();
        foreach (var p in outline)
        {
            string key = $"{Math.Round(p.X / g)}:{Math.Round(p.Y / g)}";
            if (keys.Count == 0 || keys[^1] != key) keys.Add(key);
        }
        if (keys.Count > 1 && keys[0] == keys[^1]) keys.RemoveAt(keys.Count - 1);
        string best = "";
        foreach (var seq in new[] { keys, keys.AsEnumerable().Reverse().ToList() })
            for (int start = 0; start < seq.Count; start++)
            {
                string candidate = string.Join("|", seq.Skip(start).Concat(seq.Take(start)));
                if (best.Length == 0 || string.CompareOrdinal(candidate, best) < 0) best = candidate;
            }
        return best;
    }

    private static void PerformanceRegressions()
    {
        var previous = System.Globalization.CultureInfo.CurrentCulture;
        bool same = true;
        try
        {
            foreach (string culture in new[] { "en-US", "es-ES", "ar-SA" })
            {
                System.Globalization.CultureInfo.CurrentCulture = System.Globalization.CultureInfo.GetCultureInfo(culture);
                var random = new Random(6143);
                for (int sample = 0; sample < 250; sample++)
                {
                    var points = Enumerable.Range(0, random.Next(0, 50))
                        .Select(_ => new RegionPoint(random.Next(-20, 20), random.Next(-20, 20))).ToList();
                    if (sample % 2 == 0) points.AddRange(points.ToArray()); // repeated minimum rotations
                    same &= RegionGeometry.Signature(points, 0.5) == ReferenceSignature(points, 0.5);
                }
            }
        }
        finally { System.Globalization.CultureInfo.CurrentCulture = previous; }
        Check(same, "Linear signature equals exhaustive rotations exactly, including repeats and culture");

        // The first representative is in a later grid bucket than the second. Both
        // match the third endpoint: first-created must win, not nearest/bucket order.
        var pieces = new[]
        {
            new RegionPiece(100, "tie", new[] { new RegionPoint(0.75, 0), new RegionPoint(10, 0) }, false),
            new RegionPiece(101, "tie", new[] { new RegionPoint(-0.75, 0), new RegionPoint(10, 10) }, false),
            new RegionPiece(102, "tie", new[] { new RegionPoint(0, 0), new RegionPoint(0, 10) }, false),
            new RegionPiece(103, "tie", new[] { new RegionPoint(10, 0), new RegionPoint(0, 10) }, false)
        };
        var scan = RegionGeometry.Find(pieces, 1);
        Check(scan.Loops.Count == 1 && scan.Loops[0].Steps.Select(step => step.PieceId).SequenceEqual(new[] { 100, 103, 102 })
            && RegionScan.Get(scan.OpenPieces, "tie") == 1, "Endpoint clustering preserves first representative and visible loop order");
        var many = Enumerable.Range(0, 1000).SelectMany(i => Square("grid", i * 3, 0, 2)).ToList();
        scan = RegionGeometry.Find(many);
        Check(scan.Loops.Count == 1000 && scan.Loops.All(loop => Math.Abs(loop.AreaSquareMeters - 4) < 1e-6),
            "Thousands of disconnected CAD outlines retain their regions and areas");
    }

    private static int Main()
    {
        PerformanceRegressions();
        // Closed polyline → one region with its area.
        var s = RegionGeometry.Find(new[] { Poly("ZONA-A", P(0, 0), P(4, 0), P(4, 3), P(0, 3), P(0, 0)) });
        Check(s.Loops.Count == 1 && Math.Abs(s.Loops[0].AreaSquareMeters - 12) < 1e-6, "Polilínea cerrada: una región de 12 m²");
        Check(s.Loops[0].FromPolyline && s.Loops[0].Outline.Count == 4, "Polilínea cerrada: 4 vértices sin repetir el cierre");

        // Open polyline is not a region.
        s = RegionGeometry.Find(new[] { Poly("ZONA-A", P(0, 0), P(4, 0), P(4, 3), P(0, 3)) });
        Check(s.Loops.Count == 0 && RegionScan.Get(s.OpenPieces, "ZONA-A") == 1, "Polilínea abierta: no se crea, se informa");

        // Two closed polylines sharing an edge/vertices (adjacent rooms, same layer) → two regions.
        s = RegionGeometry.Find(new[]
        {
            Poly("ZONA-A", P(0, 0), P(4, 0), P(4, 3), P(0, 3), P(0, 0)),
            Poly("ZONA-A", P(4, 0), P(8, 0), P(8, 3), P(4, 3), P(4, 0))
        });
        Check(s.Loops.Count == 2, "Dos polilíneas cerradas contiguas en la misma capa: dos regiones");

        // Loose lines forming a square (one drawn backwards) → one chained region with reversed step.
        s = RegionGeometry.Find(Square("ZONA-B", 10, 10, 2));
        Check(s.Loops.Count == 1 && Math.Abs(s.Loops[0].AreaSquareMeters - 4) < 1e-6 && !s.Loops[0].FromPolyline, "Líneas sueltas que cierran: una región de 4 m²");
        Check(s.Loops.Count == 1 && s.Loops[0].Steps.Count == 4 && s.Loops[0].Steps.Count(st => st.Reversed) == 1, "Encadenado: el tramo dibujado al revés se recorre invertido");

        // Gap of 0.5 mm closes (tolerance 1 mm); gap of 5 mm does not.
        var near = Square("ZONA-C", 0, 0, 1).ToList();
        near[3] = Seg("ZONA-C", P(0, 1), P(0.0005, 0));
        Check(RegionGeometry.Find(near).Loops.Count == 1, "Hueco de 0,5 mm: se cierra (tolerancia 1 mm)");
        near[3] = Seg("ZONA-C", P(0, 1), P(0.005, 0));
        s = RegionGeometry.Find(near);
        Check(s.Loops.Count == 0 && RegionScan.Get(s.OpenPieces, "ZONA-C") == 4, "Hueco de 5 mm: no se cierra, se informa como abierto");

        // Lines of different layers do not chain together.
        var mixed = Square("ZONA-D", 0, 0, 1).ToList();
        mixed[2] = Seg("OTRA", P(1, 1), P(0, 1));
        Check(RegionGeometry.Find(mixed).Loops.Count == 0, "Líneas de capas distintas no se encadenan");

        // Junction of 3+ lines: ambiguous, nothing guessed.
        var t = Square("ZONA-E", 0, 0, 2).ToList();
        t.Add(Seg("ZONA-E", P(0, 0), P(-1, -1)));
        s = RegionGeometry.Find(t);
        Check(s.Loops.Count == 0 && RegionScan.Get(s.AmbiguousPieces, "ZONA-E") == 5, "Cruce de 3 líneas en un punto: ambiguo, no se adivina");

        // Same outline drawn twice (start shifted, reversed) → one region and one duplicate.
        s = RegionGeometry.Find(new[]
        {
            Poly("ZONA-F", P(0, 0), P(4, 0), P(4, 3), P(0, 3), P(0, 0)),
            Poly("ZONA-F", P(4, 3), P(4, 0), P(0, 0), P(0, 3), P(4, 3))
        });
        Check(s.Loops.Count == 1 && RegionScan.Get(s.Duplicates, "ZONA-F") == 1, "Contorno repetido: una región y un duplicado informado");

        // Same outline on two different layers → both kept (each layer is its own zone).
        s = RegionGeometry.Find(new[]
        {
            Poly("ZONA-G", P(0, 0), P(4, 0), P(4, 3), P(0, 3), P(0, 0)),
            Poly("ZONA-H", P(0, 0), P(4, 0), P(4, 3), P(0, 3), P(0, 0))
        });
        Check(s.Loops.Count == 2, "Mismo contorno en dos capas: dos regiones");

        // Tiny outline (5 x 5 cm) is noise; degenerate (collinear) also.
        s = RegionGeometry.Find(new[]
        {
            Poly("ZONA-I", P(0, 0), P(0.05, 0), P(0.05, 0.05), P(0, 0.05), P(0, 0)),
            Poly("ZONA-I", P(0, 0), P(1, 0), P(2, 0), P(0, 0))
        });
        Check(s.Loops.Count == 0 && RegionScan.Get(s.TooSmall, "ZONA-I") == 2, "Contornos de menos de 0,01 m² o sin superficie: descartados e informados");

        // Full circle as two arcs (how the adapter splits a cyclic curve) → one region.
        var up = Enumerable.Range(0, 9).Select(i => P(Math.Cos(Math.PI * i / 8), Math.Sin(Math.PI * i / 8))).ToArray();
        var down = Enumerable.Range(8, 9).Select(i => P(Math.Cos(Math.PI * i / 8), Math.Sin(Math.PI * i / 8))).ToArray();
        s = RegionGeometry.Find(new[] { new RegionPiece(_id++, "CIRC", up, false), new RegionPiece(_id++, "CIRC", down, false) });
        Check(s.Loops.Count == 1 && Math.Abs(s.Loops[0].AreaSquareMeters - Math.PI) < 0.1, "Círculo en dos arcos: una región de ~π m²");

        // Two separate squares of loose lines on one layer → two regions.
        s = RegionGeometry.Find(Square("ZONA-J", 0, 0, 1).Concat(Square("ZONA-J", 5, 5, 1)));
        Check(s.Loops.Count == 2, "Dos cuadrados de líneas sueltas separados: dos regiones");

        // Layers listed once, ordered, including those with only problems.
        s = RegionGeometry.Find(new[] { Poly("b", P(0, 0), P(4, 0), P(4, 3), P(0, 3), P(0, 0)), Poly("A", P(0, 0), P(1, 1)) });
        Check(s.Layers.SequenceEqual(new[] { "A", "b" }), "Capas listadas una vez y ordenadas (también las que solo tienen problemas)");

        // Same points, different outline (order matters): not duplicates (Codex review).
        s = RegionGeometry.Find(new[]
        {
            Poly("ZONA-K", P(0, 0), P(2, 1), P(4, 0), P(4, 4), P(0, 4), P(0, 0)),
            Poly("ZONA-K", P(0, 0), P(4, 0), P(4, 4), P(2, 1), P(0, 4), P(0, 0))
        });
        Check(s.Loops.Count == 2 && RegionScan.Get(s.Duplicates, "ZONA-K") == 0, "Mismos vértices en otro orden: dos contornos distintos, no duplicados");

        // Layers that clean to the same name get distinct types (Codex review).
        var names = RegionNaming.TypeNames(new[] { "A]B", "A[B", "ZONA" });
        Check(names["A[B"] == "REG - A_B" && names["A]B"] == "REG - A_B (2)" && names["ZONA"] == "REG - ZONA", "Capas que se limpian igual: un tipo distinto por capa");
        Check(RegionNaming.TypeNames(new[] { "A[B", "A]B" })["A]B"] == names["A]B"], "Nombres de tipo estables (no dependen del orden de entrada)");

        // Names.
        Check(RegionNaming.TypeName("ZONA:1 [norte]") == "REG - ZONA_1 _norte_", "Nombre de tipo sin caracteres prohibidos por Revit");
        Check(RegionNaming.TypeName("  ") == "REG - sin nombre", "Capa sin nombre: «REG - sin nombre»");
        Check(RegionNaming.IsOurs(RegionNaming.Comment("ZONA-A")) && !RegionNaming.IsOurs("ZONA-A") && !RegionNaming.IsOurs(null), "Marca de comentarios: solo las regiones de REGION");

        Console.WriteLine($"REGION_TOTAL={_pass + _fail} PASS={_pass} FAIL={_fail}; lógica pura, no API de Revit.");
        return _fail == 0 ? 0 : 1;
    }
}
