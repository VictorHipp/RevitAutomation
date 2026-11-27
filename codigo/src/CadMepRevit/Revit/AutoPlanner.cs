using System.Globalization;
using Autodesk.Revit.DB;
using CadMepRevit.Core;
using CadMepRevit.Ui;

namespace CadMepRevit.Revit;

/// <summary>Plan preparado sin ventanas y lo que no se va a construir, con su motivo.</summary>
public sealed class AutoPlanResult
{
    public BuildPlan? Plan { get; set; }
    public DrawingUnit Unit { get; set; }
    public double UnitToMm { get; set; } = 1;
    /// <summary>Capas con líneas que no se construyen, y por qué.</summary>
    public Dictionary<string, string> SkippedLayers { get; } = new(StringComparer.OrdinalIgnoreCase);
    /// <summary>Bloques (capa, nombre) que no se colocan, y por qué.</summary>
    public Dictionary<(string Layer, string Block), string> SkippedBlocks { get; } = new();
    /// <summary>Decisiones tomadas sin preguntar porque el código anterior no fijaba el valor (con su valor).</summary>
    public List<string> DefaultDecisions { get; } = new();
    /// <summary>Valores SUPUESTOS que la configuración existente ya aplicaba (alto de conductos, de marcadores…).</summary>
    public List<string> AssumedValues { get; } = new();
    /// <summary>Avisos que no impiden modelar.</summary>
    public List<string> Notices { get; } = new();
    /// <summary>Si no hay nada que construir, por qué.</summary>
    public string? Error { get; set; }
}

/// <summary>
/// Prepara el plan de construcción SIN ventanas, con exactamente lo que el asistente (ImportForm) propone al
/// abrirse: qué es cada capa, sección, tipo, sistema, doble línea, medidas, bloques y ajustes generales
/// (<see cref="PlanDefaults"/>). No cambia ningún valor: solo deja de mostrarlos.
/// </summary>
public static class AutoPlanner
{
    /// <summary>¿Coloca este botón el bloque? Los bloques sin función reconocida pero con familia cargada por su nombre van con «Equipos y PCI».</summary>
    public static bool BlockInScope(ModelScope? scope, BlockRole role, string block, bool hasFamily) =>
        scope is not ModelScope s || ModelScopes.Includes(s, role, block) || role == BlockRole.Otro && hasFamily && s == ModelScope.EquiposPci;

