namespace CadMepRevit.Core;

/// <summary>Tramo recto ya limpio, en milímetros, listo para crearse en Revit.</summary>
public sealed class NetSegment
{
    public int Index { get; internal set; }
    public string Layer { get; init; } = "";
    public P3 Start { get; internal set; }
    public P3 End { get; internal set; }
    public double Length => Start.DistanceTo(End);
    public P3 Direction => (End - Start).Normalized();
    /// <summary>Ancho/diámetro (mm) medido en el CAD (dibujo a doble línea); null = usar el de la capa.</summary>
    public double? WidthMm { get; init; }
    /// <summary>Curva del CAD de la que este tramo es una cuerda (0 = tramo recto). Ver <see cref="CadSegment.ArcId"/>.</summary>
    internal int ArcId { get; init; }
    /// <summary>Esquina de esa curva, en mm. Ver <see cref="CadSegment.ArcCorner"/>.</summary>
    internal P3? ArcCorner { get; init; }
}

public enum JunctionKind
{
    Codo,
    Te,
    Cruz,
    NoAdmitida,
    /// <summary>Dos tramos alineados de distinto tamaño.</summary>
    Transicion,
    EnLinea
}

/// <summary>Punto donde se tocan extremos de tramos de la misma capa.</summary>
public sealed class Junction
{
    public P3 Point { get; init; }
    public string Layer { get; init; } = "";
    /// <summary>(índice del tramo, true si es su extremo inicial).</summary>
    public List<(int Segment, bool AtStart)> Ends { get; } = new();
    public JunctionKind Kind { get; internal set; }
    /// <summary>Para te/cruz: los tramos ordenados (los dos colineales primero).</summary>
    public List<(int Segment, bool AtStart)> Ordered { get; internal set; } = new();
    public string Note { get; internal set; } = "";

    public void MarkUnavailableTransition(string reason)
    {
        Kind = JunctionKind.Transicion;
        Ordered = Ends.ToList();
        Note = reason;
    }
}

public sealed class NetworkResult
{
    public List<NetSegment> Segments { get; } = new();
    public List<Junction> Junctions { get; } = new();
    public int RemovedShort { get; internal set; }
    public int RemovedDuplicates { get; internal set; }
    /// <summary>Tramos alineados que se pisaban (una línea dibujada sobre parte de otra) y se han unido en uno.</summary>
    public int MergedOverlaps { get; internal set; }
    /// <summary>Curvas (codos dibujados como arco) sustituidas por una esquina.</summary>
    public int FilletsCollapsed { get; internal set; }
    public int MergedCollinear { get; internal set; }
    /// <summary>Trozos coincidentes de distinto ancho (el mismo tramo leído dos veces) reducidos a uno.</summary>
    public int CoaxialResolved { get; internal set; }
    /// <summary>Trocitos cortos de otro ancho (restos del cuerpo de una pieza) que han tomado el ancho de su tramo.</summary>
    public int StubsAbsorbed { get; internal set; }
    public int SplitAtTee { get; internal set; }
    public List<NetworkGap> Gaps { get; } = new();
    public int Count(JunctionKind k) => Junctions.Count(j => j.Kind == k);
}

/// <summary>
/// Prepara los segmentos CAD para Revit:
/// 1) convierte a mm y aplica desplazamiento; 2) convierte en esquina las curvas que hacen de codo;
/// 3) quita tramos cortos y duplicados y une los alineados que se pisan;
/// 4) parte un tramo donde otro de la misma capa termina en su interior (unión en T);
/// 5) une tramos colineales consecutivos; 6) clasifica las uniones (codo, te, cruz, transición).
/// Las uniones solo se buscan entre tramos de la MISMA capa (mismo sistema).
/// </summary>
public static class NetworkBuilder
{
    public sealed class Settings
    {
        public double UnitToMm { get; init; } = 1.0;
        /// <summary>Distancia máxima (mm) para considerar que dos extremos se tocan.</summary>
        public double ToleranceMm { get; init; } = 10.0;
        /// <summary>Tramos más cortos que esto (mm) se descartan.</summary>
        public double MinLengthMm { get; init; } = 50.0;
        /// <summary>Desplazamiento (en mm) que se suma a todas las coordenadas.</summary>
        public P3 OffsetMm { get; init; }
        /// <summary>Si es true, se ignora la Z del CAD (todo a la misma altura).</summary>
        public bool FlattenZ { get; init; } = true;
        /// <summary>Ángulo máximo (grados) para considerar dos tramos colineales.</summary>
        public double CollinearAngleDeg { get; init; } = 1.0;
        /// <summary>
        /// Una curva que une dos tramos rectos es un codo dibujado: se sustituye por una esquina prolongando
        /// los dos tramos hasta donde se cortan, siempre que esa prolongación no supere esta distancia (mm).
        /// Las curvas mayores se dejan como cuerdas.
        /// </summary>
        public double MaxFilletMm { get; init; } = 1500.0;
        /// <summary>
        /// Puntos (en unidades del dibujo, como los tramos) donde el plano dibuja una TRANSICIÓN o un reductor como
        /// bloque: ahí el cambio de ancho es real y no se toma por el cuerpo de una pieza. Opcional.
        /// </summary>
        public IReadOnlyList<P3>? TransitionHints { get; init; }
        /// <summary>
        /// Capas cuyo «ancho» es un espesor real (MUROS): en ellas no se aplican las reglas de cuerpos de pieza (tramos
        /// coincidentes de distinto ancho y trocitos cortos de otro ancho), que son para tuberías, conductos y bandejas.
        /// </summary>
        public IReadOnlyCollection<string>? ThicknessLayers { get; init; }
        public IReadOnlyDictionary<string, (MepKind Kind, double WidthMm)>? GapLayers { get; init; }
        public double GapScale { get; init; } = 1;
        public Func<NetworkResult, Func<GapProposal, string?>>? PrepareGapValidation { get; init; }
        public Func<IReadOnlyList<NetSegment>, IReadOnlyDictionary<NetSegment, (double Width, double? Height)>>? ReadSizesBeforeMerge { get; init; }
    }

