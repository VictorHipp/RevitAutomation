using System.Globalization;
using System.Text.RegularExpressions;

namespace CadMepRevit.Core;

/// <summary>Cómo se mide la sección de un tramo.</summary>
public enum SizeShape
{
    /// <summary>Por su diámetro (tuberías, tubos eléctricos, conductos redondos).</summary>
    Redonda,
    /// <summary>Por ancho × alto (conductos rectangulares y ovales, bandejas).</summary>
    AnchoAlto
}

/// <summary>Tramo ya modelado al que se le puede asociar una cota. Coordenadas del modelo en mm (con el desplazamiento aplicado).</summary>
public sealed class SizeTarget
{
    public string Id { get; init; } = "";
    public string Layer { get; init; } = "";
    public P3 Start { get; init; }
    public P3 End { get; init; }
    public SizeShape Shape { get; init; }
    public bool EitherShape { get; init; }
}

/// <summary>De dónde sale una medida.</summary>
public enum SizeSource
{
    /// <summary>Valor medido de una entidad DIMENSION.</summary>
    Cota,
    /// <summary>Texto escrito en una cota DIMENSION sin valor medido (respaldo).</summary>
    TextoDeCota,
    /// <summary>Rótulo de tamaño (TEXT/MTEXT: «Ø16», «200x200», «DN25») junto al tramo (respaldo).</summary>
    Rotulo,
    /// <summary>Medida de otro tramo del mismo recorrido (unido por codos o en línea, sin tes) que sí tiene cota o rótulo.</summary>
    Recorrido
}

/// <summary>Una cota o un rótulo de tamaño del plano, y qué se ha hecho con él.</summary>
public sealed class SizeAnnotation
{
    public int EntityIndex { get; init; }
    public string Layer { get; init; } = "";
    public SizeSource Source { get; set; }
    public string Description { get; init; } = "";
    public P3 PositionMm { get; init; }
    /// <summary>Diámetro o ancho leído (mm).</summary>
    public double? ValueMm { get; set; }
    /// <summary>Alto leído (mm), solo en rótulos «ancho x alto».</summary>
    public double? HeightMm { get; set; }
    public string? TargetId { get; set; }
    /// <summary>True si se usa para fijar el tamaño de su tramo.</summary>
    public bool Applied { get; set; }
    /// <summary>Motivo por el que NO se aplica (vacío si se aplica).</summary>
    public string Reason { get; set; } = "";
    /// <summary>Nombre escrito delante de la medida («AFS» en «FON. AFS Ø32»): dice de qué instalación es el tubo.</summary>
    public string? Prefix { get; set; }
}

/// <summary>Tamaño que el plano indica para un tramo.</summary>
public sealed class SizeDecision
{
    public string TargetId { get; init; } = "";
    /// <summary>Diámetro o ancho (mm).</summary>
    public double ValueMm { get; init; }
    /// <summary>Alto (mm), si el rótulo lo indica.</summary>
    public double? HeightMm { get; init; }
    public SizeSource Source { get; init; }
    public List<SizeAnnotation> From { get; } = new();
}

public sealed class SizeAssociation
{
    /// <summary>Todas las cotas y rótulos de tamaño del plano (aplicados o no).</summary>
    public List<SizeAnnotation> Annotations { get; } = new();
    public Dictionary<string, SizeDecision> Decisions { get; } = new();
}

/// <summary>
/// Lee las cotas (DIMENSION) y, como respaldo, los rótulos de tamaño, y los asocia a los tramos modelados.
/// No inventa medidas: una cota ilegible, contradictoria, ambigua o que no se asocia a ningún tramo no se aplica.
/// </summary>
public static class SizeAnnotations
{
    public sealed class Settings
    {
        public double UnitToMm { get; init; } = 1.0;
        /// <summary>Desplazamiento aplicado al modelar (mm).</summary>
        public P3 OffsetMm { get; init; }
        /// <summary>Tolerancia geométrica (mm): la misma tolerancia de unión del modelado.</summary>
        public double ToleranceMm { get; init; } = PlanDefaults.ToleranceMm;
        /// <summary>Desviación máxima (grados) entre la dirección de la cota y la perpendicular al tramo.</summary>
        public double MaxAngleDeg { get; init; } = 5;
        /// <summary>Distancia máxima (mm) de un rótulo a su tramo.</summary>
        public double MaxLabelDistanceMm { get; init; } = 1000;
        /// <summary>Si el segundo tramo más cercano no está al menos este factor más lejos, el rótulo es ambiguo.</summary>
        public double AmbiguityRatio { get; init; } = 1.5;
        /// <summary>Dos medidas que difieren menos que esto (mm) se consideran iguales.</summary>
        public double SameValueMm { get; init; } = 0.5;
        /// <summary>Aplicar la medida de un rótulo a todo su recorrido (tramos unidos por codos o en línea, sin tes).</summary>
        public bool PropagateAlongRuns { get; init; } = true;
    }

