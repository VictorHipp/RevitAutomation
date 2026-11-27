using System.Text;
using Autodesk.Revit.DB;
using CadMepRevit.Config;
using CadMepRevit.Core;

namespace CadMepRevit.Revit;

/// <summary>Textos de los informes finales de los dos botones (las cinco secciones pedidas y el cuadre).</summary>
public static class ModelReport
{
    private static double ToMm(double ft) => UnitUtils.ConvertFromInternalUnits(ft, UnitTypeId.Millimeters);

    /// <summary>Familia y tipo de un elemento creado, para el informe.</summary>
    public static string TypeLabel(Document doc, ElementId id)
    {
        var e = doc.GetElement(id);
        if (e is null) return "(elemento borrado)";
        if (e is FamilyInstance fi) return $"{fi.Symbol.FamilyName} : {fi.Symbol.Name}";
        if (e is DirectShape) return $"marcador 3D sin familia · categoría {e.Category?.Name}";
        return doc.GetElement(e.GetTypeId()) is ElementType t ? $"{t.FamilyName} : {t.Name}" : e.Name;
    }

    /// <summary>Datos para el inventario del CAD (cuadre), a partir de lo que se ha construido de verdad.</summary>
    public static InventoryInput InventoryInput(CadDrawing drawing, AutoPlanResult auto, BuildPlan plan, BuildReport report)
    {
        var segments = new Dictionary<int, NetSegment>();
        foreach (var s in plan.Network.Segments) segments.TryAdd(s.Index, s);
        var input = new InventoryInput
        {
            Drawing = drawing, UnitToMm = auto.UnitToMm, OffsetMm = plan.TranslationMm, ToleranceMm = plan.ToleranceMm,
            SkippedLayers = new Dictionary<string, string>(auto.SkippedLayers, StringComparer.OrdinalIgnoreCase)
        };
        foreach (var (_, index, layer, kind) in report.CurvesCreated)
            if (!plan.RisersBySegment.ContainsKey(index) && segments.TryGetValue(index, out var s))
                input.Created.Add(new ModeledRun(layer, s.Start, s.End, (s.WidthMm ?? 0) / 2, "Modelada: " + MepKinds.Label(kind)));
        foreach (var (index, layer, error) in report.SegmentErrors)
            if (!plan.RisersBySegment.ContainsKey(index) && segments.TryGetValue(index, out var s))
                input.Failed.Add(new ModeledRun(layer, s.Start, s.End, (s.WidthMm ?? 0) / 2, error));
        var created = report.CurvesCreated.Select(c => c.Segment).ToHashSet();
        var errors = report.SegmentErrors.ToDictionary(e => e.Segment, e => e.Error);
        if (plan.Risers is { } risers)
            foreach (var (entity, reason) in risers.Discarded) input.Circles[entity] = (false, reason);
        foreach (var (index, riser) in plan.RisersBySegment)
            foreach (int entity in riser.Entities)
                input.Circles[entity] = created.Contains(index) ? (true, "Modelada: círculo de bajante o montante")
                    : (false, "Vertical no creada: " + errors.GetValueOrDefault(index, "revise el resultado de la transacción"));
        foreach (var (block, _, done) in report.BlocksPlaced)
            input.Blocks[block.EntityIndex] = (true, done == BlockAction.Familia ? "Modelada: bloque como familia" : "Modelada: bloque como marcador 3D (sin familia)");
        foreach (var (block, error) in report.BlockErrors)
            input.Blocks[block.EntityIndex] = (false, "Revit no pudo colocar el bloque: " + error);
        foreach (var b in drawing.Blocks)
            if (!input.Blocks.ContainsKey(b.EntityIndex))
                input.Blocks[b.EntityIndex] = (false, "Bloque no colocado: " +
                    (auto.SkippedBlocks.TryGetValue((b.Layer, b.BlockName), out var why) ? why : "no está en el plan"));
        // Luminarias dibujadas con líneas: sus entidades (o el bloque de planta que las contiene) cuentan como modeladas
        // si su luminaria se ha colocado.
        foreach (var f in drawing.Fixtures)
        {
            var (placed, what) = input.Blocks.TryGetValue(f.SyntheticIndex, out var r) ? r : (false, "no está en el plan");
            string reason = placed ? "Modelada: luminaria dibujada con líneas (" + what.Replace("Modelada: bloque", "colocada") + ")"
                                   : "Luminaria dibujada con líneas no colocada: " + what.Replace("Bloque no colocado: ", "");
            foreach (int e in f.EntityIndices)
                if (!input.Fixtures.TryGetValue(e, out var prev) || !prev.Modeled && placed) input.Fixtures[e] = (placed, reason);
        }
        return input;
    }

