using System;
using System.Collections.Generic;
using System.Linq;

namespace RevitAutomation.Addin.Commands.PipeTagging;

// Pure logic for "Etiquetar tuberías seleccionadas" (no Revit API, testable outside Revit).
// Coordinates are 2D view coordinates (feet along the view's right/up directions).

/// <summary>2D point in view coordinates.</summary>
internal readonly record struct TagPoint(double X, double Y)
{
    public static TagPoint operator +(TagPoint a, TagPoint b) => new(a.X + b.X, a.Y + b.Y);
    public static TagPoint operator -(TagPoint a, TagPoint b) => new(a.X - b.X, a.Y - b.Y);
    public static TagPoint operator *(TagPoint a, double k) => new(a.X * k, a.Y * k);
    internal double Length => Math.Sqrt(X * X + Y * Y);
    internal TagPoint Lerp(TagPoint to, double t) => new(X + (to.X - X) * t, Y + (to.Y - Y) * t);
}

/// <summary>Axis-aligned box in view coordinates. Conservative: a rotated tag is enclosed by its box.</summary>
internal readonly record struct TagBox(double MinX, double MinY, double MaxX, double MaxY)
{
    internal double Width => MaxX - MinX;
    internal double Height => MaxY - MinY;
    internal TagPoint Center => new((MinX + MaxX) / 2, (MinY + MaxY) / 2);
    internal bool IsValid => MaxX >= MinX && MaxY >= MinY && double.IsFinite(MinX) && double.IsFinite(MinY) && double.IsFinite(MaxX) && double.IsFinite(MaxY);

    internal TagBox Translate(TagPoint by) => new(MinX + by.X, MinY + by.Y, MaxX + by.X, MaxY + by.Y);

    /// <summary>True when the boxes touch or are closer than <paramref name="gap"/>.</summary>
    internal bool Overlaps(TagBox other, double gap)
        => MinX < other.MaxX + gap && other.MinX < MaxX + gap && MinY < other.MaxY + gap && other.MinY < MaxY + gap;

    internal bool Contains(TagBox inner)
        => inner.MinX >= MinX && inner.MaxX <= MaxX && inner.MinY >= MinY && inner.MaxY <= MaxY;

    internal static TagBox FromPoints(IEnumerable<TagPoint> points)
    {
        double minX = double.PositiveInfinity, minY = double.PositiveInfinity, maxX = double.NegativeInfinity, maxY = double.NegativeInfinity;
        foreach (var p in points)
        {
            minX = Math.Min(minX, p.X); minY = Math.Min(minY, p.Y);
            maxX = Math.Max(maxX, p.X); maxY = Math.Max(maxY, p.Y);
        }
        return new TagBox(minX, minY, maxX, maxY);
    }

    /// <summary>Segment-box intersection (Liang-Barsky), with the box grown by <paramref name="gap"/>.</summary>
    internal bool IntersectsSegment(TagPoint a, TagPoint b, double gap)
    {
        double t0 = 0, t1 = 1, dx = b.X - a.X, dy = b.Y - a.Y;
        bool Clip(double p, double q)
        {
            if (Math.Abs(p) < 1e-12) return q >= 0;
            double r = q / p;
            if (p < 0) { if (r > t1) return false; if (r > t0) t0 = r; }
            else { if (r < t0) return false; if (r < t1) t1 = r; }
            return true;
        }
        return Clip(-dx, a.X - (MinX - gap)) && Clip(dx, (MaxX + gap) - a.X)
            && Clip(-dy, a.Y - (MinY - gap)) && Clip(dy, (MaxY + gap) - a.Y)
            && t0 <= t1;
    }
}

/// <summary>
/// Kind of a selected element. The names come from the first (pipe) tool; for ducts the same values mean
/// straight duct, duct placeholder, duct fitting, duct accessory and flex duct.
/// </summary>
internal enum PipeTagElementKind { Pipe, PipePlaceholder, PipeFitting, PipeAccessory, FlexPipe, Other }

