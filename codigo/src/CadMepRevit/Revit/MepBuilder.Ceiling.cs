using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Electrical;
using Autodesk.Revit.DB.Mechanical;
using Autodesk.Revit.DB.Plumbing;
using CadMepRevit.Config;
using CadMepRevit.Core;

namespace CadMepRevit.Revit;

/// <summary>
/// Recorrido en Revit de un tramo del plano que sigue el techo: sus trozos horizontales (en orden desde el inicio del
/// tramo) y las subidas o bajadas verticales entre ellos.
/// </summary>
public sealed class CeilingRoute
{
    public int Segment { get; init; }
    public double LengthMm { get; init; }
    public List<CeilingPiece> Pieces { get; set; } = new();
    /// <summary>Un elemento de Revit por trozo, en el mismo orden que <see cref="Pieces"/>.</summary>
    public List<MEPCurve> Horizontal { get; } = new();
    /// <summary>Subidas o bajadas: la k-ésima va entre los trozos k y k+1.</summary>
    public List<MEPCurve> Risers { get; } = new();
    public double StartAxisMm => Pieces[0].AxisMm;
    public double EndAxisMm => Pieces[^1].AxisMm;

    /// <summary>Cota del eje (mm sobre el nivel) en una fracción (0 a 1) del tramo del plano.</summary>
    public double AxisMmAt(double fraction)
    {
        double along = Math.Clamp(fraction, 0, 1) * LengthMm;
        return Pieces.FirstOrDefault(p => along <= p.ToMm + 1e-6)?.AxisMm ?? EndAxisMm;
    }

    public MEPCurve? CurveAt(bool atStart) => Horizontal.Count == 0 ? null : atStart ? Horizontal[0] : Horizontal[^1];
}

