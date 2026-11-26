namespace CadMepRevit.Core;

/// <summary>
/// Trozo horizontal de un tramo que sigue el techo: desde y hasta (mm medidos a lo largo del tramo del plano, desde su
/// inicio) y cota del eje (mm sobre el nivel).
/// </summary>
public sealed record CeilingPiece(double FromMm, double ToMm, double AxisMm)
{
    public double LengthMm => ToMm - FromMm;
}

/// <summary>Datos de un tramo recto del plano para que siga el techo.</summary>
public sealed class CeilingSegmentInput
{
    public int Segment { get; init; }
    public double LengthMm { get; init; }
    /// <summary>
    /// Muestras a lo largo del tramo: posición (mm desde el inicio) y cota MÁXIMA del eje (mm sobre el nivel) para que la
    /// parte de arriba del tramo quede a la holgura pedida bajo el forjado. Null = no hay forjado encima de ese punto.
    /// </summary>
    public List<(double AlongMm, double? MaxAxisMm)> Samples { get; init; } = new();
    /// <summary>Subida o bajada mínima que justifica dos codos; las diferencias menores se resuelven yendo por la cota más baja.</summary>
    public double MinRiseMm { get; init; } = 200;
    /// <summary>Longitud horizontal mínima de cada trozo (para que quepan los codos); los más cortos van por la cota más baja.</summary>
    public double MinPieceMm { get; init; } = 500;
    /// <summary>Lo que se aparta cada subida o bajada hacia la zona alta, para que el codo no toque el forjado más bajo.</summary>
    public double StepMarginMm { get; init; } = 100;
}

public sealed class CeilingRouteResult
{
    /// <summary>Trozos de cada tramo, en orden desde su inicio. Un solo trozo = tramo recto a una cota.</summary>
    public Dictionary<int, List<CeilingPiece>> Pieces { get; } = new();
    /// <summary>Tramos sin forjado encima en ninguna muestra: toman la cota de los tramos con los que se unen.</summary>
    public HashSet<int> WithoutSlab { get; } = new();
    /// <summary>Tramos de una red sin ningún forjado encima: quedan a la altura supuesta.</summary>
    public HashSet<int> Fallback { get; } = new();
    /// <summary>Extremos bajados para encontrarse a la misma cota con los demás tramos de su unión.</summary>
    public int EndsLowered { get; set; }
    /// <summary>Subidas o bajadas (cada una con dos codos) previstas.</summary>
    public int Steps => Pieces.Values.Sum(p => Math.Max(0, p.Count - 1));
}

/// <summary>
/// Recorrido vertical de tramos colgados que siguen el techo (sin Revit, para poder probarlo).
/// La planta no cambia: solo la cota. Donde el forjado sube o baja, el tramo se parte y sube o baja con dos codos;
/// nunca queda por encima de la cota máxima de ninguna muestra. En cada unión todos los extremos quedan a la misma cota
/// (la más baja de ellos), para que encajen codos, tes y cruces.
/// </summary>
public static class CeilingProfile
{
    /// <summary>Trozos de un tramo según sus muestras. Null si ninguna muestra tiene forjado encima.</summary>
    public static List<CeilingPiece>? Profile(CeilingSegmentInput s)
    {
        var samples = s.Samples.Where(x => x.AlongMm >= -1e-6 && x.AlongMm <= s.LengthMm + 1e-6)
            .OrderBy(x => x.AlongMm).ToList();
        if (samples.Count == 0 || samples.All(x => x.MaxAxisMm is null)) return null;
        // Donde no hay forjado no hay límite: vale lo del punto con forjado más próximo.
        var values = new double[samples.Count];
        for (int i = 0; i < samples.Count; i++)
        {
            if (samples[i].MaxAxisMm is { } v) { values[i] = v; continue; }
            double best = double.MaxValue, near = 0;
            for (int j = 0; j < samples.Count; j++)
            {
                if (samples[j].MaxAxisMm is not { } w) continue;
                double d = Math.Abs(samples[j].AlongMm - samples[i].AlongMm);
                if (d < best || d == best && w < near) { best = d; near = w; }
            }
            values[i] = near;
        }
        var pieces = new List<CeilingPiece>();
        double from = 0, z = values[0];
        for (int i = 1; i < samples.Count; i++)
        {
            if (Math.Abs(values[i] - z) < s.MinRiseMm) { z = Math.Min(z, values[i]); continue; }
            // El trozo bajo se alarga sobre el tramo entre las dos muestras (no se sabe dónde está el escalón) y un margen
            // más, para que la subida y su codo queden ya bajo el forjado alto.
            bool up = values[i] > z;
            double at = up ? samples[i].AlongMm + s.StepMarginMm : samples[i - 1].AlongMm - s.StepMarginMm;
            at = Math.Clamp(at, from, s.LengthMm);
            pieces.Add(new CeilingPiece(from, at, z));
            from = at;
            z = values[i];
        }
        pieces.Add(new CeilingPiece(from, s.LengthMm, z));
        return Simplify(pieces, s);
    }

