using CadMepRevit.Core;
using System.Text.Json;
using System.Security.Cryptography;
using System.Text;

var root = Environment.GetEnvironmentVariable("CADMEP_PLANOS") ?? throw new Exception("Defina CADMEP_PLANOS");
if (args.Contains("--synthetic"))
{
    int errors = 0;
    SlopeTests.Run((ok, label) => { Console.WriteLine((ok ? "OK " : "FALLO ") + label); if (!ok) errors++; }, Path.Combine("Pruebas", "PendientesSinteticas"));
    return errors == 0 ? 0 : 1;
}
var signatures = new SortedDictionary<string, ScopeSignature>();
bool snapshot = args.Contains("--snapshot");
bool legacyStats = args.Contains("--legacy-stats");
var drawings = new Dictionary<string, CadDrawing>();
var files = Directory.GetFiles(root, "*.dwg", SearchOption.AllDirectories).Where(p => !args.Contains("--san") || p.Contains("RED ")).Order().ToList();
foreach (var path in files)
{
    try
    {
        var d = CadReader.Read(path);
        drawings[path] = d;
        double k = DrawingUnits.ToMillimeters(d.HeaderUnit ?? DrawingUnit.Milimetros);
        var by = LayerPlanning.ByLayer(d);
        foreach (var scope in Enum.GetValues<ModelScope>().Where(s => s != ModelScope.Tuberias))
        {
            var layers = by.Select(g => (Layer: g.Key, Kind: MepKinds.Suggest(g.Key)))
                .Where(g => ModelScopes.Includes(scope, g.Kind)).Select(g => (g.Layer, g.Kind,
                    LayerPlanning.LooksDoubleLine(by[g.Layer].ToList(), g.Kind, k, PlanDefaults.ToleranceMm))).ToList();
            var net = NetworkBuilder.Build(LayerPlanning.NetworkInput(by, layers, k, PlanDefaults.ToleranceMm, null),
                new NetworkBuilder.Settings { UnitToMm = k, ToleranceMm = PlanDefaults.ToleranceMm, MinLengthMm = PlanDefaults.MinLengthMm,
                    TransitionHints = LayerPlanning.TransitionHints(d) });
            bool ElectricalChamber(CadBlockRef b) => scope == ModelScope.EquiposPci && b.Role == BlockRole.Electrico &&
                b.BlockName.Contains("ARQUETA", StringComparison.OrdinalIgnoreCase);
            var selected = d.Blocks.Where(b => ModelScopes.Includes(scope, b.Role, b.BlockName)).ToList();
            var data = JsonSerializer.Serialize(new { d.Entities, d.Segments, d.Texts, d.Dimensions,
                Blocks = d.Blocks.Select(b => new { b.EntityIndex, b.BlockName, b.Layer, b.Position, b.Footprint, b.RotationRadians }),
                NetSegments = net.Segments, Junctions = net.Junctions,
                Selected = selected.Where(b => !ElectricalChamber(b)) });
            signatures[Path.GetRelativePath(root, path) + "|" + scope] = new ScopeSignature(
                Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(data))),
                selected.Where(ElectricalChamber).Select(b => $"{b.EntityIndex}/{b.Layer}/{b.BlockName}").ToArray());
        }
    }
    catch (CadReadException e) { signatures[Path.GetRelativePath(root, path)] = new ScopeSignature(e.Message, Array.Empty<string>()); }
}
if (snapshot)
{
    File.WriteAllText(args[1], JsonSerializer.Serialize(signatures, new JsonSerializerOptions { WriteIndented = true }));
    Console.WriteLine($"Firmas guardadas: {signatures.Count}");
    if (!legacyStats) return 0;
}
var baseline = snapshot ? new SortedDictionary<string, ScopeSignature>()
    : JsonSerializer.Deserialize<SortedDictionary<string, ScopeSignature>>(File.ReadAllText(args[0]))!;
