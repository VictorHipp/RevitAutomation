using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using Autodesk.Revit.Attributes;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using Color = Autodesk.Revit.DB.Color;
using RevitTaskDialog = Autodesk.Revit.UI.TaskDialog;
using View = Autodesk.Revit.DB.View;

namespace RevitAutomation.Addin.Commands.Regiones;

// REGION: traces the coloured zones of a linked CAD drawing as Revit filled regions in the active plan view.
// One filled-region type per CAD layer («REG - <capa>», solid fill in the layer colour); each closed outline of the layer
// becomes one region, with the layer name in its Comments. Automatic: the CAD is the preselected one, the only one in the
// view, or the one the user clicks; every layer with closed outlines is created in one transaction (Ctrl+Z undoes it) and a
// report follows. Regions created earlier by REGION in the view are replaced; other regions are never touched.

[Transaction(TransactionMode.Manual)]
[Regeneration(RegenerationOption.Manual)]
public sealed class RegionCommand : IExternalCommand
{
    internal const string Title = "REGION · Regiones desde el CAD vinculado";

    public Result Execute(ExternalCommandData data, ref string message, ElementSet elements)
    {
        UIDocument? ui = data.Application.ActiveUIDocument;
        if (ui is null || ui.Document.IsFamilyDocument)
        {
            Show("Abra un proyecto de Revit.", "Este botón trabaja en proyectos (.rvt), no en familias. Abra el proyecto y vuelva a pulsar REGION.");
            return Result.Cancelled;
        }
        Document doc = ui.Document;
        if (doc.IsReadOnly)
        {
            Show("El proyecto está abierto en solo lectura.", "Guarde una copia editable o ábralo con permisos de escritura y vuelva a pulsar REGION.");
            return Result.Cancelled;
        }
        View view = ui.ActiveView;
        if (!RegionReader.IsSupportedView(view))
        {
            Show("La vista activa no admite regiones rellenadas del CAD.",
                "Abra la vista en planta (o de techo, de áreas o de diseño) donde se ve el CAD vinculado y vuelva a pulsar REGION. " +
                "Vista activa: «" + view.Name + "» (" + view.ViewType + ").");
            return Result.Cancelled;
        }
        if (!new FilteredElementCollector(doc).OfClass(typeof(FilledRegionType)).Any())
        {
            Show("El proyecto no tiene ningún tipo de región rellenada.",
                "Cree uno con Anotar > Región > Región rellenada (o transfiéralo con Gestionar > Transferir normas de proyecto) y repita.");
            return Result.Cancelled;
        }

        // 1 · Linked CAD drawings visible in this view (a preselected one goes first).
        var links = new FilteredElementCollector(doc, view.Id).OfClass(typeof(ImportInstance)).Cast<ImportInstance>()
            .OrderByDescending(i => i.IsLinked).ThenBy(i => RegionReader.LinkName(doc, i), StringComparer.CurrentCultureIgnoreCase).ToList();
        if (links.Count == 0)
        {
            Show("No se ve ningún CAD vinculado en la vista «" + view.Name + "».",
                "Vincule el plano con Insertar > Vincular CAD (en esta vista o en todas) o haga visible el vínculo en Visibilidad/Gráficos (VV), " +
                "y vuelva a pulsar REGION. No se ha modificado nada.");
            return Result.Cancelled;
        }
        // Which CAD: the preselected one; else the only one in the view; else the user clicks it in the view.
        ImportInstance? link = ui.Selection.GetElementIds().Select(id => doc.GetElement(id)).OfType<ImportInstance>()
            .FirstOrDefault(i => links.Any(l => l.Id == i.Id));
        if (link is null && links.Count == 1) link = links[0];
        if (link is null)
        {
            try
            {
                var picked = ui.Selection.PickObject(Autodesk.Revit.UI.Selection.ObjectType.Element, new CadLinkFilter(),
                    "REGION: hay " + links.Count + " CAD en la vista. Pinche el plano CAD vinculado que quiere convertir en regiones (Esc para cancelar).");
                link = doc.GetElement(picked) as ImportInstance;
            }
            catch (Autodesk.Revit.Exceptions.OperationCanceledException) { return Result.Cancelled; }
            if (link is null) return Result.Cancelled;
        }

        // 2 · Read the CAD (nothing is changed).
        RegionSource src;
        try { src = RegionReader.Read(doc, view, link); }
        catch (Exception ex)
        {
            message = ex.ToString();
            Show("No se pudo leer la geometría del CAD «" + RegionReader.LinkName(doc, link) + "». No se ha modificado nada.",
                "Cómo corregirlo: recargue el vínculo (Insertar > Gestionar vínculos > Volver a cargar) y vuelva a pulsar REGION.", ex.ToString());
            return Result.Failed;
        }
        if (src.Scan.Loops.Count == 0)
        {
            Show("El CAD «" + src.Name + "» no tiene contornos cerrados en esta vista. No se ha modificado nada.",
                "REGION convierte cada POLILÍNEA cerrada en una región. Compruebe en el CAD que las zonas están dibujadas con polilínea cerrada " +
                "(un sombreado sin contorno no se ve) y que sus capas están visibles en Visibilidad/Gráficos (VV) > Categorías importadas.",
                Unused(src));
            return Result.Cancelled;
        }
        var choices = new RegionChoices(src, src.Scan.Loops.Select(l => l.Layer).Distinct(StringComparer.OrdinalIgnoreCase).ToList(), replacePrevious: true);

        // 3 · Create (one transaction; on error nothing stays).
        RegionResult result;
        try { result = RegionBuilder.Build(doc, view, choices); }
        catch (Exception ex)
        {
            message = ex.ToString();
            Show("No se pudieron crear las regiones. El proyecto queda como estaba.", ex.Message, ex.ToString());
            return Result.Failed;
        }
        ShowReport(view, choices, result);
        return Result.Succeeded;
    }

