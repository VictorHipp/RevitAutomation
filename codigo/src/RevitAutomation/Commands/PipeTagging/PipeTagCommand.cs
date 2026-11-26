using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.Attributes;
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Mechanical;
using Autodesk.Revit.DB.Plumbing;
using Autodesk.Revit.UI;
using Autodesk.Revit.UI.Selection;
using RevitTaskDialog = Autodesk.Revit.UI.TaskDialog;
using View = Autodesk.Revit.DB.View;
using WF = System.Windows.Forms;

namespace RevitAutomation.Addin.Commands.PipeTagging;

/// <summary>
/// Etiquetado › 1 · Tags the straight pipes (> 15 cm) selected in the active view with a pipe tag type loaded in
/// the model, without overlapping any tag, and reports what was tagged and what was not.
/// </summary>
[Transaction(TransactionMode.Manual)]
public sealed class PipeTagSelectedCommand : IExternalCommand
{
    public Result Execute(ExternalCommandData data, ref string message, ElementSet elements)
        => MepCurveTagEngine.Run(data, ref message, MepTagProfile.Pipes);
}

/// <summary>Etiquetado › 2 · Same as the pipe tool, for straight ducts (> 15 cm) with a duct tag type of the model.</summary>
[Transaction(TransactionMode.Manual)]
public sealed class DuctTagSelectedCommand : IExternalCommand
{
    public Result Execute(ExternalCommandData data, ref string message, ElementSet elements)
        => MepCurveTagEngine.Run(data, ref message, MepTagProfile.Ducts);
}

/// <summary>Etiquetado › 3 · Same as the pipe tool, for straight cable trays (> 15 cm) with a cable tray tag type of the model.</summary>
[Transaction(TransactionMode.Manual)]
public sealed class CableTrayTagSelectedCommand : IExternalCommand
{
    public Result Execute(ExternalCommandData data, ref string message, ElementSet elements)
        => MepCurveTagEngine.Run(data, ref message, MepTagProfile.CableTrays);
}

/// <summary>What changes between pipes, ducts and cable trays: categories, words, system and size shown in the report.</summary>
internal sealed class MepTagProfile
{
    internal string Title { get; init; } = "";
    internal MepTagNouns Nouns { get; init; } = MepTagNouns.Pipe;
    internal BuiltInCategory TagCategory { get; init; }
    internal BuiltInCategory CurveCategory { get; init; }
    internal BuiltInCategory FittingCategory { get; init; }
    /// <summary>Null when the discipline has no such category (cable trays have no accessories nor flexible runs).</summary>
    internal BuiltInCategory? AccessoryCategory { get; init; }
    internal BuiltInCategory? FlexCategory { get; init; }
    internal string SystemHeader { get; init; } = "Sistema";
    internal Func<MEPCurve, string> System { get; init; } = _ => "";
    internal string SizeHeader { get; init; } = "";
    /// <summary>Optional full set of 8 headers for the "Etiquetados" tab (used by the family tool).</summary>
    internal string[]? ReportHeaders { get; init; }
    internal Func<Element, bool?> IsPlaceholder { get; init; } = _ => null;
    internal Func<MEPCurve, string> Size { get; init; } = _ => "";

    private static string ValueText(Element e, BuiltInParameter p) => e.get_Parameter(p)?.AsValueString() ?? "";
    private static string OrMissing(string value, string missing) => string.IsNullOrWhiteSpace(value) ? missing : value;

    internal static readonly MepTagProfile Pipes = new()
    {
        Title = "Etiquetar tuberías seleccionadas",
        Nouns = MepTagNouns.Pipe,
        TagCategory = BuiltInCategory.OST_PipeTags,
        CurveCategory = BuiltInCategory.OST_PipeCurves,
        FittingCategory = BuiltInCategory.OST_PipeFitting,
        AccessoryCategory = BuiltInCategory.OST_PipeAccessory,
        FlexCategory = BuiltInCategory.OST_FlexPipeCurves,
        System = c => OrMissing(ValueText(c, BuiltInParameter.RBS_PIPING_SYSTEM_TYPE_PARAM), "(sin sistema)"),
        SizeHeader = "Ø (mm)",
        IsPlaceholder = e => e is Pipe p ? p.IsPlaceholder : null,
        Size = c => c is Pipe p ? UnitUtils.ConvertFromInternalUnits(p.Diameter, UnitTypeId.Millimeters).ToString("0.##") : ""
    };

    internal static readonly MepTagProfile Ducts = new()
    {
        Title = "Etiquetar conductos seleccionados",
        Nouns = MepTagNouns.Duct,
        TagCategory = BuiltInCategory.OST_DuctTags,
        CurveCategory = BuiltInCategory.OST_DuctCurves,
        FittingCategory = BuiltInCategory.OST_DuctFitting,
        AccessoryCategory = BuiltInCategory.OST_DuctAccessory,
        FlexCategory = BuiltInCategory.OST_FlexDuctCurves,
        System = c => OrMissing(ValueText(c, BuiltInParameter.RBS_DUCT_SYSTEM_TYPE_PARAM), "(sin sistema)"),
        SizeHeader = "Tamaño",
        IsPlaceholder = e => e is Duct d ? d.IsPlaceholder : null,
        Size = c => c.get_Parameter(BuiltInParameter.RBS_CALCULATED_SIZE)?.AsString() ?? ""
    };

