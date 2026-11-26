using CadMepRevit.Compat; // RevitIds: ElementId de 32 bits en Revit 2023
using System.Text;
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Electrical;
using Autodesk.Revit.DB.Mechanical;
using Autodesk.Revit.DB.Plumbing;
using Autodesk.Revit.DB.Structure;
using CadMepRevit.Core;

namespace CadMepRevit.Revit;

/// <summary>Lo que el usuario ha decidido para una capa CAD.</summary>
public sealed class LayerPlan
{
    public string Layer { get; init; } = "";
    public MepKind Kind { get; init; }
    public ElementId? SystemTypeId { get; init; }
    public ElementId? TypeId { get; init; }
    /// <summary>Diámetro (o ancho si el tipo es rectangular/bandeja), en mm.</summary>
    public double SizeMm { get; init; }
    /// <summary>Alto en mm (solo conductos rectangulares y bandejas).</summary>
    public double HeightMm { get; init; }
    /// <summary>La capa está dibujada con contorno (doble línea): el ancho de cada tramo viene del CAD.</summary>
    public bool DoubleLine { get; init; }
}

public enum BlockAction
{
    NoColocar,
    /// <summary>Colocar una familia cargada en el proyecto.</summary>
    Familia,
    /// <summary>Sin familia: forma 3D (DirectShape) con la huella medida en el CAD y el alto indicado.</summary>
    Marcador
}

public sealed class BlockPlan
{
    public string Layer { get; init; } = "";
    public string BlockName { get; init; } = "";
    public BlockAction Action { get; init; } = BlockAction.Familia;
    public ElementId SymbolId { get; init; } = ElementId.InvalidElementId;
    public BuiltInCategory MarkerCategory { get; init; } = BuiltInCategory.OST_GenericModel;
    /// <summary>Alto del marcador (mm).</summary>
    public double HeightMm { get; init; }
    public bool IsChamber { get; init; }
    /// <summary>Altura sobre el nivel (mm): centro del marcador o punto de inserción de la familia.</summary>
    public double ElevationMm { get; init; }
    /// <summary>Va en el techo (luminarias, detectores): una familia basada en cara se coloca en un plano horizontal mirando hacia abajo.</summary>
    public bool CeilingMounted { get; init; }
    /// <summary>Su altura es la del techo (falso techo o forjado de encima) sobre su punto, si se encuentra; si no, <see cref="ElevationMm"/>.</summary>
    public bool UseCeilingHeight { get; init; }
}

/// <summary>Plan completo, ya revisado por el usuario, que se ejecuta en una sola transacción.</summary>
public sealed class BuildPlan
{
    public ModelScope? Scope { get; init; }
    public bool UsesPipeNetwork => Scope == ModelScope.Tuberias;
    public bool AllowsFitting(MepKind kind, JunctionKind joint) => CreateFittings && ModelScopes.AllowsFitting(Scope, kind, joint);
    public Dictionary<CadBlockRef, NetworkBlockLink> BlockLinks { get; } = new();
    public SlopeSolution? Slopes { get; set; }
    public RiserPlan? Risers { get; set; }
    public Dictionary<int, PipeRiser> RisersBySegment { get; } = new();
    public Dictionary<int, ElementId> SegmentPipeTypes { get; } = new();
    public Dictionary<int, ElementId> SegmentDuctTypes { get; } = new();
    public HashSet<int> DuctLabelSizes { get; } = new();
    public List<string> PipeNetworkNotes { get; } = new();
    public List<string> ChamberNotes { get; } = new();
    public ElementId LevelId { get; init; } = ElementId.InvalidElementId;
    public string LevelName { get; init; } = "";
    public double LevelElevationFt { get; init; }
    public double HeightAboveLevelMm { get; set; }
    /// <summary>Aplicar la regla «sigue el techo» (ver Config/PlacementConfig). Las Salas técnicas la desactivan: usan su altura.</summary>
    public bool FollowCeiling { get; set; } = true;
    public Dictionary<string, LayerPlan> Layers { get; } = new(StringComparer.OrdinalIgnoreCase);
    public List<BlockPlan> BlockPlans { get; } = new();
    /// <summary>Resultado de la detección de doble línea, por capa.</summary>
    public Dictionary<string, OutlineResult> Outlines { get; } = new(StringComparer.OrdinalIgnoreCase);
    /// <summary>Avisos que no impiden construir (p. ej. tipos sin familias de unión).</summary>
    public List<string> Warnings { get; } = new();
    public NetworkResult Network { get; set; } = new();
    /// <summary>
    /// Medida de cada tramo leída del plano (rótulos «Ø25», «300x150» y cotas), por número de tramo de la red: diámetro
    /// o ancho y, si se conoce, alto (mm). En tuberías a doble línea manda el diámetro dibujado.
    /// </summary>
    public Dictionary<int, (double ValueMm, double? HeightMm, string Source)> SegmentSizes { get; } = new();
    /// <summary>Tipo de sistema de cada tramo de tubería según su rótulo («FON. AFS» → agua fría), si difiere del de su capa.</summary>
    public Dictionary<int, ElementId> SegmentSystems { get; } = new();
    /// <summary>Bloques del CAD ya convertidos a mm (con el mismo desplazamiento que la red).</summary>
    public List<(CadBlockRef Block, P3 PositionMm, Footprint? FootprintMm)> Blocks { get; } = new();
    public bool CreateFittings { get; init; } = true;
    /// <summary>Conectar extremos libres con los conectores de las familias colocadas.</summary>
    public bool AutoConnect { get; init; } = true;
    public P3 TranslationMm { get; init; }
    /// <summary>
    /// Posición del CAD vinculado/importado de ese mismo archivo en el proyecto (pies, del DWG al proyecto): todo se coloca
    /// encima de él. Null = coordenadas del propio DWG. Ver <see cref="CadPlacement"/>.
    /// </summary>
    public Transform? CadToProject { get; set; }
    // Datos de origen, solo para el informe
    public string SourcePath { get; init; } = "";
    public string UnitsLabel { get; init; } = "";
    public double ToleranceMm { get; init; }
    public double MinLengthMm { get; init; }

    public int Count(MepKind kind) => Network.Segments.Count(s => Layers.TryGetValue(s.Layer, out var p) && p.Kind == kind);
    public int BlockCount => CountBlocks(BlockAction.Familia);
    public int MarkerCount => CountBlocks(BlockAction.Marcador);
    public int CountBlocks(BlockAction action) => Blocks.Count(b => BlockPlans.Any(p => Same(p, b.Block) && p.Action == action));
    internal static bool Same(BlockPlan p, CadBlockRef b) =>
        string.Equals(p.Layer, b.Layer, StringComparison.OrdinalIgnoreCase) &&
        string.Equals(p.BlockName, b.BlockName, StringComparison.OrdinalIgnoreCase);
}

public sealed class BuildReport
{
    public bool HasAutomaticFittings { get; set; }
    public int PipeCrossesSkipped { get; set; }
    public Dictionary<JunctionKind, int> FittingFailuresByKind { get; } = new();
    public List<(string Family, string Result, string Detail)> NetworkAccessories { get; } = new();
    public Dictionary<MepKind, int> Created { get; } = new();
    public int Elbows, Tees, Crosses, Transitions, FittingFailures, InstancesPlaced, MarkersPlaced, WallWidthMismatches, AutoConnections, InstanceFailures, SegmentFailures, SizeAdjusted, WarningsDismissed;
    /// <summary>Bloques que ya estaban colocados en el mismo sitio (de otro botón o de una ejecución anterior): no se repiten.</summary>
    public int BlocksAlreadyThere;
    /// <summary>Luminarias y detectores colocados a la cota real del techo / supuesta (sin techo ni forjado encima).</summary>
    public int CeilingHeights, CeilingAssumed;
    public Dictionary<string, int> CeilingSources { get; } = new();
    /// <summary>Familias que no quedaron a su cota y se movieron a ella / que siguen sin estar a su cota.</summary>
    public int HeightCorrected, HeightWrong;
    public List<string> Problems { get; } = new();
    /// <summary>
    /// Lo que Revit ha creado realmente (una muestra por capa y por bloque, y el resultado de cada unión).
    /// Va al archivo de informe: sirve para comprobar dentro de Revit lo que fuera solo se sabe que compila.
    /// </summary>
    public List<string> Diagnostics { get; } = new();
    public List<ElementId> CreatedIds { get; } = new();
    public TransactionStatus Status { get; set; }

    // ---- Seguimiento para el informe y el inventario
    /// <summary>Tramos y muros creados: elemento, tramo de la red, capa y tipo.</summary>
    public List<(ElementId Id, int Segment, string Layer, MepKind Kind)> CurvesCreated { get; } = new();
    /// <summary>Tramos que Revit no pudo crear.</summary>
    public List<(int Segment, string Layer, string Error)> SegmentErrors { get; } = new();
    /// <summary>Uniones creadas: elemento, capa, clase y primer tramo que une.</summary>
    public List<(ElementId Id, string Layer, JunctionKind Kind)> FittingsCreated { get; } = new();
    /// <summary>Bloques colocados como familia o como marcador.</summary>
    public List<(CadBlockRef Block, ElementId Id, BlockAction Done)> BlocksPlaced { get; } = new();
    /// <summary>Bloques que no se pudieron colocar.</summary>
    public List<(CadBlockRef Block, string Error)> BlockErrors { get; } = new();
    // Regla «bajo_forjado» (desactivada en esta versión: ver Config/PlacementConfig). Quedan vacías.
    public List<string> SystemsOrder { get; } = new();
    public Dictionary<int, (int Component, double? UndersideFt, string Status)> Placement { get; } = new();
    public List<string> SlabWarnings { get; } = new();
    public List<string> HoleWarnings { get; } = new();
    public List<string> PlacementNotes { get; } = new();

