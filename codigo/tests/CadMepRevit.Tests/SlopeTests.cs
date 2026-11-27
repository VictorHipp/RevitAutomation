using ACadSharp;
using ACadSharp.Entities;
using ACadSharp.IO;
using ACadSharp.Tables;
using CadMepRevit.Core;
using CSMath;

public static class SlopeTests
{
    public static void RunReal(Action<bool, string> check, string folder, string output)
    {
        Directory.CreateDirectory(output);
        var files = Directory.GetFiles(folder, "*.dwg", SearchOption.AllDirectories).Order().ToList();
        check(files.Count > 0, "Planos reales disponibles para pendientes");
        foreach (var path in files)
        {
            try
            {
                var drawing = CadReader.Read(path);
                double k = DrawingUnits.ToMillimeters(drawing.HeaderUnit ?? DrawingUnit.Milimetros);
                var byLayer = LayerPlanning.ByLayer(drawing);
                var layers = byLayer.Where(g => PipeNetworks.LayerKind(g.Key) == MepKind.Tuberia && LayerPlanning.CenterlineDuplicateOf(g.Key, drawing.SegmentLayers) == null)
                    .Select(g => (g.Key, MepKind.Tuberia, LayerPlanning.LooksDoubleLine(g.ToList(), MepKind.Tuberia, k, PlanDefaults.ToleranceMm))).ToList();
                var network = NetworkBuilder.Build(LayerPlanning.NetworkInput(byLayer, layers, k, PlanDefaults.ToleranceMm, null),
                    new NetworkBuilder.Settings { UnitToMm = k, ToleranceMm = PlanDefaults.ToleranceMm, MinLengthMm = PlanDefaults.MinLengthMm,
                        TransitionHints = LayerPlanning.TransitionHints(drawing) });
                var association = SlopeAnnotations.Associate(drawing, network.Segments, k, default, PlanDefaults.ToleranceMm);
                bool documentBuried = SlopeSolver.IsBuried(drawing, Array.Empty<string>());
                var solution = SlopeSolver.Solve(network.Segments, association, PlanDefaults.ToleranceMm,
                    isBuriedLayer: layer => PipeRisers.IsBuriedLayer(layer, documentBuried));
                string name = Path.GetFileName(path);
                Console.WriteLine($"PLANO {name}: {network.Segments.Count} tramos; etiquetas {association.Labels.Count}; asociadas {association.Associated}; con pendiente {solution.SlopingRuns}; sin etiqueta {solution.UnlabelledRuns}; conflictos {solution.Networks.Count(n => n.Conflict)}");
                if (name.Contains("RED COLGADA")) check(association.Labels.Count == 30 && association.Associated == 30, "RED COLGADA: 30/30 etiquetas");
                else if (name.Contains("RED ENTERRADA")) check(association.Labels.Count == 70 && association.Associated == 70, "RED ENTERRADA: 70/70 etiquetas");
                else check(solution.Pipes.Values.All(p => p.PlannedRatio == 0), name + ": 0 pendientes inventadas");
                check(network.Junctions.All(j => j.Ends.Select(e => e.AtStart ? solution.Pipes[e.Segment].Start : solution.Pipes[e.Segment].End).Distinct().Count() == 1), name + ": extremos XYZ compartidos, diferencia 0 mm");
                var input = new InventoryInput { Drawing = drawing, UnitToMm = k,
                    Created = network.Segments.Select(s => new ModeledRun(s.Layer, s.Start, s.End, (s.WidthMm ?? 0) / 2, "Simulada sin unión")).ToList() };
                var inventory = ModelInventory.Classify(input);
                check(inventory.Total == inventory.Modeled + inventory.NotModeled && inventory.MissingOrRepeated == 0, name + ": inventario = modeladas + no modeladas sin piezas de unión");
                PipeFeatureTests.InspectReal(check, drawing, network, solution, k);
                File.WriteAllText(Path.Combine(output, Path.GetFileNameWithoutExtension(path) + "_PENDIENTES.txt"), solution.Report());
            }
            catch (CadReadException ex) { check(false, "Plano real ilegible: " + path + ": " + ex.Message); }
        }
    }

