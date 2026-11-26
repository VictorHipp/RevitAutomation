using Autodesk.Revit.DB;
using CadMepRevit.Core;

namespace CadMepRevit.Revit;

/// <summary>
/// Dónde va el plano en el proyecto (lo usan los cinco botones de modelar). Si ese mismo DWG/DXF está VINCULADO o
/// IMPORTADO en el proyecto, todo se coloca exactamente encima de él: con su desplazamiento y su giro, aunque el usuario
/// lo haya movido o lo vinculara «centro a centro», solo en una vista o con otra unidad.
/// La posición no se saca de la caja que da Revit (incluye textos, sombreados y restos lejanos, y no existe si el CAD
/// está solo en una vista): se ENCAJA la geometría. Los extremos de las líneas del dibujo tienen que caer sobre los
/// vértices del CAD en Revit; si su transformación no los hace caer, se busca el desplazamiento que sí
/// (<see cref="CadRegistration"/>).
/// Si el plano no está, se prueba con los demás CAD del proyecto (p. ej. la base de arquitectura con las mismas
/// coordenadas). Si nada encaja, se vincula el plano en su sitio y se modela encima de él.
/// </summary>
public static class CadPlacement
{
    private static double Ft(double mm) => UnitUtils.ConvertToInternalUnits(mm, UnitTypeId.Millimeters);
    private static double M(double ft) => UnitUtils.ConvertFromInternalUnits(ft, UnitTypeId.Meters);

    /// <summary>
    /// Transformación (pies, del DWG leído al proyecto) y explicación para el informe. T = null: coordenadas del DWG.
    /// Present = este mismo archivo está vinculado o importado en el proyecto (aunque no se haya podido usar).
    /// </summary>
    public static (Transform? T, string Note, bool Present) Find(Document doc, string cadPath, CadDrawing drawing, double unitToMm)
    {
        string file = Path.GetFileName(cadPath);
        var (lines, blocks) = CadRegistration.DrawingPoints(drawing, Ft(unitToMm), Ft(5));
        string counts = $"{lines.Count} puntos de líneas y centros de círculos, {blocks.Count} de inserción de bloques";
        string uncheckedPoints = $"{counts}; 0 coincidencias comprobadas.";
        bool present = false;
        try
        {
            var all = new FilteredElementCollector(doc).OfClass(typeof(ImportInstance)).Cast<ImportInstance>().ToList();
            string NameOf(ImportInstance ii) => doc.GetElement(ii.GetTypeId())?.Name ?? "";
            var same = all.Where(ii => string.Equals(NameOf(ii), file, StringComparison.OrdinalIgnoreCase)).ToList();
            present = same.Count > 0;
            if (lines.Count + blocks.Count == 0)
                return (null, $"Posición: el dibujo carece de puntos con los que situarlo; se usan sus coordenadas. {uncheckedPoints}", present);

            string? lastNote = null;
            foreach (var ii in same)
            {
                var (t, note) = Match(doc, ii, lines, blocks, counts, sameFile: true);
                if (t != null)
                    return (t, $"Posición: encima de '{file}', {(ii.IsLinked ? "vinculado" : "importado")} en el proyecto {note} " +
                               (IsIdentity(t) ? "Coincide con las coordenadas del DWG." : "El CAD está movido respecto a sus coordenadas: TODO se coloca encima de él.") +
                               " Si lo mueve después, lo modelado no se mueve con él.", true);
                lastNote = note;
            }
            // Otros CAD del proyecto (la base de arquitectura, el plano de otra instalación…): solo si casan muchos puntos.
            foreach (var ii in all.Except(same).Take(10))
            {
                var (t, note) = Match(doc, ii, lines, blocks, counts, sameFile: false);
                if (t != null)
                    return (t, $"Posición: el dibujo de '{file}' coincide con el CAD '{NameOf(ii)}' {note} " +
                               "Se coloca todo encima de ese CAD.", present);
            }
            if (same.Count == 0)
                return (null, $"Posición: '{file}' no está vinculado ni importado en el proyecto" +
                              (all.Count > 0 ? $" y su dibujo no coincide con ninguno de los {all.Count} CAD que hay" : "") +
                              $"; se usan las coordenadas del propio DWG. {uncheckedPoints}", false);
            return (null, $"Posición: '{file}' está en el proyecto, pero {lastNote} Se usan las coordenadas del DWG. " +
                          $"{counts}. Compruebe que es el mismo archivo (no otra versión del plano).", true);
        }
        catch (Exception ex) { return (null, $"Posición: no se pudo leer el CAD vinculado ({ex.Message}); se usan las coordenadas del DWG. {uncheckedPoints}", present); }
    }

