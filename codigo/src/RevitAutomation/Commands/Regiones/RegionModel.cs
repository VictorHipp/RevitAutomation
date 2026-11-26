using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace RevitAutomation.Addin.Commands.Regiones;

// REGION: pure logic (no Revit API) to find the closed outlines of a linked CAD drawing. The Revit adapter
// (RegionCommand) turns each geometry object of the link into RegionPiece values; this file decides which pieces
// form a closed region. Nothing is invented: a loop is accepted only when it is unambiguous.

/// <summary>Point in the plan (feet, Revit internal units; Z is ignored).</summary>
internal readonly struct RegionPoint
{
    internal RegionPoint(double x, double y) { X = x; Y = y; }
    internal double X { get; }
    internal double Y { get; }
    internal double DistanceTo(RegionPoint o) => Math.Sqrt((X - o.X) * (X - o.X) + (Y - o.Y) * (Y - o.Y));
    public override string ToString() => $"({X:0.###}, {Y:0.###})";
}

/// <summary>
/// One geometry object of the CAD link. <see cref="Points"/> are its vertices in order (for an arc or spline, a few
/// points along it, used only for the area and duplicate checks). A polyline (PolyLine in Revit) is closed when its last
/// point repeats the first one; a single line or arc is never closed by itself and may be chained with others.
/// </summary>
internal sealed class RegionPiece
{
    internal RegionPiece(int id, string layer, IReadOnlyList<RegionPoint> points, bool isPolyline)
    {
        Id = id; Layer = layer ?? ""; Points = points; IsPolyline = isPolyline;
    }
    internal int Id { get; }
    internal string Layer { get; }
    internal IReadOnlyList<RegionPoint> Points { get; }
    internal bool IsPolyline { get; }
    internal RegionPoint Start => Points[0];
    internal RegionPoint End => Points[Points.Count - 1];
}

/// <summary>One step of a loop: a piece, walked forwards or backwards.</summary>
internal readonly struct RegionStep
{
    internal RegionStep(int pieceId, bool reversed) { PieceId = pieceId; Reversed = reversed; }
    internal int PieceId { get; }
    internal bool Reversed { get; }
}

/// <summary>A closed outline found in the CAD, ready to become one filled region.</summary>
internal sealed class RegionLoop
{
    internal RegionLoop(string layer, IReadOnlyList<RegionStep> steps, IReadOnlyList<RegionPoint> outline, bool fromPolyline)
    {
        Layer = layer; Steps = steps; Outline = outline; FromPolyline = fromPolyline;
        AreaSquareFeet = Math.Abs(RegionGeometry.SignedArea(outline));
    }
    internal string Layer { get; }
    internal IReadOnlyList<RegionStep> Steps { get; }
    /// <summary>Vertices without the closing repetition.</summary>
    internal IReadOnlyList<RegionPoint> Outline { get; }
    internal bool FromPolyline { get; }
    internal double AreaSquareFeet { get; }
    internal double AreaSquareMeters => AreaSquareFeet * 0.09290304;
}

/// <summary>Result of the search, per layer, with what could not be used and why.</summary>
internal sealed class RegionScan
{
    internal List<RegionLoop> Loops { get; } = new();
    /// <summary>Open polylines, loose lines that do not close, or chains with junctions of 3+ pieces.</summary>
    internal Dictionary<string, int> OpenPieces { get; } = new(StringComparer.OrdinalIgnoreCase);
    /// <summary>Lines that meet 3 or more others at a point: the outline is ambiguous and is not guessed.</summary>
    internal Dictionary<string, int> AmbiguousPieces { get; } = new(StringComparer.OrdinalIgnoreCase);
    internal Dictionary<string, int> Duplicates { get; } = new(StringComparer.OrdinalIgnoreCase);
    internal Dictionary<string, int> TooSmall { get; } = new(StringComparer.OrdinalIgnoreCase);

