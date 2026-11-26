using ACadSharp;
using ACadSharp.Entities;
using ACadSharp.IO;

namespace CadMepRevit.Core;

/// <summary>
/// Lee DWG o DXF con ACadSharp (sin abrir AutoCAD ni importar nada en Revit).
/// Solo se lee el espacio modelo. Las líneas y polilíneas se convierten en segmentos
/// rectos; las referencias de bloque se guardan como puntos (posibles accesorios).
/// </summary>
public static class CadReader
{
    private const int MaxEntities = 300000;
    private const int MaxDepth = 16;
    /// <summary>
    /// Giro máximo de una curva para tratarla como un codo dibujado (se guarda su esquina).
    /// Revit no crea codos de más de unos 95°; con más giro la curva se deja como cuerdas.
    /// </summary>
    private const double MaxCornerSweep = 100 * Math.PI / 180;

    public sealed class Options
    {
        /// <summary>Si es true también se leen las líneas que están dentro de los bloques.</summary>
        public bool ReadLinesInsideBlocks { get; init; }
    }

    public static CadDrawing Read(string path, Options? options = null)
    {
        options ??= new Options();
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
            throw new CadReadException(
                "No se encuentra el archivo CAD.",
                "Compruebe que la ruta existe y que tiene permiso para leerla: " + path);

        string ext = System.IO.Path.GetExtension(path).ToLowerInvariant();
        CadDocument document;
        try
        {
            document = ext switch
            {
                ".dwg" => DwgReader.Read(path),
                ".dxf" => DxfReader.Read(path),
                _ => throw new CadReadException(
                    "Formato no admitido: " + ext,
                    "Seleccione un archivo .dwg o .dxf.")
            };
        }
        catch (CadReadException) { throw; }
        catch (IOException ex)
        {
            throw new CadReadException(
                "No se pudo abrir el archivo CAD (puede estar abierto o bloqueado por otro programa).",
                "Cierre el archivo en AutoCAD u otro programa, o cópielo a una carpeta local y vuelva a intentarlo. Detalle: " + ex.Message);
        }
        catch (Exception ex)
        {
            throw new CadReadException(
                "El lector no pudo interpretar el archivo CAD.",
                "Puede ser una versión de DWG no admitida o un archivo dañado. En AutoCAD use 'Guardar como' DWG 2018 o DXF y vuelva a intentarlo. Detalle: " + ex.Message);
        }

        return Extract(document, path, options);
    }

    public static CadDrawing Extract(CadDocument document, string path, Options? options = null)
    {
        options ??= new Options();
        var result = new CadDrawing { Path = path };
        var insUnits = document.Header.InsUnits;
        result.HeaderUnitText = insUnits.ToString();
        result.HeaderUnit = insUnits.ToString() switch
        {
            "Millimeters" => DrawingUnit.Milimetros,
            "Centimeters" => DrawingUnit.Centimetros,
            "Meters" => DrawingUnit.Metros,
            "Inches" => DrawingUnit.Pulgadas,
            "Feet" => DrawingUnit.Pies,
            _ => null
        };

        foreach (var layer in document.Layers)
            if (!string.IsNullOrEmpty(layer.Name)) result.AllLayers.Add(layer.Name);

        var frozenOrOff = new HashSet<string>(
            document.Layers.Where(l => !l.IsOn).Select(l => l.Name), StringComparer.OrdinalIgnoreCase);
        if (frozenOrOff.Count > 0)
            result.Warnings.Add("Capas apagadas en el CAD (se leen igualmente; desactívelas en el mapeo si no las quiere): "
                                + string.Join(", ", frozenOrOff.Take(10)) + (frozenOrOff.Count > 10 ? "..." : ""));

        int count = 0;
        Walk(document.ModelSpace.Entities, result, options, "", 0, ref count);
        LinkLeaders(result);

        if (result.ArcSegmentsApproximated > 0)
            result.Warnings.Add(result.CurveCount + " curvas (arcos y tramos curvos de polilínea) leídas como " +
                result.ArcSegmentsApproximated + " cuerdas rectas. Las que unen dos tramos rectos (un codo dibujado) " +
                "se convierten en una esquina y Revit pone el codo; las demás se quedan como cuerdas.");
        if (result.IgnoredEntityCounts.Count > 0)
            result.Warnings.Add("Entidades no convertidas (textos, cotas, círculos, sombreados...): " +
                string.Join(", ", result.IgnoredEntityCounts.OrderByDescending(k => k.Value).Take(8).Select(k => k.Key + "=" + k.Value)));
        if (result.Segments.Count == 0)
            result.Warnings.Add("No se encontró ninguna línea ni polilínea en el espacio modelo. Si las tuberías están dentro de bloques, active 'Leer líneas dentro de bloques'.");
        return result;
    }