    // Regla «sigue el techo» (ver Config/PlacementConfig y MepBuilder.Ceiling)
    /// <summary>Tramos llevados bajo el forjado; subidas o bajadas creadas; codos de esas subidas creados y fallidos.</summary>
    public int CeilingFollowed, CeilingSteps, CeilingElbows, CeilingElbowFailures;
    /// <summary>Tramos sin forjado encima que toman la cota de sus vecinos; que quedan a la altura supuesta; extremos bajados en uniones.</summary>
    public int CeilingWithoutSlab, CeilingFallback, CeilingLoweredEnds;
    public List<string> CeilingNotes { get; } = new();
    /// <summary>Tramos que atraviesan el forjado o quedan bajo el suelo de la planta.</summary>
    public List<string> CeilingCrossings { get; } = new();
    /// <summary>Tramos que quedan por debajo del falso techo (no caben sobre el falso techo).</summary>
    public List<string> FalseCeilingWarnings { get; } = new();

    public string Summary()
    {
        var sb = new StringBuilder();
        foreach (var kv in Created.Where(k => k.Value > 0))
            sb.AppendLine("• " + MepKinds.Label(kv.Key) + ": " + kv.Value);
        if (HasAutomaticFittings) sb.AppendLine($"• Uniones creadas: {Elbows} codos, {Tees} tes, {Crosses} cruces" + (Transitions > 0 ? $", {Transitions} transiciones" : ""));
        if (CeilingFollowed > 0)
            sb.AppendLine($"• Bajo el techo (100 mm bajo el forjado): {CeilingFollowed} tramos, {CeilingSteps} subidas o bajadas con {CeilingElbows} codos" +
                          (CeilingElbowFailures > 0 ? $" ({CeilingElbowFailures} sin codos: vea el detalle)" : ""));
        if (CeilingFallback > 0) sb.AppendLine($"• Sin forjado encima, a la altura supuesta: {CeilingFallback} tramos");
        if (CeilingCrossings.Count > 0) sb.AppendLine($"• Tramos que atraviesan el forjado o el suelo: {CeilingCrossings.Count} (revise el informe)");
        if (FalseCeilingWarnings.Count > 0) sb.AppendLine($"• Tramos por debajo del falso techo: {FalseCeilingWarnings.Count} (revise el informe)");
        if (InstancesPlaced > 0) sb.AppendLine("• Familias colocadas (bloques): " + InstancesPlaced);
        if (MarkersPlaced > 0) sb.AppendLine("• Marcadores 3D sin familia (bloques): " + MarkersPlaced + " — sustitúyalos por familias cuando las tenga");
        if (BlocksAlreadyThere > 0) sb.AppendLine($"• Bloques que ya estaban colocados en su sitio (no se repiten): {BlocksAlreadyThere}");
        if (CeilingHeights > 0)
            sb.AppendLine($"• A la cota del techo: {CeilingHeights} (" + string.Join(", ", CeilingSources.Select(k => $"{k.Value} bajo {k.Key}")) + ")" +
                          (CeilingSources.Keys.Any(k => k.StartsWith("forjado"))
                              ? ". Bajo FORJADO = no hay falso techo modelado encima: si lo hay en obra, es una cota SUPUESTA; revísela" : ""));
        if (CeilingAssumed > 0)
            sb.AppendLine($"• Sin techo ni forjado encima, a la altura supuesta: {CeilingAssumed} (revise; véanse en 3D o en el plano de techo)");
        if (HeightWrong > 0) sb.AppendLine($"• Familias que no han quedado a su cota (vea el detalle): {HeightWrong}");
        if (AutoConnections > 0) sb.AppendLine($"• Conexiones automáticas con equipos y terminales: {AutoConnections}");
        if (WallWidthMismatches > 0) sb.AppendLine($"• Muros sin tipo del espesor exacto (se usó el más parecido): {WallWidthMismatches}");
        if (SizeAdjusted > 0) sb.AppendLine($"• {SizeAdjusted} elementos con tamaño ajustado por Revit al más cercano disponible");
        int failed = SegmentFailures + FittingFailures + InstanceFailures;
        if (failed > 0) sb.AppendLine($"• No se pudieron crear: {SegmentFailures} tramos, {FittingFailures} uniones, {InstanceFailures} familias (vea el detalle)");
        if (WarningsDismissed > 0) sb.AppendLine($"• Avisos de Revit cerrados automáticamente: {WarningsDismissed}");
        return sb.ToString();
    }
}

public static partial class MepBuilder
{
    private static double Mm(double mm) => UnitUtils.ConvertToInternalUnits(mm, UnitTypeId.Millimeters);
    private static double ToMm(double ft) => UnitUtils.ConvertFromInternalUnits(ft, UnitTypeId.Millimeters);

    /// <summary>Construye todo el plan en una única transacción propia (se deshace con Ctrl+Z).</summary>
    public static BuildReport Build(Document doc, BuildPlan plan)
    {
        var report = new BuildReport();
        var level = FindLevel(doc, plan);

        using var t = new Transaction(doc, "CAD-REV: construir desde CAD");
        var options = t.GetFailureHandlingOptions();
        options.SetFailuresPreprocessor(new WarningDismisser(report));
        t.SetFailureHandlingOptions(options);
        t.Start();
        BuildElements(doc, plan, level, report);
        report.Status = t.Commit();
        return report;
    }

    /// <summary>
    /// Igual que <see cref="Build"/>, pero dentro de una transacción que ya ha abierto quien llama (y que él
    /// confirma o deshace). Los avisos de Revit no se cierran solos. Lo usa la construcción sin ventanas.
    /// </summary>
    public static BuildReport BuildInOpenTransaction(Document doc, BuildPlan plan)
    {
        var report = new BuildReport();
        BuildElements(doc, plan, FindLevel(doc, plan), report);
        report.Status = TransactionStatus.Started;
        return report;
    }

    private static Level FindLevel(Document doc, BuildPlan plan) =>
        doc.GetElement(plan.LevelId) as Level
        ?? throw new InvalidOperationException("El nivel elegido ya no existe en el proyecto.");

    /// <summary>Un sistema: capas del mismo tipo de elemento y del mismo tipo de sistema de Revit (solo para informes).</summary>
    public sealed record SystemGroup(string Label, MepKind Kind, ElementId? SystemTypeId, HashSet<string> Layers);

    /// <summary>Sistemas del plan (tubería, conducto, bandeja, tubo eléctrico, muro y tipo de sistema). Solo agrupa para los informes.</summary>
    public static List<SystemGroup> SystemGroups(Document doc, BuildPlan plan) =>
        plan.Layers.Values.Where(lp => lp.Kind != MepKind.NoImportar)
            .GroupBy(lp => (lp.Kind, System: lp.SystemTypeId?.Value ?? -1))
            .Select(g =>
            {
                var sysId = g.First().SystemTypeId;
                string? sys = sysId is null ? null : doc.GetElement(sysId)?.Name;
                return new SystemGroup(MepKinds.Label(g.Key.Kind) + (sys is null ? "" : " · " + sys), g.Key.Kind, sysId,
                    new HashSet<string>(g.Select(x => x.Layer), StringComparer.OrdinalIgnoreCase));
            })
            .OrderBy(s => Array.IndexOf(MepKinds.All, s.Kind))
            .ThenBy(s => s.Label, StringComparer.CurrentCultureIgnoreCase)
            .ToList();