/// <summary>Words shown to the user for each MEP family of runs (pipes, ducts or cable trays).</summary>
internal sealed record MepTagNouns(
    string Singular, string Plural, string Indefinite, string Flexible, string AccessoryExamples, string TagCategoryName,
    string DefinitePlural, string TagName)
{
    internal static readonly MepTagNouns Pipe = new("tubería", "tuberías", "una tubería", "una tubería flexible", "válvula, filtro…",
        "Etiquetas de tubería", "las tuberías", "etiqueta de tubería");
    internal static readonly MepTagNouns Duct = new("conducto", "conductos", "un conducto", "un conducto flexible", "compuerta, silenciador…",
        "Etiquetas de conducto", "los conductos", "etiqueta de conducto");
    // Cable trays have neither accessories nor flexible runs: those two texts are never shown.
    internal static readonly MepTagNouns CableTray = new("bandeja de cables", "bandejas de cables", "una bandeja de cables", "una bandeja flexible", "—",
        "Etiquetas de bandeja de cables", "las bandejas de cables", "etiqueta de bandeja de cables");
}

internal enum PipeTagPlacementKind { OnPipe, AlongPipe, OffsetWithLeader, Beside }

/// <summary>One tag to place: the pipe projected on the view and the tag box measured in Revit at its first head.</summary>
internal sealed class PipeTagRequest
{
    internal string Key { get; init; } = "";
    internal TagPoint Start { get; init; }
    internal TagPoint End { get; init; }
    internal TagPoint MeasuredHead { get; init; }
    internal TagBox MeasuredBox { get; init; }
    internal double ProjectedLength => (End - Start).Length;
}

internal sealed class PipeTagPlacement
{
    internal string Key { get; init; } = "";
    internal TagPoint Head { get; init; }
    internal TagBox Box { get; init; }
    internal PipeTagPlacementKind Kind { get; init; }
    internal TagPoint? LeaderEnd { get; init; }
    internal double PipeParameter { get; init; }
    /// <summary>
    /// Symbol of the tagged element (point families). The tag box must not cover it, but its leader may enter it
    /// because the leader points at the element. Null for pipes, ducts and trays.
    /// </summary>
    internal TagBox? Target { get; init; }
    /// <summary>Signed distance of the head from the run's axis (feet, + = upper side). 0 for point families.</summary>
    internal double AxisOffset { get; init; }
}

/// <summary>A modelled run (pipe, duct, tray, conduit) seen in the view: tags must not cover it. Leaders may cross it.</summary>
internal readonly record struct RunSegment(TagPoint A, TagPoint B, double HalfWidth);

internal static class PipeTagRules
{
    internal const double MinLengthMetres = 0.15;
    internal const double MinLengthToleranceMetres = 1e-6;
    /// <summary>Clearance between tags on paper, in millimetres.</summary>
    internal const double PaperGapMm = 1.0;
    /// <summary>Pipes within this angle of the view axes get a horizontal or vertical tag.</summary>
    internal const double OrthogonalToleranceDegrees = 1.0;
    /// <summary>
    /// Run tags (pipe, duct, cable tray): head 20 cm above the run's axis, in model units, on the upper side of the view
    /// (towards +Y; to the left for vertical runs). If there it would cover another tag, a text or any modelled run,
    /// it goes up just enough (in 5 cm steps, up to 100 cm, with a leader); if the upper side is full, the same below the axis.
    /// </summary>
    internal const double AboveAxisMetres = 0.20;
    internal const double MaxAxisOffsetMetres = 1.00;
    internal const double AxisOffsetStepMetres = 0.05;
    internal const double AboveAxisToleranceMetres = 0.005;
    internal static double AboveAxisFeet => AboveAxisMetres / 0.3048;

    /// <summary>Head distances from the axis tried on each side, in feet: 20, 25, … 100 cm.</summary>
    internal static IEnumerable<double> AxisOffsetsFeet()
    {
        int steps = (int)Math.Round((MaxAxisOffsetMetres - AboveAxisMetres) / AxisOffsetStepMetres);
        for (int i = 0; i <= steps; i++) yield return (AboveAxisMetres + i * AxisOffsetStepMetres) / 0.3048;
    }