    private static void Walk(IEnumerable<Entity> entities, CadDrawing result, Options options,
        string inheritedLayer, int depth, ref int count)
    {
        foreach (var entity in entities)
        {
            if (entity is null) continue;
            if (++count > MaxEntities)
                throw new CadReadException(
                    "El dibujo tiene demasiadas entidades (más de " + MaxEntities + ").",
                    "Limpie el dibujo (PURGE / borrar capas no MEP) o divídalo en varios archivos.");

            string own = entity.Layer?.Name ?? "";
            string layer = (own.Length == 0 || own == "0") && inheritedLayer.Length > 0 ? inheritedLayer : own;
            // Inventario: cada entidad recibe un número; los tramos, bloques, textos y cotas lo guardan.
            int index = count;
            result.Entities.Add(new CadEntityInfo(index, entity.ObjectName ?? entity.GetType().Name, layer, depth));
            CaptureArrowGeometry(entity, result, layer, 0, false);
            if (depth == 0) CaptureFixtures(entity, result, layer, index);
            if (depth == 0 && entity is Circle circle)
                result.Circles.Add(new CadCircle(Ocs.ToWcs(circle.Normal, circle.Center.X, circle.Center.Y, circle.Center.Z),
                    circle.Radius, layer, index, Math.Abs(Math.Abs(circle.Normal.Z) - 1) < SlopeRules.Epsilon));

            switch (entity)
            {
                case Line line:
                    Add(result, layer, ToP(line.StartPoint), ToP(line.EndPoint), "LINE", index);
                    break;

                case LwPolyline lw:
                {
                    // Los vértices están en el sistema de coordenadas del objeto (OCS): una polilínea
                    // reflejada (SIMETRÍA) tiene la normal hacia abajo y su X invertida.
                    var v = lw.Vertices.Select(x => (x.Location.X, x.Location.Y, x.Bulge)).ToList();
                    AddPolyline(result, layer, v, (x, y) => Ocs.ToWcs(lw.Normal, x, y, lw.Elevation), lw.IsClosed, "LWPOLYLINE", index);
                    break;
                }

                case Polyline2D p2:
                {
                    var v = p2.Vertices.Select(x => (x.Location.X, x.Location.Y, x.Bulge)).ToList();
                    AddPolyline(result, layer, v, (x, y) => Ocs.ToWcs(p2.Normal, x, y, p2.Elevation), p2.IsClosed, "POLYLINE", index);
                    break;
                }

                case Polyline3D p3:
                {
                    var v = p3.Vertices.Select(x => ToP(x.Location)).ToList();
                    int segments = p3.IsClosed ? v.Count : v.Count - 1;
                    for (int i = 0; i < segments; i++)
                        Add(result, layer, v[i], v[(i + 1) % v.Count], "POLYLINE3D", index);
                    break;
                }

                case Arc arc:
                {
                    // Arco (curva de una tubería o conducto): se sustituye por cuerdas de 90° como máximo.
                    // Si gira poco (un codo dibujado) se guarda además su esquina: el punto donde se cortan
                    // las tangentes de sus extremos, a distancia radio / cos(giro / 2) del centro.
                    double sweep = arc.EndAngle - arc.StartAngle;
                    while (sweep <= 0) sweep += 2 * Math.PI;
                    int n = Math.Max(1, (int)Math.Ceiling(sweep / (Math.PI / 2) - 1e-9));
                    P3 At(double a, double radius) => Ocs.ToWcs(arc.Normal, arc.Center.X + radius * Math.Cos(a), arc.Center.Y + radius * Math.Sin(a), arc.Center.Z);
                    int id = ++result.CurveCount;
                    P3? corner = sweep <= MaxCornerSweep ? At(arc.StartAngle + sweep / 2, arc.Radius / Math.Cos(sweep / 2)) : null;
                    for (int i = 0; i < n; i++)
                        Add(result, layer, At(arc.StartAngle + sweep * i / n, arc.Radius), At(arc.StartAngle + sweep * (i + 1) / n, arc.Radius), "ARC", index, id, corner);
                    result.ArcSegmentsApproximated += n;
                    break;
                }

                case Insert insert:
                {
                    if (depth == 0 && IsUnloadedXref(insert))
                    {
                        result.Warnings.Add($"El plano tiene una referencia externa (XREF) sin contenido: '{insert.Block?.Name}'. " +
                            "Lo que hay en ella (normalmente la arquitectura: muros, puertas…) NO está en este archivo. " +
                            "Cómo corregirlo: en AutoCAD abra el plano con la referencia cargada y use XREF > Unir (Bind), o importe también el archivo de arquitectura.");
                        result.UnloadedXrefs.Add(index);
                        break;
                    }
                    if (depth == 0)
                    {
                        result.Blocks.Add(new CadBlockRef
                        {
                            Layer = layer,
                            BlockName = insert.Block?.Name ?? "(sin nombre)",
                            Position = ToP(insert.InsertPoint),
                            RotationRadians = insert.Rotation,
                            Footprint = MeasureFootprint(insert),
                            EntityIndex = index
                        });
                        // Rótulos dentro de bloques (etiquetas de tubería): atributos y, si no se leen las líneas de
                        // dentro de los bloques, también sus textos, para las medidas del modelado.
                        CaptureBlockTexts(insert, result, layer, index, !options.ReadLinesInsideBlocks);
                    }
                    if (options.ReadLinesInsideBlocks && depth < MaxDepth)
                    {
                        try { Walk(insert.Explode(), result, options, layer, depth + 1, ref count); }
                        catch (Exception ex)
                        {
                            result.Warnings.Add("No se pudo leer el contenido del bloque " + (insert.Block?.Name ?? "?") + ": " + ex.Message);
                        }
                    }
                    break;
                }

                default:
                {
                    // No se convierten en elementos (se siguen contando igual que antes). Los textos y las
                    // cotas se guardan además para el inventario y para las medidas del modelado.
                    string kind = entity.ObjectName ?? entity.GetType().Name;
                    result.IgnoredEntityCounts[kind] = result.IgnoredEntityCounts.GetValueOrDefault(kind) + 1;
                    CaptureAnnotation(entity, result, layer, index);
                    break;
                }
            }
        }
    }

