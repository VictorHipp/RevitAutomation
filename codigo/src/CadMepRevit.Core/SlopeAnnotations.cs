using System.Globalization;
using System.Text.RegularExpressions;

namespace CadMepRevit.Core;

/// <summary>Todos los umbrales de lectura y resolución de pendientes, en mm o proporciones.</summary>
public static class SlopeRules
{
    public const double MinPercent = 0.1, MaxPercent = 15;
    public const double MaxDistanceMm = 1000, TextHeightFactor = 6, AmbiguityRatio = 1.5;
    public const double ParallelAngleDeg = 5, HeadLengthRatio = 0.6, ArrowJoinMm = 2;
    public const double LoopErrorMm = 10, ReferenceErrorMm = 20, Epsilon = 1e-6;
    public const double UnitlessMetersBelow = 50;
}

public sealed class SlopeLabel
{
    public CadText Text { get; init; } = new();
    public P3 PositionMm { get; init; }
    public double Ratio { get; init; }
    public int? Segment { get; set; }
    public P3 Downstream { get; set; }
    public string Reason { get; set; } = "";
    public bool IsolatedVoteDiscarded { get; set; }
}

public sealed record ElevationReference(int Segment, P3 PointMm, double AxisMm, string Description);

public sealed class SlopeAssociation
{
    public List<SlopeLabel> Labels { get; } = new();
    public List<ElevationReference> References { get; } = new();
    public List<string> Warnings { get; } = new();
    public int Associated => Labels.Count(l => l.Segment != null);
}