    /// <summary>Vértices e inserciones del CAD en el proyecto (pies), también para vínculos de una sola vista.</summary>
    private static (List<(double X, double Y)> Vertices, List<(double X, double Y)> Inserts) CadPoints(Document doc, ImportInstance ii)
    {
        var vertices = new List<(double X, double Y)>();
        var inserts = new List<(double X, double Y)>();
        void Walk(GeometryElement? g, Transform parent, bool insideImport)
        {
            if (g is null) return;
            void Put(XYZ local)
            {
                var p = parent.OfPoint(local);
                vertices.Add((p.X, p.Y));
            }
            foreach (var o in g)
            {
                if (o is PolyLine pl) foreach (var p in pl.GetCoordinates()) Put(p);
                else if (o is Arc arc && !arc.IsBound) Put(arc.Center);
                else if (o is Curve c && c.IsBound) { Put(c.GetEndPoint(0)); Put(c.GetEndPoint(1)); }
                else if (o is GeometryInstance gi)
                {
                    var toProject = parent.Multiply(gi.Transform);
                    if (insideImport) inserts.Add((toProject.Origin.X, toProject.Origin.Y));
                    Walk(gi.GetSymbolGeometry(), toProject, true);
                }
            }
        }
        Walk(ii.get_Geometry(new Options()), Transform.Identity, false);
        if (vertices.Count + inserts.Count == 0 && ii.OwnerViewId != ElementId.InvalidElementId && doc.GetElement(ii.OwnerViewId) is View view)
            Walk(ii.get_Geometry(new Options { View = view }), Transform.Identity, false);
        return (vertices, inserts);
    }

    /// <summary>
    /// Posición de ese CAD que pone el dibujo encima de él, o null y por qué no. Primero su propia transformación (con
    /// la escala corregida si se vinculó con otra unidad); si no casa, se busca el desplazamiento por los puntos.
    /// </summary>
    private static (Transform? T, string Note) Match(Document doc, ImportInstance ii,
        List<(double X, double Y)> lines, List<(double X, double Y)> blocks, string counts, bool sameFile)
    {
        var (vertices, inserts) = CadPoints(doc, ii);
        if (vertices.Count + inserts.Count == 0)
            return (null, "Revit no da su geometría (¿descargado u oculto?). 0 coincidencias comprobadas.");
        var lineReg = new CadRegistration(vertices, Ft(10));
        var insertReg = new CadRegistration(inserts, Ft(10));
        string CheckedAt(Transform t)
        {
            int Hits(List<(double X, double Y)> points, CadRegistration registration) => points.Count(p =>
            {
                var q = Apply(t, p, false);
                return registration.Hit(q.X, q.Y);
            });
            return $"{counts}; caen sobre el CAD {Hits(lines, lineReg)}/{lines.Count} de líneas y círculos " +
                   $"y {Hits(blocks, insertReg)}/{blocks.Count} de bloques.";
        }

        var pts = lines.Concat(blocks).ToList();
        var reg = lines.Count == 0 && inserts.Count > 0 ? insertReg : blocks.Count == 0 ? lineReg
            : new CadRegistration(vertices.Concat(inserts), Ft(10));
        var results = MatchPoints(ii, pts, reg, sameFile);
        // El destino puede exponer solo vértices o tener un conjunto diferente de bloques (p. ej. otra instalación).
        if (results.T is null && lines.Count > 0 && blocks.Count > 0)
        {
            if (lines.Count >= 3 && vertices.Count > 0) results = MatchPoints(ii, lines, lineReg, sameFile);
            if (results.T is null && blocks.Count >= 3 && inserts.Count > 0) results = MatchPoints(ii, blocks, insertReg, sameFile);
        }
        return results.T is { } found ? (found, $"{results.Note} {CheckedAt(found)}") : results;
    }

