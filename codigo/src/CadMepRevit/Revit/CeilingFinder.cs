using Autodesk.Revit.DB;

namespace CadMepRevit.Revit;

/// <summary>
/// Cota del techo sobre un punto de la planta, para colocar luminarias y detectores a su altura REAL en vez de a una
/// supuesta: primero la cara inferior del FALSO TECHO (techos de Revit) y, si no hay, la cara inferior del FORJADO de
/// encima. Busca en el proyecto y en los modelos VINCULADOS cargados (la arquitectura suele estar vinculada), con su
/// transformación. Solo lee el modelo. Sin techo ni forjado encima devuelve null y se usa la altura supuesta.
/// </summary>
public sealed class CeilingFinder
{
    private readonly List<(Solid Solid, BoundingBoxXYZ Box, string Source)> _ceilings = new();
    private readonly List<(Solid Solid, BoundingBoxXYZ Box, string Source)> _floors = new();
    private readonly double _minZ, _maxZ;
    private readonly Dictionary<(long, long), (double Z, string Source)?> _cache = new();

    public int CeilingCount => _ceilings.Count;

    /// <summary>Rectángulo en planta (pies) que ocupan los techos y forjados encontrados; null si no hay ninguno.</summary>
    public (double MinX, double MinY, double MaxX, double MaxY)? Extent
    {
        get
        {
            var all = _ceilings.Concat(_floors).Select(c => c.Box).ToList();
            if (all.Count == 0) return null;
            return (all.Min(b => b.Min.X), all.Min(b => b.Min.Y), all.Max(b => b.Max.X), all.Max(b => b.Max.Y));
        }
    }
    public int FloorCount => _floors.Count;
    /// <summary>Modelos consultados (el proyecto y los vínculos), para el informe.</summary>
    public List<string> Models { get; } = new();
    public List<string> Notes { get; } = new();

    private static double Mm(double mm) => UnitUtils.ConvertToInternalUnits(mm, UnitTypeId.Millimeters);

    private CeilingFinder(double minZ, double maxZ) { _minZ = minZ; _maxZ = maxZ; }

    /// <summary>
    /// Franja de búsqueda: desde 1,2 m sobre el nivel hasta el nivel superior (+0,3 m), o 8 m si no hay nivel superior.
    /// Así no se toma el suelo de la propia planta ni el techo de la planta de arriba.
    /// </summary>
    public static CeilingFinder Create(Document doc, Level level)
    {
        double baseZ = level.ProjectElevation;
        var upper = new FilteredElementCollector(doc).OfClass(typeof(Level)).Cast<Level>()
            .Where(l => l.ProjectElevation > baseZ + Mm(1500)).OrderBy(l => l.ProjectElevation).FirstOrDefault();
        var f = new CeilingFinder(baseZ + Mm(1200), upper != null ? upper.ProjectElevation + Mm(300) : baseZ + Mm(8000));
        try
        {
            f.Collect(doc, Transform.Identity, "proyecto");
            foreach (var link in new FilteredElementCollector(doc).OfClass(typeof(RevitLinkInstance)).Cast<RevitLinkInstance>())
            {
                try
                {
                    var linkDoc = link.GetLinkDocument();
                    if (linkDoc is null) { f.Notes.Add($"Vínculo '{link.Name}' no cargado: no se consulta."); continue; }
                    f.Collect(linkDoc, link.GetTotalTransform(), "vínculo " + linkDoc.Title);
                }
                catch (Exception ex) { f.Notes.Add($"Vínculo '{link.Name}': no se pudo leer ({ex.Message})."); }
            }
        }
        catch (Exception ex) { f.Notes.Add("No se pudieron leer techos y forjados: " + ex.Message); }
        return f;
    }