    internal static readonly MepTagProfile CableTrays = new()
    {
        Title = "Etiquetar bandejas de cables seleccionadas",
        Nouns = MepTagNouns.CableTray,
        TagCategory = BuiltInCategory.OST_CableTrayTags,
        CurveCategory = BuiltInCategory.OST_CableTray,
        FittingCategory = BuiltInCategory.OST_CableTrayFitting,
        AccessoryCategory = null,
        FlexCategory = null,
        // Cable trays have no MEP system: the report shows their service type instead.
        SystemHeader = "Tipo de servicio",
        System = c => OrMissing(c.get_Parameter(BuiltInParameter.RBS_CTC_SERVICE_TYPE)?.AsString() ?? "", "(sin tipo de servicio)"),
        SizeHeader = "Ancho x alto",
        IsPlaceholder = e => e is Autodesk.Revit.DB.Electrical.CableTray ? false : null,
        Size = c =>
        {
            string w = ValueText(c, BuiltInParameter.RBS_CABLETRAY_WIDTH_PARAM), h = ValueText(c, BuiltInParameter.RBS_CABLETRAY_HEIGHT_PARAM);
            return w.Length == 0 && h.Length == 0 ? "" : $"{w} x {h}";
        }
    };
}

/// <summary>Shared tagging engine: selection, review, creation, overlap-free layout, verification and reporter.</summary>
internal static class MepCurveTagEngine
{
    internal static Result Run(ExternalCommandData data, ref string message, MepTagProfile profile)
    {
        string title = profile.Title;
        var nouns = profile.Nouns;
        UIDocument? ui = data.Application.ActiveUIDocument;
        if (ui is null || ui.Document.IsFamilyDocument || ui.Document.IsReadOnly)
        {
            RevitTaskDialog.Show(title, "Abre un proyecto de Revit editable y vuelve a pulsar el botón. No se ha modificado el modelo.");
            return Result.Cancelled;
        }
        Document doc = ui.Document;
        View view = doc.ActiveView;

        // 1 · Requirements: a view that accepts tags, tag types of the model loaded, those tags visible.
        string? viewProblem = ViewProblem(view, nouns);
        if (viewProblem is not null)
        {
            Block(title, $"La vista activa no admite etiquetas de {nouns.Singular}.", viewProblem);
            return Result.Cancelled;
        }
        var tagTypes = new FilteredElementCollector(doc)
            .OfCategory(profile.TagCategory)
            .WhereElementIsElementType()
            .OfType<FamilySymbol>()
            .OrderBy(s => s.FamilyName, StringComparer.CurrentCultureIgnoreCase)
            .ThenBy(s => s.Name, StringComparer.CurrentCultureIgnoreCase)
            .ToList();
        if (tagTypes.Count == 0)
        {
            Block(title, $"El modelo no tiene ninguna familia de {nouns.TagName} cargada.",
                $"Se usan solo las etiquetas del propio modelo, y no hay ninguna. Cómo corregirlo: Insertar › Cargar familia y elige una " +
                $"{nouns.TagName} (carpeta Anotaciones), o crea una con la plantilla «{char.ToUpperInvariant(nouns.TagName[0]) + nouns.TagName.Substring(1)}». Después vuelve a pulsar el botón.");
            return Result.Cancelled;
        }
        var tagCategoryId = new ElementId(profile.TagCategory);
        bool tagsHidden;
        try { tagsHidden = view.GetCategoryHidden(tagCategoryId); }
        catch (Exception) { tagsHidden = false; }
        if (tagsHidden)
        {
            Block(title, $"Las {nouns.TagName.Replace("etiqueta", "etiquetas")} están ocultas en esta vista.",
                "Las etiquetas se crearían pero no se verían, y no se podría comprobar que no se pisan. Cómo corregirlo: " +
                $"Visibilidad/Gráficos (VV) › Categorías de anotación › activa «{nouns.TagCategoryName}» (si la vista usa una plantilla, cámbialo en la plantilla).");
            return Result.Cancelled;
        }

        // 2 · Selection: what the user selected in the view; if nothing, ask for it.
        var ids = ui.Selection.GetElementIds().ToList();
        if (ids.Count == 0)
        {
            RevitTaskDialog.Show(title,
                $"Selecciona en la vista {nouns.DefinitePlural} que quieres etiquetar (puedes seleccionar con ventana: los codos, tes y accesorios se descartan solos) " +
                "y pulsa «Finalizar» en la barra de opciones. Esc cancela.");
            try
            {
                ids = ui.Selection.PickObjects(ObjectType.Element, new NetworkSelectionFilter(profile),
                        $"Selecciona {nouns.DefinitePlural} a etiquetar y pulsa Finalizar (Esc cancela).")
                    .Select(r => r.ElementId).ToList();
            }
            catch (Autodesk.Revit.Exceptions.OperationCanceledException) { return Result.Cancelled; }
            if (ids.Count == 0) return Result.Cancelled;
        }

        // 3 · Classification (read-only).
        var projector = new ViewProjector(view);
        var alreadyTagged = TaggedInView(doc, view, tagCategoryId);
        var candidates = new List<PipeTagCandidate>();
        var deviations = new List<PipeTagDeviation>();
        var pieces = new Dictionary<string, (MEPCurve Curve, Line Line, bool Selected)>(StringComparer.Ordinal);
        foreach (var id in ids.Distinct())
        {
            Element? element = doc.GetElement(id);
            if (element is null) continue;
            if (SafeIsHidden(element, view))
            {
                deviations.Add(PipeTagDeviation.Excluded(element, "Está oculto en esta vista.", "Muéstralo en la vista (Mostrar elementos ocultos) si quieres etiquetarlo."));
                continue;
            }
            var kind = KindOf(element, profile);
            if (kind != PipeTagElementKind.Pipe)
            {
                deviations.Add(PipeTagDeviation.Excluded(element, PipeTagRules.ExclusionReason(kind, double.NaN, false, 0, nouns) ?? "", FixFor(kind, double.NaN, false)));
                continue;
            }
            if ((element.Location as LocationCurve)?.Curve is not Line line)
            {
                deviations.Add(PipeTagDeviation.Excluded(element, "El tramo no es recto o no tiene eje legible.", "No requiere acción: solo se etiquetan tramos rectos."));
                continue;
            }
            pieces[element.UniqueId] = ((MEPCurve)element, line, true);
        }

        // One tag per run: the selected pieces are grouped with the pieces in line with them (same type and size, joined
        // directly or by a straight coupling), and only the longest piece of each run is tagged.
        var links = ExpandRuns(pieces, profile);
        double Metres(Line l) => UnitUtils.ConvertFromInternalUnits(l.Length, UnitTypeId.Meters);
        double Projected(Line l) => (projector.Project(l.GetEndPoint(1)) - projector.Project(l.GetEndPoint(0))).Length;
        var runPieces = pieces.Select(kv => new RunPiece(kv.Key, Metres(kv.Value.Line), Projected(kv.Value.Line), kv.Value.Selected,
            alreadyTagged.Contains(kv.Value.Curve.Id))).ToList();
        foreach (var run in PipeTagRuns.Decide(runPieces, links, nouns))
        {
            bool runTagged = run.Pieces.Any(p => p.AlreadyTagged);
            foreach (var piece in run.Pieces.Where(p => p.Selected))
            {
                var (curve, line, _) = pieces[piece.Key];
                if (run.Chosen == piece)
                    candidates.Add(new PipeTagCandidate(curve, line, run.LengthMetres));
                else if (run.Chosen is { } chosen)
                    deviations.Add(PipeTagDeviation.Excluded(curve,
                        $"Es una pieza del mismo tramo que el elemento {pieces[chosen.Key].Curve.Id} (alineadas, mismo tipo y tamaño), que lleva la etiqueta: una etiqueta por tramo.",
                        "No requiere acción."));
                else
                    deviations.Add(PipeTagDeviation.Excluded(curve, run.Reason ?? "", FixFor(PipeTagElementKind.Pipe, run.LengthMetres, runTagged)));
            }
        }

        if (candidates.Count == 0)
        {
            ShowReport(ui, view, profile, "", new List<PipeTagReportRow>(), deviations,
                $"No hay ningún tramo recto de {nouns.Singular} de más de 15 cm que etiquetar en la selección. No se ha modificado el modelo.");
            return Result.Cancelled;
        }

        // 4 · Review and confirmation before modifying the model.
        FamilySymbol? symbol;
        var defaultTypeId = SafeDefaultTypeId(doc, tagCategoryId);
        using (var setup = new PipeTagSetupForm(profile, view.Name, view.Scale, candidates.Count, deviations, tagTypes, defaultTypeId))
        {
            if (setup.ShowDialog() != WF.DialogResult.OK) return Result.Cancelled;
            symbol = setup.SelectedType;
        }
        if (symbol is null) return Result.Cancelled;

        // 5 · Create, lay out without overlaps, verify on Revit's real geometry.
        var tagged = new List<PipeTagReportRow>();
        try
        {
            using var transaction = new Transaction(doc, title);
            var swallower = new CadWarningSwallower();
            var options = transaction.GetFailureHandlingOptions();
            options.SetFailuresPreprocessor(swallower);
            options.SetClearAfterRollback(true);
            transaction.SetFailureHandlingOptions(options);
            if (transaction.Start() != TransactionStatus.Started)
                throw new InvalidOperationException("Revit no permitió iniciar la transacción.");

            if (!symbol.IsActive) symbol.Activate();
            var created = CreateTags(doc, view, symbol, candidates, projector, deviations, nouns);
            doc.Regenerate();

            var requests = new List<PipeTagRequest>();
            foreach (var item in created.Values.ToList())
            {
                BoundingBoxXYZ? box = item.Tag.get_BoundingBox(view);
                if (box is null)
                {
                    Remove(doc, created, item, deviations, "La etiqueta no se ve en la vista (puede ocultarla un filtro o la plantilla de vista), así que no se puede comprobar que no pisa nada; se ha quitado.",
                        "Revisa los filtros de Visibilidad/Gráficos de la vista y vuelve a ejecutar.");
                    continue;
                }
                item.MeasuredHead = projector.Project(item.Tag.TagHeadPosition);
                item.MeasuredBox = projector.ProjectBox(box);
                requests.Add(new PipeTagRequest
                {
                    Key = item.Key,
                    Start = projector.Project(item.Candidate.Line.GetEndPoint(0)),
                    End = projector.Project(item.Candidate.Line.GetEndPoint(1)),
                    MeasuredHead = item.MeasuredHead,
                    MeasuredBox = item.MeasuredBox
                });
            }

            var ownTagIds = new HashSet<ElementId>(created.Values.Select(c => c.Tag.Id));
            var obstacles = AnnotationBoxes(doc, view, ownTagIds, projector);
            TagBox? limit = CropLimit(view, projector);
            var runs = RunSegments(doc, view, projector);
            double gap = PipeTagRules.PaperGapFeet(view.Scale);
            var (placed, unplaced) = PipeTagLayout.PlaceAll(requests, obstacles, gap, limit, runs);

            foreach (string key in unplaced)
                Remove(doc, created, created[key], deviations,
                    "No hay ninguna posición libre entre 20 y 100 cm por encima o por debajo del eje: en todas pisaría otra etiqueta, " +
                    "un texto o una tubería, conducto o bandeja modelada" +
                    (limit is null ? "." : ", o quedaría fuera de la región de recorte."),
                    "Libera espacio moviendo etiquetas o textos cercanos" + (limit is null ? "" : " o amplía la región de recorte") +
                    ", y vuelve a ejecutar seleccionando solo este tramo.");

            foreach (var placement in placed)
            {
                var item = created[placement.Key];
                try { Apply(item, placement, projector); }
                catch (Exception ex)
                {
                    Remove(doc, created, item, deviations, "Revit no permitió colocar la etiqueta en la posición libre calculada: " + ex.Message,
                        "Vuelve a ejecutar seleccionando solo este tramo, o colócala a mano.");
                }
            }
            doc.Regenerate();

            // Verification on what Revit really drew, in placement order (earlier tags win).
            var actual = new List<PipeTagPlacement>();
            foreach (var placement in placed)
            {
                if (!created.TryGetValue(placement.Key, out var item)) continue;
                actual.Add(ActualPlacement(item, placement, view, projector));
            }
            foreach (string key in PipeTagLayout.Conflicts(actual, obstacles, gap, runs))
                Remove(doc, created, created[key], deviations,
                    "Tras colocarla, la etiqueta real de Revit pisaba otra etiqueta, un texto o un tramo modelado; se ha quitado para no dejar solapes.",
                    "Vuelve a ejecutar seleccionando solo este tramo, o colócala a mano.");

            foreach (var placement in placed)
            {
                if (!created.TryGetValue(placement.Key, out var item)) continue;
                string note = item.OrientationNote;
                // Check on Revit's real head that the tag really sits at the planned distance from the axis.
                TagPoint realHead = projector.Project(item.Tag.TagHeadPosition);
                TagPoint a = projector.Project(item.Candidate.Line.GetEndPoint(0)), b = projector.Project(item.Candidate.Line.GetEndPoint(1));
                if (!PipeTagLayout.IsAtAxisOffset(realHead, a, b, placement.AxisOffset))
                    note = (note.Length > 0 ? note + " · " : "") +
                           $"Atención: Revit la dejó a {PipeTagRules.AxisOffsetText(PipeTagLayout.DistanceAboveAxis(realHead, a, b))}, no a {PipeTagRules.AxisOffsetText(placement.AxisOffset)}";
                tagged.Add(PipeTagReportRow.From(profile, item.Candidate.Curve, item.Tag, symbol, item.Candidate.LengthMetres, placement, note));
            }

            if (tagged.Count == 0)
            {
                transaction.RollBack();
            }
            else if (transaction.Commit() != TransactionStatus.Committed)
            {
                throw new InvalidOperationException("Revit no confirmó la transacción.");
            }
        }
        catch (Exception ex)
        {
            message = ex.ToString();
            RevitTaskDialog.Show(title, "No se ha etiquetado nada: " + ex.Message + "\n\nNo se ha modificado el modelo.");
            return Result.Failed;
        }

        string status = tagged.Count > 0
            ? $"{tagged.Count} tramos etiquetados en la vista «{view.Name}». Ctrl+Z deshace todo el etiquetado."
            : "No se ha podido etiquetar ningún tramo. No se ha modificado el modelo.";
        ShowReport(ui, view, profile, symbol, tagged.OrderBy(r => r.System, StringComparer.CurrentCultureIgnoreCase).ThenBy(r => r.SortKey).ToList(), deviations, status);
        return tagged.Count > 0 ? Result.Succeeded : Result.Cancelled;
    }

