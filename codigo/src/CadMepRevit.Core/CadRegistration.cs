namespace CadMepRevit.Core;

/// <summary>
/// Encaje del dibujo leído sobre la geometría de ese mismo CAD tal y como está colocado en el proyecto (vinculado o
/// importado, movido, girado o con otra unidad). No se fía de la caja que da Revit (incluye textos, sombreados y
/// restos lejanos, y no existe si el CAD está solo en una vista): compara PUNTOS. Los extremos de las líneas rectas
/// y los centros de círculos e inserciones del dibujo, llevados al proyecto, se comparan con los puntos del CAD en Revit.
/// Todo en una misma unidad (en Revit, pies) y en planta (X, Y).
/// </summary>
public sealed class CadRegistration
{
    /// <summary>Puntos del archivo, sin los bloques equivalentes creados para luminarias dibujadas con líneas.</summary>
    public static (List<(double X, double Y)> Lines, List<(double X, double Y)> Blocks) DrawingPoints(
        CadDrawing drawing, double scale, double cell)
    {
        var geometry = drawing.Segments.Where(s => s.ArcId == 0).SelectMany(s => new[] { s.Start, s.End })
            .Concat(drawing.Circles.Where(c => c.IsPlanar).Select(c => c.Center));
        // CadReader solo incluye INSERT del espacio modelo en Blocks; LooseFixtures añade índices negativos.
        var inserts = drawing.Blocks.Where(b => b.EntityIndex >= 0).Select(b => b.Position);
        List<(double X, double Y)> DistinctPoints(IEnumerable<P3> points)
        {
            var seen = new HashSet<(long, long)>();
            return points.Select(p => (X: p.X * scale, Y: p.Y * scale))
                .Where(p => double.IsFinite(p.X) && double.IsFinite(p.Y) &&
                            seen.Add(((long)Math.Floor(p.X / cell), (long)Math.Floor(p.Y / cell))))
                .ToList();
        }
        return (DistinctPoints(geometry), DistinctPoints(inserts));
    }

    private readonly double _cell;
    private readonly Dictionary<(long, long), (double X, double Y)> _grid = new();
    private readonly List<(double X, double Y)> _points = new();

    /// <param name="cadPoints">Vértices del CAD en el proyecto.</param>
    /// <param name="cell">Tolerancia: los puntos casan a menos de 1 a 2 celdas.</param>
    public CadRegistration(IEnumerable<(double X, double Y)> cadPoints, double cell)
    {
        _cell = cell;
        foreach (var p in cadPoints)
        {
            if (!double.IsFinite(p.X) || !double.IsFinite(p.Y)) continue;
            if (_grid.TryAdd(Key(p.X, p.Y), p)) _points.Add(p);
        }
    }

    /// <summary>Vértices distintos del CAD (uno por celda).</summary>
    public int Count => _points.Count;
    public IReadOnlyList<(double X, double Y)> Points => _points;

    private (long, long) Key(double x, double y) => ((long)Math.Floor(x / _cell), (long)Math.Floor(y / _cell));

    public bool Hit(double x, double y)
    {
        var (cx, cy) = Key(x, y);
        for (long dx = -1; dx <= 1; dx++)
            for (long dy = -1; dy <= 1; dy++)
                if (_grid.ContainsKey((cx + dx, cy + dy))) return true;
        return false;
    }

    private (double X, double Y)? Near(double x, double y)
    {
        var (cx, cy) = Key(x, y);
        (double X, double Y)? best = null;
        double bd = double.MaxValue;
        for (long dx = -1; dx <= 1; dx++)
            for (long dy = -1; dy <= 1; dy++)
                if (_grid.TryGetValue((cx + dx, cy + dy), out var p))
                {
                    double d = (p.X - x) * (p.X - x) + (p.Y - y) * (p.Y - y);
                    if (d < bd) { bd = d; best = p; }
                }
        return best;
    }

    /// <summary>Hasta <paramref name="count"/> puntos repartidos por la lista (siempre los mismos: el resultado no varía).</summary>
    public static List<(double X, double Y)> Spread(IReadOnlyList<(double X, double Y)> pts, int count, double phase = 0)
    {
        var list = new List<(double X, double Y)>();
        if (pts.Count == 0 || count <= 0) return list;
        if (pts.Count <= count) return pts.ToList();
        for (int i = 0; i < count; i++)
            list.Add(pts[Math.Min(pts.Count - 1, (int)((i + phase) * pts.Count / count))]);
        return list;
    }

    /// <summary>Cuántos de una muestra de hasta 300 puntos, desplazados (dx, dy), caen sobre el CAD.</summary>
    public (int Hits, int Tried) Check(IReadOnlyList<(double X, double Y)> pts, double dx, double dy)
    {
        int hits = 0;
        var sample = Spread(pts, 300);
        foreach (var p in sample) if (Hit(p.X + dx, p.Y + dy)) hits++;
        return (hits, sample.Count);
    }