    public static AutoPlanResult Create(CadDrawing drawing, RevitCatalog catalog, (string Name, ElementId Id, double ElevationFt) level,
        ModelScope? scope = null, double gapScale = 1)
    {
        var res = new AutoPlanResult();
        var unit = drawing.HeaderUnit ?? DrawingUnit.Milimetros;
        res.Unit = unit;
        if (drawing.UnitCorrection.Length > 0)
            res.Notices.Add(drawing.UnitCorrection);
        else if (drawing.HeaderUnit is null)
            res.DefaultDecisions.Add($"Unidades del dibujo: el archivo no las indica ($INSUNITS = {drawing.HeaderUnitText}); se usan milímetros (mm).");
        double k = DrawingUnits.ToMillimeters(unit);
        res.UnitToMm = k;
        if (LayerPlanning.UnitsLookWrong(drawing, unit) is string unitWarn)
            res.Notices.Add("Unidades: " + unitWarn.Replace(" Revise 'Unidades del dibujo'.", "") +
                            $" Se ha usado la unidad del archivo ({DrawingUnits.Label(unit)}), sin cambiarla.");
        double tol = PlanDefaults.ToleranceMm;
        var byLayer = LayerPlanning.ByLayer(drawing);
        var translation = LayerPlanning.Translation(drawing, k, scope == ModelScope.Tuberias);

        var plan = new BuildPlan
        {
            Scope = scope,
            LevelId = level.Id,
            LevelName = level.Name,
            LevelElevationFt = level.ElevationFt,
            HeightAboveLevelMm = PlanDefaults.HeightAboveLevelMm,
            CreateFittings = PlanDefaults.CreateFittings,
            AutoConnect = PlanDefaults.AutoConnect,
            TranslationMm = translation,
            SourcePath = drawing.Path,
            UnitsLabel = DrawingUnits.Label(unit),
            ToleranceMm = tol,
            MinLengthMm = PlanDefaults.MinLengthMm
        };
        double? storeyMm = NextLevelHeightMm(catalog, level);

        // ---- Capas: lo que el asistente propone en la tabla de capas
        var candidateLayers = plan.UsesPipeNetwork
            ? drawing.SegmentLayers.Concat(drawing.Circles.Select(c => c.Layer).Where(l => PipeNetworks.LayerKind(l) == MepKind.Tuberia))
                .Distinct(StringComparer.OrdinalIgnoreCase).ToList()
            : drawing.SegmentLayers.ToList();
        foreach (var layer in candidateLayers)
        {
            var kind = plan.UsesPipeNetwork ? PipeNetworks.LayerKind(layer) : MepKinds.Suggest(layer);
            if (kind == MepKind.NoImportar)
            {
                res.SkippedLayers[layer] = "su nombre no corresponde a nada que se construya (anotación, rótulos y sus directrices, arquitectura de referencia u otra)";
                continue;
            }
            if (scope is ModelScope sc && !ModelScopes.Includes(sc, kind))
            {
                string owner = ModelScopes.OwnerOf(kind);
                res.SkippedLayers[layer] = $"es {MepKinds.Label(kind)}: " + (owner.Length > 0 ? $"lo modela el botón {owner}" : "no lo modela ningún botón de la cinta");
                continue;
            }
            if (LayerPlanning.CenterlineDuplicateOf(layer, drawing.SegmentLayers) is string outlineLayer && MepKinds.Suggest(outlineLayer) == kind)
            {
                res.SkippedLayers[layer] = $"son los ejes de la capa '{outlineLayer}', que ya se modela con su contorno (se crearía cada tramo dos veces)";
                continue;
            }
            if (catalog.Types[kind].Count == 0)
            {
                res.SkippedLayers[layer] = $"sería {MepKinds.Label(kind)}, pero " + RevitCatalog.HowToGetTypes(kind);
                continue;
            }
            // Sección: en conductos, la primera disponible (rectangular, redonda, oval); el resto es fija.
            List<NamedId> types;
            string shapeText = "";
            if (kind == MepKind.Conducto)
            {
                var shapes = catalog.DuctShapesAvailable();
                var shape = shapes.Count > 0 ? shapes[0] : ConnectorProfileType.Rectangular;
                // Conductos redondos: lo dicen las piezas dibujadas como bloque («M_Codo redondo») o los rótulos «Ø125».
                if (shapes.Contains(ConnectorProfileType.Round) && LooksRoundDuct(drawing, layer))
                {
                    shape = ConnectorProfileType.Round;
                    res.DefaultDecisions.Add($"Capa '{layer}': conductos REDONDOS (sus piezas o sus rótulos son redondos).");
                }
                types = catalog.DuctTypesOf(shape);
                shapeText = shape == ConnectorProfileType.Round ? "redonda" : shape == ConnectorProfileType.Oval ? "oval" : "rectangular";
                if (types.Count == 0)
                {
                    res.SkippedLayers[layer] = $"el proyecto no tiene tipos de conducto de sección {shapeText}";
                    continue;
                }
            }
            else types = catalog.Types[kind];

            // Tipo: el primero (por nombre) con todas sus uniones definidas; muros: automático por espesor.
            ElementId typeId = kind == MepKind.Muro
                ? ElementId.InvalidElementId
                : (types.FirstOrDefault(t => catalog.FittingWarning(t.Name, t.Id) is null) ?? types[0]).Id;

            ElementId? systemId = null;
            if (RevitCatalog.NeedsSystem(kind))
            {
                var systems = catalog.Systems[kind];
                if (systems.Count == 0)
                {
                    res.SkippedLayers[layer] = RevitCatalog.HowToGetTypes(kind);
                    continue;
                }
                if (!plan.UsesPipeNetwork)
                {
                    string? name = ImportForm.SuggestSystem(layer, systems.Select(s => s.Name).ToList());
                    systemId = systems.FirstOrDefault(s => s.Name == name)?.Id;
                    if (systemId is null)
                    {
                        res.SkippedLayers[layer] = "no se pudo proponer un tipo de sistema";
                        continue;
                    }
                }
            }

            var segs = byLayer[layer].ToList();
            bool doubleLine = LayerPlanning.LooksDoubleLine(segs, kind, k, tol);
            bool widthHeight = catalog.UsesWidthHeight(kind, typeId);
            double size = 0;
            if (!doubleLine && kind != MepKind.Muro)
                size = widthHeight ? DefaultWidthMm(kind) : DefaultDiameterMm(kind);
            double height = 0;
            bool heightAssumed = false;
            if (widthHeight)
            {
                // Muros: altura hasta el nivel superior (dato del modelo, redondeado a mm como en la tabla).
                if (kind == MepKind.Muro && storeyMm is double h)
                    height = double.Parse(h.ToString("0", CultureInfo.InvariantCulture), CultureInfo.InvariantCulture);
                else
                {
                    height = DefaultHeightMm(kind);
                    heightAssumed = true;
                }
            }
            if (widthHeight && heightAssumed && height > 0)
            {
                plan.Warnings.Add($"Capa '{layer}': alto {height:0} mm SUPUESTO (el plano en planta no lo indica).");
                res.AssumedValues.Add($"Capa '{layer}' ({MepKinds.Label(kind)}): alto {height:0} mm (el plano en planta no lo indica).");
            }
            if (!doubleLine && size > 0 && kind != MepKind.Muro)
                res.AssumedValues.Add($"Capa '{layer}' ({MepKinds.Label(kind)}{(shapeText.Length > 0 ? ", sección " + shapeText : "")}): " +
                                      (widthHeight ? "ancho" : "diámetro") + $" {size:0.#} mm (valor por defecto para los tramos sin medida del plano).");
            plan.Layers[layer] = new LayerPlan
            {
                Layer = layer, Kind = kind, SystemTypeId = systemId, TypeId = typeId,
                SizeMm = size, HeightMm = height, DoubleLine = doubleLine
            };
        }

        // ---- Bloques: lo que el asistente propone en la tabla de bloques
        foreach (var (layer, block) in drawing.BlockGroups)
        {
            var refs = drawing.Blocks.Where(b => Eq(b.Layer, layer) && Eq(b.BlockName, block)).ToList();
            var role = BlockNames.Role(block, layer);
            var foot = refs.Select(b => b.Footprint).FirstOrDefault(x => x != null);
            string? match = catalog.MatchBlock(block);
            if (role == BlockRole.PiezaDeUnion)
            {
                res.SkippedBlocks[(layer, block)] = "pieza de unión dibujada como bloque: Revit la crea al unir los tramos";
                continue;
            }
            if (!BlockInScope(scope, role, block, match != null))
            {
                string owner = ModelScopes.OwnerOf(role, block);
                res.SkippedBlocks[(layer, block)] = BlockNames.IsLegendOrReference(block) ? "es una leyenda o una referencia externa, no un elemento"
                    : BlockNames.IsAnnotationLayer(layer) ? "está en una capa de anotaciones (símbolo, no elemento)"
                    : owner.Length > 0 ? $"es {BlockNames.Label(role)}: lo coloca el botón {owner}"
                    : "no se reconoce su función (equipo, terminal, luminaria, PCI…) y no hay familia cargada con su nombre";
                continue;
            }
            if (role is BlockRole.Luminaria or BlockRole.MandoIluminacion or BlockRole.Pci or BlockRole.Electrico &&
                foot is { } big && Math.Max(big.SizeX, big.SizeY) * k > PlanDefaults.MaxApplianceFootprintMm)
            {
                res.SkippedBlocks[(layer, block)] = $"mide {Math.Max(big.SizeX, big.SizeY) * k / 1000:0.#} m: es un dibujo entero insertado como bloque, no un aparato";
                continue;
            }
            string category = ImportForm.CategoryFor(role, block);
            var cat = ImportForm.MarkerCategories.FirstOrDefault(c => c.Label == category);
            var (markerHeight, assumed) = ImportForm.DefaultMarkerHeight(block, role);
            double markerH = double.Parse(markerHeight.ToString("0", CultureInfo.InvariantCulture), CultureInfo.InvariantCulture);
            bool chamber = (plan.UsesPipeNetwork && role == BlockRole.Sanitario && BlockNames.IsDrainageChamber(block))
                || scope == ModelScope.EquiposPci && BlockNames.IsElectricalChamber(block, layer);
            if (chamber)
            {
                var (depth, depthAssumed) = BlockNames.ChamberDepth(block);
                markerH = depth;
                plan.ChamberNotes.Add($"'{BlockNames.Clean(block)}', capa '{layer}': {refs.Count} bloques con tapa a cota 0 mm del nivel; " +
                    $"profundidad {depth:0} mm {(depthAssumed ? "SUPUESTA; compruebe la medida" : "leída del nombre")}.");
                assumed = depthAssumed;
            }
            var (elevMm, elevAssumed) = BlockElevations.For(role, block);
            bool followsNetwork = NetworkBlocks.For(scope, role, block) != null;
            double elevation = double.Parse(elevMm.ToString("0", CultureInfo.InvariantCulture), CultureInfo.InvariantCulture);
            // Luminarias y detectores de techo: a la cota real del techo (falso techo o forjado, del proyecto o vinculado);
            // la altura supuesta solo vale donde no haya ninguno encima.
            bool useCeiling = role is BlockRole.Luminaria or BlockRole.MandoIluminacion or BlockRole.Pci && BlockElevations.IsCeilingMounted(role, block);
            if (elevAssumed)
                res.AssumedValues.Add($"Bloque '{BlockNames.Clean(block)}' (capa '{layer}', {BlockNames.Label(role)}): " +
                                      (useCeiling ? $"a la cota del falso techo o del forjado de encima; donde no haya ninguno, {elevation:0} mm sobre el nivel (supuesto)."
                                                  : $"altura {elevation:0} mm sobre el nivel (el plano en planta no la indica; valor habitual)."));
            if (match is null)
            {
                if (foot is null || role == BlockRole.Otro)
                {
                    res.SkippedBlocks[(layer, block)] = foot is null
                        ? "no hay familia cargada con su nombre y el bloque no tiene geometría medible"
                        : "no hay familia cargada con su nombre y no se reconoce su función (equipo, terminal, aparato…)";
                    continue;
                }
                plan.BlockPlans.Add(new BlockPlan
                {
                    Layer = layer, BlockName = block, Action = BlockAction.Marcador,
                    MarkerCategory = cat.Label is null ? BuiltInCategory.OST_GenericModel : cat.Cat,
                    HeightMm = markerH, IsChamber = chamber, ElevationMm = elevation, CeilingMounted = !followsNetwork && BlockElevations.IsCeilingMounted(role, block), UseCeilingHeight = useCeiling
                });
                if (assumed)
                    res.AssumedValues.Add($"Bloque '{BlockNames.Clean(block)}' (capa '{layer}'): sin familia cargada → marcador 3D '{category}', alto {markerH:0} mm (supuesto).");
                continue;
            }
            var symbolId = catalog.FamilySymbols.FirstOrDefault(s => s.Name == match)?.Id;
            if (symbolId is null)
            {
                res.SkippedBlocks[(layer, block)] = "la familia propuesta no está en el proyecto";
                continue;
            }
            plan.BlockPlans.Add(new BlockPlan
            {
                Layer = layer, BlockName = block, Action = BlockAction.Familia, SymbolId = symbolId, ElevationMm = elevation,
                HeightMm = markerH > 0 ? markerH : markerHeight, IsChamber = chamber,
                MarkerCategory = cat.Label is null ? BuiltInCategory.OST_GenericModel : cat.Cat,
                CeilingMounted = !followsNetwork && BlockElevations.IsCeilingMounted(role, block), UseCeilingHeight = useCeiling
            });
        }

        if (plan.Layers.Count == 0 && plan.BlockPlans.Count == 0)
        {
            res.Error = scope is ModelScope only
                ? $"En este plano no hay nada que modele el botón «{ModelScopes.Label(only)}» (o el proyecto no tiene los tipos necesarios)."
                : "Ninguna capa ni ningún bloque del plano corresponde a algo que se pueda construir en este proyecto.";
            res.Plan = plan;
            return res;
        }

        // ---- Red de tramos: igual que el asistente al pulsar «Previsualizar», pero agrupando por capa una sola vez
        var input = LayerPlanning.NetworkInput(byLayer, plan.Layers.Values.Select(lp => (lp.Layer, lp.Kind, lp.DoubleLine)), k, tol, plan.Outlines);
        var originalPipeSizes = new Dictionary<(string Layer, P3 Start, P3 End, double? Width), (double ValueMm, double? HeightMm, string Source)?>();
        plan.Network = NetworkBuilder.Build(input, new NetworkBuilder.Settings
        {
            UnitToMm = k, ToleranceMm = tol, MinLengthMm = PlanDefaults.MinLengthMm,
            FlattenZ = !PlanDefaults.UseCadZ, OffsetMm = translation,
            TransitionHints = LayerPlanning.TransitionHints(drawing),
            ThicknessLayers = plan.Layers.Values.Where(lp => lp.Kind == MepKind.Muro).Select(lp => lp.Layer).ToList(),
            GapLayers = scope is ModelScope.Tuberias or ModelScope.Conductos or ModelScope.Bandejas
                ? plan.Layers.Values.Where(lp => ModelScopes.AllowsAnyFitting(scope, lp.Kind))
                    .ToDictionary(lp => lp.Layer, lp => (lp.Kind, LayerSize(lp)), StringComparer.OrdinalIgnoreCase)
                : null,
            GapScale = gapScale,
            PrepareGapValidation = plan.UsesPipeNetwork ? ValidatePipeGaps : null,
            ReadSizesBeforeMerge = scope is ModelScope.Tuberias or ModelScope.Conductos
                ? segments => SizesBeforeMerge(drawing, plan, catalog, segments, k, translation, tol)
                : null
        });
        // Los muros no llevan piezas de unión: Revit los une solo en sus extremos.
        plan.Network.Junctions.RemoveAll(j => plan.Layers.TryGetValue(j.Layer, out var jl) && jl.Kind == MepKind.Muro);
        // Lo que se ha limpiado del dibujo para que las uniones salgan: se dice, porque cambia lo que se modela.
        int bodies = plan.Outlines.Values.Sum(o => o.FittingBodies);
        if (bodies + plan.Network.CoaxialResolved + plan.Network.StubsAbsorbed > 0)
            res.DefaultDecisions.Add($"Piezas dibujadas con líneas (tes, manguitos, embocaduras): {bodies} cuerpos de pieza no se modelan como tramo; " +
                                     $"{plan.Network.CoaxialResolved} tramos leídos dos veces (el tubo y, por fuera, la pieza o el aislamiento) quedan en uno; " +
                                     $"{plan.Network.StubsAbsorbed} trocitos cortos de otro ancho toman el de su tubo. " +
                                     "Donde el plano dibuja un bloque de transición o reductor, el cambio de ancho se respeta.");
        // Tuberías a doble línea: cruces que el plano dibuja cortando el tubo de debajo, y restos de testa descartados.
        var crossings = plan.Outlines.Values.SelectMany(o => o.CrossingsJoined).ToList();
        int tinyLines = plan.Outlines.Values.Sum(o => o.TinyLines);
        if (crossings.Count > 0)
            res.DefaultDecisions.Add($"Cruces de tuberías: {crossings.Count} tubos cortados en el plano donde pasa otro se modelan rectos y SIN unir " +
                                     "al que pasa (el plano oculta el de debajo: van a distinta cota). Si en alguno sí hay unión, póngala a mano. " +
                                     "Ejemplos (mm, como las uniones): " + string.Join("; ", crossings.Take(5).Select(p => p + translation)
                                         .Select(p => $"({p.X:0}, {p.Y:0})")) + ".");
        if (tinyLines > 0)
            res.DefaultDecisions.Add($"Doble línea: {tinyLines} líneas de menos de 5 mm (restos de testas o de piezas) no se toman como bordes de tubo.");
        foreach (var b in drawing.Blocks.Where(b => plan.BlockPlans.Any(p => BuildPlan.Same(p, b))))
        {
            var pos = new P3(b.Position.X * k, b.Position.Y * k, PlanDefaults.UseCadZ ? b.Position.Z * k : 0) + translation;
            var f = b.Footprint;
            var foot = f is null ? null
                : new Footprint(f.CenterX * k + translation.X, f.CenterY * k + translation.Y, f.AngleRad, f.SizeX * k, f.SizeY * k);
            plan.Blocks.Add((b, pos, foot));
        }
        if (plan.CreateFittings && scope is not (ModelScope.Tuberias or ModelScope.Conductos))
        {
            foreach (var lp in plan.Layers.Values)
            {
                if (!ModelScopes.AllowsAnyFitting(scope, lp.Kind)) continue;
                int joints = plan.Network.Junctions.Count(j => Eq(j.Layer, lp.Layer) && plan.AllowsFitting(lp.Kind, j.Kind));
                var typeName = catalog.Types[lp.Kind].FirstOrDefault(t => t.Id == lp.TypeId)?.Name ?? "";
                var warn = catalog.FittingWarning(typeName, lp.TypeId);
                if (joints > 0 && warn != null) plan.Warnings.Add($"Capa '{lp.Layer}' ({joints} uniones): {warn}");
            }
        }
        var explicitDucts = scope == ModelScope.Conductos ? ReadDuctChanges(drawing, plan, catalog, k, translation, tol) : [];
        AssignSizesFromLabels(drawing, plan, catalog, k, translation, tol, res);
        if (originalPipeSizes.Count > 0 && plan.Network.Gaps.Any(g => g.Closed))
            foreach (var s in plan.Network.Segments)
                if (originalPipeSizes.TryGetValue((s.Layer, s.Start, s.End, s.WidthMm), out var previous))
                {
                    if (previous.HasValue) plan.SegmentSizes[s.Index] = previous.Value;
                    else plan.SegmentSizes.Remove(s.Index);
                }
        if (scope is ModelScope.Tuberias or ModelScope.Conductos or ModelScope.Bandejas)
            NetworkBuilder.ReclassifySizes(plan.Network, s => scope == ModelScope.Bandejas &&
                !plan.Network.Gaps.Any(g => g.Closed && g.Kind == GapKind.EnLinea &&
                    Eq(g.Layer, s.Layer) && Math.Min(s.Start.DistanceTo(g.Point), s.End.DistanceTo(g.Point)) <= tol)
                    ? null : EffectiveSize(plan, catalog, s));
        foreach (var (joint, note) in explicitDucts)
            joint.MarkUnavailableTransition(note);
        if (scope == ModelScope.Tuberias)
        {
            var pipes = plan.Network.Segments.Where(s => plan.Layers[s.Layer].Kind == MepKind.Tuberia).ToList();
            var networks = PipeNetworks.Infer(drawing, pipes, plan.Network.Junctions, k, translation, tol);
            foreach (var network in networks) AssignPipeNetwork(plan, catalog, network, network.Segments);
            plan.Slopes = SolvePipeSlopes(plan, pipes, reportAssumptions: true);
            double? Adjacent(bool above) => catalog.Levels.Select(l => (double?)((l.ElevationFt - level.ElevationFt) * 304.8))
                .Where(mm => above ? mm > SlopeRules.Epsilon : mm < -SlopeRules.Epsilon)
                .OrderBy(mm => Math.Abs(mm!.Value)).FirstOrDefault();
            plan.Risers = PipeRisers.Plan(drawing, pipes, plan.Slopes, plan.Layers.Keys.ToHashSet(StringComparer.OrdinalIgnoreCase),
                k, translation, tol, Adjacent(true), Adjacent(false));
            foreach (var (original, piece) in PipeRisers.Split(plan.Network, plan.Slopes, plan.Risers.Risers))
            {
                CopyPipeChoice(plan, original, piece.Index);
                if (plan.SegmentSizes.TryGetValue(original, out var size)) plan.SegmentSizes[piece.Index] = size;
            }

            int nextNetwork = networks.Count + 1;
            foreach (var riser in plan.Risers.Risers)
            {
                int index = PipeRisers.Append(plan.Network, plan.Slopes, riser);
                plan.RisersBySegment[index] = riser;
                if (riser.AttachedSegment is int source) CopyPipeChoice(plan, source, index);
                else AssignPipeNetwork(plan, catalog,
                    PipeNetworks.InferLoose(drawing, nextNetwork++, riser.Layer, PipeNetworks.InPlan(riser.Base), k, translation, tol), new[] { index });
                if (riser.NominalMm is double d) plan.SegmentSizes[index] = (d, null, "rótulo de bajante");
                PipeRisers.AddJunction(plan.Network, plan.Slopes, riser, index, tol);
            }
        }
        if (scope is ModelScope.Tuberias or ModelScope.Conductos or ModelScope.Bandejas)
        {
            var byKind = plan.Network.Segments.GroupBy(s => plan.Layers[s.Layer].Kind)
                .ToDictionary(g => g.Key, g => g.Select(s => (s, EffectiveSize(plan, catalog, s).Width)).ToList());
            foreach (var (block, pos, foot) in plan.Blocks)
            {
                if (NetworkBlocks.For(scope, BlockNames.Role(block.BlockName, block.Layer), block.BlockName) is not { } use) continue;
                if (byKind.TryGetValue(use.Kind, out var network))
                {
                    var centre = foot == null ? pos : new P3(foot.CenterX, foot.CenterY, pos.Z);
                    bool useInsert = foot == null || foot.Contains(pos.X, pos.Y, PlanDefaults.BlockFootprintToleranceFactor * Math.Max(foot.SizeX, foot.SizeY));
                    var link = NetworkBlocks.Associate(useInsert ? pos : centre, use.Use, tol, network, block.RotationRadians,
                        useInsert ? null : foot);
                    if (link == null && useInsert && centre != pos)
                        link = NetworkBlocks.Associate(centre, use.Use, tol, network, block.RotationRadians, foot);
                    if (link != null) { plan.BlockLinks[block] = link; continue; }
                }
                res.AssumedValues.Add($"Bloque '{BlockNames.Clean(block.BlockName)}' en {pos}: sin tramo próximo de su red. Cota de colocación SUPUESTA.");
            }
        }
        if (translation.Length > 0)
            res.Notices.Add($"El dibujo está a más de 10 km del origen: se acerca X={translation.X / 1000:0.###} m, Y={translation.Y / 1000:0.###} m (configuración existente).");
        if (plan.Network.Segments.Count == 0 && plan.Blocks.Count == 0)
            res.Error = "Después de limpiar el dibujo no queda ningún tramo: puede que las unidades sean incorrectas (tramos demasiado cortos).";
        res.Plan = plan;
        return res;

        SlopeSolution SolvePipeSlopes(BuildPlan pipePlan, IReadOnlyList<NetSegment> pipes, bool reportAssumptions)
        {
            var annotations = SlopeAnnotations.Associate(drawing, pipes, k, translation, tol,
                s =>
                {
                    bool has = pipePlan.SegmentSizes.TryGetValue(s.Index, out var size);
                    bool useNominal = has && (size.Source.StartsWith("cota", StringComparison.Ordinal) || s.WidthMm == null ||
                        Math.Abs(size.ValueMm - s.WidthMm.Value) <= PlanDefaults.PipeNominalOutlineFraction * s.WidthMm.Value);
                    double nominal = has ? size.ValueMm : pipePlan.Layers[s.Layer].SizeMm;
                    double outer = catalog.PipeOuterMm(pipePlan.SegmentPipeTypes.GetValueOrDefault(s.Index) ?? pipePlan.Layers[s.Layer].TypeId,
                        nominal, s.WidthMm, useNominal, out bool assumed);
                    if (assumed && reportAssumptions) pipePlan.Warnings.Add($"Cota inferior junto a {s.Start}: diámetro exterior {outer:0.##} mm SUPUESTO igual al nominal; el tipo no tiene tamaños de segmento. Cargue el segmento correcto y revise la cota de eje.");
                    return outer;
                });
            bool documentBuried = SlopeSolver.IsBuried(drawing, Array.Empty<string>());
            var buriedLayers = pipePlan.Layers.Keys.ToDictionary(l => l, l => PipeRisers.IsBuriedLayer(l, documentBuried), StringComparer.OrdinalIgnoreCase);
            return SlopeSolver.Solve(pipes, annotations, tol, isBuriedLayer: l => buriedLayers[l]);
        }

        Func<GapProposal, string?> ValidatePipeGaps(NetworkResult network)
        {
            var initial = new BuildPlan { Scope = ModelScope.Tuberias, Network = network };
            foreach (var (layer, lp) in plan.Layers) initial.Layers[layer] = lp;
            AssignSizesFromLabels(drawing, initial, catalog, k, translation, tol, new AutoPlanResult());
            foreach (var s in network.Segments)
                originalPipeSizes[(s.Layer, s.Start, s.End, s.WidthMm)] = initial.SegmentSizes.TryGetValue(s.Index, out var size) ? size : null;
            var services = PipeNetworks.Infer(drawing, network.Segments, network.Junctions, k, translation, tol);
            foreach (var service in services) AssignPipeNetwork(initial, catalog, service, service.Segments);
            var slopes = SolvePipeSlopes(initial, network.Segments, reportAssumptions: false);
            return gap =>
            {
                int a = gap.First.Index, b = gap.Other.Index;
                if (initial.SegmentSystems.GetValueOrDefault(a) != initial.SegmentSystems.GetValueOrDefault(b))
                    return "los tramos pertenecen a sistemas distintos";
                if (initial.SegmentPipeTypes.GetValueOrDefault(a) != initial.SegmentPipeTypes.GetValueOrDefault(b))
                    return "el cierre cambiaría el tipo de la red de tubería";
                if (!slopes.Pipes.TryGetValue(a, out var sa) || !slopes.Pipes.TryGetValue(b, out var sb)) return null;
                if (Math.Abs(AxisElevation(sa, gap.Point) - AxisElevation(sb, gap.Point)) > SlopeRules.Epsilon)
                    return "los ejes prolongados tienen cotas distintas; el cierre afectaría a las pendientes existentes";
                if (gap.Kind != GapKind.Te && Math.Abs(sa.PlannedRatio - sb.PlannedRatio) > PlanDefaults.PipeSlopeToleranceRatio)
                    return "los recorridos que se unirían tienen pendientes distintas";
                return null;
            };
        }
    }