    /// <summary>Text for the report: "20 cm sobre el eje", "45 cm bajo el eje"…</summary>
    internal static string AxisOffsetText(double signedFeet)
        => $"{Math.Abs(signedFeet) * 30.48:0.#} cm {(signedFeet >= 0 ? "sobre" : "bajo")} el eje";

    /// <summary>"Superior a 15 cm": strictly longer, with a tolerance for unit conversion noise.</summary>
    internal static bool IsLongEnough(double lengthMetres)
        => double.IsFinite(lengthMetres) && lengthMetres > MinLengthMetres + MinLengthToleranceMetres;

    /// <summary>Why an element is not tagged, or null when it must be tagged. <paramref name="nouns"/> defaults to pipes.</summary>
    internal static string? ExclusionReason(PipeTagElementKind kind, double lengthMetres, bool alreadyTagged, double projectedLengthFeet, MepTagNouns? nouns = null)
    {
        var n = nouns ?? MepTagNouns.Pipe;
        switch (kind)
        {
            case PipeTagElementKind.PipeFitting: return "Es un accesorio de unión (codo, te, cruz, reducción…): solo se etiquetan tramos rectos.";
            case PipeTagElementKind.PipeAccessory: return $"Es un accesorio de {n.Singular} ({n.AccessoryExamples}): solo se etiquetan tramos rectos.";
            case PipeTagElementKind.FlexPipe: return $"Es {n.Flexible}: solo se etiquetan tramos rectos.";
            case PipeTagElementKind.PipePlaceholder: return $"Es un marcador de posición de {n.Singular}, no {n.Indefinite} real.";
            case PipeTagElementKind.Other: return $"No es {n.Indefinite}: solo se etiquetan tramos rectos de {n.Singular}.";
        }
        if (!double.IsFinite(lengthMetres)) return "No se puede leer la longitud del tramo.";
        if (!IsLongEnough(lengthMetres))
            return $"Longitud {lengthMetres * 100:0.#} cm: solo se etiquetan tramos de más de {MinLengthMetres * 100:0} cm.";
        if (alreadyTagged) return $"Ya tiene una {n.TagName} en esta vista; no se duplica.";
        if (!(projectedLengthFeet > 1e-6)) return "El tramo se ve de punta en esta vista (es perpendicular al plano de la vista); no hay dónde colocar la etiqueta.";
        return null;
    }

    internal static double PaperGapFeet(int viewScale) => PaperGapMm / 304.8 * Math.Max(1, viewScale);

    /// <summary>0 = horizontal, 1 = vertical, -1 = diagonal, for a direction in view coordinates.</summary>
    internal static int Orthogonality(TagPoint direction)
    {
        if (direction.Length < 1e-12) return -1;
        double degrees = Math.Abs(Math.Atan2(direction.Y, direction.X) * 180 / Math.PI) % 180;
        if (degrees <= OrthogonalToleranceDegrees || degrees >= 180 - OrthogonalToleranceDegrees) return 0;
        if (Math.Abs(degrees - 90) <= OrthogonalToleranceDegrees) return 1;
        return -1;
    }
}


// A bounded broad phase only: the existing predicates below still decide every collision.
// Kept in this file so the standalone harness and the add-in share the same implementation.
internal sealed class TagSpatialList<T> : IReadOnlyList<T>
{
    private const double CellSize = 4;
    private readonly List<T> items = new();
    private readonly Dictionary<(long X, long Y), List<int>> cells = new();
    private readonly List<int> overflow = new();
    private readonly Func<T, TagBox> bounds;
    internal TagSpatialList(IEnumerable<T> source, Func<T, TagBox> bounds)
    {
        this.bounds = bounds;
        foreach (var item in source) Add(item);
    }
    public int Count => items.Count;
    public T this[int index] => items[index];
    public IEnumerator<T> GetEnumerator() => items.GetEnumerator();
    System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator() => GetEnumerator();

