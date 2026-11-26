namespace CadMepRevit.Core;

/// <summary>
/// Configuración que el programa aplica sin preguntar. Son EXACTAMENTE los valores que el asistente de
/// importación (ImportForm) propone al abrirse; están aquí para que el modo automático («Modelar desde CAD»)
/// y el asistente lean los mismos. No cambie estos valores sin autorización: cambian el resultado del modelado.
/// </summary>
public static class PlanDefaults
{
    /// <summary>Altura del eje de tuberías y conductos (y de los bloques) sobre el nivel, en mm.</summary>
    public const double HeightAboveLevelMm = 2500;
    /// <summary>Eje más alto de red enterrada sin cota escrita. Ambos valores son SUPUESTOS.</summary>
    public const double BuriedTopAxisMm = -600;
    public const double AssumedStoreyMm = 3000;
    public const double CircleCenterAllowanceMm = 5;
    public const double RiserDirectionLabelMm = 1500;
    public const double PipeSystemLabelMm = 1000;
    public const double AssumedChamberDepthMm = 600;
    public const double AssumedManholeDepthMm = 1500;
    /// <summary>Diámetro propuesto para tuberías y tubos eléctricos (mm).</summary>
    public const double PipeDiameterMm = 25;
    /// <summary>Ancho propuesto para conductos rectangulares y ovales (mm).</summary>
    public const double DuctWidthMm = 300;
    /// <summary>Alto propuesto para conductos rectangulares y ovales (mm). Es un valor SUPUESTO.</summary>
    public const double DuctHeightMm = 200;
    /// <summary>Distancia máxima para considerar que dos extremos se tocan (mm).</summary>
    public const double ToleranceMm = 10;
    /// <summary>Los tramos más cortos se descartan (mm).</summary>
    public const double MinLengthMm = 50;
    public const double PipeGapDiameterFactor = 3;
    public const double PipeGapMinMm = 50;
    public const double PipeGapMaxMm = 250;
    public const double WideGapWidthFactor = 1.5;
    public const double WideGapMaxMm = 1000;
    public const double GapSearchMm = 2 * WideGapMaxMm;
    public const double TeeGapMinAngleDeg = 30;
    public const double BlockNetworkWidthFactor = 2;
    public const double BlockInlineWidthFactor = 0.25;
    public const double BlockFootprintToleranceFactor = 0.1;
    public const double PipeNominalOutlineFraction = 0.25;
    public const double FittingSizeToleranceMm = 0.5;
    public const double PipeSlopeToleranceRatio = 1e-6;
    /// <summary>Si el dibujo está más lejos del origen (mm), se acerca a él.</summary>
    public const double TranslateBeyondMm = 1.0e7;
    public const bool CreateFittings = true;
    public const bool AutoConnect = true;
    public const bool UseCadZ = false;
    public const bool ReadLinesInsideBlocks = false;
    /// <summary>Se propone «doble línea» en conductos y bandejas con este número de líneas…</summary>
    public const int DoubleLineMinLines = 4;
    public const int DoubleLineMaxLines = 20000;
    /// <summary>…y al menos esta fracción emparejada.</summary>
    public const double DoubleLineMinPairedFraction = 0.6;
    /// <summary>Tuberías a doble línea: al menos estas testas (líneas cortas que cierran el rectángulo) por eje detectado.</summary>
    public const double PipeOutlineMinCapFraction = 0.25;
    /// <summary>Tuberías a doble línea sin testas: casi todas las líneas emparejadas.</summary>
    public const double PipeOutlineMinPairedFraction = 0.95;
    /// <summary>Tubos eléctricos a doble línea (E-POWR-CNDT de Revit, con los codos como bloques): emparejado y testas mínimos.</summary>
    public const double ConduitOutlineMinPairedFraction = 0.8;
    public const double ConduitOutlineMinCapFraction = 0.1;
    /// <summary>Conductos y bandejas a doble línea en una capa con más dibujo: emparejado mínimo y tapas por eje.</summary>
    public const double DuctOutlineMinPairedFraction = 0.4;
    public const double DuctOutlineMinCapFraction = 0.5;
    /// <summary>
    /// Un bloque de luminaria, mando, PCI o eléctrico con la huella mayor que esto (mm) es un dibujo entero insertado
    /// como bloque (planta de patinillos, esquema), no un aparato: no se coloca.
    /// </summary>
    public const double MaxApplianceFootprintMm = 6000;
}

