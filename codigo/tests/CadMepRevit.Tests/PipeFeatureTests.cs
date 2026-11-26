using ACadSharp;
using ACadSharp.Entities;
using ACadSharp.IO;
using ACadSharp.Tables;
using CadMepRevit.Core;
using CSMath;

public static class PipeFeatureTests
{
    private const double Tol = PlanDefaults.ToleranceMm;
    private const string Layer = "Plomberie_PIPE";
    private const string Notes = "Plomberie_PIPE_ETIQUETAS";

    public static void Run(Action<bool, string> check, string folder)
    {
        Directory.CreateDirectory(folder);
        foreach (double k in new[] { 1d, 1000d })
        {
            var doc = new CadDocument();
            doc.Header.InsUnits = k == 1 ? ACadSharp.Types.Units.UnitsType.Millimeters : ACadSharp.Types.Units.UnitsType.Meters;
            var pipes = new Layer(Layer);
            var notes = new Layer(Notes);
            doc.Layers.Add(pipes);
            doc.Layers.Add(notes);
            XYZ P(double x, double y = 0) => new(x / k, y / k, 0);
            Circle CircleAt(double x, double diameter, Layer layer) => new() { Center = P(x), Radius = diameter / (2 * k), Layer = layer };
            doc.Entities.Add(new Line(P(0), P(6000)) { Layer = pipes });
            doc.Entities.Add(CircleAt(0, 110, pipes));
            doc.Entities.Add(CircleAt(7, 135, pipes));
            doc.Entities.Add(CircleAt(3000, 80, pipes));
            doc.Entities.Add(CircleAt(6000, 110, pipes));
            doc.Entities.Add(CircleAt(16000, 40, pipes));
            doc.Entities.Add(CircleAt(12000, 401, pipes));
            doc.Entities.Add(CircleAt(12000, 110, notes));
            var nested = new BlockRecord("geometría interior");
            nested.Entities.Add(CircleAt(0, 110, pipes));
            doc.BlockRecords.Add(nested);
            doc.Entities.Add(new Insert(nested) { InsertPoint = P(9000), Layer = pipes });
            foreach (var (x, value) in new[] { (0, "BAJA A NIVEL -1"), (3000, "SUBE"), (6000, "DN125"), (16000, "SUBE") })
                doc.Entities.Add(new MText { Value = value, InsertPoint = P(x, 120), Height = 100 / k, Layer = notes, AlignmentPoint = new XYZ(1, 0, 0) });

            string path = Path.Combine(folder, $"red_colgada_{k:0}.dxf");
            DxfWriter.Write(path, doc, false);
            var d = CadReader.Read(path);
            check(d.Circles.Count == 7, $"DXF ({k:0} mm/unidad): siete círculos de modelo; se excluye el del bloque");
            var net = NetworkBuilder.Build(d.Segments, new() { UnitToMm = k, ToleranceMm = Tol });
            var slope = SlopeSolver.Solve(net.Segments, new(), Tol);
            int index = net.Segments.Single().Index;
            var original = slope.Pipes[index];
            slope.Pipes[index] = original with { End = new P3(original.End.X, original.End.Y, 2200), PlannedRatio = .05 };
            var planned = PipeRisers.Plan(d, net.Segments, slope, new HashSet<string> { Layer }, k, default, Tol, 3600, -3200);
            check(planned.Risers.Count == 4 && planned.Discarded.Count == 2, "Cuatro verticales: concéntricos agrupados y dos círculos descartados");
            if (planned.Risers.Count != 4) continue;
            var site = planned.Risers.OrderBy(r => r.Base.X).ToArray();
            check(site[0].OuterMm == 110 && site[0].Entities.Count == 2 && site[0].Base == original.Start &&
                site[0].End.Z == -3200, "Concéntricos: exterior menor, extremo exacto y BAJA manda en colgada");
            check(site[1].Base.Z == 2350 && site[1].End.Z == 3600 && site[1].AttachedSegment == index,
                "En interior: cota interpolada y texto SUBE");
            check(site[2].NominalMm == 125 && site[2].End.Z == 3600, "Rótulo de diámetro prevalece; colgada sube al nivel superior");
            check(site[3].AttachedSegment == null && site[3].Base.Z == slope.Networks.Single().ReferenceMm && site[3].End.Z == 3600,
                "Círculo suelto usa la referencia de su red y respeta el texto SUBE");
            var pieces = PipeRisers.Split(net, slope, planned.Risers);
            check(pieces.Count == 1 && slope.Pipes[index].End == site[1].Base &&
                slope.Pipes[pieces[0].Piece.Index].Start == site[1].Base &&
                slope.Pipes[pieces[0].Piece.Index].PlannedRatio == .05, "Corte interior comparte XYZ y conserva pendiente");
            var input = new InventoryInput { Drawing = d, UnitToMm = k };
            foreach (var r in site)
                foreach (int id in r.Entities) input.Circles[id] = (true, "Bajante simulada");
            foreach (var (id, reason) in planned.Discarded) input.Circles[id] = (false, reason);
            var inventory = ModelInventory.Classify(input);
            check(inventory.Balanced && inventory.Entities.Count(e => e.Modeled && e.Type == "CIRCLE") == 5,
                "Inventario: los dos círculos concéntricos tienen destino y no generan un tramo sin justificar");
        }

        CadDrawing Isolated(string file, bool fixture)
        {
            var d = new CadDrawing { Path = file };
            d.Circles.Add(new(new P3(0, 0, 0), 55, Layer, 1));
            if (fixture) d.Blocks.Add(new CadBlockRef { BlockName = "Lavabo" });
            return d;
        }
        var layers = new HashSet<string> { Layer };
        var across = Isolated("Planta.dwg", false);
        across.Circles.Add(new(new P3(2, 0, 0), 65, "Tubería contorno", 2));
        var concentric = PipeRisers.Plan(across, Array.Empty<NetSegment>(), new(),
            new HashSet<string> { Layer, "Tubería contorno" }, 1, default, Tol, null, null);
        check(concentric.Risers.Single().Entities.Count == 2, "Contorno concéntrico en otra capa compatible se representa con la misma vertical");
        var buried = PipeRisers.Plan(Isolated("Red enterrada.dwg", true), Array.Empty<NetSegment>(), new(), layers, 1, default, Tol, null, null);
        check(buried.Risers.Single().Base.Z == -600 && buried.Risers.Single().End.Z == 3000, "Enterrada sube; alternativa de nivel superior SUPUESTA");
        var fixtures = PipeRisers.Plan(Isolated("Planta con aparatos.dwg", true), Array.Empty<NetSegment>(), new(), layers, 1, default, Tol, null, null);
        check(fixtures.Risers.Single().End.Z == -3000, "Planta con aparatos baja; alternativa de nivel inferior SUPUESTA");

        const string chamber = "Arqueta de paso - A1500xL1500xA2800mm";
        var role = BlockNames.Role(chamber, "Saneamiento accesorios");
        check(role == BlockRole.Sanitario && BlockElevations.For(role, chamber) == (0, false) &&
            BlockNames.ChamberDepth(chamber) == (2800, false), "Arqueta sanitaria enrasada, profundidad del nombre");
        check(BlockNames.ChamberDepth("Pozo de bombeo") == (1500, true) &&
            BlockNames.ChamberDepth("Arqueta - 400x400") == (600, true), "Profundidad supuesta para pozo y arqueta");
        role = BlockNames.Role(chamber, "Cuadro_BT_registros");
        check(role == BlockRole.Electrico && ModelScopes.Includes(ModelScope.EquiposPci, role, chamber) &&
            !ModelScopes.Includes(ModelScope.Tuberias, role, chamber), "Arqueta eléctrica va a Equipos y PCI");

        foreach (var (label, service) in new[]
            { ("SAN.FEC", PipeService.Sanitary), ("SAN. PLUV", PipeService.Storm), ("DRENAJE", PipeService.Drain), ("AFS Ø32", PipeService.Cold) })
        {
            var d = new CadDrawing { Path = "Planta sin información.dwg" };
            d.Segments.Add(new CadSegment { Start = default, End = new P3(6000, 0, 0), Layer = Layer });
            d.Texts.Add(new CadText { Layer = Notes, Value = label, Position = new P3(2300, 400, 0) });
            var net = NetworkBuilder.Build(d.Segments, new());
            var choice = PipeNetworks.Infer(d, net.Segments, net.Junctions, 1, default, Tol).Single();
            check(choice.Service == service && choice.Reason == "mayoría de rótulos", $"Sistema por rótulo «{label}»");
            d.Texts.Clear();
            choice = PipeNetworks.Infer(d, net.Segments, net.Junctions, 1, default, Tol).Single();
            check(choice.Service == PipeService.Unknown && choice.Reason.Contains("SUPUESTO"), "Sin pistas, sistema SUPUESTO");
        }
        var fire = new CadDrawing { Path = "PCI_ejemplo.dwg" };
        fire.Segments.Add(new CadSegment { Start = default, End = new P3(6000, 0, 0), Layer = Layer });
        var fireNet = NetworkBuilder.Build(fire.Segments, new());
        fire.Blocks.Add(new CadBlockRef { Layer = "SANR-FIXT", BlockName = "Armario de acometida" });
        PipeService Service() => PipeNetworks.Infer(fire, fireNet.Segments, fireNet.Junctions, 1, default, Tol).Single().Service;
        check(Service() == PipeService.Fire, "Un bloque genérico en capa de aparatos no suplanta la designación PCI del archivo");
        fire.Blocks.Add(new CadBlockRef { Layer = "SANR-FIXT", BlockName = "Lavabo" });
        check(Service() == PipeService.Sanitary, "Un aparato sanitario conectado prevalece sobre el nombre del archivo");

        var catalog = new[] { "Agua caliente", "Sanitario", "Pluvial", "Agua fría" };
        check(PipeNetworks.SelectSystem(PipeService.Drain, catalog) == "Pluvial" &&
            PipeNetworks.SelectSystem(PipeService.Storm, catalog.Take(2)) == "Sanitario" &&
            PipeNetworks.SelectSystem(PipeService.Unknown, catalog) == "Agua fría", "Respaldos semánticos del catálogo; el orden alfabético no decide");
        check(PipeNetworks.DrainageType(new[] { "Acero", "PVC sanitario", "PP evacuación" }, "PP") == "PP evacuación" &&
            PipeNetworks.DrainageType(new[] { "Acero", "PVC sanitario" }, null) == "PVC sanitario", "Tipo de saneamiento: material del rótulo o PVC");
        var mixed = NetworkBuilder.Build(new[]
        {
            new CadSegment { Layer = "Tuberías - enterrada", Start = default, End = new P3(6000, 0, 0) },
            new CadSegment { Layer = "Tuberías - colgada", Start = new P3(0, 3000, 0), End = new P3(6000, 3000, 0) }
        }, new());
        var elevations = SlopeSolver.Solve(mixed.Segments, new(), Tol, isBuriedLayer: l => PipeRisers.IsBuriedLayer(l, false));
        check(elevations.Networks.Select(n => n.ReferenceMm).Order().SequenceEqual(new[] { -600d, 2500d }), "Una misma planta admite redes enterradas y colgadas");
    }