    private static void BuildElements(Document doc, BuildPlan plan, Level level, BuildReport report)
    {
        report.HasAutomaticFittings = plan.CreateFittings && plan.Layers.Values.Any(l => ModelScopes.AllowsAnyFitting(plan.Scope, l.Kind));
        double baseZ = level.ProjectElevation + Mm(plan.HeightAboveLevelMm);
        // Del plano (mm) al proyecto (pies): encima del CAD vinculado si lo hay (con su desplazamiento y giro); si no, las
        // coordenadas del propio DWG. La cota la pone siempre el nivel.
        var cad = plan.CadToProject;
        double rotOffset = cad is null ? 0 : Math.Atan2(cad.BasisX.Y, cad.BasisX.X);
        XYZ Map(double xMm, double yMm, double zFt)
        {
            if (cad is null) return new XYZ(Mm(xMm), Mm(yMm), zFt);
            var q = cad.OfPoint(new XYZ(Mm(xMm - plan.TranslationMm.X), Mm(yMm - plan.TranslationMm.Y), 0));
            return new XYZ(q.X, q.Y, zFt);
        }
        XYZ ToXyz(P3 p) => Map(p.X, p.Y, baseZ + Mm(p.Z));

        // Tiempo de cada fase, para el informe (saber dónde se va el tiempo dentro de Revit en vez de suponerlo).
        var clock = System.Diagnostics.Stopwatch.StartNew();
        var phases = new List<string>();
        void Mark(string phase) { phases.Add($"{phase} {clock.Elapsed.TotalSeconds:0.0} s"); clock.Restart(); }

        // 1) Tramos rectos
        var curves = new Dictionary<int, MEPCurve>();
        var placed = new List<FamilyInstance>();
        var sizeChecks = new List<(Parameter P, double RequestedMm, string Layer)>();
        var wallWidths = new FilteredElementCollector(doc).OfClass(typeof(WallType)).Cast<WallType>()
            .Where(w => w.Kind == WallKind.Basic).ToDictionary(w => w.Id, w => ToMm(w.Width));
        // Muestras para el diagnóstico del informe
        var sampleWalls = new Dictionary<string, (Wall Wall, int Count)>(StringComparer.OrdinalIgnoreCase);
        var sampleInstances = new List<(string Text, FamilyInstance Instance, double RequestedMm, XYZ Site)>();
        var sampledPlans = new HashSet<BlockPlan>();
        foreach (var seg in plan.Network.Segments)
        {
            if (!plan.Layers.TryGetValue(seg.Layer, out var lp) || lp.Kind == MepKind.NoImportar) continue;
            if (lp.Kind == MepKind.Muro)
            {
                if (CreateWall(doc, level, seg, lp, wallWidths, report, Map) is { } wall)
                {
                    sampleWalls[seg.Layer] = sampleWalls.TryGetValue(seg.Layer, out var sw) ? (sw.Wall, sw.Count + 1) : (wall, 1);
                    report.CurvesCreated.Add((wall.Id, seg.Index, seg.Layer, MepKind.Muro));
                }
                else report.SegmentErrors.Add((seg.Index, seg.Layer, "Revit no pudo crear el muro (vea el detalle)"));
                continue;
            }
            try
            {
                XYZ a = ToXyz(seg.Start), b = ToXyz(seg.End);
                if (lp.Kind == MepKind.Tuberia && plan.Slopes?.Pipes.TryGetValue(seg.Index, out var slope) == true)
                {
                    a = Map(slope.Start.X, slope.Start.Y, level.ProjectElevation + Mm(slope.Start.Z));
                    b = Map(slope.End.X, slope.End.Y, level.ProjectElevation + Mm(slope.End.Z));
                }
                if (plan.UsesPipeNetwork && plan.SegmentSystems.GetValueOrDefault(seg.Index) == ElementId.InvalidElementId)
                    throw new InvalidOperationException("Falta un tipo de sistema compatible. Cárguelo en el proyecto y repita.");
                MEPCurve curve = lp.Kind switch
                {
                    MepKind.Tuberia => Pipe.Create(doc, plan.SegmentSystems.TryGetValue(seg.Index, out var sys) ? sys : lp.SystemTypeId,
                        plan.SegmentPipeTypes.GetValueOrDefault(seg.Index) ?? lp.TypeId, level.Id, a, b),
                    MepKind.Conducto => Duct.Create(doc, lp.SystemTypeId, plan.SegmentDuctTypes.GetValueOrDefault(seg.Index) ?? lp.TypeId, level.Id, a, b),
                    MepKind.Bandeja => CableTray.Create(doc, lp.TypeId, a, b, level.Id),
                    MepKind.TuboElectrico => Conduit.Create(doc, lp.TypeId, a, b, level.Id),
                    _ => throw new InvalidOperationException("Tipo no admitido")
                };
                ApplySize(curve, lp, plan.RisersBySegment.GetValueOrDefault(seg.Index)?.NominalMm != null ||
                    plan.DuctLabelSizes.Contains(seg.Index) ? null : seg.WidthMm, sizeChecks,
                    plan.SegmentSizes.TryGetValue(seg.Index, out var fromPlan) ? fromPlan : null,
                    plan.UsesPipeNetwork && lp.Kind == MepKind.Tuberia);
                curves[seg.Index] = curve;
                report.CreatedIds.Add(curve.Id);
                report.Created[lp.Kind] = report.Created.GetValueOrDefault(lp.Kind) + 1;
                report.CurvesCreated.Add((curve.Id, seg.Index, seg.Layer, lp.Kind));
            }
            catch (Exception ex)
            {
                report.SegmentFailures++;
                report.Problems.Add($"Tramo de la capa '{seg.Layer}' de {seg.Start} a {seg.End} ({seg.Length:0} mm): {ex.Message}");
                report.SegmentErrors.Add((seg.Index, seg.Layer, ex.Message));
            }
        }

        doc.Regenerate();
        if (plan.Slopes != null)
            Diagnose(report, () =>
            {
                foreach (var network in plan.Slopes.Networks)
                    foreach (int index in network.Segments.Where(curves.ContainsKey).Take(3))
                    {
                        var pipe = curves[index];
                        var line = ((LocationCurve)pipe.Location).Curve;
                        var p = line.GetEndPoint(0); var q = line.GetEndPoint(1);
                        double horizontal = Math.Sqrt(Math.Pow(q.X - p.X, 2) + Math.Pow(q.Y - p.Y, 2));
                        double actual = horizontal > 0 ? Math.Abs(q.Z - p.Z) / horizontal : 0;
                        string revit = pipe.get_Parameter(BuiltInParameter.RBS_PIPE_SLOPE)?.AsValueString() ?? "no disponible";
                        report.Diagnostics.Add($"PENDIENTES red {network.Id}, tubo {pipe.Id}: prevista {plan.Slopes.Pipes[index].PlannedRatio * 100:0.###} %; geometría Revit {actual * 100:0.###} %; parámetro Revit {revit}.");
                    }
            });
        foreach (var (p, mm, layer) in sizeChecks)
        {
            double got = ToMm(p.AsDouble());
            if (Math.Abs(got - mm) <= 0.5) continue;
            report.SizeAdjusted++;
            if (report.SizeAdjusted <= 20)
                report.Problems.Add($"Capa '{layer}': se pidió {mm:0.#} mm y Revit aplicó {got:0.#} mm (tamaño disponible en el tipo).");
        }
        // 1b) Regla «sigue el techo»: cota bajo el forjado, con subidas y bajadas donde cambia (antes de las uniones).
        var routes = FollowCeiling(doc, plan, level, curves, report);
        if (routes.Count > 0) Mark($"bajo el techo ({report.CeilingFollowed} tramos, {report.CeilingSteps} subidas o bajadas)");
        MEPCurve? CurveAt(int index, bool atStart) =>
            routes.TryGetValue(index, out var r) ? r.CurveAt(atStart) : curves.GetValueOrDefault(index);
        // Trozos horizontales de cada tramo del plano (uno si no se ha partido), para accesorios y conexiones.
        var pieceMap = curves.ToDictionary(c => c.Key,
            c => routes.TryGetValue(c.Key, out var r) ? r.Horizontal.ToList() : new List<MEPCurve> { c.Value });
        Diagnose(report, () =>
        {
            foreach (var grp in plan.Network.Segments.Where(s => curves.ContainsKey(s.Index)).GroupBy(s => s.Layer, StringComparer.OrdinalIgnoreCase))
            {
                var c = curves[grp.First().Index];
                double z = (c.Location as LocationCurve)?.Curve.GetEndPoint(0).Z ?? double.NaN;
                report.Diagnostics.Add($"Capa '{grp.Key}': {grp.Count()} tramos creados. Muestra: {c.GetType().Name} id {c.Id}, tipo '{doc.GetElement(c.GetTypeId())?.Name}', " +
                    $"tamaño real {SizeOf(c)}, eje a {ToMm(z - level.ProjectElevation):0} mm sobre el nivel.");
            }
            foreach (var (layer, s) in sampleWalls)
                report.Diagnostics.Add($"Capa '{layer}': {s.Count} muros creados. Muestra: id {s.Wall.Id}, tipo '{s.Wall.WallType.Name}', espesor {ToMm(s.Wall.Width):0} mm, " +
                    $"línea de ubicación '{s.Wall.get_Parameter(BuiltInParameter.WALL_KEY_REF_PARAM)?.AsValueString() ?? "?"}'.");
        });

        Mark($"tramos ({curves.Count})");
        // Los conectores se buscan en los extremos previstos, incluidos los XYZ de las tuberías con pendiente.
        XYZ JointEnd(int index, bool atStart)
        {
            if (routes.TryGetValue(index, out var route))
            {
                var s0 = plan.Network.Segments[index];
                var e = atStart ? s0.Start : s0.End;
                return Map(e.X, e.Y, level.ProjectElevation + Mm(atStart ? route.StartAxisMm : route.EndAxisMm));
            }
            if (plan.Slopes?.Pipes.TryGetValue(index, out var slope) == true)
            {
                var p = atStart ? slope.Start : slope.End;
                return Map(p.X, p.Y, level.ProjectElevation + Mm(p.Z));
            }
            var s = plan.Network.Segments[index];
            return ToXyz(atStart ? s.Start : s.End);
        }
        BuildFittings(doc, plan, CurveAt, JointEnd, report);
        Mark($"uniones ({report.Elbows + report.Tees + report.Crosses + report.Transitions} creadas, {report.FittingFailures} fallidas)");
        // 3) Bloques: familia cargada o marcador 3D con la huella medida en el CAD.
        //    Lo que ya colocó antes otro botón (o este mismo) en el mismo sitio no se repite: se reutiliza para conectar.
        var existing = new ExistingBlocks(doc);
        View? planeView = null;
        var verified = new HashSet<long>(); // familias (tipos) cuyo modo de colocación ya se comprobó en Revit
        // Luminarias y detectores de techo: a la cota REAL del falso techo o del forjado de encima (proyecto y vínculos).
        CeilingFinder? ceilings = null;
        if (plan.BlockPlans.Any(p => p.UseCeilingHeight))
        {
            ceilings = CeilingFinder.Create(doc, level);
            var cf = ceilings;
            Diagnose(report, () => report.Diagnostics.Add("Techos y forjados consultados para la altura de luminarias y detectores: " +
                string.Join("; ", cf.Models.Concat(cf.Notes))));
        }
        // Velocidad: el modo de colocación que funcionó con una familia se prueba primero con las demás de esa familia, y
        // la altura real se comprueba al final, con una sola regeneración del modelo (no una por elemento).
        var modeBySymbol = new Dictionary<long, string>();
        var toCheck = new List<(FamilyInstance Fi, double X, double Y, double WantFt, string Name)>();
        var networkPlacement = new Dictionary<CadBlockRef, (NetworkBlockKind Use, NetworkBlockLink? Link, XYZ Point, XYZ Direction, double Rotation)>();
        // Lo necesario para sustituir por un marcador en su sitio una familia que no quede donde debe.
        var fallbackOf = new Dictionary<long, (BlockPlan Bp, Footprint? Foot, CadBlockRef Block, double ZMm)>();
        var missedSites = new List<XYZ>(); // de techo sin techo ni forjado encima
        foreach (var (block, pos, cadFoot) in plan.Blocks)
        {
            var foot = cadFoot;
            var bp = plan.BlockPlans.FirstOrDefault(p => BuildPlan.Same(p, block));
            if (bp is null || bp.Action == BlockAction.NoColocar) continue;
            string name = BlockNames.Clean(block.BlockName);
            string? familyError = null;
            // Si el punto de inserción del CAD está fuera de su propia geometría (pasa en algunos
            // DWG exportados), se usa el centro de la huella medida.
            bool insideOwn = foot is null || !bp.IsChamber && foot.Contains(pos.X, pos.Y, 0.1 * Math.Max(foot.SizeX, foot.SizeY));
            double x = insideOwn ? pos.X : foot!.CenterX, y = insideOwn ? pos.Y : foot!.CenterY;
            double rot = (insideOwn ? block.RotationRadians : foot!.AngleRad) + rotOffset;
            double zMm = bp.ElevationMm + pos.Z;
            if (NetworkBlocks.For(plan.Scope, block.Role, block.BlockName) is { } use)
            {
                plan.BlockLinks.TryGetValue(block, out var link);
                var direction = new XYZ(Math.Cos(rot), Math.Sin(rot), 0);
                if (link != null)
                {
                    rot = Math.Atan2(link.Direction.Y, link.Direction.X) + rotOffset;
                    direction = new XYZ(Math.Cos(rot), Math.Sin(rot), 0);
                    x = link.Axis.X;
                    y = link.Axis.Y;
                    zMm = plan.HeightAboveLevelMm + link.Axis.Z;
                    // Si su tramo sigue el techo, a la cota de ese tramo en su punto.
                    if (routes.TryGetValue(link.Segment, out var linkRoute)) zMm = linkRoute.AxisMmAt(link.Along) + link.Axis.Z;
                    if (plan.Slopes?.Pipes.TryGetValue(link.Segment, out var slope) == true)
                    {
                        zMm = slope.Start.Z + (slope.End.Z - slope.Start.Z) * link.Along;
                        if (use.Use == NetworkBlockKind.EnLinea)
                            direction = (JointEnd(link.Segment, false) - JointEnd(link.Segment, true)).Normalize();
                    }
                    if (link.Site == NetworkBlockSite.Costado && link.Side != 0)
                    {
                        double width = curves.TryGetValue(link.Segment, out var trunk) && EndConnectors(trunk).FirstOrDefault() is { } connector
                            ? ConnectorSize(connector).Width : link.WidthMm;
                        x += link.Direction.X * width / 2;
                        y += link.Direction.Y * width / 2;
                    }
                    if (foot != null) foot = new Footprint(x, y, rot - rotOffset, foot.SizeX, foot.SizeY);
                }
                networkPlacement[block] = (use.Use, link, Map(x, y, level.ProjectElevation + Mm(zMm)), direction, rot);
            }
            var site = Map(x, y, level.ProjectElevation);
            if (bp.UseCeilingHeight)
            {
                if (ceilings?.UndersideAt(site.X, site.Y) is { } under)
                {
                    zMm = ToMm(under.Z - level.ProjectElevation);
                    report.CeilingHeights++;
                    report.CeilingSources[under.Source] = report.CeilingSources.GetValueOrDefault(under.Source) + 1;
                }
                else
                {
                    report.CeilingAssumed++;
                    missedSites.Add(site);
                }
            }
            if (existing.Find(bp, site.X, site.Y, level.ProjectElevation + Mm(zMm)) is { } already)
            {
                report.BlocksAlreadyThere++;
                report.BlocksPlaced.Add((block, already.Id, bp.Action));
                if (already is FamilyInstance afi) placed.Add(afi);
                continue;
            }
            if (bp.Action == BlockAction.Familia)
            {
                try
                {
                    if (doc.GetElement(bp.SymbolId) is not FamilySymbol symbol)
                        throw new InvalidOperationException("La familia elegida ya no está en el proyecto.");
                    if (!symbol.IsActive) { symbol.Activate(); doc.Regenerate(); }
                    var placementType = symbol.Family?.FamilyPlacementType ?? FamilyPlacementType.Invalid;
                    var point = site;
                    var dir = new XYZ(Math.Cos(rot), Math.Sin(rot), 0);
                    double wantFt = level.ProjectElevation + Mm(zMm);
                    // Intentos, cada uno en su subtransacción: el primero que Revit acepte.
                    var attempts = new List<(string Key, string How, Func<FamilyInstance> Make)>();
                    // De techo y de plano de trabajo (las «basadas en cara» lo son): primero en un plano horizontal a la cota
                    // del techo mirando hacia abajo, que es como cuelgan del falso techo; así quedan a la altura y bien orientadas.
                    if (placementType == FamilyPlacementType.WorkPlaneBased && (bp.CeilingMounted || bp.IsChamber))
                        attempts.Add(("plano", bp.CeilingMounted ? "en un plano de referencia horizontal a la cota pedida, mirando hacia abajo"
                            : "en un plano de referencia horizontal a la cota de la tapa", () =>
                        {
                            var at = new XYZ(point.X, point.Y, wantFt);
                            var face = HorizontalPlane(doc, level, at, dir, ref planeView, bp.CeilingMounted);
                            return doc.Create.NewFamilyInstance(face, at, dir, symbol);
                        }));
                    if (placementType == FamilyPlacementType.WorkPlaneBased && networkPlacement.TryGetValue(block, out var mounting)
                        && mounting.Use == NetworkBlockKind.Terminal && mounting.Link != null)
                        attempts.Add(("plano de red", "en el plano vertical de la red", () =>
                        {
                            var face = NetworkPlane(doc, level, mounting.Point, mounting.Direction, ref planeView);
                            return doc.Create.NewFamilyInstance(face, mounting.Point, XYZ.BasisZ, symbol);
                        }));
                    string elevNote = "";
                    attempts.Add(("nivel", "en el nivel, con su altura en el parámetro de elevación", () =>
                    {
                        var fi = doc.Create.NewFamilyInstance(point, symbol, level, StructuralType.NonStructural);
                        elevNote = SetElevation(fi, Mm(zMm));
                        if (Math.Abs(rot) > 1e-6)
                            ElementTransformUtils.RotateElement(doc, fi.Id, Line.CreateBound(point, point + XYZ.BasisZ), rot);
                        return fi;
                    }));
                    attempts.Add(("punto", "en el punto con su altura, sin nivel", () =>
                    {
                        var p3 = new XYZ(point.X, point.Y, wantFt);
                        var fi = doc.Create.NewFamilyInstance(p3, symbol, StructuralType.NonStructural);
                        if (Math.Abs(rot) > 1e-6)
                            ElementTransformUtils.RotateElement(doc, fi.Id, Line.CreateBound(p3, p3 + XYZ.BasisZ), rot);
                        return fi;
                    }));
                    if (modeBySymbol.TryGetValue(symbol.Id.Value, out var good))
                        attempts = attempts.OrderBy(a => a.Key == good ? 0 : 1).ToList();
                    FamilyInstance? made = null;
                    string how = "";
                    var errors = new List<string>();
                    foreach (var (key, h, make) in attempts)
                    {
                        using var st = new SubTransaction(doc);
                        st.Start();
                        try
                        {
                            made = make();
                            if (bp.IsChamber) SizeChamber(doc, made, foot, bp.HeightMm, name, report);
                            if (networkPlacement.TryGetValue(block, out var network) && network.Link != null)
                                OrientNetworkFamily(doc, made, network.Use, network.Direction, network.Point);
                            // Primer elemento de cada familia: se comprueba en Revit que ha quedado EN SU SITIO (X, Y) y a su
                            // altura. Si no, este modo no vale para esa familia y se prueba el siguiente. Una regeneración por
                            // familia, no por elemento; los demás se comprueban todos juntos al final.
                            if (!verified.Contains(symbol.Id.Value))
                            {
                                doc.Regenerate();
                                if (made.Location is LocationPoint lp0)
                                {
                                    double dxy = Math.Sqrt(Math.Pow(lp0.Point.X - point.X, 2) + Math.Pow(lp0.Point.Y - point.Y, 2));
                                    if (dxy > Mm(50))
                                        throw new InvalidOperationException($"quedó a {ToMm(dxy) / 1000:0.##} m de su sitio");
                                    if (Math.Abs(lp0.Point.Z - wantFt) > Mm(50))
                                    {
                                        ElementTransformUtils.MoveElement(doc, made.Id, new XYZ(0, 0, wantFt - lp0.Point.Z));
                                        doc.Regenerate();
                                        if (made.Location is LocationPoint lp1 && Math.Abs(lp1.Point.Z - wantFt) > Mm(50))
                                            throw new InvalidOperationException($"quedó a {ToMm(lp1.Point.Z - level.ProjectElevation):0} mm sobre el nivel en vez de {zMm:0} mm");
                                    }
                                }
                            }
                            st.Commit();
                            how = h;
                            modeBySymbol[symbol.Id.Value] = key;
                            verified.Add(symbol.Id.Value);
                            toCheck.Add((made, point.X, point.Y, wantFt, name));
                            fallbackOf[made.Id.Value] = (bp, foot, block, zMm);
                            break;
                        }
                        catch (Exception ex)
                        {
                            if (st.GetStatus() == TransactionStatus.Started) st.RollBack();
                            made = null;
                            errors.Add(ex.Message);
                        }
                    }
                    if (made is null) throw new InvalidOperationException(string.Join(" | ", errors));
                    report.CreatedIds.Add(made.Id);
                    report.InstancesPlaced++;
                    report.BlocksPlaced.Add((block, made.Id, BlockAction.Familia));
                    placed.Add(made);
                    existing.Add(bp, made);
                    if (sampledPlans.Add(bp))
                        sampleInstances.Add(($"Bloque '{name}' → familia '{symbol.FamilyName} : {symbol.Name}' (colocación de la familia: {placementType}, " +
                            $"anfitrión de familia: {(symbol.Family is { } sf ? BlockFamilies.HostingBehavior(sf)?.ToString() ?? "?" : "?")}); " +
                            $"colocada {how}" + (elevNote.Length > 0 ? $" [{elevNote}]" : "") +
                            (errors.Count > 0 ? $" (antes falló: {string.Join(" | ", errors)})" : ""), made, zMm, site));
                    continue;
                }
                catch (Exception ex)
                {
                    report.InstanceFailures++;
                    report.Problems.Add($"Bloque '{name}' (capa '{block.Layer}'): no se pudo colocar la familia: {ex.Message} " +
                        "→ Use una familia que no necesite anfitrión (no basada en cara/muro). Se crea un marcador 3D en su lugar.");
                    familyError = ex.Message;
                    if (foot is null) { report.BlockErrors.Add((block, "no se pudo colocar la familia: " + ex.Message)); continue; }
                }
            }
            try
            {
                if (foot is null) throw new InvalidOperationException("el bloque no tiene geometría medible en el CAD");
                // Los de techo cuelgan bajo él: el centro del marcador queda medio alto por debajo de la cota del techo.
                double centerFt = level.ProjectElevation + Mm(zMm) - (bp.UseCeilingHeight || bp.IsChamber ? Mm(Math.Max(5, bp.HeightMm)) / 2 : 0);
                var id = CreateMarker(doc, bp, foot, name, centerFt, Map);
                if (doc.GetElement(id) is { } markerElement) existing.Add(bp, markerElement);
                report.CreatedIds.Add(id);
                report.MarkersPlaced++;
                report.BlocksPlaced.Add((block, id, BlockAction.Marcador));
                if (sampledPlans.Add(bp))
                {
                    string shape = $"huella {foot.SizeX:0}×{foot.SizeY:0} mm, alto {Math.Max(5, bp.HeightMm):0} mm";
                    Diagnose(report, () => report.Diagnostics.Add(
                        $"Bloque '{name}' → marcador 3D id {id}, categoría '{doc.GetElement(id)?.Category?.Name}', {shape}."));
                }
            }
            catch (Exception ex)
            {
                report.InstanceFailures++;
                report.Problems.Add($"Bloque '{name}' (capa '{block.Layer}') en {pos}: no se pudo crear el marcador: {ex.Message}");
                report.BlockErrors.Add((block, (familyError is null ? "" : "no se pudo colocar la familia (" + familyError + ") y ") +
                                              "no se pudo crear el marcador: " + ex.Message));
            }
        }
        // Si muchas de techo no tienen techo encima, ¿están donde la arquitectura? Se dice en qué zona está cada cosa: si no
        // se solapan, el DWG no está en las coordenadas del proyecto (vincúlelo en su sitio y repita).
        if (missedSites.Count > 0 && ceilings?.Extent is { } ext)
        {
            double M(double ft) => UnitUtils.ConvertFromInternalUnits(ft, UnitTypeId.Meters);
            double mnX = missedSites.Min(p => p.X), mxX = missedSites.Max(p => p.X), mnY = missedSites.Min(p => p.Y), mxY = missedSites.Max(p => p.Y);
            bool overlap = mnX <= ext.MaxX && mxX >= ext.MinX && mnY <= ext.MaxY && mxY >= ext.MinY;
            report.Problems.Add($"{missedSites.Count} luminarias o detectores de techo sin techo ni forjado encima: están en X {M(mnX):0.#}..{M(mxX):0.#} m, " +
                                $"Y {M(mnY):0.#}..{M(mxY):0.#} m; los techos y forjados de la arquitectura en X {M(ext.MinX):0.#}..{M(ext.MaxX):0.#} m, " +
                                $"Y {M(ext.MinY):0.#}..{M(ext.MaxY):0.#} m." +
                                " Primer punto sin techo: " + ceilings.Explain(missedSites[0].X, missedSites[0].Y, level.ProjectElevation) + "." +
                                (overlap ? " Se solapan: en esos puntos simplemente no hay techo modelado."
                                         : " NO se solapan: el DWG no está en las coordenadas del proyecto. Vincule el DWG en su sitio (Insertar > Vincular CAD) y repita: se colocará encima de él."));
        }

        Mark($"bloques ({report.InstancesPlaced} familias, {report.MarkersPlaced} marcadores)");
        // 3b) Altura real de las familias colocadas: una sola regeneración; la que no esté a su cota (p. ej. porque su
        //     parámetro de elevación no se pudo escribir) se mueve a ella, y si aun así no está, el informe lo dice.
        if (toCheck.Count > 0)
        {
            doc.Regenerate();
            XYZ Want((FamilyInstance Fi, double X, double Y, double WantFt, string Name) c) => new(c.X, c.Y, c.WantFt);
            bool Off((FamilyInstance Fi, double X, double Y, double WantFt, string Name) c) =>
                c.Fi.IsValidObject && c.Fi.Location is LocationPoint lp && lp.Point.DistanceTo(Want(c)) > Mm(50);
            var off = toCheck.Where(Off).ToList();
            foreach (var c in off)
            {
                try { ElementTransformUtils.MoveElement(doc, c.Fi.Id, Want(c) - ((LocationPoint)c.Fi.Location).Point); }
                catch { /* se cuenta abajo como no corregida */ }
            }
            if (off.Count > 0)
            {
                doc.Regenerate();
                report.HeightCorrected = off.Count;
                var still = off.Where(Off).ToList();
                report.HeightWrong = still.Count;
                foreach (var g in still.GroupBy(c => c.Name).Take(15))
                {
                    var p = ((LocationPoint)g.First().Fi.Location).Point;
                    report.Problems.Add($"'{g.Key}': {g.Count()} no quedaban en su sitio (una a {ToMm(p.DistanceTo(Want(g.First()))) / 1000:0.##} m, " +
                                        $"a {ToMm(p.Z - level.ProjectElevation):0} mm sobre el nivel en vez de {ToMm(g.First().WantFt - level.ProjectElevation):0} mm): " +
                                        "se han quitado y en su lugar hay un marcador 3D en el sitio correcto. Coloque esa familia a mano.");
                }
                // Nunca se deja nada lejos de su sitio: la familia se borra y se pone un marcador en el punto correcto.
                foreach (var c in still)
                {
                    if (!fallbackOf.TryGetValue(c.Fi.Id.Value, out var fb)) continue;
                    try
                    {
                        using var st = new SubTransaction(doc);
                        st.Start();
                        var oldId = c.Fi.Id;
                        placed.Remove(c.Fi);
                        doc.Delete(oldId);
                        report.CreatedIds.Remove(oldId);
                        report.InstancesPlaced--;
                        report.BlocksPlaced.RemoveAll(b => b.Id == oldId);
                        if (fb.Foot is { } ft)
                        {
                            double centerFt = level.ProjectElevation + Mm(fb.ZMm) - (fb.Bp.UseCeilingHeight || fb.Bp.IsChamber ? Mm(Math.Max(5, fb.Bp.HeightMm)) / 2 : 0);
                            var id = CreateMarker(doc, fb.Bp, ft, c.Name, centerFt, Map);
                            report.CreatedIds.Add(id);
                            report.MarkersPlaced++;
                            report.BlocksPlaced.Add((fb.Block, id, BlockAction.Marcador));
                        }
                        else report.BlockErrors.Add((fb.Block, "la familia no quedó en su sitio y el bloque no tiene geometría para un marcador"));
                        st.Commit();
                    }
                    catch (Exception ex) { report.Problems.Add($"'{c.Name}': no se pudo sustituir por un marcador ({ex.Message})."); }
                }
            }
        }

        var chambers = fallbackOf.Where(p => p.Value.Bp.IsChamber)
            .Select(p => (Id: RevitIds.FromValue(p.Key), Data: p.Value)).ToList();
        if (chambers.Count > 0)
        {
            doc.Regenerate();
            foreach (var (id, data) in chambers)
                if (doc.GetElement(id) is FamilyInstance fi && fi.get_BoundingBox(null) is { } box)
                {
                    double shift = level.ProjectElevation + Mm(data.ZMm) - box.Max.Z;
                    if (Math.Abs(shift) <= Mm(ChamberSizeToleranceMm)) continue;
                    try { ElementTransformUtils.MoveElement(doc, id, new XYZ(0, 0, shift)); }
                    catch (Exception ex) { report.Problems.Add($"Arqueta id {id}: no se pudo enrasar la tapa: {ex.Message}. Revise su desfase en el nivel."); }
                }
            doc.Regenerate();
            foreach (var (id, data) in chambers)
                if (doc.GetElement(id) is FamilyInstance fi)
                {
                    var box = fi.get_BoundingBox(null);
                    double top = box is null ? double.NaN : ToMm(box.Max.Z - level.ProjectElevation);
                    double depth = box is null ? double.NaN : ToMm(box.Max.Z - box.Min.Z);
                    report.Diagnostics.Add($"Desagüe id {id}: tapa {top:0.##} mm sobre nivel y profundidad geométrica {depth:0.##} mm; " +
                        $"pedido {data.ZMm:0.##} mm y {data.Bp.HeightMm:0.##} mm.");
                    if (box is null || Math.Abs(top - data.ZMm) > ChamberSizeToleranceMm || Math.Abs(depth - data.Bp.HeightMm) > ChamberSizeToleranceMm)
                        report.Problems.Add($"Desagüe id {id}: la geometría de la familia no confirma la cota o profundidad previstas. Revise la tapa y los parámetros de tamaño.");
                }
        }
        Mark("comprobar sitio y altura");
        var fixedPipeEnds = plan.UsesPipeNetwork ? plan.Network.Junctions.Where(j => j.Ends.Count == 4)
            .SelectMany(j => j.Ends).ToLookup(e => e.Segment, e => JointEnd(e.Segment, e.AtStart)) : null;
        var fixedEndsById = pieceMap.SelectMany(p => p.Value.Select(c => (Curve: c, Segment: p.Key)))
            .ToDictionary(c => c.Curve.Id, c => fixedPipeEnds?[c.Segment].ToList() ?? new List<XYZ>());
        if (networkPlacement.Count > 0)
            ConnectNetworkBlocks(doc, level, plan, pieceMap, networkPlacement, fixedEndsById, report);
        // 4) Conexiones automáticas: extremos libres de tuberías/conductos con los conectores de las
        //    familias colocadas (equipos, aparatos, terminales) que estén cerca.
        if (plan.AutoConnect && placed.Count > 0 && curves.Count > 0)
        {
            doc.Regenerate();
            var networkIds = report.BlocksPlaced.Where(b => networkPlacement.ContainsKey(b.Block)).Select(b => b.Id).ToHashSet();
            AutoConnect(doc, pieceMap.Values.SelectMany(p => p).Where(c => plan.Scope != ModelScope.Bandejas || c is not Conduit),
                placed.Where(f => !networkIds.Contains(f.Id)).ToList(), report, plan.UsesPipeNetwork, plan.ToleranceMm, fixedEndsById);
        }

        Mark($"conexiones automáticas ({report.AutoConnections})");
        report.Diagnostics.Insert(0, "Tiempos dentro de Revit: " + string.Join("; ", phases) + ".");
        if (sampleInstances.Count > 0)
            Diagnose(report, () =>
            {
                doc.Regenerate();
                foreach (var (text, fi, requestedMm, at) in sampleInstances)
                {
                    if (!fi.IsValidObject) continue;
                    string real = fi.Location is LocationPoint p ? ToMm(p.Point.Z - level.ProjectElevation).ToString("0") : "?";
                    string box = fi.get_BoundingBox(null) is { } bb
                        ? $"{ToMm(bb.Min.Z - level.ProjectElevation):0} a {ToMm(bb.Max.Z - level.ProjectElevation):0} mm" : "?";
                    // Sitio en planta: el pedido (el del bloque en el CAD) y el real, y lo que se ha desviado.
                    string plan = fi.Location is LocationPoint lp
                        ? $"sitio pedido X {ToMm(at.X):0}, Y {ToMm(at.Y):0} mm; real X {ToMm(lp.Point.X):0}, Y {ToMm(lp.Point.Y):0} mm (desvío {ToMm(Math.Sqrt(Math.Pow(lp.Point.X - at.X, 2) + Math.Pow(lp.Point.Y - at.Y, 2))):0} mm)"
                        : $"sitio pedido X {ToMm(at.X):0}, Y {ToMm(at.Y):0} mm; real desconocido";
                    int connectors = fi.MEPModel?.ConnectorManager?.Connectors.Size ?? 0;
                    report.Diagnostics.Add($"{text}; id {fi.Id}; {plan}; altura pedida {requestedMm:0} mm sobre el nivel, altura real {real} mm " +
                        $"(su geometría ocupa de {box}); " +
                        $"anfitrión: {fi.Host?.Name ?? "ninguno"}; conectores: {connectors}.");
                }
            });
    }