    /// <summary>
    /// Une los trozos demasiado cortos con el vecino más bajo y los escalones demasiado pequeños, siempre por la cota más
    /// baja: así nunca se sube por encima de lo permitido.
    /// </summary>
    private static List<CeilingPiece> Simplify(List<CeilingPiece> pieces, CeilingSegmentInput s)
    {
        var list = pieces.Where(p => p.LengthMm > 1e-6 || pieces.Count == 1).ToList();
        if (list.Count == 0) list.Add(new CeilingPiece(0, s.LengthMm, pieces.Min(p => p.AxisMm)));
        bool changed = true;
        while (changed && list.Count > 1)
        {
            changed = false;
            for (int i = 0; i + 1 < list.Count; i++)
            {
                if (Math.Abs(list[i].AxisMm - list[i + 1].AxisMm) >= s.MinRiseMm) continue;
                list[i] = new CeilingPiece(list[i].FromMm, list[i + 1].ToMm, Math.Min(list[i].AxisMm, list[i + 1].AxisMm));
                list.RemoveAt(i + 1);
                changed = true;
                break;
            }
            if (changed) continue;
            int shortest = -1;
            for (int i = 0; i < list.Count; i++)
                if (list[i].LengthMm < s.MinPieceMm && (shortest < 0 || list[i].LengthMm < list[shortest].LengthMm)) shortest = i;
            if (shortest < 0) break;
            // Zona baja corta (una viga, un escalón estrecho): se ensancha a costa de los vecinos, más altos, hasta que
            // quepan los codos; así baja solo ahí y vuelve a subir. Si los vecinos son cortos, se une con el más bajo.
            var low = list[shortest];
            bool hasLeft = shortest > 0, hasRight = shortest < list.Count - 1;
            if ((!hasLeft || list[shortest - 1].AxisMm > low.AxisMm) && (!hasRight || list[shortest + 1].AxisMm > low.AxisMm))
            {
                double need = s.MinPieceMm - low.LengthMm;
                double takeLeft = hasLeft ? (hasRight ? need / 2 : need) : 0, takeRight = need - takeLeft;
                bool leftOk = !hasLeft || list[shortest - 1].LengthMm - takeLeft >= s.MinPieceMm;
                bool rightOk = !hasRight || list[shortest + 1].LengthMm - takeRight >= s.MinPieceMm;
                if (!leftOk && hasRight && list[shortest + 1].LengthMm - need >= s.MinPieceMm) { takeLeft = 0; takeRight = need; leftOk = rightOk = true; }
                if (!rightOk && hasLeft && list[shortest - 1].LengthMm - need >= s.MinPieceMm) { takeLeft = need; takeRight = 0; leftOk = rightOk = true; }
                if (leftOk && rightOk)
                {
                    double from = low.FromMm - takeLeft, to = low.ToMm + takeRight;
                    if (hasLeft) list[shortest - 1] = list[shortest - 1] with { ToMm = from };
                    if (hasRight) list[shortest + 1] = list[shortest + 1] with { FromMm = to };
                    list[shortest] = new CeilingPiece(from, to, low.AxisMm);
                    changed = true;
                    continue;
                }
            }
            int other = shortest == 0 ? 1 : shortest == list.Count - 1 ? shortest - 1
                : list[shortest - 1].AxisMm <= list[shortest + 1].AxisMm ? shortest - 1 : shortest + 1;
            int a = Math.Min(shortest, other), b = Math.Max(shortest, other);
            list[a] = new CeilingPiece(list[a].FromMm, list[b].ToMm, Math.Min(list[a].AxisMm, list[b].AxisMm));
            list.RemoveAt(b);
            changed = true;
        }
        return list;
    }