    private static (Transform? T, string Note) MatchPoints(ImportInstance ii, List<(double X, double Y)> pts,
        CadRegistration reg, bool sameFile)
    {
        var t0 = ii.GetTotalTransform();
        double angle = Math.Atan2(t0.BasisX.Y, t0.BasisX.X) * 180 / Math.PI;
        // Si se vinculó con otra unidad, el tamaño difiere en un factor de unidades: se corrige la escala.
        var cadExt = CadRegistration.Extent(reg.Points);
        var ourExt = CadRegistration.Extent(pts.Select(p => Apply(t0, p, true)));
        double factor = cadExt is { Size: > 0 } ce && ourExt is { } oe ? CadRegistration.UnitFactor(oe.Size / ce.Size) : 1;
        var scales = Math.Abs(factor - 1) > 1e-9 ? new[] { factor, 1.0 } : new[] { 1.0 };
        bool few = pts.Count < 10;
        int Need(int tried) => sameFile ? Math.Max(Math.Min(6, tried), (int)Math.Ceiling(0.3 * tried)) : Math.Max(20, (int)Math.Ceiling(0.4 * tried));
        string best = "no encaja con el dibujo.";
        int bestHits = -1;
        foreach (double f in scales)
        {
            var t = Math.Abs(f - 1) > 1e-9 ? t0.ScaleBasis(1 / f) : t0;
            string scaleNote = Math.Abs(f - 1) > 1e-9 ? $" Se vinculó con otra unidad que la usada al leerlo (escala {1 / f:0.####}): se ha corregido." : "";
            string Pos() => $"(desplazado {M(t.Origin.X):0.###} m, {M(t.Origin.Y):0.###} m, girado {angle:0.##}°).";
            // 1) Su propia transformación.
            var mapped = pts.Select(p => Apply(t, p, false)).ToList();
            var (hits, tried) = reg.Check(mapped, 0, 0);
            if (hits >= Need(tried) && hits * 10 >= tried * 5)
                return (t, $"{Pos()} Comprobado: {hits} de {tried} puntos del dibujo caen sobre él.{scaleNote}");
            if (hits > bestHits) { bestHits = hits; best = $"solo {hits} de {tried} puntos del dibujo caen sobre él con su posición {Pos()}"; }
            // 2) Buscar el desplazamiento por los puntos (mismo giro y escala que el CAD).
            var linear = pts.Select(p => Apply(t, p, true)).ToList();
            if (reg.FindOffset(linear) is { } off && off.Hits >= Need(off.Tried))
            {
                var found = Transform.CreateTranslation(new XYZ(off.Dx, off.Dy, t.Origin.Z));
                found.BasisX = t.BasisX; found.BasisY = t.BasisY; found.BasisZ = t.BasisZ;
                return (found, $"(desplazado {M(off.Dx):0.###} m, {M(off.Dy):0.###} m, girado {angle:0.##}°; posición hallada encajando el dibujo: " +
                               $"{off.Hits} de {off.Tried} puntos caen sobre él).{scaleNote}");
            }
            if (few && sameFile)
            {
                // Con pocos puntos también se puede comprobar el contorno del mismo archivo.
                var e = CadRegistration.Extent(mapped);
                if (e is { } a && cadExt is { } c && c.Size > 0 && a.Size / c.Size is > 0.7 and < 1.3 &&
                    Math.Sqrt(Math.Pow(a.CenterX - c.CenterX, 2) + Math.Pow(a.CenterY - c.CenterY, 2)) < Math.Max(Ft(2000), 0.15 * c.Size))
                    return (t, Pos() + " Comprobado por su contorno (hay pocos puntos)." + scaleNote);
            }
        }
        return (null, best);
    }

    private static (double X, double Y) Apply(Transform t, (double X, double Y) p, bool linearOnly)
    {
        double x = t.BasisX.X * p.X + t.BasisY.X * p.Y, y = t.BasisX.Y * p.X + t.BasisY.Y * p.Y;
        return linearOnly ? (x, y) : (x + t.Origin.X, y + t.Origin.Y);
    }