    public static NetworkResult Build(IEnumerable<CadSegment> input, Settings s)
    {
        var result = new NetworkResult();
        double tol = Math.Max(0.001, s.ToleranceMm);
        double cosTol = Math.Cos(s.CollinearAngleDeg * Math.PI / 180.0);

        // 1) Conversión
        var segs = new List<NetSegment>();
        foreach (var c in input)
            segs.Add(new NetSegment
            {
                Layer = c.Layer, Start = Convert(c.Start, s), End = Convert(c.End, s), WidthMm = c.WidthMm,
                ArcId = c.ArcId, ArcCorner = c.ArcCorner is { } corner ? Convert(corner, s) : null
            });

        var clean = new Dictionary<string, List<NetSegment>>(StringComparer.OrdinalIgnoreCase);
        foreach (var group in segs.GroupBy(x => x.Layer, StringComparer.OrdinalIgnoreCase))
        {
            var list = group.ToList();
            // Las curvas se tratan antes de descartar tramos cortos: la cuerda de un codo pequeño es corta.
            list = CollapseFillets(list, tol, s.MaxFilletMm, result);
            result.RemovedShort += list.RemoveAll(x => x.Length < s.MinLengthMm);
            list = RemoveDuplicates(list, tol, result);
            list = MergeOverlaps(list, tol, cosTol, result);
            list = SplitAtInteriorEnds(list, tol, result);
            bool fittings = s.ThicknessLayers is null || !s.ThicknessLayers.Contains(group.Key, StringComparer.OrdinalIgnoreCase);
            if (fittings) list = ResolveCoaxial(list, tol, cosTol, result);
            clean.Add(group.Key, list);
        }
        var readSizes = s.ReadSizesBeforeMerge?.Invoke(clean.Values.SelectMany(x => x).ToList());
        var hints = s.TransitionHints?.Select(p => Convert(p, s)).ToList();
        foreach (var (layer, cleaned) in clean)
        {
            var list = cleaned;
            bool fittings = s.ThicknessLayers is null || !s.ThicknessLayers.Contains(layer, StringComparer.OrdinalIgnoreCase);
            var sizeBreaks = new List<(P3 Point, P3 Direction)>();
            if (readSizes is { Count: > 0 })
            {
                var (nodes, _, _, points) = BuildNodes(list, tol);
                for (int i = 0; i < nodes.Count; i++)
                {
                    var ends = nodes[i];
                    if (ends.Count != 2 || ends[0].Item1 == ends[1].Item1) continue;
                    var first = list[ends[0].Item1];
                    var second = list[ends[1].Item1];
                    var dir = OutDir(first, ends[0].Item2);
                    if (!SameWidth(first, second) || dir.Dot(OutDir(second, ends[1].Item2)) > -cosTol ||
                        !readSizes.TryGetValue(first, out var a) || !readSizes.TryGetValue(second, out var b) || SameSize(a, b)) continue;
                    sizeBreaks.Add((points[i], dir));
                }
            }
            // Unir alineados y quitar los cortos, repitiendo: al quitar un trocito corto (el ramal mínimo de una te) pueden
            // quedar dos tramos alineados del mismo ancho que antes no se unían y que Revit dejaría sin conectar.
            for (int pass = 0; pass < 6; pass++)
            {
                int absorbed = fittings ? AbsorbStubs(list, tol, cosTol, result, hints) : 0;
                list = MergeCollinear(list, tol, cosTol, result);
                int removed = list.RemoveAll(x => x.Length < s.MinLengthMm);
                result.RemovedShort += removed;
                if (removed == 0 && absorbed == 0) break;
            }
            // Se conserva la limpieza de contornos. Solo se parte el eje limpio cuando dos rótulos
            // o cotas distintos describen un cambio en el nudo original.
            foreach (var (point, direction) in sizeBreaks)
            {
                var candidates = list.Select((seg, index) =>
                    (seg, index, projection: ProjectToSegment(point, seg.Start, seg.End)))
                    .Where(x => Math.Abs(x.seg.Direction.Dot(direction)) >= cosTol &&
                        Math.Min(x.projection.T, 1 - x.projection.T) * x.seg.Length >= s.MinLengthMm &&
                        x.projection.Dist <= tol + Math.Min(x.projection.T, 1 - x.projection.T) * x.seg.Length *
                            Math.Sin(s.CollinearAngleDeg * Math.PI / 180))
                    .OrderBy(x => x.projection.Dist).ToList();
                if (candidates.Count == 0) continue;
                var (seg, idx, hit) = candidates[0];
                var at = seg.Start + (seg.End - seg.Start) * hit.T;
                list.Insert(idx + 1, new NetSegment { Layer = seg.Layer, Start = at, End = seg.End, WidthMm = seg.WidthMm });
                seg.End = at;
            }
            foreach (var seg in list)
            {
                seg.Index = result.Segments.Count;
                result.Segments.Add(seg);
            }
        }

        void BuildJunctions()
        {
            result.Junctions.Clear();
            foreach (var group in result.Segments.GroupBy(x => x.Layer, StringComparer.OrdinalIgnoreCase))
            foreach (var cluster in ClusterEnds(group.ToList(), tol))
            {
                if (cluster.Ends.Count < 2) continue;
                Classify(cluster, result.Segments, cosTol);
                result.Junctions.Add(cluster);
            }
        }
        BuildJunctions();
        if (s.GapScale <= 0 || s.GapLayers is null || result.Segments.Count == 0) return result;
        var validate = s.PrepareGapValidation?.Invoke(result);
        var groups = result.Segments.GroupBy(x => x.Layer, StringComparer.OrdinalIgnoreCase).ToList();
        result.Segments.Clear();
        foreach (var group in groups)
        {
            var list = group.ToList();
            if (s.GapLayers.TryGetValue(group.Key, out var gap) && gap.WidthMm > 0)
                NetworkGaps.Close(list, segs.Where(x => !x.Layer.Equals(group.Key, StringComparison.OrdinalIgnoreCase)).ToList(),
                    gap.Kind, gap.WidthMm, tol, s.MinLengthMm, cosTol, s.GapScale, result, validate);
            result.Segments.AddRange(list);
        }
        for (int i = 0; i < result.Segments.Count; i++) result.Segments[i].Index = i;
        BuildJunctions();
        return result;
    }

