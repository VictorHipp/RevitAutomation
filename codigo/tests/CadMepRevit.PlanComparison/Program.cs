using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json;
using CadMepRevit.Core;
using CadMepRevit.Revit;

CultureInfo.CurrentCulture = CultureInfo.InvariantCulture;
if (args is ["snapshot", var folder, var output, var sample])
{
    var files = Files(folder).ToList();
    if (files.Count == 0) throw new InvalidOperationException("No hay DWG ni DXF para comparar.");
    var plans = new SortedDictionary<string, PlanSignature>(StringComparer.Ordinal);
    var catalog = new RevitCatalog();
    var level = catalog.Levels.Single(l => l.Name == "Planta baja");
    var samples = Directory.GetFiles(Path.GetDirectoryName(sample)!, "*_uniones_accesorios.dxf").Prepend(sample).ToArray();
    foreach (string path in files.Concat(samples))
    {
        string name = samples.Contains(path) ? "[sintético] " + Path.GetFileName(path) : Path.GetRelativePath(folder, path);
        var drawing = ReadDrawing(path);
        foreach (var scope in Enum.GetValues<ModelScope>().OrderBy(s => s == ModelScope.Luminarias))
        {
            double k = DrawingUnits.ToMillimeters(drawing.HeaderUnit ?? DrawingUnit.Milimetros);
            var layerClasses = drawing.AllLayers.Concat(drawing.SegmentLayers).Distinct(StringComparer.OrdinalIgnoreCase)
                .Order(StringComparer.OrdinalIgnoreCase)
                .ToDictionary(l => l, l => new LayerClass(MepKinds.Suggest(l), PipeNetworks.LayerKind(l), MepKinds.IsUnknownLayer(l)));
            if (scope == ModelScope.Luminarias)
            {
                var fixtures = LooseFixtures.Find(drawing, k);
                var groups = LooseFixtures.Groups(fixtures);
                if (groups.Count > 1)
                {
                    string selected = LooseFixtures.GroupForLevel(groups, level.Name) ?? groups[0];
                    fixtures = fixtures.Where(f => f.Group.Length == 0 || f.Group == selected).ToList();
                }
                if (fixtures.Count > 0) LooseFixtures.AddAsBlocks(drawing, fixtures, k);
            }
            plans.Add($"{name} | {scope}", Sign(drawing, AutoPlanner.Create(drawing, catalog, level, scope), layerClasses));
        }
        string dwf = Path.ChangeExtension(path, ".dwf");
        if (File.Exists(dwf))
        {
            drawing = ReadDrawing(path);
            double k = DrawingUnits.ToMillimeters(drawing.HeaderUnit ?? DrawingUnit.Milimetros);
            DwfReader.Merge(drawing, DwfReader.Read(dwf), k);
            var layerClasses = drawing.AllLayers.Concat(drawing.SegmentLayers).Distinct(StringComparer.OrdinalIgnoreCase)
                .Order(StringComparer.OrdinalIgnoreCase)
                .ToDictionary(l => l, l => new LayerClass(MepKinds.Suggest(l), PipeNetworks.LayerKind(l), MepKinds.IsUnknownLayer(l)));
            foreach (var scope in new[] { ModelScope.Tuberias, ModelScope.Conductos, ModelScope.Bandejas })
                plans.Add($"{name} [DWF] | {scope}", Sign(drawing, AutoPlanner.Create(drawing, catalog, level, scope), layerClasses));
        }
    }
    File.WriteAllText(output, JsonSerializer.Serialize(plans, new JsonSerializerOptions { WriteIndented = true }));
    Console.WriteLine($"{files.Count} planos reales leídos, {plans.Count} combinaciones de plano y botón con muestra y DWF.");
    return 0;
}
#if CURRENT_FITTINGS
if (args is ["inspect", var inspectPath, var button, var fragment])
{
    var catalog = new RevitCatalog();
    var level = catalog.Levels.Single(l => l.Name == "Planta baja");
    var plan = AutoPlanner.Create(ReadDrawing(inspectPath), catalog, level, Enum.Parse<ModelScope>(button)).Plan!;
    foreach (var (block, pos, foot) in plan.Blocks.Where(b => b.Block.BlockName.Contains(fragment, StringComparison.OrdinalIgnoreCase)))
    {
        Console.WriteLine($"{block.BlockName}, inserción {pos}, huella {foot}, vínculo {plan.BlockLinks.GetValueOrDefault(block)}");
        var centre = foot == null ? pos : new P3(foot.CenterX, foot.CenterY, pos.Z);
        foreach (var (segment, distance) in plan.Network.Segments.Select(s => (s, NetworkBuilder.ProjectToSegment(centre, s.Start, s.End).Dist))
                     .OrderBy(x => x.Dist).Take(3))
            Console.WriteLine($"  tramo {segment.Index} '{segment.Layer}': {segment.Start} → {segment.End}, distancia {distance:0.###} mm, " +
                $"ancho CAD {segment.WidthMm}, medida de rótulo {plan.SegmentSizes.GetValueOrDefault(segment.Index)}, capa {plan.Layers[segment.Layer].SizeMm}");
    }
    return 0;
}
if (args is ["calibrate", var calFolder, var calSample])
{
    var catalog = new RevitCatalog();
    var level = catalog.Levels.Single(l => l.Name == "Planta baja");
    var drawings = Files(calFolder).Append(calSample).Select(ReadDrawing).ToList();
    foreach (double scale in new[] { 0.5, 1.0, 1.5 })
    foreach (var scope in new[] { ModelScope.Tuberias, ModelScope.Conductos, ModelScope.Bandejas })
    {
        var gaps = drawings.SelectMany(d => AutoPlanner.Create(d, catalog, level, scope, scale).Plan!.Network.Gaps).ToList();
        Console.WriteLine($"CALIBRACIÓN {scope}, escala {scale:0.0}: " + string.Join(", ",
            Enum.GetValues<GapKind>().Select(k => $"{k} {gaps.Count(g => g.Kind == k && g.Closed)} cerrados / " +
                $"{gaps.Count(g => g.Kind == k && !g.Closed && g.Rejection == null)} sobre límite / " +
                $"{gaps.Count(g => g.Kind == k && g.Rejection != null)} incompatibles")) +
            $", avance máximo {gaps.Where(g => g.Closed).Select(g => g.ExtensionMm).DefaultIfEmpty().Max():0.##} mm");
    }
    return 0;
}
if (args is ["compare", var beforeFile, var afterFile])
{
    var before = Read(beforeFile);
    var after = Read(afterFile);
    int failures = 0;
    void Require(bool ok, string check)
    {
        if (ok) return;
        Console.WriteLine("FALLO " + check);
        failures++;
    }
    Require(before.Keys.SequenceEqual(after.Keys), "mismos planos y botones");
    foreach (var (key, a) in before)
    {
        if (!after.TryGetValue(key, out var b)) continue;
        var scope = Enum.Parse<ModelScope>(key.Split(" | ")[^1]);
        Require(a.LayerClasses.Keys.SequenceEqual(b.LayerClasses.Keys) && a.LayerClasses.All(l =>
            b.LayerClasses.TryGetValue(l.Key, out var cls) && cls == l.Value), key + " clasificación de capas");
        Require(a.LayerNames.SequenceEqual(b.LayerNames), key + " capas del plan");
        Require(!b.SizeFailed && !a.SizeFailed, key + " cálculo de medidas completado");
        Require(a.BlockCount == b.BlockCount, key + " inventario de bloques");
        bool Air(BlockSignature x) => ModelScopes.IsDuctBlock(x.Role, x.Name);
        bool Tray(BlockSignature x) => x.Role == BlockRole.Accesorio && BlockNames.IsTrayAccessory(x.Name);
        var oldBlocks = a.Blocks.ToDictionary(x => x.Index);
        var newBlocks = b.Blocks.ToDictionary(x => x.Index);
        foreach (var (index, oldBlock) in oldBlocks)
        {
            if (!newBlocks.TryGetValue(index, out var current))
            {
                Require(false, $"{key} desapareció el INSERT {index}");
                continue;
            }
            if (oldBlock.Role == current.Role && oldBlock.Action == current.Action) continue;
            bool staysOutsideScope = !oldBlock.Selected && !current.Selected;
            bool allowed = (Air(oldBlock) || Air(current)) && (staysOutsideScope ||
                    scope is ModelScope.Tuberias or ModelScope.Conductos or ModelScope.EquiposPci) ||
                Tray(current) && (staysOutsideScope || scope is ModelScope.Tuberias or ModelScope.Bandejas or ModelScope.EquiposPci);
            Require(allowed, $"{key}, cambió el bloque '{current.Name}' ({oldBlock.Role} → {current.Role}, {oldBlock.Selected} → {current.Selected})");
        }
        int linked = b.Blocks.Count(x => x.Selected && x.Linked);
        int selected = b.Blocks.Count(x => x.Selected);
        if (!key.StartsWith("[sintético]", StringComparison.Ordinal))
        {
            // En cualquier plano con válvulas (Tuberías) o rejillas (Conductos), todas deben quedar asociadas a su tramo.
            bool realValves = scope == ModelScope.Tuberias && b.Blocks.Any(x => x.Selected && x.Role == BlockRole.Accesorio &&
                PipeNetworks.Normalize(x.Name).Contains("VALVULA"));
            bool realGrilles = scope == ModelScope.Conductos && b.Blocks.Any(x => x.Selected && x.Role == BlockRole.Terminal &&
                PipeNetworks.Normalize(x.Name).Contains("REJILLA"));
            if (realValves || realGrilles)
            {
                var required = b.Blocks.Where(x => x.Selected && (realValves
                    ? x.Role == BlockRole.Accesorio && PipeNetworks.Normalize(x.Name).Contains("VALVULA")
                    : x.Role == BlockRole.Terminal && PipeNetworks.Normalize(x.Name).Contains("REJILLA"))).ToList();
                Require(required.Count > 0 && required.All(x => x.Linked), $"{key}: {required.Count(x => x.Linked)}/{required.Count} " +
                    (realValves ? "válvulas" : "rejillas") + " asociadas al tramo");
                Console.WriteLine($"{key}: {required.Count(x => x.Linked)}/{required.Count} {(realValves ? "válvulas" : "rejillas")} asociadas al tramo.");
            }
        }
        Require(a.SlopeLabels.SequenceEqual(b.SlopeLabels), key + " etiquetas de pendiente y sus votos");
        var changes = new List<string>();
        foreach (string layer in a.LayerNames)
        {
            var x = a.Layers[layer];
            var y = b.Layers[layer];
            Require(x.Configuration == y.Configuration, $"{key}, configuración de '{layer}'");
            Require(y.Joints.Where(j => j.Fitting).All(j => ModelScopes.AllowsFitting(scope, y.Kind, j.Kind)),
                $"{key}, uniones permitidas de '{layer}'");
            // Tubos eléctricos: desde el 27-11-2026 llevan uniones; sus encuentros (la geometría) no deben cambiar.
            if (y.Kind == MepKind.TuboElectrico)
                Require(x.Junctions == y.Junctions, $"{key}, encuentros de '{layer}'");
            else if (y.Kind == MepKind.Bandeja && y.Gaps.All(g => !g.Closed))
                Require(x.Junctions == y.Junctions && x.PlannedFittings == y.PlannedFittings, $"{key}, uniones de '{layer}'");
            int affected = CompareSegments(key, layer, x, y, Require);
            if (affected > 0 || x.Fittings != y.Fittings || x.Encounters != y.Encounters)
                changes.Add($"'{layer}': {x.Segments.Length} → {y.Segments.Length} tramos ({affected} cambios geométricos), " +
                    $"{x.Fittings} → {y.Fittings} uniones");
        }
        var yLayers = b.Layers.Values.ToList();
        var counts = Enum.GetValues<JunctionKind>().Where(k => ModelScopes.AllowsFitting(scope, scope switch
        {
            ModelScope.Tuberias => MepKind.Tuberia, ModelScope.Conductos => MepKind.Conducto,
            ModelScope.Bandejas => MepKind.Bandeja, _ => MepKind.NoImportar
        }, k)).Select(k => $"{k}={yLayers.Sum(l => l.Joints.Count(j => j.Fitting && j.Kind == k))}");
        var closed = yLayers.SelectMany(l => l.Gaps).Count(g => g.Closed);
        var above = yLayers.SelectMany(l => l.Gaps).Count(g => !g.Closed && g.Rejection == null);
        var incompatible = yLayers.SelectMany(l => l.Gaps).Count(g => g.Rejection != null);
        int crosses = yLayers.Where(l => l.Kind == MepKind.Tuberia).Sum(l => l.Joints.Count(j => j.Ends == 4));
        Console.WriteLine($"{key}: {b.Layers.Count} capas, {a.SelectedCount} → {selected} bloques ({linked} asociados), " +
            $"{yLayers.Sum(l => l.Segments.Length)} tramos, {string.Join(", ", counts)}, {crosses} encuentros de 4 tubos, " +
            $"{closed} huecos cerrados / {above} sobre límite / {incompatible} incompatibles" + (changes.Count == 0 ? "." : " · " + string.Join(" | ", changes)));
        if (b.SlopeLabels.Length > 0)
            Console.WriteLine($"  Pendientes: {b.SlopeLabels.Count(l => l.Associated)}/{b.SlopeLabels.Length} etiquetas asociadas.");
        if (scope is ModelScope.Conductos or ModelScope.Tuberias)
            foreach (var group in b.Blocks.Where(x => x.Selected && x.NetworkBlock).GroupBy(x => BlockNames.Clean(x.Name)).OrderBy(g => g.Key))
                Console.WriteLine($"  Accesorio '{group.Key}': {group.Count(x => x.Linked)}/{group.Count()} asociados.");
    }
    var trays = after["[sintético] bandejas_uniones.dxf | Bandejas"].Layers;
    Require(trays.Values.Where(l => l.Kind == MepKind.Bandeja).Sum(l => l.Fittings) == 5, "DXF sintético: 5 uniones de bandeja");
    Require(trays.Values.Single(l => l.Kind == MepKind.TuboElectrico) is { Segments.Length: 5, Encounters: 2, Fittings: 2 },
        "DXF sintético: 5 tramos y 2 encuentros de tubo eléctrico, con sus 2 uniones (codo y te) planificadas");
    Console.WriteLine($"{after.Count} combinaciones. " + (failures == 0 ? "RESULTADO: solo las diferencias previstas." : $"RESULTADO: {failures} discrepancias."));
    return failures == 0 ? 0 : 1;
}

