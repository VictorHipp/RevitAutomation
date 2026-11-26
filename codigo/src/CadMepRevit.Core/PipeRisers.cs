using System.Globalization;
using System.Text.RegularExpressions;

namespace CadMepRevit.Core;

public sealed record PipeRiser(string Layer, P3 Center, P3 Base, P3 End, double OuterMm, double? NominalMm,
    int? AttachedSegment, double Along, IReadOnlyList<int> Entities, string Reason)
{
    public string Description => $"Ø{NominalMm ?? OuterMm:0.##} mm {(NominalMm.HasValue ? "nominal (rótulo)" : "exterior (círculo)")}; " +
        $"cotas {Math.Min(Base.Z, End.Z):0.##} a {Math.Max(Base.Z, End.Z):0.##} mm SUPUESTAS; " +
        $"{(End.Z > Base.Z ? "SUBE" : "BAJA")} por {Reason}";
}

public sealed class RiserPlan
{
    public List<PipeRiser> Risers { get; } = new();
    public Dictionary<int, string> Discarded { get; } = new();
}

public static class PipeRisers
{
    private static readonly Regex Up = new(@"(?<![A-Z])(?:SUBE|MONTANTE|UP)(?![A-Z])");
    private static readonly Regex Down = new(@"(?<![A-Z])(?:BAJA|BAJANTE|DOWN)(?![A-Z])");
    private const string Hanging = "COLGAD|COLGANT|SUSPEND|OVERHEAD";
    private const string Buried = "ENTERRAD|BURIED";