    private static double AxisElevation(SlopedPipe pipe, P3 at)
    {
        var start = PipeNetworks.InPlan(pipe.Start);
        var axis = PipeNetworks.InPlan(pipe.End) - start;
        double length2 = axis.Dot(axis);
        double along = length2 > 0 ? (PipeNetworks.InPlan(at) - start).Dot(axis) / length2 : 0;
        return pipe.Start.Z + along * (pipe.End.Z - pipe.Start.Z);
    }

    private static double LayerSize(LayerPlan layer) => layer.SizeMm > 0 ? layer.SizeMm :
        layer.HeightMm > 0 ? DefaultWidthMm(layer.Kind) : DefaultDiameterMm(layer.Kind);

    private static (double Width, double? Height) EffectiveSize(BuildPlan plan, RevitCatalog catalog, NetSegment segment)
    {
        var layer = plan.Layers[segment.Layer];
        bool found = plan.SegmentSizes.TryGetValue(segment.Index, out var size);
        double width = (plan.DuctLabelSizes.Contains(segment.Index) ? null : segment.WidthMm) ??
            (found ? size.ValueMm : LayerSize(layer));
        if (found && layer.Kind == MepKind.Tuberia &&
            (size.Source.StartsWith("cota", StringComparison.Ordinal) || size.Source == "rótulo de bajante" ||
             segment.WidthMm is double outer && Math.Abs(size.ValueMm - outer) <= PlanDefaults.PipeNominalOutlineFraction * outer))
            width = size.ValueMm;
        return (width, catalog.UsesWidthHeight(layer.Kind, plan.SegmentDuctTypes.GetValueOrDefault(segment.Index) ?? layer.TypeId)
            ? size.HeightMm ?? layer.HeightMm : null);
    }