    private static void ShowReport(View view, RegionChoices choices, RegionResult r)
    {
        if (r.RolledBack)
        {
            Show("No se pudo crear ninguna región. No se ha modificado nada.",
                "Revit rechazó los " + r.Failures.Count + " contornos del CAD" + (choices.ReplacePrevious ? " y las regiones anteriores se conservan" : "") + ". " +
                "Suele deberse a polilíneas que se cortan a sí mismas o con tramos casi nulos. Cómo corregirlo: revise esas polilíneas en el CAD " +
                "(detalle abajo, con su posición), guárdelo, recargue el vínculo y repita REGION.",
                string.Join("\n", r.Failures.Take(60).Select(f => f.Layer + " · " + f.AreaM2.ToString("0.##") + " m² · cerca de " + f.Near + ": " + f.Reason)));
            return;
        }
        var sb = new StringBuilder();
        sb.AppendLine("Vista: «" + view.Name + "»  ·  CAD: " + choices.SourceName);
        if (r.Replaced > 0) sb.AppendLine("Regiones anteriores de REGION sustituidas: " + r.Replaced);
        sb.AppendLine("Tipos de región: " + r.TypesCreated + " nuevos, " + r.TypesUpdated + " actualizados (color de la capa).");
        sb.AppendLine();
        foreach (var layer in r.CreatedPerLayer.Keys.Union(r.Failures.Select(f => f.Layer)).OrderBy(l => l, StringComparer.CurrentCultureIgnoreCase))
        {
            r.CreatedPerLayer.TryGetValue(layer, out int ok);
            int bad = r.Failures.Count(f => f.Layer == layer);
            sb.AppendLine("• " + layer + ": " + ok + " creadas" + (bad > 0 ? ", " + bad + " no creadas" : ""));
        }
        var details = new StringBuilder();
        string unused = Unused(choices.Source);
        if (unused.Length > 0) details.AppendLine("Lo que no se ha convertido en región, por capa:").AppendLine(unused);
        if (r.Failures.Count > 0) details.AppendLine("Contornos que Revit no aceptó:");
        foreach (var f in r.Failures.Take(60))
            details.AppendLine(f.Layer + " · " + f.AreaM2.ToString("0.##") + " m² · cerca de " + f.Near + ": " + f.Reason);
        if (r.Failures.Count > 60) details.AppendLine("… y " + (r.Failures.Count - 60) + " más.");
        if (r.Failures.Count > 0)
            sb.AppendLine().AppendLine("Las no creadas suelen ser contornos que se cortan a sí mismos o con tramos casi nulos en el CAD. " +
                                       "Cómo corregirlo: revise esas polilíneas en el plano (detalle abajo), guárdelo, recargue el vínculo y repita REGION.");
        sb.AppendLine().Append("Ctrl+Z deshace todo lo creado.");
        Show(r.Created + " regiones rellenadas creadas" + (r.Failures.Count > 0 ? " · " + r.Failures.Count + " no se pudieron crear" : "") + ".",
            sb.ToString(), details.ToString());
    }

