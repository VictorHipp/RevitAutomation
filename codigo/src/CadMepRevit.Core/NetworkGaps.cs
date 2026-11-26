namespace CadMepRevit.Core;

public enum GapKind { Esquina, Te, EnLinea }

public sealed record NetworkGap(string Layer, GapKind Kind, P3 Point, double ExtensionMm, double LimitMm, bool Closed,
    string? Rejection = null);

public sealed record GapProposal(GapKind Kind, NetSegment First, NetSegment Other, P3 Point);

/// <summary>Completa extremos libres prolongando sus ejes existentes en planta.</summary>
internal static class NetworkGaps
{
    private const double ParallelEpsilon = 1e-9;
    private static readonly double MinTeeSine = Math.Sin(PlanDefaults.TeeGapMinAngleDeg * Math.PI / 180);

    private sealed class End(NetSegment segment, bool atStart, P3 point, P3 outward)
    {
        public NetSegment Segment { get; set; } = segment;
        public bool AtStart { get; } = atStart;
        public P3 Point { get; } = point;
        public P3 Out { get; } = outward;
    }
    private sealed record Candidate(GapKind Kind, End First, End? Second, NetSegment? Trunk,
        P3 Point, double FirstMm, double SecondMm, double FirstLimit, double SecondLimit, End[]? JunctionEnds = null)
    {
        public bool WithinLimit => Kind == GapKind.EnLinea
            ? FirstMm + SecondMm < Math.Min(FirstLimit, SecondLimit)
            : FirstMm <= FirstLimit && SecondMm <= SecondLimit;
        public double ExtensionMm => Kind == GapKind.EnLinea ? FirstMm + SecondMm : Math.Max(FirstMm, SecondMm);
        public double LimitMm => Math.Min(FirstLimit, SecondLimit);
    }

    public static double Limit(MepKind kind, double width, double scale) => scale * (kind == MepKind.Tuberia
        ? Math.Clamp(PlanDefaults.PipeGapDiameterFactor * width, PlanDefaults.PipeGapMinMm, PlanDefaults.PipeGapMaxMm)
        : Math.Min(PlanDefaults.WideGapWidthFactor * width, PlanDefaults.WideGapMaxMm));