public static partial class MepBuilder
{
    /// <summary>
    /// Regla «sigue el techo» (ver <see cref="PlacementConfig"/>): mueve a su cota los tramos ya creados a la altura fija,
    /// los parte donde el forjado sube o baja, crea las subidas o bajadas con sus dos codos y avisa de lo que atraviesa
    /// el falso techo o el forjado. La planta no cambia. Devuelve el recorrido de cada tramo que sigue el techo.
    /// </summary>
    private static Dictionary<int, CeilingRoute> FollowCeiling(Document doc, BuildPlan plan, Level level,
        IReadOnlyDictionary<int, MEPCurve> curves, BuildReport report)
    {
        var routes = new Dictionary<int, CeilingRoute>();
        if (!PlacementConfig.FollowsCeiling || !plan.FollowCeiling || curves.Count == 0) return routes;
        double levelZ = level.ProjectElevation;

        // 1) Qué tramos pueden seguir el techo: conductos, bandejas, tubos eléctricos y tuberías horizontales cuya altura es
        //    la SUPUESTA. Lo que el plano fija (pendiente, cota, bajantes) se conserva.
        var candidate = new HashSet<int>();
        var keptByPlan = new Dictionary<string, int>();
        void Keep(string why) => keptByPlan[why] = keptByPlan.GetValueOrDefault(why) + 1;
        foreach (var seg in plan.Network.Segments)
        {
            if (!curves.TryGetValue(seg.Index, out var c) || !c.IsValidObject) continue;
            if (!plan.Layers.TryGetValue(seg.Layer, out var lp)) continue;
            if (lp.Kind is not (MepKind.Conducto or MepKind.Tuberia or MepKind.Bandeja or MepKind.TuboElectrico)) continue;
            if (plan.RisersBySegment.ContainsKey(seg.Index)) { Keep("bajante o montante"); continue; }
            if (Math.Abs(seg.Start.Z - seg.End.Z) > 1) { Keep("inclinado en el CAD"); continue; }
            if (lp.Kind == MepKind.Tuberia && plan.Slopes is { } slopes)
            {
                if (!slopes.Pipes.TryGetValue(seg.Index, out var sp)) { Keep("tubería sin cota calculada"); continue; }
                if (sp.PlannedRatio > 1e-9) { Keep("tubería con pendiente del plano"); continue; }
                var net = slopes.Networks.FirstOrDefault(n => n.Id == sp.Network);
                if (net is null || !net.AssumedSuspended) { Keep("tubería con cota del plano o enterrada"); continue; }
            }
            candidate.Add(seg.Index);
        }
        // Una red unida a un tramo que conserva su cota del plano se queda entera como estaba: si no, sus uniones no encajan.
        for (bool changed = true; changed;)
        {
            changed = false;
            foreach (var j in plan.Network.Junctions)
            {
                if (!j.Ends.Any(e => candidate.Contains(e.Segment))) continue;
                if (!j.Ends.Any(e => !candidate.Contains(e.Segment) && curves.ContainsKey(e.Segment))) continue;
                foreach (var e in j.Ends)
                    if (candidate.Remove(e.Segment)) { changed = true; Keep("unido a un tramo que conserva la cota del plano"); }
            }
        }
        foreach (var (why, n) in keptByPlan)
            report.CeilingNotes.Add($"{n} tramos conservan la cota del plano o de su red ({why}); solo se comprueba que no atraviesen el forjado.");

        // 2) Forjados y falsos techos del proyecto y de los vínculos.
        var finder = CeilingFinder.Create(doc, level);
        report.CeilingNotes.Add("Forjados y falsos techos consultados: " + string.Join("; ", finder.Models.Concat(finder.Notes)) + ".");
        if (finder.FloorCount == 0)
        {
            report.CeilingFallback = candidate.Count;
            if (candidate.Count > 0)
                report.SlabWarnings.Add($"No hay ningún forjado (suelo) encima de la planta '{level.Name}' en el proyecto ni en los vínculos cargados: " +
                    $"{candidate.Count} tramos quedan a la altura SUPUESTA de {plan.HeightAboveLevelMm:0} mm. Vincule la estructura o la arquitectura y repita.");
            return routes;
        }

        // 3) Cota máxima del eje en cada punto de cada tramo: cara inferior del forjado − holgura − medio alto del tramo.
        double clearance = PlacementConfig.CeilingClearanceMm, step = PlacementConfig.CeilingSampleStepMm;
        var inputs = new List<CeilingSegmentInput>();
        var geometry = new Dictionary<int, (XYZ A, XYZ B, double V, double W, string Layer)>();
        foreach (int index in candidate.OrderBy(i => i))
        {
            var curve = curves[index];
            var axis = AxisOf(curve);
            XYZ a = axis.GetEndPoint(0), b = axis.GetEndPoint(1);
            var (v, w) = SectionMm(curve);
            double lengthMm = ToMm(a.DistanceTo(b));
            if (lengthMm < 1) continue;
            var dir = (b - a).Normalize();
            var side = new XYZ(-dir.Y, dir.X, 0);
            var samples = new List<(double, double?)>();
            int n = Math.Max(1, (int)Math.Ceiling(lengthMm / step));
            for (int i = 0; i <= n; i++)
            {
                double t = (double)i / n;
                var p = a + (b - a) * t;
                double? under = null;
                foreach (double off in w > 0 ? new[] { 0, w / 2, -w / 2 } : new[] { 0.0 })
                {
                    var q = p + side * Mm(off);
                    if (finder.SlabUndersideAt(q.X, q.Y) is { } u && (under is null || u < under)) under = u;
                }
                samples.Add((lengthMm * t, under is { } z ? ToMm(z - levelZ) - clearance - v / 2 : null));
            }
            bool slim = curve is Pipe or Conduit;
            inputs.Add(new CeilingSegmentInput
            {
                Segment = index, LengthMm = lengthMm, Samples = samples,
                MinRiseMm = slim ? Math.Max(150, 3 * v + 50) : Math.Max(200, 2 * v + 50),
                MinPieceMm = Math.Max(400, 3 * v + 200),
                StepMarginMm = v / 2 + 50
            });
            geometry[index] = (a, b, v, w, plan.Network.Segments[index].Layer);
        }
        var solved = CeilingProfile.Solve(inputs, plan.Network.Junctions.Select(j => (IReadOnlyList<(int, bool)>)j.Ends), plan.HeightAboveLevelMm);
        report.CeilingLoweredEnds = solved.EndsLowered;
        report.CeilingWithoutSlab = solved.WithoutSlab.Count;

        // 4) Mover y partir los tramos en Revit. Cada tramo en su subtransacción: si falla, se queda como estaba.
        foreach (var input in inputs)
        {
            int index = input.Segment;
            var curve = curves[index];
            var (a, b, _, _, layer) = geometry[index];
            var route = new CeilingRoute { Segment = index, LengthMm = input.LengthMm };
            routes[index] = route;
            void Unchanged()
            {
                route.Pieces = new List<CeilingPiece> { new(0, input.LengthMm, ToMm(a.Z - levelZ)) };
                route.Horizontal.Clear();
                route.Risers.Clear();
                route.Horizontal.Add(curve);
            }
            if (solved.Fallback.Contains(index)) { Unchanged(); report.CeilingFallback++; continue; }
            var pieces = solved.Pieces[index];
            route.Pieces = pieces;
            XYZ At(double alongMm, double axisMm)
            {
                var p = a + (b - a) * (alongMm / input.LengthMm);
                return new XYZ(p.X, p.Y, levelZ + Mm(axisMm));
            }
            using var st = new SubTransaction(doc);
            st.Start();
            try
            {
                for (int k = 0; k < pieces.Count; k++)
                {
                    var piece = k == 0 ? curve : CopyCurve(doc, curve);
                    SetAxis(piece, At(pieces[k].FromMm, pieces[k].AxisMm), At(pieces[k].ToMm, pieces[k].AxisMm));
                    route.Horizontal.Add(piece);
                    if (k == 0) continue;
                    var riser = CopyCurve(doc, curve);
                    SetAxis(riser, At(pieces[k].FromMm, pieces[k - 1].AxisMm), At(pieces[k].FromMm, pieces[k].AxisMm));
                    route.Risers.Add(riser);
                }
                if (st.Commit() != TransactionStatus.Committed) throw new InvalidOperationException("Revit anuló la subtransacción");
                foreach (var e in route.Horizontal.Skip(1).Concat(route.Risers)) report.CreatedIds.Add(e.Id);
                report.CeilingFollowed++;
                report.CeilingSteps += route.Risers.Count;
            }
            catch (Exception ex)
            {
                if (st.GetStatus() == TransactionStatus.Started) st.RollBack();
                Unchanged();
                report.Problems.Add($"Capa '{layer}', tramo {index}: no se pudo llevar bajo el techo ({ex.Message}); queda a {route.StartAxisMm:0} mm sobre el nivel. " +
                                    "Revise sus uniones: los tramos vecinos sí siguen el techo.");
            }
        }
        doc.Regenerate();

        // 5) Codos de cada subida o bajada (dos por escalón). Si no encajan, se gira la subida 90° y se repite.
        double tolerance = Mm(plan.ToleranceMm);
        foreach (var route in routes.Values.Where(r => r.Risers.Count > 0))
        {
            var (a, b, _, _, layer) = geometry[route.Segment];
            for (int k = 1; k < route.Horizontal.Count; k++)
            {
                var below = route.Horizontal[k - 1];
                var above = route.Horizontal[k];
                var riser = route.Risers[k - 1];
                var p = a + (b - a) * (route.Pieces[k].FromMm / route.LengthMm);
                var p0 = new XYZ(p.X, p.Y, levelZ + Mm(route.Pieces[k - 1].AxisMm));
                var p1 = new XYZ(p.X, p.Y, levelZ + Mm(route.Pieces[k].AxisMm));
                string where = $"capa '{layer}', X {ToMm(p.X):0}, Y {ToMm(p.Y):0} mm, de {route.Pieces[k - 1].AxisMm:0} a {route.Pieces[k].AxisMm:0} mm sobre el nivel";
                var errors = new List<string>();
                bool done = false;
                for (int attempt = 0; attempt < 2 && !done; attempt++)
                {
                    using var st = new SubTransaction(doc);
                    st.Start();
                    try
                    {
                        AlignRiser(doc, riser, FreeEnd(below, p0, tolerance), FreeEnd(riser, p0, tolerance));
                        if (attempt == 1) ElementTransformUtils.RotateElement(doc, riser.Id, Line.CreateBound(p0, p1), Math.PI / 2);
                        doc.Regenerate();
                        var e1 = Elbow(doc, below, riser, p0, tolerance);
                        var e2 = Elbow(doc, riser, above, p1, tolerance);
                        if (st.Commit() != TransactionStatus.Committed) throw new InvalidOperationException("Revit anuló la subtransacción");
                        RecordFitting(report, e1, layer, JunctionKind.Codo);
                        RecordFitting(report, e2, layer, JunctionKind.Codo);
                        report.CeilingElbows += 2;
                        done = true;
                    }
                    catch (Exception ex)
                    {
                        if (st.GetStatus() == TransactionStatus.Started) st.RollBack();
                        errors.Add(ex.Message);
                    }
                }
                if (done) continue;
                report.CeilingElbowFailures++;
                report.Problems.Add($"Subida o bajada bajo el techo en {where}: codos NO creados ({string.Join(" / ", errors.Distinct())}). " +
                                    "Los tramos quedan a su cota pero sin unir: compruebe que el tipo tiene codo en sus preferencias de enrutamiento.");
            }
        }

        // 6) Avisos: tramos que atraviesan el forjado (los que conservan la cota del plano) o el falso techo (decisión del
        //    usuario: se dejan bajo el forjado y se avisa), y tramos por debajo del suelo de la planta.
        var checkedCurves = routes.Values.SelectMany(r => r.Horizontal.Select(c => (Curve: c, r.Segment)))
            .Concat(curves.Where(c => !routes.ContainsKey(c.Key) && c.Value.IsValidObject &&
                plan.Layers.TryGetValue(plan.Network.Segments[c.Key].Layer, out var lp) &&
                lp.Kind is MepKind.Conducto or MepKind.Tuberia or MepKind.Bandeja or MepKind.TuboElectrico &&
                !plan.RisersBySegment.ContainsKey(c.Key)).Select(c => (Curve: c.Value, Segment: c.Key)))
            .ToList();
        foreach (var (curve, index) in checkedCurves)
        {
            if (!curve.IsValidObject || curve.Location is not LocationCurve { Curve: Line line }) continue;
            var (v, _) = SectionMm(curve);
            string layer = plan.Network.Segments[index].Layer;
            bool kept = !routes.ContainsKey(index);
            int n = Math.Max(1, (int)Math.Ceiling(ToMm(line.Length) / 500));
            (XYZ P, double By)? slabHit = null, ceilingHit = null;
            int slabCount = 0, ceilingCount = 0;
            for (int i = 0; i <= n; i++)
            {
                var p = line.Evaluate((double)i / n, true);
                double top = p.Z + Mm(v / 2), bottom = p.Z - Mm(v / 2);
                if (finder.SlabUndersideAt(p.X, p.Y) is { } slab && top > slab + Mm(1))
                {
                    slabCount++;
                    if (slabHit is null || top - slab > slabHit.Value.By) slabHit = (p, top - slab);
                }
                if (finder.CeilingTopAt(p.X, p.Y) is { } ceiling && bottom < ceiling - Mm(1))
                {
                    ceilingCount++;
                    ceilingHit ??= (p, ceiling - bottom);
                }
            }
            string At(XYZ p) => $"X {ToMm(p.X):0}, Y {ToMm(p.Y):0} mm, eje a {ToMm(p.Z - levelZ):0} mm sobre el nivel";
            if (slabHit is { } sh)
                report.CeilingCrossings.Add($"Capa '{layer}', id {curve.Id}: " +
                    (kept ? "conserva la cota del plano y " : "") + $"atraviesa el forjado {ToMm(sh.By):0} mm en {At(sh.P)} " +
                    $"({slabCount} de {n + 1} puntos comprobados). Revise la cota.");
            if (ceilingHit is { } ph)
                report.FalseCeilingWarnings.Add($"Capa '{layer}', id {curve.Id}: queda {ToMm(ph.By):0} mm por debajo de la cara superior del falso techo en {At(ph.P)} " +
                    $"({ceilingCount} de {n + 1} puntos). No cabe sobre el falso techo: se deja bajo el forjado; revise el falso techo o el tamaño.");
            if (line.GetEndPoint(0).Z - Mm(v / 2) < levelZ || line.GetEndPoint(1).Z - Mm(v / 2) < levelZ)
                report.CeilingCrossings.Add($"Capa '{layer}', id {curve.Id}: queda por debajo del suelo de la planta (forjado muy bajo o tramo muy alto). Revíselo.");
        }
        return routes;
    }