    private static P3 Convert(P3 p, Settings s)
    {
        var mm = new P3(p.X * s.UnitToMm, p.Y * s.UnitToMm, s.FlattenZ ? 0 : p.Z * s.UnitToMm);
        return mm + s.OffsetMm;
    }

    /// <summary>Índice espacial sencillo en rejilla XY para búsquedas por cercanía.</summary>
    internal sealed class PointGrid
    {
        private readonly double _cell;
        private readonly Dictionary<(long, long), List<int>> _cells = new();
        private readonly List<P3> _points = new();
        public PointGrid(double cell) => _cell = Math.Max(cell, 1e-6);
        private (long, long) Key(double x, double y) => ((long)Math.Floor(x / _cell), (long)Math.Floor(y / _cell));
        public IReadOnlyList<P3> Points => _points;
        public int Add(P3 p)
        {
            int id = _points.Count;
            _points.Add(p);
            var k = Key(p.X, p.Y);
            if (!_cells.TryGetValue(k, out var l)) _cells[k] = l = new List<int>();
            l.Add(id);
            return id;
        }
        /// <summary>Puntos a distancia &lt;= r de p (r &lt;= tamaño de celda).</summary>
        public IEnumerable<int> Near(P3 p, double r)
        {
            var (kx, ky) = Key(p.X, p.Y);
            for (long dx = -1; dx <= 1; dx++)
            for (long dy = -1; dy <= 1; dy++)
                if (_cells.TryGetValue((kx + dx, ky + dy), out var l))
                    foreach (int id in l)
                        if (_points[id].DistanceTo(p) <= r) yield return id;
        }
    }

    private static List<NetSegment> RemoveDuplicates(List<NetSegment> list, double tol, NetworkResult r)
    {
        var keep = new List<NetSegment>();
        var grid = new PointGrid(tol);   // índice de los puntos iniciales de 'keep'
        foreach (var s in list)
        {
            bool dup = false;
            foreach (var p in new[] { s.Start, s.End })
            foreach (int id in grid.Near(p, tol))
            {
                var k = keep[id];
                if ((k.Start.DistanceTo(s.Start) <= tol && k.End.DistanceTo(s.End) <= tol) ||
                    (k.Start.DistanceTo(s.End) <= tol && k.End.DistanceTo(s.Start) <= tol)) { dup = true; break; }
            }
            if (dup) { r.RemovedDuplicates++; continue; }
            grid.Add(s.Start);
            keep.Add(s);
        }
        return keep;
    }

