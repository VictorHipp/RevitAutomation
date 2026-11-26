namespace CadMepRevit.Core;

/// <summary>Redes conectadas: grupos de tramos unidos entre sí por uniones (codos, tes, cruces, transiciones).</summary>
public static class NetworkComponents
{
    /// <summary>
    /// Agrupa los tramos indicados según las uniones de la red. Cada grupo conserva el orden de entrada;
    /// los grupos salen en el orden de su primer tramo.
    /// </summary>
    public static List<List<int>> Find(IEnumerable<int> segmentIndices, IEnumerable<Junction> junctions)
    {
        var order = segmentIndices.Distinct().ToList();
        var parent = order.ToDictionary(i => i, i => i);
        int Root(int i)
        {
            while (parent[i] != i) { parent[i] = parent[parent[i]]; i = parent[i]; }
            return i;
        }
        foreach (var j in junctions)
        {
            var ends = j.Ends.Select(e => e.Segment).Where(parent.ContainsKey).Distinct().ToList();
            for (int k = 1; k < ends.Count; k++)
            {
                int a = Root(ends[0]), b = Root(ends[k]);
                if (a != b) parent[b] = a;
            }
        }
        var groups = new Dictionary<int, List<int>>();
        var result = new List<List<int>>();
        foreach (int i in order)
        {
            int r = Root(i);
            if (!groups.TryGetValue(r, out var g)) { g = new List<int>(); groups[r] = g; result.Add(g); }
            g.Add(i);
        }
        return result;
    }

    /// <summary>Puntos cada <paramref name="stepMm"/> a lo largo del tramo, incluidos los dos extremos.</summary>
    public static List<P3> Samples(P3 start, P3 end, double stepMm)
    {
        double len = start.DistanceTo(end);
        int n = Math.Max(1, (int)Math.Ceiling(len / Math.Max(stepMm, 1)));
        var list = new List<P3>(n + 1);
        for (int i = 0; i <= n; i++) list.Add(start + (end - start) * (i / (double)n));
        return list;
    }
}