    private static Dictionary<NetSegment, (double Width, double? Height)> SizesBeforeMerge(CadDrawing drawing, BuildPlan plan,
        RevitCatalog catalog, IReadOnlyList<NetSegment> segments, double k, P3 translation, double tolerance)
    {
        var targets = segments.Select((s, i) => new SizeTarget
        {
            Id = i.ToString(CultureInfo.InvariantCulture), Layer = s.Layer, Start = s.Start, End = s.End,
            Shape = catalog.UsesWidthHeight(plan.Layers[s.Layer].Kind, plan.Layers[s.Layer].TypeId) ? SizeShape.AnchoAlto : SizeShape.Redonda,
            EitherShape = plan.Layers[s.Layer].Kind == MepKind.Conducto
        }).ToList();
        return SizeAnnotations.Associate(drawing, targets, new SizeAnnotations.Settings
            { UnitToMm = k, OffsetMm = translation, ToleranceMm = tolerance, PropagateAlongRuns = false }).Decisions.ToDictionary(
            pair => segments[int.Parse(pair.Key, CultureInfo.InvariantCulture)], pair =>
            {
                var s = segments[int.Parse(pair.Key, CultureInfo.InvariantCulture)];
                var d = pair.Value;
                double width = d.ValueMm;
                if (plan.Layers[s.Layer].Kind == MepKind.Tuberia && s.WidthMm is double outer &&
                    !d.From.Any(f => f.Source is SizeSource.Cota or SizeSource.TextoDeCota) &&
                    Math.Abs(width - outer) > PlanDefaults.PipeNominalOutlineFraction * outer)
                    width = outer;
                return (width, d.HeightMm);
            });
    }