    public static RiserPlan Plan(CadDrawing drawing, IReadOnlyList<NetSegment> pipes, SlopeSolution slopes,
        ISet<string> layers, double k, P3 offset, double tol, double? upperMm, double? lowerMm)
    {
        var result = new RiserPlan();
        var limits = OutlineDetector.Settings.ForPipes(k, tol);
        double join = tol + PlanDefaults.CircleCenterAllowanceMm;
        var services = layers.Concat(pipes.Select(p => p.Layer)).Distinct(StringComparer.OrdinalIgnoreCase)
            .ToDictionary(l => l, PipeNetworks.Recognize, StringComparer.OrdinalIgnoreCase);
        bool Compatible(string layer, string other) =>
            services[layer] == PipeService.Unknown || services[other] == PipeService.Unknown || services[layer] == services[other];
        var groups = new List<(P3 Center, string Layer, double Outer, List<int> Entities, HashSet<string> Layers)>();
        foreach (var c in drawing.Circles.OrderBy(c => c.Radius).ThenBy(c => c.EntityIndex))
        {
            double diameter = 2 * c.Radius * k;
            string? why = LayerKindReason(c, layers);
            if (why == null && !c.IsPlanar) why = "círculo fuera del plano horizontal";
            if (why == null && !(diameter >= limits.MinWidthMm && diameter <= limits.MaxWidthMm))
                why = $"diámetro {diameter:0.##} mm fuera de {limits.MinWidthMm:0}–{limits.MaxWidthMm:0} mm; revise las unidades";
            if (why != null) { result.Discarded[c.EntityIndex] = why; continue; }
            var center = PipeNetworks.ToMm(c.Center, k, offset);
            var group = groups.FirstOrDefault(g => g.Center.DistanceTo(center) <= join &&
                g.Layers.All(l => Compatible(l, c.Layer)));
            if (group.Entities != null)
            {
                group.Entities.Add(c.EntityIndex);
                group.Layers.Add(c.Layer);
            }
            else groups.Add((center, c.Layer, diameter, new List<int> { c.EntityIndex },
                new HashSet<string>(StringComparer.OrdinalIgnoreCase) { c.Layer }));
        }

        var targets = groups.Select((g, i) => new SizeTarget
        {
            Id = i.ToString(CultureInfo.InvariantCulture), Layer = g.Layer,
            Start = g.Center, End = g.Center, Shape = SizeShape.Redonda
        }).ToList();
        // La misma asociación por distancia, capa y directriz, sin propagar entre círculos.
        var labels = SizeAnnotations.Associate(drawing, targets, new SizeAnnotations.Settings
        {
            UnitToMm = k, OffsetMm = offset, ToleranceMm = tol, PropagateAlongRuns = false
        });
        var references = slopes.Networks.ToDictionary(n => n.Id);
        var directions = drawing.Texts.Select(t => (Text: PipeNetworks.Normalize(t.Value),
                Site: PipeNetworks.ToMm(t.Anchor ?? t.Center, k, offset)))
            .Select(t => (t.Text, t.Site, Up: Up.IsMatch(t.Text), Down: Down.IsMatch(t.Text)))
            .Where(t => t.Up != t.Down).ToList();
        bool fixtures = drawing.Blocks.Any(PipeNetworks.IsSanitaryFixture);
        bool documentBuried = SlopeSolver.IsBuried(drawing, Array.Empty<string>());
        string file = Path.GetFileNameWithoutExtension(drawing.Path);
        bool documentHanging = Regex.IsMatch(file, Hanging, RegexOptions.IgnoreCase) ||
            !Regex.IsMatch(file, Buried, RegexOptions.IgnoreCase) &&
            drawing.Texts.Any(t => Regex.IsMatch(t.Value, Hanging, RegexOptions.IgnoreCase)) &&
            !drawing.Texts.Any(t => Regex.IsMatch(t.Value, Buried, RegexOptions.IgnoreCase));
        var cuts = new Dictionary<int, List<double>>();

        for (int i = 0; i < groups.Count; i++)
        {
            var (center, layer, outer, entities, sourceLayers) = groups[i];
            var candidates = pipes.Where(p => slopes.Pipes.ContainsKey(p.Index) && sourceLayers.All(l => Compatible(l, p.Layer))).Select(p =>
            {
                var s = slopes.Pipes[p.Index];
                var a = PipeNetworks.InPlan(s.Start);
                var b = PipeNetworks.InPlan(s.End);
                var (dist, t) = NetworkBuilder.ProjectToSegment(center, a, b);
                double da = center.DistanceTo(a), db = center.DistanceTo(b);
                bool end = Math.Min(da, db) <= join;
                return (Pipe: p, Dist: end ? Math.Min(da, db) : dist, T: end ? (da <= db ? 0d : 1d) : t, AtEnd: end);
            }).ToList();
            var nearest = candidates.Where(p => p.Dist <= join)
                .OrderByDescending(p => sourceLayers.Contains(p.Pipe.Layer))
                .ThenByDescending(p => p.AtEnd).ThenBy(p => p.Dist).FirstOrDefault();
            string networkLayer = nearest.Pipe?.Layer ?? layer;
            bool layerHanging = Regex.IsMatch(networkLayer, Hanging, RegexOptions.IgnoreCase);
            bool buried = IsBuriedLayer(networkLayer, documentBuried);
            var localReference = candidates.Where(p => sourceLayers.Contains(p.Pipe.Layer))
                .OrderBy(p => p.Dist).FirstOrDefault().Pipe;
            double baseZ = localReference != null
                ? references[slopes.Pipes[localReference.Index].Network].ReferenceMm
                : buried ? PlanDefaults.BuriedTopAxisMm : PlanDefaults.HeightAboveLevelMm;
            P3 at = new(center.X, center.Y, baseZ);
            double along = nearest.T;
            if (nearest.Pipe != null)
            {
                var s = slopes.Pipes[nearest.Pipe.Index];
                double length = PipeNetworks.InPlan(s.End - s.Start).Length;
                if (along * length <= tol) along = 0;
                else if ((1 - along) * length <= tol) along = 1;
                if (along is > 0 and < 1)
                {
                    if (!cuts.TryGetValue(nearest.Pipe.Index, out var existing)) cuts[nearest.Pipe.Index] = existing = new();
                    double? same = existing.Where(t => Math.Abs(t - along) * length <= tol).Select(t => (double?)t).FirstOrDefault();
                    if (same is double t) along = t;
                    else existing.Add(along);
                }
                at = along == 0 ? s.Start : along == 1 ? s.End : s.Start + (s.End - s.Start) * along;
            }
            var direction = directions.Select(d => (d.Text, d.Up, Dist: d.Site.DistanceTo(center)))
                .Where(d => d.Dist <= PlanDefaults.RiserDirectionLabelMm).OrderBy(d => d.Dist).FirstOrDefault();
            bool up = direction.Text is not null ? direction.Up : buried || layerHanging || documentHanging || !fixtures;
            string reason = direction.Text is not null ? $"rótulo «{direction.Text}»" : buried ? "red enterrada" :
                layerHanging || documentHanging ? "red colgada" : fixtures ? "planta con aparatos" : "red colgada por defecto";
            double? level = up ? upperMm : lowerMm;
            double target = level ?? (up ? PlanDefaults.AssumedStoreyMm : -PlanDefaults.AssumedStoreyMm);
            reason += level.HasValue ? (up ? "; nivel superior" : "; nivel inferior") : $"; sin nivel adyacente, {target:0} mm respecto al nivel elegido";
            if ((target - at.Z) * (up ? 1 : -1) < PlanDefaults.MinLengthMm)
            {
                target = at.Z + (up ? PlanDefaults.AssumedStoreyMm : -PlanDefaults.AssumedStoreyMm);
                reason += $"; destino incompatible con el sentido, {PlanDefaults.AssumedStoreyMm:0} mm desde el eje";
            }
            reason += nearest.Pipe is not null ? (along is 0 or 1 ? "; base en extremo" : "; base interpolada en el tramo")
                : localReference is not null ? "; base en referencia de la red más próxima de su capa" : "; base en referencia por defecto";
            labels.Decisions.TryGetValue(targets[i].Id, out var size);
            double? nominal = IsDiameterLabel(size) ? size!.ValueMm : null;
            result.Risers.Add(new PipeRiser(layer, center, at, new P3(at.X, at.Y, target), outer, nominal, nearest.Pipe?.Index, along, entities, reason));
        }
        return result;
    }