    public static void Close(List<NetSegment> segments, IReadOnlyList<NetSegment> otherLayers, MepKind kind,
        double layerWidth, double tolerance, double minLength, double cosCollinear, double scale, NetworkResult result,
        Func<GapProposal, string?>? validate)
    {
        double SegmentLimit(NetSegment s) => Limit(kind, s.WidthMm ?? layerWidth, scale);
        var grid = new NetworkBuilder.PointGrid(tolerance);
        var ends = new List<End>();
        foreach (var s in segments)
        {
            P3 a = Flat(s.Start), b = Flat(s.End), d = (b - a).Normalized();
            grid.Add(a);
            ends.Add(new End(s, true, a, d * -1));
            grid.Add(b);
            ends.Add(new End(s, false, b, d));
        }
        var free = ends.Where(e => !grid.Near(e.Point, tolerance).Skip(1).Any() && e.Out.Length > 0).ToList();
        double search = Math.Max(PlanDefaults.GapSearchMm, scale * PlanDefaults.WideGapMaxMm);
        var freeGrid = new NetworkBuilder.PointGrid(2 * search);
        foreach (var e in free) freeGrid.Add(e.Point);
        var junctionGrid = new NetworkBuilder.PointGrid(search);
        var junctionEnds = new List<End[]>();
        for (int i = 0; i < ends.Count; i++)
        {
            var pair = grid.Near(ends[i].Point, tolerance).Order().ToArray();
            if (pair.Length != 2 || pair[0] != i || ends[pair[0]].Segment == ends[pair[1]].Segment ||
                ends[pair[0]].Out.Dot(ends[pair[1]].Out) > -cosCollinear) continue;
            junctionGrid.Add(ends[i].Point);
            junctionEnds.Add(pair.Select(j => ends[j]).ToArray());
        }
        var ordered = segments.OrderBy(s => Math.Min(s.Start.X, s.End.X)).ToList();
        var candidates = new List<Candidate>();
        for (int i = 0; i < free.Count; i++)
        {
            var a = free[i];
            double aLimit = SegmentLimit(a.Segment);
            foreach (int j in junctionGrid.Near(a.Point, search))
            {
                var pair = junctionEnds[j];
                var delta = pair[0].Point - a.Point;
                double distance = delta.Dot(a.Out);
                if (distance <= 0 || Math.Abs(Cross(delta, a.Out)) > tolerance ||
                    Math.Abs(Cross(a.Out, pair[0].Out)) < MinTeeSine) continue;
                candidates.Add(new(GapKind.Te, a, null, null, pair[0].Point, distance, 0, aLimit, aLimit, pair));
            }
            foreach (int j in freeGrid.Near(a.Point, 2 * search))
            {
                if (j <= i) continue;
                var b = free[j];
                if (a.Segment == b.Segment) continue;
                var delta = b.Point - a.Point;
                if (a.Out.Dot(b.Out) <= -cosCollinear)
                {
                    double gap = delta.Dot(a.Out);
                    if (gap <= tolerance || gap > search || delta.Dot(b.Out) >= 0 ||
                        Math.Abs(Cross(delta, a.Out)) > tolerance || Math.Abs(Cross(delta, b.Out)) > tolerance)
                        continue;
                    candidates.Add(new(GapKind.EnLinea, a, b, null, (a.Point + b.Point) * 0.5, gap * 0.5,
                        gap * 0.5, aLimit, SegmentLimit(b.Segment)));
                    continue;
                }
                if (Math.Abs(a.Out.Dot(b.Out)) >= cosCollinear ||
                    !Intersect(a.Point, a.Out, b.Point, b.Out, out double da, out double db) ||
                    da <= 0 || db <= 0 || da > search || db > search)
                    continue;
                candidates.Add(new(GapKind.Esquina, a, b, null, a.Point + a.Out * da, da, db, aLimit, SegmentLimit(b.Segment)));
            }

            P3 reach = a.Point + a.Out * search;
            foreach (var trunk in ordered)
            {
                if (Math.Min(trunk.Start.X, trunk.End.X) > Math.Max(a.Point.X, reach.X)) break;
                if (trunk == a.Segment || !BoxesMeet(a.Point, reach, trunk.Start, trunk.End, 0)) continue;
                var axis = Flat(trunk.End - trunk.Start);
                double len = axis.Length;
                if (len < 2 * minLength || Math.Abs(Cross(a.Out, axis.Normalized())) < MinTeeSine ||
                    !Intersect(a.Point, a.Out, Flat(trunk.Start), axis, out double d, out double t) ||
                    d <= 0 || d > search || t * len < minLength || (1 - t) * len < minLength)
                    continue;
                candidates.Add(new(GapKind.Te, a, null, trunk, a.Point + a.Out * d, d, 0, aLimit, aLimit));
            }
        }

        var blocked = new HashSet<End>();
        foreach (var gap in candidates.Where(c => c.Kind == GapKind.EnLinea && c.WithinLimit))
        {
            var a = gap.First;
            var b = gap.Second!;
            if (!segments.Concat(otherLayers).Any(s => s != a.Segment && s != b.Segment &&
                Hits(a.Point, b.Point, s, tolerance))) continue;
            blocked.Add(a);
            blocked.Add(b);
        }
        var used = new HashSet<End>();
        var trunkParts = segments.ToDictionary(s => s, s => new List<NetSegment> { s });
        var lastEnds = ends.Where(e => !e.AtStart).ToDictionary(e => e.Segment);
        foreach (var c in candidates.OrderByDescending(c => c.WithinLimit).ThenBy(c => c.ExtensionMm))
        {
            var a = c.First;
            var b = c.Second;
            if (used.Contains(a) || blocked.Contains(a) || b != null && (used.Contains(b) || blocked.Contains(b))) continue;
            NetSegment? trunk = null;
            if (c.Trunk != null)
            {
                trunk = trunkParts[c.Trunk].FirstOrDefault(s =>
                {
                    var (dist, t) = NetworkBuilder.ProjectToSegment(c.Point, Flat(s.Start), Flat(s.End));
                    return dist <= tolerance && Math.Min(t, 1 - t) * s.Length >= minLength;
                });
                if (trunk == null) continue;
            }
            if (segments.Any(s => s != a.Segment && s != b?.Segment && s != trunk && c.JunctionEnds?.Any(e => e.Segment == s) != true &&
                    (Hits(a.Point, c.Point, s, tolerance) || b != null && Hits(b.Point, c.Point, s, tolerance))))
                continue;
            if (c.Kind == GapKind.EnLinea && otherLayers.Any(s => Hits(a.Point, b!.Point, s, tolerance))) continue;

            string? rejection = null;
            if (c.WithinLimit && validate != null)
            {
                var others = c.JunctionEnds?.Select(e => e.Segment) ?? new[] { (b?.Segment ?? trunk)! };
                rejection = others.Select(other => validate(new(c.Kind, a.Segment, other, c.Point))).FirstOrDefault(r => r != null);
            }
            bool closed = c.WithinLimit && rejection == null;
            result.Gaps.Add(new(a.Segment.Layer, c.Kind, c.Point, c.ExtensionMm, c.LimitMm, closed, rejection));
            if (!closed) continue;
            Extend(a, c.Point);
            used.Add(a);
            if (b != null)
            {
                Extend(b, c.Point);
                used.Add(b);
            }
            if (trunk != null)
            {
                var split = new NetSegment { Index = trunk.Index, Layer = trunk.Layer, WidthMm = trunk.WidthMm, Start = c.Point, End = trunk.End };
                trunk.End = c.Point;
                segments.Add(split);
                trunkParts[c.Trunk!].Add(split);
                if (lastEnds.Remove(trunk, out var end))
                {
                    end.Segment = split;
                    lastEnds.Add(split, end);
                }
                result.SplitAtTee++;
            }
        }
    }