    /// <summary>Per layer: what was left out and why (open, ambiguous, repeated, too small), plus non-curve objects.</summary>
    internal static string Unused(RegionSource src)
    {
        var s = src.Scan;
        var sb = new StringBuilder();
        foreach (string layer in s.Layers)
        {
            var parts = new List<string>();
            int open = RegionScan.Get(s.OpenPieces, layer), amb = RegionScan.Get(s.AmbiguousPieces, layer),
                dup = RegionScan.Get(s.Duplicates, layer), small = RegionScan.Get(s.TooSmall, layer);
            if (open > 0) parts.Add(open + " líneas o polilíneas abiertas (no cierran un contorno)");
            if (amb > 0) parts.Add(amb + " líneas que se juntan 3 o más en un punto (contorno ambiguo: dibújelo como polilínea cerrada)");
            if (dup > 0) parts.Add(dup + " contornos repetidos (se ha creado uno)");
            if (small > 0) parts.Add(small + " contornos de menos de 0,01 m²");
            if (parts.Count > 0) sb.AppendLine("• " + layer + ": " + string.Join("; ", parts) + ".");
        }
        if (src.OtherObjects > 0) sb.AppendLine("• " + src.OtherObjects + " objetos que no son líneas (sombreados, textos, sólidos): no se usan.");
        if (src.Unreadable > 0) sb.AppendLine("• " + src.Unreadable + " objetos que Revit no pudo leer: se han saltado.");
        return sb.ToString().TrimEnd();
    }

    internal static void Show(string instruction, string content, string? details = null)
    {
        var dlg = new RevitTaskDialog(Title) { MainInstruction = instruction, MainContent = content };
        if (!string.IsNullOrWhiteSpace(details)) dlg.ExpandedContent = details;
        dlg.Show();
    }
}

/// <summary>Only CAD drawings (linked or imported) can be picked.</summary>
internal sealed class CadLinkFilter : Autodesk.Revit.UI.Selection.ISelectionFilter
{
    public bool AllowElement(Element elem) => elem is ImportInstance;
    public bool AllowReference(Reference reference, XYZ position) => false;
}

/// <summary>What the CAD link contains, read through Revit's geometry of the link (already in project coordinates).</summary>
internal sealed class RegionSource
{
    internal RegionSource(ImportInstance link, string name) { Link = link; Name = name; }
    internal ImportInstance Link { get; }
    internal string Name { get; }
    internal RegionScan Scan { get; set; } = new();
    internal Dictionary<int, List<Curve>> Curves { get; } = new();
    internal Dictionary<string, Color?> LayerColors { get; } = new(StringComparer.OrdinalIgnoreCase);
    internal Dictionary<string, int> ObjectsPerLayer { get; } = new(StringComparer.OrdinalIgnoreCase);
    /// <summary>Solids, meshes, points or texts: not outlines (a hatch without a boundary polyline is not seen).</summary>
    internal int OtherObjects { get; set; }
    /// <summary>Objects Revit could not turn into curves (skipped; the rest of the drawing is still read).</summary>
    internal int Unreadable { get; set; }
}

internal static class RegionReader
{
    internal static bool IsSupportedView(View? v) =>
        v != null && !v.IsTemplate && (v is ViewPlan || v is ViewDrafting);

    internal static string LinkName(Document doc, ImportInstance i)
    {
        string name = (doc.GetElement(i.GetTypeId()) as ElementType)?.Name ?? i.Category?.Name ?? "CAD";
        return name + (i.IsLinked ? "" : " (importado, no vinculado)");
    }

