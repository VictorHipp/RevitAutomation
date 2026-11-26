namespace CadMepRevit.Core;

/// <summary>Qué ha pasado con una entidad del CAD.</summary>
public sealed record EntityDisposition(int Index, string Type, string Layer, bool Modeled, string Reason);

/// <summary>Elemento lineal creado (o que se intentó crear) en Revit, en coordenadas del modelo (mm).</summary>
public sealed record ModeledRun(string Layer, P3 Start, P3 End, double HalfWidthMm, string What);

public sealed class InventoryInput
{
    public CadDrawing Drawing { get; init; } = new();
    public double UnitToMm { get; init; } = 1.0;
    public P3 OffsetMm { get; init; }
    public double ToleranceMm { get; init; } = PlanDefaults.ToleranceMm;
    /// <summary>Capas con líneas que NO se construyen y su motivo.</summary>
    public Dictionary<string, string> SkippedLayers { get; init; } = new(StringComparer.OrdinalIgnoreCase);
    /// <summary>Tramos y muros creados en Revit.</summary>
    public List<ModeledRun> Created { get; init; } = new();
    /// <summary>Tramos previstos que Revit no pudo crear (What = el error).</summary>
    public List<ModeledRun> Failed { get; init; } = new();
    public Dictionary<int, (bool Modeled, string What)> Circles { get; } = new();
    /// <summary>Bloques (por número de entidad): si se han modelado y qué se ha hecho, o por qué no.</summary>
    public Dictionary<int, (bool Modeled, string What)> Blocks { get; init; } = new();
    /// <summary>Entidades que forman luminarias dibujadas con líneas (o el bloque de planta que las contiene): qué ha pasado.</summary>
    public Dictionary<int, (bool Modeled, string What)> Fixtures { get; init; } = new();
}

public sealed class InventoryResult
{
    public List<EntityDisposition> Entities { get; } = new();
    public int Total => Entities.Count;
    public int Modeled => Entities.Count(e => e.Modeled);
    public int NotModeled => Entities.Count(e => !e.Modeled);
    /// <summary>Elementos creados en Revit que ninguna entidad del plano explica (debería ser 0).</summary>
    public List<ModeledRun> UnexplainedRuns { get; } = new();
    /// <summary>Tramos de cierre: la red los crea entre dos tramos dibujados (hueco de pieza especial o curva).</summary>
    public List<ModeledRun> ClosingRuns { get; } = new();
    /// <summary>Entidades del plano sin destino o con más de uno (debería ser 0).</summary>
    public int MissingOrRepeated { get; internal set; }
    /// <summary>
    /// Cuadre en los dos sentidos: cada entidad del plano tiene exactamente un destino (modelada o no, con
    /// motivo) y cada elemento creado está explicado por al menos una entidad del plano.
    /// </summary>
    public bool Balanced { get; internal set; }
    /// <summary>Resumen por destino: (modelada, motivo, tipo de entidad) → número.</summary>
    public IEnumerable<(bool Modeled, string Reason, string Type, int Count)> Summary() =>
        Entities.GroupBy(e => (e.Modeled, e.Reason, e.Type))
            .OrderByDescending(g => g.Key.Modeled).ThenByDescending(g => g.Count())
            .Select(g => (g.Key.Modeled, g.Key.Reason, g.Key.Type, g.Count()));
}

/// <summary>
/// Cruza el inventario del CAD con lo modelado: cada entidad queda como modelada o no modelada, con su motivo.
/// Una línea cuenta como modelada si la mayor parte de su longitud cae dentro de un elemento creado
/// (eje ± medio ancho ± tolerancia). Así el total del plano = modeladas + no modeladas.
/// </summary>
public static class ModelInventory
{
    /// <summary>
    /// Longitud máxima de un tramo de cierre (mm): el mayor hueco que cierra la doble línea
    /// (<see cref="OutlineDetector.Settings.MaxGapMm"/>) y la mayor curva que la red convierte en esquina
    /// (<see cref="NetworkBuilder.Settings.MaxFilletMm"/>), ambos 1500 mm por defecto.
    /// </summary>
    public const double MaxClosingMm = 1500;
    public const string ReasonText = "Texto (anotación: no es un elemento constructivo)";
    public const string ReasonDimension = "Cota (anotación para las medidas del modelado)";
    public const string ReasonXref = "Referencia externa (XREF) sin cargar: su contenido no está en el archivo";
    public const string ReasonNested = "Entidad dentro de un bloque (se representa con el bloque)";
    public const string ReasonZero = "Geometría de longitud cero";
    public const string ReasonDiscarded = "Descartada al preparar la red (tramo más corto que la longitud mínima, línea sin pareja en doble línea o resto de una pieza especial)";