    /// <summary>
    /// Una curva (arco o tramo curvo de polilínea) que une dos tramos rectos tangentes a ella es un codo
    /// dibujado. Se sustituye por una esquina: los dos tramos rectos se prolongan hasta el punto donde se
    /// cortan (<see cref="NetSegment.ArcCorner"/>, calculado con la geometría del CAD) y las cuerdas de la
    /// curva se eliminan. Así Revit crea UN codo con el ángulo real, en lugar de dos codos con un tramo
    /// corto entre ellos, o ninguno si la cuerda es más corta que la longitud mínima.
    /// Las curvas que no unen exactamente dos tramos rectos se dejan como cuerdas.
    /// </summary>
    private static List<NetSegment> CollapseFillets(List<NetSegment> list, double tol, double maxFilletMm, NetworkResult r)
    {
        if (!list.Any(x => x.ArcId != 0 && x.ArcCorner is not null)) return list;
        double tangentCos = Math.Cos(5 * Math.PI / 180);

        // Extremos de los tramos rectos, para saber quién llega a cada extremo de una curva.
        var grid = new PointGrid(tol);
        var owner = new List<(int Seg, bool AtStart)>();
        for (int i = 0; i < list.Count; i++)
        {
            if (list[i].ArcId != 0) continue;
            grid.Add(list[i].Start); owner.Add((i, true));
            grid.Add(list[i].End); owner.Add((i, false));
        }
        // Extremos de todas las cuerdas: si a un punto llegan dos curvas distintas, no es un codo simple.
        var arcEnds = new PointGrid(tol);
        var arcOf = new List<int>();
        for (int i = 0; i < list.Count; i++)
        {
            if (list[i].ArcId == 0) continue;
            arcEnds.Add(list[i].Start); arcOf.Add(list[i].ArcId);
            arcEnds.Add(list[i].End); arcOf.Add(list[i].ArcId);
        }

        // Único tramo recto que termina en p y llega en dirección a la esquina (tangente a la curva).
        (int Seg, bool AtStart)? TangentNeighbor(P3 p, P3 corner, int arcId)
        {
            if (arcEnds.Near(p, tol).Any(id => arcOf[id] != arcId)) return null;
            (int Seg, bool AtStart)? found = null;
            foreach (int id in grid.Near(p, tol))
            {
                if (found != null) return null;                 // llega más de un tramo: no es un codo simple
                var (seg, atStart) = owner[id];
                var far = atStart ? list[seg].End : list[seg].Start;
                if (corner.DistanceTo(p) > tol && (p - far).Normalized().Dot((corner - p).Normalized()) < tangentCos) return null;
                found = (seg, atStart);
            }
            return found;
        }

        var remove = new HashSet<int>();
        var moved = new HashSet<(int Seg, bool AtStart)>();
        foreach (var arc in Enumerable.Range(0, list.Count).Where(i => list[i].ArcId != 0).GroupBy(i => list[i].ArcId))
        {
            var chords = arc.ToList();                          // en el orden del CAD: de un extremo de la curva al otro
            if (list[chords[0]].ArcCorner is not { } corner) continue;
            P3 a = list[chords[0]].Start, b = list[chords[^1]].End;
            if (a.DistanceTo(corner) > maxFilletMm || b.DistanceTo(corner) > maxFilletMm) continue;
            var na = TangentNeighbor(a, corner, arc.Key);
            var nb = TangentNeighbor(b, corner, arc.Key);
            if (na is null || nb is null || na.Value.Seg == nb.Value.Seg) continue;
            if (moved.Contains(na.Value) || moved.Contains(nb.Value)) continue;
            foreach (var (seg, atStart) in new[] { na.Value, nb.Value })
            {
                if (atStart) list[seg].Start = corner; else list[seg].End = corner;
                moved.Add((seg, atStart));
            }
            foreach (int c in chords) remove.Add(c);
            r.FilletsCollapsed++;
        }
        return remove.Count == 0 ? list : list.Where((_, i) => !remove.Contains(i)).ToList();
    }