    private static bool IsIdentity(Transform t) =>
        new XYZ(t.Origin.X, t.Origin.Y, 0).GetLength() < Ft(1) && Math.Abs(t.BasisX.X - 1) < 1e-6 && Math.Abs(t.BasisX.Y) < 1e-6 && Math.Abs(t.BasisY.Y - 1) < 1e-6;

    /// <summary>
    /// Vincula el plano en el proyecto, en la vista de planta del nivel y con la unidad con la que se ha leído (ya
    /// corregida si la cabecera era absurda). Por defecto en su sitio («Automático: origen a origen interno»); con
    /// <paramref name="centered"/> «centro a centro» (dibujos a más de 10 km del origen, que Revit no admite en su sitio);
    /// con <paramref name="place"/>, se mueve y gira a esa posición (la del CAD con el que coincide).
    /// Necesita que NO haya una transacción abierta (abre la suya). Devuelve la explicación y si se vinculó.
    /// </summary>
    public static (string Note, bool Linked) LinkInPlace(Document doc, string cadPath, DrawingUnit unit, ElementId levelId,
        bool centered = false, Transform? place = null)
    {
        string file = Path.GetFileName(cadPath);
        try
        {
            var view = new FilteredElementCollector(doc).OfClass(typeof(ViewPlan)).Cast<ViewPlan>()
                .Where(v => !v.IsTemplate && v.GenLevel?.Id == levelId)
                .OrderBy(v => v.ViewType == ViewType.FloorPlan ? 0 : v.ViewType == ViewType.CeilingPlan ? 1 : 2).FirstOrDefault();
            var options = new DWGImportOptions
            {
                Placement = centered ? ImportPlacement.Centered : ImportPlacement.Origin,
                Unit = unit switch
                {
                    DrawingUnit.Metros => ImportUnit.Meter,
                    DrawingUnit.Centimetros => ImportUnit.Centimeter,
                    DrawingUnit.Pulgadas => ImportUnit.Inch,
                    DrawingUnit.Pies => ImportUnit.Foot,
                    _ => ImportUnit.Millimeter
                },
                ThisViewOnly = view != null,
                ColorMode = ImportColorMode.Preserved
            };
            using var t = new Transaction(doc, "CAD-REV: vincular el plano en su sitio");
            t.Start();
            bool ok = doc.Link(cadPath, options, view ?? doc.ActiveView, out ElementId id);
            if (!ok || id == ElementId.InvalidElementId)
            {
                t.RollBack();
                return ($"Posición: '{file}' no estaba vinculado y Revit no pudo vincularlo. " +
                        "Vincúlelo a mano (Insertar > Vincular CAD) para verlo debajo de lo modelado.", false);
            }
            string moved = "";
            if (place != null && !IsIdentity(place) && doc.GetElement(id) is ImportInstance ii)
            {
                bool pinned = ii.Pinned;
                if (pinned) ii.Pinned = false;
                double angle = Math.Atan2(place.BasisX.Y, place.BasisX.X);
                if (Math.Abs(angle) > 1e-9) ElementTransformUtils.RotateElement(doc, id, Line.CreateBound(XYZ.Zero, XYZ.BasisZ), angle);
                var shift = new XYZ(place.Origin.X, place.Origin.Y, 0) - new XYZ(ii.GetTotalTransform().Origin.X, ii.GetTotalTransform().Origin.Y, 0);
                if (shift.GetLength() > 1e-9) ElementTransformUtils.MoveElement(doc, id, shift);
                if (pinned) ii.Pinned = true;
                moved = ", movido encima del CAD con el que coincide";
            }
            t.Commit();
            return ($"Vinculado '{file}' ({(centered ? "centro a centro: el dibujo está a más de 10 km del origen" : "origen a origen interno")}, " +
                    $"en {DrawingUnits.Label(unit)}{moved}" + (view != null ? $", solo en la vista '{view.Name}'" : "") + ").", true);
        }
        catch (Exception ex)
        {
            return ($"Posición: '{file}' no estaba vinculado y no se pudo vincular ({ex.Message}).", false);
        }
    }
}