    /// <summary>Informe de «Modelar desde CAD».</summary>
    public static string ForModel(Document doc, CadDrawing drawing, AutoPlanResult auto, BuildPlan plan, BuildReport report,
        InventoryResult inventory, UnderSlabPlacer? placer, string levelChoice, IEnumerable<string> extraDecisions)
    {
        var sb = new StringBuilder();
        sb.AppendLine("CAD-REV · MODELAR DESDE CAD · " + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss") + " · " + doc.Title);
        sb.AppendLine("Plano: " + drawing.Path);
        sb.AppendLine($"Planta: nivel '{plan.LevelName}' · Unidades: {plan.UnitsLabel} · Regla de colocación: {PlacementConfig.PLACEMENT_RULE} (tuberías y conductos)");
        sb.AppendLine();

        // 1) Modelados por sistema
        if (plan.UsesPipeNetwork)
        {
            if (plan.Slopes != null) sb.AppendLine(plan.Slopes.Report());
            if (plan.Risers is { } risers)
            {
                var verticalsMade = report.CurvesCreated.Where(c => plan.RisersBySegment.ContainsKey(c.Segment)).ToDictionary(c => c.Segment, c => c.Id);
                int represented = plan.RisersBySegment.Where(p => verticalsMade.ContainsKey(p.Key)).Sum(p => p.Value.Entities.Count);
                int planned = risers.Risers.Sum(r => r.Entities.Count);
                int nested = drawing.Entities.Count(e => e.Depth > 0 && e.Type.Equals("CIRCLE", StringComparison.OrdinalIgnoreCase));
                sb.AppendLine("BAJANTES Y MONTANTES");
                sb.AppendLine($"  Círculos leídos en espacio modelo: {planned + risers.Discarded.Count}; representados: {represented}; descartados: {risers.Discarded.Count}; pendientes por fallo de creación: {planned - represented}.");
                sb.AppendLine($"  Verticales previstas: {risers.Risers.Count}; modeladas: {verticalsMade.Count}; fallidas: {risers.Risers.Count - verticalsMade.Count}.");
                if (nested > 0) sb.AppendLine($"  Círculos dentro de bloques excluidos: {nested}.");
                foreach (var group in risers.Discarded.Values.GroupBy(r => r))
                    sb.AppendLine($"  {group.Count()} descartados: {group.Key}.");
                foreach (var (layer, center, circle, pipe) in risers.SmallUnlabeled)
                    sb.AppendLine($"  REVISAR '{layer}', {PipeNetworks.InPlan(center)} mm: círculo de Ø{circle:0.#} mm sobre un tubo de Ø{pipe:0.#} mm, " +
                                  "sin rótulo SUBE/BAJA: no se ha modelado. Si es una bajante o un montante, rotule SUBE o BAJA junto a él y repita.");
                foreach (var (index, r) in plan.RisersBySegment)
                {
                    var pipe = verticalsMade.TryGetValue(index, out var id) ? doc.GetElement(id) as Autodesk.Revit.DB.Plumbing.Pipe : null;
                    sb.AppendLine($"  '{r.Layer}', {PipeNetworks.InPlan(r.Base)} mm: {r.Description}; " +
                        (pipe is null ? "sin modelar." : $"Revit Ø nominal {ToMm(pipe.Diameter):0.##} mm, id {id}."));
                }
                sb.AppendLine();
            }
            sb.AppendLine("SISTEMAS Y TIPOS DE TUBERÍA POR RED");
            foreach (string note in plan.PipeNetworkNotes) sb.AppendLine("  " + note);
            sb.AppendLine();
        }
        if (report.HasAutomaticFittings)
        {
            sb.AppendLine("UNIONES");
            foreach (var (kind, created) in new[] { (JunctionKind.Codo, report.Elbows), (JunctionKind.Te, report.Tees),
                (JunctionKind.Cruz, report.Crosses), (JunctionKind.Transicion, report.Transitions) })
                sb.AppendLine($"  {kind}: {created} creadas, {report.FittingFailuresByKind.GetValueOrDefault(kind)} no creadas. Motivos en DIAGNÓSTICO.");
            if (plan.UsesPipeNetwork) sb.AppendLine($"  Encuentros de 4 tuberías sin unir: {report.PipeCrossesSkipped}.");
            var closed = plan.Network.Gaps.Where(g => g.Closed).ToList();
            sb.AppendLine($"Huecos cerrados: {closed.Count(g => g.Kind == GapKind.Esquina)} esquinas, " +
                $"{closed.Count(g => g.Kind == GapKind.Te)} tes, {closed.Count(g => g.Kind == GapKind.EnLinea)} en línea " +
                $"(alargamiento máximo {closed.Select(g => g.ExtensionMm).DefaultIfEmpty(0).Max():0.##} mm).");
            foreach (var gap in plan.Network.Gaps.Where(g => !g.Closed))
                sb.AppendLine($"  Hueco {gap.Kind} en '{gap.Layer}' en {gap.Point}: {gap.ExtensionMm:0.##} mm, " +
                    (gap.Rejection ?? "supera el límite aplicable entre los tramos") + ".");
            sb.AppendLine();
        }
        if (PlacementConfig.FollowsCeiling && (report.CeilingNotes.Count > 0 || report.CeilingCrossings.Count > 0 || report.FalseCeilingWarnings.Count > 0))
        {
            sb.AppendLine("ALTURA SEGÚN EL TECHO (parte de arriba a 100 mm bajo el forjado; planta del CAD)");
            sb.AppendLine($"  Tramos bajo el forjado: {report.CeilingFollowed}; subidas o bajadas: {report.CeilingSteps} " +
                          $"({report.CeilingElbows} codos creados, {report.CeilingElbowFailures} subidas sin codos); " +
                          $"extremos bajados para encontrarse en su unión: {report.CeilingLoweredEnds}.");
            if (report.CeilingWithoutSlab > 0)
                sb.AppendLine($"  {report.CeilingWithoutSlab} tramos sin forjado encima toman la cota de los tramos con los que se unen.");
            if (report.CeilingFallback > 0)
                sb.AppendLine($"  {report.CeilingFallback} tramos de redes sin ningún forjado encima quedan a la altura SUPUESTA ({plan.HeightAboveLevelMm:0} mm).");
            foreach (var note in report.CeilingNotes) sb.AppendLine("  " + note);
            foreach (var (title, items) in new[] { ("Atraviesan el forjado o quedan bajo el suelo", report.CeilingCrossings),
                         ("Por debajo del falso techo (no caben sobre el falso techo; se dejan bajo el forjado)", report.FalseCeilingWarnings) })
            {
                if (items.Count == 0) continue;
                sb.AppendLine($"  {title} ({items.Count}):");
                foreach (var i in items.Take(40)) sb.AppendLine("   - " + i);
                if (items.Count > 40) sb.AppendLine($"   … y {items.Count - 40} más.");
            }
            sb.AppendLine();
        }
        if (report.NetworkAccessories.Count > 0)
        {
            sb.AppendLine("ACCESORIOS, TERMINALES Y EQUIPOS DE RED");
            foreach (var family in report.NetworkAccessories.GroupBy(a => a.Family))
            {
                sb.AppendLine($"  '{family.Key}': {family.Count()} colocadas, {family.Count(a => a.Result == "insertada")} insertadas, " +
                    $"{family.Count(a => a.Result == "conectada")} conectadas, {family.Count(a => a.Result == "solo colocada")} solo colocadas.");
                foreach (var reason in family.Where(a => a.Result == "solo colocada").GroupBy(a => a.Detail))
                    sb.AppendLine($"    {reason.Count()} · {reason.Key}");
            }
            sb.AppendLine();
        }
        if (plan.Scope == ModelScope.Bandejas)
        {
            int joints = plan.Network.Junctions.Count(j => plan.Layers[j.Layer].Kind == MepKind.TuboElectrico);
            sb.AppendLine($"Tubos eléctricos: solo tramos rectos. El técnico resuelve los encuentros ({joints} en el plano).");
            sb.AppendLine();
        }
        if (plan.ChamberNotes.Count > 0)
        {
            sb.AppendLine("ARQUETAS Y ELEMENTOS DE DESAGÜE");
            foreach (string note in plan.ChamberNotes) sb.AppendLine("  " + note);
            sb.AppendLine();
        }
        sb.AppendLine("1. ELEMENTOS MODELADOS, POR SISTEMA (familia : tipo)");
        if (plan.UsesPipeNetwork)
        {
            var createdByIndex = report.CurvesCreated.ToDictionary(c => c.Segment);
            foreach (var group in plan.Network.Segments.GroupBy(s => plan.SegmentSystems[s.Index]))
            {
                string system = group.Key == ElementId.InvalidElementId ? "sin tipo de sistema compatible"
                    : doc.GetElement(group.Key)?.Name ?? "tipo de sistema no disponible";
                var curves = group.Where(s => createdByIndex.ContainsKey(s.Index)).Select(s => createdByIndex[s.Index]).ToList();
                sb.AppendLine($"  [Tubería · {system}] {curves.Count}/{group.Count()} tramos en capas: " +
                    string.Join(", ", group.Select(s => s.Layer).Distinct(StringComparer.OrdinalIgnoreCase).Order()));
                foreach (var type in curves.GroupBy(c => TypeLabel(doc, c.Id)).OrderByDescending(g => g.Count()))
                    sb.AppendLine($"     · {type.Count()} × Tubería · '{type.Key}'");
            }
        }
        foreach (var sys in plan.UsesPipeNetwork ? new List<MepBuilder.SystemGroup>() : MepBuilder.SystemGroups(doc, plan))
        {
            var curves = report.CurvesCreated.Where(c => sys.Layers.Contains(c.Layer)).ToList();
            var fittings = report.FittingsCreated.Where(f => sys.Layers.Contains(f.Layer)).ToList();
            int planned = plan.Network.Segments.Count(s => sys.Layers.Contains(s.Layer));
            sb.AppendLine($"  [{sys.Label}] capas: {string.Join(", ", sys.Layers.OrderBy(l => l))}");
            if (curves.Count == 0 && fittings.Count == 0) sb.AppendLine($"     · ningún elemento creado ({planned} tramos previstos)");
            foreach (var g in curves.GroupBy(c => TypeLabel(doc, c.Id)).OrderByDescending(g => g.Count()))
                sb.AppendLine($"     · {g.Count()} × {MepKinds.Label(g.First().Kind)} · '{g.Key}'");
            foreach (var g in fittings.GroupBy(f => (f.Kind, Type: TypeLabel(doc, f.Id))).OrderByDescending(g => g.Count()))
                sb.AppendLine($"     · {g.Count()} × {g.Key.Kind.ToString().ToLowerInvariant()} · '{g.Key.Type}'");
            if (PlacementConfig.Applies(sys.Kind) && curves.Count > 0)
            {
                var states = curves.Select(c => report.Placement.TryGetValue(c.Segment, out var p) ? p.Status : "sin regla").GroupBy(x => x);
                sb.AppendLine("     · colocación: " + string.Join("; ", states.Select(g => $"{g.Count()} {g.Key}")));
            }
        }
        if (report.BlocksPlaced.Count > 0)
        {
            sb.AppendLine("  [Bloques]");
            foreach (var g in report.BlocksPlaced.GroupBy(b => TypeLabel(doc, b.Id)).OrderByDescending(g => g.Count()))
                sb.AppendLine($"     · {g.Count()} × '{g.Key}'");
        }
        if (report.AutoConnections > 0) sb.AppendLine($"  Conexiones automáticas con equipos y terminales: {report.AutoConnections}");
        sb.AppendLine();

        // 2) No modelados con motivo
        sb.AppendLine("2. ELEMENTOS DEL PLANO NO MODELADOS, CON EL MOTIVO");
        var notModeled = inventory.Entities.Where(e => !e.Modeled).ToList();
        if (notModeled.Count == 0) sb.AppendLine("  (ninguno)");
        foreach (var g in notModeled.GroupBy(e => e.Reason).OrderByDescending(g => g.Count()))
        {
            sb.AppendLine($"  · {g.Count()} — {g.Key}");
            sb.AppendLine("      tipos: " + string.Join(", ", g.GroupBy(e => e.Type).OrderByDescending(t => t.Count()).Select(t => $"{t.Key}={t.Count()}")) +
                          " · capas: " + string.Join(", ", g.GroupBy(e => e.Layer).OrderByDescending(t => t.Count()).Take(8).Select(t => $"'{t.Key}'={t.Count()}")) +
                          (g.Select(e => e.Layer).Distinct().Count() > 8 ? " …" : ""));
        }
        foreach (var (layer, why) in auto.SkippedLayers.OrderBy(k => k.Key))
            sb.AppendLine($"  · Capa '{layer}' no construida: {why}");
        foreach (var ((layer, block), why) in auto.SkippedBlocks.OrderBy(k => k.Key.Layer))
            sb.AppendLine($"  · Bloque '{BlockNames.Clean(block)}' (capa '{layer}') no colocado: {why}");
        sb.AppendLine();

        // 3) Avisos
        sb.AppendLine("3. AVISOS");
        void Section(string title, IReadOnlyCollection<string> items, int max)
        {
            if (items.Count == 0) return;
            sb.AppendLine($"  {title} ({items.Count}):");
            foreach (var i in items.Take(max)) sb.AppendLine("   - " + i);
            if (items.Count > max) sb.AppendLine($"   … y {items.Count - max} más.");
        }
        if (placer?.Problem != null) sb.AppendLine("  Forjado de referencia: " + placer.Problem + ".");
        else if (placer != null) sb.AppendLine($"  Forjado de referencia: {placer.SlabCount} suelos del nivel '{placer.UpperLevelName}'." +
                                               (placer.Unreadable > 0 ? $" {placer.Unreadable} suelos no se pudieron leer y se ignoran." : ""));
        Section("Forjados no encontrados", report.SlabWarnings, 40);
        Section("Huecos del forjado bajo el recorrido (la colocación no se cambia)", report.HoleWarnings, 40);
        Section("Colocación", report.PlacementNotes, 20);
        Section("Plan", plan.Warnings.Concat(auto.Notices).ToList(), 40);
        Section("Revit (uniones, tamaños, conexiones…)", report.Problems, 100);
        Section("Lectura del CAD", drawing.Warnings, 20);
        if (plan.Layers.Count > 0)
            sb.AppendLine("  Medidas: las cotas DIMENSION y los rótulos asociados se aplican al modelar.");
        sb.AppendLine();

        // 4) Decisiones por defecto
        sb.AppendLine("4. DECISIONES POR DEFECTO (con su valor)");
        var decisions = new List<string> { levelChoice };
        decisions.AddRange(auto.DefaultDecisions);
        if (PlacementConfig.PLACEMENT_RULE == PlacementConfig.RuleUnderSlab)
        {
            decisions.Add("Orden de modelado por sistemas: tubería → conducto → bandeja → tubo eléctrico → muro. Orden usado: " + string.Join(" | ", report.SystemsOrder));
            decisions.Add($"Regla {PlacementConfig.PLACEMENT_RULE}: referencia = {PlacementConfig.Reference}" +
                          (placer?.UpperLevelName is string up ? $" (nivel '{up}')" : "") + ".");
            decisions.Add("Criterio de colocación: " + PlacementConfig.Criterion + ".");
            decisions.Add("Medida para la generatriz superior: " + PlacementConfig.OuterSize + ".");
            decisions.Add("Red conectada: " + PlacementConfig.SharedAxis + "; " + PlacementConfig.LowestUnderside + ".");
            decisions.Add($"Huecos: se comprueba el forjado cada {PlacementConfig.SampleStepMm:0} mm a lo largo de cada tramo.");
            decisions.Add("Sin forjado: " + PlacementConfig.NotFound + ".");
        }
        else
        {
            decisions.Add("Motor de modelado: el de CadMepRevit2 (tramos, después uniones, bloques y conexiones automáticas, en el orden del dibujo).");
            decisions.Add($"Regla {PlacementConfig.PLACEMENT_RULE}: " +
                          (PlacementConfig.FollowsCeiling ? PlacementConfig.FollowCeilingText : PlacementConfig.FixedHeightText) + ".");
        }
        decisions.Add("Sin confirmación antes de modificar el modelo: todo se hace en una sola operación (Ctrl+Z lo deshace).");
        decisions.AddRange(extraDecisions);
        foreach (var d in decisions) sb.AppendLine("  · " + d);
        if (auto.AssumedValues.Count > 0)
        {
            sb.AppendLine("  Valores SUPUESTOS que ya aplicaba la configuración existente:");
            foreach (var a in auto.AssumedValues) sb.AppendLine("   - " + a);
        }
        sb.AppendLine($"  Configuración existente aplicada sin mostrarla: altura sobre el nivel {PlanDefaults.HeightAboveLevelMm:0} mm " +
                      $"(eje de tuberías, conductos, bandejas y tubos; bloques), diámetro {PlanDefaults.PipeDiameterMm:0} mm, " +
                      $"conducto {PlanDefaults.DuctWidthMm:0}×{PlanDefaults.DuctHeightMm:0} mm, tolerancia {PlanDefaults.ToleranceMm:0} mm, " +
                      $"longitud mínima {PlanDefaults.MinLengthMm:0} mm, uniones {(PlanDefaults.CreateFittings ? "sí" : "no")}, " +
                      $"conexión automática {(PlanDefaults.AutoConnect ? "sí" : "no")}, Z del CAD {(PlanDefaults.UseCadZ ? "sí" : "no")}, " +
                      $"líneas dentro de bloques {(PlanDefaults.ReadLinesInsideBlocks ? "sí" : "no")}.");
        sb.AppendLine();

        // 5) Cuadre
        sb.AppendLine("5. CUADRE: INVENTARIO DEL CAD = MODELADOS + NO MODELADOS");
        sb.AppendLine($"  Entidades del plano: {inventory.Total} = modeladas {inventory.Modeled} + no modeladas {inventory.NotModeled} → " +
                      (inventory.Balanced ? "CUADRA" : "NO CUADRA (revise el detalle técnico)"));
        foreach (var g in inventory.Entities.GroupBy(e => e.Type).OrderByDescending(g => g.Count()))
            sb.AppendLine($"   {g.Key,-12} {g.Count(),6} = {g.Count(e => e.Modeled),6} modeladas + {g.Count(e => !e.Modeled),6} no modeladas");
        int planned2 = plan.Network.Segments.Count, made = report.CurvesCreated.Count, failed = report.SegmentErrors.Count;
        sb.AppendLine($"  Tramos previstos tras preparar la red: {planned2} = creados {made} + no creados por Revit {failed} → " +
                      (planned2 == made + failed ? "CUADRA" : "NO CUADRA"));
        sb.AppendLine($"  Elementos creados que ninguna entidad del plano explica: {inventory.UnexplainedRuns.Count} → " +
                      (inventory.UnexplainedRuns.Count == 0 ? "CUADRA"
                          : "NO CUADRA: la red los ha creado donde el plano no tiene líneas (los ha prolongado o ha cerrado un hueco); revíselos"));
        foreach (var r in inventory.UnexplainedRuns.Take(10)) sb.AppendLine($"   - capa '{r.Layer}' de {r.Start} a {r.End}");
        if (inventory.ClosingRuns.Count > 0)
            sb.AppendLine($"   ({inventory.ClosingRuns.Count} tramos de cierre: la red los crea entre dos tramos dibujados, en el hueco de una pieza " +
                          $"especial o de una curva de hasta {ModelInventory.MaxClosingMm:0} mm.)");
        if (inventory.MissingOrRepeated > 0) sb.AppendLine($"  Entidades sin destino o con varios: {inventory.MissingOrRepeated} → NO CUADRA");
        // Bloques: los no colocados se cuentan por su motivo explícito (no por diferencia), para que el cuadre pueda fallar.
        var placedIdx = report.BlocksPlaced.Select(b => b.Block.EntityIndex).ToHashSet();
        var errorIdx = report.BlockErrors.Select(b => b.Block.EntityIndex).ToHashSet();
        int blocks = drawing.Blocks.Count, placed = drawing.Blocks.Count(b => placedIdx.Contains(b.EntityIndex));
        int withReason = drawing.Blocks.Count(b => !placedIdx.Contains(b.EntityIndex) &&
                                                   (errorIdx.Contains(b.EntityIndex) || auto.SkippedBlocks.ContainsKey((b.Layer, b.BlockName))));
        sb.AppendLine($"  Bloques del plano: {blocks} = colocados {placed} + no colocados con motivo {withReason} → " +
                      (blocks == placed + withReason ? "CUADRA" : "NO CUADRA"));
        sb.AppendLine("  (Una entidad cuenta como modelada si la mayor parte de su longitud queda dentro de un elemento creado; una línea " +
                      "duplicada o solapada también, porque su geometría está en ese elemento. Un bloque, si se ha colocado como familia o marcador.)");
        return sb.ToString();
    }

    /// <summary>Guarda un informe en %LOCALAPPDATA%\CadMepRevit\{nombre}.txt y una copia con fecha en «informes». Devuelve la ruta o "".</summary>
    public static string Save(string text, string name)
    {
        try
        {
            string dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "CadMepRevit");
            Directory.CreateDirectory(dir);
            string file = Path.Combine(dir, name + ".txt");
            var enc = new UTF8Encoding(true);
            File.WriteAllText(file, text, enc);
            try
            {
                string history = Path.Combine(dir, "informes");
                Directory.CreateDirectory(history);
                File.WriteAllText(Path.Combine(history, name + "_" + DateTime.Now.ToString("yyyyMMdd_HHmmss") + ".txt"), text, enc);
            }
            catch { /* la copia con fecha es una ayuda */ }
            return file;
        }
        catch { return ""; }
    }

    /// <summary>Altura (mm) de un valor en pies respecto a un nivel, para textos.</summary>
    public static string Height(double ft, double levelFt) => $"{ToMm(ft - levelFt):0} mm";
}