    /// <summary>
    /// Une los tramos alineados que se pisan (una línea dibujada encima de parte de otra). Sin este paso el
    /// trozo pisado se construiría dos veces y aparecerían tes falsas en los extremos del solape.
    /// No une tramos de distinto ancho, ni los que solo se tocan por un extremo: de esos se ocupa
    /// <see cref="MergeCollinear"/>, que respeta las tes.
    /// </summary>
    private static List<NetSegment> MergeOverlaps(List<NetSegment> list, double tol, double cosTol, NetworkResult r)
    {
        if (list.Count < 2) return list;
        var alive = new bool[list.Count];
        Array.Fill(alive, true);
        // Cada unión elimina un tramo, así que hay como mucho tantas pasadas como tramos.
        for (int pass = 0; pass <= list.Count; pass++)
        {
            // Extremos ordenados por X, con su tramo, para filtrar rápido por la caja de cada tramo.
            var ends = new List<(P3 P, int Seg)>(list.Count * 2);
            for (int i = 0; i < list.Count; i++)
                if (alive[i]) { ends.Add((list[i].Start, i)); ends.Add((list[i].End, i)); }
            ends.Sort((p, q) => p.P.X.CompareTo(q.P.X));
            var xs = ends.Select(e => e.P.X).ToArray();
            bool changed = false;
            for (int i = 0; i < list.Count; i++)
            {
                if (!alive[i]) continue;
                var seg = list[i];
                bool merged;
                do
                {
                    merged = false;
                    double len = seg.Length;
                    if (len <= tol) break;
                    var dir = seg.Direction;
                    double minX = Math.Min(seg.Start.X, seg.End.X) - tol, maxX = Math.Max(seg.Start.X, seg.End.X) + tol;
                    double minY = Math.Min(seg.Start.Y, seg.End.Y) - tol, maxY = Math.Max(seg.Start.Y, seg.End.Y) + tol;
                    int from = Array.BinarySearch(xs, minX);
                    if (from < 0) from = ~from;
                    while (from > 0 && xs[from - 1] >= minX) from--;
                    for (int k = from; k < ends.Count && xs[k] <= maxX; k++)
                    {
                        var (p, j) = ends[k];
                        if (j == i || !alive[j] || p.Y < minY || p.Y > maxY) continue;
                        var other = list[j];
                        if (!SameWidth(seg, other) || Math.Abs(dir.Dot(other.Direction)) < cosTol) continue;
                        // Un extremo del otro tramo debe caer DENTRO de este (no en sus extremos)...
                        var (dist, t) = ProjectToSegment(p, seg.Start, seg.End);
                        double along = t * len;
                        if (dist > tol || along <= tol || along >= len - tol) continue;
                        // ...y sus dos extremos, sobre la misma recta.
                        double t0 = (other.Start - seg.Start).Dot(dir), t1 = (other.End - seg.Start).Dot(dir);
                        if ((seg.Start + dir * t0).DistanceTo(other.Start) > tol || (seg.Start + dir * t1).DistanceTo(other.End) > tol) continue;
                        var origin = seg.Start;
                        seg.Start = origin + dir * Math.Min(0, Math.Min(t0, t1));
                        seg.End = origin + dir * Math.Max(len, Math.Max(t0, t1));
                        alive[j] = false;
                        r.MergedOverlaps++;
                        merged = changed = true;
                        break;                                  // la caja del tramo ha cambiado: se vuelve a buscar
                    }
                } while (merged);
            }
            if (!changed) break;
        }
        return list.Where((_, i) => alive[i]).ToList();
    }

    /// <summary>Distancia de p al segmento ab y parámetro t (0..1) de la proyección.</summary>
    internal static (double Dist, double T) ProjectToSegment(P3 p, P3 a, P3 b)
    {
        var ab = b - a;
        double len2 = ab.Dot(ab);
        if (len2 <= 0) return (p.DistanceTo(a), 0);
        double t = (p - a).Dot(ab) / len2;
        t = Math.Clamp(t, 0, 1);
        var q = a + ab * t;
        return (p.DistanceTo(q), t);
    }

    private static List<NetSegment> SplitAtInteriorEnds(List<NetSegment> list, double tol, NetworkResult r)
    {
        // Extremos ordenados por X para filtrar rápidamente por la caja de cada tramo.
        var endpoints = list.SelectMany(s => new[] { s.Start, s.End }).OrderBy(p => p.X).ToArray();
        var xs = endpoints.Select(p => p.X).ToArray();
        var output = new List<NetSegment>();
        foreach (var seg in list)
        {
            double len = seg.Length;
            double minX = Math.Min(seg.Start.X, seg.End.X) - tol, maxX = Math.Max(seg.Start.X, seg.End.X) + tol;
            double minY = Math.Min(seg.Start.Y, seg.End.Y) - tol, maxY = Math.Max(seg.Start.Y, seg.End.Y) + tol;
            int from = Array.BinarySearch(xs, minX);
            if (from < 0) from = ~from;
            while (from > 0 && xs[from - 1] >= minX) from--;
            var cuts = new List<double>();
            for (int i = from; i < endpoints.Length && xs[i] <= maxX; i++)
            {
                var p = endpoints[i];
                if (p.Y < minY || p.Y > maxY) continue;
                var (d, t) = ProjectToSegment(p, seg.Start, seg.End);
                if (d > tol) continue;
                double along = t * len;
                if (along <= tol || along >= len - tol) continue; // es un extremo, no el interior
                if (cuts.Any(c => Math.Abs(c - along) <= tol)) continue;
                cuts.Add(along);
            }
            if (cuts.Count == 0) { output.Add(seg); continue; }
            cuts.Sort();
            var dir = seg.Direction;
            P3 prev = seg.Start;
            foreach (var c in cuts)
            {
                var pt = seg.Start + dir * c;
                output.Add(new NetSegment { Layer = seg.Layer, Start = prev, End = pt, WidthMm = seg.WidthMm });
                prev = pt;
                r.SplitAtTee++;
            }
            output.Add(new NetSegment { Layer = seg.Layer, Start = prev, End = seg.End, WidthMm = seg.WidthMm });
        }
        return output;
    }