    private static Dictionary<Junction, string> ReadDuctChanges(CadDrawing drawing, BuildPlan plan, RevitCatalog catalog,
        double k, P3 translation, double tolerance)
    {
        var joints = plan.Network.Junctions.Where(j => NetworkBuilder.IsAlignedPair(j, plan.Network)).ToList();
        var unsupported = new Dictionary<Junction, string>();
        if (joints.Count == 0) return unsupported;
        var targets = plan.Network.Segments.Select(s => new SizeTarget
        {
            Id = s.Index.ToString(CultureInfo.InvariantCulture), Layer = s.Layer, Start = s.Start, End = s.End,
            Shape = catalog.UsesWidthHeight(MepKind.Conducto, plan.Layers[s.Layer].TypeId) ? SizeShape.AnchoAlto : SizeShape.Redonda,
            EitherShape = true
        }).ToList();
        var decisions = SizeAnnotations.Associate(drawing, targets, new SizeAnnotations.Settings
            { UnitToMm = k, OffsetMm = translation, ToleranceMm = tolerance, PropagateAlongRuns = false }).Decisions;
        foreach (var joint in joints)
        {
            int a = joint.Ends[0].Segment, b = joint.Ends[1].Segment;
            var first = decisions.GetValueOrDefault(targets[a].Id);
            var second = decisions.GetValueOrDefault(targets[b].Id);
            if (first?.Source != SizeSource.Rotulo || second?.Source != SizeSource.Rotulo ||
                NetworkBuilder.SameSize((first.ValueMm, first.HeightMm), (second.ValueMm, second.HeightMm))) continue;
            foreach (var (index, size) in new[] { (a, first), (b, second) })
            {
                var layer = plan.Layers[joint.Layer];
                bool rectangular = size.HeightMm.HasValue;
                if (rectangular != catalog.UsesWidthHeight(MepKind.Conducto, layer.TypeId))
                {
                    var types = catalog.DuctTypesOf(rectangular ? ConnectorProfileType.Rectangular : ConnectorProfileType.Round);
                    var type = types.FirstOrDefault(t => catalog.FittingWarning(t.Name, t.Id) == null) ?? types.FirstOrDefault();
                    if (type == null)
                    {
                        unsupported[joint] = "el proyecto carece del tipo de conducto necesario para el cambio de forma indicado por los rótulos";
                        continue;
                    }
                    plan.SegmentDuctTypes[index] = type.Id;
                }
                plan.DuctLabelSizes.Add(index);
            }
        }
        return unsupported;
    }

