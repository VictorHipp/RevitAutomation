using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Mechanical;
using Autodesk.Revit.DB.Plumbing;
using CadMepRevit.Core;

namespace CadMepRevit.Revit;

public static partial class MepBuilder
{
    /// <param name="curveAt">Elemento de Revit en un extremo de un tramo del plano (el primer o el último trozo si el tramo
    /// se ha partido para seguir el techo).</param>
    private static void BuildFittings(Document doc, BuildPlan plan, Func<int, bool, MEPCurve?> curveAt,
        Func<int, bool, XYZ> endPoint, BuildReport report)
    {
        if (!plan.CreateFittings) return;
        var sampled = new HashSet<JunctionKind>();
        foreach (var joint in plan.Network.Junctions)
        {
            var kind = plan.Layers[joint.Layer].Kind;
            if (!ModelScopes.AllowsAnyFitting(plan.Scope, kind)) continue;
            if (kind == MepKind.Tuberia && joint.Ends.Count == 4 && !plan.AllowsFitting(kind, JunctionKind.Cruz))
            {
                report.PipeCrossesSkipped++;
                report.Diagnostics.Add($"Encuentro de 4 tuberías en {joint.Point}: sin unir; resuélvalo a mano (en tubería no hay cruces). Capa '{joint.Layer}'.");
                continue;
            }
            if (joint.Kind == JunctionKind.NoAdmitida)
            {
                var note = $"Unión no creada en capa '{joint.Layer}' en {joint.Point}: {joint.Note}";
                report.Diagnostics.Add(note);
                report.Problems.Add(note);
                continue;
            }
            if (!plan.AllowsFitting(kind, joint.Kind) && joint.Kind != JunctionKind.EnLinea) continue;
            string name = $"Unión {joint.Kind} en capa '{joint.Layer}' en {joint.Point}";
            string reach = string.Join(" + ", joint.Ordered.Select(e =>
                $"{plan.Network.Segments[e.Segment].Length:0} mm"));
            name += $" ({reach})";
            if (joint.Kind == JunctionKind.Transicion && joint.Note.Length > 0)
            {
                FittingFailed(report, joint.Kind, name, joint.Note);
                continue;
            }
            var participants = joint.Ordered.Select(e => curveAt(e.Segment, e.AtStart)).ToList();
            if (participants.Any(p => p is null))
            {
                if (joint.Kind != JunctionKind.EnLinea) FittingFailed(report, joint.Kind, name, "falta un tramo");
                continue;
            }
            var errors = new List<string>();
            bool done = false;
            var evaluated = joint.Kind;
            for (int order = 0; order < 2 && !done; order++)
            {
                using var tx = new SubTransaction(doc);
                tx.Start();
                try
                {
                    var c = joint.Ordered.Select((e, i) =>
                        FreeEnd(participants[i]!, endPoint(e.Segment, e.AtStart), Mm(plan.ToleranceMm))).ToArray();
                    if (c.Any(x => x == null)) throw new InvalidOperationException("falta un conector libre en el extremo previsto");
                    if (order > 0) (c[0], c[1]) = (c[1], c[0]);
                    if (order > 0 && joint.Kind == JunctionKind.Cruz)
                        (c[0], c[1], c[2], c[3]) = (c[2], c[3], c[1], c[0]);
                    var pipes = ConnectedPipeAxes(participants.OfType<Pipe>());
                    FamilyInstance? piece = null;
                    var actualKind = joint.Kind;
                    bool recheckSize = (actualKind is JunctionKind.EnLinea or JunctionKind.Transicion) &&
                        (kind is MepKind.Tuberia or MepKind.Conducto);
                    if (recheckSize)
                        actualKind = SameConnectorSize(c[0]!, c[1]!) ? JunctionKind.EnLinea : JunctionKind.Transicion;
                    evaluated = actualKind;
                    if (actualKind == JunctionKind.EnLinea)
                    {
                        actualKind = JunctionKind.EnLinea;
                        if (!Opposed(c[0]!, c[1]!) || !SameConnectorSize(c[0]!, c[1]!))
                            throw new InvalidOperationException("los conectores reales no coinciden en sección y dirección");
                        c[0]!.ConnectTo(c[1]!);
                        if (!c[0]!.IsConnectedTo(c[1]!)) throw new InvalidOperationException("Revit no enlazó directamente los extremos");
                    }
                    else
                    {
                        piece = actualKind switch
                        {
                            JunctionKind.Codo => doc.Create.NewElbowFitting(c[0], c[1]),
                            JunctionKind.Te => doc.Create.NewTeeFitting(c[0], c[1], c[2]),
                            JunctionKind.Cruz => doc.Create.NewCrossFitting(c[0], c[1], c[2], c[3]),
                            JunctionKind.Transicion => doc.Create.NewTransitionFitting(c[0], c[1]),
                            _ => null
                        };
                        if (piece is null) throw new InvalidOperationException("Revit no devolvió ninguna pieza");
                    }
                    doc.Regenerate();
                    string slopes = CheckPipeSlopes(doc, pipes);
                    var transitions = piece != null && actualKind == JunctionKind.Te
                        ? EndConnectors(piece).SelectMany(x => x.AllRefs.Cast<Connector>()).Select(x => x.Owner)
                            .OfType<FamilyInstance>().Where(x => x.Id != piece.Id &&
                                x.MEPModel is MechanicalFitting { PartType: PartType.Transition }).DistinctBy(x => x.Id).ToList()
                        : new List<FamilyInstance>();
                    if (tx.Commit() != TransactionStatus.Committed)
                        throw new InvalidOperationException("Revit anuló la subtransacción");
                    done = true;
                    if (piece != null) RecordFitting(report, piece, joint.Layer, actualKind);
                    foreach (var transition in transitions) RecordFitting(report, transition, joint.Layer, JunctionKind.Transicion);
                    report.Diagnostics.Add(name + (piece == null ? ": extremos enlazados directamente" :
                        $": CREADA con '{piece.Symbol.FamilyName} : {piece.Name}'") +
                        (transitions.Count > 0 ? $" con {transitions.Count} reducciones de enrutamiento" : "") + ".");
                    if (slopes.Length > 0 && sampled.Add(joint.Kind))
                        report.Diagnostics.Add($"{name}. Pendiente antes y después: {slopes}.");
                }
                catch (Exception ex)
                {
                    if (tx.GetStatus() == TransactionStatus.Started) tx.RollBack();
                    errors.Add(ex.Message);
                }
            }
            if (done) continue;
            string reason = string.Join(" / ", errors.Distinct());
            if (evaluated == JunctionKind.EnLinea)
                report.Diagnostics.Add($"{name}: sin pieza, extremos a revisar ({reason}).");
            else FittingFailed(report, evaluated, name, reason);
        }
    }