    public static void Run(Action<bool, string> check, string folder)
    {
        Directory.CreateDirectory(folder);
        foreach (string text in new[] { "2%", "i=1,5 %", "2‰", "P=1,5%", "Pte. 2%", "pendiente 2%", "DN110 2%" })
            check(SlopeAnnotations.ParseRatio(text) is > 0, "Formato de pendiente: " + text);
        foreach (string shape in new[] { "líneas", "triángulo", "SOLID", "HATCH", "ancho", "LEADER", "MLEADER", "bloque" })
            foreach (double k in new[] { 1.0, 1000.0 })
            {
                var doc = new CadDocument();
                doc.Header.InsUnits = k == 1 ? ACadSharp.Types.Units.UnitsType.Millimeters : ACadSharp.Types.Units.UnitsType.Meters;
                var pipeLayer = new Layer("Q7"); var noteLayer = new Layer("N8");
                doc.Layers.Add(pipeLayer); doc.Layers.Add(noteLayer);
                XYZ P(double x, double y) => new(x / k, y / k, 0);
                Line Edge(double x, double y, double xx, double yy) => new(P(x, y), P(xx, yy)) { Layer = noteLayer };
                bool rotated = shape == "bloque";
                doc.Entities.Add(new Line(P(0, 0), rotated ? P(0, 3000) : P(3000, 0)) { Layer = pipeLayer });
                if (shape != "MLEADER") doc.Entities.Add(new MText { Value = "i=1,5 %", InsertPoint = rotated ? P(-500, 750) : P(750, 500),
                    Height = 100 / k, Layer = noteLayer, AlignmentPoint = rotated ? new XYZ(0, 1, 0) : new XYZ(1, 0, 0) });
                if (shape is "líneas" or "triángulo" or "SOLID" or "HATCH") doc.Entities.Add(Edge(500, 300, 1500, 300));
                if (shape == "líneas")
                {
                    doc.Entities.Add(Edge(1500, 300, 1250, 400)); doc.Entities.Add(Edge(1500, 300, 1250, 200));
                }
                if (shape == "triángulo")
                {
                    var poly = new LwPolyline { Layer = noteLayer, IsClosed = true };
                    foreach (var p in new[] { P(1500, 300), P(1250, 400), P(1250, 200) }) poly.Vertices.Add(new LwPolyline.Vertex(new XY(p.X, p.Y)));
                    doc.Entities.Add(poly);
                }
                if (shape == "SOLID") doc.Entities.Add(new Solid(P(1500, 300), P(1250, 400), P(1250, 200)) { Layer = noteLayer });
                if (shape == "HATCH")
                {
                    var h = new Hatch { Layer = noteLayer };
                    h.Paths.Add(new Hatch.BoundaryPath(new Entity[] { Edge(1500, 300, 1250, 400), Edge(1250, 400, 1250, 200), Edge(1250, 200, 1500, 300) }));
                    doc.Entities.Add(h);
                }
                if (shape == "ancho")
                {
                    var poly = new LwPolyline { Layer = noteLayer };
                    poly.Vertices.Add(new LwPolyline.Vertex(new XY(500 / k, 300 / k)) { StartWidth = 200 / k, EndWidth = 0 });
                    poly.Vertices.Add(new LwPolyline.Vertex(new XY(1500 / k, 300 / k)));
                    doc.Entities.Add(poly);
                }
                if (shape == "LEADER") doc.Entities.Add(new Leader { Layer = noteLayer, Vertices = new List<XYZ> { P(1500, 300), P(500, 300) } });
                if (shape == "MLEADER")
                {
                    var ml = new MultiLeader { Layer = noteLayer };
                    ml.ContextData.HasTextContents = true;
                    ml.ContextData.TextLabel = "i=1,5 %";
                    ml.ContextData.TextLocation = P(750, 500);
                    ml.ContextData.TextHeight = 100 / k;
                    var line = new ACadSharp.Objects.MultiLeaderObjectContextData.LeaderLine();
                    line.Points.Add(P(1500, 300)); line.Points.Add(P(500, 300));
                    var leaderRoot = new ACadSharp.Objects.MultiLeaderObjectContextData.LeaderRoot();
                    leaderRoot.Lines.Add(line);
                    ml.ContextData.LeaderRoots.Add(leaderRoot);
                    doc.Entities.Add(ml);
                }
                if (shape == "bloque")
                {
                    var block = new BlockRecord("geometria_libre");
                    block.Entities.Add(Edge(500, 300, 1500, 300)); block.Entities.Add(Edge(1500, 300, 1250, 400)); block.Entities.Add(Edge(1500, 300, 1250, 200));
                    doc.BlockRecords.Add(block);
                    doc.Entities.Add(new Insert(block) { Layer = noteLayer, Rotation = Math.PI / 2 });
                }
                string path = Path.Combine(folder, shape + "_" + k + ".dxf");
                DxfWriter.Write(path, doc, false);
                var drawing = CadReader.Read(path);
                var net = NetworkBuilder.Build(drawing.Segments.Where(s => s.Layer == pipeLayer.Name), new NetworkBuilder.Settings { UnitToMm = k });
                var assoc = SlopeAnnotations.Associate(drawing, net.Segments, k, default, 10);
                var sol = SlopeSolver.Solve(net.Segments, assoc, 10);
                check(assoc.Associated == 1 && sol.Pipes.Values.Single().Start.Z > sol.Pipes.Values.Single().End.Z,
                    $"DXF flecha {shape}, unidad {(k == 1 ? "mm" : "m")}: lectura, asociación y descenso (asociadas {assoc.Associated}; {string.Join(";", assoc.Labels.Select(l => l.Reason))})");
                check(sol.Pipes.Values.All(p => Math.Abs(p.Start.Z - 2500) < 1e-6 && Math.Abs(p.End.Z - 2455) < 1e-6), $"{shape}: mismas cotas exactas en m y mm (2500 → 2455 mm)");
            }

        NetworkResult Net(params (P3 A, P3 B)[] edges) => NetworkBuilder.Build(edges.Select(e => new CadSegment { Layer = "Q7", Start = e.A, End = e.B }), new NetworkBuilder.Settings());
        P3 P0(double x, double y) => new(x, y, 0);
        SlopeLabel Vote(NetSegment s, P3 direction, double ratio = .02, double fraction = .5) => new() { Segment = s.Index,
            Downstream = direction, Ratio = ratio, PositionMm = s.Start + (s.End - s.Start) * fraction, Text = new CadText { Value = (ratio * 100) + "%" } };
        var l = Net((P0(0, 0), P0(1000, 0)), (P0(1000, 0), P0(1000, 1000)));
        var la = new SlopeAssociation(); la.Labels.Add(Vote(l.Segments[0], P0(1, 0)));
        var ls = SlopeSolver.Solve(l.Segments, la, 10);
        check(Math.Abs(ls.Pipes[l.Segments[0].Index].Start.Z - 2500) < 1e-6 && Math.Abs(ls.Pipes[l.Segments[1].Index].End.Z - 2460) < 1e-6,
            "Red L: 2 % en todo el recorrido (40 mm de caída)");
        check(ls.Pipes[l.Segments[0].Index].End == ls.Pipes[l.Segments[1].Index].Start, "L: extremo compartido XYZ exacto");
        var v = new SlopeAssociation();
        v.Labels.AddRange(new[] { Vote(l.Segments[0], P0(1, 0)), Vote(l.Segments[0], P0(1, 0)), Vote(l.Segments[1], P0(0, 1)), Vote(l.Segments[1], P0(0, -1)) });
        var vs = SlopeSolver.Solve(l.Segments, v, 10);
        check(vs.SlopingRuns == 1 && vs.Warnings.Any(w => w.Contains("aislado")), "Votos 3/1: descarta aislado y avisa");
        v.Labels.RemoveRange(0, 2);
        var tie = SlopeSolver.Solve(l.Segments, v, 10);
        check(tie.Pipes.Values.All(p => p.Start.Z == p.End.Z), "Votos 1/1: horizontal");
        var t = Net((P0(0, 0), P0(2000, 0)), (P0(1000, 0), P0(1000, 1000)));
        var ta = new SlopeAssociation();
        foreach (var s in t.Segments.Where(s => Math.Abs(s.Direction.X) > .9)) ta.Labels.Add(Vote(s, P0(1, 0)));
        var ts = SlopeSolver.Solve(t.Segments, ta, 10);
        check(ts.UnlabelledRuns == 1 && ts.Pipes[t.Segments.Single(s => Math.Abs(s.Direction.Y) > .9).Index].PlannedRatio == 0, "T: ramal sin etiqueta horizontal");
        var joint = t.Junctions.Single();
        var jointPoints = joint.Ends.Select(e => e.AtStart ? ts.Pipes[e.Segment].Start : ts.Pipes[e.Segment].End).ToList();
        check(jointPoints.All(p => p == jointPoints[0]), "T: cota única en nudo y extremos coincidentes");
        ta.Labels.Clear();
        foreach (var s in t.Segments) ta.Labels.Add(Vote(s, ((s.Start + s.End) * .5 - joint.Point).Normalized()));
        check(SlopeSolver.Solve(t.Segments, ta, 10).Networks.All(n => n.Conflict), "T con todos los ramales saliendo: red horizontal");
        var ring = Net((P0(0, 0), P0(1000, 0)), (P0(1000, 0), P0(1000, 1000)), (P0(1000, 1000), P0(0, 1000)), (P0(0, 1000), P0(0, 0)));
        var ra = new SlopeAssociation(); ra.Labels.Add(Vote(ring.Segments[0], ring.Segments[0].Direction));
        var rs = SlopeSolver.Solve(ring.Segments, ra, 10);
        check(rs.Networks.Single().Conflict && rs.Pipes.Values.All(p => Math.Abs(p.Start.Z - p.End.Z) < 1e-9), "Anillo que no cierra: sus recorridos quedan horizontales");
        // El conflicto solo afecta a los recorridos del anillo: un ramal etiquetado de la misma red conserva su pendiente.
        //   Ramal que DESAGUA en el anillo (flecha hacia él), así el nudo de unión no tiene dos salidas.
        var ringTail = Net((P0(0, 0), P0(1000, 0)), (P0(1000, 0), P0(1000, 1000)), (P0(1000, 1000), P0(0, 1000)), (P0(0, 1000), P0(0, 0)),
                           (P0(1000, 0), P0(4000, 0)));
        var rta = new SlopeAssociation();
        rta.Labels.Add(Vote(ringTail.Segments.Single(s => s.Start.DistanceTo(P0(0, 0)) < 1 && s.End.DistanceTo(P0(1000, 0)) < 1 ||
                                                          s.End.DistanceTo(P0(0, 0)) < 1 && s.Start.DistanceTo(P0(1000, 0)) < 1), P0(1, 0)));
        var tail = ringTail.Segments.Single(s => Math.Max(s.Start.X, s.End.X) > 3999);
        rta.Labels.Add(Vote(tail, P0(-1, 0)));
        var rts = SlopeSolver.Solve(ringTail.Segments, rta, 10);
        check(rts.Networks.Single().Conflict && rts.Pipes[tail.Index].PlannedRatio > 0 &&
              ringTail.Segments.Where(s => s.Index != tail.Index).All(s => rts.Pipes[s.Index].PlannedRatio == 0),
              "Anillo que no cierra: el ramal etiquetado fuera del anillo conserva su pendiente");
        var both = new CadDrawing { Path = "PLANTA SOTANO.dwg" };
        both.Texts.Add(new CadText { Value = "TUBERÍA DE FECALES ENTERRADAS" }); both.Texts.Add(new CadText { Value = "TUBERÍA DE FECALES COLGANTES" });
        check(!SlopeSolver.IsBuried(both, new[] { "P-PIPE" }), "Leyenda con red enterrada y colgada: no decide, colgada por defecto");
        var onlyBuried = new CadDrawing { Path = "PLANTA SOTANO.dwg" };
        onlyBuried.Texts.Add(new CadText { Value = "RED ENTERRADA" });
        check(SlopeSolver.IsBuried(onlyBuried, new[] { "P-PIPE" }), "Solo textos de red enterrada: enterrada");
        foreach (var pair in new[] { (Text: "h: -0.62 m", Expected: -620.0), (Text: "C.I. 1,20", Expected: 1255.0), (Text: "Z=+2,50", Expected: 2500.0), (Text: "CT 1250 mm", Expected: 1250.0) })
        {
            var d = new CadDrawing(); d.Texts.Add(new CadText { Value = pair.Text, Position = P0(500, 200), Height = 100 });
            var n = Net((P0(0, 0), P0(1000, 0)));
            var ar = SlopeAnnotations.Associate(d, n.Segments, 1, default, 10, _ => 110);
            check(ar.References.Count == 1 && Math.Abs(ar.References[0].AxisMm - pair.Expected) < 1e-6, "Cota de referencia: " + pair.Text);
            check(Math.Abs(SlopeSolver.Solve(n.Segments, ar, 10).Pipes[0].Start.Z - pair.Expected) < 1e-6, "Referencia aplicada al eje: " + pair.Text);
        }
        var legend = new CadDrawing(); legend.Texts.Add(new CadText { Value = "100%", Position = P0(500, 100) });
        var legendAssoc = SlopeAnnotations.Associate(legend, l.Segments, 1, default, 10);
        check(legendAssoc.Associated == 0 && legendAssoc.Labels.Single().Reason.Contains("fuera de rango"), "Leyenda 100 % sin flecha: no se usa");
        check(SizeAnnotations.ParseLabel("DN110 2%")?.ValueMm == 110 && SlopeAnnotations.ParseRatio("DN110 2%") == .02, "Texto mixto DN110 2 % alimenta ambos lectores");
        var noArrow = new CadDrawing(); noArrow.Texts.Add(new CadText { Value = "2%", Position = P0(500, 100), Height = 100 });
        var noArrowAssociation = SlopeAnnotations.Associate(noArrow, l.Segments, 1, default, 10);
        check(noArrowAssociation.Associated == 0 && noArrowAssociation.Labels.Single().Reason.Contains("sin flecha") &&
            SlopeSolver.Solve(l.Segments, noArrowAssociation, 10).Pipes.Values.All(p => p.PlannedRatio == 0), "Pendiente cercana sin flecha: horizontal y aviso");
        check(SlopeSolver.Solve(l.Segments, new SlopeAssociation(), 10, true).Pipes.Values.Max(p => p.Start.Z) == PlanDefaults.BuriedTopAxisMm,
            "Red enterrada: referencia SUPUESTA -600 mm");
        check(ModelScopes.AllowsFitting(ModelScope.Tuberias, MepKind.Tuberia, JunctionKind.Codo) &&
            !ModelScopes.AllowsFitting(ModelScope.Tuberias, MepKind.Tuberia, JunctionKind.Cruz) &&
            ModelScopes.AllowsFitting(ModelScope.Conductos, MepKind.Conducto, JunctionKind.Cruz) &&
            ModelScopes.AllowsFitting(ModelScope.Bandejas, MepKind.Bandeja, JunctionKind.Te) &&
            new[] { JunctionKind.Codo, JunctionKind.Te, JunctionKind.Cruz, JunctionKind.Transicion }
                .All(j => ModelScopes.AllowsFitting(ModelScope.Bandejas, MepKind.TuboElectrico, j)) &&
            !ModelScopes.AllowsAnyFitting(ModelScope.Tuberias, MepKind.TuboElectrico),
            "Plan: tuberías con codos sin cruces, conductos con cruces, bandejas con tes y tubos eléctricos (en Bandejas) con codos, tes, cruces y reducciones");
        var mixed = new SlopeAssociation();
        mixed.Labels.Add(Vote(l.Segments[0], P0(1, 0), .01)); mixed.Labels.Add(Vote(l.Segments[1], P0(0, 1), .03));
        var mixedResult = SlopeSolver.Solve(l.Segments, mixed, 10);
        check(mixedResult.Pipes[0].PlannedRatio == .01 && mixedResult.Pipes[1].PlannedRatio == .03 && mixedResult.Warnings.Any(w => w.Contains("distintos")), "Valores distintos: cada tramo conserva su etiqueta y se avisa");
        var twoAgainst = new SlopeAssociation();
        twoAgainst.Labels.AddRange(Enumerable.Repeat(Vote(l.Segments[0], P0(1, 0)), 4));
        twoAgainst.Labels.AddRange(Enumerable.Repeat(Vote(l.Segments[1], P0(0, -1)), 2));
        check(SlopeSolver.Solve(l.Segments, twoAgainst, 10).Pipes.Values.All(p => p.PlannedRatio == 0), "Más de un voto contrario: horizontal aunque haya mayoría");
        var refs = new SlopeAssociation(); refs.Labels.Add(Vote(l.Segments[0], P0(1, 0)));
        refs.References.Add(new ElevationReference(0, P0(0, 0), 1000, "aguas arriba"));
        refs.References.Add(new ElevationReference(1, P0(1000, 1000), 500, "aguas abajo"));
        var referenced = SlopeSolver.Solve(l.Segments, refs, 10);
        check(referenced.Pipes[1].End.Z == 500 && referenced.Pipes[0].Start.Z == 540 && referenced.Warnings.Any(w => w.Contains("1000") && w.Contains("540")),
            "Varias cotas: manda la de aguas abajo, las demás se comprueban con ambas cifras");
        var sizesDrawing = new CadDrawing();
        sizesDrawing.Texts.Add(new CadText { Value = "DN50 2%", Position = P0(500, 100), Height = 100 });
        sizesDrawing.Segments.Add(new CadSegment { Layer = "Q7", Start = P0(0, -55), End = P0(1000, -55) });
        sizesDrawing.Segments.Add(new CadSegment { Layer = "Q7", Start = P0(0, 55), End = P0(1000, 55) });
        sizesDrawing.Dimensions.Add(new CadDimension { Kind = "alineada", Measurement = 110, FirstPoint = P0(500, -55), SecondPoint = P0(500, 55), Direction = P0(0, 1) });
        var sizes = SizeAnnotations.Associate(sizesDrawing, l.Segments.Select(s => new SizeTarget { Id = s.Index.ToString(), Layer = s.Layer, Start = s.Start, End = s.End, Shape = SizeShape.Redonda }).ToList(), new SizeAnnotations.Settings());
        check(sizes.Decisions.Count == 2 && sizes.Decisions.Values.All(d => d.ValueMm == 110), "DIMENSION manda sobre DN y se propaga por el recorrido antes de modelar");
        var ambiguous = new CadDrawing(); ambiguous.Texts.Add(new CadText { Value = "2%", Position = P0(500, 100), Height = 100 });
        ambiguous.Arrows.Add(new CadArrow("N8", P0(0, 200), P0(1000, 200), "sintética"));
        var parallel = Net((P0(0, 0), P0(1000, 0)), (P0(0, 400), P0(1000, 400)));
        // Texto también equidistante: no existe evidencia adicional que resuelva el empate.
        ambiguous.Texts.Clear(); ambiguous.Texts.Add(new CadText { Value = "2%", Position = P0(500, 200), Height = 100 });
        check(SlopeAnnotations.Associate(ambiguous, parallel.Segments, 1, default, 10).Associated == 0, "Dos tuberías equidistantes: no se inventa una asociación");
    }
}