    /// <summary>
    /// Trozos COINCIDENTES (mismos dos extremos) de distinto ancho: el mismo tramo leído dos veces en un dibujo a doble
    /// línea (el tubo y, por fuera, el cuerpo de una pieza, un aislamiento o una pareja falsa de líneas). Se deja uno
    /// solo: el del ancho que CONTINÚA en el recorrido a un lado u otro; si ninguno o los dos continúan, el más estrecho
    /// (el contorno exterior es la pieza o el aislamiento). Sin esto llegan a Revit dos tubos superpuestos y nudos de 5 a
    /// 7 tramos que no se pueden unir. Solo actúa en capas con ancho medido.
    /// </summary>
    private static List<NetSegment> ResolveCoaxial(List<NetSegment> list, double tol, double cosTol, NetworkResult r)
    {
        if (list.Count < 2 || list.All(x => x.WidthMm is null)) return list;
        var (nodes, startNode, endNode, _) = BuildNodes(list, tol);
        var groups = Enumerable.Range(0, list.Count)
            .Where(i => startNode[i] != endNode[i])
            .GroupBy(i => (Math.Min(startNode[i], endNode[i]), Math.Max(startNode[i], endNode[i])))
            .Where(g => g.Count() > 1).ToList();
        if (groups.Count == 0) return list;
        var inGroup = new HashSet<int>(groups.SelectMany(g => g));
        var remove = new HashSet<int>();
        foreach (var g in groups)
        {
            var members = g.ToList();
            // Continuaciones: tramos fuera del grupo que salen de sus nudos en la misma recta
            int Evidence(int m)
            {
                int score = 0;
                foreach (int node in new[] { startNode[m], endNode[m] })
                    foreach (var (o, _) in nodes[node])
                    {
                        if (inGroup.Contains(o) || remove.Contains(o)) continue;
                        if (Math.Abs(list[o].Direction.Dot(list[m].Direction)) < cosTol) continue;
                        if (SameWidth(list[o], list[m])) score++;
                    }
                return score;
            }
            int keep = members.OrderByDescending(Evidence).ThenBy(m => list[m].WidthMm ?? double.MaxValue).First();
            foreach (int m in members) if (m != keep) { remove.Add(m); r.CoaxialResolved++; }
        }
        return list.Where((_, i) => !remove.Contains(i)).ToList();
    }

    /// <summary>
    /// Trocito CORTO para su ancho (largo ≤ 3 anchos) alineado con un tramo MÁS LARGO de otro ancho, y sin ningún vecino
    /// alineado de su mismo ancho: es el resto del cuerpo de una pieza (embocadura, manguito), no un tubo de otro
    /// diámetro. Toma el ancho de ese tramo y después se une a él. Sin esto se leería una transición donde no la hay.
    /// Un cambio de diámetro real tiene tramos largos a los dos lados (o su vecino del mismo ancho) y no se toca.
    /// Devuelve cuántos ha cambiado.
    /// </summary>
    private static int AbsorbStubs(List<NetSegment> list, double tol, double cosTol, NetworkResult r, List<P3>? transitionHints)
    {
        if (list.Count < 2 || list.All(x => x.WidthMm is null)) return 0;
        var (nodes, startNode, endNode, _) = BuildNodes(list, tol);
        int count = 0;
        for (int i = 0; i < list.Count; i++)
        {
            var x = list[i];
            if (x.WidthMm is not double w) continue;
            int best = -1;
            P3 joint = default;
            bool sameRun = false;
            foreach (bool atStart in new[] { true, false })
            {
                var outDir = OutDir(x, atStart);
                foreach (var (o, oAtStart) in nodes[atStart ? startNode[i] : endNode[i]])
                {
                    if (o == i || list[o].WidthMm is null) continue;
                    if (outDir.Dot(OutDir(list[o], oAtStart)) > -cosTol) continue; // no es su continuación en línea
                    if (SameWidth(x, list[o])) { sameRun = true; break; }
                    if (best < 0 || list[o].Length > list[best].Length) { best = o; joint = atStart ? x.Start : x.End; }
                }
                if (sameRun) break;
            }
            if (sameRun || best < 0) continue;
            var n = list[best];
            if (n.Length <= x.Length || x.Length > StubLimit(Math.Max(w, n.WidthMm!.Value))) continue;
            // El plano dibuja ahí una transición o un reductor: el cambio de ancho es real.
            double reach = Math.Max(150, 2.5 * Math.Max(w, n.WidthMm.Value));
            if (transitionHints != null && transitionHints.Any(h => h.DistanceTo(joint) <= reach || h.DistanceTo((x.Start + x.End) * 0.5) <= reach)) continue;
            list[i] = new NetSegment { Layer = x.Layer, Start = x.Start, End = x.End, WidthMm = n.WidthMm, ArcId = x.ArcId, ArcCorner = x.ArcCorner };
            r.StubsAbsorbed++;
            count++;
        }
        return count;
    }

