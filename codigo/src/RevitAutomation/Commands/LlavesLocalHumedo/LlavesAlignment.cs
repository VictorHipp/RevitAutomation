// Antes «#if !NET48»: ahora también se compila para Revit 2023 y 2024 (.NET Framework 4.8).
namespace RevitAutomation.Addin.Commands.LlavesLocalHumedo;

internal readonly record struct LlavesPoint(double X, double Y, double Z);

/// <summary>Comprueba que el archivo y el vínculo comparten posición y escala antes de colocar.</summary>
internal static class LlavesAlignment
{
    internal static bool Matches(IEnumerable<LlavesPoint> vertices, IEnumerable<LlavesPoint> samples, double tolerance)
    {
        if (!double.IsFinite(tolerance) || tolerance <= 0) throw new ArgumentOutOfRangeException(nameof(tolerance));
        bool Finite(LlavesPoint p) => double.IsFinite(p.X) && double.IsFinite(p.Y) && double.IsFinite(p.Z);
        (long, long, long) Cell(LlavesPoint p) => ((long)Math.Floor(p.X / tolerance), (long)Math.Floor(p.Y / tolerance), (long)Math.Floor(p.Z / tolerance));
        var grid = vertices.Where(Finite).GroupBy(Cell).ToDictionary(g => g.Key, g => g.ToArray());
        var points = samples.ToArray();
        if (points.Any(p => !Finite(p))) return false;
        // Repetir el mismo extremo varias veces no demuestra la posición del dibujo.
        points = points.DistinctBy(Cell).ToArray();
        if (points.Length < 3) return false;
        bool Hit(LlavesPoint p)
        {
            var (x, y, z) = Cell(p);
            for (int dx = -1; dx <= 1; dx++)
                for (int dy = -1; dy <= 1; dy++)
                    for (int dz = -1; dz <= 1; dz++)
                        if (grid.TryGetValue((x + dx, y + dy, z + dz), out var nearby) && nearby.Any(q =>
                            (p.X-q.X)*(p.X-q.X) + (p.Y-q.Y)*(p.Y-q.Y) + (p.Z-q.Z)*(p.Z-q.Z) <= tolerance*tolerance)) return true;
            return false;
        }
        return points.Count(Hit) >= Math.Ceiling(points.Length * 0.8);
    }
}