    private void Collect(Document src, Transform t, string label)
    {
        int c0 = _ceilings.Count, f0 = _floors.Count;
        var options = new Options { ComputeReferences = false, DetailLevel = ViewDetailLevel.Coarse };
        void Add(Element e, List<(Solid, BoundingBoxXYZ, string)> into)
        {
            var bb = e.get_BoundingBox(null);
            if (bb is null) return;
            var box = TransformBox(bb, t);
            // Solo lo que está en la franja de la planta (antes de leer su geometría, que es lo costoso)
            if (box.Max.Z < _minZ || box.Min.Z > _maxZ) return;
            try
            {
                foreach (var s in Solids(e.get_Geometry(options)))
                    into.Add((t.IsIdentity ? s : SolidUtils.CreateTransformed(s, t), box, label));
            }
            catch { /* geometría ilegible: se ignora */ }
        }
        foreach (var e in new FilteredElementCollector(src).OfCategory(BuiltInCategory.OST_Ceilings).WhereElementIsNotElementType())
            Add(e, _ceilings);
        foreach (var e in new FilteredElementCollector(src).OfClass(typeof(Floor)).WhereElementIsNotElementType())
            Add(e, _floors);
        Models.Add($"{label}: {_ceilings.Count - c0} techos y {_floors.Count - f0} forjados en la franja de la planta");
    }

    private static BoundingBoxXYZ TransformBox(BoundingBoxXYZ bb, Transform t)
    {
        if (t.IsIdentity) return bb;
        var pts = new[] { bb.Min, bb.Max, new XYZ(bb.Min.X, bb.Max.Y, bb.Min.Z), new XYZ(bb.Max.X, bb.Min.Y, bb.Min.Z),
                          new XYZ(bb.Min.X, bb.Min.Y, bb.Max.Z), new XYZ(bb.Max.X, bb.Max.Y, bb.Min.Z),
                          new XYZ(bb.Min.X, bb.Max.Y, bb.Max.Z), new XYZ(bb.Max.X, bb.Min.Y, bb.Max.Z) }.Select(t.OfPoint).ToList();
        return new BoundingBoxXYZ
        {
            Min = new XYZ(pts.Min(p => p.X), pts.Min(p => p.Y), pts.Min(p => p.Z)),
            Max = new XYZ(pts.Max(p => p.X), pts.Max(p => p.Y), pts.Max(p => p.Z))
        };
    }

    private static IEnumerable<Solid> Solids(GeometryElement? geometry)
    {
        if (geometry is null) yield break;
        foreach (var obj in geometry)
        {
            if (obj is Solid s && s.Volume > 1e-9) yield return s;
            else if (obj is GeometryInstance gi)
                foreach (var inner in Solids(gi.GetInstanceGeometry())) yield return inner;
        }
    }

    /// <summary>
    /// Cota (pies) de la cara inferior del techo sobre (x, y): falso techo si hay; si no, forjado. Null si no hay nada.
    /// Se guarda por celdas de 10 cm: las luminarias cercanas no repiten el cálculo.
    /// </summary>
    public (double Z, string Source)? UndersideAt(double xFt, double yFt)
    {
        var key = ((long)Math.Round(xFt / Mm(100)), (long)Math.Round(yFt / Mm(100)));
        if (_cache.TryGetValue(key, out var hit)) return hit;
        var r = Lowest(_ceilings, xFt, yFt, "falso techo") ?? Lowest(_floors, xFt, yFt, "forjado");
        _cache[key] = r;
        return r;
    }

    /// <summary>
    /// Por qué no se encontró techo sobre (x, y): cuántos techos y forjados tienen ese punto dentro de su rectángulo en
    /// planta, entre qué cotas están y qué dio el corte vertical con cada uno. Solo para el informe.
    /// </summary>
    public string Explain(double xFt, double yFt, double levelZFt)
    {
        double Mmm(double ft) => UnitUtils.ConvertFromInternalUnits(ft, UnitTypeId.Millimeters);
        var parts = new List<string>
        {
            $"franja buscada: de {Mmm(_minZ - levelZFt):0} a {Mmm(_maxZ - levelZFt):0} mm sobre el nivel"
        };
        foreach (var (name, list) in new[] { ("techos", _ceilings), ("forjados", _floors) })
        {
            var over = list.Where(c => xFt >= c.Box.Min.X && xFt <= c.Box.Max.X && yFt >= c.Box.Min.Y && yFt <= c.Box.Max.Y).ToList();
            if (over.Count == 0) { parts.Add($"{name}: ninguno de {list.Count} tiene el punto dentro de su rectángulo en planta"); continue; }
            var detail = new List<string>();
            foreach (var (solid, box, _) in over.Take(4))
            {
                string cut;
                try
                {
                    var line = Line.CreateBound(new XYZ(xFt, yFt, box.Min.Z - 1), new XYZ(xFt, yFt, box.Max.Z + 1));
                    var inter = solid.IntersectWithCurve(line, new SolidCurveIntersectionOptions());
                    cut = inter.SegmentCount == 0 ? "la vertical no lo corta (hueco o borde)"
                        : "cortes en " + string.Join(", ", Enumerable.Range(0, inter.SegmentCount).Select(i =>
                            $"{Mmm(Math.Min(inter.GetCurveSegment(i).GetEndPoint(0).Z, inter.GetCurveSegment(i).GetEndPoint(1).Z) - levelZFt):0} mm"));
                }
                catch (Exception ex) { cut = "no se pudo cortar (" + ex.Message + ")"; }
                detail.Add($"de {Mmm(box.Min.Z - levelZFt):0} a {Mmm(box.Max.Z - levelZFt):0} mm → {cut}");
            }
            parts.Add($"{name}: {over.Count} sobre el punto [{string.Join(" | ", detail)}]");
        }
        return string.Join("; ", parts);
    }