    // ---------------------------------------------------------------- creation and layout

    private static Dictionary<string, CreatedTag> CreateTags(Document doc, View view, FamilySymbol symbol,
        List<PipeTagCandidate> candidates, ViewProjector projector, List<PipeTagDeviation> deviations, MepTagNouns nouns)
    {
        string fix = $"Comprueba que la familia elegida es una {nouns.TagName} válida y que el tramo se ve en la vista.";
        var created = new Dictionary<string, CreatedTag>(StringComparer.Ordinal);
        foreach (var candidate in candidates)
        {
            XYZ start = candidate.Line.GetEndPoint(0), end = candidate.Line.GetEndPoint(1);
            XYZ middle = (start + end) * 0.5;
            var direction = projector.Project(end) - projector.Project(start);
            var orientation = PipeTagRules.Orthogonality(direction) switch
            {
                0 => TagOrientation.Horizontal,
                1 => TagOrientation.Vertical,
                _ => TagOrientation.AnyModelDirection
            };
            string note = "";
            IndependentTag? tag = null;
            try
            {
                tag = IndependentTag.Create(doc, symbol.Id, view.Id, new Reference(candidate.Curve), false, orientation, middle);
            }
            catch (Exception) when (orientation == TagOrientation.AnyModelDirection)
            {
                note = "Orientación horizontal: la familia no admite girar con el modelo.";
            }
            catch (Exception ex)
            {
                deviations.Add(PipeTagDeviation.Failed(candidate.Curve, "Revit no permitió crear la etiqueta: " + ex.Message, fix));
                continue;
            }
            if (tag is null)
            {
                try { tag = IndependentTag.Create(doc, symbol.Id, view.Id, new Reference(candidate.Curve), false, TagOrientation.Horizontal, middle); }
                catch (Exception ex)
                {
                    deviations.Add(PipeTagDeviation.Failed(candidate.Curve, "Revit no permitió crear la etiqueta: " + ex.Message, fix));
                    continue;
                }
            }
            created[candidate.Curve.UniqueId] = new CreatedTag(candidate.Curve.UniqueId, candidate, tag) { OrientationNote = note };
        }
        return created;
    }