    /// <summary>
    /// Recorrido de todos los tramos. <paramref name="nodes"/>: los extremos que se unen en cada unión (codo, te, cruz,
    /// transición o tramos en línea). Sin forjado en toda la red, los tramos quedan a <paramref name="fallbackAxisMm"/>.
    /// </summary>
    public static CeilingRouteResult Solve(IReadOnlyList<CeilingSegmentInput> segments,
        IEnumerable<IReadOnlyList<(int Segment, bool AtStart)>> nodes, double fallbackAxisMm)
    {
        var result = new CeilingRouteResult();
        var inputs = segments.ToDictionary(s => s.Segment);
        var pieces = new Dictionary<int, List<CeilingPiece>?>();
        foreach (var s in segments) pieces[s.Segment] = Profile(s);
        var nodeList = nodes.Select(n => n.Where(e => inputs.ContainsKey(e.Segment)).ToList()).Where(n => n.Count > 1).ToList();
        double EndZ(int seg, bool atStart) => atStart ? pieces[seg]![0].AxisMm : pieces[seg]![^1].AxisMm;

        for (int round = 0; round < 500; round++)
        {
            bool changed = false;
            foreach (var node in nodeList)
            {
                var known = node.Where(e => pieces[e.Segment] != null).ToList();
                if (known.Count == 0) continue;
                double target = known.Min(e => EndZ(e.Segment, e.AtStart));
                foreach (var (seg, atStart) in known)
                {
                    if (EndZ(seg, atStart) <= target + 0.5) continue;
                    pieces[seg] = LowerEnd(pieces[seg]!, inputs[seg], atStart, target);
                    result.EndsLowered++;
                    changed = true;
                }
            }
            // Los tramos sin forjado encima toman la cota de los tramos con los que se unen.
            foreach (var s in segments)
            {
                if (pieces[s.Segment] != null) continue;
                var near = nodeList.Where(n => n.Any(e => e.Segment == s.Segment))
                    .SelectMany(n => n).Where(e => e.Segment != s.Segment && pieces[e.Segment] != null)
                    .Select(e => EndZ(e.Segment, e.AtStart)).ToList();
                if (near.Count == 0) continue;
                pieces[s.Segment] = new List<CeilingPiece> { new(0, s.LengthMm, near.Min()) };
                result.WithoutSlab.Add(s.Segment);
                changed = true;
            }
            if (!changed) break;
        }
        foreach (var s in segments)
        {
            if (pieces[s.Segment] is { } list) { result.Pieces[s.Segment] = list; continue; }
            result.Pieces[s.Segment] = new List<CeilingPiece> { new(0, s.LengthMm, fallbackAxisMm) };
            result.Fallback.Add(s.Segment);
        }
        return result;
    }

    /// <summary>
    /// Baja un extremo a la cota de su unión: si el trozo del extremo es largo y el salto merece codos, solo baja el
    /// principio del trozo (con una bajada); si no, baja el trozo entero.
    /// </summary>
    private static List<CeilingPiece> LowerEnd(List<CeilingPiece> list, CeilingSegmentInput s, bool atStart, double target)
    {
        var result = list.ToList();
        int i = atStart ? 0 : result.Count - 1;
        var p = result[i];
        if (p.LengthMm >= 2 * s.MinPieceMm && p.AxisMm - target >= s.MinRiseMm)
        {
            if (atStart)
            {
                result[0] = new CeilingPiece(p.FromMm, p.FromMm + s.MinPieceMm, target);
                result.Insert(1, new CeilingPiece(p.FromMm + s.MinPieceMm, p.ToMm, p.AxisMm));
            }
            else
            {
                result[i] = new CeilingPiece(p.FromMm, p.ToMm - s.MinPieceMm, p.AxisMm);
                result.Add(new CeilingPiece(p.ToMm - s.MinPieceMm, p.ToMm, target));
            }
            return result;
        }
        result[i] = p with { AxisMm = target };
        return Simplify(result, s);
    }
}