static int CompareSegments(string key, string layer, LayerSignature before, LayerSignature after, Action<bool, string> require)
{
    var tol = PlanDefaults.ToleranceMm;
    var changes = after.Gaps.Where(g => g.Closed).ToList();
    var previousFittings = before.Joints.Where(j => j.Kind == JunctionKind.Transicion).Select(j => j.Point).ToHashSet();
    var reducers = after.Joints.Where(j => j.Fitting && j.Kind == JunctionKind.Transicion && !previousFittings.Contains(j.Point))
        .Select(j => j.Point).ToList();
    var newGeometry = after.Segments.ToLookup(s => (s.Start, s.End, s.CadWidth));
    var matched = new HashSet<SegmentSignature>(ReferenceEqualityComparer.Instance);

    static bool NearEndpoint(SegmentSignature s, P3 p, double allowance) =>
        Math.Min(s.Start.DistanceTo(p), s.End.DistanceTo(p)) <= allowance;
    static bool OnSegment(SegmentSignature s, P3 p, double tolerance) =>
        NetworkBuilder.ProjectToSegment(p, s.Start, s.End).Dist <= tolerance;
    bool FollowsReducer(SegmentSignature s, P3 r)
    {
        if (OnSegment(s, r, tol)) return true;
        var legs = after.Segments.Where(p => NearEndpoint(p, r, tol)).ToList();
        return legs.Count == 2 && ((NearEndpoint(legs[0], s.Start, tol) && NearEndpoint(legs[1], s.End, tol)) ||
            (NearEndpoint(legs[1], s.Start, tol) && NearEndpoint(legs[0], s.End, tol)));
    }
    bool Witness(SegmentSignature s) => changes.Any(g => NearEndpoint(s, g.Point, g.Extension + tol) ||
        g.Kind == "Te" && OnSegment(s, g.Point, tol)) || reducers.Any(r => FollowsReducer(s, r));

    int changed = 0;
    var unrelated = new Dictionary<string, int>();
    void Deviation(string label, SegmentSignature s)
    {
        unrelated[label] = unrelated.GetValueOrDefault(label) + 1;
        Console.WriteLine($"  {key}, '{layer}' {label}, {s.Start} → {s.End}, ancho CAD {s.CadWidth}, tamaño {s.Size}.");
    }
    foreach (var old in before.Segments)
    {
        var same = BestMatch(newGeometry[(old.Start, old.End, old.CadWidth)]) ?? BestMatch(after.Segments.Where(x =>
            x.Start.DistanceTo(old.Start) <= 1e-6 && x.End.DistanceTo(old.End) <= 1e-6 && SameHeight(x.CadWidth, old.CadWidth)));
        if (same == null)
        {
            changed++;
            if (!Witness(old)) Deviation("geometría anterior sin hueco ni transición", old);
            continue;
        }
        matched.Add(same);
        if ((old.System != same.System || old.PipeType != same.PipeType || old.DuctType != same.DuctType) && !Witness(old))
            Deviation("tipo o sistema en tramo conservado", old);
        if ((old.Size != same.Size || old.Height != same.Height || old.SizeSource != same.SizeSource) && !Witness(old))
            Deviation("medida o fuente en tramo conservado", old);
        if ((!SameHeight(old.Ratio, same.Ratio) || !SameHeight(old.StartZ, same.StartZ) || !SameHeight(old.EndZ, same.EndZ)) && !Witness(old))
            Deviation("pendiente o cotas en tramo conservado", old);

        SegmentSignature? BestMatch(IEnumerable<SegmentSignature> candidates) => candidates.Where(x => !matched.Contains(x))
            .OrderByDescending(x => x.Size == old.Size && x.Height == old.Height && x.SizeSource == old.SizeSource).FirstOrDefault();
    }
    foreach (var current in after.Segments)
        if (!matched.Contains(current) && !Witness(current))
            Deviation("geometría nueva sin hueco ni transición", current);
    foreach (var (label, count) in unrelated)
        require(false, $"{key}, '{layer}': {count} {label}");
    return changed;

    static bool SameHeight(double? a, double? b) => a == b || a is double x && b is double y && Math.Abs(x - y) <= 1e-6;
}
#endif
throw new ArgumentException("Uso: snapshot <planos> <firmas.json> <muestra.dxf> | compare <antes.json> <después.json> | calibrate <planos> <muestra.dxf>");

