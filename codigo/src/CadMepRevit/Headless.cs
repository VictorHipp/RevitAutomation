using System.Globalization;
using Autodesk.Revit.DB;
using CadMepRevit.Commands;
using CadMepRevit.Core;
using CadMepRevit.Revit;
using CadMepRevit.Ui;

namespace CadMepRevit;

/// <summary>
/// Construcción SIN ventanas: lee un plano y construye con lo mismo que el asistente propondría por defecto
/// (qué es cada capa, tipo, sistema, tamaños, doble línea, bloques), sin que nadie lo revise.
/// Sirve para PROBAR el complemento dentro de Revit de forma automática (desde una macro o una herramienta
/// externa) y obtener el informe como texto. No sustituye al asistente: el usuario final usa los botones.
/// </summary>
public static class Headless
{
    /// <summary>
    /// Construye el plano indicado en el proyecto abierto y devuelve el informe completo.
    /// Nunca lanza excepciones: si algo falla, el texto devuelto empieza por "ERROR".
    /// </summary>
    /// <param name="doc">Proyecto de Revit (no una familia).</param>
    /// <param name="cadPath">Ruta del archivo DWG o DXF.</param>
    /// <param name="options">
    /// Opciones "clave=valor" separadas por punto y coma; todas son opcionales:
    /// unidades=mm|cm|m|in|ft (por defecto, la del archivo); nivel=nombre (por defecto, el más bajo);
    /// altura=mm sobre el nivel (2500); tolerancia=mm (10); minimo=mm (50);
    /// capas=A|B|C (solo esas capas; por defecto, todas las que el programa propone construir);
    /// uniones=si|no; bloques=si|no; conectar=si|no; dentrodebloques=si|no;
    /// tipo.Tuberia / tipo.Conducto / tipo.Bandeja / tipo.TuboElectrico / tipo.Muro = nombre del tipo de Revit.
    /// </param>
    public static string BuildFromCad(Document doc, string cadPath, string? options)
    {
        try
        {
            if (doc is null || doc.IsFamilyDocument) return "ERROR: hace falta un proyecto de Revit abierto (no una familia).";
            var o = Parse(options);
            bool Flag(string key, bool byDefault) => o.TryGetValue(key, out var v) ? v is "si" or "sí" or "true" or "1" : byDefault;
            double Number(string key, double byDefault) =>
                o.TryGetValue(key, out var v) && double.TryParse(v.Replace(',', '.'), NumberStyles.Float, CultureInfo.InvariantCulture, out double d) ? d : byDefault;

            var catalog = RevitCatalog.Load(doc);
            if (catalog.Levels.Count == 0) return "ERROR: el proyecto no tiene niveles.";
            var drawing = CadReader.Read(cadPath, new CadReader.Options { ReadLinesInsideBlocks = Flag("dentrodebloques", false) });

            DrawingUnit unit = drawing.HeaderUnit ?? DrawingUnit.Milimetros;
            if (o.TryGetValue("unidades", out var u))
                unit = u switch
                {
                    "mm" => DrawingUnit.Milimetros, "cm" => DrawingUnit.Centimetros, "m" => DrawingUnit.Metros,
                    "in" => DrawingUnit.Pulgadas, "ft" => DrawingUnit.Pies,
                    _ => throw new ArgumentException("unidades debe ser mm, cm, m, in o ft")
                };
            double k = DrawingUnits.ToMillimeters(unit);
            double tolerance = Number("tolerancia", 10), minLength = Number("minimo", 50), height = Number("altura", 2500);

            var level = catalog.Levels[0];
            if (o.TryGetValue("nivel", out var levelName))
            {
                int i = catalog.Levels.FindIndex(l => string.Equals(l.Name, levelName, StringComparison.OrdinalIgnoreCase));
                if (i < 0) return $"ERROR: no hay ningún nivel llamado '{levelName}'. Niveles: {string.Join(", ", catalog.Levels.Select(l => l.Name))}";
                level = catalog.Levels[i];
            }
            var above = catalog.Levels.Where(l => l.ElevationFt > level.ElevationFt + 1e-6).OrderBy(l => l.ElevationFt).ToList();
            double? storeyMm = above.Count > 0 ? (above[0].ElevationFt - level.ElevationFt) * 304.8 : null;

            // Igual que el asistente: si el dibujo está a más de 10 km del origen, se acerca
            P3 translation = default;
            if (drawing.Segments.Count > 0)
            {
                double far = drawing.Segments.Max(s => Math.Max(Math.Max(Math.Abs(s.Start.X), Math.Abs(s.Start.Y)), Math.Max(Math.Abs(s.End.X), Math.Abs(s.End.Y)))) * k;
                if (far > 1.0e7)
                    translation = new P3(-Math.Floor(drawing.Segments.Min(s => Math.Min(s.Start.X, s.End.X)) * k / 1000) * 1000,
                                         -Math.Floor(drawing.Segments.Min(s => Math.Min(s.Start.Y, s.End.Y)) * k / 1000) * 1000, 0);
            }

            var plan = new BuildPlan
            {
                LevelId = level.Id, LevelName = level.Name, LevelElevationFt = level.ElevationFt, HeightAboveLevelMm = height,
                CreateFittings = Flag("uniones", true), AutoConnect = Flag("conectar", true), TranslationMm = translation,
                SourcePath = drawing.Path, UnitsLabel = DrawingUnits.Label(unit) + " (construcción sin ventanas)",
                ToleranceMm = tolerance, MinLengthMm = minLength
            };

            OutlineDetector.Settings Outline(MepKind kind) => kind == MepKind.Muro
                ? OutlineDetector.Settings.ForWalls(k, tolerance)
                : new() { UnitToMm = k, ToleranceMm = tolerance };

            // ---- Capas: lo que el asistente propone al abrirse
            var only = o.TryGetValue("capas", out var list)
                ? new HashSet<string>(list.Split('|', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries), StringComparer.OrdinalIgnoreCase)
                : null;
            var notes = new List<string>();
            foreach (var layer in drawing.SegmentLayers)
            {
                if (only != null && !only.Contains(layer)) continue;
                var kind = MepKinds.Suggest(layer);
                if (kind == MepKind.NoImportar) { if (only != null) notes.Add($"Capa '{layer}': el programa no propone construirla (se omite)."); continue; }
                var types = catalog.Types[kind];
                if (types.Count == 0) { notes.Add($"Capa '{layer}' ({kind}): {RevitCatalog.HowToGetTypes(kind)}"); continue; }
                var segs = drawing.Segments.Where(s => string.Equals(s.Layer, layer, StringComparison.OrdinalIgnoreCase)).ToList();

                ElementId? typeId;
                if (o.TryGetValue("tipo." + kind.ToString().ToLowerInvariant(), out var typeName))
                {
                    typeId = types.FirstOrDefault(t => string.Equals(t.Name, typeName, StringComparison.OrdinalIgnoreCase))?.Id;
                    if (typeId is null) { notes.Add($"Capa '{layer}': no existe el tipo '{typeName}'. Tipos: {string.Join(", ", types.Select(t => t.Name))}"); continue; }
                }
                else if (kind == MepKind.Muro) typeId = ElementId.InvalidElementId;     // automático por espesor
                else typeId = (types.FirstOrDefault(t => catalog.FittingWarning(t.Name, t.Id) is null) ?? types[0]).Id;

                ElementId? systemId = null;
                if (RevitCatalog.NeedsSystem(kind))
                {
                    var systems = catalog.Systems[kind];
                    if (systems.Count == 0) { notes.Add($"Capa '{layer}' ({kind}): {RevitCatalog.HowToGetTypes(kind)}"); continue; }
                    string? name = ImportForm.SuggestSystem(layer, systems.Select(s => s.Name).ToList());
                    systemId = systems.FirstOrDefault(s => s.Name == name)?.Id ?? systems[0].Id;
                }

                bool doubleLine = kind == MepKind.Muro;
                if (kind is MepKind.Conducto or MepKind.Bandeja && segs.Count is >= 4 and <= 20000)
                {
                    try { var r = OutlineDetector.Detect(segs, Outline(kind)); doubleLine = r.Centerlines.Count > 0 && r.PairedFraction >= 0.6; }
                    catch { doubleLine = false; }
                }
                bool widthHeight = catalog.UsesWidthHeight(kind, typeId);
                plan.Layers[layer] = new LayerPlan
                {
                    Layer = layer, Kind = kind, SystemTypeId = systemId, TypeId = typeId, DoubleLine = doubleLine,
                    SizeMm = doubleLine ? 0 : MepKinds.DefaultSizeMm(kind),
                    HeightMm = !widthHeight ? 0 : kind == MepKind.Muro && storeyMm is > 500 and < 20000 ? storeyMm.Value : MepKinds.DefaultHeightMm(kind)
                };
                if (widthHeight && kind != MepKind.Muro)
                    plan.Warnings.Add($"Capa '{layer}': alto {plan.Layers[layer].HeightMm:0} mm SUPUESTO (el plano en planta no lo indica).");
            }

            // ---- Bloques: familia con el mismo nombre, o marcador 3D con la huella medida
            if (Flag("bloques", true))
                foreach (var (layer, block) in drawing.BlockGroups)
                {
                    var role = BlockNames.Role(block, layer);
                    if (role == BlockRole.PiezaDeUnion) continue;                       // la crea Revit con las uniones
                    var foot = drawing.Blocks.Where(b => b.Layer == layer && b.BlockName == block).Select(b => b.Footprint).FirstOrDefault(f => f != null);
                    string? match = catalog.MatchBlock(block);
                    var symbol = match is null ? null : catalog.FamilySymbols.FirstOrDefault(s => s.Name == match)?.Id;
                    if (symbol is null && (foot is null || role == BlockRole.Otro)) continue;
                    string category = ImportForm.CategoryFor(role);
                    var cat = ImportForm.MarkerCategories.FirstOrDefault(c => c.Label == category);
                    plan.BlockPlans.Add(new BlockPlan
                    {
                        Layer = layer, BlockName = block,
                        Action = symbol != null ? BlockAction.Familia : BlockAction.Marcador,
                        SymbolId = symbol ?? ElementId.InvalidElementId,
                        MarkerCategory = cat.Label is null ? BuiltInCategory.OST_GenericModel : cat.Cat,
                        HeightMm = ImportForm.DefaultMarkerHeight(block, role).Mm,
                        ElevationMm = height
                    });
                }

            if (plan.Layers.Count == 0 && plan.BlockPlans.Count == 0)
                return "ERROR: no hay nada que construir con lo propuesto por defecto.\n" + string.Join("\n", notes) +
                       "\nCapas del plano: " + string.Join(", ", drawing.SegmentLayers);

            // ---- Red de tramos (igual que el asistente al pulsar «Previsualizar»)
            var input = new List<CadSegment>();
            foreach (var lp in plan.Layers.Values)
            {
                var layerSegs = drawing.Segments.Where(s => string.Equals(s.Layer, lp.Layer, StringComparison.OrdinalIgnoreCase)).ToList();
                if (!lp.DoubleLine) { input.AddRange(layerSegs); continue; }
                var outline = OutlineDetector.Detect(layerSegs, Outline(lp.Kind));
                plan.Outlines[lp.Layer] = outline;
                input.AddRange(outline.Centerlines);
            }
            plan.Network = NetworkBuilder.Build(input, new NetworkBuilder.Settings
            {
                UnitToMm = k, ToleranceMm = tolerance, MinLengthMm = minLength, FlattenZ = true, OffsetMm = translation,
                TransitionHints = LayerPlanning.TransitionHints(drawing),
                ThicknessLayers = plan.Layers.Values.Where(lp => lp.Kind == MepKind.Muro).Select(lp => lp.Layer).ToList()
            });
            plan.Network.Junctions.RemoveAll(j => plan.Layers.TryGetValue(j.Layer, out var jl) && jl.Kind == MepKind.Muro);
            foreach (var b in drawing.Blocks.Where(b => plan.BlockPlans.Any(p => BuildPlan.Same(p, b))))
            {
                var pos = new P3(b.Position.X * k, b.Position.Y * k, 0) + translation;
                var f = b.Footprint;
                plan.Blocks.Add((b, pos, f is null ? null
                    : new Footprint(f.CenterX * k + translation.X, f.CenterY * k + translation.Y, f.AngleRad, f.SizeX * k, f.SizeY * k)));
            }
            foreach (var lp in plan.Layers.Values)
            {
                var type = catalog.Types[lp.Kind].FirstOrDefault(t => t.Id == lp.TypeId);
                var warn = type is null ? null : catalog.FittingWarning(type.Name, lp.TypeId);
                if (warn != null && plan.CreateFittings) plan.Warnings.Add($"Capa '{lp.Layer}': {warn}");
            }
            foreach (var n in notes) plan.Warnings.Add(n);
            foreach (var w in drawing.Warnings) plan.Warnings.Add("Lectura del CAD: " + w);

            // ---- Construir. Si quien llama ya ha abierto una transacción, se trabaja dentro de ella.
            BuildReport report;
            string? failure = null;
            try
            {
                report = doc.IsModifiable ? MepBuilder.BuildInOpenTransaction(doc, plan) : MepBuilder.Build(doc, plan);
                if (report.Status == TransactionStatus.Started)
                    failure = "Construido dentro de una transacción abierta por quien llama: los cambios quedan en el modelo solo si esa transacción se confirma.";
                else if (report.Status != TransactionStatus.Committed)
                    failure = "REVIT CANCELÓ LA OPERACIÓN (no se guardó ningún cambio). Estado: " + report.Status;
            }
            catch (Exception ex)
            {
                string file = ImportCadCommand.SaveReport(doc, plan, null, "LA CONSTRUCCIÓN SE DETUVO (no se guardó ningún cambio):\n" + ex);
                return "ERROR: la construcción se detuvo.\n" + (file.Length > 0 && File.Exists(file) ? File.ReadAllText(file) : ex.ToString());
            }
            string saved = ImportCadCommand.SaveReport(doc, plan, report, failure);
            return saved.Length > 0 && File.Exists(saved) ? File.ReadAllText(saved) : report.Summary() + "\n" + string.Join("\n", report.Problems);
        }
        catch (CadReadException ex) { return "ERROR al leer el CAD: " + ex.Message + " " + ex.HowToFix; }
        catch (Exception ex) { return "ERROR: " + ex; }
    }

    private static Dictionary<string, string> Parse(string? options)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var part in (options ?? "").Split(';', StringSplitOptions.RemoveEmptyEntries))
        {
            int eq = part.IndexOf('=');
            if (eq <= 0) continue;
            string key = part[..eq].Trim();
            string value = part[(eq + 1)..].Trim();
            // Los nombres (capas, nivel, tipo) se guardan tal cual; el resto, en minúsculas
            bool isName = key.Equals("capas", StringComparison.OrdinalIgnoreCase) || key.Equals("nivel", StringComparison.OrdinalIgnoreCase) ||
                          key.StartsWith("tipo.", StringComparison.OrdinalIgnoreCase);
            result[key.ToLowerInvariant()] = isName ? value : value.ToLowerInvariant();
        }
        return result;
    }
}