    /// <summary>Agrupa los extremos en nudos. Devuelve, para cada tramo, el nudo de inicio y de fin.</summary>
    private static (List<List<(int Seg, bool AtStart)>> Nodes, int[] StartNode, int[] EndNode, List<P3> NodePoints)
        BuildNodes(List<NetSegment> list, double tol)
    {
        var grid = new PointGrid(tol);
        var nodes = new List<List<(int, bool)>>();
        var startNode = new int[list.Count];
        var endNode = new int[list.Count];
        for (int i = 0; i < list.Count; i++)
        {
            foreach (bool atStart in new[] { true, false })
            {
                var p = atStart ? list[i].Start : list[i].End;
                int node = -1;
                foreach (int id in grid.Near(p, tol)) { node = id; break; }
                if (node < 0) { node = grid.Add(p); nodes.Add(new List<(int, bool)>()); }
                nodes[node].Add((i, atStart));
                if (atStart) startNode[i] = node; else endNode[i] = node;
            }
        }
        return (nodes, startNode, endNode, grid.Points.ToList());
    }

    private static List<NetSegment> MergeCollinear(List<NetSegment> list, double tol, double cosTol, NetworkResult r)
    {
        var (nodes, startNode, endNode, _) = BuildNodes(list, tol);
        // Un nudo es "de paso" si une exactamente dos tramos distintos y alineados.
        bool PassThrough(int node)
        {
            var e = nodes[node];
            if (e.Count != 2 || e[0].Item1 == e[1].Item1) return false;
            if (!SameWidth(list[e[0].Item1], list[e[1].Item1])) return false; // distinto tamaño: transición
            // De paso = los dos tramos salen del nudo en sentidos OPUESTOS. Si salen hacia el mismo lado
            // están superpuestos: unirlos haría desaparecer el trozo común, así que se dejan y la unión
            // queda marcada como no admitida para que el usuario la vea.
            return OutDir(list[e[0].Item1], e[0].Item2).Dot(OutDir(list[e[1].Item1], e[1].Item2)) <= -cosTol;
        }
        var visited = new bool[list.Count];
        var output = new List<NetSegment>();
        for (int i = 0; i < list.Count; i++)
        {
            if (visited[i]) continue;
            visited[i] = true;
            // Extender hacia el extremo inicial y hacia el final.
            P3 Extend(int seg, bool atStart)
            {
                int cur = seg; bool curAtStart = atStart;
                while (true)
                {
                    int node = curAtStart ? startNode[cur] : endNode[cur];
                    P3 here = curAtStart ? list[cur].Start : list[cur].End;
                    if (!PassThrough(node)) return here;
                    var next = nodes[node].First(e => e.Item1 != cur);
                    if (visited[next.Item1]) return here; // ciclo cerrado
                    visited[next.Item1] = true;
                    r.MergedCollinear++;
                    cur = next.Item1;
                    curAtStart = !next.Item2; // continuar por el extremo opuesto
                }
            }
            P3 a = Extend(i, true);
            P3 b = Extend(i, false);
            output.Add(new NetSegment { Layer = list[i].Layer, Start = a, End = b, WidthMm = list[i].WidthMm });
        }
        return output;
    }

    private static List<Junction> ClusterEnds(List<NetSegment> group, double tol)
    {
        var (nodes, _, _, points) = BuildNodes(group, tol);
        var clusters = new List<Junction>();
        for (int n = 0; n < nodes.Count; n++)
        {
            var j = new Junction { Point = points[n], Layer = group[0].Layer };
            foreach (var (seg, atStart) in nodes[n]) j.Ends.Add((group[seg].Index, atStart));
            clusters.Add(j);
        }
        return clusters;
    }

    private static bool SameWidth(NetSegment a, NetSegment b) =>
        a.WidthMm is null ? b.WidthMm is null : b.WidthMm is not null && Math.Abs(a.WidthMm.Value - b.WidthMm.Value) <= 0.5;

    private static P3 OutDir(NetSegment s, bool atStart) => atStart ? s.Direction : s.Direction * -1;

    private static readonly double NearStraightCos = Math.Cos(10 * Math.PI / 180);

    public static bool SameSize((double Width, double? Height) a, (double Width, double? Height) b) =>
        Math.Abs(a.Width - b.Width) <= PlanDefaults.FittingSizeToleranceMm &&
        a.Height.HasValue == b.Height.HasValue &&
        Math.Abs(a.Height.GetValueOrDefault() - b.Height.GetValueOrDefault()) <= PlanDefaults.FittingSizeToleranceMm;