    // Canal auxiliar: no altera segmentos, textos, bloques ni inventario existentes.
    private static void CaptureArrowGeometry(Entity e, CadDrawing d, string inherited, int depth, bool inside)
    {
        if (depth > MaxDepth) return;
        string layer = e.Layer?.Name is string own && own != "0" ? own : inherited;
        void Edge(P3 a, P3 b) => d.AnnotationGeometry.Add(new CadSegment { Layer = layer, Start = a, End = b, Source = "PUNTA" });
        void Polygon(IEnumerable<P3> points)
        {
            var v = points.Distinct().ToList();
            if (v.Count != 3) return;
            for (int i = 0; i < 3; i++) Edge(v[i], v[(i + 1) % 3]);
        }
        try
        {
            switch (e)
            {
                case Insert ins when ins.Block != null:
                    foreach (var child in ins.Explode()) CaptureArrowGeometry(child, d, layer, depth + 1, true);
                    break;
                case Line l when inside: Edge(ToP(l.StartPoint), ToP(l.EndPoint)); break;
                case Solid solid:
                    Polygon(new[] { ToP(solid.FirstCorner), ToP(solid.SecondCorner), ToP(solid.ThirdCorner), ToP(solid.FourthCorner) }); break;
                case Hatch hatch:
                    foreach (var path in hatch.Paths)
                        Polygon(path.GetPoints(3).Select(p => Ocs.ToWcs(hatch.Normal, p.X, p.Y, hatch.Elevation)));
                    break;
                case LwPolyline poly:
                    var v = poly.Vertices;
                    for (int i = 0; i < v.Count - 1; i++)
                    {
                        P3 a = Ocs.ToWcs(poly.Normal, v[i].Location.X, v[i].Location.Y, poly.Elevation);
                        P3 b = Ocs.ToWcs(poly.Normal, v[i + 1].Location.X, v[i + 1].Location.Y, poly.Elevation);
                        if (inside) Edge(a, b);
                        if (v[i].StartWidth > v[i].EndWidth && v[i].StartWidth > 0)
                            d.Arrows.Add(new CadArrow(layer, a, b, "ancho variable"));
                        else if (v[i].EndWidth > v[i].StartWidth && v[i].EndWidth > 0)
                            d.Arrows.Add(new CadArrow(layer, b, a, "ancho variable"));
                    }
                    if (inside && poly.IsClosed && v.Count > 2)
                        Edge(Ocs.ToWcs(poly.Normal, v[^1].Location.X, v[^1].Location.Y, poly.Elevation),
                            Ocs.ToWcs(poly.Normal, v[0].Location.X, v[0].Location.Y, poly.Elevation));
                    break;
                case Leader leader when leader.Vertices.Count >= 2:
                    d.Arrows.Add(new CadArrow(layer, ToP(leader.Vertices[1]), ToP(leader.Vertices[0]), "LEADER")); break;
                case MultiLeader ml when ml.ContextData is { } ctx:
                    foreach (var line in ctx.LeaderRoots.SelectMany(r => r.Lines))
                        if (line.Points.Count >= 2) d.Arrows.Add(new CadArrow(layer, ToP(line.Points[1]), ToP(line.Points[0]), "MLEADER"));
                    break;
            }
        }
        catch (Exception ex) { d.Warnings.Add($"Geometría de flecha ilegible en capa '{layer}': {ex.Message}"); }
    }

    /// <summary>Guarda los textos y las cotas (sin convertirlos en nada). Un fallo aquí nunca impide leer el plano.</summary>
    private static void CaptureAnnotation(Entity entity, CadDrawing result, string layer, int index)
    {
        try
        {
            switch (entity)
            {
                case MText m:
                    result.Texts.Add(ReadMText(m, layer, index));
                    break;
                case TextEntity t:
                    result.Texts.Add(ReadText(t, layer, index, entity.ObjectName ?? "TEXT"));
                    break;
                case MultiLeader ml when ml.ContextData is { } ctx:
                {
                    // Directriz múltiple: el texto y el punto al que señala su flecha (sobre el tubo).
                    string plain = CleanText(StripMText(ctx.TextLabel ?? ""));
                    if (plain.Length == 0) break;
                    P3? arrow = ctx.LeaderRoots.SelectMany(r => r.Lines).SelectMany(l => l.Points).Select(p => (P3?)ToP(p)).FirstOrDefault();
                    var pos = ToP(ctx.TextLocation);
                    result.Texts.Add(new CadText
                    {
                        EntityIndex = index, Layer = layer, Source = "MULTILEADER", Value = plain, Position = pos,
                        Height = ctx.TextHeight, RotationRad = ctx.TextRotation, Anchor = arrow, Handle = entity.Handle
                    });
                    break;
                }
                case Leader ld when ld.Vertices.Count >= 2:
                    result.Leaders.Add((ld.AssociatedAnnotation?.Handle ?? 0, ToP(ld.Vertices[0]), ToP(ld.Vertices[^1])));
                    break;
                case Dimension d:
                {
                    P3? a = null, b = null, dir = null;
                    if (d is DimensionAligned al)
                    {
                        a = ToP(al.FirstPoint);
                        b = ToP(al.SecondPoint);
                        dir = d is DimensionLinear lin
                            ? new P3(Math.Cos(lin.Rotation), Math.Sin(lin.Rotation), 0)
                            : new P3(b.Value.X - a.Value.X, b.Value.Y - a.Value.Y, 0).Normalized();
                    }
                    double measured;
                    try { measured = d.Measurement; } catch { measured = double.NaN; }
                    result.Dimensions.Add(new CadDimension
                    {
                        EntityIndex = index, Layer = layer, Kind = DimensionKind(d), Measurement = measured, Text = d.Text ?? "",
                        FirstPoint = a, SecondPoint = b, Direction = dir, IsPlanar = Math.Abs(d.Normal.Z) > 0.999
                    });
                    break;
                }
            }
        }
        catch { /* anotación ilegible: queda en el inventario como entidad, sin datos */ }
    }