    private static void CopyPipeChoice(BuildPlan plan, int source, int target)
    {
        plan.SegmentSystems[target] = plan.SegmentSystems[source];
        plan.SegmentPipeTypes[target] = plan.SegmentPipeTypes[source];
    }

    private static void AssignPipeNetwork(BuildPlan plan, RevitCatalog catalog, PipeNetworkDecision network, IReadOnlyList<int> segments)
    {
        var systems = catalog.Systems[MepKind.Tuberia];
        NamedId? Classified(params MEPSystemClassification[] classes) => classes.Select(cls => systems
            .Where(s => catalog.PipeClassifications.TryGetValue(s.Id, out var c) && c == cls &&
                PipeNetworks.Recognize(s.Name) == PipeService.Unknown)
            .OrderBy(s => s.Id.Value).FirstOrDefault()).FirstOrDefault(s => s != null);
        NamedId? system = null;
        PipeService selected = network.Service;
        foreach (var service in PipeNetworks.SystemPreferences(network.Service))
        {
            system = systems.FirstOrDefault(s => PipeNetworks.Recognize(s.Name) == service) ?? (service switch
            {
                PipeService.Sanitary => Classified(MEPSystemClassification.Sanitary),
                PipeService.Storm => Classified(MEPSystemClassification.Storm),
                PipeService.Cold => Classified(MEPSystemClassification.DomesticColdWater),
                PipeService.Hot => Classified(MEPSystemClassification.DomesticHotWater),
                PipeService.Recirculation => Classified(MEPSystemClassification.Recirculation),
                PipeService.Fire => Classified(MEPSystemClassification.FireProtectWet, MEPSystemClassification.FireProtectOther,
                    MEPSystemClassification.FireProtectDry, MEPSystemClassification.FireProtectPreaction),
                PipeService.Vent => Classified(MEPSystemClassification.Vent),
                PipeService.Heating => Classified(MEPSystemClassification.SupplyHydronic),
                PipeService.Return => Classified(MEPSystemClassification.ReturnHydronic),
                _ => null
            });
            if (system is not null) { selected = service; break; }
        }
        bool assumed = network.Service == PipeService.Unknown || selected != network.Service || system is null;
        system ??= Classified(MEPSystemClassification.OtherPipe);
        if (network.Service == PipeService.Unknown)
            system ??= systems.OrderBy(s => s.Id.Value).FirstOrDefault();

        var types = catalog.Types[MepKind.Tuberia];
        var complete = types.Where(t => catalog.FittingWarning(t.Name, t.Id) is null).ToList();
        var candidates = complete.Count > 0 ? complete : types;
        bool drainage = PipeNetworks.IsDrainage(network.Service) || PipeNetworks.IsDrainage(selected);
        string? preferredType = drainage ? PipeNetworks.DrainageType(candidates.Select(t => t.Name), network.Material) : null;
        var type = candidates.FirstOrDefault(t => t.Name == preferredType) ?? candidates[0];
        foreach (int index in segments)
        {
            plan.SegmentSystems[index] = system?.Id ?? ElementId.InvalidElementId;
            plan.SegmentPipeTypes[index] = type.Id;
        }
        string choice = system is null ? "sin sistema compatible; cargue un tipo de sistema adecuado y repita"
            : $"«{system.Name}»" + (assumed ? "; sistema SUPUESTO, revise la clasificación en Revit" : "");
        string typeReason = preferredType != null ? "material del rótulo o tipo de saneamiento" :
            drainage ? "sin tipo de material adecuado; cargue un tipo de saneamiento" : "tipo habitual";
        if (complete.Count == 0) typeReason += "; sin preferencias de enrutamiento completas";
        var first = plan.Network.Segments.First(s => s.Index == segments[0]);
        plan.PipeNetworkNotes.Add($"Red {network.Id}{(network.Segments.Count == 0 ? " (vertical suelta)" : "")}, capa '{first.Layer}', junto a {first.Start} mm: {choice} por {network.Reason}; " +
            $"tipo «{type.Name}» ({typeReason}).");
    }