    internal IEnumerable<string> Layers => Loops.Select(l => l.Layer)
        .Concat(OpenPieces.Keys).Concat(AmbiguousPieces.Keys).Concat(Duplicates.Keys).Concat(TooSmall.Keys)
        .Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(l => l, StringComparer.CurrentCultureIgnoreCase);

    internal static void Add(Dictionary<string, int> d, string layer, int n = 1)
    {
        d.TryGetValue(layer, out int v);
        d[layer] = v + n;
    }
    internal static int Get(Dictionary<string, int> d, string layer) => d.TryGetValue(layer, out int v) ? v : 0;
}

internal static class RegionGeometry
{
    /// <summary>Endpoint tolerance: 1 mm in feet (CAD outlines are usually exact; snaps of a few tenths of mm are common).</summary>
    internal const double DefaultTolerance = 1.0 / 304.8;
    /// <summary>Outlines smaller than 0,01 m² (10 x 10 cm) are taken as drawing noise.</summary>
    internal const double MinAreaSquareFeet = 0.01 / 0.09290304;

    internal static double SignedArea(IReadOnlyList<RegionPoint> p)
    {
        double a = 0;
        for (int i = 0; i < p.Count; i++)
        {
            var q = p[(i + 1) % p.Count];
            a += p[i].X * q.Y - q.X * p[i].Y;
        }
        return a / 2;
    }

    /// <summary>
    /// Finds the closed outlines. Closed polylines are accepted as they are (two regions sharing a vertex stay two
    /// regions). Loose lines and arcs of the same layer are chained only where every end meets exactly one other end;
    /// junctions of three or more ends are left out (reported) instead of guessing the outline.
    /// </summary>
    internal static RegionScan Find(IEnumerable<RegionPiece> pieces, double tolerance = DefaultTolerance)
    {
        var scan = new RegionScan();
        var seen = new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase);
        var loose = new List<RegionPiece>();

        foreach (var piece in pieces)
        {
            if (piece.Points.Count < 2) continue;
            if (piece.IsPolyline && piece.Points.Count >= 4 && piece.Start.DistanceTo(piece.End) <= tolerance)
            {
                var outline = piece.Points.Take(piece.Points.Count - 1).ToList();
                Accept(scan, seen, new RegionLoop(piece.Layer, new[] { new RegionStep(piece.Id, false) }, outline, true), tolerance);
            }
            else loose.Add(piece);
        }

