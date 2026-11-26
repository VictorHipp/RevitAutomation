using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Mechanical;
using Autodesk.Revit.DB.Plumbing;

namespace CadMepRevit.Revit;

/// <summary>
/// Cálculo de la regla «bajo_forjado» (ver Config/PlacementConfig.cs): busca la cara inferior de los forjados
/// que cubren la planta (suelos asociados al nivel inmediatamente superior) en un punto de la planta.
/// Solo LEE el modelo. Si no hay forjado no inventa nada: devuelve null.
/// </summary>
public sealed class UnderSlabPlacer
{
    private readonly List<(Solid Solid, BoundingBoxXYZ Box)> _slabs = new();

    /// <summary>Nivel cuyos suelos forman el forjado de referencia (null si no hay nivel superior).</summary>
    public string? UpperLevelName { get; private set; }
    /// <summary>Número de forjados (suelos) encontrados.</summary>
    public int SlabCount { get; private set; }
    /// <summary>Por qué no se puede aplicar la regla en ningún punto (null si hay forjados).</summary>
    public string? Problem { get; private set; }
    /// <summary>Suelos del nivel superior cuya geometría no se pudo leer (se ignoran y el informe lo dice).</summary>
    public int Unreadable { get; private set; }

    public static UnderSlabPlacer Create(Document doc, Level level)
    {
        try { return CreateCore(doc, level); }
        catch (Exception ex)
        {
            return new UnderSlabPlacer { Problem = "no se pudieron leer los forjados del proyecto (" + ex.Message + ")" };
        }
    }

    private static UnderSlabPlacer CreateCore(Document doc, Level level)
    {
        var p = new UnderSlabPlacer();
        var upper = new FilteredElementCollector(doc).OfClass(typeof(Level)).Cast<Level>()
            .Where(l => l.ProjectElevation > level.ProjectElevation + 1e-6)
            .OrderBy(l => l.ProjectElevation).FirstOrDefault();
        if (upper is null)
        {
            p.Problem = $"el proyecto no tiene ningún nivel por encima de '{level.Name}', así que no hay forjado que cubra la planta";
            return p;
        }
        p.UpperLevelName = upper.Name;
        var floors = new FilteredElementCollector(doc).OfClass(typeof(Floor)).WhereElementIsNotElementType()
            .Cast<Floor>().Where(f => f.LevelId == upper.Id).ToList();
        var options = new Options { ComputeReferences = false, DetailLevel = ViewDetailLevel.Fine };
        int unreadable = 0;
        foreach (var floor in floors)
        {
            try
            {
                var box = floor.get_BoundingBox(null);
                if (box is null) continue;
                var solids = Solids(floor.get_Geometry(options)).ToList();
                foreach (var solid in solids) p._slabs.Add((solid, box));
                if (solids.Count > 0) p.SlabCount++;
            }
            catch { unreadable++; }
        }
        if (unreadable > 0) p.Unreadable = unreadable;
        if (p.SlabCount == 0)
            p.Problem = $"no hay forjados (suelos con geometría) asociados al nivel superior '{upper.Name}' en este proyecto " +
                        "(los modelos vinculados no se consultan)";
        return p;
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

    /// <summary>Cota (pies, coordenadas del proyecto) de la cara inferior más baja bajo el punto (x, y), o null si no hay forjado.</summary>
    public double? UndersideAt(double xFt, double yFt)
    {
        double? lowest = null;
        foreach (var (solid, box) in _slabs)
        {
            if (xFt < box.Min.X - 1e-6 || xFt > box.Max.X + 1e-6 || yFt < box.Min.Y - 1e-6 || yFt > box.Max.Y + 1e-6) continue;
            var line = Line.CreateBound(new XYZ(xFt, yFt, box.Min.Z - 1), new XYZ(xFt, yFt, box.Max.Z + 1));
            SolidCurveIntersection hit;
            try { hit = solid.IntersectWithCurve(line, new SolidCurveIntersectionOptions()); }
            catch { continue; }
            for (int i = 0; i < hit.SegmentCount; i++)
            {
                var seg = hit.GetCurveSegment(i);
                double z = Math.Min(seg.GetEndPoint(0).Z, seg.GetEndPoint(1).Z);
                if (lowest is null || z < lowest) lowest = z;
            }
        }
        return lowest;
    }

    /// <summary>
    /// Mitad de la medida exterior vertical del tramo (pies): diámetro exterior de la tubería, diámetro del
    /// conducto redondo o alto del conducto rectangular/oval. <paramref name="note"/> indica si se ha usado otra medida.
    /// </summary>
    public static double HalfOuterHeight(MEPCurve curve, out string? note)
    {
        note = null;
        double V(BuiltInParameter bip) => curve.get_Parameter(bip) is { HasValue: true } p ? p.AsDouble() : 0;
        switch (curve)
        {
            case Pipe:
            {
                double od = V(BuiltInParameter.RBS_PIPE_OUTER_DIAMETER);
                if (od > 0) return od / 2;
                note = "la tubería no tiene diámetro exterior: se usa el nominal";
                return V(BuiltInParameter.RBS_PIPE_DIAMETER_PARAM) / 2;
            }
            case Duct d:
            {
                bool round = (d.Document.GetElement(d.GetTypeId()) as DuctType)?.Shape == ConnectorProfileType.Round;
                return (round ? V(BuiltInParameter.RBS_CURVE_DIAMETER_PARAM) : V(BuiltInParameter.RBS_CURVE_HEIGHT_PARAM)) / 2;
            }
            default:
                return 0;
        }
    }
}
