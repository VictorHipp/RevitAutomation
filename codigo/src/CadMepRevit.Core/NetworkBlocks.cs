namespace CadMepRevit.Core;

public enum NetworkBlockKind { EnLinea, Terminal, Equipo }
public enum NetworkBlockSite { Interior, Inicio, Fin, Costado }

public sealed record NetworkBlockLink(int Segment, P3 Axis, double Along, NetworkBlockSite Site, int Side,
    P3 Direction, double WidthMm, double DistanceMm);

public static class NetworkBlocks
{
    public static (MepKind Kind, NetworkBlockKind Use)? For(ModelScope? scope, BlockRole role, string name)
    {
        if (scope is not ModelScope button || !ModelScopes.Includes(button, role, name)) return null;
        return (button, role) switch
        {
            (ModelScope.Tuberias, BlockRole.Accesorio) => (MepKind.Tuberia, NetworkBlockKind.EnLinea),
            (ModelScope.Tuberias, BlockRole.Equipo) => (MepKind.Tuberia, NetworkBlockKind.Equipo),
            (ModelScope.Conductos, BlockRole.Terminal) => (MepKind.Conducto, NetworkBlockKind.Terminal),
            (ModelScope.Conductos, BlockRole.Accesorio) => (MepKind.Conducto, NetworkBlockKind.EnLinea),
            (ModelScope.Conductos, BlockRole.Equipo or BlockRole.Conector) => (MepKind.Conducto, NetworkBlockKind.Equipo),
            (ModelScope.Bandejas, BlockRole.Accesorio) => (MepKind.Bandeja, NetworkBlockKind.EnLinea),
            _ => null
        };
    }

    public static NetworkBlockLink? Associate(P3 position, NetworkBlockKind use, double tolerance,
        IEnumerable<(NetSegment Segment, double WidthMm)> candidates, double blockAngleRad = 0, Footprint? footprint = null)
    {
        var point = position with { Z = 0 };
        NetworkBlockLink? best = null;
        foreach (var (segment, width) in candidates)
        {
            var a = segment.Start with { Z = 0 };
            var b = segment.End with { Z = 0 };
            var axis = b - a;
            double length = axis.Length;
            if (length <= tolerance || width <= 0) continue;
            var dir = axis.Normalized();
            double raw = (point - a).Dot(dir);
            var (distance, along) = NetworkBuilder.ProjectToSegment(point, a, b);
            var at = a + axis * along;
            double limit = PlanDefaults.BlockNetworkWidthFactor * width;
            if (best != null && distance >= best.DistanceMm ||
                distance > limit && (footprint == null || DistanceToFootprint(at, footprint) > limit)) continue;
            var left = new P3(-dir.Y, dir.X, 0);
            double lateral = (point - at).Dot(left);
            int side = Math.Abs(lateral) > tolerance ? Math.Sign(lateral) : 0;
            var site = raw <= tolerance ? NetworkBlockSite.Inicio :
                raw >= length - tolerance ? NetworkBlockSite.Fin : NetworkBlockSite.Interior;
            var direction = site == NetworkBlockSite.Inicio ? dir * -1 : dir;
            if (use == NetworkBlockKind.Terminal)
            {
                double inline = Math.Max(tolerance, PlanDefaults.BlockInlineWidthFactor * width);
                if (raw < length / 2 && Math.Abs(raw) <= inline && Math.Abs(lateral) <= inline)
                { site = NetworkBlockSite.Inicio; at = a; along = 0; direction = dir * -1; }
                else if (raw >= length / 2 && Math.Abs(raw - length) <= inline && Math.Abs(lateral) <= inline)
                { site = NetworkBlockSite.Fin; at = b; along = 1; direction = dir; }
                else if (raw > tolerance && raw < length - tolerance)
                {
                    site = NetworkBlockSite.Costado;
                    if (side == 0)
                    {
                        var drawn = new P3(Math.Cos(blockAngleRad), Math.Sin(blockAngleRad), 0);
                        side = Math.Sign(drawn.Dot(left));
                    }
                    if (side != 0) direction = left * side;
                }
            }
            else if (use == NetworkBlockKind.EnLinea) direction = dir;
            double z = segment.Start.Z + (segment.End.Z - segment.Start.Z) * along;
            best = new(segment.Index, at with { Z = z }, along, site, side, direction, width, distance);
        }
        return best;
    }

    private static double DistanceToFootprint(P3 at, Footprint foot)
    {
        double x = at.X - foot.CenterX, y = at.Y - foot.CenterY;
        double cos = Math.Cos(foot.AngleRad), sin = Math.Sin(foot.AngleRad);
        double dx = Math.Max(0, Math.Abs(x * cos + y * sin) - foot.SizeX / 2);
        double dy = Math.Max(0, Math.Abs(y * cos - x * sin) - foot.SizeY / 2);
        return Math.Sqrt(dx * dx + dy * dy);
    }
}