/// <summary>
/// Decisiones por capa que no dependen de Revit: doble línea, desplazamiento al origen, aviso de unidades y
/// entrada de la red. Las usan el asistente y el modo automático, con los valores de <see cref="PlanDefaults"/>.
/// </summary>
public static class LayerPlanning
{
    public static OutlineDetector.Settings OutlineSettings(MepKind kind, double unitToMm, double toleranceMm) => kind switch
    {
        MepKind.Muro => OutlineDetector.Settings.ForWalls(unitToMm, toleranceMm),
        MepKind.Tuberia or MepKind.TuboElectrico => OutlineDetector.Settings.ForPipes(unitToMm, toleranceMm),
        MepKind.Bandeja => new() { UnitToMm = unitToMm, ToleranceMm = toleranceMm, CloseDrawnArcElbows = true },
        _ => new() { UnitToMm = unitToMm, ToleranceMm = toleranceMm }
    };

    /// <summary>
    /// Propone «doble línea» si la mayor parte de la capa son parejas de líneas paralelas.
    /// Los muros siempre son a doble línea; tuberías y tubos eléctricos nunca (se dibujan con su eje).
    /// </summary>
    public static bool LooksDoubleLine(IReadOnlyCollection<CadSegment> layerSegments, MepKind kind, double unitToMm, double toleranceMm)
    {
        if (kind == MepKind.Muro) return true;
        if (kind is MepKind.Tuberia or MepKind.TuboElectrico) return LooksPipeOutline(layerSegments, kind, unitToMm, toleranceMm);
        if (kind is not (MepKind.Conducto or MepKind.Bandeja)) return false;
        if (layerSegments.Count < PlanDefaults.DoubleLineMinLines || layerSegments.Count > PlanDefaults.DoubleLineMaxLines) return false;
        try
        {
            var r = OutlineDetector.Detect(layerSegments, OutlineSettings(kind, unitToMm, toleranceMm));
            if (r.Centerlines.Count == 0) return false;
            if (r.PairedFraction >= PlanDefaults.DoubleLineMinPairedFraction) return true;
            // Capa de conductos con mucho más dibujo dentro (rejillas, flexibles, compuertas): baja la fracción emparejada,
            // pero los conductos siguen cerrados por sus tapas. Con al menos una tapa por cada dos ejes es doble línea.
            return r.PairedFraction >= PlanDefaults.DuctOutlineMinPairedFraction &&
                   r.Caps >= PlanDefaults.DuctOutlineMinCapFraction * r.Centerlines.Count;
        }
        catch { return false; }
    }

    /// <summary>
    /// Tuberías dibujadas con sus dos bordes (como las exporta Revit: cada tramo es un rectángulo cerrado con sus
    /// testas). Se exige que casi todo esté emparejado Y que haya testas, para no confundir dos tuberías de
    /// línea simple que van juntas en paralelo con los bordes de un solo tubo.
    /// </summary>
    public static bool LooksPipeOutline(IReadOnlyCollection<CadSegment> layerSegments, MepKind kind, double unitToMm, double toleranceMm)
    {
        if (layerSegments.Count < PlanDefaults.DoubleLineMinLines || layerSegments.Count > PlanDefaults.DoubleLineMaxLines) return false;
        try
        {
            var r = OutlineDetector.Detect(layerSegments, OutlineSettings(kind, unitToMm, toleranceMm));
            if (r.Centerlines.Count == 0) return false;
            // Casi todo emparejado (saneamiento dibujado a escala), o emparejado con testas (exportación de Revit).
            // Un suelo radiante (líneas paralelas sin testas, 85 % emparejado) no es doble línea.
            if (r.PairedFraction >= PlanDefaults.PipeOutlineMinPairedFraction ||
                (r.PairedFraction >= PlanDefaults.DoubleLineMinPairedFraction && r.Caps >= PlanDefaults.PipeOutlineMinCapFraction * r.Centerlines.Count))
                return true;
            // Tubos eléctricos exportados de Revit (E-POWR-CNDT): los codos van como bloques y quedan menos testas; dos tubos
            // de línea simple pegados en paralelo no se dibujan, así que basta con casi todo emparejado y algunas testas.
            return kind == MepKind.TuboElectrico && r.PairedFraction >= PlanDefaults.ConduitOutlineMinPairedFraction &&
                   r.Caps >= PlanDefaults.ConduitOutlineMinCapFraction * r.Centerlines.Count;
        }
        catch { return false; }
    }