    public static bool IsDiameterLabel(SizeDecision? decision)
    {
        var limits = OutlineDetector.Settings.ForPipes(1, PlanDefaults.ToleranceMm);
        return decision is { Source: SizeSource.Rotulo } && decision.ValueMm >= limits.MinWidthMm && decision.ValueMm <= limits.MaxWidthMm;
    }

    public static bool IsBuriedLayer(string layer, bool documentBuried) =>
        Regex.IsMatch(layer, Buried, RegexOptions.IgnoreCase) ||
        !Regex.IsMatch(layer, Hanging, RegexOptions.IgnoreCase) && documentBuried;

    private static string? LayerKindReason(CadCircle c, ISet<string> layers) =>
        layers.Contains(c.Layer) ? null : PipeNetworks.LayerKind(c.Layer) == MepKind.Tuberia
            ? "capa de tuberías sin tipo o sistema utilizable; revise el catálogo del proyecto"
            : "capa que no se modela como tubería";

    public static int Append(NetworkResult network, SlopeSolution slopes, PipeRiser riser)
    {
        int index = network.Segments.Count == 0 ? 0 : network.Segments[^1].Index + 1;
        var p = PipeNetworks.InPlan(riser.Base);
        network.Segments.Add(new NetSegment { Index = index, Layer = riser.Layer, Start = p, End = p, WidthMm = riser.OuterMm });
        int owner = riser.AttachedSegment is int from ? slopes.Pipes[from].Network : 0;
        slopes.Pipes[index] = new SlopedPipe(riser.Base, riser.End, 0, owner);
        return index;
    }