int failures = 0;
void Check(bool ok, string label) { Console.WriteLine((ok ? "OK " : "FALLO ") + label); if (!ok) failures++; }
if (!snapshot && !args.Contains("--san"))
{
    Check(baseline.Keys.SequenceEqual(signatures.Keys), $"Mismos {signatures.Count} planos y botones antes y después");
    int delta = 0;
    foreach (var (key, current) in signatures)
    {
        if (!baseline.TryGetValue(key, out var old)) continue;
        if (old.Hash != current.Hash) Check(false, "Firma distinta: " + key);
        foreach (var added in current.ElectricalChambers.Except(old.ElectricalChambers))
        {
            delta++;
            Console.WriteLine($"EXCEPCIÓN {key}: {added}");
        }
        if (old.ElectricalChambers.Except(current.ElectricalChambers).Any()) Check(false, "Se perdió una arqueta eléctrica: " + key);
    }
    Check(delta > 0, $"Única excepción en {signatures.Count} firmas: {delta} arquetas eléctricas añadidas a Equipos y PCI");
}
foreach (var path in files)
{
    try
    {
        var d = drawings[path];
        double k = DrawingUnits.ToMillimeters(d.HeaderUnit ?? DrawingUnit.Milimetros);
        var by = LayerPlanning.ByLayer(d);
        var layers = by.Where(g => MepKinds.Suggest(g.Key) == MepKind.Tuberia)
            .Where(g => LayerPlanning.CenterlineDuplicateOf(g.Key, d.SegmentLayers) == null)
            .Select(g => (g.Key, MepKind.Tuberia, LayerPlanning.LooksDoubleLine(g.ToList(), MepKind.Tuberia, k, PlanDefaults.ToleranceMm))).ToList();
        var net = NetworkBuilder.Build(LayerPlanning.NetworkInput(by, layers, k, PlanDefaults.ToleranceMm, null),
            new NetworkBuilder.Settings { UnitToMm = k, ToleranceMm = PlanDefaults.ToleranceMm, MinLengthMm = PlanDefaults.MinLengthMm,
                TransitionHints = LayerPlanning.TransitionHints(d) });
        if (legacyStats)
        {
            var selected = d.Blocks.Where(b => ModelScopes.Includes(ModelScope.Tuberias, b.Role, b.BlockName)).ToList();
            var catalog = new[] { "Agua caliente", "Agua fría", "Retorno agua caliente", "Sanitario", "Pluvial", "Drenaje", "Incendios" };
            var targets = net.Segments.Select(s => new SizeTarget
            {
                Id = s.Index.ToString(System.Globalization.CultureInfo.InvariantCulture),
                Layer = s.Layer, Start = s.Start, End = s.End, Shape = SizeShape.Redonda
            }).ToList();
            var sizes = SizeAnnotations.Associate(d, targets, new SizeAnnotations.Settings { UnitToMm = k, ToleranceMm = PlanDefaults.ToleranceMm });
            var counts = NetworkComponents.Find(net.Segments.Select(s => s.Index), net.Junctions).Select(ids =>
            {
                var votes = ids.Select(i => sizes.Decisions.GetValueOrDefault(i.ToString(System.Globalization.CultureInfo.InvariantCulture))?
                    .From.FirstOrDefault(f => f.Prefix != null)?.Prefix).Where(p => p != null)
                    .GroupBy(p => p).OrderByDescending(g => g.Count()).FirstOrDefault();
                string? Match(string? value)
                {
                    string input = (value ?? "").ToUpperInvariant();
                    if (input.Contains("RACS") || input.Contains("RECIRC")) return catalog[2];
                    if (input.Contains("ACS") || input.Contains("CALIENTE")) return catalog[0];
                    if (input.Contains("AF")) return catalog[1];
                    if (input.Contains("SANEAM") || input.Contains("FECAL") || input.Contains("RESIDUAL")) return catalog[3];
                    if (input.Contains("PLUVIAL") || input.Contains("STORM")) return catalog[4];
                    if (input.Contains("PCI") || input.Contains("INCEND") || input.Contains("BIE")) return catalog[6];
                    return null;
                }
                return Match(votes?.Key) ?? Match(net.Segments.First(s => s.Index == ids[0]).Layer) ?? catalog[0];
            }).GroupBy(s => s).Select(g => $"{g.Key}={g.Count()}");
            int chambers = selected.Count(b => System.Text.RegularExpressions.Regex.IsMatch(BlockNames.Clean(b.BlockName),
                @"ARQUETA|SUMIDERO|SIF[OÓ]N|CANALETA|SEPARADOR|ARENERO|MANHOLE|FLOOR DRAIN|(?<![A-Z])(?:BOTE|POZO)(?![A-Z])|POZODEBOMBEO",
                System.Text.RegularExpressions.RegexOptions.IgnoreCase));
            Console.WriteLine($"ANTES {Path.GetFileName(path)}: {net.Segments.Count} tramos; 0 verticales; {chambers} desagües; redes " + string.Join(", ", counts));
        }
        var assoc = SlopeAnnotations.Associate(d, net.Segments, k, default, PlanDefaults.ToleranceMm);
        if (args.Contains("--san"))
        {
            foreach (var text in d.Texts.Where(t => t.Value.Contains("enterrad", StringComparison.OrdinalIgnoreCase))) Console.WriteLine("TEXTO ENTERRADA: " + text.Value);
            foreach (var layer in layers)
            {
                var outline = OutlineDetector.Detect(by[layer.Key], OutlineDetector.Settings.ForPipes(k, PlanDefaults.ToleranceMm));
                Console.WriteLine($"CONTORNO {layer.Key}: doble={layer.Item3}; emparejado {outline.PairedFraction}; tapas {outline.Caps}; ejes {outline.Centerlines.Count}; líneas {outline.InputLines}");
            }
            foreach (var label in assoc.Labels.Where(l => l.Segment == null))
            {
                Console.WriteLine($"DUDOSO {label.Text.Value} posición {label.PositionMm}; rotación {label.Text.RotationRad}");
                foreach (var arrow in SlopeAnnotations.FindArrows(d, k).Select(ar => (Ar: ar, D: NetworkBuilder.ProjectToSegment(label.PositionMm, ar.Tail * k, ar.Tip * k).Dist)).Where(ar => ar.D < 1000).OrderBy(ar => ar.D).Take(4))
                {
                    Console.WriteLine($"  FLECHA d={arrow.D}, {arrow.Ar.Tail * k} -> {arrow.Ar.Tip * k}, {arrow.Ar.Source}");
                    foreach (var text in d.Texts.Where(t => SlopeAnnotations.ParseRatio(t.Value) != null).OrderBy(t => NetworkBuilder.ProjectToSegment(t.Center * k, arrow.Ar.Tail * k, arrow.Ar.Tip * k).Dist).Take(3))
                        Console.WriteLine($"    TEXTO {text.Value}, {text.Center * k}, d={NetworkBuilder.ProjectToSegment(text.Center * k, arrow.Ar.Tail * k, arrow.Ar.Tip * k).Dist}, rot={text.RotationRad}");
                }
                foreach (var segment in net.Segments.OrderBy(s => NetworkBuilder.ProjectToSegment(label.PositionMm, s.Start, s.End).Dist).Take(6))
                    Console.WriteLine($"  TUBO {segment.Index}: {segment.Start} -> {segment.End}; ancho {segment.WidthMm}");
            }
        }
        var sol = SlopeSolver.Solve(net.Segments, assoc, PlanDefaults.ToleranceMm, SlopeSolver.IsBuried(d, layers.Select(l => l.Key)));
        Console.WriteLine($"PLANO {Path.GetFileName(path)}: {net.Segments.Count} tramos; etiquetas {assoc.Labels.Count}; asociadas {assoc.Associated}; con pendiente {sol.SlopingRuns}; sin etiqueta {sol.UnlabelledRuns}; conflictos {sol.Networks.Count(n => n.Conflict)}");
        foreach (var l in assoc.Labels.Where(l => l.Segment == null)) Console.WriteLine($"  {l.Text.Value} {l.PositionMm}: {l.Reason}");
        if (Path.GetFileName(path).Contains("RED COLGADA")) Check(assoc.Labels.Count == 30 && assoc.Associated == 30, "RED COLGADA 30/30");
        else if (Path.GetFileName(path).Contains("RED ENTERRADA")) Check(assoc.Labels.Count == 70 && assoc.Associated == 70, "RED ENTERRADA 70/70");
        else Check(sol.Pipes.Values.All(p => p.PlannedRatio == 0), "Resto de planos: cero pendientes inventadas");
        foreach (var j in net.Junctions)
        {
            var points = j.Ends.Select(end => end.AtStart ? sol.Pipes[end.Segment].Start : sol.Pipes[end.Segment].End).ToList();
            Check(points.All(p => p == points[0]), $"Nudo {j.Point}: extremos idénticos");
        }
        File.WriteAllText(Path.Combine(Path.GetDirectoryName(args[0])!, Path.GetFileNameWithoutExtension(path) + "_PENDIENTES.txt"), sol.Report());
    }
    catch (CadReadException ex) { Console.WriteLine("Plano ilegible: " + ex.Message); }
}
return failures == 0 ? 0 : 1;

sealed record ScopeSignature(string Hash, string[] ElectricalChambers);