    private static bool Range(TagBox b, int maximum, out long x0, out long y0, out long x1, out long y1)
    {
        x0 = y0 = x1 = y1 = 0;
        if (!b.IsValid) return false;
        // Stay far from integer overflow; exceptional coordinates use the original full scan.
        double a = Math.Floor(b.MinX / CellSize), c = Math.Floor(b.MaxX / CellSize);
        double d = Math.Floor(b.MinY / CellSize), e = Math.Floor(b.MaxY / CellSize);
        if (Math.Abs(a) > 1e15 || Math.Abs(c) > 1e15 || Math.Abs(d) > 1e15 || Math.Abs(e) > 1e15
            || (c - a + 1) * (e - d + 1) > maximum) return false;
        x0 = (long)a; x1 = (long)c; y0 = (long)d; y1 = (long)e;
        return true;
    }
    internal void Add(T item)
    {
        int index = items.Count;
        items.Add(item);
        if (!Range(bounds(item), 256, out long x0, out long y0, out long x1, out long y1))
        { overflow.Add(index); return; }
        for (long x = x0; x <= x1; x++)
            for (long y = y0; y <= y1; y++)
            {
                if (!cells.TryGetValue((x, y), out var bucket)) cells[(x, y)] = bucket = new List<int>();
                bucket.Add(index);
            }
    }
    internal IEnumerable<T> Query(TagBox box)
    {
        if (!Range(box, 4096, out long x0, out long y0, out long x1, out long y1))
        { foreach (var item in items) yield return item; yield break; }
        foreach (int index in overflow) yield return items[index];
        var seen = new HashSet<int>();
        for (long x = x0; x <= x1; x++)
            for (long y = y0; y <= y1; y++)
                if (cells.TryGetValue((x, y), out var bucket))
                    foreach (int index in bucket)
                        if (seen.Add(index)) yield return items[index];
    }
    internal static IEnumerable<T> Near(IReadOnlyList<T> source, TagBox box)
        => source is TagSpatialList<T> index ? index.Query(box) : source;
}

internal static class PipeTagLayout
{
    internal static TagBox Grow(TagBox box, double gap)
    {
        double magnitude = Math.Max(Math.Max(Math.Abs(box.MinX), Math.Abs(box.MaxX)),
            Math.Max(Math.Abs(box.MinY), Math.Abs(box.MaxY)));
        double padding = Math.Max(0, gap) + 1e-9 + magnitude * 1e-14;
        return new TagBox(box.MinX - padding, box.MinY - padding, box.MaxX + padding, box.MaxY + padding);
    }
    internal static TagBox SegmentBounds(TagPoint a, TagPoint b)
        => new(Math.Min(a.X, b.X), Math.Min(a.Y, b.Y), Math.Max(a.X, b.X), Math.Max(a.Y, b.Y));
    internal static TagBox PlacementBounds(PipeTagPlacement placement)
    {
        var b = placement.Box;
        if (Leader(placement) is { } l)
            return new TagBox(Math.Min(b.MinX, l.To.X), Math.Min(b.MinY, l.To.Y),
                Math.Max(b.MaxX, l.To.X), Math.Max(b.MaxY, l.To.Y));
        return b;
    }
    internal static TagSpatialList<TagBox> IndexObstacles(IReadOnlyList<TagBox> obstacles)
        => new(obstacles, b => b);
    internal static TagSpatialList<PipeTagPlacement> IndexPlacements()
        => new(Array.Empty<PipeTagPlacement>(), PlacementBounds);
    private static TagSpatialList<RunSegment>? IndexRuns(IReadOnlyList<RunSegment>? runs)
        => runs is null ? null : new(runs, r => Grow(SegmentBounds(r.A, r.B), r.HalfWidth));

    /// <summary>Positions along the pipe, from the middle outwards, so tags stay centred when there is room.</summary>
    internal static readonly double[] AlongParameters = { 0.5, 0.375, 0.625, 0.25, 0.75, 0.125, 0.875 };
    internal const int MaxLeaderSteps = 3;

    /// <summary>Unit normal of the pipe in the view, on the "upper" side (towards +Y, or -X for vertical pipes).</summary>
    internal static TagPoint UpperNormal(TagPoint start, TagPoint end)
    {
        var d = end - start;
        double length = d.Length;
        if (length < 1e-12) return new TagPoint(0, 1);
        var n = new TagPoint(-d.Y / length, d.X / length);
        if (n.Y < -1e-9 || (Math.Abs(n.Y) <= 1e-9 && n.X > 0)) n = n * -1;
        return n;
    }