    /// <summary>Altura de la instancia sobre su nivel: «Elevación desde el nivel» o, en las basadas en plano de trabajo, «Desfase».</summary>
    /// <summary>Devuelve qué parámetro se escribió, para el diagnóstico («ninguno» si la familia no deja cambiar la altura).</summary>
    private static string SetElevation(FamilyInstance fi, double ft)
    {
        var found = new List<string>();
        foreach (var (bip, label) in new[] { (BuiltInParameter.INSTANCE_ELEVATION_PARAM, "Elevación desde el nivel"), (BuiltInParameter.INSTANCE_FREE_HOST_OFFSET_PARAM, "Desfase") })
        {
            var p = fi.get_Parameter(bip);
            if (p is null) continue;
            if (p is { IsReadOnly: false, StorageType: StorageType.Double } && p.Set(ft)) return $"«{label}» escrito";
            found.Add($"«{label}» de solo lectura");
        }
        return found.Count > 0 ? string.Join(", ", found) : "sin parámetro de elevación";
    }

    /// <summary>
    /// Plano de referencia horizontal a esa cota, con su cara hacia abajo (como la cara inferior de un techo), para colocar
    /// familias basadas en cara (luminarias, detectores) sin techo modelado. Uno por cota; se nombra «CAD-REV · techo …».
    /// </summary>
    /// <summary>
    /// Plano de referencia horizontal CENTRADO en el punto del elemento, con su cara hacia abajo (como la cara inferior de
    /// un techo), para colocar familias basadas en cara sin techo modelado. Uno por elemento y SIN nombre:
    /// · uno solo y pequeño junto al origen hacía que Revit dejara las luminarias junto al origen, fuera del plano;
    /// · con nombre fijo, la segunda ejecución daba el error de Revit «El nombre introducido está en uso».
    /// </summary>
    private const double ChamberSizeToleranceMm = 1;
    private const string ChamberTypePrefix = "CAD-REV";