/// <summary>Lectura general por texto y geometría, sin nombres de capa ni valores particulares de planos.</summary>
public static class SlopeAnnotations
{
    private static readonly Regex Percent = new(@"(?<![\d.,])([+-]?\d+(?:[.,]\d+)?)\s*([%‰])", RegexOptions.Compiled);
    private static readonly Regex Elevation = new(@"(?:\bh\b|\bZ\b|cota\s*inferior|cota|generatriz\s*inferior|solera|fondo|C\.?\s*I\.?|C\.?\s*E\.?|\bCT\b)\s*[:=]?\s*([+-]?\d+(?:[.,]\d+)?)\s*(mm|m)?", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static double Number(string value) => double.Parse(value.Replace(',', '.'), CultureInfo.InvariantCulture);
    public static double? ParseRatio(string text)
    {
        var m = Percent.Match(text);
        return m.Success ? Number(m.Groups[1].Value) / (m.Groups[2].Value == "‰" ? 1000 : 100) : null;
    }

    public static List<CadArrow> FindArrows(CadDrawing drawing, double k)
    {
        var result = new List<CadArrow>(drawing.Arrows);
        var edges = drawing.Segments.Concat(drawing.AnnotationGeometry).Where(e => e.Length > SlopeRules.Epsilon).ToList();
        foreach (var group in edges.GroupBy(e => e.Layer, StringComparer.OrdinalIgnoreCase))
        {
            // Índice espacial de extremos: no se compara cada línea del plano con todas las demás.
            double cell = SlopeRules.ArrowJoinMm / k;
            var grid = new Dictionary<(long, long), List<(P3 P, P3 Other, double Length)>>();
            (long, long) Key(P3 p) => ((long)Math.Floor(p.X / cell), (long)Math.Floor(p.Y / cell));
            foreach (var e in group)
                foreach (var pair in new[] { (e.Start, e.End), (e.End, e.Start) })
                {
                    var key = Key(pair.Item1);
                    if (!grid.TryGetValue(key, out var list)) grid[key] = list = new();
                    list.Add((pair.Item1, pair.Item2, e.Length));
                }
            foreach (var shaft in group)
                foreach (var pair in new[] { (Tip: shaft.Start, Tail: shaft.End), (Tip: shaft.End, Tail: shaft.Start) })
                {
                    var u = (pair.Tip - pair.Tail).Normalized();
                    var wings = new List<P3>();
                    var (gx, gy) = Key(pair.Tip);
                    for (long dx = -1; dx <= 1; dx++)
                        for (long dy = -1; dy <= 1; dy++)
                            if (grid.TryGetValue((gx + dx, gy + dy), out var near))
                                foreach (var e in near)
                                {
                                    if (e.P.DistanceTo(pair.Tip) > cell || e.Length >= shaft.Length * SlopeRules.HeadLengthRatio) continue;
                                    var v = e.Other - pair.Tip;
                                    if (v.Dot(u) < -SlopeRules.Epsilon) wings.Add(v);
                                }
                    bool left = wings.Any(v => u.X * v.Y - u.Y * v.X > SlopeRules.Epsilon);
                    bool right = wings.Any(v => u.X * v.Y - u.Y * v.X < -SlopeRules.Epsilon);
                    if (left && right) result.Add(new CadArrow(shaft.Layer, pair.Tail, pair.Tip, "trazo y punta geométrica"));
                }
        }
        var unique = new List<CadArrow>();
        foreach (var a in result.OrderByDescending(a => a.Tail.DistanceTo(a.Tip)))
            if (!unique.Any(b => b.Layer.Equals(a.Layer, StringComparison.OrdinalIgnoreCase) &&
                b.Tip.DistanceTo(a.Tip) * k <= SlopeRules.ArrowJoinMm &&
                (b.Tip - b.Tail).Normalized().Dot((a.Tip - a.Tail).Normalized()) > Math.Cos(SlopeRules.ParallelAngleDeg * Math.PI / 180))) unique.Add(a);
        return unique;
    }

    public static SlopeAssociation Associate(CadDrawing drawing, IReadOnlyList<NetSegment> pipes, double k,
        P3 offset, double toleranceMm, Func<NetSegment, double>? outerDiameterMm = null)
    {
        var result = new SlopeAssociation();
        if (pipes.Count == 0) return result;
        P3 Map(P3 p) => new P3(p.X * k + offset.X, p.Y * k + offset.Y, 0);
        var layers = pipes.Select(p => p.Layer).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var arrows = FindArrows(drawing, k);
        var targets = pipes.Select(p => new SizeTarget { Id = p.Index.ToString(CultureInfo.InvariantCulture), Layer = p.Layer,
            Start = p.Start, End = p.End, Shape = SizeShape.Redonda }).ToList();
        var runs = SizeAnnotations.Runs(targets, toleranceMm, out _);
        foreach (var tx in drawing.Texts)
        {
            var ratio = ParseRatio(tx.Value);
            if (ratio == null) continue;
            var pos = Map(tx.Center);
            var label = new SlopeLabel { Text = tx, Ratio = ratio.Value, PositionMm = pos };
            result.Labels.Add(label);
            if (ratio.Value * 100 < SlopeRules.MinPercent || ratio.Value * 100 > SlopeRules.MaxPercent)
            { label.Reason = $"fuera de rango ({SlopeRules.MinPercent}–{SlopeRules.MaxPercent} %)"; continue; }
            double max = Math.Max(SlopeRules.MaxDistanceMm, SlopeRules.TextHeightFactor * Math.Abs(tx.Height) * k);
            var ranked = arrows.Where(a => !layers.Contains(a.Layer) || a.Layer.Equals(tx.Layer, StringComparison.OrdinalIgnoreCase))
                .Select(a => (Arrow: a, A: Map(a.Tail), B: Map(a.Tip)))
                .Select(a => (a.Arrow, a.A, a.B, D: NetworkBuilder.ProjectToSegment(pos, a.A, a.B).Dist))
                .Where(a => a.D <= max).OrderBy(a => a.D).ToList();
            // Una flecha reservada por un rótulo claramente más cercano no compite con el rótulo vecino.
            ranked.RemoveAll(a => drawing.Texts.Any(other => !ReferenceEquals(other, tx) && ParseRatio(other.Value) is double r &&
                r * 100 >= SlopeRules.MinPercent && r * 100 <= SlopeRules.MaxPercent &&
                NetworkBuilder.ProjectToSegment(Map(other.Center), a.A, a.B).Dist * SlopeRules.AmbiguityRatio + toleranceMm < a.D));
            if (ranked.Count == 0) { label.Reason = "sin flecha asociada; se mantiene horizontal"; continue; }
            if (ranked.Count > 1)
            {
                var textDir = new P3(Math.Cos(tx.RotationRad), Math.Sin(tx.RotationRad), 0);
                double band = Math.Max(ranked[0].D * SlopeRules.AmbiguityRatio, ranked[0].D + toleranceMm);
                var aligned = ranked.Where(a => a.D <= band && Math.Abs((a.B - a.A).Normalized().Dot(textDir)) >
                    Math.Cos(SlopeRules.ParallelAngleDeg * Math.PI / 180)).ToList();
                if (aligned.Count == 1) ranked = aligned;
            }
            if (ranked.Count > 1 && ranked[1].D < Math.Max(ranked[0].D * SlopeRules.AmbiguityRatio, ranked[0].D + toleranceMm))
            {
                // Etiquetas próximas en tuberías paralelas: resolver primero las parejas inequívocas del grupo.
                // No se roba la flecha que otro texto identifica con separación suficiente de su segunda opción.
                var reserved = new HashSet<CadArrow>();
                foreach (var other in drawing.Texts.Where(t => !ReferenceEquals(t, tx) && ParseRatio(t.Value) is double r &&
                    r * 100 >= SlopeRules.MinPercent && r * 100 <= SlopeRules.MaxPercent))
                {
                    var otherPos = Map(other.Center);
                    double otherMax = Math.Max(SlopeRules.MaxDistanceMm, SlopeRules.TextHeightFactor * Math.Abs(other.Height) * k);
                    var choices = arrows.Where(ar => !layers.Contains(ar.Layer) || ar.Layer.Equals(other.Layer, StringComparison.OrdinalIgnoreCase))
                        .Select(ar => (Arrow: ar, D: NetworkBuilder.ProjectToSegment(otherPos, Map(ar.Tail), Map(ar.Tip)).Dist))
                        .Where(ar => ar.D <= otherMax).OrderBy(ar => ar.D).ToList();
                    if (choices.Count > 0 && (choices.Count == 1 || choices[1].D >= Math.Max(choices[0].D * SlopeRules.AmbiguityRatio, choices[0].D + toleranceMm)))
                        reserved.Add(choices[0].Arrow);
                }
                var remaining = ranked.Where(ar => !reserved.Contains(ar.Arrow)).ToList();
                if (remaining.Count > 0) ranked = remaining;
            }
            if (ranked.Count > 1 && ranked[1].D < Math.Max(ranked[0].D * SlopeRules.AmbiguityRatio, ranked[0].D + toleranceMm))
            { label.Reason = $"dos flechas casi a la misma distancia ({ranked[0].D:0.#}/{ranked[1].D:0.#} mm, puntas {ranked[0].B}/{ranked[1].B}); se mantiene horizontal"; continue; }
            var arrow = ranked[0];
            var dir = (arrow.B - arrow.A).Normalized();
            var mid = (arrow.A + arrow.B) * 0.5;
            var pool = pipes.Select((p, i) => (P: p, I: i, D: NetworkBuilder.ProjectToSegment(mid, p.Start, p.End).Dist))
                .Where(f => Math.Abs(f.P.Direction.Dot(dir)) > Math.Cos(SlopeRules.ParallelAngleDeg * Math.PI / 180) &&
                    f.D <= SlopeRules.MaxDistanceMm &&
                    Math.Max((arrow.A - f.P.Start).Dot(f.P.Direction), (arrow.B - f.P.Start).Dot(f.P.Direction)) > 0 &&
                    Math.Min((arrow.A - f.P.Start).Dot(f.P.Direction), (arrow.B - f.P.Start).Dot(f.P.Direction)) < f.P.Length)
                .GroupBy(f => runs[f.I]).Select(g => g.OrderBy(f => f.D).First()).OrderBy(f => f.D).ToList();
            // Una flecha puede solaparse con varios tramos consecutivos: el texto sitúa su propio tramo.
            var underText = pool.Where(f => (pos - f.P.Start).Dot(f.P.Direction) >= -toleranceMm &&
                (pos - f.P.Start).Dot(f.P.Direction) <= f.P.Length + toleranceMm).ToList();
            if (underText.Count > 0) pool = underText;
            if (pool.Count > 1 && pool[1].D < Math.Max(pool[0].D * SlopeRules.AmbiguityRatio, pool[0].D + toleranceMm))
            {
                var byText = pool.Select(f => (F: f, D: NetworkBuilder.ProjectToSegment(pos, f.P.Start, f.P.End).Dist)).OrderBy(f => f.D).ToList();
                if (byText[1].D >= Math.Max(byText[0].D * SlopeRules.AmbiguityRatio, byText[0].D + toleranceMm)) pool = new() { byText[0].F };
            }
            // Una directriz puede señalar directamente el tramo aunque no sea paralela a él.
            if (tx.Anchor is P3 anchor && arrow.Arrow.Source is "LEADER" or "MLEADER")
            {
                var at = Map(anchor);
                var pointed = pipes.Select((p, i) => (P: p, I: i, D: NetworkBuilder.ProjectToSegment(at, p.Start, p.End).Dist))
                    .Where(f => f.D <= toleranceMm).GroupBy(f => runs[f.I]).Select(g => g.OrderBy(f => f.D).First()).OrderBy(f => f.D).ToList();
                if (pointed.Count > 0) pool = pointed;
                if (pointed.Count == 1) dir = pool[0].P.Direction * (pool[0].P.Direction.Dot(arrow.B - arrow.A) >= 0 ? 1 : -1);
            }
            if (pool.Count == 0) { label.Reason = "flecha sin tubería paralela y solapada a menos de 1000 mm"; continue; }
            if (pool.Count > 1 && pool[1].D < Math.Max(pool[0].D * SlopeRules.AmbiguityRatio, pool[0].D + toleranceMm))
            { label.Reason = $"dos tuberías casi a la misma distancia ({pool[0].D:0.#}/{pool[1].D:0.#} mm, tramos {pool[0].P.Index}/{pool[1].P.Index}, capas '{pool[0].P.Layer}'/'{pool[1].P.Layer}'); se mantiene horizontal"; continue; }
            label.Segment = pool[0].P.Index;
            label.Downstream = dir;
        }
        foreach (var tx in drawing.Texts)
        {
            var m = Elevation.Match(tx.Value);
            if (!m.Success) continue;
            var point = Map(tx.Anchor ?? tx.Center);
            var pool = pipes.Select(p => (P: p, Hit: NetworkBuilder.ProjectToSegment(point, p.Start, p.End)))
                .Where(f => f.Hit.Dist <= SlopeRules.MaxDistanceMm).OrderBy(f => f.Hit.Dist).ToList();
            if (pool.Count == 0) continue;
            if (pool.Count > 1 && pool[1].Hit.Dist < Math.Max(pool[0].Hit.Dist * SlopeRules.AmbiguityRatio, pool[0].Hit.Dist + toleranceMm))
            { result.Warnings.Add($"Cota «{tx.Value}» en {point}: dos tuberías próximas; indique una directriz."); continue; }
            double n = Number(m.Groups[1].Value);
            string unit = m.Groups[2].Value.ToLowerInvariant();
            bool meters = unit == "m" || unit == "" && Math.Abs(n) < SlopeRules.UnitlessMetersBelow;
            double axis = n * (meters ? 1000 : 1);
            bool lower = Regex.IsMatch(m.Value, @"inferior|solera|fondo|C\.?\s*I\.?", RegexOptions.IgnoreCase);
            if (lower) axis += (outerDiameterMm?.Invoke(pool[0].P) ?? pool[0].P.WidthMm ?? PlanDefaults.PipeDiameterMm) / 2;
            var p = pool[0].P;
            double t = Math.Clamp((point - p.Start).Dot(p.Direction) / p.Length, 0, 1);
            result.References.Add(new ElevationReference(p.Index, p.Start + (p.End - p.Start) * t, axis,
                $"cota del plano «{tx.Value}»" + (lower ? "; inferior + radio exterior" : "; eje") +
                (unit == "" ? $"; unidad inferida: {(meters ? "m" : "mm")}" : "")));
        }
        return result;
    }
}