        foreach (var layer in loose.GroupBy(p => p.Layer, StringComparer.OrdinalIgnoreCase))
            Chain(scan, seen, layer.Key, layer.ToList(), tolerance);
        return scan;
    }

    private static void Chain(RegionScan scan, Dictionary<string, HashSet<string>> seen, string layer, List<RegionPiece> pieces, double tol)
    {
        // Only representative nodes are indexed: retain the original first-created match,
        // rather than joining tolerance chains transitively or choosing the nearest point.
        var nodes = new List<RegionPoint>();
        var grid = new Dictionary<(long X, long Y), List<int>>();
        bool CanIndex(RegionPoint p) => double.IsFinite(p.X) && double.IsFinite(p.Y)
            && Math.Abs(p.X / tol) < 1e14 && Math.Abs(p.Y / tol) < 1e14;
        bool indexed = double.IsFinite(tol) && tol >= 1e-100
            && pieces.All(p => CanIndex(p.Start) && CanIndex(p.End));
        int NodeOf(RegionPoint p)
        {
            long x = 0, y = 0;
            if (indexed)
            {
                x = (long)Math.Floor(p.X / tol); y = (long)Math.Floor(p.Y / tol);
                int first = int.MaxValue;
                // Extra neighbour cell conservatively covers floating-point division at a boundary.
                for (long dx = -2; dx <= 2; dx++)
                    for (long dy = -2; dy <= 2; dy++)
                        if (grid.TryGetValue((x + dx, y + dy), out var bucket))
                            foreach (int i in bucket)
                                if (i < first && nodes[i].DistanceTo(p) <= tol) first = i;
                if (first != int.MaxValue) return first;
            }
            else
                for (int i = 0; i < nodes.Count; i++) if (nodes[i].DistanceTo(p) <= tol) return i;
            int id = nodes.Count;
            nodes.Add(p);
            if (indexed)
            {
                if (!grid.TryGetValue((x, y), out var bucket)) grid[(x, y)] = bucket = new List<int>();
                bucket.Add(id);
            }
            return id;
        }
        var ends = new List<(int A, int B)>();
        var incident = new Dictionary<int, List<int>>();
        for (int i = 0; i < pieces.Count; i++)
        {
            int a = NodeOf(pieces[i].Start), b = NodeOf(pieces[i].End);
            ends.Add((a, b));
            if (a == b) continue; // a single closed curve (e.g. a full circle) is handled below
            foreach (int n in new[] { a, b })
            {
                if (!incident.TryGetValue(n, out var list)) incident[n] = list = new List<int>();
                list.Add(i);
            }
        }

        var used = new bool[pieces.Count];
        var expanded = new bool[nodes.Count];
        for (int i = 0; i < pieces.Count; i++)
        {
            if (ends[i].A != ends[i].B || pieces[i].Points.Count < 3) continue;
            used[i] = true; // closed by itself (circle, closed spline): one region
            Accept(scan, seen, new RegionLoop(layer, new[] { new RegionStep(pieces[i].Id, false) },
                pieces[i].Points.Take(pieces[i].Points.Count - 1).ToList(), false), tol);
        }

        for (int start = 0; start < pieces.Count; start++)
        {
            if (used[start] || ends[start].A == ends[start].B) continue;
            // Collect the connected component and check that it is a simple cycle (every node has exactly 2 ends).
            var component = new List<int>();
            var stack = new Stack<int>();
            stack.Push(start);
            used[start] = true;
            while (stack.Count > 0)
            {
                int i = stack.Pop();
                component.Add(i);
                foreach (int n in new[] { ends[i].A, ends[i].B })
                {
                    if (expanded[n]) continue;
                    expanded[n] = true;
                    foreach (int j in incident[n])
                        if (!used[j]) { used[j] = true; stack.Push(j); }
                }
            }
            var componentNodes = component.SelectMany(i => new[] { ends[i].A, ends[i].B }).Distinct().ToList();
            bool junction = componentNodes.Any(n => incident[n].Count > 2);
            bool open = componentNodes.Any(n => incident[n].Count < 2);
            if (junction) { RegionScan.Add(scan.AmbiguousPieces, layer, component.Count); continue; }
            if (open) { RegionScan.Add(scan.OpenPieces, layer, component.Count); continue; }

            // Walk the cycle.
            var steps = new List<RegionStep>();
            var outline = new List<RegionPoint>();
            int current = component[0], node = ends[current].A;
            for (int k = 0; k < component.Count; k++)
            {
                bool reversed = ends[current].A != node;
                steps.Add(new RegionStep(pieces[current].Id, reversed));
                var pts = reversed ? pieces[current].Points.Reverse().ToList() : pieces[current].Points.ToList();
                outline.AddRange(pts.Take(pts.Count - 1));
                node = reversed ? ends[current].A : ends[current].B;
                int prev = current;
                current = incident[node].First(j => j != prev);
            }
            Accept(scan, seen, new RegionLoop(layer, steps, outline, false), tol);
        }
    }

    private static void Accept(RegionScan scan, Dictionary<string, HashSet<string>> seen, RegionLoop loop, double tol)
    {
        if (loop.Outline.Count < 3 || loop.AreaSquareFeet < MinAreaSquareFeet) { RegionScan.Add(scan.TooSmall, loop.Layer); return; }
        if (!seen.TryGetValue(loop.Layer, out var keys)) seen[loop.Layer] = keys = new HashSet<string>(StringComparer.Ordinal);
        if (!keys.Add(Signature(loop.Outline, tol))) { RegionScan.Add(scan.Duplicates, loop.Layer); return; }
        scan.Loops.Add(loop);
    }

    /// <summary>
    /// Same vertex sequence with any start or direction → same signature (an outline drawn twice). The order is kept:
    /// two different outlines through the same points are not duplicates.
    /// </summary>
    internal static string Signature(IReadOnlyList<RegionPoint> outline, double tol)
    {
        double g = Math.Max(tol, 1e-9) * 2;
        var keys = new List<string>();
        foreach (var p in outline)
        {
            string k = $"{Math.Round(p.X / g)}:{Math.Round(p.Y / g)}";
            if (keys.Count == 0 || keys[keys.Count - 1] != k) keys.Add(k);
        }
        if (keys.Count > 1 && keys[0] == keys[keys.Count - 1]) keys.RemoveAt(keys.Count - 1);
        // Booth's minimum rotation over whole vertex tokens. Include the separator in
        // comparisons: ordinal order of a key that prefixes another must match string.Join.
        string MinimumRotation(List<string> seq)
        {
            int n = seq.Count;
            if (n == 0) return "";
            var tokens = seq.Select(k => k + "|").ToArray();
            int a = 0, b = 1, offset = 0;
            while (a < n && b < n && offset < n)
            {
                int comparison = string.CompareOrdinal(tokens[(a + offset) % n], tokens[(b + offset) % n]);
                if (comparison == 0) { offset++; continue; }
                if (comparison > 0) { a += offset + 1; if (a <= b) a = b + 1; }
                else { b += offset + 1; if (b <= a) b = a + 1; }
                offset = 0;
            }
            int start = Math.Min(a, b);
            return string.Join("|", seq.Skip(start).Concat(seq.Take(start)));
        }
        string forward = MinimumRotation(keys);
        keys.Reverse();
        string reverse = MinimumRotation(keys);
        return string.CompareOrdinal(forward, reverse) <= 0 ? forward : reverse;
    }
}