    private static void SizeChamber(Document doc, FamilyInstance instance, Footprint? foot, double depthMm, string name, BuildReport report)
    {
        var dimensions = new (string Name, double? Mm, string[] Keys)[]
        {
            ("ancho", foot?.SizeX, new[] { "ANCHO", "ANCHURA", "WIDTH", "ANCHO EXTERIOR", "ANCHO ARQUETA", "DIMENSION X" }),
            ("largo", foot?.SizeY, new[] { "LARGO", "LONGITUD", "LENGTH", "LONGITUD EXTERIOR", "LARGO ARQUETA", "DIMENSION Y" }),
            ("profundidad", depthMm, new[] { "PROFUNDIDAD", "DEPTH", "ALTURA TOTAL", "TOTAL HEIGHT", "ALTURA", "ALTO", "HEIGHT", "CANTO", "PROFUNDIDAD ARQUETA" })
        };
        double? diameter = foot is { } circle && Math.Abs(circle.SizeX - circle.SizeY) <= ChamberSizeToleranceMm
            ? (circle.SizeX + circle.SizeY) / 2 : null;
        if (diameter.HasValue && (Find(instance, new[] { "DIAMETRO", "DIAMETER", "DIAMETRO EXTERIOR" }) != null ||
            Find(instance.Symbol, new[] { "DIAMETRO", "DIAMETER", "DIAMETRO EXTERIOR" }) != null) &&
            !dimensions.Take(2).Any(d => Find(instance, d.Keys) != null || Find(instance.Symbol, d.Keys) != null))
            dimensions = new[] { ("diámetro", diameter, new[] { "DIAMETRO", "DIAMETER", "DIAMETRO EXTERIOR" }), dimensions[2] };

        var symbol = instance.Symbol;
        var typeParameters = new List<(string Name, double Mm, string[] Keys)>();
        var missing = new List<string>();
        foreach (var (axis, mm, keys) in dimensions)
        {
            if (mm is not double value || value <= 0) { missing.Add(axis); continue; }
            if (Apply(Find(instance, keys), value)) continue;
            var parameter = Find(symbol, keys);
            if (parameter is { IsReadOnly: false } && !Equal(parameter, value)) typeParameters.Add((axis, value, keys));
            else if (!Equal(parameter, value)) missing.Add(axis);
        }
        if (typeParameters.Count > 0)
        {
            try
            {
                using var st = new SubTransaction(doc);
                st.Start();
                string signature = string.Join(" ", typeParameters.Select(p => FormattableString.Invariant($"{p.Name} {p.Mm:0.###}")));
                string typeName = $"{ChamberTypePrefix} {symbol.Id.Value} {signature}";
                var sized = symbol.Family.GetFamilySymbolIds().Select(id => doc.GetElement(id) as FamilySymbol).FirstOrDefault(s => s?.Name == typeName)
                    ?? (FamilySymbol)symbol.Duplicate(typeName);
                foreach (var (axis, mm, keys) in typeParameters)
                    if (!Apply(Find(sized, keys), mm)) missing.Add(axis);
                if (!sized.IsActive) sized.Activate();
                instance.Symbol = sized;
                st.Commit();
            }
            catch { missing.AddRange(typeParameters.Select(p => p.Name)); }
        }
        if (missing.Count > 0)
            report.Problems.Add($"'{name}' id {instance.Id}: no se pudo ajustar {string.Join(", ", missing.Distinct())}. Revise la huella CAD y los parámetros de tamaño de la familia.");

        static Parameter? Find(Element element, string[] keys)
        {
            foreach (string key in keys)
                foreach (Parameter p in element.Parameters)
                    if (p.StorageType == StorageType.Double && p.Definition.GetDataType() == SpecTypeId.Length &&
                        PipeNetworks.Normalize(p.Definition.Name) == key) return p;
            return null;
        }
        static bool Equal(Parameter? p, double value) => p != null && Math.Abs(ToMm(p.AsDouble()) - value) <= ChamberSizeToleranceMm;
        static bool Apply(Parameter? p, double value)
        {
            if (Equal(p, value)) return true;
            try { return p is { IsReadOnly: false } && p.Set(Mm(value)); }
            catch { return false; }
        }
    }