    private static void FittingFailed(BuildReport report, JunctionKind kind, string name, string reason)
    {
        report.FittingFailures++;
        report.FittingFailuresByKind[kind] = report.FittingFailuresByKind.GetValueOrDefault(kind) + 1;
        string line = $"{name}: NO creada → {reason}.";
        report.Diagnostics.Add(line);
        report.Problems.Add(line);
    }

    private static void RecordFitting(BuildReport report, FamilyInstance fitting, string layer, JunctionKind kind)
    {
        if (report.FittingsCreated.Any(f => f.Id == fitting.Id)) return;
        report.CreatedIds.Add(fitting.Id);
        report.FittingsCreated.Add((fitting.Id, layer, kind));
        switch (kind)
        {
            case JunctionKind.Codo: report.Elbows++; break;
            case JunctionKind.Te: report.Tees++; break;
            case JunctionKind.Cruz: report.Crosses++; break;
            case JunctionKind.Transicion: report.Transitions++; break;
        }
    }

    private static IEnumerable<Connector> EndConnectors(Element element) =>
        (element switch
        {
            MEPCurve curve => curve.ConnectorManager,
            FamilyInstance fi => fi.MEPModel?.ConnectorManager,
            _ => null
        })?.Connectors.Cast<Connector>().Where(c => c.ConnectorType == ConnectorType.End) ?? Enumerable.Empty<Connector>();