    internal static RegionSource Read(Document doc, View view, ImportInstance link)
    {
        var src = new RegionSource(link, LinkName(doc, link));
        double shortTol = doc.Application.ShortCurveTolerance;
        var pieces = new List<RegionPiece>();
        var styles = new Dictionary<ElementId, (string Layer, Color? Color)>();
        (string Layer, Color? Color) LayerOf(GeometryObject g)
        {
            if (styles.TryGetValue(g.GraphicsStyleId, out var known)) return known;
            var cat = (doc.GetElement(g.GraphicsStyleId) as GraphicsStyle)?.GraphicsStyleCategory;
            Color? c = null;
            try { if (cat?.LineColor is { IsValid: true } lc) c = lc; } catch (Exception) { }
            var v = (cat?.Name ?? "0", c);
            styles[g.GraphicsStyleId] = v;
            return v;
        }
        void AddPiece(GeometryObject g, List<Curve> curves, List<XYZ> pts, bool isPolyline)
        {
            if (pts.Count < 2) return;
            var (layer, color) = LayerOf(g);
            if (!src.LayerColors.ContainsKey(layer)) src.LayerColors[layer] = color;
            src.ObjectsPerLayer.TryGetValue(layer, out int n);
            src.ObjectsPerLayer[layer] = n + 1;
            int id = pieces.Count;
            pieces.Add(new RegionPiece(id, layer, pts.Select(p => new RegionPoint(p.X, p.Y)).ToList(), isPolyline));
            src.Curves[id] = curves;
        }
        void Walk(GeometryElement? ge, int depth)
        {
            if (ge is null || depth > 12) return;
            foreach (GeometryObject g in ge)
            {
                // One unreadable object (e.g. a degenerate curve) must not stop the reading of the whole drawing.
                try { ReadObject(g, depth); }
                catch (Autodesk.Revit.Exceptions.ApplicationException) { src.Unreadable++; }
            }
        }
        void ReadObject(GeometryObject g, int depth)
        {
                switch (g)
                {
                    case GeometryInstance gi:
                        Walk(gi.GetInstanceGeometry(), depth + 1);
                        break;
                    case PolyLine pl:
                    {
                        var pts = Distinct(pl.GetCoordinates());
                        var lines = new List<Curve>();
                        // Parts shorter than Revit's tolerance cannot be lines; ToCurveLoop joins the neighbours over them.
                        for (int k = 0; k + 1 < pts.Count; k++)
                            if (pts[k].DistanceTo(pts[k + 1]) > shortTol) lines.Add(Line.CreateBound(pts[k], pts[k + 1]));
                        AddPiece(g, lines, pts, true);
                        break;
                    }
                    case Curve c when c.IsBound:
                        AddPiece(g, new List<Curve> { c }, Distinct(c.Tessellate()), false);
                        break;
                    case Curve c when c.IsCyclic:
                    {
                        // Full circle or ellipse: two halves that chain into one closed outline.
                        double p = c.Period;
                        var a = c.Clone(); a.MakeBound(0, p / 2);
                        var b = c.Clone(); b.MakeBound(p / 2, p);
                        AddPiece(g, new List<Curve> { a }, Distinct(a.Tessellate()), false);
                        AddPiece(g, new List<Curve> { b }, Distinct(b.Tessellate()), false);
                        break;
                    }
                    default:
                        src.OtherObjects++;
                        break;
                }
        }

        var options = new Options { View = view, IncludeNonVisibleObjects = false };
        Walk(link.get_Geometry(options), 0);
        src.Scan = RegionGeometry.Find(pieces);
        return src;
    }

    private static List<XYZ> Distinct(IList<XYZ> pts)
    {
        var r = new List<XYZ>();
        foreach (var p in pts) if (r.Count == 0 || !r[r.Count - 1].IsAlmostEqualTo(p)) r.Add(p);
        return r;
    }
}

internal sealed class RegionChoices
{
    internal RegionChoices(RegionSource source, IReadOnlyCollection<string> layers, bool replacePrevious)
    {
        Source = source; Layers = layers; ReplacePrevious = replacePrevious;
    }
    internal RegionSource Source { get; }
    internal string SourceName => Source.Name;
    internal IReadOnlyCollection<string> Layers { get; }
    internal bool ReplacePrevious { get; }
}

