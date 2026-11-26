using ACadSharp;
using ACadSharp.Entities;
using ACadSharp.Tables;
using CadMepRevit.Core;
using CSMath;

/// <summary>
/// Situar lo modelado encima del CAD tal y como está en el proyecto (movido, girado, centro a centro, con más o menos
/// cosas que las que lee el complemento), encajando puntos y no por la caja del CAD (<see cref="CadRegistration"/>).
/// </summary>
static class PlacementTests
{
    /// <summary>El CAD «en Revit»: girado, desplazado, sin una parte de los puntos y con otros que el lector no ve.</summary>
    static List<(double X, double Y)> Placed(List<(double X, double Y)> pts, double angleDeg, double dx, double dy, int seed)
    {
        var rnd = new Random(seed);
        double a = angleDeg * Math.PI / 180, c = Math.Cos(a), s = Math.Sin(a);
        var list = new List<(double X, double Y)>();
        foreach (var p in pts)
            if (rnd.NextDouble() > 0.3) list.Add((c * p.X - s * p.Y + dx, s * p.X + c * p.Y + dy));
        var ext = CadRegistration.Extent(list)!.Value;
        for (int i = 0; i < pts.Count / 5; i++)
            list.Add((ext.CenterX + (rnd.NextDouble() - .5) * ext.Size, ext.CenterY + (rnd.NextDouble() - .5) * ext.Size));
        list.Add((ext.CenterX + 5e7, ext.CenterY - 5e7)); // resto lejano: inflaría la caja del CAD en Revit
        return list;
    }

    static List<(double X, double Y)> Rotate(List<(double X, double Y)> pts, double angleDeg)
    {
        double a = angleDeg * Math.PI / 180, c = Math.Cos(a), s = Math.Sin(a);
        return pts.Select(p => (c * p.X - s * p.Y, s * p.X + c * p.Y)).ToList();
    }

    public static void Run(Action<bool, string> check)
    {
        Console.WriteLine("2m) Situar lo modelado encima del CAD del proyecto (encaje por puntos)");
        // Sintético: retícula irregular de 400 puntos.
        var rnd = new Random(7);
        var pts = Enumerable.Range(0, 400).Select(_ => (X: Math.Round(rnd.NextDouble() * 60000), Y: Math.Round(rnd.NextDouble() * 40000))).ToList();
        var cad = Placed(pts, 0, 123456.7, -78901.2, 1);
        var reg = new CadRegistration(cad, 10);
        var (h0, t0) = reg.Check(pts, 0, 0);
        check(h0 < 0.1 * t0, $"Sin desplazar, el dibujo no cae sobre el CAD movido ({h0} de {t0})");
        var off = reg.FindOffset(pts);
        check(off is { } o && Math.Abs(o.Dx - 123456.7) < 1 && Math.Abs(o.Dy + 78901.2) < 1 && o.Hits >= 0.5 * o.Tried,
              $"Encuentra el desplazamiento (123456,7; -78901,2) a menos de 1 mm: {off}");

        var cadRot = Placed(pts, 17, -5000, 250000, 2);
        var regRot = new CadRegistration(cadRot, 10);
        var offRot = regRot.FindOffset(Rotate(pts, 17));
        check(offRot is { } r && Math.Abs(r.Dx + 5000) < 1 && Math.Abs(r.Dy - 250000) < 1, $"Girado 17° y movido: lo encuentra con el giro del CAD ({offRot})");

        var other = Enumerable.Range(0, 400).Select(_ => (X: rnd.NextDouble() * 60000, Y: rnd.NextDouble() * 40000)).ToList();
        var none = reg.FindOffset(other);
        check(none is null || none.Value.Hits < Math.Max(20, 0.4 * none.Value.Tried), $"Un dibujo distinto NO encaja ({none})");

        check(CadRegistration.UnitFactor(1000.4) == 1000 && CadRegistration.UnitFactor(0.0394) == 1 / 25.4 && CadRegistration.UnitFactor(1.02) == 1 &&
              CadRegistration.UnitFactor(3.3) == 1, "Factor de unidades entre lo leído y lo vinculado (m↔mm, mm↔pulgadas; nada si no es un factor de unidades)");
        var ext = CadRegistration.Extent(cad)!.Value;
        check(ext.Size < 150000, $"El contorno robusto ignora el resto lejano (lado {ext.Size / 1000:0.#} m)");

        var doc = new CadDocument();
        var symbol = new BlockRecord("DETECTOR");
        doc.BlockRecords.Add(symbol);
        symbol.Entities.Add(new Line(new XYZ(100, 150, 0), new XYZ(300, 150, 0)));
        var insertions = pts.Take(8).ToList();
        foreach (var (x, y) in insertions)
            doc.Entities.Add(new Insert(symbol) { InsertPoint = new XYZ(x, y, 0) });
        var drawing = CadReader.Extract(doc, "solo bloques", new CadReader.Options { ReadLinesInsideBlocks = false });
        drawing.Blocks.Add(new CadBlockRef { EntityIndex = -1, Position = new P3(90000, 90000, 0) });
        var (lines, blocks) = CadRegistration.DrawingPoints(drawing, 1, 5);
        check(lines.Count == 0 && blocks.SequenceEqual(insertions),
            "Plano de solo INSERT: se usan sus ocho inserciones, sin geometría del símbolo ni luminarias equivalentes");
        var rotatedBlocks = Rotate(insertions, 31);
        var blockPositions = rotatedBlocks.Select(p => (p.X + 17520.0, p.Y - 34850.0));
        var blockReg = new CadRegistration(blockPositions, 10);
        var blockOffset = blockReg.FindOffset(Rotate(blocks, 31));
        check(blockOffset is { } b && b.Hits == blocks.Count && Math.Abs(b.Dx - 17520) < 0.01 && Math.Abs(b.Dy + 34850) < 0.01,
            $"Plano de solo INSERT: encajan los ocho bloques tras desplazar y girar 31° ({blockOffset})");
    }