    private static void Apply(CreatedTag item, PipeTagPlacement placement, ViewProjector projector)
    {
        XYZ anchor3d = PointOnCurve(item.Candidate.Line, placement.PipeParameter);
        if (placement.LeaderEnd is not null)
        {
            item.Tag.HasLeader = true;
            Reference? tagged = item.Tag.GetTaggedReferences().FirstOrDefault();
            if (tagged is not null && item.Tag.CanLeaderEndConditionBeAssigned(LeaderEndCondition.Free))
            {
                item.Tag.LeaderEndCondition = LeaderEndCondition.Free;
                item.Tag.SetLeaderEnd(tagged, anchor3d);
                item.FreeLeader = true;
            }
        }
        if ((placement.Head - item.MeasuredHead).Length > 1e-9)
            item.Tag.TagHeadPosition = projector.ToModel(placement.Head, item.Tag.TagHeadPosition);
    }

    /// <summary>The placement as Revit really left it: real head, real box (rigid box for leader tags), real leader end.</summary>
    private static PipeTagPlacement ActualPlacement(CreatedTag item, PipeTagPlacement planned, View view, ViewProjector projector)
    {
        TagPoint head = projector.Project(item.Tag.TagHeadPosition);
        TagBox box;
        TagPoint? leaderEnd = null;
        if (planned.LeaderEnd is null)
        {
            BoundingBoxXYZ? real = item.Tag.get_BoundingBox(view);
            box = real is null ? item.MeasuredBox.Translate(head - item.MeasuredHead) : projector.ProjectBox(real);
        }
        else
        {
            // The Revit box of a tag with a leader also encloses the leader; the head box moves rigidly with the head.
            box = item.MeasuredBox.Translate(head - item.MeasuredHead);
            leaderEnd = planned.LeaderEnd;
            if (item.FreeLeader)
            {
                Reference? tagged = item.Tag.GetTaggedReferences().FirstOrDefault();
                if (tagged is not null)
                {
                    try { leaderEnd = projector.Project(item.Tag.GetLeaderEnd(tagged)); }
                    catch (Exception) { /* keep the planned end */ }
                }
            }
        }
        return new PipeTagPlacement { Key = item.Key, Head = head, Box = box, Kind = planned.Kind, LeaderEnd = leaderEnd, PipeParameter = planned.PipeParameter, AxisOffset = planned.AxisOffset };
    }

