using System.Text;
using System.Text.RegularExpressions;

namespace CadMepRevit.Core;

public sealed record SlopedPipe(P3 Start, P3 End, double PlannedRatio, int Network);
public sealed record SlopeNetwork(int Id, IReadOnlyList<int> Segments, string Reference, double ReferenceMm,
    double HighestMm, double OutletMm, bool Conflict)
{
    /// <summary>
    /// Red colgada sin ninguna cota en el plano: su altura es la SUPUESTA. Solo estas redes (si además son horizontales)
    /// pueden seguir el techo; las que tienen cota o pendiente en el plano la conservan.
    /// </summary>
    public bool AssumedSuspended { get; init; }
}

public sealed class SlopeSolution
{
    public SlopeAssociation Association { get; init; } = new();
    public Dictionary<int, SlopedPipe> Pipes { get; } = new();
    public List<SlopeNetwork> Networks { get; } = new();
    public List<string> Warnings { get; } = new();
    public int SlopingRuns { get; set; }
    public int UnlabelledRuns { get; set; }
    public string Report()
    {
        var sb = new StringBuilder("PENDIENTES\n");
        sb.AppendLine($"  Etiquetas leídas: {Association.Labels.Count}; asociadas: {Association.Associated}; descartadas: {Association.Labels.Count - Association.Associated}.");
        sb.AppendLine($"  Votos aislados descartados tras asociar: {Association.Labels.Count(l => l.IsolatedVoteDiscarded)}.");
        sb.AppendLine($"  Recorridos con pendiente: {SlopingRuns}; horizontales sin etiqueta: {UnlabelledRuns}; redes con recorridos dejados horizontales por conflicto: {Networks.Count(n => n.Conflict)}.");
        foreach (var n in Networks)
            sb.AppendLine($"  Red {n.Id}: {n.Reference}; referencia {n.ReferenceMm:0.##} mm; eje más alto {n.HighestMm:0.##} mm; salida {n.OutletMm:0.##} mm; {n.Segments.Count} tramos.");
        foreach (var l in Association.Labels.Where(l => l.Segment == null))
            sb.AppendLine($"  Aviso en {l.PositionMm} mm: «{l.Text.Value}»: {l.Reason}.");
        foreach (var w in Association.Warnings.Concat(Warnings)) sb.AppendLine("  Aviso: " + w);
        return sb.ToString();
    }
}

/// <summary>Una cota por nudo, recorridos hasta bifurcación y propagación determinista, sin Revit.</summary>
public static class SlopeSolver
{
    private sealed class Node
    {
        public P3 Point;
        public string Layer = "";
        public List<int> Edges = new();
    }
    public static bool IsBuried(CadDrawing drawing, IEnumerable<string> layers)
    {
        // La identificación explícita de la red prevalece sobre referencias a otra instalación en la leyenda.
        // Un plano de red colgada puede contener «conexión con enterrado» sin convertirse entero en enterrado.
        string identity = string.Join(" ", layers.Append(Path.GetFileName(drawing.Path)));
        if (Regex.IsMatch(identity, "ENTERRAD|BURIED", RegexOptions.IgnoreCase)) return true;
        if (Regex.IsMatch(identity, "COLGAD|SUSPEND|OVERHEAD", RegexOptions.IgnoreCase)) return false;
        // Por los textos solo si el plano habla de enterrado y NO de colgado: las leyendas de saneamiento suelen nombrar
        // las dos redes («fecales colgantes», «fecales enterradas»), y entonces los textos no deciden (colgada por defecto).
        bool buriedText = drawing.Texts.Any(t => Regex.IsMatch(t.Value, "ENTERRAD|BURIED", RegexOptions.IgnoreCase));
        bool hangingText = drawing.Texts.Any(t => Regex.IsMatch(t.Value, "COLGAD|COLGANT|SUSPEND|OVERHEAD", RegexOptions.IgnoreCase));
        return buriedText && !hangingText;
    }