    private static Reference HorizontalPlane(Document doc, Level level, XYZ at, XYZ dir, ref View? view, bool down)
    {
        view ??= new FilteredElementCollector(doc).OfClass(typeof(ViewPlan)).Cast<ViewPlan>()
                     .Where(v => !v.IsTemplate).OrderByDescending(v => v.GenLevel?.Id == level.Id).FirstOrDefault()
                 ?? doc.ActiveView;
        // Normal = (libre − burbuja) × corte = dir × (dir girada −90°) = −Z: la cara mira hacia abajo.
        var half = dir.Multiply(Mm(500));
        var cut = new XYZ(dir.Y, -dir.X, 0) * (down ? 1 : -1);
        var rp = doc.Create.NewReferencePlane(at - half, at + half, cut, view);
        return rp.GetReference();
    }

    /// <summary>
    /// Bloques que ya están en el modelo (de una ejecución anterior de este u otro botón): misma familia y tipo, o
    /// marcador del mismo bloque, en el mismo sitio en planta. Evita duplicarlos y permite conectar con ellos.
    /// </summary>
    private sealed class ExistingBlocks
    {
        private readonly Document _doc;
        private readonly Dictionary<long, List<Element>> _bySymbol = new();
        private List<DirectShape>? _markers;
        private bool _loaded;
        public ExistingBlocks(Document doc) => _doc = doc;

        private List<Element> Of(BlockPlan bp)
        {
            if (bp.Action == BlockAction.Marcador)
                return (_markers ??= new FilteredElementCollector(_doc).OfClass(typeof(DirectShape)).Cast<DirectShape>()
                    .Where(d => d.ApplicationId == "CadMepRevit").ToList())
                    .Where(d => string.Equals(d.ApplicationDataId, bp.BlockName, StringComparison.OrdinalIgnoreCase)).Cast<Element>().ToList();
            // Una sola pasada por el modelo para todas las familias (antes, una pasada por cada tipo: lento en modelos grandes).
            if (!_loaded)
            {
                _loaded = true;
                foreach (var fi in new FilteredElementCollector(_doc).OfClass(typeof(FamilyInstance)).Cast<FamilyInstance>())
                {
                    long sid = fi.GetTypeId().Value;
                    if (!_bySymbol.TryGetValue(sid, out var l)) _bySymbol[sid] = l = new List<Element>();
                    l.Add(fi);
                }
            }
            long k = bp.SymbolId.Value;
            if (!_bySymbol.TryGetValue(k, out var list)) _bySymbol[k] = list = new List<Element>();
            return list;
        }

        public void Add(BlockPlan bp, Element e)
        {
            if (bp.Action == BlockAction.Marcador) { if (e is DirectShape d) (_markers ??= new()).Add(d); }
            else Of(bp).Add(e);
        }

