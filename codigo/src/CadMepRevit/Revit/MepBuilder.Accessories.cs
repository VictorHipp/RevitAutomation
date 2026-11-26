using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Electrical;
using Autodesk.Revit.DB.Mechanical;
using Autodesk.Revit.DB.Plumbing;
using CadMepRevit.Core;

namespace CadMepRevit.Revit;

public static partial class MepBuilder
{
    private static Reference NetworkPlane(Document doc, Level level, XYZ at, XYZ outward, ref View? view)
    {
        view = view is { IsValidObject: true } ? view :
            new FilteredElementCollector(doc).OfClass(typeof(ViewPlan)).Cast<ViewPlan>()
                .FirstOrDefault(v => !v.IsTemplate && v.GenLevel?.Id == level.Id) ?? doc.ActiveView;
        var along = XYZ.BasisZ.CrossProduct(outward).Normalize();
        return doc.Create.NewReferencePlane(at - along, at + along, XYZ.BasisZ, view).GetReference();
    }

    private static void OrientNetworkFamily(Document doc, FamilyInstance family, NetworkBlockKind use, XYZ direction, XYZ at)
    {
        if (use == NetworkBlockKind.Equipo) return;
        doc.Regenerate();
        var ports = EndConnectors(family).ToList();
        if (ports.Count == 0) return;
        var desired = use == NetworkBlockKind.Terminal ? direction.Negate() : direction;
        var port = ports.MaxBy(c => c.CoordinateSystem.BasisZ.DotProduct(desired))!;
        var current = port.CoordinateSystem.BasisZ;
        double angle = current.AngleTo(desired);
        if (angle <= PlanDefaults.PipeSlopeToleranceRatio) return;
        var axis = current.CrossProduct(desired);
        if (axis.GetLength() <= PlanDefaults.PipeSlopeToleranceRatio)
            axis = Math.Abs(current.Z) < 0.9 ? XYZ.BasisZ : XYZ.BasisX;
        using var rotate = new SubTransaction(doc);
        rotate.Start();
        try
        {
            ElementTransformUtils.RotateElement(doc, family.Id, Line.CreateUnbound(at, axis.Normalize()), angle);
            doc.Regenerate();
            rotate.Commit();
        }
        catch
        {
            if (rotate.GetStatus() == TransactionStatus.Started) rotate.RollBack();
        }
    }

    private sealed record AccessoryConnection(string Result, string Note, MEPCurve? NewCurve = null,
        IReadOnlyList<FamilyInstance>? Transitions = null, FamilyInstance? Takeoff = null);