    /// <summary>Box of the tag when its head is moved to <paramref name="head"/> (rigid translation of the measured box).</summary>
    internal static TagBox BoxAt(PipeTagRequest request, TagPoint head) => request.MeasuredBox.Translate(head - request.MeasuredHead);

    /// <summary>
    /// Ordered candidates, all on the upper side of the run: head 20 cm above the axis at the middle, then 20 cm above
    /// it further along the run, then (only if that row is full) further up with a leader to the axis.
    /// </summary>
    internal static IEnumerable<PipeTagPlacement> Candidates(PipeTagRequest request, double gap)
    {
        var normal = UpperNormal(request.Start, request.End);
        double first = PipeTagRules.AboveAxisFeet;
        // Upper side first (closest distance first, then along the run), then the same below the axis.
        // The measured head sits on the pipe midpoint: keep the family's own offset between head and box.
        foreach (double side in new[] { 1.0, -1.0 })
            foreach (double distance in PipeTagRules.AxisOffsetsFeet())
            {
                bool atFirstRow = Math.Abs(distance - first) < 1e-9;
                foreach (double t in AlongParameters)
                {
                    var anchor = request.Start.Lerp(request.End, t);
                    var head = anchor + normal * (distance * side);
                    yield return new PipeTagPlacement
                    {
                        Key = request.Key, Head = head, Box = BoxAt(request, head), PipeParameter = t, AxisOffset = distance * side,
                        // At 20 cm the tag clearly belongs to its run; further away a leader to the axis says which run it is.
                        Kind = !atFirstRow ? PipeTagPlacementKind.OffsetWithLeader
                            : Math.Abs(t - 0.5) < 1e-9 ? PipeTagPlacementKind.OnPipe : PipeTagPlacementKind.AlongPipe,
                        LeaderEnd = atFirstRow ? null : anchor
                    };
                }
            }
    }

    /// <summary>Signed distance of <paramref name="head"/> from the run's axis, positive on the upper side (feet).</summary>
    internal static double DistanceAboveAxis(TagPoint head, TagPoint start, TagPoint end)
    {
        var n = UpperNormal(start, end);
        var d = head - start;
        return d.X * n.X + d.Y * n.Y;
    }

    /// <summary>True when <paramref name="head"/> is 20 cm (± 5 mm) above the axis.</summary>
    internal static bool IsAtAboveAxis(TagPoint head, TagPoint start, TagPoint end)
        => IsAtAxisOffset(head, start, end, PipeTagRules.AboveAxisFeet);

    /// <summary>True when <paramref name="head"/> is at <paramref name="signedOffset"/> (feet, + = above) from the axis, ± 5 mm.</summary>
    internal static bool IsAtAxisOffset(TagPoint head, TagPoint start, TagPoint end, double signedOffset)
        => Math.Abs(DistanceAboveAxis(head, start, end) - signedOffset) <= PipeTagRules.AboveAxisToleranceMetres / 0.3048;

    /// <summary>True when <paramref name="box"/> covers a modelled run (its drawn width plus <paramref name="gap"/>).</summary>
    internal static bool CoversRun(TagBox box, IReadOnlyList<RunSegment>? runs, double gap)
    {
        if (runs is null) return false;
        foreach (var run in TagSpatialList<RunSegment>.Near(runs, Grow(box, gap)))
            if (box.IntersectsSegment(run.A, run.B, run.HalfWidth + gap)) return true;
        return false;
    }

    /// <summary>Leader drawn from the tag box centre to its end on the pipe.</summary>
    internal static (TagPoint From, TagPoint To)? Leader(PipeTagPlacement placement)
        => placement.LeaderEnd is { } end ? (placement.Box.Center, end) : null;