    private static Connector? FreeEnd(Element element, XYZ point, double tolerance) => EndConnectors(element)
        .Where(c => !c.IsConnected && c.Origin.DistanceTo(point) <= tolerance)
        .MinBy(c => c.Origin.DistanceTo(point));

    private static bool Opposed(Connector a, Connector b) =>
        a.CoordinateSystem.BasisZ.DotProduct(b.CoordinateSystem.BasisZ) <= -Math.Cos(SlopeRules.ParallelAngleDeg * Math.PI / 180);

    private static (double Width, double? Height) ConnectorSize(Connector connector) => connector.Shape == ConnectorProfileType.Round
        ? (ToMm(2 * connector.Radius), null)
        : (ToMm(connector.Width), ToMm(connector.Height));

    private static bool SameConnectorSize(Connector a, Connector b) =>
        a.Shape == b.Shape && NetworkBuilder.SameSize(ConnectorSize(a), ConnectorSize(b));

    private static Line AxisOf(MEPCurve curve) =>
        (curve.Location as LocationCurve)?.Curve as Line ?? throw new InvalidOperationException($"tramo {curve.Id} sin eje recto");

    private static Dictionary<ElementId, Line> ConnectedPipeAxes(IEnumerable<Pipe> pipes)
    {
        var axes = new Dictionary<ElementId, Line>();
        var seen = new HashSet<ElementId>();
        var queue = new Queue<Element>(pipes);
        while (queue.TryDequeue(out var element))
        {
            if (!seen.Add(element.Id)) continue;
            if (element is Pipe pipe) axes[pipe.Id] = AxisOf(pipe);
            foreach (var from in EndConnectors(element).Where(c => c.IsConnected))
            foreach (Connector to in from.AllRefs)
            {
                var owner = to.Owner;
                if (seen.Contains(owner.Id) || to.ConnectorType != ConnectorType.End || !from.IsConnectedTo(to)) continue;
                if (owner is Pipe || owner is FamilyInstance &&
                    owner.Category?.Id.Value is (long)BuiltInCategory.OST_PipeFitting or (long)BuiltInCategory.OST_PipeAccessory)
                    queue.Enqueue(owner);
            }
        }
        return axes;
    }

    private static string CheckPipeSlopes(Document doc, IReadOnlyDictionary<ElementId, Line> before)
    {
        static double Ratio(XYZ direction)
        {
            double horizontal = Math.Sqrt(direction.X * direction.X + direction.Y * direction.Y);
            return horizontal <= PlanDefaults.PipeSlopeToleranceRatio ? double.PositiveInfinity : direction.Z / horizontal;
        }
        static string Format(double ratio) => double.IsInfinity(ratio) ? "vertical" : $"{ratio * 100:0.######} %";
        var values = new List<string>();
        var changes = new List<string>();
        foreach (var (id, axis) in before)
        {
            if (doc.GetElement(id) is not Pipe pipe) throw new InvalidOperationException($"Revit eliminó el tubo {id}");
            var a = axis.Direction;
            var b = AxisOf(pipe).Direction;
            if (a.DotProduct(b) < 0) b = b.Negate();
            double prior = Ratio(a), after = Ratio(b);
            string value = $"tubo {id} {Format(prior)} → {Format(after)}";
            if (prior != after && (double.IsInfinity(prior) || double.IsInfinity(after) ||
                Math.Abs(prior - after) > PlanDefaults.PipeSlopeToleranceRatio))
                changes.Add(value);
            values.Add(value);
        }
        if (changes.Count > 0) throw new InvalidOperationException("Revit cambió la pendiente, pieza deshecha (" + string.Join(", ", changes) + ")");
        return string.Join(", ", values.Take(4));
    }
}