    /// <summary>Medida exterior vertical (V) y horizontal (W) de un tramo, en mm.</summary>
    private static (double V, double W) SectionMm(MEPCurve c)
    {
        double P(BuiltInParameter bip) =>
            c.get_Parameter(bip) is { HasValue: true, StorageType: StorageType.Double } p ? ToMm(p.AsDouble()) : 0;
        switch (c)
        {
            case Pipe:
            {
                double od = P(BuiltInParameter.RBS_PIPE_OUTER_DIAMETER);
                if (od <= 0) od = P(BuiltInParameter.RBS_PIPE_DIAMETER_PARAM);
                return (od, od);
            }
            case Duct:
            {
                double d = P(BuiltInParameter.RBS_CURVE_DIAMETER_PARAM);
                return d > 0 ? (d, d) : (P(BuiltInParameter.RBS_CURVE_HEIGHT_PARAM), P(BuiltInParameter.RBS_CURVE_WIDTH_PARAM));
            }
            case CableTray:
                return (P(BuiltInParameter.RBS_CABLETRAY_HEIGHT_PARAM), P(BuiltInParameter.RBS_CABLETRAY_WIDTH_PARAM));
            case Conduit:
            {
                double od = P(BuiltInParameter.RBS_CONDUIT_OUTER_DIAM_PARAM);
                if (od <= 0) od = P(BuiltInParameter.RBS_CONDUIT_DIAMETER_PARAM);
                return (od, od);
            }
            default:
                return (0, 0);
        }
    }