    /// <summary>True when the candidate clears every obstacle, every placed tag and every placed leader.</summary>
    internal static bool IsFree(PipeTagPlacement candidate, IReadOnlyList<TagBox> obstacles, IReadOnlyList<PipeTagPlacement> placed, double gap, TagBox? limit,
        IReadOnlyList<RunSegment>? runs = null)
    {
        if (!candidate.Box.IsValid) return false;
        if (limit is { } crop && !crop.Contains(candidate.Box)) return false;
        if (CoversRun(candidate.Box, runs, gap)) return false;
        var leader = Leader(candidate);
        foreach (var obstacle in TagSpatialList<TagBox>.Near(obstacles, Grow(PlacementBounds(candidate), gap)))
        {
            if (candidate.Box.Overlaps(obstacle, gap)) return false;
            bool ownTarget = candidate.Target is { } target && target == obstacle;
            if (!ownTarget && leader is { } l && obstacle.IntersectsSegment(l.From, l.To, 0)) return false;
        }
        foreach (var other in TagSpatialList<PipeTagPlacement>.Near(placed, Grow(PlacementBounds(candidate), gap)))
        {
            if (candidate.Box.Overlaps(other.Box, gap)) return false;
            if (leader is { } l && other.Box.IntersectsSegment(l.From, l.To, 0)) return false;
            if (Leader(other) is { } ol && candidate.Box.IntersectsSegment(ol.From, ol.To, 0)) return false;
        }
        return true;
    }

    /// <summary>First free candidate, or null when the tag cannot be placed without overlapping.</summary>
    internal static PipeTagPlacement? Place(PipeTagRequest request, IReadOnlyList<TagBox> obstacles, IReadOnlyList<PipeTagPlacement> placed, double gap, TagBox? limit,
        IReadOnlyList<RunSegment>? runs = null)
    {
        // Only runs that any candidate could reach (large models have thousands of segments).
        List<RunSegment>? near = null;
        if (runs is not null)
        {
            var b = request.MeasuredBox.Translate(request.Start.Lerp(request.End, 0.5) - request.MeasuredHead);
            double reach = PipeTagRules.MaxAxisOffsetMetres / 0.3048 + Math.Max(b.Width, b.Height) + Math.Abs((b.Center - request.Start.Lerp(request.End, 0.5)).Length) + gap;
            var zone = new TagBox(Math.Min(request.Start.X, request.End.X) - reach, Math.Min(request.Start.Y, request.End.Y) - reach,
                Math.Max(request.Start.X, request.End.X) + reach, Math.Max(request.Start.Y, request.End.Y) + reach);
            near = TagSpatialList<RunSegment>.Near(runs, Grow(zone, 0)).Where(r => zone.IntersectsSegment(r.A, r.B, r.HalfWidth)).ToList();
        }
        return Candidates(request, gap)
            .Select(candidate => LeaderIfRunBetween(request, candidate, near))
            .FirstOrDefault(candidate => IsFree(candidate, obstacles, placed, gap, limit, near));
    }

    /// <summary>
    /// A tag without leader is read as belonging to the nearest run below it: if another run lies between the tag and
    /// its own axis, the same position gets a leader to its own run so nobody reads it as the other run's tag.
    /// </summary>
    internal static PipeTagPlacement LeaderIfRunBetween(PipeTagRequest request, PipeTagPlacement candidate, IReadOnlyList<RunSegment>? runs)
    {
        if (candidate.LeaderEnd is not null || runs is null) return candidate;
        var anchor = request.Start.Lerp(request.End, candidate.PipeParameter);
        var to = candidate.Box.Center;
        foreach (var run in runs)
        {
            if (DistanceToSegment(anchor, run.A, run.B) <= run.HalfWidth + 1e-6) continue; // its own run (or one touching it)
            if (DistanceBetweenSegments(anchor, to, run.A, run.B) <= run.HalfWidth)
                return new PipeTagPlacement
                {
                    Key = candidate.Key, Head = candidate.Head, Box = candidate.Box, PipeParameter = candidate.PipeParameter,
                    AxisOffset = candidate.AxisOffset, Kind = PipeTagPlacementKind.OffsetWithLeader, LeaderEnd = anchor
                };
        }
        return candidate;
    }