    /// <summary>Ancho aproximado de un carácter respecto a la altura del texto (fuentes técnicas habituales).</summary>
    internal const double CharWidthFactor = 0.75;

    /// <summary>TEXT o ATTRIB: valor, posición de inserción y centro aproximado según su justificación.</summary>
    internal static CadText ReadText(TextEntity t, string layer, int index, string source)
    {
        string value = CleanText(t.Value ?? "");
        double h = t.Height > 0 ? t.Height : 1;
        double wf = t.WidthFactor > 0 ? t.WidthFactor : 1;
        double w = Math.Max(1, value.Length) * h * CharWidthFactor * wf;
        double rot = t.Rotation;
        double cx = Math.Cos(rot), cy = Math.Sin(rot);
        // Punto base en el sistema del objeto (OCS): inserción si es «izquierda / línea base»; si no, el de alineación.
        var ins = t.InsertPoint;
        var al = t.AlignmentPoint;
        bool simple = t.HorizontalAlignment == TextHorizontalAlignment.Left && t.VerticalAlignment == TextVerticalAlignmentType.Baseline;
        double bx, by, along, up;
        switch (t.HorizontalAlignment)
        {
            case TextHorizontalAlignment.Aligned:
            case TextHorizontalAlignment.Fit:
                bx = (ins.X + al.X) / 2; by = (ins.Y + al.Y) / 2; along = 0; break;
            case TextHorizontalAlignment.Center:
                bx = al.X; by = al.Y; along = 0; break;
            case TextHorizontalAlignment.Middle:
                bx = al.X; by = al.Y; along = 0; break;
            case TextHorizontalAlignment.Right:
                bx = al.X; by = al.Y; along = -w / 2; break;
            default:
                bx = simple ? ins.X : al.X; by = simple ? ins.Y : al.Y; along = w / 2; break;
        }
        if (t.HorizontalAlignment == TextHorizontalAlignment.Middle) up = 0;
        else up = t.VerticalAlignment switch
        {
            TextVerticalAlignmentType.Middle => 0,
            TextVerticalAlignmentType.Top => -h / 2,
            _ => h / 2
        };
        double ox = bx + along * cx - up * cy, oy = by + along * cy + up * cx;
        var normal = t.Normal;
        return new CadText
        {
            EntityIndex = index, Layer = layer, Source = source, Value = value, Height = t.Height,
            Position = Ocs.ToWcs(normal, ins.X, ins.Y, ins.Z),
            CenterPoint = Ocs.ToWcs(normal, ox, oy, ins.Z),
            RotationRad = Math.Atan2(Ocs.ToWcs(normal, cx, cy, 0).Y, Ocs.ToWcs(normal, cx, cy, 0).X),
            Handle = t.Handle, WidthApprox = w, HeightApprox = h
        };
    }

    /// <summary>MTEXT: texto sin formato, punto de inserción (el de su anclaje) y centro aproximado del bloque de texto.</summary>
    internal static CadText ReadMText(MText m, string layer, int index)
    {
        // Se quitan los códigos de formato del texto original (el PlainText del lector deja restos como «T1.1;»).
        string raw = m.Value ?? "";
        string plain = StripMText(raw);
        if (string.IsNullOrWhiteSpace(plain))
            try { plain = m.PlainText ?? ""; } catch { plain = ""; }
        string value = CleanText(plain);
        double h = m.Height > 0 ? m.Height : 1;
        // Líneas: los saltos de párrafo (\P) del original.
        var lines = System.Text.RegularExpressions.Regex.Split(raw, @"\\P").Select(l => CleanText(StripMText(l))).Where(l => l.Length > 0).ToList();
        int rows = Math.Max(1, lines.Count);
        double w = Math.Max(1, lines.Count > 0 ? lines.Max(l => l.Length) : value.Length) * h * CharWidthFactor;
        if (m.RectangleWidth > 0) w = Math.Min(w, m.RectangleWidth);
        double th = h * (1 + (rows - 1) * 1.67);
        var dir = m.AlignmentPoint;
        double rot = Math.Abs(dir.X) + Math.Abs(dir.Y) > 1e-9 ? Math.Atan2(dir.Y, dir.X) : m.Rotation;
        double cx = Math.Cos(rot), cy = Math.Sin(rot);
        string a = m.AttachmentPoint.ToString();
        double along = a.EndsWith("Left") ? w / 2 : a.EndsWith("Right") ? -w / 2 : 0;
        double up = a.StartsWith("Top") ? -th / 2 : a.StartsWith("Bottom") ? th / 2 : 0;
        var ins = ToP(m.InsertPoint);
        return new CadText
        {
            EntityIndex = index, Layer = layer, Source = "MTEXT", Value = value, Height = m.Height,
            Position = ins,
            CenterPoint = new P3(ins.X + along * cx - up * cy, ins.Y + along * cy + up * cx, ins.Z),
            RotationRad = rot,
            Handle = m.Handle, WidthApprox = w, HeightApprox = th
        };
    }