internal sealed class RegionResult
{
    internal int Created { get; set; }
    internal int Replaced { get; set; }
    internal int TypesCreated { get; set; }
    internal int TypesUpdated { get; set; }
    /// <summary>No region could be created, so the transaction was rolled back: the project is unchanged.</summary>
    internal bool RolledBack { get; set; }
    internal Dictionary<string, int> CreatedPerLayer { get; } = new(StringComparer.OrdinalIgnoreCase);
    internal List<(string Layer, double AreaM2, string Near, string Reason)> Failures { get; } = new();
}

internal static class RegionBuilder
{
    /// <summary>Filled regions created earlier by REGION in this view (marked in Comments; else by the «REG - » type).</summary>
    internal static List<ElementId> Previous(Document doc, View view) =>
        new FilteredElementCollector(doc, view.Id).OfClass(typeof(FilledRegion)).Cast<FilledRegion>()
            .Where(r =>
            {
                var comment = r.get_Parameter(BuiltInParameter.ALL_MODEL_INSTANCE_COMMENTS);
                if (comment != null) return RegionNaming.IsOurs(comment.AsString());
                return (doc.GetElement(r.GetTypeId())?.Name ?? "").StartsWith(RegionNaming.TypePrefix, StringComparison.Ordinal);
            })
            .Select(r => r.Id).ToList();

    internal static RegionResult Build(Document doc, View view, RegionChoices choices)
    {
        var result = new RegionResult();
        var src = choices.Source;
        double z = 0;
        try { z = view.Origin?.Z ?? 0; } catch (Autodesk.Revit.Exceptions.ApplicationException) { }
        double shortTol = doc.Application.ShortCurveTolerance;
        var solid = new FilteredElementCollector(doc).OfClass(typeof(FillPatternElement)).Cast<FillPatternElement>()
            .FirstOrDefault(f => { try { return f.GetFillPattern().IsSolidFill; } catch (Exception) { return false; } })?.Id;
        var types = new FilteredElementCollector(doc).OfClass(typeof(FilledRegionType)).Cast<FilledRegionType>().ToList();
        var baseType = types.First();
        var typesByName = new Dictionary<string, FilledRegionType>(StringComparer.OrdinalIgnoreCase);
        foreach (var existing in types) typesByName.TryAdd(existing.Name, existing);
        var loopsByLayer = src.Scan.Loops.ToLookup(l => l.Layer, StringComparer.OrdinalIgnoreCase);

        // Names over every layer of the drawing (not only the chosen ones), so a layer always gets the same type.
        var typeNames = RegionNaming.TypeNames(src.Scan.Layers);

        using var t = new Transaction(doc, "REGION: regiones desde el CAD");
        t.Start();
        if (choices.ReplacePrevious)
        {
            var old = Previous(doc, view);
            if (old.Count > 0) { doc.Delete(old); result.Replaced = old.Count; }
        }

        foreach (string layer in choices.Layers)
        {
            var loops = loopsByLayer[layer].ToList();
            if (loops.Count == 0) continue;

            // Type «REG - <capa>»: reused if it exists (its colour is set to the layer colour), else duplicated.
            string typeName = typeNames.TryGetValue(layer, out var unique) ? unique : RegionNaming.TypeName(layer);
            typesByName.TryGetValue(typeName, out var type);
            if (type is null)
            {
                type = (FilledRegionType)baseType.Duplicate(typeName);
                typesByName.Add(typeName, type);
                result.TypesCreated++;
            }
            else result.TypesUpdated++;
            src.LayerColors.TryGetValue(layer, out Color? colour);
            if (solid != null) type.ForegroundPatternId = solid;
            type.ForegroundPatternColor = colour ?? new Color(128, 128, 128);

            foreach (var loop in loops)
            {
                var near = loop.Outline[0];
                string nearText = $"X={near.X * 0.3048:0.00} m, Y={near.Y * 0.3048:0.00} m";
                try
                {
                    var curveLoop = ToCurveLoop(loop, src, z, shortTol);
                    var region = FilledRegion.Create(doc, type.Id, view.Id, new List<CurveLoop> { curveLoop });
                    region.get_Parameter(BuiltInParameter.ALL_MODEL_INSTANCE_COMMENTS)?.Set(RegionNaming.Comment(layer));
                    result.Created++;
                    result.CreatedPerLayer.TryGetValue(layer, out int n);
                    result.CreatedPerLayer[layer] = n + 1;
                }
                catch (Exception ex) when (ex is Autodesk.Revit.Exceptions.ApplicationException || ex is RegionLoopException)
                {
                    result.Failures.Add((layer, loop.AreaSquareMeters, nearText, ex.Message));
                }
            }
        }
        if (result.Created == 0)
        {
            // Nothing could be created: undo everything (also the deletion of the previous regions and the new types).
            t.RollBack();
            result.RolledBack = true;
            return result;
        }
        if (t.Commit() != TransactionStatus.Committed)
            throw new InvalidOperationException("Revit no confirmó la operación (estado distinto de «confirmada»). No se ha creado nada.");
        return result;
    }