    internal static double DistanceToSegment(TagPoint p, TagPoint a, TagPoint b)
    {
        var ab = b - a;
        double len2 = ab.X * ab.X + ab.Y * ab.Y;
        double t = len2 < 1e-18 ? 0 : Math.Max(0, Math.Min(1, ((p.X - a.X) * ab.X + (p.Y - a.Y) * ab.Y) / len2));
        return (p - a.Lerp(b, t)).Length;
    }

    internal static double DistanceBetweenSegments(TagPoint p1, TagPoint p2, TagPoint q1, TagPoint q2)
    {
        static double Cross(TagPoint o, TagPoint a, TagPoint b) => (a.X - o.X) * (b.Y - o.Y) - (a.Y - o.Y) * (b.X - o.X);
        double d1 = Cross(q1, q2, p1), d2 = Cross(q1, q2, p2), d3 = Cross(p1, p2, q1), d4 = Cross(p1, p2, q2);
        if (((d1 > 0 && d2 < 0) || (d1 < 0 && d2 > 0)) && ((d3 > 0 && d4 < 0) || (d3 < 0 && d4 > 0))) return 0;
        return Math.Min(Math.Min(DistanceToSegment(p1, q1, q2), DistanceToSegment(p2, q1, q2)),
            Math.Min(DistanceToSegment(q1, p1, p2), DistanceToSegment(q2, p1, p2)));
    }

    /// <summary>
    /// Places every request in a stable order (longest projected pipe first so long runs get centred tags).
    /// Returns the placements and the keys that found no free position.
    /// </summary>
    internal static (List<PipeTagPlacement> Placed, List<string> Unplaced) PlaceAll(
        IEnumerable<PipeTagRequest> requests, IReadOnlyList<TagBox> obstacles, double gap, TagBox? limit, IReadOnlyList<RunSegment>? runs = null)
    {
        var placed = new List<PipeTagPlacement>();
        var unplaced = new List<string>();
        var obstacleIndex = IndexObstacles(obstacles);
        var placedIndex = IndexPlacements();
        var runIndex = IndexRuns(runs);
        foreach (var request in requests.OrderByDescending(r => r.ProjectedLength).ThenBy(r => r.Key, StringComparer.Ordinal))
        {
            var placement = Place(request, obstacleIndex, placedIndex, gap, limit, runIndex);
            if (placement is null) unplaced.Add(request.Key);
            else { placed.Add(placement); placedIndex.Add(placement); }
        }
        return (placed, unplaced);
    }

    /// <summary>
    /// Final check on the geometry Revit really produced: returns the keys whose tag (or leader) overlaps an
    /// obstacle or an earlier tag. Earlier entries win, so removing the returned keys leaves no overlap.
    /// </summary>
    internal static List<string> Conflicts(IReadOnlyList<PipeTagPlacement> actual, IReadOnlyList<TagBox> obstacles, double gap,
        IReadOnlyList<RunSegment>? runs = null)
    {
        var kept = IndexPlacements();
        var obstacleIndex = IndexObstacles(obstacles);
        var runIndex = IndexRuns(runs);
        var conflicts = new List<string>();
        foreach (var placement in actual)
        {
            // Small tolerance: Revit boxes are rounded, so allow touching within 10 % of the gap.
            if (IsFree(placement, obstacleIndex, kept, gap * 0.9, null, runIndex)) kept.Add(placement);
            else conflicts.Add(placement.Key);
        }
        return conflicts;
    }
}

/// <summary>One straight piece of a run (a Revit pipe, duct or tray element) for the "one tag per run" rule.</summary>
internal sealed record RunPiece(string Key, double LengthMetres, double ProjectedFeet, bool Selected, bool AlreadyTagged);

/// <summary>What to do with one run (a chain of aligned pieces of the same type and size).</summary>
internal sealed class RunDecision
{
    internal List<RunPiece> Pieces { get; } = new();
    internal double LengthMetres => Pieces.Sum(p => p.LengthMetres);
    /// <summary>The piece that gets the tag (null = the run is not tagged; see <see cref="Reason"/>).</summary>
    internal RunPiece? Chosen { get; set; }
    internal string? Reason { get; set; }
}