    private static bool Eq(string a, string b) => string.Equals(a, b, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Medidas escritas en el plano (rótulos «Ø25», «FON. AFS Ø32», «300x150» con o sin directriz, y cotas), asociadas
    /// a cada tramo al modelar. Para tuberías a doble línea, la prioridad entre contorno, cota y rótulo
    /// se resuelve en MepBuilder.ApplySize.
    /// </summary>
    private static void AssignSizesFromLabels(CadDrawing drawing, BuildPlan plan, RevitCatalog catalog, double k, P3 translation, double tol, AutoPlanResult res)
    {
        try
        {
            var targets = new List<SizeTarget>();
            foreach (var s in plan.Network.Segments)
            {
                if (!plan.Layers.TryGetValue(s.Layer, out var lp) || lp.Kind is MepKind.Muro or MepKind.NoImportar) continue;
                var shape = catalog.UsesWidthHeight(lp.Kind, plan.SegmentDuctTypes.GetValueOrDefault(s.Index) ?? lp.TypeId)
                    ? SizeShape.AnchoAlto : SizeShape.Redonda;
                targets.Add(new SizeTarget { Id = s.Index.ToString(CultureInfo.InvariantCulture), Layer = s.Layer, Start = s.Start, End = s.End, Shape = shape });
            }
            if (targets.Count == 0) return;
            var a = SizeAnnotations.Associate(drawing, targets, new SizeAnnotations.Settings { UnitToMm = k, OffsetMm = translation, ToleranceMm = tol });
            foreach (var (id, d) in a.Decisions)
                plan.SegmentSizes[int.Parse(id, CultureInfo.InvariantCulture)] = (d.ValueMm, d.HeightMm,
                    plan.UsesPipeNetwork && d.From.Any(f => f.Source is SizeSource.Cota or SizeSource.TextoDeCota) ? "cota DIMENSION (propagada si procede)" :
                    d.Source == SizeSource.Recorrido ? "rótulo del mismo recorrido" : d.Source == SizeSource.Rotulo ? "rótulo" : "cota");
            if (!plan.UsesPipeNetwork) AssignSystemsFromLabels(plan, catalog, a, res);
            int labels = a.Annotations.Count(x => x.Source == SizeSource.Rotulo);
            if (labels > 0 || a.Decisions.Count > 0)
                res.DefaultDecisions.Add($"Medidas del plano: {a.Annotations.Count(x => x.Applied)} de {a.Annotations.Count} rótulos y cotas asociados a un tramo; " +
                                         $"{a.Decisions.Count} tramos toman su medida de ellos. Las tuberías dibujadas con sus dos bordes toman el diámetro dibujado " +
                                         "(el nominal cuyo diámetro exterior se le parece), salvo que su rótulo coincida con él.");
        }
        catch (Exception ex) { res.Notices.Add("No se pudieron leer los rótulos de tamaño del plano: " + ex.Message); }
    }

    /// <summary>
    /// Tuberías de varias instalaciones en una misma capa (planos de Revit: todo en «P-PIPE»): el rótulo dice de cuál es
    /// cada red («FON. AFS Ø32» → agua fría, «ACS» → caliente, «RACS» → retorno). Cada red conectada toma el tipo de
    /// sistema que indican la mayoría de sus rótulos. Las redes sin rótulo se quedan con el de su capa.
    /// </summary>
    private static void AssignSystemsFromLabels(BuildPlan plan, RevitCatalog catalog, SizeAssociation a, AutoPlanResult res)
    {
        var systems = catalog.Systems.GetValueOrDefault(MepKind.Tuberia) ?? new List<NamedId>();
        if (systems.Count == 0) return;
        var names = new List<string> { "\u0001" }.Concat(systems.Select(s => s.Name)).ToList();
        var prefixBySeg = new Dictionary<int, string>();
        foreach (var (id, d) in a.Decisions)
            if (d.From.FirstOrDefault(f => f.Prefix != null)?.Prefix is string p)
                prefixBySeg[int.Parse(id, CultureInfo.InvariantCulture)] = p.ToUpperInvariant();
        if (prefixBySeg.Count == 0) return;
        var pipeSegs = plan.Network.Segments.Where(s => plan.Layers.TryGetValue(s.Layer, out var lp) && lp.Kind == MepKind.Tuberia).Select(s => s.Index).ToList();
        int nets = 0;
        foreach (var comp in NetworkComponents.Find(pipeSegs, plan.Network.Junctions))
        {
            var votes = comp.Where(prefixBySeg.ContainsKey).GroupBy(i => prefixBySeg[i]).OrderByDescending(g => g.Count()).ToList();
            if (votes.Count == 0) continue;
            string? sysName = ImportForm.SuggestSystem(votes[0].Key, names);
            if (sysName is null || sysName == "\u0001") continue;
            var sysId = systems.First(s => s.Name == sysName).Id;
            foreach (int i in comp) plan.SegmentSystems[i] = sysId;
            nets++;
        }
        if (nets > 0)
            res.DefaultDecisions.Add($"Tipo de sistema por red: {nets} redes de tubería toman el sistema que dicen sus rótulos (AFS → agua fría, ACS → caliente, RACS → retorno…).");
    }

    /// <summary>¿Los conductos de esta capa son redondos? Por las piezas dibujadas como bloque o por los rótulos de su capa.</summary>
    private static bool LooksRoundDuct(CadDrawing drawing, string layer)
    {
        int round = 0, rect = 0;
        foreach (var b in drawing.Blocks.Where(b => Eq(b.Layer, layer)))
        {
            string n = b.BlockName.ToUpperInvariant();
            if (n.Contains("REDOND") || n.Contains("ROUND")) round++;
            else if (n.Contains("RECTANG")) rect++;
        }
        if (round + rect > 0) return round > rect;
        string root = System.Text.RegularExpressions.Regex.Replace(layer, @"[-_ ](IDEN|ANNO|TEXT|TEXTO|TEXTOS)$", "", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        foreach (var t in drawing.Texts.Where(t => t.Layer.StartsWith(root, StringComparison.OrdinalIgnoreCase)))
        {
            var l = SizeAnnotations.ParseLabel(t.Value);
            if (l is not { Readable: true }) continue;
            if (l.Shape == SizeShape.Redonda) round++; else rect++;
        }
        return round > rect;
    }

    // Mismas medidas propuestas que el asistente (ImportForm.DefaultDiameterMm / DefaultWidthMm / DefaultHeightMm).
    private static double DefaultDiameterMm(MepKind kind) =>
        kind is MepKind.Tuberia or MepKind.TuboElectrico ? PlanDefaults.PipeDiameterMm : MepKinds.DefaultSizeMm(kind);

    private static double DefaultWidthMm(MepKind kind) =>
        kind == MepKind.Conducto ? PlanDefaults.DuctWidthMm : MepKinds.DefaultSizeMm(kind);

    private static double DefaultHeightMm(MepKind kind) =>
        kind == MepKind.Conducto ? PlanDefaults.DuctHeightMm : MepKinds.DefaultHeightMm(kind);

    /// <summary>Distancia (mm) del nivel al siguiente nivel superior, si está entre 500 y 20 000 mm (como el asistente).</summary>
    private static double? NextLevelHeightMm(RevitCatalog catalog, (string Name, ElementId Id, double ElevationFt) level)
    {
        var next = catalog.Levels.Where(l => l.ElevationFt > level.ElevationFt + 1e-6).OrderBy(l => l.ElevationFt).FirstOrDefault();
        if (next.Id is null) return null;
        double mm = (next.ElevationFt - level.ElevationFt) * 304.8;
        return mm is > 500 and < 20000 ? mm : null;
    }
}