    private static void ConnectNetworkBlocks(Document doc, Level level, BuildPlan plan, IReadOnlyDictionary<int, List<MEPCurve>> curves,
        Dictionary<CadBlockRef, (NetworkBlockKind Use, NetworkBlockLink? Link, XYZ Point, XYZ Direction, double Rotation)> placements,
        Dictionary<ElementId, List<XYZ>> fixedEnds, BuildReport report)
    {
        doc.Regenerate();
        double tolerance = Mm(plan.ToleranceMm);
        // Trozos horizontales de cada tramo (varios si sigue el techo con subidas o bajadas).
        var pieces = curves.ToDictionary(pair => pair.Key, pair => pair.Value.ToList());
        var sampled = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (block, id, _) in report.BlocksPlaced)
        {
            if (!placements.TryGetValue(block, out var p)) continue;
            var found = doc.GetElement(id) as FamilyInstance;
            string name = found?.Symbol.FamilyName ?? BlockNames.Clean(block.BlockName);
            AccessoryConnection result;
            MEPCurve? target = p.Link is { } link && pieces.TryGetValue(link.Segment, out var route)
                ? route.Where(c => c.IsValidObject).MinBy(c => AxisOf(c).Distance(p.Point))
                : null;
            if (found is null) result = new("solo colocada", "marcador 3D sin conectores de familia");
            else if (p.Link == null) result = new("solo colocada", "sin tramo próximo de su red");
            else if (target == null) result = new("solo colocada", $"Revit no creó el tramo previsto {p.Link.Segment}");
            else if (EndConnectors(found).Any(c => c.IsConnected))
            {
                var owners = EndConnectors(found).SelectMany(c => c.AllRefs.Cast<Connector>())
                    .Where(c => c.Owner.Id != found.Id).Select(c => c.Owner.Id).Distinct();
                result = new("conectada", "familia ya conectada a " + string.Join(", ", owners));
            }
            else
            {
                using var tx = new SubTransaction(doc);
                tx.Start();
                try
                {
                    result = p.Use switch
                    {
                        NetworkBlockKind.EnLinea => InsertAccessory(doc, level, found, target, p.Point, tolerance, Mm(plan.MinLengthMm)),
                        NetworkBlockKind.Terminal when p.Link.Site == NetworkBlockSite.Costado && target is Duct duct && p.Link.Side != 0 =>
                            ConnectSideTerminal(doc, level, found, duct, plan.Layers[blockLayer(p.Link.Segment)].SystemTypeId,
                                p.Point, p.Direction, tolerance, Mm(plan.MinLengthMm)),
                        NetworkBlockKind.Terminal when p.Link.Site is NetworkBlockSite.Inicio or NetworkBlockSite.Fin =>
                            ConnectEndTerminal(doc, found, target, p.Point, tolerance),
                        NetworkBlockKind.Equipo => ConnectEquipment(doc, found, target, pieces.Values.SelectMany(x => x), fixedEnds, tolerance),
                        _ => throw new InvalidOperationException("el terminal no apunta a un extremo ni a un costado inequívoco")
                    };
                    if (tx.Commit() != TransactionStatus.Committed)
                        throw new InvalidOperationException("Revit anuló la subtransacción");
                    report.AutoConnections++;
                    if (result.NewCurve is { } added)
                    {
                        if (p.Use == NetworkBlockKind.EnLinea)
                        {
                            pieces[p.Link.Segment].Add(added);
                            if (fixedEnds.TryGetValue(target.Id, out var reserved)) fixedEnds[added.Id] = reserved;
                        }
                        report.CreatedIds.Add(added.Id);
                    }
                    if (result.Transitions != null)
                        foreach (var transition in result.Transitions) RecordFitting(report, transition, blockLayer(p.Link.Segment), JunctionKind.Transicion);
                    if (result.Takeoff != null) report.CreatedIds.Add(result.Takeoff.Id);
                }
                catch (Exception ex)
                {
                    if (tx.GetStatus() == TransactionStatus.Started) tx.RollBack();
                    result = new("solo colocada", ex.Message);
                    report.Problems.Add($"Accesorio '{name}', id {id}, tramo {target.Id}: {ex.Message}.");
                }
            }
            report.NetworkAccessories.Add((name, result.Result, result.Note));
            if (sampled.Add(name))
            {
                var actual = (found?.Location as LocationPoint)?.Point ?? p.Point;
                string rotation = found?.Location is LocationPoint loc
                    ? $"{loc.Rotation * 180 / Math.PI:0.##}° (previsto {p.Rotation * 180 / Math.PI:0.##}°)"
                    : $"{p.Rotation * 180 / Math.PI:0.##}° previsto";
                report.Diagnostics.Add($"Accesorio '{name}', id {id}: {result.Result}. " +
                    $"Punto X {ToMm(actual.X):0.##}, Y {ToMm(actual.Y):0.##} mm, cota {ToMm(actual.Z - level.ProjectElevation):0.##} mm sobre nivel, " +
                    $"giro {rotation}, tramo {target?.Id.ToString() ?? "sin tramo"} ({p.Link?.Site.ToString() ?? "sin asociación"}). {result.Note}.");
            }
        }
        string blockLayer(int index) => plan.Network.Segments[index].Layer;
    }

    private static AccessoryConnection InsertAccessory(Document doc, Level level, FamilyInstance family, MEPCurve target,
        XYZ point, double tolerance, double minLength)
    {
        var axis = AxisOf(target);
        XYZ a = axis.GetEndPoint(0), b = axis.GetEndPoint(1);
        var familyPosition = (family.Location as LocationPoint)?.Point;
        var dir = axis.Direction;
        double Offset(XYZ p) => (p - a).DotProduct(dir);
        double distance = Offset(point);
        if (distance < minLength || axis.Length - distance < minLength || axis.Distance(point) > tolerance)
            throw new InvalidOperationException("el punto de inserción no deja dos tramos rectos de longitud suficiente");
        XYZ splitAt = a + dir * distance;
        var pipeAxes = ConnectedPipeAxes(target is Pipe current ? new[] { current } : []);
        var reference = EndConnectors(target).First();
        var ports = EndConnectors(family).Where(c => c.Domain == reference.Domain && !c.IsConnected).ToList();
        if (ports.Count != 2) throw new InvalidOperationException("la familia en línea necesita dos conectores físicos libres de su red");
        TryResizeAccessory(doc, ports, reference);
        ports = EndConnectors(family).Where(c => c.Domain == reference.Domain && !c.IsConnected)
            .OrderBy(c => c.CoordinateSystem.BasisZ.DotProduct(dir)).ToList();
        if (ports.Count != 2 || ports[0].CoordinateSystem.BasisZ.DotProduct(dir) >= 0 ||
            ports[1].CoordinateSystem.BasisZ.DotProduct(dir) <= 0)
            throw new InvalidOperationException("los conectores de la familia no miran hacia los dos extremos");
        int startPort = ports[0].Id, endPort = ports[1].Id;
        double from = Offset(ports[0].Origin), to = Offset(ports[1].Origin);
        if (from < minLength || axis.Length - to < minLength || from > to + tolerance ||
            ports.Any(c => axis.Distance(c.Origin) > tolerance))
            throw new InvalidOperationException("los conectores del accesorio no caben sobre el eje sin mover el tramo");

        MEPCurve other = target switch
        {
            Pipe => SplitPipeOrDuct(doc, PlumbingUtils.BreakCurve(doc, target.Id, splitAt)),
            Duct => SplitPipeOrDuct(doc, MechanicalUtils.BreakCurve(doc, target.Id, splitAt)),
            CableTray tray => SplitTray(doc, level, tray, a, splitAt, b, tolerance),
            _ => throw new InvalidOperationException("esta clase de tramo no admite inserción en línea")
        };
        doc.Regenerate();
        bool originalAtStart = AxisOf(target).Distance(a) <= tolerance;
        var first = originalAtStart ? target : other;
        var second = originalAtStart ? other : target;
        MoveCutEnd(first, splitAt, a + dir * from, tolerance);
        MoveCutEnd(second, splitAt, a + dir * to, tolerance);
        doc.Regenerate();
        var transitions = new List<FamilyInstance>();
        Attach(first, a + dir * from, startPort);
        Attach(second, a + dir * to, endPort);
        doc.Regenerate();
        if (familyPosition != null && family.Location is LocationPoint location && location.Point.DistanceTo(familyPosition) > tolerance)
            throw new InvalidOperationException("Revit desplazó el accesorio durante la inserción");
        CheckOutsideEnd(first, a, tolerance);
        CheckOutsideEnd(second, b, tolerance);
        if (target is Pipe) pipeAxes[other.Id] = axis;
        string slopes = CheckPipeSlopes(doc, pipeAxes);
        return new("insertada", $"tramos {first.Id} y {second.Id}; {transitions.Count} reducciones" +
            (slopes.Length > 0 ? $"; pendiente antes y después {slopes}" : ""), other, transitions);

        void Attach(MEPCurve pipe, XYZ at, int port)
        {
            var end = FreeEnd(pipe, at, tolerance) ?? throw new InvalidOperationException("no hay un extremo libre en el corte");
            var device = EndConnectors(family).Single(c => c.Id == port);
            if (ConnectPorts(doc, end, device, tolerance, allowTransition: true) is { } transition) transitions.Add(transition);
        }
    }

    private static MEPCurve SplitPipeOrDuct(Document doc, ElementId id) =>
        doc.GetElement(id) as MEPCurve ?? throw new InvalidOperationException("Revit no devolvió el segundo tramo tras el corte");

    private static CableTray SplitTray(Document doc, Level level, CableTray tray, XYZ first, XYZ at, XYZ last, double tolerance)
    {
        var end = EndConnectors(tray).MinBy(c => c.Origin.DistanceTo(last))!;
        var refs = end.AllRefs.Cast<Connector>().Where(c => c.Owner.Id != tray.Id && end.IsConnectedTo(c)).ToList();
        foreach (var connected in refs) end.DisconnectFrom(connected);
        var other = CableTray.Create(doc, tray.GetTypeId(), at, last, level.Id);
        foreach (var param in new[] { BuiltInParameter.RBS_CABLETRAY_WIDTH_PARAM, BuiltInParameter.RBS_CABLETRAY_HEIGHT_PARAM })
            other.get_Parameter(param).Set(tray.get_Parameter(param).AsDouble());
        ((LocationCurve)tray.Location).Curve = Line.CreateBound(first, at);
        doc.Regenerate();
        foreach (var connected in refs)
        {
            var newEnd = FreeEnd(other, last, tolerance) ?? throw new InvalidOperationException("no se pudo conservar la conexión exterior de la bandeja");
            newEnd.ConnectTo(connected);
        }
        return other;
    }

    private static void MoveCutEnd(MEPCurve curve, XYZ at, XYZ to, double tolerance)
    {
        var line = AxisOf(curve);
        XYZ a = line.GetEndPoint(0), b = line.GetEndPoint(1);
        if (Math.Min(a.DistanceTo(at), b.DistanceTo(at)) > tolerance)
            throw new InvalidOperationException("Revit no cortó el tramo en el punto previsto");
        ((LocationCurve)curve.Location).Curve = a.DistanceTo(at) < b.DistanceTo(at)
            ? Line.CreateBound(to, b) : Line.CreateBound(a, to);
    }

    private static void CheckOutsideEnd(MEPCurve curve, XYZ at, double tolerance)
    {
        var line = AxisOf(curve);
        if (Math.Min(line.GetEndPoint(0).DistanceTo(at), line.GetEndPoint(1).DistanceTo(at)) > tolerance)
            throw new InvalidOperationException("Revit desplazó un extremo exterior del tramo");
    }

    private static void TryResizeAccessory(Document doc, List<Connector> ports, Connector reference)
    {
        if (ports.Any(c => c.Shape != reference.Shape) || ports.All(c => SameConnectorSize(c, reference))) return;
        using var tx = new SubTransaction(doc);
        tx.Start();
        try
        {
            foreach (var c in ports.Where(c => !SameConnectorSize(c, reference)))
            {
                if (reference.Shape == ConnectorProfileType.Round) c.Radius = reference.Radius;
                else if (reference.Shape == ConnectorProfileType.Rectangular)
                {
                    c.Width = reference.Width;
                    c.Height = reference.Height;
                }
            }
            doc.Regenerate();
            tx.Commit();
        }
        catch { if (tx.GetStatus() == TransactionStatus.Started) tx.RollBack(); }
    }

    private static FamilyInstance? ConnectPorts(Document doc, Connector curve, Connector device, double tolerance, bool allowTransition)
    {
        if (!Opposed(curve, device) || curve.Origin.DistanceTo(device.Origin) > tolerance)
            throw new InvalidOperationException("los conectores no coinciden en posición y dirección");
        if (!SameConnectorSize(curve, device))
        {
            if (!allowTransition) throw new InvalidOperationException("los conectores no tienen la misma sección");
            return doc.Create.NewTransitionFitting(curve, device)
                ?? throw new InvalidOperationException("Revit no admite una transición entre estas secciones");
        }
        curve.ConnectTo(device);
        if (!curve.IsConnectedTo(device)) throw new InvalidOperationException("Revit no enlazó directamente los conectores");
        return null;
    }

    private static AccessoryConnection ConnectEndTerminal(Document doc, FamilyInstance terminal, MEPCurve target, XYZ at, double tolerance)
    {
        var end = FreeEnd(target, at, tolerance) ?? throw new InvalidOperationException("el extremo previsto está ocupado o no coincide");
        var port = EndConnectors(terminal).Where(c => c.Domain == end.Domain && !c.IsConnected)
            .MinBy(c => c.Origin.DistanceTo(end.Origin)) ?? throw new InvalidOperationException("la rejilla no tiene conector de aire libre");
        var offset = end.Origin - port.Origin;
        if (offset.GetLength() > tolerance && Opposed(end, port))
        {
            var direction = AxisOf(target).Direction;
            var axial = direction * offset.DotProduct(direction);
            if ((offset - axial).GetLength() <= tolerance && axial.GetLength() <= Mm(ConnectorSize(end).Width))
            {
                int portId = port.Id;
                ElementTransformUtils.MoveElement(doc, terminal.Id, axial);
                doc.Regenerate();
                end = FreeEnd(target, at, tolerance) ?? throw new InvalidOperationException("el extremo previsto dejó de estar libre");
                port = EndConnectors(terminal).Single(c => c.Id == portId);
            }
        }
        var transition = ConnectPorts(doc, end, port, tolerance, allowTransition: true);
        return new("conectada", transition == null ? $"al extremo del conducto {target.Id}" :
            $"al extremo del conducto {target.Id}, con transición {transition.Id}", Transitions: transition == null ? null : new[] { transition });
    }

    private static AccessoryConnection ConnectSideTerminal(Document doc, Level level, FamilyInstance terminal, Duct trunk,
        ElementId? system, XYZ at, XYZ outward, double tolerance, double minLength)
    {
        var axis = AxisOf(trunk);
        string directError;
        using (var direct = new SubTransaction(doc))
        {
            direct.Start();
            try
            {
                if (!MechanicalUtils.ConnectAirTerminalOnDuct(doc, terminal.Id, trunk.Id))
                    throw new InvalidOperationException("Revit rechazó la conexión directa a la cara");
                doc.Regenerate();
                if (terminal.Location is not LocationPoint location || Math.Abs(location.Point.Z - at.Z) > tolerance ||
                    (location.Point - axis.Project(at).XYZPoint).DotProduct(outward) <= 0 ||
                    !EndConnectors(terminal).Any(c => c.IsConnected &&
                        c.CoordinateSystem.BasisZ.DotProduct(outward) < -Math.Cos(SlopeRules.ParallelAngleDeg * Math.PI / 180)))
                    throw new InvalidOperationException("Revit eligió otra cara, cota u orientación");
                CheckOutsideEnd(trunk, axis.GetEndPoint(0), tolerance);
                CheckOutsideEnd(trunk, axis.GetEndPoint(1), tolerance);
                if (direct.Commit() != TransactionStatus.Committed) throw new InvalidOperationException("Revit anuló la toma directa");
                return new("conectada", $"toma directa de Revit en la cara del conducto {trunk.Id}");
            }
            catch (Exception ex)
            {
                if (direct.GetStatus() == TransactionStatus.Started) direct.RollBack();
                directError = ex.Message;
            }
        }
        try
        {
            var port = EndConnectors(terminal).FirstOrDefault(c => !c.IsConnected && c.Domain == Domain.DomainHvac &&
                c.CoordinateSystem.BasisZ.DotProduct(outward) < -Math.Cos(SlopeRules.ParallelAngleDeg * Math.PI / 180))
                ?? throw new InvalidOperationException("la familia no tiene un conector orientado hacia el conducto");
            var onAxis = axis.Project(port.Origin).XYZPoint;
            if (system == null || port.Origin.DistanceTo(onAxis) < minLength || (port.Origin - onAxis).DotProduct(outward) <= 0)
                throw new InvalidOperationException("no hay espacio o tipo de sistema para la derivación");
            int portId = port.Id;
            var branch = Duct.Create(doc, system, trunk.GetTypeId(), level.Id, onAxis, port.Origin);
            var size = ConnectorSize(port);
            if (branch.DuctType.Shape == ConnectorProfileType.Round)
                branch.get_Parameter(BuiltInParameter.RBS_CURVE_DIAMETER_PARAM).Set(Mm(size.Width));
            else if (size.Height is double height)
            {
                branch.get_Parameter(BuiltInParameter.RBS_CURVE_WIDTH_PARAM).Set(Mm(size.Width));
                branch.get_Parameter(BuiltInParameter.RBS_CURVE_HEIGHT_PARAM).Set(Mm(height));
            }
            doc.Regenerate();
            var fitting = doc.Create.NewTakeoffFitting(FreeEnd(branch, onAxis, tolerance), trunk)
                ?? throw new InvalidOperationException("el tipo de conducto no admite una toma");
            doc.Regenerate();
            port = EndConnectors(terminal).Single(c => c.Id == portId);
            var end = FreeEnd(branch, port.Origin, tolerance) ?? throw new InvalidOperationException("la toma no deja un extremo junto al terminal");
            var transition = ConnectPorts(doc, end, port, tolerance, allowTransition: true);
            return new("conectada", $"toma {fitting.Id} y conducto de derivación {branch.Id} al conducto {trunk.Id}", branch,
                transition is null ? null : new[] { transition }, fitting);
        }
        catch (Exception ex) { throw new InvalidOperationException($"{directError}; derivación: {ex.Message}", ex); }
    }

    private static AccessoryConnection ConnectEquipment(Document doc, FamilyInstance family, MEPCurve associated,
        IEnumerable<MEPCurve> curves, IReadOnlyDictionary<ElementId, List<XYZ>> fixedEnds, double tolerance)
    {
        var outlets = EndConnectors(family).Where(c => !c.IsConnected).ToArray();
        var plans = from port in outlets
                    from curve in curves
                    where curve.GetType() == associated.GetType()
                    from end in EndConnectors(curve)
                    where !end.IsConnected && end.Domain == port.Domain &&
                        end.Origin.DistanceTo(port.Origin) <= tolerance && Opposed(end, port) && SameConnectorSize(end, port) &&
                        fixedEnds.GetValueOrDefault(curve.Id)?.Any(p => p.DistanceTo(end.Origin) <= tolerance) != true
                    orderby end.Origin.DistanceTo(port.Origin)
                    select (Port: port.Id, Curve: curve, End: end.Id);
        var matched = plans.DistinctBy(p => p.Port).DistinctBy(p => (p.Curve.Id, p.End)).ToList();
        if (matched.Count == 0) throw new InvalidOperationException("ningún conector coincide con un extremo libre de su red");
        var old = matched.Select(m => m.Curve).DistinctBy(c => c.Id).ToDictionary(c => c.Id, c => (Curve: c, Axis: AxisOf(c)));
        var location = (family.Location as LocationPoint)?.Point;
        foreach (var m in matched)
            ConnectPorts(doc, EndConnectors(m.Curve).Single(c => c.Id == m.End), EndConnectors(family).Single(c => c.Id == m.Port),
                tolerance, allowTransition: false);
        doc.Regenerate();
        if (location != null && family.Location is LocationPoint p && p.Point.DistanceTo(location) > Mm(SlopeRules.Epsilon))
            throw new InvalidOperationException("Revit desplazó el equipo al conectar");
        foreach (var (_, entry) in old)
        {
            CheckOutsideEnd(entry.Curve, entry.Axis.GetEndPoint(0), Mm(SlopeRules.Epsilon));
            CheckOutsideEnd(entry.Curve, entry.Axis.GetEndPoint(1), Mm(SlopeRules.Epsilon));
        }
        return new("conectada", "a extremos coincidentes de tramos " + string.Join(", ", old.Keys));
    }
}