    public static void AddJunction(NetworkResult network, SlopeSolution slopes, PipeRiser riser, int vertical,
        double tolerance)
    {
        if (riser.AttachedSegment is null) return;
        var p = PipeNetworks.InPlan(riser.Base);
        var adjacent = network.Segments.Where(s => s.Index != vertical && s.Layer.Equals(riser.Layer, StringComparison.OrdinalIgnoreCase))
            .SelectMany(s => new[] { (s.Index, Start: true), (s.Index, Start: false) })
            .Where(e => slopes.Pipes.TryGetValue(e.Index, out var slope) &&
                (e.Start ? slope.Start : slope.End).DistanceTo(riser.Base) <= tolerance &&
                network.Segments[e.Index].Length > 0).ToList();
        if (adjacent.Count == 0) return;
        var joint = network.Junctions.FirstOrDefault(j => j.Layer.Equals(riser.Layer, StringComparison.OrdinalIgnoreCase) &&
            j.Point.DistanceTo(p) <= tolerance);
        var ends = joint?.Ends.ToList() ?? adjacent;
        ends.Add((vertical, true));
        if (joint != null) network.Junctions.Remove(joint);
        var added = new Junction { Layer = riser.Layer, Point = p, Kind = JunctionKind.NoAdmitida };
        added.Ends.AddRange(ends);
        if (ends.Count == 2)
        {
            added.Kind = JunctionKind.Codo;
            added.Ordered = ends;
        }
        else if (ends.Count == 3 && adjacent.Count == 2 && added.Ends.Take(2).All(endsOnAxis => adjacent.Contains(endsOnAxis)))
        {
            var a = network.Segments[ends[0].Item1].Direction * (ends[0].Item2 ? 1 : -1);
            var b = network.Segments[ends[1].Item1].Direction * (ends[1].Item2 ? 1 : -1);
            if (a.Dot(b) <= -Math.Cos(Math.PI / 180))
            {
                added.Kind = JunctionKind.Te;
                added.Ordered = ends;
            }
        }
        if (added.Kind == JunctionKind.NoAdmitida) added.Note = "La bajante y los tramos del nudo no forman un codo ni una te con principal alineada.";
        network.Junctions.Add(added);
    }

    /// <summary>Divide el eje ya calculado para compartir XYZ con cada vertical sin cambiar la pendiente.</summary>
    public static List<(int Original, NetSegment Piece)> Split(NetworkResult network, SlopeSolution slopes, IEnumerable<PipeRiser> risers)
    {
        var added = new List<(int, NetSegment)>();
        int next = network.Segments.Select(s => s.Index).DefaultIfEmpty(-1).Max() + 1;
        var byIndex = network.Segments.ToDictionary(s => s.Index);
        foreach (var group in risers.Where(r => r.AttachedSegment.HasValue && r.Along is > 0 and < 1).GroupBy(r => r.AttachedSegment!.Value))
        {
            var segment = byIndex[group.Key];
            var slope = slopes.Pipes[segment.Index];
            var cuts = group.OrderBy(r => r.Along).Select(r => r.Base).Distinct().Append(slope.End).ToList();
            var from = slope.Start;
            for (int i = 0; i < cuts.Count; i++)
            {
                var piece = i == 0 ? segment : new NetSegment { Index = next++, Layer = segment.Layer, WidthMm = segment.WidthMm };
                piece.Start = PipeNetworks.InPlan(from);
                piece.End = PipeNetworks.InPlan(cuts[i]);
                slopes.Pipes[piece.Index] = slope with { Start = from, End = cuts[i] };
                if (i != 0) added.Add((segment.Index, piece));
                from = cuts[i];
            }
            var last = added[^1].Item2.Index;
            foreach (var j in network.Junctions)
            {
                var end = (Segment: segment.Index, AtStart: false);
                int a = j.Ends.IndexOf(end), b = j.Ordered.IndexOf(end);
                if (a >= 0) j.Ends[a] = (last, false);
                if (b >= 0) j.Ordered[b] = (last, false);
            }
        }
        network.Segments.AddRange(added.Select(a => a.Item2));
        return added;
    }
}