        /// <summary>
        /// Mismo bloque a menos de 50 mm en planta y a menos de 1 m de la altura pedida (otra planta no cuenta).
        /// Se compara con la caja del elemento, que vale igual para familias y marcadores.
        /// </summary>
        public Element? Find(BlockPlan bp, double xFt, double yFt, double zFt)
        {
            double r = Mm(50), dz = Mm(1000);
            IEnumerable<Element> choices = Of(bp);
            if (bp.IsChamber && bp.Action == BlockAction.Familia && _doc.GetElement(bp.SymbolId) is FamilySymbol original)
                choices = choices.Concat(original.Family.GetFamilySymbolIds().Where(id =>
                    (_doc.GetElement(id) as FamilySymbol)?.Name.StartsWith($"{ChamberTypePrefix} {bp.SymbolId.Value} ", StringComparison.Ordinal) == true)
                    .SelectMany(id => _bySymbol.GetValueOrDefault(id.Value) ?? new List<Element>()));
            foreach (var e in choices)
            {
                if (!e.IsValidObject) continue;
                XYZ? p = e.Location is LocationPoint lp ? lp.Point
                    : e.get_BoundingBox(null) is { } bb ? (bb.Min + bb.Max) / 2 : null;
                if (p is null || Math.Abs(p.X - xFt) > r || Math.Abs(p.Y - yFt) > r) continue;
                var box = e.get_BoundingBox(null);
                double zMin = Math.Min(p.Z, box?.Min.Z ?? p.Z), zMax = Math.Max(p.Z, box?.Max.Z ?? p.Z);
                if (zFt >= zMin - dz && zFt <= zMax + dz) return e;
            }
            return null;
        }
    }

    /// <summary>El diagnóstico es solo informativo: un fallo al obtenerlo nunca debe detener la construcción.</summary>
    private static void Diagnose(BuildReport report, Action action)
    {
        try { action(); }
        catch (Exception ex) { report.Diagnostics.Add("(no se pudo obtener un dato de diagnóstico: " + ex.Message + ")"); }
    }

    /// <summary>Tamaño que tiene realmente un tramo: diámetro o ancho × alto, en mm.</summary>
    private static string SizeOf(MEPCurve c)
    {
        string V(BuiltInParameter bip) => c.get_Parameter(bip) is { HasValue: true } p ? ToMm(p.AsDouble()).ToString("0.#") : "";
        string d = c switch
        {
            Pipe => V(BuiltInParameter.RBS_PIPE_DIAMETER_PARAM),
            Conduit => V(BuiltInParameter.RBS_CONDUIT_DIAMETER_PARAM),
            Duct => V(BuiltInParameter.RBS_CURVE_DIAMETER_PARAM),
            _ => ""
        };
        if (d.Length > 0) return "Ø " + d + " mm";
        bool tray = c is CableTray;
        return V(tray ? BuiltInParameter.RBS_CABLETRAY_WIDTH_PARAM : BuiltInParameter.RBS_CURVE_WIDTH_PARAM) + "×" +
               V(tray ? BuiltInParameter.RBS_CABLETRAY_HEIGHT_PARAM : BuiltInParameter.RBS_CURVE_HEIGHT_PARAM) + " mm";
    }

    /// <summary>
    /// Marcador sin familia: prisma con la huella del bloque medida en el CAD, centrado en la altura
    /// indicada. Es un DirectShape de la categoría elegida (no tiene conectores MEP).
    /// </summary>
    private static ElementId CreateMarker(Document doc, BlockPlan bp, Footprint foot, string name, double centerZ, Func<double, double, double, XYZ> map)
    {
        var catId = new ElementId(bp.MarkerCategory);
        if (!DirectShape.IsValidCategoryId(catId, doc)) catId = new ElementId(BuiltInCategory.OST_GenericModel);
        double h = Mm(Math.Max(5, bp.HeightMm));
        double z0 = centerZ - h / 2;
        var f = foot with { SizeX = Math.Max(foot.SizeX, 5), SizeY = Math.Max(foot.SizeY, 5) };
        var pts = f.Corners().Select(c => map(c.X, c.Y, z0)).ToList();
        var loop = new CurveLoop();
        for (int i = 0; i < 4; i++) loop.Append(Line.CreateBound(pts[i], pts[(i + 1) % 4]));
        var solid = GeometryCreationUtilities.CreateExtrusionGeometry(new List<CurveLoop> { loop }, XYZ.BasisZ, h);
        var ds = DirectShape.CreateElement(doc, catId);
        ds.ApplicationId = "CadMepRevit";
        ds.ApplicationDataId = bp.BlockName;
        ds.SetShape(new List<GeometryObject> { solid });
        // Sin SetName: un nombre repetido puede dar el error de Revit «El nombre introducido está en uso» al confirmar la
        // operación (y deshacerla entera). El nombre del bloque va en «Comentarios».
        var comments = ds.get_Parameter(BuiltInParameter.ALL_MODEL_INSTANCE_COMMENTS);
        if (comments is { IsReadOnly: false })
            comments.Set("Marcador CAD-REV del bloque '" + name + "': sustituir por la familia real.");
        return ds.Id;
    }

    /// <summary>
    /// Muro por el eje detectado en el CAD. Tipo: el elegido o, en automático, el de espesor más parecido.
    /// Base en el nivel; altura = el "Alto" de la capa.
    /// </summary>
    private static Wall? CreateWall(Document doc, Level level, NetSegment seg, LayerPlan lp,
        Dictionary<ElementId, double> wallWidths, BuildReport report, Func<double, double, double, XYZ> map)
    {
        try
        {
            ElementId typeId = lp.TypeId ?? ElementId.InvalidElementId;
            double cadW = seg.WidthMm ?? 0;
            if (typeId == ElementId.InvalidElementId)
            {
                var near = RevitCatalog.NearestWallType(wallWidths, cadW)
                           ?? throw new InvalidOperationException("el proyecto no tiene tipos de muro básico");
                typeId = near.Id;
                if (Math.Abs(near.WidthMm - cadW) > 5)
                {
                    report.WallWidthMismatches++;
                    if (report.WallWidthMismatches <= 15)
                        report.Problems.Add($"Muro de {cadW:0} mm en el CAD: no hay tipo con ese espesor; se usó '{doc.GetElement(typeId)?.Name}' ({near.WidthMm:0} mm). " +
                                            "Si necesita el espesor exacto, cree un tipo de muro de ese espesor (Editar tipo > Duplicar) y vuelva a importar.");
                }
            }
            var line = Line.CreateBound(map(seg.Start.X, seg.Start.Y, level.ProjectElevation),
                                        map(seg.End.X, seg.End.Y, level.ProjectElevation));
            var wall = Wall.Create(doc, line, typeId, level.Id, Mm(lp.HeightMm > 0 ? lp.HeightMm : 3000), 0, false, false);
            report.CreatedIds.Add(wall.Id);
            report.Created[MepKind.Muro] = report.Created.GetValueOrDefault(MepKind.Muro) + 1;
            return wall;
        }
        catch (Exception ex)
        {
            report.SegmentFailures++;
            report.Problems.Add($"Muro de la capa '{seg.Layer}' de {seg.Start} a {seg.End} ({seg.Length:0} mm): {ex.Message}");
            return null;
        }
    }