    private static void Remove(Document doc, Dictionary<string, CreatedTag> created, CreatedTag item,
        List<PipeTagDeviation> deviations, string reason, string fix)
    {
        try { doc.Delete(item.Tag.Id); } catch (Exception) { /* the transaction is rolled back if nothing remains */ }
        created.Remove(item.Key);
        deviations.Add(PipeTagDeviation.Failed(item.Candidate.Curve, reason, fix));
    }

    private static XYZ PointOnCurve(Line line, double t) => line.GetEndPoint(0) + (line.GetEndPoint(1) - line.GetEndPoint(0)) * t;

    // ---------------------------------------------------------------- reading the view

    internal static string? ViewProblem(View view, MepTagNouns nouns)
    {
        if (view.IsTemplate) return "La vista activa es una plantilla de vista. Abre una planta, sección o alzado y vuelve a pulsar el botón.";
        switch (view.ViewType)
        {
            case ViewType.FloorPlan:
            case ViewType.CeilingPlan:
            case ViewType.EngineeringPlan:
            case ViewType.AreaPlan:
            case ViewType.Section:
            case ViewType.Elevation:
            case ViewType.Detail:
                return null;
            case ViewType.ThreeD:
                return view is View3D { IsLocked: true }
                    ? "Las vistas 3D bloqueadas no se admiten todavía. Abre una planta, sección o alzado y vuelve a pulsar el botón."
                    : "En una vista 3D sin bloquear Revit no permite etiquetar. Abre una planta, sección o alzado y vuelve a pulsar el botón.";
            default:
                return $"La vista activa es de tipo «{view.ViewType}». Abre una planta, sección o alzado donde se vean {nouns.DefinitePlural} y vuelve a pulsar el botón.";
        }
    }