    /// <summary>
    /// Atributos de un bloque (etiquetas «Ø25» en bloques de rótulo) y, si se pide, los textos de su definición.
    /// Un fallo aquí nunca impide leer el plano.
    /// </summary>
    private static void CaptureBlockTexts(Insert insert, CadDrawing result, string layer, int index, bool definitionTexts)
    {
        try
        {
            foreach (var att in insert.Attributes)
            {
                if (att is null || string.IsNullOrWhiteSpace(att.Value)) continue;
                string own = att.Layer?.Name ?? "";
                result.Texts.Add(ReadText(att, own.Length == 0 || own == "0" ? layer : own, index, "ATTRIB"));
            }
        }
        catch { /* atributos ilegibles: se ignoran */ }
        if (!definitionTexts) return;
        try
        {
            if (insert.Block?.Entities is not { } defs || !defs.Any(e => e is MText || (e is TextEntity && e is not AttributeDefinition))) return;
            foreach (var e in insert.Explode())
            {
                if (e is AttributeDefinition) continue;
                string own = e.Layer?.Name ?? "";
                string l = own.Length == 0 || own == "0" ? layer : own;
                if (e is MText mt) result.Texts.Add(ReadMText(mt, l, index));
                else if (e is TextEntity te) result.Texts.Add(ReadText(te, l, index, "TEXT EN BLOQUE"));
            }
        }
        catch { /* contenido ilegible: se ignora */ }
    }

    /// <summary>
    /// Une cada directriz (LEADER) con su rótulo: el asociado en el CAD o, si no lo tiene, el texto más cercano a su
    /// final. La punta de la flecha es el punto del tubo al que se refiere el rótulo.
    /// </summary>
    /// <summary>Une con sus directrices de líneas los textos añadidos después de leer (p. ej. los del DWF).</summary>
    public static void RelinkLineLeaders(CadDrawing result, IReadOnlyCollection<CadText> added) => LinkLineLeaders(result, added);

    private static void LinkLeaders(CadDrawing result)
    {
        LinkLeaderEntities(result);
        LinkLineLeaders(result);
    }

    /// <summary>
    /// Directrices dibujadas con LÍNEAS en la misma capa que el rótulo (así exporta Revit las etiquetas: «P-PIPE-IDEN»).
    /// Empiezan junto al texto (en su lado izquierdo o derecho) y terminan sobre el tubo: se sigue la cadena de
    /// líneas unidas desde el texto hasta su otro extremo, que es el punto del tubo al que se refiere el rótulo.
    /// </summary>
    private static void LinkLineLeaders(CadDrawing result, IReadOnlyCollection<CadText>? only = null)
    {
        // «only»: solo esos textos (los añadidos del DWF), para no reasignar a los del DWG directrices ya repartidas.
        var pending = (only ?? (IReadOnlyCollection<CadText>)result.Texts).Where(t => t.Anchor == null && t.HeightApprox > 0).ToList();
        if (pending.Count == 0) return;
        var byLayer = result.Segments.Where(s => s.Length > 0).GroupBy(s => s.Layer, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.ToList(), StringComparer.OrdinalIgnoreCase);
        var used = new HashSet<CadSegment>();
        foreach (var t in pending)
        {
            if (!byLayer.TryGetValue(t.Layer, out var lines)) continue;
            double h = Math.Abs(t.Height) > 0 ? Math.Abs(t.Height) : t.HeightApprox;
            double tol = Math.Max(0.05 * h, 1e-9);
            double cos = Math.Cos(t.RotationRad), sin = Math.Sin(t.RotationRad);
            var c = Flat(t.Center);
            // Coordenadas locales del texto: u a lo largo, v en perpendicular.
            (double U, double V) Local(P3 p) { double dx = p.X - c.X, dy = p.Y - c.Y; return (dx * cos + dy * sin, -dx * sin + dy * cos); }
            double hu = t.WidthApprox / 2, hv = t.HeightApprox / 2;
            bool Inside(P3 p, double margin) { var (u, v) = Local(p); return Math.Abs(u) <= hu + margin && Math.Abs(v) <= hv + margin; }
            // Inicio: un extremo junto al texto (como mucho a una altura de texto de su caja) y el otro fuera de ella.
            CadSegment? first = null; P3 from = default; double bestD = double.MaxValue;
            foreach (var s in lines)
            {
                if (used.Contains(s) || s.Length > 50 * h) continue;
                foreach (var (near, far) in new[] { (s.Start, s.End), (s.End, s.Start) })
                {
                    if (!Inside(Flat(near), h) || Inside(Flat(far), 0.25 * h)) continue;
                    var (u, v) = Local(Flat(near));
                    double d = Math.Abs(Math.Abs(u) - hu) + Math.Abs(v); // cerca del centro de un lado
                    if (d < bestD) { bestD = d; first = s; from = far; }
                }
            }
            if (first is null) continue;
            // Se sigue la cadena (hombro y pata de la directriz) hasta su final.
            var chain = new List<CadSegment> { first };
            P3 end = from;
            for (int step = 0; step < 6; step++)
            {
                CadSegment? next = null; P3 nextEnd = default;
                foreach (var s in lines)
                {
                    if (chain.Contains(s) || used.Contains(s) || s.Length > 50 * h) continue;
                    if (Flat(s.Start).DistanceTo(Flat(end)) <= tol) { next = s; nextEnd = s.End; break; }
                    if (Flat(s.End).DistanceTo(Flat(end)) <= tol) { next = s; nextEnd = s.Start; break; }
                }
                if (next is null || Inside(Flat(nextEnd), 0)) break;
                chain.Add(next);
                end = nextEnd;
            }
            t.Anchor = end;
            foreach (var s in chain) used.Add(s);
        }
    }