    /// <summary>
    /// Une cada extremo libre de tubería/conducto/bandeja con el conector libre más cercano (mismo dominio,
    /// a menos de 500 mm) de las familias colocadas. Si no coinciden exactamente, se prolonga o acorta el
    /// tramo hasta el conector, solo si su dirección cambia menos de 10°. Cada unión en su subtransacción.
    /// Después, los terminales de aire que quedan sobre el cuerpo de un conducto se conectan a él.
    /// </summary>
    private static void AutoConnect(Document doc, IEnumerable<MEPCurve> curves, List<FamilyInstance> instances, BuildReport report,
        bool preservePipes = false, double toleranceMm = PlanDefaults.ToleranceMm,
        IReadOnlyDictionary<ElementId, List<XYZ>>? fixedEnds = null)
    {
        var famConnectors = new List<Connector>();
        foreach (var fi in instances)
        {
            var cm = fi.MEPModel?.ConnectorManager;
            if (cm is null) continue;
            foreach (Connector c in cm.Connectors)
                if (!c.IsConnected && c.ConnectorType == ConnectorType.End) famConnectors.Add(c);
        }
        if (famConnectors.Count == 0) return;
        double limit = Mm(500);
        double maxTurn = Math.Cos(10 * Math.PI / 180);
        var curveList = curves.Where(c => c.IsValidObject).ToList();

        foreach (var curve in curveList)
        {
            foreach (Connector cc in curve.ConnectorManager.Connectors)
            {
                if (cc.IsConnected || cc.ConnectorType != ConnectorType.End) continue;
                if (fixedEnds?.GetValueOrDefault(curve.Id)?.Any(p => p.DistanceTo(cc.Origin) <= Mm(toleranceMm)) == true) continue;
                Connector? best = null;
                double bestD = double.MaxValue;
                foreach (var fc in famConnectors)
                {
                    if (fc.IsConnected || fc.Domain != cc.Domain) continue;
                    double d = fc.Origin.DistanceTo(cc.Origin);
                    if (preservePipes && curve is Pipe && (d > Mm(toleranceMm) ||
                        cc.CoordinateSystem.BasisZ.DotProduct(fc.CoordinateSystem.BasisZ) > -Math.Cos(SlopeRules.ParallelAngleDeg * Math.PI / 180))) continue;
                    if (d <= limit && d < bestD) { best = fc; bestD = d; }
                }
                if (best is null) continue;

                if (preservePipes && curve is Pipe && (bestD > Mm(toleranceMm) ||
                    cc.CoordinateSystem.BasisZ.DotProduct(best.CoordinateSystem.BasisZ) > -Math.Cos(SlopeRules.ParallelAngleDeg * Math.PI / 180)))
                {
                    report.Problems.Add($"Conexión de tubería no realizada en {ToMm(cc.Origin.X):0},{ToMm(cc.Origin.Y):0} mm: el conector no coincide en posición y dirección; coloque la conexión manualmente sin modificar la pendiente.");
                    continue;
                }

                using var st = new SubTransaction(doc);
                st.Start();
                try
                {
                    var target = cc;
                    var original = preservePipes && curve is Pipe && curve.Location is LocationCurve originalLocation ? originalLocation.Curve.Clone() : null;
                    var pipeAxes = original != null ? curveList.OfType<Pipe>().ToDictionary(p => p.Id, p => ((LocationCurve)p.Location).Curve.Clone()) : null;
                    var fittingsBefore = original != null ? new FilteredElementCollector(doc).OfCategory(BuiltInCategory.OST_PipeFitting).ToElementIds().ToHashSet() : null;
                    if (!(preservePipes && curve is Pipe) && bestD > Mm(1) && curve.Location is LocationCurve lc && lc.Curve is Line line)
                    {
                        bool atEnd = line.GetEndPoint(1).DistanceTo(cc.Origin) < line.GetEndPoint(0).DistanceTo(cc.Origin);
                        XYZ fixedPt = atEnd ? line.GetEndPoint(0) : line.GetEndPoint(1);
                        var oldDir = (cc.Origin - fixedPt).Normalize();
                        var newDir = (best.Origin - fixedPt).Normalize();
                        if (oldDir.DotProduct(newDir) < maxTurn)
                            throw new InvalidOperationException("el conector está desviado más de 10° del tramo");
                        lc.Curve = atEnd ? Line.CreateBound(fixedPt, best.Origin) : Line.CreateBound(best.Origin, fixedPt);
                        doc.Regenerate();
                        target = curve.ConnectorManager.Connectors.Cast<Connector>()
                            .Where(c => !c.IsConnected && c.ConnectorType == ConnectorType.End)
                            .OrderBy(c => c.Origin.DistanceTo(best.Origin)).First();
                    }
                    target.ConnectTo(best);
                    if (original != null)
                    {
                        doc.Regenerate();
                        var after = ((LocationCurve)curve.Location).Curve;
                        if (after.GetEndPoint(0).DistanceTo(original.GetEndPoint(0)) > Mm(SlopeRules.Epsilon) ||
                            after.GetEndPoint(1).DistanceTo(original.GetEndPoint(1)) > Mm(SlopeRules.Epsilon) ||
                            !fittingsBefore!.SetEquals(new FilteredElementCollector(doc).OfCategory(BuiltInCategory.OST_PipeFitting).ToElementIds()) ||
                            pipeAxes!.Any(pair => doc.GetElement(pair.Key)?.Location is not LocationCurve location ||
                                location.Curve.GetEndPoint(0).DistanceTo(pair.Value.GetEndPoint(0)) > Mm(SlopeRules.Epsilon) ||
                                location.Curve.GetEndPoint(1).DistanceTo(pair.Value.GetEndPoint(1)) > Mm(SlopeRules.Epsilon)))
                            throw new InvalidOperationException("Revit intentó mover el tubo o crear una unión; conexión anulada para conservar sus extremos");
                    }
                    st.Commit();
                    report.AutoConnections++;
                }
                catch (Exception ex)
                {
                    if (st.GetStatus() == TransactionStatus.Started) st.RollBack();
                    if (report.Problems.Count < 400)
                        report.Problems.Add($"Conexión automática no realizada cerca de {ToMm(cc.Origin.X):0},{ToMm(cc.Origin.Y):0} mm: {ex.Message}. " +
                            (preservePipes && curve is Pipe ? "Coloque la conexión manualmente conservando las cotas de los extremos." : "Puede unirla a mano arrastrando el extremo hasta el equipo."));
                }
            }
        }

        if (preservePipes)
            foreach (var fc in famConnectors.Where(c => c.IsValidObject && !c.IsConnected && c.Domain == Domain.DomainPiping))
                report.Problems.Add($"Conexión de aparato {fc.Owner.Id} no realizada en ({ToMm(fc.Origin.X):0.#}, {ToMm(fc.Origin.Y):0.#}, {ToMm(fc.Origin.Z):0.#}) mm: no hay un extremo de tubería coincidente y alineado; coloque la conexión manualmente conservando las cotas.");

        // Terminales de aire (rejillas, difusores) apoyados en el cuerpo de un conducto
        var ducts = curveList.OfType<Duct>().ToList();
        foreach (var fi in instances.Where(i => i.Category?.Id.Value == (long)BuiltInCategory.OST_DuctTerminal))
        {
            var cm = fi.MEPModel?.ConnectorManager;
            if (cm is null || cm.Connectors.Cast<Connector>().All(c => c.IsConnected)) continue;
            if (fi.Location is not LocationPoint lp) continue;
            var duct = ducts.Where(d => d.Location is LocationCurve dl && dl.Curve.Distance(lp.Point) <= limit)
                            .OrderBy(d => ((LocationCurve)d.Location).Curve.Distance(lp.Point)).FirstOrDefault();
            if (duct is null) continue;
            using var st = new SubTransaction(doc);
            st.Start();
            try
            {
                if (MechanicalUtils.ConnectAirTerminalOnDuct(doc, fi.Id, duct.Id)) { st.Commit(); report.AutoConnections++; }
                else st.RollBack();
            }
            catch { if (st.GetStatus() == TransactionStatus.Started) st.RollBack(); }
        }
    }

    private static Connector? ClosestFreeConnector(MEPCurve curve, XYZ point)
    {
        Connector? best = null;
        double bestD = double.MaxValue;
        foreach (Connector c in curve.ConnectorManager.Connectors)
        {
            if (c.IsConnected) continue;
            double d = c.Origin.DistanceTo(point);
            if (d < bestD) { bestD = d; best = c; }
        }
        return best;
    }

    private static void ApplySize(MEPCurve curve, LayerPlan lp, double? cadWidthMm, List<(Parameter P, double RequestedMm, string Layer)> checks,
        (double ValueMm, double? HeightMm, string Source)? fromPlan = null, bool applyPipeDimensions = false)
    {
        // Doble línea: el ancho (o diámetro) medido en el CAD manda sobre el de la capa. Si no, el rótulo del plano.
        double size = cadWidthMm ?? fromPlan?.ValueMm ?? lp.SizeMm;
        double height = fromPlan?.HeightMm ?? lp.HeightMm;
        bool dimensionWins = applyPipeDimensions && fromPlan is { } dimension && dimension.Source.StartsWith("cota", StringComparison.Ordinal);
        if (dimensionWins) size = fromPlan!.Value.ValueMm;
        // Tubería dibujada con sus dos bordes: el ancho es el diámetro EXTERIOR; se pide el nominal que le corresponde.
        // Si además tiene rótulo y coincide con lo dibujado (±25 %), manda el rótulo (es el diámetro que pide el proyecto).
        if (!dimensionWins && cadWidthMm is double outer && curve is Pipe pipe)
        {
            if (fromPlan is { } label && Math.Abs(label.ValueMm - outer) <= 0.25 * outer) size = label.ValueMm;
            else if (FittingSetup.NominalForOuterMm(pipe, outer) is double nominal) size = nominal;
        }
        // Tubo eléctrico dibujado con sus dos bordes: igual, el ancho es el diámetro exterior → nominal de su norma.
        if (cadWidthMm is double outerC && curve is Conduit conduit && FittingSetup.ConduitNominalForOuterMm(conduit, outerC) is double nominalC)
            size = nominalC;
        void Set(BuiltInParameter bip, double mm)
        {
            if (mm <= 0) return;
            var p = curve.get_Parameter(bip);
            if (p is null || p.IsReadOnly) return;
            p.Set(Mm(mm));
            checks.Add((p, mm, lp.Layer));
        }

        switch (lp.Kind)
        {
            case MepKind.Tuberia:
                Set(BuiltInParameter.RBS_PIPE_DIAMETER_PARAM, size);
                break;
            case MepKind.TuboElectrico:
                Set(BuiltInParameter.RBS_CONDUIT_DIAMETER_PARAM, size);
                break;
            case MepKind.Bandeja:
                Set(BuiltInParameter.RBS_CABLETRAY_WIDTH_PARAM, size);
                Set(BuiltInParameter.RBS_CABLETRAY_HEIGHT_PARAM, height);
                break;
            case MepKind.Conducto:
                if (curve.get_Parameter(BuiltInParameter.RBS_CURVE_DIAMETER_PARAM) is { IsReadOnly: false })
                    Set(BuiltInParameter.RBS_CURVE_DIAMETER_PARAM, size);
                else
                {
                    Set(BuiltInParameter.RBS_CURVE_WIDTH_PARAM, size);
                    Set(BuiltInParameter.RBS_CURVE_HEIGHT_PARAM, height);
                }
                break;
        }
    }
}

/// <summary>Cierra los avisos (no los errores) para no interrumpir con cientos de ventanas.</summary>
internal sealed class WarningDismisser : IFailuresPreprocessor
{
    private readonly BuildReport _report;
    public WarningDismisser(BuildReport report) => _report = report;

    public FailureProcessingResult PreprocessFailures(FailuresAccessor accessor)
    {
        bool resolved = false;
        foreach (var msg in accessor.GetFailureMessages())
        {
            if (msg.GetSeverity() == FailureSeverity.Error)
            {
                // Errores (no avisos): antes deshacían TODO sin decir por qué. Se anotan y, si Revit ofrece una
                // solución (p. ej. borrar el elemento que falla), se aplica para que el resto se conserve.
                _report.Problems.Add("Error de Revit: " + msg.GetDescriptionText() +
                                     (msg.HasResolutions() ? " (resuelto por Revit; se conserva el resto)" : " (sin solución automática)"));
                if (msg.HasResolutions()) { accessor.ResolveFailure(msg); resolved = true; }
                continue;
            }
            if (msg.GetSeverity() != FailureSeverity.Warning) continue;
            _report.WarningsDismissed++;
            if (_report.WarningsDismissed <= 10)
                _report.Problems.Add("Aviso de Revit: " + msg.GetDescriptionText());
            accessor.DeleteWarning(msg);
        }
        return resolved ? FailureProcessingResult.ProceedWithCommit : FailureProcessingResult.Continue;
    }
}