    /// <summary>
    /// Read-only. Follows the connectors of the selected pieces and adds to <paramref name="pieces"/> (as not selected) the
    /// pieces in line with them: same category, type and size, joined directly or through a straight coupling (a fitting
    /// with two connectors). Elbows, tees and size changes end the run. Returns the links between pieces of a run.
    /// </summary>
    private static List<(string A, string B)> ExpandRuns(Dictionary<string, (MEPCurve Curve, Line Line, bool Selected)> pieces, MepTagProfile profile)
    {
        var links = new List<(string, string)>();
        var curveCategory = new ElementId(profile.CurveCategory);
        var fittingCategory = new ElementId(profile.FittingCategory);
        var queue = new Queue<string>(pieces.Keys);
        static (double, double, double) P(XYZ p) => (p.X, p.Y, p.Z);
        while (queue.Count > 0)
        {
            var (curve, line, _) = pieces[queue.Dequeue()];
            string size = PipeTagReportRow.SafeText(() => profile.Size(curve));
            foreach (var next in NeighbourCurves(curve, fittingCategory))
            {
                if (next.Category?.Id.Equals(curveCategory) != true || profile.IsPlaceholder(next) == true) continue;
                if (!next.GetTypeId().Equals(curve.GetTypeId()) || PipeTagReportRow.SafeText(() => profile.Size(next)) != size) continue;
                if ((next.Location as LocationCurve)?.Curve is not Line nextLine) continue;
                if (!PipeTagRuns.SameLine(P(line.GetEndPoint(0)), P(line.GetEndPoint(1)), P(nextLine.GetEndPoint(0)), P(nextLine.GetEndPoint(1)))) continue;
                links.Add((curve.UniqueId, next.UniqueId));
                if (!pieces.ContainsKey(next.UniqueId))
                {
                    pieces[next.UniqueId] = (next, nextLine, false);
                    queue.Enqueue(next.UniqueId);
                }
            }
        }
        return links;
    }

    /// <summary>Curves connected to the ends of <paramref name="curve"/>, directly or through a fitting with exactly two connectors.</summary>
    private static IEnumerable<MEPCurve> NeighbourCurves(MEPCurve curve, ElementId fittingCategory)
    {
        var found = new List<MEPCurve>();
        try
        {
            foreach (Connector end in curve.ConnectorManager.Connectors)
            {
                if (end.ConnectorType != ConnectorType.End || !end.IsConnected) continue;
                foreach (Connector other in end.AllRefs)
                {
                    if (other.Owner is null || other.Owner.Id.Equals(curve.Id)) continue;
                    if (other.Owner is MEPCurve direct) { found.Add(direct); continue; }
                    if (other.Owner is not FamilyInstance fitting || fitting.Category?.Id.Equals(fittingCategory) != true) continue;
                    if (fitting.MEPModel?.ConnectorManager?.Connectors is not ConnectorSet set) continue;
                    var ends = set.Cast<Connector>().Where(c => c.ConnectorType == ConnectorType.End).ToList();
                    if (ends.Count != 2) continue; // tee, cross…: the run ends here
                    var far = ends.FirstOrDefault(c => !c.Origin.IsAlmostEqualTo(other.Origin));
                    if (far is null || !far.IsConnected) continue;
                    foreach (Connector beyond in far.AllRefs)
                        if (beyond.Owner is MEPCurve m && !m.Id.Equals(curve.Id)) found.Add(m);
                }
            }
        }
        catch (Exception) { /* a piece whose connectors cannot be read simply ends its run */ }
        return found;
    }

    private static HashSet<ElementId> TaggedInView(Document doc, View view, ElementId tagCategoryId)
    {
        var tagged = new HashSet<ElementId>();
        foreach (var tag in new FilteredElementCollector(doc, view.Id).OfClass(typeof(IndependentTag)).Cast<IndependentTag>())
        {
            if (tag.Category is null || !tag.Category.Id.Equals(tagCategoryId)) continue;
            try { foreach (var id in tag.GetTaggedLocalElementIds()) tagged.Add(id); }
            catch (Exception) { /* orphaned or unreadable tag: it does not count as tagging the element */ }
        }
        return tagged;
    }

    /// <summary>Existing tags, room/space tags and text notes in the view: new tags must not overlap them.</summary>
    internal static List<TagBox> AnnotationBoxes(Document doc, View view, HashSet<ElementId> exclude, ViewProjector projector)
    {
        var boxes = new List<TagBox>();
        foreach (var type in new[] { typeof(IndependentTag), typeof(SpatialElementTag), typeof(TextNote) })
        {
            foreach (var element in new FilteredElementCollector(doc, view.Id).OfClass(type))
            {
                if (exclude.Contains(element.Id)) continue;
                BoundingBoxXYZ? box = element.get_BoundingBox(view);
                if (box is null) continue;
                var projected = projector.ProjectBox(box);
                if (projected.IsValid) boxes.Add(projected);
            }
        }
        return boxes;
    }

    /// <summary>
    /// Every pipe, duct, cable tray and conduit (rigid or flexible) visible in the view, as segments with half their drawn
    /// width: tags must not cover any of them (including the run being tagged). Leaders may cross them.
    /// </summary>
    internal static List<RunSegment> RunSegments(Document doc, View view, ViewProjector projector)
    {
        var categories = new List<BuiltInCategory>
        {
            BuiltInCategory.OST_PipeCurves, BuiltInCategory.OST_FlexPipeCurves, BuiltInCategory.OST_DuctCurves,
            BuiltInCategory.OST_FlexDuctCurves, BuiltInCategory.OST_CableTray, BuiltInCategory.OST_Conduit
        };
        var segments = new List<RunSegment>();
        foreach (var element in new FilteredElementCollector(doc, view.Id).WherePasses(new ElementMulticategoryFilter(categories)).WhereElementIsNotElementType())
        {
            if (element.Location is not LocationCurve location) continue;
            IList<XYZ> points;
            try { points = location.Curve is Line line ? new[] { line.GetEndPoint(0), line.GetEndPoint(1) } : location.Curve.Tessellate(); }
            catch (Exception) { continue; }
            double half = HalfWidth(element);
            for (int i = 0; i + 1 < points.Count; i++)
                segments.Add(new RunSegment(projector.Project(points[i]), projector.Project(points[i + 1]), half));
        }
        return segments;
    }