    private readonly Dictionary<(long, long), double?> _slabCache = new();
    private readonly Dictionary<(long, long), double?> _ceilingTopCache = new();

    /// <summary>
    /// Cota (pies) de la cara inferior del FORJADO más bajo sobre (x, y), sin contar los falsos techos. Null si no hay.
    /// Es el límite que no deben atravesar los conductos, tuberías y bandejas colgados. Se guarda por celdas de 5 cm.
    /// </summary>
    public double? SlabUndersideAt(double xFt, double yFt)
    {
        var key = ((long)Math.Round(xFt / Mm(50)), (long)Math.Round(yFt / Mm(50)));
        if (_slabCache.TryGetValue(key, out var hit)) return hit;
        var r = Lowest(_floors, xFt, yFt, "forjado")?.Z;
        _slabCache[key] = r;
        return r;
    }

    /// <summary>Cota (pies) de la cara SUPERIOR del falso techo más alto sobre (x, y). Null si no hay falso techo.</summary>
    public double? CeilingTopAt(double xFt, double yFt)
    {
        var key = ((long)Math.Round(xFt / Mm(50)), (long)Math.Round(yFt / Mm(50)));
        if (_ceilingTopCache.TryGetValue(key, out var hit)) return hit;
        double? best = null;
        foreach (var (solid, box, _) in _ceilings)
        {
            if (xFt < box.Min.X - 1e-6 || xFt > box.Max.X + 1e-6 || yFt < box.Min.Y - 1e-6 || yFt > box.Max.Y + 1e-6) continue;
            var line = Line.CreateBound(new XYZ(xFt, yFt, box.Min.Z - 1), new XYZ(xFt, yFt, box.Max.Z + 1));
            SolidCurveIntersection inter;
            try { inter = solid.IntersectWithCurve(line, new SolidCurveIntersectionOptions()); }
            catch { continue; }
            for (int i = 0; i < inter.SegmentCount; i++)
            {
                var seg = inter.GetCurveSegment(i);
                double z = Math.Max(seg.GetEndPoint(0).Z, seg.GetEndPoint(1).Z);
                if (z < _minZ || z > _maxZ) continue;
                if (best is null || z > best) best = z;
            }
        }
        _ceilingTopCache[key] = best;
        return best;
    }

    private (double Z, string Source)? Lowest(List<(Solid Solid, BoundingBoxXYZ Box, string Source)> list, double x, double y, string what)
    {
        (double Z, string Source)? best = null;
        foreach (var (solid, box, src) in list)
        {
            if (x < box.Min.X - 1e-6 || x > box.Max.X + 1e-6 || y < box.Min.Y - 1e-6 || y > box.Max.Y + 1e-6) continue;
            var line = Line.CreateBound(new XYZ(x, y, box.Min.Z - 1), new XYZ(x, y, box.Max.Z + 1));
            SolidCurveIntersection inter;
            try { inter = solid.IntersectWithCurve(line, new SolidCurveIntersectionOptions()); }
            catch { continue; }
            for (int i = 0; i < inter.SegmentCount; i++)
            {
                var seg = inter.GetCurveSegment(i);
                double z = Math.Min(seg.GetEndPoint(0).Z, seg.GetEndPoint(1).Z);
                if (z < _minZ || z > _maxZ) continue;
                if (best is null || z < best.Value.Z) best = (z, $"{what} ({src})");
            }
        }
        return best;
    }
}