    /// <summary>
    /// Desplazamiento que lleva los puntos del dibujo (ya girados y escalados como el CAD) sobre el CAD: cada punto
    /// «semilla» del dibujo se prueba sobre cada vértice del CAD, y el desplazamiento se puntúa con cuántos puntos de
    /// control caen también sobre el CAD. Los mejores se comprueban con 300 puntos y se afinan. Null si nada encaja.
    /// </summary>
    public (double Dx, double Dy, int Hits, int Tried)? FindOffset(IReadOnlyList<(double X, double Y)> pts, int anchorCount = 40, int seedCount = 12)
    {
        if (pts.Count < 3 || _points.Count == 0) return null;
        var anchors = Spread(pts, Math.Min(anchorCount, pts.Count));
        var seeds = Spread(pts, Math.Min(seedCount, pts.Count), 0.5);
        int n = anchors.Count;
        int need = Math.Max(3, (int)Math.Ceiling(0.3 * n));
        int probe = Math.Min(8, n), probeNeed = n >= 8 ? 2 : 1;
        // Los mejores candidatos (distintos entre sí): en dibujos repetitivos, uno desplazado un módulo también casa a medias.
        var top = new List<(int Hits, double Dx, double Dy)>();
        int floor = need - 1;
        foreach (var s in seeds)
        {
            foreach (var v in _points)
            {
                double dx = v.X - s.X, dy = v.Y - s.Y;
                int hits = 0;
                for (int i = 0; i < n; i++)
                {
                    if (Hit(anchors[i].X + dx, anchors[i].Y + dy)) hits++;
                    if (i == probe - 1 && hits < probeNeed) break;
                    if (hits + (n - 1 - i) <= floor) break;
                }
                if (hits <= floor) continue;
                int same = top.FindIndex(c => Math.Abs(c.Dx - dx) < 3 * _cell && Math.Abs(c.Dy - dy) < 3 * _cell);
                if (same >= 0) { if (top[same].Hits < hits) top[same] = (hits, dx, dy); }
                else top.Add((hits, dx, dy));
                top = top.OrderByDescending(c => c.Hits).Take(4).ToList();
                if (top.Count == 4) floor = Math.Max(need - 1, top[^1].Hits);
            }
            if (top.Count > 0 && top[0].Hits >= 0.7 * n) break;
        }
        (double Dx, double Dy, int Hits, int Tried)? best = null;
        foreach (var c in top)
        {
            var (rdx, rdy) = Refine(pts, c.Dx, c.Dy);
            var (h, t) = Check(pts, rdx, rdy);
            if (best is null || h > best.Value.Hits) best = (rdx, rdy, h, t);
        }
        return best;
    }

    /// <summary>Afina el desplazamiento con la media de las diferencias a los vértices más cercanos.</summary>
    public (double Dx, double Dy) Refine(IReadOnlyList<(double X, double Y)> pts, double dx, double dy)
    {
        var sample = Spread(pts, 300);
        for (int iter = 0; iter < 3; iter++)
        {
            double sx = 0, sy = 0; int k = 0;
            foreach (var p in sample)
            {
                double x = p.X + dx, y = p.Y + dy;
                if (Near(x, y) is not { } q) continue;
                sx += q.X - x; sy += q.Y - y; k++;
            }
            if (k == 0) break;
            dx += sx / k; dy += sy / k;
        }
        return (dx, dy);
    }

    /// <summary>Contorno «robusto» (del 2 % al 98 % de los puntos, sin restos lejanos): centro y lado mayor.</summary>
    public static (double CenterX, double CenterY, double Size)? Extent(IEnumerable<(double X, double Y)> points)
    {
        var xs = new List<double>(); var ys = new List<double>();
        foreach (var p in points) { if (!double.IsFinite(p.X) || !double.IsFinite(p.Y)) continue; xs.Add(p.X); ys.Add(p.Y); }
        if (xs.Count == 0) return null;
        xs.Sort(); ys.Sort();
        double Q(List<double> l, double q) => l[(int)Math.Round(q * (l.Count - 1))];
        double x0 = Q(xs, .02), x1 = Q(xs, .98), y0 = Q(ys, .02), y1 = Q(ys, .98);
        return ((x0 + x1) / 2, (y0 + y1) / 2, Math.Max(x1 - x0, y1 - y0));
    }

    /// <summary>Cambios de unidad posibles entre cómo se leyó el plano y cómo se vinculó en Revit.</summary>
    public static readonly double[] UnitRatios = { 1, 10, 100, 1000, 25.4, 304.8, 2.54, 30.48, 12, 0.1, 0.01, 0.001, 1 / 25.4, 1 / 304.8, 1 / 2.54, 1 / 30.48, 1 / 12.0 };

    /// <summary>Factor de unidades que explica la proporción de tamaños (dibujo / CAD), o 1 si ninguno se acerca (±15 %).</summary>
    public static double UnitFactor(double ratio)
    {
        if (!(ratio > 0) || !double.IsFinite(ratio)) return 1;
        double f = UnitRatios.OrderBy(r => Math.Abs(Math.Log(ratio / r))).First();
        return Math.Abs(ratio / f - 1) < 0.15 ? f : 1;
    }
}