/// <summary>
/// "One tag per run": a run is a chain of straight pieces in line, of the same type and size, joined directly or through a
/// straight coupling; elbows, tees and size changes end it. Plans converted from CAD give runs cut in many pieces, and
/// tagging each piece filled the drawing with repeated tags.
/// </summary>
internal static class PipeTagRuns
{
    /// <summary>Directions within this angle are parallel.</summary>
    internal const double ParallelDegrees = 0.5;
    /// <summary>Pieces whose axes are this close (feet, ≈ 1 cm) are on the same line.</summary>
    internal const double OnLineFeet = 0.033;

    /// <summary>True when segment B lies on the infinite line of segment A (parallel and with both ends on it).</summary>
    internal static bool SameLine((double X, double Y, double Z) a1, (double X, double Y, double Z) a2,
        (double X, double Y, double Z) b1, (double X, double Y, double Z) b2, double tolerance = OnLineFeet)
    {
        double ux = a2.X - a1.X, uy = a2.Y - a1.Y, uz = a2.Z - a1.Z, la = Math.Sqrt(ux * ux + uy * uy + uz * uz);
        double vx = b2.X - b1.X, vy = b2.Y - b1.Y, vz = b2.Z - b1.Z, lb = Math.Sqrt(vx * vx + vy * vy + vz * vz);
        if (la < 1e-9 || lb < 1e-9) return false;
        ux /= la; uy /= la; uz /= la;
        double cx = uy * vz - uz * vy, cy = uz * vx - ux * vz, cz = ux * vy - uy * vx;
        if (Math.Sqrt(cx * cx + cy * cy + cz * cz) / lb > Math.Sin(ParallelDegrees * Math.PI / 180)) return false;
        double Off((double X, double Y, double Z) p)
        {
            double dx = p.X - a1.X, dy = p.Y - a1.Y, dz = p.Z - a1.Z, t = dx * ux + dy * uy + dz * uz;
            return Math.Sqrt(Math.Pow(dx - t * ux, 2) + Math.Pow(dy - t * uy, 2) + Math.Pow(dz - t * uz, 2));
        }
        return Off(b1) <= tolerance && Off(b2) <= tolerance;
    }

    /// <summary>
    /// Groups the pieces by their links and decides each run that has selected pieces: the longest selected piece visible
    /// in length gets the tag; the run is skipped when it is already tagged (on any piece), seen end-on or too short
    /// (the 15 cm minimum applies to the whole run, not to each piece).
    /// </summary>
    internal static List<RunDecision> Decide(IReadOnlyList<RunPiece> pieces, IEnumerable<(string A, string B)> links, MepTagNouns nouns)
    {
        var parent = pieces.ToDictionary(p => p.Key, p => p.Key, StringComparer.Ordinal);
        string Root(string k) { while (parent[k] != k) { parent[k] = parent[parent[k]]; k = parent[k]; } return k; }
        foreach (var (a, b) in links)
        {
            if (!parent.ContainsKey(a) || !parent.ContainsKey(b)) continue;
            string ra = Root(a), rb = Root(b);
            if (ra != rb) parent[rb] = ra;
        }
        var runs = new List<RunDecision>();
        foreach (var group in pieces.GroupBy(p => Root(p.Key)))
        {
            var run = new RunDecision();
            run.Pieces.AddRange(group);
            if (!run.Pieces.Any(p => p.Selected)) continue;
            var visible = run.Pieces.Where(p => p.Selected && p.ProjectedFeet > 1e-6)
                .OrderByDescending(p => p.LengthMetres).ThenBy(p => p.Key, StringComparer.Ordinal).FirstOrDefault();
            if (run.Pieces.Any(p => p.AlreadyTagged))
                run.Reason = run.Pieces.Count == 1
                    ?$"Ya tiene una {nouns.TagName} en esta vista; no se duplica."
                    : $"Su tramo ya tiene una {nouns.TagName} en esta vista (en otra de sus piezas); una etiqueta por tramo, no se duplica.";
            else
                run.Reason = PipeTagRules.ExclusionReason(PipeTagElementKind.Pipe, run.LengthMetres, false, visible?.ProjectedFeet ?? 0, nouns);
            if (run.Reason is null) run.Chosen = visible;
            runs.Add(run);
        }
        return runs;
    }
}