internal static class RegionNaming
{
    internal const string TypePrefix = "REG - ";
    internal const string CommentPrefix = "REGION · ";

    /// <summary>Characters Revit does not accept in element names.</summary>
    private const string Forbidden = "\\:{}[]|;<>?`~";

    internal static string Clean(string layer)
    {
        var sb = new StringBuilder();
        foreach (char c in (layer ?? "").Trim()) sb.Append(Forbidden.IndexOf(c) >= 0 || char.IsControl(c) ? '_' : c);
        string s = sb.ToString().Trim();
        return s.Length == 0 ? "sin nombre" : s;
    }

    /// <summary>Name of the filled-region type for a CAD layer: «REG - &lt;capa&gt;».</summary>
    internal static string TypeName(string layer) => TypePrefix + Clean(layer);

    /// <summary>
    /// One distinct type name per layer: layers that clean to the same name («A[B» and «A]B» → «A_B») get « (2)», « (3)»…
    /// in alphabetical order of the original layer names, so each layer keeps its own colour.
    /// </summary>
    internal static Dictionary<string, string> TypeNames(IEnumerable<string> layers)
    {
        var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var taken = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (string layer in layers.Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(l => l, StringComparer.Ordinal))
        {
            string name = TypeName(layer);
            for (int n = 2; !taken.Add(name); n++) name = TypeName(layer) + " (" + n + ")";
            map[layer] = name;
        }
        return map;
    }

    /// <summary>Comments of every region created by REGION: marks it as ours (only these are replaced on a new run).</summary>
    internal static string Comment(string layer) => CommentPrefix + Clean(layer);

    internal static bool IsOurs(string? comment) => comment != null && comment.StartsWith(CommentPrefix, StringComparison.Ordinal);
}