    /// <summary>
    /// The loop's curves, in order, flattened to the view plane and joined end to start (CAD vertices that are within the
    /// 1 mm tolerance are snapped so Revit sees a closed loop). Lines and arcs keep their shape; other curves become chords.
    /// </summary>
    internal static CurveLoop ToCurveLoop(RegionLoop loop, RegionSource src, double z, double shortTol)
    {
        XYZ Flat(XYZ p) => new XYZ(p.X, p.Y, z);
        var parts = new List<(XYZ A, XYZ B, XYZ? Mid)>();
        foreach (var step in loop.Steps)
        {
            var curves = src.Curves[step.PieceId].ToList();
            if (step.Reversed) curves.Reverse();
            foreach (var c in curves)
            {
                XYZ a = c.GetEndPoint(step.Reversed ? 1 : 0), b = c.GetEndPoint(step.Reversed ? 0 : 1);
                if (c is Line) parts.Add((Flat(a), Flat(b), null));
                else if (c is Arc) parts.Add((Flat(a), Flat(b), Flat(c.Evaluate(0.5, true))));
                else
                {
                    var pts = c.Tessellate().Select(Flat).ToList();
                    if (step.Reversed) pts.Reverse();
                    for (int k = 0; k + 1 < pts.Count; k++) parts.Add((pts[k], pts[k + 1], null));
                }
            }
        }
        // Drop pieces shorter than Revit's tolerance; then join each part to the previous end.
        parts = parts.Where(p => p.A.DistanceTo(p.B) > shortTol || p.Mid != null).ToList();
        if (parts.Count < 2) throw new RegionLoopException("El contorno tiene menos de dos tramos útiles.");
        Curve Make(XYZ a, XYZ b, XYZ? mid) => mid is null ? Line.CreateBound(a, b) : Arc.Create(a, b, mid);
        var kept = new List<(XYZ A, XYZ B, XYZ? Mid)>();
        XYZ first = parts[0].A, prev = first;
        for (int i = 0; i < parts.Count; i++)
        {
            XYZ end = i == parts.Count - 1 ? first : parts[i].B;
            if (prev.DistanceTo(end) <= shortTol) continue;
            kept.Add((prev, end, parts[i].Mid));
            prev = end;
        }
        // If the closing part was too short to keep, the last kept part is stretched to the first vertex.
        if (kept.Count > 0 && !kept[kept.Count - 1].B.IsAlmostEqualTo(first))
        {
            var last = kept[kept.Count - 1];
            kept[kept.Count - 1] = (last.A, first, last.Mid);
        }
        if (kept.Count < 2) throw new RegionLoopException("El contorno tiene menos de dos tramos útiles.");
        return CurveLoop.Create(kept.Select(k => Make(k.A, k.B, k.Mid)).ToList());
    }
}

/// <summary>An outline that cannot become a Revit curve loop (reported per region; the others are still created).</summary>
internal sealed class RegionLoopException : Exception
{
    internal RegionLoopException(string message) : base(message) { }
}