    /// <summary>Con planos reales: el CAD vinculado centro a centro y girado se encuentra, en poco tiempo.</summary>
    public static void RunReal(Action<bool, string> check, string folder)
    {
        Console.WriteLine("2m) Situar sobre el CAD con planos reales");
        // Cualquier plano de la carpeta indicada (hasta seis), sin depender de sus nombres.
        var files = Directory.GetFiles(folder, "*.dwg", SearchOption.AllDirectories).Order().Take(6).ToList();
        if (files.Count == 0) { Console.WriteLine("   (sin planos reales)"); return; }
        var loaded = new List<(string Name, List<(double X, double Y)> Pts)>();
        foreach (var path in files)
        {
            var d = CadReader.Read(path, new CadReader.Options { ReadLinesInsideBlocks = PlanDefaults.ReadLinesInsideBlocks });
            LayerPlanning.FixImplausibleUnits(d);
            double k = DrawingUnits.ToMillimeters(d.HeaderUnit ?? DrawingUnit.Milimetros);
            var (lines, blocks) = CadRegistration.DrawingPoints(d, k, 5);
            var pts = lines.Concat(blocks).ToList();
            loaded.Add((Path.GetFileName(path), pts));
            var rotated = Rotate(pts, 30);
            var ext = CadRegistration.Extent(rotated)!.Value;
            // «Centro a centro»: el centro del dibujo en el origen del proyecto, y además girado 30°.
            var cad = Placed(pts, 30, -ext.CenterX, -ext.CenterY, 3);
            var clock = System.Diagnostics.Stopwatch.StartNew();
            var reg = new CadRegistration(cad, 10);
            var off = reg.FindOffset(rotated);
            double s = clock.Elapsed.TotalSeconds;
            double ex = -ext.CenterX, ey = -ext.CenterY; // el desplazamiento usado al colocarlo
            double error = off is { } fit ? Math.Sqrt(Math.Pow(fit.Dx - ex, 2) + Math.Pow(fit.Dy - ey, 2)) : double.PositiveInfinity;
            check(off is { } o && error < 2 && o.Hits >= 0.3 * o.Tried && s < 20,
                  $"{Path.GetFileName(path)}: {lines.Count} puntos de líneas/círculos y {blocks.Count} de bloques; CAD centro a centro y girado 30° → desplazamiento hallado " +
                  $"{(off is { } q ? $"{q.Dx / 1000:0.###}, {q.Dy / 1000:0.###} m ({q.Hits} de {q.Tried} puntos)" : "NINGUNO")}, error {error:0.###} mm; {s:0.0} s");
        }
        // Un plano no se coloca encima del CAD de OTRO plano que no tiene nada que ver.
        if (loaded.Count >= 2 && loaded[^1] is { Pts: not null } unrelated && loaded[0].Name != unrelated.Name)
        {
            var reg = new CadRegistration(Placed(unrelated.Pts, 0, 0, 0, 4), 10);
            var off = reg.FindOffset(loaded[0].Pts);
            check(off is null || off.Value.Hits < Math.Max(20, 0.4 * off.Value.Tried) || (Math.Abs(off.Value.Dx) < 2 && Math.Abs(off.Value.Dy) < 2),
                  $"'{loaded[0].Name}' sobre el CAD de '{unrelated.Name}': no se coloca en un sitio cualquiera ({off})");
        }
    }
}