    private static void LinkLeaderEntities(CadDrawing result)
    {
        if (result.Leaders.Count == 0 || result.Texts.Count == 0) return;
        var byHandle = new Dictionary<ulong, CadText>();
        foreach (var t in result.Texts) if (t.Handle != 0) byHandle.TryAdd(t.Handle, t);
        foreach (var (ann, arrow, tail) in result.Leaders)
        {
            if (ann != 0 && byHandle.TryGetValue(ann, out var linked)) { linked.Anchor ??= arrow; continue; }
            CadText? best = null;
            double bestD = double.MaxValue;
            foreach (var t in result.Texts)
            {
                if (t.Anchor != null) continue;
                double d = Math.Min(Flat(t.Center).DistanceTo(Flat(tail)), Flat(t.Position).DistanceTo(Flat(tail)));
                double limit = Math.Max(3 * (t.Height > 0 ? t.Height : 0), 1e-9) + 0.5 * Math.Max(1, t.Value.Length) * t.Height * CharWidthFactor;
                if (d <= limit && d < bestD) { best = t; bestD = d; }
            }
            if (best != null) best.Anchor = arrow;
        }
    }

    private static P3 Flat(P3 p) => new(p.X, p.Y, 0);

    private static string DimensionKind(Dimension d) => d switch
    {
        DimensionLinear => "lineal",
        DimensionAligned => "alineada",
        DimensionRadius => "radio",
        DimensionDiameter => "diámetro",
        DimensionAngular2Line or DimensionAngular3Pt => "angular",
        DimensionOrdinate => "ordenada",
        DimensionArc => "de arco",
        _ => "otra"
    };

    /// <summary>Quita los códigos de formato de un MTEXT que el lector no haya quitado ya.</summary>
    internal static string StripMText(string s)
    {
        if (string.IsNullOrEmpty(s)) return "";
        s = System.Text.RegularExpressions.Regex.Replace(s, @"\\[Uu]\+([0-9A-Fa-f]{4})",
            m => ((char)Convert.ToInt32(m.Groups[1].Value, 16)).ToString());
        s = System.Text.RegularExpressions.Regex.Replace(s, @"\\[PpXx]", " ");
        s = System.Text.RegularExpressions.Regex.Replace(s, @"\\[A-Za-z][^;\\{}]*;", "");
        s = s.Replace("{", "").Replace("}", "").Replace("\\~", " ");
        return s;
    }

    /// <summary>Convierte los códigos de control de AutoCAD (%%C = Ø, %%D = °, %%P = ±) y limpia espacios.</summary>
    internal static string CleanText(string s)
    {
        if (string.IsNullOrEmpty(s)) return "";
        s = System.Text.RegularExpressions.Regex.Replace(s, "%%[cC]", "Ø");
        s = System.Text.RegularExpressions.Regex.Replace(s, "%%[dD]", "°");
        s = System.Text.RegularExpressions.Regex.Replace(s, "%%[pP]", "±");
        s = System.Text.RegularExpressions.Regex.Replace(s, "%%[uUoOkK]", "");
        s = s.Replace("%%%", "%");
        return System.Text.RegularExpressions.Regex.Replace(s, @"\s+", " ").Trim();
    }

    // ---- Luminarias dibujadas con líneas (ver LooseFixtures). Canal aparte: no cambia tramos, bloques ni inventario.

    /// <summary>
    /// Guarda el dibujo suelto de las capas de luminarias. Si la entidad es un bloque que NO es una luminaria pero tiene
    /// dentro dibujo de luminarias (una planta entera insertada como bloque), se mira por dentro, en su sitio.
    /// </summary>
    private static void CaptureFixtures(Entity entity, CadDrawing d, string layer, int index)
    {
        try
        {
            if (entity is Insert ins)
            {
                if (IsUnloadedXref(ins)) return;
                if (BlockNames.Role(ins.Block?.Name ?? "", layer) is BlockRole.Luminaria or BlockRole.MandoIluminacion) return;
                if (!BlockHasFixtures(ins.Block, d, 0)) return;
                CaptureInside(ins, d, layer, index, ins.Block?.Name ?? "", 1);
                return;
            }
            if (LooseFixtures.IsFixtureLayer(layer)) AddFixtureShape(entity, d, layer, "", index);
        }
        catch { /* dibujo ilegible: no se usa para luminarias */ }
    }

    private static bool BlockHasFixtures(ACadSharp.Tables.BlockRecord? block, CadDrawing d, int depth)
    {
        if (block is null || depth > MaxDepth) return false;
        if (d.FixtureBlockCache.TryGetValue(block, out bool known)) return known;
        d.FixtureBlockCache[block] = false; // contra bloques que se contienen a sí mismos
        bool has = false;
        foreach (var e in block.Entities)
        {
            string l = e.Layer?.Name ?? "";
            if (e is Insert n)
            {
                if (BlockNames.Role(n.Block?.Name ?? "", l) is BlockRole.Luminaria or BlockRole.MandoIluminacion ||
                    BlockHasFixtures(n.Block, d, depth + 1)) { has = true; break; }
            }
            else if (e is not (TextEntity or MText or Dimension or Hatch) && LooseFixtures.IsFixtureLayer(l)) { has = true; break; }
        }
        d.FixtureBlockCache[block] = has;
        return has;
    }