    public static InventoryResult Classify(InventoryInput input)
    {
        var result = new InventoryResult();
        var d = input.Drawing;
        double k = input.UnitToMm, tol = input.ToleranceMm;
        P3 ToMm(P3 p) => new(p.X * k + input.OffsetMm.X, p.Y * k + input.OffsetMm.Y, 0);
        var segsByEntity = d.Segments.GroupBy(s => s.EntityIndex).ToDictionary(g => g.Key, g => g.ToList());
        var created = Index(input.Created);
        var failed = Index(input.Failed);
        var explained = new HashSet<ModeledRun>(ReferenceEqualityComparer.Instance);
        var geometryTypes = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "LINE", "LWPOLYLINE", "POLYLINE", "ARC" };

        foreach (var e in d.Entities)
        {
            EntityDisposition Out(bool modeled, string reason) => new(e.Index, e.Type, e.Layer, modeled, reason);
            string type = e.Type.ToUpperInvariant();
            if (type is "TEXT" or "MTEXT" or "ATTRIB" or "ATTDEF") { result.Entities.Add(Out(false, ReasonText)); continue; }
            if (type == "DIMENSION") { result.Entities.Add(Out(false, ReasonDimension)); continue; }
            if (e.Depth == 0 && input.Fixtures.TryGetValue(e.Index, out var fixture)) { result.Entities.Add(Out(fixture.Modeled, fixture.What)); continue; }
            if (type == "CIRCLE" && input.Circles.TryGetValue(e.Index, out var circle))
            {
                result.Entities.Add(Out(circle.Modeled, circle.What));
                continue;
            }
            if (type == "INSERT")
            {
                if (d.UnloadedXrefs.Contains(e.Index)) result.Entities.Add(Out(false, ReasonXref));
                else if (e.Depth > 0) result.Entities.Add(Out(false, ReasonNested));
                else if (input.Blocks.TryGetValue(e.Index, out var b)) result.Entities.Add(Out(b.Modeled, b.What));
                else result.Entities.Add(Out(false, "Bloque no colocado"));
                continue;
            }
            if (segsByEntity.TryGetValue(e.Index, out var segs))
            {
                if (input.SkippedLayers.TryGetValue(e.Layer, out var why)) { result.Entities.Add(Out(false, "Capa no construida: " + why)); continue; }
                var runs = created.GetValueOrDefault(e.Layer);
                double total = segs.Sum(s => s.Length), covered = 0;
                string? what = null;
                foreach (var s in segs)
                {
                    var hit = Covering(runs, ToMm((s.Start + s.End) * 0.5), tol);
                    if (hit is null) continue;
                    covered += s.Length;
                    what ??= hit.What;
                    explained.Add(hit);
                }
                // (Una línea duplicada o solapada cuenta como modelada: su geometría está en un elemento creado.)
                if (total > 0 && covered >= 0.5 * total) { result.Entities.Add(Out(true, what ?? "Modelada")); continue; }
                // Curva que hace de codo: los dos tramos rectos se prolongan hasta su esquina.
                var cornerRuns = segs.Where(s => s.ArcCorner is not null).SelectMany(s => EndingAt(runs, ToMm(s.ArcCorner!.Value), tol)).ToList();
                if (cornerRuns.Count > 0)
                {
                    foreach (var r in cornerRuns) explained.Add(r);
                    result.Entities.Add(Out(true, "Curva convertida en esquina (codo)"));
                    continue;
                }
                var fail = segs.Select(s => Covering(failed.GetValueOrDefault(e.Layer), ToMm((s.Start + s.End) * 0.5), tol)).FirstOrDefault(f => f != null);
                if (fail != null) { result.Entities.Add(Out(false, "Revit no pudo crear el tramo: " + fail.What)); continue; }
                result.Entities.Add(Out(false, ReasonDiscarded));
                continue;
            }
            if (geometryTypes.Contains(type) && e.Depth == 0) { result.Entities.Add(Out(false, ReasonZero)); continue; }
            if (e.Depth > 0) { result.Entities.Add(Out(false, ReasonNested)); continue; }
            result.Entities.Add(Out(false, $"Tipo de entidad que no se convierte en elemento ({e.Type})"));
        }
        // Sentido contrario: un elemento creado está explicado si su punto medio cae sobre alguna línea de su capa
        // (a su medio ancho + tolerancia). Así se explican también los tramos de una línea larga partida por una te.
        var rawByLayer = d.Segments.Where(s => !input.SkippedLayers.ContainsKey(s.Layer))
            .GroupBy(s => s.Layer, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.Select(s => (A: ToMm(s.Start), B: ToMm(s.End))).ToList(), StringComparer.OrdinalIgnoreCase);
        foreach (var run in input.Created)
        {
            if (explained.Contains(run) || !rawByLayer.TryGetValue(run.Layer, out var raw)) continue;
            var mid = new P3((run.Start.X + run.End.X) / 2, (run.Start.Y + run.End.Y) / 2, 0);
            double reach = run.HalfWidthMm + tol;
            if (raw.Any(l => NetworkBuilder.ProjectToSegment(mid, l.A, l.B).Dist <= reach)) explained.Add(run);
        }
        // Tramos de cierre: la red cierra huecos de piezas especiales y curvas (hasta MaxClosingMm) entre tramos
        // dibujados; no tienen una línea propia debajo. Se aceptan si sus dos extremos tocan tramos explicados.
        var pending = input.Created.Where(r => !explained.Contains(r)).ToList();
        for (bool progress = true; progress && pending.Count > 0;)
        {
            progress = false;
            foreach (var run in pending.ToList())
            {
                var a = new P3(run.Start.X, run.Start.Y, 0);
                var b = new P3(run.End.X, run.End.Y, 0);
                if (a.DistanceTo(b) > MaxClosingMm) continue;
                var mates = created.GetValueOrDefault(run.Layer)?.Where(m => !ReferenceEquals(m, run) && explained.Contains(m)).ToList();
                bool Touches(P3 p) => mates != null && mates.Any(m =>
                    NetworkBuilder.ProjectToSegment(p, new P3(m.Start.X, m.Start.Y, 0), new P3(m.End.X, m.End.Y, 0)).Dist <= tol);
                if (!Touches(a) || !Touches(b)) continue;
                explained.Add(run);
                result.ClosingRuns.Add(run);
                pending.Remove(run);
                progress = true;
            }
        }
        result.UnexplainedRuns.AddRange(input.Created.Where(r => !explained.Contains(r)));
        var expected = d.Entities.Select(e => e.Index).ToHashSet();
        var got = result.Entities.GroupBy(x => x.Index).ToDictionary(g => g.Key, g => g.Count());
        result.MissingOrRepeated = expected.Count(i => got.GetValueOrDefault(i) != 1) + got.Keys.Count(i => !expected.Contains(i));
        result.Balanced = result.MissingOrRepeated == 0 && result.UnexplainedRuns.Count == 0 &&
                          result.Modeled + result.NotModeled == d.Entities.Count;
        return result;
    }

    private static Dictionary<string, List<ModeledRun>> Index(List<ModeledRun> runs) =>
        runs.GroupBy(r => r.Layer, StringComparer.OrdinalIgnoreCase).ToDictionary(g => g.Key, g => g.ToList(), StringComparer.OrdinalIgnoreCase);

    private static ModeledRun? Covering(List<ModeledRun>? runs, P3 p, double tol)
    {
        if (runs is null) return null;
        foreach (var r in runs)
        {
            double reach = r.HalfWidthMm + tol;
            if (p.X < Math.Min(r.Start.X, r.End.X) - reach || p.X > Math.Max(r.Start.X, r.End.X) + reach ||
                p.Y < Math.Min(r.Start.Y, r.End.Y) - reach || p.Y > Math.Max(r.Start.Y, r.End.Y) + reach) continue;
            var a = new P3(r.Start.X, r.Start.Y, 0);
            var b = new P3(r.End.X, r.End.Y, 0);
            if (NetworkBuilder.ProjectToSegment(p, a, b).Dist <= reach) return r;
        }
        return null;
    }

    private static IEnumerable<ModeledRun> EndingAt(List<ModeledRun>? runs, P3 corner, double tol) =>
        runs is null ? Enumerable.Empty<ModeledRun>() :
        runs.Where(r => new P3(r.Start.X, r.Start.Y, 0).DistanceTo(corner) <= tol ||
                        new P3(r.End.X, r.End.Y, 0).DistanceTo(corner) <= tol);
}