    private static void Extend(End end, P3 p)
    {
        var s = end.Segment;
        if (end.AtStart) s.Start = p with { Z = s.Start.Z };
        else s.End = p with { Z = s.End.Z };
    }

    private static P3 Flat(P3 p) => p with { Z = 0 };
    private static double Cross(P3 a, P3 b) => a.X * b.Y - a.Y * b.X;

    private static bool Intersect(P3 a, P3 u, P3 b, P3 v, out double ta, out double tb)
    {
        double det = Cross(u, v);
        ta = tb = 0;
        if (Math.Abs(det) <= ParallelEpsilon) return false;
        P3 delta = b - a;
        ta = Cross(delta, v) / det;
        tb = Cross(delta, u) / det;
        return true;
    }

    private static bool BoxesMeet(P3 a, P3 b, P3 c, P3 d, double tol) =>
        Math.Max(a.X, b.X) + tol >= Math.Min(c.X, d.X) && Math.Max(c.X, d.X) + tol >= Math.Min(a.X, b.X) &&
        Math.Max(a.Y, b.Y) + tol >= Math.Min(c.Y, d.Y) && Math.Max(c.Y, d.Y) + tol >= Math.Min(a.Y, b.Y);

    private static bool Hits(P3 from, P3 to, NetSegment s, double tol)
    {
        if (!BoxesMeet(from, to, s.Start, s.End, tol)) return false;
        P3 a = Flat(s.Start), b = Flat(s.End), u = to - from, v = b - a;
        if (Intersect(from, u, a, v, out double t, out double q) && t >= 0 && t <= 1 && q >= 0 && q <= 1)
            return true;
        return NetworkBuilder.ProjectToSegment(to, a, b).Dist <= tol ||
            NetworkBuilder.ProjectToSegment(a, from, to).Dist <= tol ||
            NetworkBuilder.ProjectToSegment(b, from, to).Dist <= tol;
    }
}