    private static readonly Regex DiameterRx = new(@"(?:Ø|∅|⌀|(?<![A-Za-z])DN\s*-?|(?<![A-Za-z])D\s*=|(?<![A-Za-z])DI[AÁ]M(?:ETRO|\.)?)\s*((?>\d+(?:[.,]\d+)?))(?!\s*/\s*\d)(?!\s*(?:""|''|”|″))",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);
    /// <summary>Diámetros en pulgadas: «Ø1/2"», «DN 1 1/4"», «Ø2"».</summary>
    private static readonly Regex InchRx = new(@"(?:Ø|∅|⌀|(?<![A-Za-z])DN\s*-?)\s*(\d+\s+\d+\s*/\s*\d+|\d+\s*/\s*\d+|\d+(?:[.,]\d+)?)\s*(?:""|''|”|″)",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex RectRx = new(@"(?<![\d.,])(\d{2,4}(?:[.,]\d+)?)\s*[xX×]\s*(\d{2,4}(?:[.,]\d+)?)(?![\d.,])", RegexOptions.Compiled);
    private static readonly Regex PrefixRx = new(@"([A-Za-zÁÉÍÓÚÑáéíóúñ]{2,})\s*[:=]?\s*$", RegexOptions.Compiled);
    private static readonly Regex NumberRx = new(@"^\s*(\d+(?:[.,]\d+)?)\s*(mm)?\s*$", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    /// <summary>Rótulo de tamaño interpretado. Las medidas de los rótulos se leen en mm.</summary>
    public sealed record Label(bool Readable, SizeShape Shape, double ValueMm, double? HeightMm, string? Prefix, string Problem);

    /// <summary>Interpreta un texto: null si no habla de tamaños; Readable = false si lo parece pero no se puede leer.</summary>
    public static Label? ParseLabel(string text)
    {
        var all = ParseLabels(text);
        if (all is null) return null;
        if (all.Count == 1) return all[0];
        return new Label(false, SizeShape.Redonda, 0, null, null, "tiene varios diámetros distintos");
    }

    /// <summary>
    /// Como <see cref="ParseLabel"/>, pero un rótulo con varios diámetros, cada uno con su nombre delante
    /// («AFS Ø16 ACS Ø20»), da una medida por nombre. null si el texto no habla de tamaños.
    /// </summary>
    public static List<Label>? ParseLabels(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        // Diámetros: en mm y en pulgadas (se pasan al diámetro nominal en mm).
        var found = new List<(int Index, int End, int NumIndex, int NumEnd, double? Mm)>();
        foreach (Match m in InchRx.Matches(text))
            found.Add((m.Index, m.Index + m.Length, m.Groups[1].Index, m.Groups[1].Index + m.Groups[1].Length, InchesToMm(m.Groups[1].Value)));
        foreach (Match m in DiameterRx.Matches(text))
            if (!found.Any(f => m.Index < f.End && f.Index < m.Index + m.Length))
                found.Add((m.Index, m.Index + m.Length, m.Groups[1].Index, m.Groups[1].Index + m.Groups[1].Length, Num(m.Groups[1].Value)));
        found.Sort((a, b) => a.Index.CompareTo(b.Index));
        // «Ø110x4,2» (diámetro × espesor) no es un ancho × alto: se descartan los que empiezan en el número de un diámetro.
        var r = RectRx.Matches(text).Where(m => !found.Any(f => m.Index < f.NumEnd && f.NumIndex < m.Index + m.Length)).ToList();
        if (found.Count == 0 && r.Count == 0) return null;
        if (found.Count > 0 && r.Count > 0)
            return new() { new Label(false, SizeShape.Redonda, 0, null, null, "mezcla un diámetro y un ancho × alto") };
        if (found.Count > 0)
        {
            var values = found.Select(f => f.Mm).ToList();
            if (values.Any(v => v is null or <= 0 or > 5000)) return new() { new Label(false, SizeShape.Redonda, 0, null, null, "diámetro fuera de rango") };
            if (values.Distinct().Count() == 1)
                return new() { new Label(true, SizeShape.Redonda, values[0]!.Value, null, Prefix(text, found[0].Index), "") };
            // Varios diámetros distintos: solo si cada uno lleva su nombre delante y los nombres son distintos.
            var parts = new List<Label>();
            int prev = 0;
            foreach (var f in found)
            {
                parts.Add(new Label(true, SizeShape.Redonda, f.Mm!.Value, null, Prefix(text[prev..f.Index] + " ", f.Index - prev), ""));
                prev = f.End;
            }
            if (parts.All(p => p.Prefix != null) && parts.Select(p => p.Prefix!.ToUpperInvariant()).Distinct().Count() == parts.Count)
                return parts;
            return new() { new Label(false, SizeShape.Redonda, 0, null, null, "tiene varios diámetros distintos") };
        }
        var pairs = r.Select(m => (W: Num(m.Groups[1].Value), H: Num(m.Groups[2].Value))).ToList();
        if (pairs.Any(p => p.W is null or <= 0 or > 5000 || p.H is null or <= 0 or > 5000))
            return new() { new Label(false, SizeShape.AnchoAlto, 0, null, null, "medida fuera de rango") };
        if (pairs.Distinct().Count() > 1) return new() { new Label(false, SizeShape.AnchoAlto, 0, null, null, "tiene varias medidas distintas") };
        return new() { new Label(true, SizeShape.AnchoAlto, pairs[0].W!.Value, pairs[0].H, Prefix(text, r[0].Index), "") };
    }

    /// <summary>Diámetro nominal (mm) de una medida en pulgadas: «1/2» → 15, «1 1/4» → 32, «2» → 50.</summary>
    private static double? InchesToMm(string s)
    {
        s = Regex.Replace(s.Trim(), @"\s*/\s*", "/");
        double inches = 0;
        foreach (var part in s.Split(' ', StringSplitOptions.RemoveEmptyEntries))
        {
            if (part.Contains('/'))
            {
                var f = part.Split('/');
                if (Num(f[0]) is not double a || Num(f[1]) is not double b || b == 0) return null;
                inches += a / b;
            }
            else if (Num(part) is double v) inches += v;
            else return null;
        }
        var nominal = new Dictionary<double, double>
        {
            [0.25] = 8, [0.375] = 10, [0.5] = 15, [0.75] = 20, [1] = 25, [1.25] = 32, [1.5] = 40, [2] = 50,
            [2.5] = 65, [3] = 80, [4] = 100, [5] = 125, [6] = 150, [8] = 200, [10] = 250, [12] = 300
        };
        foreach (var (inch, mm) in nominal) if (Math.Abs(inch - inches) < 1e-6) return mm;
        return inches > 0 ? Math.Round(inches * 25.4, 1) : null;
    }

    private static string? Prefix(string text, int index)
    {
        var m = PrefixRx.Match(text[..index]);
        return m.Success ? m.Groups[1].Value : null;
    }

    private static double? Num(string s) =>
        double.TryParse(s.Replace(',', '.'), NumberStyles.Float, CultureInfo.InvariantCulture, out double v) ? v : null;

    public static SizeAssociation Associate(CadDrawing drawing, IReadOnlyList<SizeTarget> targets, Settings s)
    {
        var result = new SizeAssociation();
        double k = s.UnitToMm;
        P3 ToMm(P3 p) => new(p.X * k + s.OffsetMm.X, p.Y * k + s.OffsetMm.Y, 0);
        var flat = targets.Select(t => (T: t, A: new P3(t.Start.X, t.Start.Y, 0), B: new P3(t.End.X, t.End.Y, 0))).ToList();
        // Líneas del CAD de cada capa con tramos (mm): una cota solo vale si mide entre líneas del propio tramo.
        var targetLayers = new HashSet<string>(targets.Select(t => t.Layer), StringComparer.OrdinalIgnoreCase);
        var cadLines = drawing.Segments.Where(x => targetLayers.Contains(x.Layer))
            .GroupBy(x => x.Layer, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.Select(x => (A: ToMm(x.Start), B: ToMm(x.End))).ToList(), StringComparer.OrdinalIgnoreCase);
        bool OnLayerLine(string layer, P3 p) =>
            cadLines.TryGetValue(layer, out var lines) && lines.Any(l => NetworkBuilder.ProjectToSegment(p, l.A, l.B).Dist <= s.ToleranceMm);
        var byDimension = new Dictionary<string, List<SizeAnnotation>>();
        var byLabel = new Dictionary<string, List<SizeAnnotation>>();
        static void Add(Dictionary<string, List<SizeAnnotation>> d, string id, SizeAnnotation a)
        {
            if (!d.TryGetValue(id, out var l)) d[id] = l = new List<SizeAnnotation>();
            l.Add(a);
        }

        // 1) Cotas DIMENSION: primero su valor medido; el texto escrito solo como respaldo.
        foreach (var dim in drawing.Dimensions)
        {
            P3 pos = dim.FirstPoint is P3 fa && dim.SecondPoint is P3 fb ? ToMm((fa + fb) * 0.5) : default;
            var ann = new SizeAnnotation
            {
                EntityIndex = dim.EntityIndex, Layer = dim.Layer, Source = SizeSource.Cota, PositionMm = pos,
                Description = $"cota {dim.Kind}" + (dim.Text.Length > 0 ? $" con texto «{dim.Text}»" : "") + $" (capa '{dim.Layer}')"
            };
            result.Annotations.Add(ann);
            string text = dim.Text.Trim();
            bool textShowsMeasure = text.Length == 0 || text.Contains("<>");
            double value;
            if (double.IsFinite(dim.Measurement) && dim.Measurement > 0)
            {
                value = dim.Measurement * k;
                if (!textShowsMeasure && !TextAgrees(text, dim.Measurement, k, s.SameValueMm))
                {
                    ann.ValueMm = value;
                    ann.Reason = $"contradictoria: el texto escrito «{text}» sustituye al valor medido ({value:0.#} mm) y no coincide con él";
                    continue;
                }
            }
            else if (ParseLabel(text) is { Readable: true } written)
            {
                value = written.ValueMm;
                ann.Source = SizeSource.TextoDeCota;
            }
            else
            {
                ann.Reason = "ilegible: no tiene valor medido ni un texto con la medida (Ø…, DN…, ancho x alto)";
                continue;
            }
            ann.ValueMm = value;
            if (value <= 0 || value > MaxSizeMm) { ann.Reason = $"no se aplica: mide {value:0.#} mm, fuera del rango de un diámetro o un ancho (0–{MaxSizeMm:0} mm)"; continue; }
            if (!dim.IsPlanar) { ann.Reason = "no se aplica: la cota no está dibujada en planta"; continue; }
            if (dim.FirstPoint is not P3 a0 || dim.SecondPoint is not P3 b0 || dim.Direction is not P3 dir)
            {
                ann.Reason = $"no se asocia: una cota {dim.Kind} no mide el ancho ni el diámetro de un tramo (solo se usan cotas lineales y alineadas)";
                continue;
            }
            P3 p1 = ToMm(a0), p2 = ToMm(b0);
            var m = new P3(dir.X, dir.Y, 0).Normalized();
            var across = flat.Where(f => SpansAcross(f.A, f.B, p1, p2, m, value, s)).ToList();
            // Además, sus dos puntos tienen que estar sobre líneas de la capa del tramo (su contorno): una cota de
            // pared a pared que pasa por encima de un conducto no mide el conducto.
            var hits = across.Where(f => OnLayerLine(f.T.Layer, p1) && OnLayerLine(f.T.Layer, p2)).Select(f => f.T).ToList();
            if (hits.Count == 0 && across.Count > 0)
            {
                ann.Reason = $"no se asocia: cruza un tramo de la capa '{across[0].T.Layer}', pero sus puntos no están sobre las líneas de ese tramo " +
                             $"(mide otra cosa: {value:0.#} mm)";
                continue;
            }
            if (hits.Count == 0) { ann.Reason = $"no se asocia a ningún tramo modelado (mide {value:0.#} mm y no cruza ningún tramo de lado a lado)"; continue; }
            if (hits.Count > 1) { ann.Reason = $"no se aplica: cruza {hits.Count} tramos a la vez"; continue; }
            ann.TargetId = hits[0].Id;
            Add(byDimension, hits[0].Id, ann);
        }

        // 2) Rótulos de tamaño (respaldo): el tramo compatible más cercano al rótulo (a la punta de su directriz si la
        //    tiene, o al centro del texto), si no hay duda. Los tramos de un mismo recorrido (unidos por codos o en
        //    línea, sin tes) cuentan como uno solo: un rótulo junto a un codo no es ambiguo.
        var run = Runs(targets, s.ToleranceMm, out var runEdges);
        foreach (var tx in drawing.Texts)
        {
            var labels = ParseLabels(tx.Value);
            if (labels is null) continue; // no habla de tamaños: es una anotación más
            var refPoint = ToMm(tx.Anchor ?? tx.Center);
            double textMm = Math.Abs(tx.Height) * k;
            // Distancia máxima: la fija o, en planos con textos grandes, 10 alturas de texto. Con directriz, el rótulo
            // señala el tubo: su punta tiene que estar sobre él.
            double maxD = tx.Anchor != null
                ? Math.Max(Math.Max(5 * s.ToleranceMm, 300), 3 * textMm)
                : Math.Max(s.MaxLabelDistanceMm, Math.Min(10 * textMm, 3 * s.MaxLabelDistanceMm));
            P3 textDir = new(Math.Cos(tx.RotationRad), Math.Sin(tx.RotationRad), 0);
            foreach (var label in labels)
            {
                var ann = new SizeAnnotation
                {
                    EntityIndex = tx.EntityIndex, Layer = tx.Layer, Source = SizeSource.Rotulo, PositionMm = refPoint,
                    Description = $"rótulo «{tx.Value}»" + (labels.Count > 1 ? $" (parte «{label.Prefix}»)" : "") +
                                  (tx.Anchor != null ? " con directriz" : "") + $" (capa '{tx.Layer}')"
                };
                result.Annotations.Add(ann);
                if (!label.Readable) { ann.Reason = "ilegible: " + label.Problem; continue; }
                ann.ValueMm = label.ValueMm;
                ann.HeightMm = label.HeightMm;
                ann.Prefix = label.Prefix;
                var compatible = flat.Select((f, i) => (f.T, I: i, D: NetworkBuilder.ProjectToSegment(refPoint, f.A, f.B).Dist, f.A, f.B))
                    .Where(f => f.T.EitherShape || f.T.Shape == label.Shape).ToList();
                if (compatible.Count == 0)
                {
                    ann.Reason = "no se asocia: no hay ningún tramo modelado de sección " +
                                 (label.Shape == SizeShape.Redonda ? "redonda (diámetro)" : "rectangular (ancho × alto)");
                    continue;
                }
                // Relación con la capa, de más a menos fuerte:
                //  1. el rótulo empieza por un nombre que es palabra completa de la capa («ACS:» → «ACS_Caliente», no «RACS»);
                //  2. su capa comparte una palabra con la del tramo («-P_CL_TEXTOS» → «-P_CL_CONDUCTO»);
                //  3. cualquier tramo cercano. Para «ancho x alto» no, si el texto es de estructura («V 30x50» en E-VIGAS).
                // Si el prefijo nombra una capa que existe, solo valen los tramos de esa capa.
                bool prefixNamesLayer = label.Prefix is string pre0 && compatible.Any(f => HasWord(f.T.Layer, pre0));
                var within = compatible.Where(f => f.D <= maxD).ToList();
                var labelWords = Words(tx.Layer);
                var tiers = new List<List<(SizeTarget T, int I, double D, P3 A, P3 B)>>();
                if (prefixNamesLayer) tiers.Add(within.Where(f => HasWord(f.T.Layer, label.Prefix!)).ToList());
                else
                {
                    tiers.Add(within.Where(f => Words(f.T.Layer).Overlaps(labelWords)).ToList());
                    if (label.Shape == SizeShape.Redonda || !LooksStructural(tx.Layer)) tiers.Add(within);
                }
                var pool = tiers.FirstOrDefault(t => t.Count > 0);
                if (pool is null)
                {
                    if (prefixNamesLayer)
                    {
                        double near = compatible.Where(f => HasWord(f.T.Layer, label.Prefix!)).Min(f => f.D);
                        ann.Reason = $"no se asocia: su prefijo «{label.Prefix}» indica la capa del tramo, pero el más cercano de esa capa está a {near:0} mm (máximo {maxD:0} mm)";
                    }
                    else if (within.Count == 0)
                        ann.Reason = $"no se asocia: el tramo compatible más cercano está a {compatible.Min(f => f.D):0} mm (máximo {maxD:0} mm)";
                    else
                        ann.Reason = $"no se asocia: ni su prefijo ni su capa ('{tx.Layer}') coinciden con la capa de ningún tramo compatible";
                    continue;
                }
                // El más cercano de cada recorrido; si dos recorridos distintos están casi a la misma distancia, decide
                // la orientación del texto (se escribe paralelo a su tubo). Si aun así hay duda, no se aplica.
                var ranked = pool.GroupBy(f => run[f.I]).Select(g => g.OrderBy(f => f.D).First()).OrderBy(f => f.D).ToList();
                var best = ranked[0];
                // Tubo dibujado con sus dos bordes (dos líneas paralelas de la misma capa, separadas como mucho lo que
                // mide): el rótulo vale para las dos y la segunda no crea duda.
                int partner = ranked.Skip(1).Where(f => TwoEdgesOfOnePipe(new() { (best.T, best.A, best.B), (f.T, f.A, f.B) }, label.ValueMm, s.ToleranceMm))
                    .Select(f => f.I).DefaultIfEmpty(-1).First();
                double band = Math.Max(best.D * s.AmbiguityRatio, best.D + s.ToleranceMm);
                var close = ranked.Where(f => f.D <= band && f.I != partner).ToList();
                if (partner >= 0 && close.Count == 1)
                {
                    ann.TargetId = best.T.Id;
                    Add(byLabel, best.T.Id, ann);
                    Add(byLabel, targets[partner].Id, ann);
                    continue;
                }
                if (close.Count > 1)
                {
                    var parallel = close.Where(f => Parallel(f.A, f.B, textDir)).ToList();
                    if (parallel.Count == 1) best = parallel[0];
                    else
                    {
                        ann.Reason = $"no se aplica: está casi a la misma distancia de dos tramos ({close[0].D:0} y {close[1].D:0} mm)";
                        continue;
                    }
                }
                ann.TargetId = best.T.Id;
                Add(byLabel, best.T.Id, ann);
            }
        }

        // 3) Una medida por tramo. Las cotas mandan; los rótulos solo si el tramo no tiene cota.
        foreach (var t in targets)
        {
            byDimension.TryGetValue(t.Id, out var dims);
            byLabel.TryGetValue(t.Id, out var labels);
            if (dims is { Count: > 0 })
            {
                if (Agree(dims, s.SameValueMm)) Decide(result, t.Id, dims);
                else Contradict(dims);
                foreach (var l in labels ?? new())
                    l.Reason = "no se aplica: el tramo tiene una cota DIMENSION (los rótulos solo se usan como respaldo)";
                continue;
            }
            if (labels is { Count: > 0 })
            {
                if (Agree(labels, s.SameValueMm)) Decide(result, t.Id, labels);
                else Contradict(labels);
            }
        }

        // 4) Un rótulo vale para todo su recorrido: los tramos unidos por codos o en línea (sin tes ni cruces) que no
        //    tienen medida propia toman la del tramo con medida más próximo del mismo recorrido. Si dos medidas
        //    distintas llegan a la vez, ese tramo no se toca.
        if (s.PropagateAlongRuns) Propagate(result, targets, runEdges);
        return result;
    }

    /// <summary>
    /// Recorridos: grupos de tramos de la misma capa y sección unidos extremo con extremo en nudos donde solo se
    /// encuentran dos tramos (codo o tramo partido en línea). Una te o una cruz separa recorridos.
    /// Devuelve el número de recorrido de cada tramo y las uniones entre tramos de un mismo recorrido.
    /// </summary>
    internal static int[] Runs(IReadOnlyList<SizeTarget> targets, double tolMm, out List<(int A, int B)> edges)
    {
        int n = targets.Count;
        var parent = Enumerable.Range(0, n).ToArray();
        int Find(int x) { while (parent[x] != x) x = parent[x] = parent[parent[x]]; return x; }
        edges = new List<(int, int)>();
        double cell = Math.Max(tolMm, 1e-6);
        // Nudos: extremos a menos de la tolerancia, por capa y sección.
        var nodes = new List<(P3 P, string Key, List<int> Ends)>();
        var grid = new Dictionary<(long, long), List<int>>();
        int NodeOf(P3 p, string key)
        {
            long gx = (long)Math.Floor(p.X / cell), gy = (long)Math.Floor(p.Y / cell);
            for (long dx = -1; dx <= 1; dx++)
                for (long dy = -1; dy <= 1; dy++)
                    if (grid.TryGetValue((gx + dx, gy + dy), out var list))
                        foreach (int id in list)
                            if (nodes[id].Key == key && Math.Abs(nodes[id].P.X - p.X) <= tolMm && Math.Abs(nodes[id].P.Y - p.Y) <= tolMm &&
                                new P3(nodes[id].P.X - p.X, nodes[id].P.Y - p.Y, 0).Length <= tolMm)
                                return id;
            nodes.Add((p, key, new List<int>()));
            if (!grid.TryGetValue((gx, gy), out var l)) grid[(gx, gy)] = l = new List<int>();
            l.Add(nodes.Count - 1);
            return nodes.Count - 1;
        }
        for (int i = 0; i < n; i++)
        {
            var t = targets[i];
            string key = t.Layer.ToUpperInvariant() + "|" + t.Shape;
            nodes[NodeOf(t.Start, key)].Ends.Add(i);
            nodes[NodeOf(t.End, key)].Ends.Add(i);
        }
        foreach (var node in nodes)
        {
            if (node.Ends.Count != 2 || node.Ends[0] == node.Ends[1]) continue;
            int a = node.Ends[0], b = node.Ends[1];
            edges.Add((a, b));
            parent[Find(a)] = Find(b);
        }
        var result = new int[n];
        for (int i = 0; i < n; i++) result[i] = Find(i);
        return result;
    }

    private static void Propagate(SizeAssociation result, IReadOnlyList<SizeTarget> targets, List<(int A, int B)> edges)
    {
        if (edges.Count == 0 || result.Decisions.Count == 0) return;
        var adj = new Dictionary<int, List<int>>();
        foreach (var (a, b) in edges)
        {
            if (!adj.TryGetValue(a, out var la)) adj[a] = la = new List<int>();
            if (!adj.TryGetValue(b, out var lb)) adj[b] = lb = new List<int>();
            la.Add(b); lb.Add(a);
        }
        // Búsqueda en anchura desde todos los tramos con medida propia a la vez.
        var source = new Dictionary<int, SizeDecision?>();
        var depth = new Dictionary<int, int>();
        var queue = new Queue<int>();
        for (int i = 0; i < targets.Count; i++)
            if (result.Decisions.TryGetValue(targets[i].Id, out var d) && adj.ContainsKey(i)) { source[i] = d; depth[i] = 0; queue.Enqueue(i); }
        while (queue.Count > 0)
        {
            int cur = queue.Dequeue();
            var from = source[cur];
            if (from is null) continue; // conflicto: no se sigue propagando desde aquí
            foreach (int next in adj[cur])
            {
                if (!depth.TryGetValue(next, out int dn))
                {
                    depth[next] = depth[cur] + 1;
                    source[next] = from;
                    queue.Enqueue(next);
                }
                else if (dn == depth[cur] + 1 && source[next] is { } other && !SameSize(other, from))
                    source[next] = null; // dos medidas distintas a la misma distancia: no se decide
            }
        }
        foreach (var (i, from) in source)
        {
            if (from is null || depth[i] == 0 || result.Decisions.ContainsKey(targets[i].Id)) continue;
            var d = new SizeDecision { TargetId = targets[i].Id, ValueMm = from.ValueMm, HeightMm = from.HeightMm, Source = SizeSource.Recorrido };
            d.From.AddRange(from.From);
            result.Decisions[targets[i].Id] = d;
        }
    }

    private static bool SameSize(SizeDecision a, SizeDecision b) =>
        Math.Abs(a.ValueMm - b.ValueMm) <= 0.5 && (a.HeightMm is null) == (b.HeightMm is null) &&
        (a.HeightMm is null || Math.Abs(a.HeightMm.Value - b.HeightMm!.Value) <= 0.5);

    /// <summary>
    /// ¿Son los dos bordes de un mismo tubo? Dos tramos de la misma capa, paralelos, que se solapan a lo largo y
    /// están separados como mucho 1,5 veces la medida del rótulo.
    /// </summary>
    private static bool TwoEdgesOfOnePipe(List<(SizeTarget T, P3 A, P3 B)> close, double valueMm, double tolMm)
    {
        if (close.Count != 2 || !string.Equals(close[0].T.Layer, close[1].T.Layer, StringComparison.OrdinalIgnoreCase)) return false;
        var (a1, b1) = (close[0].A, close[0].B);
        var (a2, b2) = (close[1].A, close[1].B);
        double len = a1.DistanceTo(b1);
        if (len < 1e-9 || a2.DistanceTo(b2) < 1e-9) return false;
        var u = (b1 - a1) * (1.0 / len);
        if (!Parallel(a2, b2, u)) return false;
        var n = new P3(-u.Y, u.X, 0);
        double sep = Math.Abs(n.Dot((a2 + b2) * 0.5 - a1));
        if (sep > 1.5 * valueMm + tolMm) return false;
        double s0 = u.Dot(a2 - a1), s1 = u.Dot(b2 - a1);
        return Math.Max(s0, s1) > 0 && Math.Min(s0, s1) < len; // se solapan a lo largo
    }

    /// <summary>¿El texto está escrito en la dirección del tramo (±10°)?</summary>
    private static bool Parallel(P3 a, P3 b, P3 textDir)
    {
        var u = new P3(b.X - a.X, b.Y - a.Y, 0).Normalized();
        return Math.Abs(u.X * textDir.Y - u.Y * textDir.X) <= Math.Sin(10 * Math.PI / 180);
    }

    /// <summary>Capas de estructura o arquitectura: sus «30x50» son vigas o pilares, no conductos.</summary>
    private static bool LooksStructural(string layer) =>
        Regex.IsMatch(layer ?? "", @"VIGA|PILAR|ESTRUC|FORJ|ZAPATA|ZUNCHO|CIMENT|MURO|ARQ|^E[-_ ]|^S[-_ ]|^A[-_ ]", RegexOptions.IgnoreCase);

    /// <summary>Medida máxima admitida para un diámetro o un ancho (mm).</summary>
    public const double MaxSizeMm = 5000;

    /// <summary>Palabras que no relacionan capas (nombres genéricos de capas de texto).</summary>
    private static readonly HashSet<string> GenericWords = new(StringComparer.OrdinalIgnoreCase)
    {
        "TEXTO", "TEXTOS", "TEXT", "TXT", "ROTULO", "ROTULOS", "RÓTULO", "RÓTULOS", "ANOTACION", "ANOTACIONES", "ANOTACIÓN",
        "NOTA", "NOTAS", "LABEL", "LABELS", "CAPA", "LAYER", "COTA", "COTAS", "DIM", "DIMENSION", "DEFPOINTS", "TAG", "TAGS"
    };

    /// <summary>Palabras (de 2 letras o más) del nombre de una capa, sin las genéricas.</summary>
    private static HashSet<string> Words(string layer) =>
        Regex.Split(layer ?? "", "[^A-Za-zÁÉÍÓÚÑáéíóúñ]+").Where(w => w.Length >= 2 && !GenericWords.Contains(w))
            .Select(w => w.ToUpperInvariant()).ToHashSet();

    private static bool HasWord(string layer, string word) =>
        Regex.IsMatch(layer, $@"(^|[^A-Za-zÁÉÍÓÚÑáéíóúñ]){Regex.Escape(word)}([^A-Za-zÁÉÍÓÚÑáéíóúñ]|$)", RegexOptions.IgnoreCase);

    private static void Decide(SizeAssociation result, string id, List<SizeAnnotation> from)
    {
        var d = new SizeDecision { TargetId = id, ValueMm = from[0].ValueMm!.Value, HeightMm = from[0].HeightMm, Source = from[0].Source };
        foreach (var a in from) { a.Applied = true; a.Reason = ""; d.From.Add(a); }
        result.Decisions[id] = d;
    }

    private static void Contradict(List<SizeAnnotation> list)
    {
        string values = string.Join(", ", list.Select(a => a.ValueMm?.ToString("0.#", CultureInfo.InvariantCulture) +
                                                           (a.HeightMm is double h ? "×" + h.ToString("0.#", CultureInfo.InvariantCulture) : "")).Distinct());
        foreach (var a in list) a.Reason = $"contradictoria: el mismo tramo tiene {list.Count} medidas distintas ({values} mm)";
    }

    private static bool Agree(List<SizeAnnotation> list, double same) =>
        list.All(a => Math.Abs(a.ValueMm!.Value - list[0].ValueMm!.Value) <= same &&
                      (a.HeightMm is null) == (list[0].HeightMm is null) &&
                      (a.HeightMm is null || Math.Abs(a.HeightMm.Value - list[0].HeightMm!.Value) <= same));

    /// <summary>
    /// ¿El texto escrito en la cota dice lo mismo que lo que mide? Se acepta un número en unidades del dibujo o
    /// en mm, o un rótulo (Ø…, DN…). Cualquier otra cosa se considera contradictoria.
    /// </summary>
    private static bool TextAgrees(string text, double measured, double k, double same)
    {
        if (ParseLabel(text) is { Readable: true } l) return Math.Abs(l.ValueMm - measured * k) <= same;
        var m = NumberRx.Match(text);
        if (!m.Success || Num(m.Groups[1].Value) is not double n) return false;
        return Math.Abs(n - measured) * k <= same || Math.Abs(n - measured * k) <= same;
    }

    /// <summary>
    /// ¿La cota cruza el tramo de lado a lado? Mide en perpendicular al eje, sus dos puntos están uno a cada
    /// lado, centrados en el eje, separados lo que mide y dentro de la longitud del tramo.
    /// </summary>
    private static bool SpansAcross(P3 a, P3 b, P3 p1, P3 p2, P3 m, double value, Settings s)
    {
        double len = a.DistanceTo(b);
        if (len < 1e-9) return false;
        var u = (b - a) * (1.0 / len);
        if (Math.Abs(u.Dot(m)) > Math.Sin(s.MaxAngleDeg * Math.PI / 180)) return false;
        var n = new P3(-u.Y, u.X, 0);
        double d1 = n.Dot(p1 - a), d2 = n.Dot(p2 - a);
        if (d1 * d2 >= 0) return false;
        if (Math.Abs(Math.Abs(d1 - d2) - value) > s.ToleranceMm) return false;
        if (Math.Abs(d1 + d2) / 2 > s.ToleranceMm) return false;
        double along = u.Dot((p1 + p2) * 0.5 - a);
        return along >= -s.ToleranceMm && along <= len + s.ToleranceMm;
    }
}