    private static void CaptureInside(Insert container, CadDrawing d, string inherited, int index, string group, int depth)
    {
        foreach (var c in container.Explode())
        {
            if (c is null) continue;
            string own = c.Layer?.Name ?? "";
            string l = own.Length == 0 || own == "0" ? inherited : own;
            if (c is Insert n)
            {
                if (IsUnloadedXref(n)) continue;
                string name = n.Block?.Name ?? "";
                // Tabla o leyenda de luminarias metida en un bloque («LUM_TABLE_BLOCK»): no es una luminaria; se leen sus textos.
                bool table = LooseFixtures.IsTableLayer(name) || LooseFixtures.IsTableLayer(l);
                if (table)
                {
                    if (depth < MaxDepth) CaptureInside(n, d, l, index, group, depth + 1);
                    continue;
                }
                if (BlockNames.Role(name, l) is BlockRole.Luminaria or BlockRole.MandoIluminacion)
                {
                    // Bloque de luminaria dentro de la planta: una luminaria entera, con su huella.
                    if (MeasureFootprint(n) is { } fp)
                    {
                        var corners = fp.Corners().ToList();
                        d.FixtureShapes.Add(new FixtureShape(l, group, corners.Min(p => p.X), corners.Min(p => p.Y),
                            corners.Max(p => p.X), corners.Max(p => p.Y), corners, index, name));
                    }
                    continue;
                }
                if (depth < MaxDepth && BlockHasFixtures(n.Block, d, depth)) CaptureInside(n, d, l, index, group, depth + 1);
                continue;
            }
            if (c is MText mt) { if (LooseFixtures.IsFixtureTextLayer(l)) d.FixtureTexts.Add(ReadMText(mt, l, index)); continue; }
            if (c is TextEntity te) { if (te is not AttributeDefinition && LooseFixtures.IsFixtureTextLayer(l)) d.FixtureTexts.Add(ReadText(te, l, index, "TEXT EN BLOQUE")); continue; }
            if (LooseFixtures.IsFixtureLayer(l)) AddFixtureShape(c, d, l, group, index);
        }
    }

    private static void AddFixtureShape(Entity e, CadDrawing d, string layer, string group, int index)
    {
        if (e is TextEntity or MText or Dimension or Hatch or Leader or MultiLeader or Viewport) return;
        var pts = new List<(double X, double Y)>();
        switch (e)
        {
            case Line l: pts.Add((l.StartPoint.X, l.StartPoint.Y)); pts.Add((l.EndPoint.X, l.EndPoint.Y)); break;
            case LwPolyline lw:
                foreach (var v in lw.Vertices) { var w = Ocs.ToWcs(lw.Normal, v.Location.X, v.Location.Y, lw.Elevation); pts.Add((w.X, w.Y)); }
                break;
            case Polyline3D p3: foreach (var v in p3.Vertices) pts.Add((v.Location.X, v.Location.Y)); break;
            case Polyline2D p2:
                foreach (var v in p2.Vertices) { var w = Ocs.ToWcs(p2.Normal, v.Location.X, v.Location.Y, p2.Elevation); pts.Add((w.X, w.Y)); }
                break;
            case Circle ci:
            {
                var w = Ocs.ToWcs(ci.Normal, ci.Center.X, ci.Center.Y, ci.Center.Z);
                for (int i = 0; i < 8; i++) pts.Add((w.X + ci.Radius * Math.Cos(i * Math.PI / 4), w.Y + ci.Radius * Math.Sin(i * Math.PI / 4)));
                break;
            }
        }
        if (pts.Count == 0)
        {
            var b = e.GetBoundingBox();
            pts.Add((b.Min.X, b.Min.Y)); pts.Add((b.Max.X, b.Min.Y)); pts.Add((b.Max.X, b.Max.Y)); pts.Add((b.Min.X, b.Max.Y));
        }
        pts.RemoveAll(p => !double.IsFinite(p.X) || !double.IsFinite(p.Y));
        if (pts.Count == 0) return;
        d.FixtureShapes.Add(new FixtureShape(layer, group, pts.Min(p => p.X), pts.Min(p => p.Y), pts.Max(p => p.X), pts.Max(p => p.Y), pts, index));
    }

    /// <summary>Rectángulo mínimo que envuelve la geometría del bloque (con bloques anidados).</summary>
    private static Footprint? MeasureFootprint(Insert insert)
    {
        try
        {
            var pts = new List<(double X, double Y)>();
            var angles = new List<double> { insert.Rotation };
            void Collect(IEnumerable<Entity> es, int depth)
            {
                foreach (var e in es)
                {
                    switch (e)
                    {
                        case Line l:
                            pts.Add((l.StartPoint.X, l.StartPoint.Y)); pts.Add((l.EndPoint.X, l.EndPoint.Y));
                            if (angles.Count < 200) angles.Add(Math.Atan2(l.EndPoint.Y - l.StartPoint.Y, l.EndPoint.X - l.StartPoint.X));
                            break;
                        case LwPolyline lw:
                            foreach (var v in lw.Vertices) { var w = Ocs.ToWcs(lw.Normal, v.Location.X, v.Location.Y, lw.Elevation); pts.Add((w.X, w.Y)); }
                            break;
                        case Polyline2D p2:
                            foreach (var v in p2.Vertices) { var w = Ocs.ToWcs(p2.Normal, v.Location.X, v.Location.Y, p2.Elevation); pts.Add((w.X, w.Y)); }
                            break;
                        case Arc a:
                            for (int i = 0; i <= 8; i++)
                            {
                                double sw = a.EndAngle - a.StartAngle; while (sw <= 0) sw += 2 * Math.PI;
                                double ang = a.StartAngle + sw * i / 8;
                                var w = Ocs.ToWcs(a.Normal, a.Center.X + a.Radius * Math.Cos(ang), a.Center.Y + a.Radius * Math.Sin(ang), a.Center.Z);
                                pts.Add((w.X, w.Y));
                            }
                            break;
                        case Circle ci:
                        {
                            var w = Ocs.ToWcs(ci.Normal, ci.Center.X, ci.Center.Y, ci.Center.Z);
                            pts.Add((w.X - ci.Radius, w.Y - ci.Radius)); pts.Add((w.X + ci.Radius, w.Y + ci.Radius));
                            break;
                        }
                        case Insert nested:
                            if (depth < MaxDepth) Collect(nested.Explode(), depth + 1);
                            break;
                        // Anotaciones: no forman parte del objeto y el lector las transforma mal al explotar.
                        case Hatch: case TextEntity: case MText: case Leader: case MultiLeader: case Dimension: case Viewport:
                            break;
                        default:
                            try
                            {
                                var b = e.GetBoundingBox();
                                pts.Add((b.Min.X, b.Min.Y)); pts.Add((b.Max.X, b.Max.Y));
                            }
                            catch { /* entidad sin caja medible */ }
                            break;
                    }
                }
            }
            Collect(insert.Explode(), 0);
            pts.RemoveAll(p => double.IsNaN(p.X) || double.IsNaN(p.Y) || double.IsInfinity(p.X) || double.IsInfinity(p.Y));
            RemoveOutliers(pts);
            return Footprint.Fit(pts, angles);
        }
        catch { return null; }
    }