    public static void InspectReal(Action<bool, string> check, CadDrawing d, NetworkResult net, SlopeSolution slopes, double k)
    {
        var layers = d.Circles.Select(c => c.Layer).Concat(net.Segments.Select(s => s.Layer))
            .Where(l => PipeNetworks.LayerKind(l) == MepKind.Tuberia).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var choices = PipeNetworks.Infer(d, net.Segments, net.Junctions, k, default, Tol);
        var plan = PipeRisers.Plan(d, net.Segments, slopes, layers, k, default, Tol, null, null);
        var splits = PipeRisers.Split(net, slopes, plan.Risers);
        var validCuts = net.Segments.SelectMany(p => new[] { slopes.Pipes[p.Index].Start, slopes.Pipes[p.Index].End }).ToHashSet();
        var attached = plan.Risers.Count(r => r.AttachedSegment != null);
        var services = choices.Select(n => n.Service).Concat(plan.Risers.Where(r => r.AttachedSegment == null)
            .Select(r => PipeNetworks.InferLoose(d, 0, r.Layer, PipeNetworks.InPlan(r.Base), k, default, Tol).Service));
        var chambers = d.Blocks.Count(b => b.Role == BlockRole.Sanitario && BlockNames.IsDrainageChamber(b.BlockName));
        var electrical = d.Blocks.Count(b => b.Role == BlockRole.Electrico && BlockNames.IsElectricalChamber(b.BlockName, b.Layer));
        int circles = d.Circles.Count(c => layers.Contains(c.Layer));
        string file = Path.GetFileName(d.Path);
        if (circles > 0 || chambers > 0 || electrical > 0)
            Console.WriteLine($"BAJANTES {file}: círculos tubería {circles}; representados {plan.Risers.Sum(r => r.Entities.Count)}; " +
                $"verticales {plan.Risers.Count} ({attached} con eje, {plan.Risers.Count - attached} sueltas, " +
                $"{plan.Risers.Count(r => r.End.Z > r.Base.Z)} suben, {plan.Risers.Count(r => r.End.Z < r.Base.Z)} bajan); " +
                $"cortes interiores {splits.Count}; desagües {chambers}; arquetas eléctricas {electrical}; " +
                "redes " + string.Join(", ", services.GroupBy(s => s).OrderBy(g => g.Key).Select(g => $"{g.Key}={g.Count()}")));
        check(plan.Risers.All(r => r.Base.X == r.End.X && r.Base.Y == r.End.Y && r.Base.Z != r.End.Z &&
            (r.AttachedSegment == null || validCuts.Contains(r.Base))), file + ": verticales con XYZ compartido donde hay eje");
        check(net.Junctions.All(j => j.Ends.Select(e => e.AtStart ? slopes.Pipes[e.Segment].Start : slopes.Pipes[e.Segment].End).Distinct().Count() == 1),
            file + ": nudos XYZ coinciden después de los cortes");
    }
}