    /// <summary>Half of the drawn width: outside diameter for pipes, diameter or width for ducts, trays and conduits.</summary>
    private static double HalfWidth(Element element)
    {
        double outside = element.get_Parameter(BuiltInParameter.RBS_PIPE_OUTER_DIAMETER)?.AsDouble() ?? 0;
        if (outside > 0) return outside / 2;
        if (element is MEPCurve curve)
        {
            try { if (curve.Diameter > 0) return curve.Diameter / 2; } catch (Exception) { /* not round */ }
            try { if (curve.Width > 0) return curve.Width / 2; } catch (Exception) { /* not rectangular */ }
        }
        return 0;
    }

    internal static TagBox? CropLimit(View view, ViewProjector projector)
    {
        try { return view.CropBoxActive ? projector.ProjectBox(view.CropBox) : null; }
        catch (Exception) { return null; }
    }

    internal static ElementId? SafeDefaultTypeId(Document doc, ElementId categoryId)
    {
        try { return doc.GetDefaultFamilyTypeId(categoryId); }
        catch (Exception) { return null; }
    }

    internal static bool SafeIsHidden(Element element, View view)
    {
        try { return element.IsHidden(view); }
        catch (Exception) { return false; }
    }

    private static PipeTagElementKind KindOf(Element element, MepTagProfile profile)
    {
        // Placeholders have their own category, so they are recognised by their class first.
        bool? placeholder = profile.IsPlaceholder(element);
        if (placeholder == true) return PipeTagElementKind.PipePlaceholder;
        ElementId? category = element.Category?.Id;
        if (category is null) return PipeTagElementKind.Other;
        if (placeholder == false && category.Equals(new ElementId(profile.CurveCategory))) return PipeTagElementKind.Pipe;
        if (profile.FlexCategory is { } flex && category.Equals(new ElementId(flex))) return PipeTagElementKind.FlexPipe;
        if (category.Equals(new ElementId(profile.FittingCategory))) return PipeTagElementKind.PipeFitting;
        if (profile.AccessoryCategory is { } accessory && category.Equals(new ElementId(accessory))) return PipeTagElementKind.PipeAccessory;
        return PipeTagElementKind.Other;
    }

    private static string FixFor(PipeTagElementKind kind, double lengthMetres, bool alreadyTagged) => kind switch
    {
        PipeTagElementKind.Pipe when alreadyTagged => "Si necesitas otra etiqueta, bórrala antes o colócala a mano.",
        PipeTagElementKind.Pipe when double.IsFinite(lengthMetres) && !PipeTagRules.IsLongEnough(lengthMetres) => "No requiere acción: los tramos cortos no se etiquetan.",
        PipeTagElementKind.Pipe => "Etiquétalo en una planta o sección donde el tramo se vea en longitud.",
        _ => "No requiere acción: se excluye por la regla de etiquetado."
    };

    internal static void Block(string title, string instruction, string content)
    {
        new RevitTaskDialog(title) { MainInstruction = instruction, MainContent = content + "\n\nNo se ha modificado el modelo." }.Show();
    }

    internal static void ShowReport(UIDocument ui, View view, MepTagProfile profile, FamilySymbol? symbol,
        List<PipeTagReportRow> tagged, List<PipeTagDeviation> deviations, string status)
        => ShowReport(ui, view, profile, symbol is null ? "" : $"{symbol.FamilyName} : {symbol.Name}", tagged, deviations, status);

    internal static void ShowReport(UIDocument ui, View view, MepTagProfile profile, string tagName,
        List<PipeTagReportRow> tagged, List<PipeTagDeviation> deviations, string status)
    {
        using var report = new PipeTagReportForm(profile, view.Name, tagName, status, tagged, deviations,
            ids =>
            {
                try { ui.Selection.SetElementIds(ids); ui.ShowElements(ids); }
                catch (Exception) { /* navigation is only a convenience */ }
            });
        report.ShowDialog();
    }

    // ---------------------------------------------------------------- helper types

    private sealed class NetworkSelectionFilter : ISelectionFilter
    {
        private readonly ElementId[] _categories;

        internal NetworkSelectionFilter(MepTagProfile profile)
            => _categories = new[] { profile.CurveCategory, profile.FittingCategory, profile.AccessoryCategory, profile.FlexCategory }
                .Where(c => c.HasValue).Select(c => new ElementId(c!.Value)).ToArray();

        public bool AllowElement(Element element)
        {
            ElementId? category = element.Category?.Id;
            return category is not null && _categories.Any(c => c.Equals(category));
        }

        public bool AllowReference(Reference reference, XYZ position) => false;
    }

    private sealed class CreatedTag
    {
        internal CreatedTag(string key, PipeTagCandidate candidate, IndependentTag tag) { Key = key; Candidate = candidate; Tag = tag; }
        internal string Key { get; }
        internal PipeTagCandidate Candidate { get; }
        internal IndependentTag Tag { get; }
        internal TagPoint MeasuredHead { get; set; }
        internal TagBox MeasuredBox { get; set; }
        internal bool FreeLeader { get; set; }
        internal string OrientationNote { get; set; } = "";
    }
}