    /// <summary>
    /// Quita puntos absurdamente lejos del resto (p. ej. entidades sin caja medible que el lector
    /// devuelve en el origen). Se compara con la distancia típica de los puntos a su mediana.
    /// </summary>
    internal static void RemoveOutliers(List<(double X, double Y)> pts)
    {
        if (pts.Count < 4) return;
        double mx = pts.Select(p => p.X).OrderBy(v => v).ElementAt(pts.Count / 2);
        double my = pts.Select(p => p.Y).OrderBy(v => v).ElementAt(pts.Count / 2);
        var d = pts.Select(p => Math.Sqrt((p.X - mx) * (p.X - mx) + (p.Y - my) * (p.Y - my))).OrderBy(v => v).ToList();
        double typical = d[(int)(d.Count * 0.75)];
        double limit = Math.Max(typical * 20, 1e-6);
        pts.RemoveAll(p => Math.Sqrt((p.X - mx) * (p.X - mx) + (p.Y - my) * (p.Y - my)) > limit);
    }

    private static bool IsUnloadedXref(Insert insert)
    {
        var b = insert.Block;
        if (b is null) return false;
        bool xref = b.Flags.HasFlag(ACadSharp.Blocks.BlockTypeFlags.XRef) || b.Flags.HasFlag(ACadSharp.Blocks.BlockTypeFlags.XRefOverlay);
        return xref && !b.Entities.Any();
    }

    /// <summary>
    /// Polilínea 2D con sus vértices en coordenadas del objeto (OCS); <paramref name="toWcs"/> los pasa a
    /// coordenadas generales. Los tramos curvos (bulge) se sustituyen por su cuerda.
    /// </summary>
    private static void AddPolyline(CadDrawing result, string layer, List<(double X, double Y, double Bulge)> v,
        Func<double, double, P3> toWcs, bool closed, string source, int entity)
    {
        int n = v.Count;
        int segments = closed ? n : n - 1;
        for (int i = 0; i < segments; i++)
        {
            var a = v[i];
            var b = v[(i + 1) % n];
            if (Math.Abs(a.Bulge) <= 1e-9)
            {
                Add(result, layer, toWcs(a.X, a.Y), toWcs(b.X, b.Y), source, entity);
                continue;
            }
            // Tramo curvo. bulge = tan(giro / 4); positivo = antihorario.
            result.ArcSegmentsApproximated++;
            double sweep = 4 * Math.Atan(a.Bulge);
            double dx = b.X - a.X, dy = b.Y - a.Y, chord = Math.Sqrt(dx * dx + dy * dy);
            P3? corner = null;
            if (chord > 1e-9 && Math.Abs(sweep) <= MaxCornerSweep)
            {
                // Las tangentes de los extremos se cortan sobre la mediatriz de la cuerda, del lado hacia el
                // que se curva el tramo (a la derecha del sentido a→b si es antihorario), a una distancia
                // (cuerda / 2) · tan(giro / 2) de la cuerda.
                double h = chord / 2 * Math.Tan(sweep / 2);
                corner = toWcs((a.X + b.X) / 2 + dy / chord * h, (a.Y + b.Y) / 2 - dx / chord * h);
            }
            Add(result, layer, toWcs(a.X, a.Y), toWcs(b.X, b.Y), source, entity, ++result.CurveCount, corner);
        }
    }

    private static void Add(CadDrawing result, string layer, P3 a, P3 b, string source, int entity, int arcId = 0, P3? arcCorner = null)
    {
        if (a.DistanceTo(b) <= 1e-9) return;
        result.Segments.Add(new CadSegment { Layer = layer, Start = a, End = b, Source = source, ArcId = arcId, ArcCorner = arcCorner, EntityIndex = entity });
    }

    private static P3 ToP(CSMath.XYZ p) => new(p.X, p.Y, p.Z);
}

/// <summary>Error de lectura con un mensaje para el usuario y una indicación de cómo corregirlo.</summary>
public sealed class CadReadException : Exception
{
    public string HowToFix { get; }
    public CadReadException(string message, string howToFix) : base(message) => HowToFix = howToFix;
}