    /// <summary>
    /// Capa de ejes que repite otra capa dibujada con contorno (planos de Revit: «M-HVAC-DUCT-CNTR» junto a
    /// «M-HVAC-DUCT»). Devuelve la capa del contorno, o null. Modelar las dos crearía cada tramo dos veces.
    /// </summary>
    public static string? CenterlineDuplicateOf(string layer, IEnumerable<string> allLayers)
    {
        var m = System.Text.RegularExpressions.Regex.Match(layer, @"^(?<base>.+?)[-_ ](CNTR|CENTER|CENTRO|EJE|EJES)$",
            System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        if (!m.Success) return null;
        string b = m.Groups["base"].Value;
        var list = allLayers.ToList();
        return list.FirstOrDefault(l => string.Equals(l, b, StringComparison.OrdinalIgnoreCase))
               // «E-POWR-CNTR» junto a «E-POWR-CNDT»: misma raíz, el contorno está en la capa hermana (no en la de rótulos).
               ?? list.FirstOrDefault(l => !string.Equals(l, layer, StringComparison.OrdinalIgnoreCase) &&
                                           l.StartsWith(b + "-", StringComparison.OrdinalIgnoreCase) &&
                                           MepKinds.Suggest(l) != MepKind.NoImportar && MepKinds.Suggest(l) == MepKinds.Suggest(layer));
    }

    /// <summary>
    /// Puntos (unidades del dibujo) donde el plano dibuja como bloque una transición o un reductor: ahí el cambio de
    /// ancho es real (ver <see cref="NetworkBuilder.Settings.TransitionHints"/>).
    /// </summary>
    public static List<P3> TransitionHints(CadDrawing drawing) =>
        drawing.Blocks.Where(b =>
            {
                string u = BlockNames.Clean(b.BlockName).ToUpperInvariant();
                return u.Contains("TRANSICI") || u.Contains("TRANSITION") || u.Contains("REDUC");
            })
            .Select(b => b.Footprint is { } f ? new P3(f.CenterX, f.CenterY, 0) : new P3(b.Position.X, b.Position.Y, 0)).ToList();

    /// <summary>Tramos agrupados por capa una sola vez (sin recorrer el plano entero por cada capa). Conserva el orden.</summary>
    public static ILookup<string, CadSegment> ByLayer(CadDrawing drawing) =>
        drawing.Segments.ToLookup(s => s.Layer, StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Desplazamiento al origen: solo si el dibujo está a más de <see cref="PlanDefaults.TranslateBeyondMm"/>;
    /// la esquina inferior izquierda va a (0,0), redondeada a metros completos.
    /// </summary>
    public static P3 Translation(CadDrawing drawing, double unitToMm, bool includePipeCircles = false)
    {
        var points = drawing.Segments.SelectMany(s => new[] { s.Start, s.End });
        if (includePipeCircles)
        {
            var sizes = OutlineDetector.Settings.ForPipes(unitToMm, PlanDefaults.ToleranceMm);
            points = points.Concat(drawing.Circles.Where(c => c.IsPlanar &&
                2 * c.Radius * unitToMm >= sizes.MinWidthMm && 2 * c.Radius * unitToMm <= sizes.MaxWidthMm &&
                PipeNetworks.LayerKind(c.Layer) == MepKind.Tuberia).Select(c => c.Center));
        }
        double far = 0, minX = double.PositiveInfinity, minY = double.PositiveInfinity;
        foreach (var p in points)
        {
            far = Math.Max(far, Math.Max(Math.Abs(p.X), Math.Abs(p.Y)));
            minX = Math.Min(minX, p.X);
            minY = Math.Min(minY, p.Y);
        }
        if (far * unitToMm <= PlanDefaults.TranslateBeyondMm) return default;
        minX *= unitToMm;
        minY *= unitToMm;
        return new P3(-Math.Floor(minX / 1000) * 1000, -Math.Floor(minY / 1000) * 1000, 0);
    }

    /// <summary>
    /// Aviso si, con estas unidades, el dibujo tiene un tamaño absurdo (p. ej. un edificio de 5 metros en
    /// milímetros: suele estar dibujado en metros). Solo avisa; no cambia nada.
    /// </summary>
    /// <summary>
    /// Unidades declaradas absurdas para el tamaño del dibujo (p. ej. la cabecera dice mm y el garaje mediría 0,16 m,
    /// porque está dibujado en metros): se cambian por la unidad con la que el dibujo tiene un tamaño de edificio
    /// (entre 2 m y 5 km), la más cercana a un tamaño típico de planta (50 m). Se aplica al leer, antes de todo lo demás,
    /// y se explica en el informe. Si la unidad declarada ya da un tamaño razonable, no se toca.
    /// </summary>
    public static string? FixImplausibleUnits(CadDrawing drawing)
    {
        var declared = drawing.HeaderUnit ?? DrawingUnit.Milimetros;
        if (UnitsLookWrong(drawing, declared) is null) return null;
        double raw = RawSize(drawing);
        if (raw <= 0) return null;
        var plausible = Enum.GetValues<DrawingUnit>()
            .Select(u => (Unit: u, Mm: raw * DrawingUnits.ToMillimeters(u)))
            .Where(c => c.Mm >= 2000 && c.Mm <= 5_000_000)
            // Primero las métricas (planos de aquí); entre ellas, la que da un tamaño más típico de planta.
            .OrderBy(c => c.Unit is DrawingUnit.Pies or DrawingUnit.Pulgadas ? 1 : 0)
            .ThenBy(c => Math.Abs(Math.Log10(c.Mm / 50_000)))
            .ToList();
        if (plausible.Count == 0) return null;
        var best = plausible[0];
        drawing.HeaderUnit = best.Unit;
        drawing.UnitCorrection = $"Unidades: el archivo dice {DrawingUnits.Label(declared)}, pero así el dibujo mediría " +
            $"{raw * DrawingUnits.ToMillimeters(declared) / 1000:0.###} m; se ha leído en {DrawingUnits.Label(best.Unit)} " +
            $"(el dibujo mide {best.Mm / 1000:0.#} m). Si no es correcto, corrija las unidades del DWG (comando UNIDADES) y repita.";
        return drawing.UnitCorrection;
    }

    private static double RawSize(CadDrawing drawing)
    {
        if (drawing.Segments.Count < 10) return 0;
        var xs = drawing.Segments.SelectMany(s => new[] { s.Start.X, s.End.X }).OrderBy(v => v).ToList();
        var ys = drawing.Segments.SelectMany(s => new[] { s.Start.Y, s.End.Y }).OrderBy(v => v).ToList();
        return Math.Max(xs[(int)(xs.Count * 0.98)] - xs[(int)(xs.Count * 0.02)], ys[(int)(ys.Count * 0.98)] - ys[(int)(ys.Count * 0.02)]);
    }

    public static string? UnitsLookWrong(CadDrawing drawing, DrawingUnit unit)
    {
        if (drawing.Segments.Count < 10) return null;
        var xs = drawing.Segments.SelectMany(s => new[] { s.Start.X, s.End.X }).OrderBy(v => v).ToList();
        var ys = drawing.Segments.SelectMany(s => new[] { s.Start.Y, s.End.Y }).OrderBy(v => v).ToList();
        double k = DrawingUnits.ToMillimeters(unit);
        double size = Math.Max(xs[(int)(xs.Count * 0.98)] - xs[(int)(xs.Count * 0.02)], ys[(int)(ys.Count * 0.98)] - ys[(int)(ys.Count * 0.02)]) * k;
        if (size < 2000) return $"Con {DrawingUnits.Label(unit)} el dibujo mide solo {size / 1000:0.##} m: probablemente está en METROS. Revise 'Unidades del dibujo'.";
        if (size > 5_000_000) return $"Con {DrawingUnits.Label(unit)} el dibujo mide {size / 1_000_000:0.#} km: probablemente está en MILÍMETROS. Revise 'Unidades del dibujo'.";
        return null;
    }

    /// <summary>
    /// Entrada de la red: las líneas de cada capa elegida, o los ejes detectados en las capas a doble línea.
    /// Mismo orden de capas que se recibe y mismo orden de líneas que el plano.
    /// </summary>
    public static List<CadSegment> NetworkInput(ILookup<string, CadSegment> byLayer,
        IEnumerable<(string Layer, MepKind Kind, bool DoubleLine)> layers, double unitToMm, double toleranceMm,
        IDictionary<string, OutlineResult>? outlines)
    {
        var input = new List<CadSegment>();
        foreach (var (layer, kind, doubleLine) in layers)
        {
            var layerSegs = byLayer[layer].ToList();
            if (!doubleLine) { input.AddRange(layerSegs); continue; }
            var outline = OutlineDetector.Detect(layerSegs, OutlineSettings(kind, unitToMm, toleranceMm));
            if (outlines != null) outlines[layer] = outline;
            input.AddRange(outline.Centerlines);
        }
        return input;
    }
}