/// <summary>A straight run (pipe or duct) to tag.</summary>
internal sealed class PipeTagCandidate
{
    internal PipeTagCandidate(MEPCurve curve, Line line, double lengthMetres) { Curve = curve; Line = line; LengthMetres = lengthMetres; }
    internal MEPCurve Curve { get; }
    internal Line Line { get; }
    internal double LengthMetres { get; }
}

/// <summary>Projects model points onto the view plane (right/up directions), in feet.</summary>
internal sealed class ViewProjector
{
    private readonly XYZ _origin, _right, _up, _direction;

    internal ViewProjector(View view)
    {
        _origin = view.Origin;
        _right = view.RightDirection;
        _up = view.UpDirection;
        _direction = view.ViewDirection;
    }

    internal TagPoint Project(XYZ point)
    {
        XYZ d = point - _origin;
        return new TagPoint(d.DotProduct(_right), d.DotProduct(_up));
    }

    /// <summary>Model point at view coordinates <paramref name="uv"/>, keeping the depth of <paramref name="depthOf"/>.</summary>
    internal XYZ ToModel(TagPoint uv, XYZ depthOf)
    {
        double depth = (depthOf - _origin).DotProduct(_direction);
        return _origin + _right * uv.X + _up * uv.Y + _direction * depth;
    }

    internal TagBox ProjectBox(BoundingBoxXYZ box)
    {
        Transform transform = box.Transform ?? Transform.Identity;
        var corners = new List<TagPoint>(8);
        foreach (double x in new[] { box.Min.X, box.Max.X })
            foreach (double y in new[] { box.Min.Y, box.Max.Y })
                foreach (double z in new[] { box.Min.Z, box.Max.Z })
                    corners.Add(Project(transform.OfPoint(new XYZ(x, y, z))));
        return TagBox.FromPoints(corners);
    }
}

internal sealed class PipeTagReportRow
{
    internal ElementId PipeId { get; init; } = ElementId.InvalidElementId;
    internal ElementId TagId { get; init; } = ElementId.InvalidElementId;
    internal string Id { get; init; } = "";
    internal string System { get; init; } = "";
    internal string PipeType { get; init; } = "";
    internal string Diameter { get; init; } = "";
    internal string Length { get; init; } = "";
    internal string Tag { get; init; } = "";
    internal string TagText { get; init; } = "";
    internal string Placement { get; init; } = "";
    internal long SortKey { get; init; }

    internal static PipeTagReportRow From(MepTagProfile profile, MEPCurve curve, IndependentTag tag, FamilySymbol symbol,
        double lengthMetres, PipeTagPlacement where, string note)
    {
        string text;
        try { text = tag.TagText ?? ""; } catch (Exception) { text = ""; }
        string size = PipeTagReportRow.SafeText(() => profile.Size(curve));
        string offset = PipeTagRules.AxisOffsetText(where.AxisOffset);
        string placement = where.Kind switch
        {
            PipeTagPlacementKind.OnPipe => offset + ", centrada",
            PipeTagPlacementKind.AlongPipe => offset + ", desplazada a lo largo",
            _ => offset + ", con directriz a su tramo"
        };
        if (note.Length > 0) placement += " · " + note;
        return new PipeTagReportRow
        {
            PipeId = curve.Id,
            TagId = tag.Id,
            Id = curve.Id.ToString(),
            SortKey = long.TryParse(curve.Id.ToString(), out long n) ? n : 0,
            System = SafeText(() => profile.System(curve)),
            PipeType = curve.Document.GetElement(curve.GetTypeId())?.Name ?? "",
            Diameter = size,
            Length = lengthMetres.ToString("0.000"),
            Tag = $"{symbol.FamilyName} : {symbol.Name}",
            TagText = text,
            Placement = placement
        };
    }

    internal static string PlacementText(PipeTagPlacementKind kind) => kind switch
    {
        PipeTagPlacementKind.OnPipe => "20 cm sobre el eje, centrada",
        PipeTagPlacementKind.AlongPipe => "20 cm sobre el eje, desplazada a lo largo",
        PipeTagPlacementKind.Beside => "Junto al elemento, sin tapar su símbolo",
        _ => "Apartada con directriz"
    };

    internal static string SafeText(Func<string> read)
    {
        try { return read() ?? ""; } catch (Exception) { return ""; }
    }
}

internal sealed class PipeTagDeviation
{
    internal ElementId ElementId { get; init; } = ElementId.InvalidElementId;
    internal string Id { get; init; } = "";
    internal string Element { get; init; } = "";
    internal string Kind { get; init; } = "";
    internal string Reason { get; init; } = "";
    internal string Fix { get; init; } = "";

    internal static PipeTagDeviation Excluded(Element element, string reason, string fix) => Create(element, "Excluido por la regla", reason, fix);
    internal static PipeTagDeviation Failed(Element element, string reason, string fix) => Create(element, "No se pudo etiquetar", reason, fix);

    private static PipeTagDeviation Create(Element element, string kind, string reason, string fix)
    {
        string category = element.Category?.Name ?? "Elemento";
        string type = element.Document.GetElement(element.GetTypeId()) is ElementType t ? $"{t.FamilyName} : {t.Name}" : element.Name;
        return new PipeTagDeviation
        {
            ElementId = element.Id,
            Id = element.Id.ToString(),
            Element = $"{category} · {type}",
            Kind = kind,
            Reason = reason,
            Fix = fix
        };
    }
}