    /// <summary>Copia de un tramo (mismo tipo, sistema y tamaño) para colocarla después en su sitio.</summary>
    private static MEPCurve CopyCurve(Document doc, MEPCurve curve)
    {
        var ids = ElementTransformUtils.CopyElement(doc, curve.Id, new XYZ(0, 0, Mm(1)));
        return ids.Select(doc.GetElement).OfType<MEPCurve>().FirstOrDefault()
               ?? throw new InvalidOperationException("Revit no copió el tramo");
    }

    private static void SetAxis(MEPCurve curve, XYZ from, XYZ to)
    {
        if (curve.Location is not LocationCurve location) throw new InvalidOperationException($"tramo {curve.Id} sin eje");
        location.Curve = Line.CreateBound(from, to);
    }

    /// <summary>
    /// Gira una subida de sección rectangular sobre su eje para que su ancho quede como el del tramo horizontal (así el
    /// codo encaja). Las secciones redondas no lo necesitan.
    /// </summary>
    private static void AlignRiser(Document doc, MEPCurve riser, Connector? horizontal, Connector? vertical)
    {
        if (horizontal is null || vertical is null || horizontal.Shape == ConnectorProfileType.Round) return;
        var h = horizontal.CoordinateSystem;
        var v = vertical.CoordinateSystem;
        bool useX = Math.Abs(h.BasisX.Z) < 0.5;
        var ha = useX ? h.BasisX : h.BasisY;
        var va = useX ? v.BasisX : v.BasisY;
        var h2 = new XYZ(ha.X, ha.Y, 0);
        var v2 = new XYZ(va.X, va.Y, 0);
        if (h2.GetLength() < 1e-6 || v2.GetLength() < 1e-6) return;
        h2 = h2.Normalize();
        v2 = v2.Normalize();
        double angle = Math.Atan2(v2.X * h2.Y - v2.Y * h2.X, v2.DotProduct(h2));
        if (Math.Abs(Math.Sin(angle)) < 1e-4) return;
        var axis = AxisOf(riser);
        ElementTransformUtils.RotateElement(doc, riser.Id, Line.CreateBound(axis.GetEndPoint(0), axis.GetEndPoint(1)), angle);
    }

    private static FamilyInstance Elbow(Document doc, MEPCurve a, MEPCurve b, XYZ at, double tolerance)
    {
        var ca = FreeEnd(a, at, tolerance) ?? throw new InvalidOperationException("falta el extremo libre del tramo horizontal");
        var cb = FreeEnd(b, at, tolerance) ?? throw new InvalidOperationException("falta el extremo libre de la subida");
        return doc.Create.NewElbowFitting(ca, cb) ?? throw new InvalidOperationException("Revit no devolvió el codo");
    }
}