    public static SlopeSolution Solve(IReadOnlyList<NetSegment> pipes, SlopeAssociation association,
        double toleranceMm, bool buried = false, double suspendedTopMm = PlanDefaults.HeightAboveLevelMm,
        Func<string, bool>? isBuriedLayer = null)
    {
        var result = new SlopeSolution { Association = association };
        foreach (var label in association.Labels) label.IsolatedVoteDiscarded = false;
        if (pipes.Count == 0) return result;
        var nodes = new List<Node>();
        var grid = new Dictionary<(long, long), List<int>>();
        double cell = Math.Max(toleranceMm, SlopeRules.Epsilon);
        int NodeOf(P3 p, string layer)
        {
            p = new P3(p.X, p.Y, 0);
            long x = (long)Math.Floor(p.X / cell), y = (long)Math.Floor(p.Y / cell);
            for (long dx = -1; dx <= 1; dx++) for (long dy = -1; dy <= 1; dy++)
                if (grid.TryGetValue((x + dx, y + dy), out var nearby))
                    foreach (int n in nearby)
                        if (nodes[n].Layer.Equals(layer, StringComparison.OrdinalIgnoreCase) && nodes[n].Point.DistanceTo(p) <= cell) return n;
            int id = nodes.Count;
            nodes.Add(new Node { Point = p, Layer = layer });
            if (!grid.TryGetValue((x, y), out var list)) grid[(x, y)] = list = new();
            list.Add(id);
            return id;
        }
        var a = new int[pipes.Count]; var b = new int[pipes.Count];
        for (int i = 0; i < pipes.Count; i++)
        {
            a[i] = NodeOf(pipes[i].Start, pipes[i].Layer); b[i] = NodeOf(pipes[i].End, pipes[i].Layer);
            nodes[a[i]].Edges.Add(i); nodes[b[i]].Edges.Add(i);
        }
        int Other(int e, int n) => a[e] == n ? b[e] : a[e];
        double Length(int e) => nodes[a[e]].Point.DistanceTo(nodes[b[e]].Point);
        var drops = new double[pipes.Count]; // Z(end) - Z(start)
        var visited = new HashSet<int>();
        var runOf = new int[pipes.Count];
        int runNumber = 0;
        foreach (int first in Enumerable.Range(0, pipes.Count).OrderBy(i => nodes[a[i]].Edges.Count == 2 && nodes[b[i]].Edges.Count == 2 ? 1 : 0))
        {
            if (visited.Contains(first)) continue;
            var run = new List<(int Edge, int From, double At)>();
            int node = nodes[a[first]].Edges.Count != 2 ? a[first] : b[first], e = first;
            double at = 0;
            while (visited.Add(e))
            {
                runOf[e] = runNumber;
                run.Add((e, node, at)); at += Length(e);
                node = Other(e, node);
                if (nodes[node].Edges.Count != 2) break;
                int next = nodes[node].Edges.First(x => x != e);
                if (visited.Contains(next)) break;
                e = next;
            }
            runNumber++;
            var votes = new List<(SlopeLabel Label, int Sign, double At)>();
            foreach (var item in run)
                foreach (var l in association.Labels.Where(l => l.Segment == pipes[item.Edge].Index))
                {
                    var dir = (nodes[Other(item.Edge, item.From)].Point - nodes[item.From].Point).Normalized();
                    double along = Math.Clamp((l.PositionMm - nodes[item.From].Point).Dot(dir), 0, Length(item.Edge));
                    votes.Add((l, dir.Dot(l.Downstream) >= 0 ? 1 : -1, item.At + along));
                }
            P3 site = nodes[run[0].From].Point;
            if (votes.Count == 0)
            { result.UnlabelledRuns++; result.Warnings.Add($"Recorrido {runNumber} en {site} mm sin etiqueta válida: horizontal; añada pendiente y flecha."); continue; }
            int plus = votes.Count(v => v.Sign == 1), minus = votes.Count - plus;
            int sign = plus > minus ? 1 : -1;
            if (plus == minus || Math.Min(plus, minus) > 1)
            { result.Warnings.Add($"Recorrido {runNumber} en {site} mm: votos incompatibles ({plus}/{minus}); horizontal; revise las flechas."); continue; }
            if (Math.Min(plus, minus) == 1)
            {
                if (votes.Count < 3) continue;
                var rejected = votes.Single(v => v.Sign != sign);
                rejected.Label.IsolatedVoteDiscarded = true;
                result.Warnings.Add($"Voto aislado en contra descartado en {rejected.Label.PositionMm} mm (recorrido {runNumber}, {plus}/{minus}).");
                votes.RemoveAll(v => v.Sign != sign);
            }
            if (votes.Select(v => v.Label.Ratio).Distinct().Count() > 1)
                result.Warnings.Add($"Recorrido {runNumber} en {site} mm con valores distintos: cada tramo usa su etiqueta o la más cercana por distancia de recorrido.");
            foreach (var item in run)
            {
                var own = votes.Where(v => v.Label.Segment == pipes[item.Edge].Index).ToList();
                var source = (own.Count > 0 ? own : votes).OrderBy(v => Math.Abs(v.At - (item.At + Length(item.Edge) / 2))).First();
                double drop = -sign * source.Label.Ratio * Length(item.Edge);
                drops[item.Edge] = item.From == a[item.Edge] ? drop : -drop;
            }
            result.SlopingRuns++;
        }
        // Un conflicto (anillo que no cierra o bifurcación con varias salidas) deja HORIZONTALES solo los recorridos
        // implicados, no la red entera: el resto de la red conserva sus pendientes y la cota única por nudo se mantiene.
        void Flatten(IEnumerable<int> edges)
        {
            var runs = edges.Where(e => Math.Abs(drops[e]) > SlopeRules.Epsilon).Select(e => runOf[e]).ToHashSet();
            for (int e = 0; e < pipes.Count; e++) if (runs.Contains(runOf[e])) drops[e] = 0;
        }
        var seen = new HashSet<int>();
        for (int start = 0; start < nodes.Count; start++)
        {
            if (!seen.Add(start)) continue;
            Dictionary<int, double> z;
            var componentEdges = new SortedSet<int>();
            bool conflict = false;
            while (true)
            {
                z = new Dictionary<int, double> { [start] = 0 };
                var parentEdge = new Dictionary<int, int> { [start] = -1 };
                var queue = new Queue<int>(); queue.Enqueue(start);
                List<int>? flatten = null;
                while (queue.Count > 0 && flatten == null)
                {
                    int n = queue.Dequeue();
                    var outgoing = nodes[n].Edges.Where(edge => (a[edge] == n ? drops[edge] : -drops[edge]) < -SlopeRules.Epsilon).ToList();
                    if (nodes[n].Edges.Count >= 3 && outgoing.Count > 1)
                    {
                        result.Warnings.Add($"Bifurcación en {nodes[n].Point} mm con {outgoing.Count} salidas aguas abajo: esos recorridos quedan horizontales; revise flechas.");
                        flatten = outgoing;
                        break;
                    }
                    foreach (int edge in nodes[n].Edges)
                    {
                        componentEdges.Add(edge);
                        int next = Other(edge, n);
                        double want = z[n] + (a[edge] == n ? drops[edge] : -drops[edge]);
                        if (z.TryGetValue(next, out double existing))
                        {
                            if (Math.Abs(want - existing) > SlopeRules.LoopErrorMm)
                            {
                                // Edges del anillo: este tramo y los caminos de sus dos nudos hasta el origen de la búsqueda.
                                // Solo el anillo: los caminos hasta el antecesor común, sin la parte compartida.
                                List<int> PathUp(int from)
                                {
                                    var path = new List<int>();
                                    for (int m = from; parentEdge.TryGetValue(m, out int pe) && pe >= 0; m = Other(pe, m)) path.Add(pe);
                                    return path;
                                }
                                var up1 = PathUp(n); var up2 = PathUp(next);
                                while (up1.Count > 0 && up2.Count > 0 && up1[^1] == up2[^1]) { up1.RemoveAt(up1.Count - 1); up2.RemoveAt(up2.Count - 1); }
                                var cycle = new List<int> { edge };
                                cycle.AddRange(up1); cycle.AddRange(up2);
                                if (cycle.All(e => Math.Abs(drops[e]) <= SlopeRules.Epsilon)) continue;
                                result.Warnings.Add($"Anillo en {nodes[next].Point} mm no cierra: {Math.Abs(want - existing):0.##} mm; sus recorridos con pendiente quedan horizontales; revise pendientes y cotas.");
                                flatten = cycle;
                                break;
                            }
                        }
                        else { z[next] = want; parentEdge[next] = edge; seen.Add(next); queue.Enqueue(next); }
                    }
                }
                if (flatten == null) break;
                conflict = true;
                Flatten(flatten);
            }
            double At(ElevationReference r)
            {
                int edge = componentEdges.First(i => pipes[i].Index == r.Segment);
                double t = Math.Clamp((r.PointMm - nodes[a[edge]].Point).Dot((nodes[b[edge]].Point - nodes[a[edge]].Point).Normalized()) / Length(edge), 0, 1);
                return z[a[edge]] + (z[b[edge]] - z[a[edge]]) * t;
            }
            var refs = association.References.Where(r => componentEdges.Any(i => pipes[i].Index == r.Segment)).OrderBy(At).ToList();
            string reference;
            double refMm, shift;
            bool assumedSuspended = false;
            if (refs.Count > 0)
            {
                var r = refs[0]; reference = r.Description + $" en {r.PointMm} mm"; refMm = r.AxisMm; shift = refMm - At(r);
                foreach (var other in refs.Skip(1))
                    if (Math.Abs(At(other) + shift - other.AxisMm) > SlopeRules.ReferenceErrorMm)
                        result.Warnings.Add($"Cota en {other.PointMm} mm: plano {other.AxisMm:0.##} mm; calculada {At(other) + shift:0.##} mm; se conserva la referencia aguas abajo.");
            }
            else
            {
                bool buriedNetwork = isBuriedLayer?.Invoke(pipes[componentEdges.First()].Layer) ?? buried;
                refMm = buriedNetwork ? PlanDefaults.BuriedTopAxisMm : suspendedTopMm;
                shift = refMm - z.Values.Max();
                reference = $"SUPUESTA, eje más alto de red {(buriedNetwork ? "enterrada" : "colgada")}; revisar";
                assumedSuspended = !buriedNetwork;
            }
            int id = result.Networks.Count + 1;
            result.Networks.Add(new SlopeNetwork(id, componentEdges.Select(i => pipes[i].Index).ToList(), reference, refMm,
                z.Values.Max() + shift, z.Values.Min() + shift, conflict) { AssumedSuspended = assumedSuspended });
            foreach (int edge in componentEdges)
            {
                var p = nodes[a[edge]].Point; var q = nodes[b[edge]].Point;
                result.Pipes[pipes[edge].Index] = new SlopedPipe(new P3(p.X, p.Y, z[a[edge]] + shift),
                    new P3(q.X, q.Y, z[b[edge]] + shift), Math.Abs(drops[edge]) / Length(edge), id);
            }
        }
        result.SlopingRuns = Enumerable.Range(0, pipes.Count).Where(i => result.Pipes[pipes[i].Index].PlannedRatio > SlopeRules.Epsilon)
            .Select(i => runOf[i]).Distinct().Count();
        return result;
    }
}