static IEnumerable<string> Files(string folder) => Directory.EnumerateFiles(folder, "*", SearchOption.AllDirectories)
    .Where(f => Path.GetExtension(f).ToUpperInvariant() is ".DWG" or ".DXF").Order(StringComparer.OrdinalIgnoreCase);

static CadDrawing ReadDrawing(string path)
{
    var drawing = CadReader.Read(path, new CadReader.Options { ReadLinesInsideBlocks = PlanDefaults.ReadLinesInsideBlocks });
    LayerPlanning.FixImplausibleUnits(drawing);
    return drawing;
}

static string Hash<T>(T data) => Convert.ToHexString(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(data)));
#if CURRENT_FITTINGS
static SortedDictionary<string, PlanSignature> Read(string path) =>
    JsonSerializer.Deserialize<SortedDictionary<string, PlanSignature>>(File.ReadAllText(path))!;
#endif

static PlanSignature Sign(CadDrawing drawing, AutoPlanResult result, Dictionary<string, LayerClass> layerClasses)
{
    var plan = result.Plan ?? throw new InvalidOperationException(result.Error);
    var layers = new Dictionary<string, LayerSignature>(StringComparer.OrdinalIgnoreCase);
    var segmentsByLayer = plan.Network.Segments.ToLookup(s => s.Layer, StringComparer.OrdinalIgnoreCase);
    var jointsByLayer = plan.Network.Junctions.ToLookup(j => j.Layer, StringComparer.OrdinalIgnoreCase);
    foreach (var (layer, lp) in plan.Layers)
    {
        var segments = segmentsByLayer[layer].ToList();
        var localNumbers = segments.Select((s, i) => (s.Index, Local: i)).ToDictionary(p => p.Index, p => p.Local);
        var junctions = jointsByLayer[layer].Select(j => new
        {
            j.Point, j.Kind, j.Note,
            Ends = j.Ends.Select(e => new { Segment = localNumbers[e.Segment], e.AtStart }),
            Ordered = j.Ordered.Select(e => new { Segment = localNumbers[e.Segment], e.AtStart })
        }).ToList();
        bool Fitting(JunctionKind kind) =>
#if CURRENT_FITTINGS
            plan.AllowsFitting(lp.Kind, kind);
        var gaps = plan.Network.Gaps.Where(g => g.Layer == layer)
            .Select(g => new GapSignature(g.Kind.ToString(), g.Point, g.ExtensionMm, g.Closed, g.Rejection)).ToArray();
#else
            plan.CreateFittings && !plan.WithoutFittings(lp.Kind) && kind != JunctionKind.NoAdmitida;
        GapSignature[] gaps = [];
#endif
        var fittings = junctions.Where(j => Fitting(j.Kind)).ToList();
        var detail = segments.Select(s =>
        {
            bool sized = plan.SegmentSizes.TryGetValue(s.Index, out var size);
            var slope = plan.Slopes?.Pipes.GetValueOrDefault(s.Index);
            return new SegmentSignature(s.Start, s.End, s.WidthMm, sized ? size.ValueMm : null, size.HeightMm, size.Source,
                plan.SegmentSystems.GetValueOrDefault(s.Index)?.Value, plan.SegmentPipeTypes.GetValueOrDefault(s.Index)?.Value,
#if CURRENT_FITTINGS
                plan.SegmentDuctTypes.GetValueOrDefault(s.Index)?.Value,
#else
                null,
#endif
                slope?.Start.Z, slope?.End.Z, slope?.PlannedRatio);
        }).ToArray();
        layers.Add(layer, new LayerSignature(lp.Kind, junctions.Count, fittings.Count, Hash(lp), Hash(junctions), Hash(fittings), detail,
            junctions.Select(j => new JointSignature(j.Point, j.Kind, j.Ends.Count(), Fitting(j.Kind))).ToArray(), gaps));
    }
    var selected = plan.Blocks.Select(b => b.Block).ToHashSet();
    var blocks = drawing.Blocks.Select((b, i) =>
    {
        var bp = selected.Contains(b) ? plan.BlockPlans.FirstOrDefault(p => BuildPlan.Same(p, b)) : null;
        string? action = bp == null ? null : Hash(bp);
#if CURRENT_FITTINGS
        bool expectsLink = NetworkBlocks.For(plan.Scope, b.Role, b.BlockName) != null;
        bool linked = plan.BlockLinks.ContainsKey(b);
        bool follows = expectsLink;
        if (bp != null && follows) action = Hash(new { bp.Action, bp.SymbolId, bp.MarkerCategory, bp.HeightMm, bp.IsChamber, bp.ElevationMm });
#else
        bool expectsLink = false;
        bool linked = false;
        if (bp != null && b.Role is BlockRole.Accesorio or BlockRole.Equipo or BlockRole.Terminal or BlockRole.Conector)
            action = Hash(new { bp.Action, bp.SymbolId, bp.MarkerCategory, bp.HeightMm, bp.IsChamber, bp.ElevationMm });
#endif
        return new BlockSignature(i, b.BlockName, b.Role, action, bp != null, expectsLink, linked);
    }).ToArray();
    var labels = plan.Slopes?.Association.Labels.Select(l => new SlopeSignature(l.Text.Value, l.Segment.HasValue,
        l.Downstream, l.IsolatedVoteDiscarded)).ToArray() ?? [];
    return new PlanSignature(layerClasses, layers, layers.Keys.ToArray(), drawing.Blocks.Count, blocks, plan.Blocks.Count, labels,
        result.Notices.Any(n => n.StartsWith("No se pudieron leer los rótulos de tamaño del plano", StringComparison.Ordinal)));
}

sealed record LayerClass(MepKind Kind, MepKind PipeKind, bool Unknown);
sealed record LayerSignature(MepKind Kind, int Encounters, int Fittings, string Configuration,
    string Junctions, string PlannedFittings, SegmentSignature[] Segments, JointSignature[] Joints, GapSignature[] Gaps);
sealed record SegmentSignature(P3 Start, P3 End, double? CadWidth, double? Size, double? Height, string? SizeSource,
    long? System, long? PipeType, long? DuctType, double? StartZ, double? EndZ, double? Ratio);
sealed record JointSignature(P3 Point, JunctionKind Kind, int Ends, bool Fitting);
sealed record GapSignature(string Kind, P3 Point, double Extension, bool Closed, string? Rejection);
sealed record BlockSignature(int Index, string Name, BlockRole Role, string? Action, bool Selected, bool NetworkBlock, bool Linked);
sealed record SlopeSignature(string Text, bool Associated, P3 Direction, bool Isolated);
sealed record PlanSignature(Dictionary<string, LayerClass> LayerClasses, Dictionary<string, LayerSignature> Layers,
    string[] LayerNames, int BlockCount, BlockSignature[] Blocks, int SelectedCount, SlopeSignature[] SlopeLabels, bool SizeFailed);