    public static bool IsAlignedPair(Junction joint, NetworkResult network, double collinearAngleDeg = 1)
    {
        if (joint.Ends.Count != 2 || joint.Ends[0].Segment == joint.Ends[1].Segment) return false;
        var (first, firstStart) = joint.Ends[0];
        var (second, secondStart) = joint.Ends[1];
        double straightCos = joint.Kind == JunctionKind.Transicion ? NearStraightCos : Math.Cos(collinearAngleDeg * Math.PI / 180);
        return OutDir(network.Segments[first], firstStart).Dot(OutDir(network.Segments[second], secondStart)) <= -straightCos;
    }

    public static void ReclassifySizes(NetworkResult network, Func<NetSegment, (double Width, double? Height)?> getSize,
        double collinearAngleDeg = 1)
    {
        foreach (var joint in network.Junctions.Where(j => IsAlignedPair(j, network, collinearAngleDeg)))
        {
            int first = joint.Ends[0].Segment, second = joint.Ends[1].Segment;
            var a = network.Segments[first];
            var b = network.Segments[second];
            if (getSize(a) is not { } sa || getSize(b) is not { } sb) continue;
            joint.Kind = SameSize(sa, sb) ? JunctionKind.EnLinea : JunctionKind.Transicion;
            joint.Ordered = joint.Ends.ToList();
            joint.Note = "";
        }
    }

    /// <summary>
    /// Largo máximo (mm) de un trozo para ser «el cuerpo de una pieza» y no un tramo: 3 anchos, pero nunca más que el
    /// ancho + 400 mm. Tubo de 20 → 60 mm; tubo de 110 → 330 mm; conducto de 1000 → 1400 mm.
    /// </summary>
    internal static double StubLimit(double widthMm) => Math.Min(3 * widthMm, widthMm + 400);

    private static void Classify(Junction j, List<NetSegment> segs, double cosTol)
    {
        var ends = j.Ends;
        if (ends.Select(e => e.Segment).Distinct().Count() != ends.Count)
        {
            j.Kind = JunctionKind.NoAdmitida;
            j.Note = "Un mismo tramo llega dos veces al nudo (tramo demasiado corto).";
            return;
        }
        switch (ends.Count)
        {
            case 2:
            {
                double dot = OutDir(segs[ends[0].Segment], ends[0].AtStart).Dot(OutDir(segs[ends[1].Segment], ends[1].AtStart));
                // Casi en línea (menos de 10°) y de distinto ancho: es una transición (una reducción excéntrica desvía un
                // poco el eje), no un codo de pocos grados.
                if (dot <= -NearStraightCos && dot > -cosTol && !SameWidth(segs[ends[0].Segment], segs[ends[1].Segment]))
                { j.Kind = JunctionKind.Transicion; j.Ordered = ends.ToList(); }
                else if (dot > -cosTol && dot < cosTol) { j.Kind = JunctionKind.Codo; j.Ordered = ends.ToList(); }
                else if (dot <= -cosTol && !SameWidth(segs[ends[0].Segment], segs[ends[1].Segment]))
                { j.Kind = JunctionKind.Transicion; j.Ordered = ends.ToList(); }
                else { j.Kind = JunctionKind.NoAdmitida; j.Note = "Dos tramos alineados o superpuestos: no necesitan codo (revise el dibujo)."; }
                return;
            }
            case 3:
            {
                // Buscar la pareja colineal (direcciones de salida opuestas): será el tramo principal.
                for (int a = 0; a < 3; a++)
                for (int b = a + 1; b < 3; b++)
                {
                    double dot = OutDir(segs[ends[a].Segment], ends[a].AtStart).Dot(OutDir(segs[ends[b].Segment], ends[b].AtStart));
                    if (dot <= -cosTol)
                    {
                        int c = 3 - a - b;
                        j.Kind = JunctionKind.Te;
                        j.Ordered = new() { ends[a], ends[b], ends[c] };
                        return;
                    }
                }
                j.Kind = JunctionKind.NoAdmitida;
                j.Note = "Tres tramos sin ninguna pareja alineada: Revit no puede crear una te.";
                return;
            }
            case 4:
            {
                var used = new HashSet<int>();
                var ordered = new List<(int, bool)>();
                for (int a = 0; a < 4; a++)
                for (int b = a + 1; b < 4; b++)
                {
                    if (used.Contains(a) || used.Contains(b)) continue;
                    double dot = OutDir(segs[ends[a].Segment], ends[a].AtStart).Dot(OutDir(segs[ends[b].Segment], ends[b].AtStart));
                    if (dot <= -cosTol) { used.Add(a); used.Add(b); ordered.Add(ends[a]); ordered.Add(ends[b]); }
                }
                if (ordered.Count == 4) { j.Kind = JunctionKind.Cruz; j.Ordered = ordered; }
                else { j.Kind = JunctionKind.NoAdmitida; j.Note = "Cuatro tramos que no forman dos ejes alineados."; }
                return;
            }
            default:
                j.Kind = JunctionKind.NoAdmitida;
                j.Note = ends.Count + " tramos en el mismo punto: une como máximo 4.";
                return;
        }
    }
}
