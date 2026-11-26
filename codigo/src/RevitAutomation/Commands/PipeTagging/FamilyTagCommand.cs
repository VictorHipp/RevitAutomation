using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.Attributes;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using Autodesk.Revit.UI.Selection;
using RevitTaskDialog = Autodesk.Revit.UI.TaskDialog;
using View = Autodesk.Revit.DB.View;
using WF = System.Windows.Forms;

namespace RevitAutomation.Addin.Commands.PipeTagging;

/// <summary>
/// Etiquetado › 4 · Tags the loadable families selected in the active view (lighting fixtures, extinguishers,
/// equipment…) with the tags of the model for their category (or multi-category tags), beside their symbol and
/// without overlapping any tag, text or other tagged element. Reports what was tagged and what was not.
/// </summary>
[Transaction(TransactionMode.Manual)]
public sealed class FamilyTagSelectedCommand : IExternalCommand
{
    internal const string Title = "Etiquetar familias seleccionadas";

    internal static readonly MepTagProfile Profile = new()
    {
        Title = Title,
        Nouns = new MepTagNouns("elemento", "elementos", "un elemento", "—", "—", "Etiquetas", "los elementos", "etiqueta"),
        ReportHeaders = new[] { "Id", "Categoría", "Familia : tipo", "Nivel", "Marca", "Etiqueta", "Texto de la etiqueta", "Colocación" }
    };

    /// <summary>Element category → its tag category. Every pair checked to exist in the Revit 2023 and 2027 API.</summary>
    internal static readonly (BuiltInCategory Category, BuiltInCategory Tag)[] TagCategories =
    {
        (BuiltInCategory.OST_LightingFixtures, BuiltInCategory.OST_LightingFixtureTags),
        (BuiltInCategory.OST_LightingDevices, BuiltInCategory.OST_LightingDeviceTags),
        (BuiltInCategory.OST_ElectricalFixtures, BuiltInCategory.OST_ElectricalFixtureTags),
        (BuiltInCategory.OST_ElectricalEquipment, BuiltInCategory.OST_ElectricalEquipmentTags),
        (BuiltInCategory.OST_MechanicalEquipment, BuiltInCategory.OST_MechanicalEquipmentTags),
        (BuiltInCategory.OST_PlumbingFixtures, BuiltInCategory.OST_PlumbingFixtureTags),
        (BuiltInCategory.OST_PlumbingEquipment, BuiltInCategory.OST_PlumbingEquipmentTags),
        (BuiltInCategory.OST_SpecialityEquipment, BuiltInCategory.OST_SpecialityEquipmentTags),
        (BuiltInCategory.OST_GenericModel, BuiltInCategory.OST_GenericModelTags),
        (BuiltInCategory.OST_FireProtection, BuiltInCategory.OST_FireProtectionTags),
        (BuiltInCategory.OST_FireAlarmDevices, BuiltInCategory.OST_FireAlarmDeviceTags),
        (BuiltInCategory.OST_Sprinklers, BuiltInCategory.OST_SprinklerTags),
        (BuiltInCategory.OST_DuctTerminal, BuiltInCategory.OST_DuctTerminalTags),
        (BuiltInCategory.OST_PipeAccessory, BuiltInCategory.OST_PipeAccessoryTags),
        (BuiltInCategory.OST_DuctAccessory, BuiltInCategory.OST_DuctAccessoryTags),
        (BuiltInCategory.OST_CommunicationDevices, BuiltInCategory.OST_CommunicationDeviceTags),
        (BuiltInCategory.OST_DataDevices, BuiltInCategory.OST_DataDeviceTags),
        (BuiltInCategory.OST_SecurityDevices, BuiltInCategory.OST_SecurityDeviceTags),
        (BuiltInCategory.OST_NurseCallDevices, BuiltInCategory.OST_NurseCallDeviceTags),
        (BuiltInCategory.OST_TelephoneDevices, BuiltInCategory.OST_TelephoneDeviceTags),
        (BuiltInCategory.OST_AudioVisualDevices, BuiltInCategory.OST_AudioVisualDeviceTags),
        (BuiltInCategory.OST_Signage, BuiltInCategory.OST_SignageTags),
        (BuiltInCategory.OST_FoodServiceEquipment, BuiltInCategory.OST_FoodServiceEquipmentTags),
        (BuiltInCategory.OST_MedicalEquipment, BuiltInCategory.OST_MedicalEquipmentTags),
        (BuiltInCategory.OST_Furniture, BuiltInCategory.OST_FurnitureTags),
        (BuiltInCategory.OST_FurnitureSystems, BuiltInCategory.OST_FurnitureSystemTags),
        (BuiltInCategory.OST_Casework, BuiltInCategory.OST_CaseworkTags),
        (BuiltInCategory.OST_Doors, BuiltInCategory.OST_DoorTags),
        (BuiltInCategory.OST_Windows, BuiltInCategory.OST_WindowTags),
        (BuiltInCategory.OST_Columns, BuiltInCategory.OST_ColumnTags),
        (BuiltInCategory.OST_Entourage, BuiltInCategory.OST_EntourageTags),
        (BuiltInCategory.OST_Planting, BuiltInCategory.OST_PlantingTags),
        (BuiltInCategory.OST_Parking, BuiltInCategory.OST_ParkingTags),
    };

    private static readonly BuiltInCategory[] LinearRuns =
    {
        BuiltInCategory.OST_PipeCurves, BuiltInCategory.OST_DuctCurves, BuiltInCategory.OST_CableTray, BuiltInCategory.OST_Conduit,
        BuiltInCategory.OST_FlexPipeCurves, BuiltInCategory.OST_FlexDuctCurves
    };

    private static readonly BuiltInCategory[] RunFittings =
    {
        BuiltInCategory.OST_PipeFitting, BuiltInCategory.OST_DuctFitting, BuiltInCategory.OST_CableTrayFitting, BuiltInCategory.OST_ConduitFitting
    };

    /// <summary>Button 4: every loadable family (texts and behaviour as before).</summary>
    internal static readonly FamilyTagScope AllFamilies = new()
    {
        Title = Title,
        Profile = Profile,
        NoTagsTitle = "El modelo no tiene etiquetas de familias cargadas.",
        NoTagsFix = "Se usan solo las etiquetas del propio modelo, y no hay ninguna para luminarias, equipos, protección contra incendios, etc. " +
                    "Cómo corregirlo: Insertar › Cargar familia y elige las etiquetas que necesites (carpeta Anotaciones), o una etiqueta multicategoría. Después vuelve a pulsar el botón.",
        SelectHint = "Selecciona en la vista las familias que quieres etiquetar (luminarias, extintores, equipos…) y pulsa «Finalizar» en la barra de opciones. " +
                     "Puedes seleccionar con ventana: tuberías, conductos, muros y similares se descartan solos. Esc cancela.",
        PickPrompt = "Selecciona las familias a etiquetar y pulsa Finalizar (Esc cancela).",
        Nothing = "No hay ninguna familia que etiquetar en la selección. No se ha modificado el modelo.",
        TaggedPlural = "elementos etiquetados"
    };

    public Result Execute(ExternalCommandData data, ref string message, ElementSet elements) => Run(data, ref message, AllFamilies);

    /// <summary>
    /// Shared engine of buttons 4 (all families) and 5 (only lighting fixtures). With <see cref="FamilyTagScope.Only"/>,
    /// other categories are left out and every visible element of that category is an obstacle for the tags.
    /// </summary>
    internal static Result Run(ExternalCommandData data, ref string message, FamilyTagScope scope)
    {
        string Title = scope.Title;
        MepTagProfile Profile = scope.Profile;
        UIDocument? ui = data.Application.ActiveUIDocument;
        if (ui is null || ui.Document.IsFamilyDocument || ui.Document.IsReadOnly)
        {
            RevitTaskDialog.Show(Title, "Abre un proyecto de Revit editable y vuelve a pulsar el botón. No se ha modificado el modelo.");
            return Result.Cancelled;
        }
        Document doc = ui.Document;
        View view = doc.ActiveView;

        // 1 · Requirements.
        string? viewProblem = MepCurveTagEngine.ViewProblem(view, Profile.Nouns);
        if (viewProblem is not null)
        {
            MepCurveTagEngine.Block(Title, "La vista activa no admite etiquetas.", viewProblem);
            return Result.Cancelled;
        }
        var multiCategory = TagTypes(doc, BuiltInCategory.OST_MultiCategoryTags);
        bool anyTag = multiCategory.Count > 0 || TagCategories.Where(p => scope.Only is null || p.Category == scope.Only)
            .Any(p => TagTypes(doc, p.Tag).Count > 0);
        if (!anyTag)
        {
            MepCurveTagEngine.Block(Title, scope.NoTagsTitle, scope.NoTagsFix);
            return Result.Cancelled;
        }

        // 2 · Selection.
        var ids = ui.Selection.GetElementIds().ToList();
        if (ids.Count == 0)
        {
            RevitTaskDialog.Show(Title, scope.SelectHint);
            try
            {
                ids = ui.Selection.PickObjects(ObjectType.Element, new FamilyInstanceFilter(scope.Only), scope.PickPrompt)
                    .Select(r => r.ElementId).ToList();
            }
            catch (Autodesk.Revit.Exceptions.OperationCanceledException) { return Result.Cancelled; }
            if (ids.Count == 0) return Result.Cancelled;
        }

        // 3 · Classification (read-only).
        var projector = new ViewProjector(view);
        var alreadyTagged = TaggedInView(doc, view);
        var candidates = new List<FamilyCandidate>();
        var deviations = new List<PipeTagDeviation>();
        foreach (var id in ids.Distinct())
        {
            Element? element = doc.GetElement(id);
            if (element is null) continue;
            if (MepCurveTagEngine.SafeIsHidden(element, view))
            {
                deviations.Add(PipeTagDeviation.Excluded(element, "Está oculto en esta vista.", "Muéstralo en la vista (Mostrar elementos ocultos) si quieres etiquetarlo."));
                continue;
            }
            if (scope.Only is { } only && !InCategories(element, new[] { only }))
            {
                deviations.Add(PipeTagDeviation.Excluded(element, scope.OutOfScope + " (categoría «" + (element.Category?.Name ?? "sin categoría") + "»).",
                    "No requiere acción: este botón solo etiqueta " + scope.Profile.Nouns.DefinitePlural + "; usa «4 · Etiquetar familias» para las demás."));
                continue;
            }
            var kind = KindOf(element);
            BoundingBoxXYZ? box = kind == FamilyTagElementKind.FamilyInstance ? element.get_BoundingBox(view) : null;
            string? reason = FamilyTagRules.ExclusionReason(kind, alreadyTagged.Contains(id), hasTagType: true, seenInView: kind != FamilyTagElementKind.FamilyInstance || box is not null);
            if (reason is not null)
            {
                deviations.Add(PipeTagDeviation.Excluded(element, reason, kind == FamilyTagElementKind.FamilyInstance && alreadyTagged.Contains(id)
                    ? "Si necesitas otra etiqueta, bórrala antes o colócala a mano."
                    : "No requiere acción: se excluye por la regla de etiquetado."));
                continue;
            }
            var target = projector.ProjectBox(box!);
            XYZ anchor3d = (element.Location as LocationPoint)?.Point ?? (box!.Min + box.Max) * 0.5;
            candidates.Add(new FamilyCandidate((FamilyInstance)element, target, anchor3d));
        }

        if (candidates.Count == 0)
        {
            MepCurveTagEngine.ShowReport(ui, view, Profile, "", new List<PipeTagReportRow>(), deviations,
                scope.Nothing);
            return Result.Cancelled;
        }

        // 4 · Review: one tag per category, chosen among the model's tags; confirm before modifying.
        var groups = candidates.GroupBy(c => c.CategoryKey).Select(g =>
        {
            var first = g.First().Instance;
            var specific = SpecificTagCategory(first.Category!.Id);
            var options = (specific is { } s ? TagTypes(doc, s) : new List<FamilySymbol>()).Concat(multiCategory).ToList();
            ElementId? preferred = specific is { } sc ? MepCurveTagEngine.SafeDefaultTypeId(doc, new ElementId(sc)) : null;
            preferred ??= MepCurveTagEngine.SafeDefaultTypeId(doc, new ElementId(BuiltInCategory.OST_MultiCategoryTags));
            return new FamilyTagCategoryChoice(g.Key, first.Category.Name, g.Count(), options, preferred);
        }).OrderBy(c => c.CategoryName, StringComparer.CurrentCultureIgnoreCase).ToList();

        using (var setup = new FamilyTagSetupForm(view.Name, view.Scale, groups, deviations))
        {
            if (setup.ShowDialog() != WF.DialogResult.OK) return Result.Cancelled;
        }
        foreach (var candidate in candidates.ToList())
        {
            var choice = groups.First(g => g.CategoryKey == candidate.CategoryKey).Selected;
            if (choice is not null) { candidate.Symbol = choice; continue; }
            candidates.Remove(candidate);
            deviations.Add(PipeTagDeviation.Excluded(candidate.Instance,
                FamilyTagRules.ExclusionReason(FamilyTagElementKind.FamilyInstance, false, hasTagType: false, seenInView: true)!,
                "Carga una etiqueta para esta categoría (Insertar › Cargar familia) o una multicategoría, y vuelve a ejecutar."));
        }
        if (candidates.Count == 0)
        {
            MepCurveTagEngine.ShowReport(ui, view, Profile, "", new List<PipeTagReportRow>(), deviations,
                "No se ha elegido ninguna etiqueta. No se ha modificado el modelo.");
            return Result.Cancelled;
        }

        // 5 · Create, lay out beside each symbol without overlaps, verify on Revit's real geometry.
        var tagged = new List<PipeTagReportRow>();
        try
        {
            using var transaction = new Transaction(doc, Title);
            var swallower = new CadWarningSwallower();
            var options = transaction.GetFailureHandlingOptions();
            options.SetFailuresPreprocessor(swallower);
            options.SetClearAfterRollback(true);
            transaction.SetFailureHandlingOptions(options);
            if (transaction.Start() != TransactionStatus.Started)
                throw new InvalidOperationException("Revit no permitió iniciar la transacción.");

            foreach (var symbol in candidates.Select(c => c.Symbol!).GroupBy(s => s.Id.ToString()).Select(g => g.First()))
                if (!symbol.IsActive) symbol.Activate();

            var created = new Dictionary<string, FamilyCandidate>(StringComparer.Ordinal);
            foreach (var candidate in candidates)
            {
                try
                {
                    candidate.Tag = IndependentTag.Create(doc, candidate.Symbol!.Id, view.Id, new Reference(candidate.Instance), false, TagOrientation.Horizontal, candidate.Anchor3d);
                    created[candidate.Key] = candidate;
                }
                catch (Exception ex)
                {
                    deviations.Add(PipeTagDeviation.Failed(candidate.Instance, "Revit no permitió crear la etiqueta: " + ex.Message,
                        "Comprueba que la etiqueta elegida corresponde a la categoría del elemento (o es multicategoría)."));
                }
            }
            doc.Regenerate();

            var requests = new List<FamilyTagRequest>();
            foreach (var item in created.Values.ToList())
            {
                BoundingBoxXYZ? box = item.Tag!.get_BoundingBox(view);
                if (box is null)
                {
                    Remove(doc, created, item, deviations, "La etiqueta no se ve en la vista (puede ocultarla la visibilidad de su categoría, un filtro o la plantilla), así que no se puede comprobar que no pisa nada; se ha quitado.",
                        "Activa la categoría de etiquetas en Visibilidad/Gráficos (VV) y vuelve a ejecutar.");
                    continue;
                }
                item.MeasuredHead = projector.Project(item.Tag.TagHeadPosition);
                item.MeasuredBox = projector.ProjectBox(box);
                requests.Add(new FamilyTagRequest
                {
                    Key = item.Key, Target = item.Target, Anchor = projector.Project(item.Anchor3d),
                    MeasuredHead = item.MeasuredHead, MeasuredBox = item.MeasuredBox
                });
            }

            var ownTagIds = new HashSet<ElementId>(created.Values.Select(c => c.Tag!.Id));
            var obstacles = MepCurveTagEngine.AnnotationBoxes(doc, view, ownTagIds, projector);
            // Symbols of every selected family are obstacles too: no tag covers another element.
            obstacles.AddRange(candidates.Select(c => c.Target));
            // Button 5: also every other element of the category seen in the view (no tag covers any luminaire).
            if (scope.Only is { } avoid) obstacles.AddRange(VisibleBoxes(doc, view, avoid, projector));
            TagBox? limit = MepCurveTagEngine.CropLimit(view, projector);
            double gap = PipeTagRules.PaperGapFeet(view.Scale);
            var (placed, unplaced) = FamilyTagLayout.PlaceAll(requests, obstacles, gap, limit);

            foreach (string key in unplaced)
                Remove(doc, created, created[key], deviations,
                    "No hay ninguna posición libre: junto al símbolo o apartada con directriz pisaría otra etiqueta, texto u otro elemento seleccionado" +
                    (limit is null ? "." : ", o quedaría fuera de la región de recorte."),
                    "Libera espacio moviendo etiquetas o textos cercanos" + (limit is null ? "" : " o amplía la región de recorte") +
                    ", y vuelve a ejecutar seleccionando solo este elemento.");

            foreach (var placement in placed)
            {
                var item = created[placement.Key];
                try { Apply(item, placement, projector); }
                catch (Exception ex)
                {
                    Remove(doc, created, item, deviations, "Revit no permitió colocar la etiqueta en la posición libre calculada: " + ex.Message,
                        "Vuelve a ejecutar seleccionando solo este elemento, o colócala a mano.");
                }
            }
            doc.Regenerate();

            var actual = new List<PipeTagPlacement>();
            foreach (var placement in placed)
            {
                if (!created.TryGetValue(placement.Key, out var item)) continue;
                actual.Add(ActualPlacement(item, placement, view, projector));
            }
            foreach (string key in PipeTagLayout.Conflicts(actual, obstacles, gap))
                Remove(doc, created, created[key], deviations,
                    "Tras colocarla, la etiqueta real de Revit pisaba otra etiqueta, texto o elemento; se ha quitado para no dejar solapes.",
                    "Vuelve a ejecutar seleccionando solo este elemento, o colócala a mano.");

            foreach (var placement in placed)
                if (created.TryGetValue(placement.Key, out var item))
                    tagged.Add(Row(item, placement.Kind));

            if (tagged.Count == 0) transaction.RollBack();
            else if (transaction.Commit() != TransactionStatus.Committed)
                throw new InvalidOperationException("Revit no confirmó la transacción.");
        }
        catch (Exception ex)
        {
            message = ex.ToString();
            RevitTaskDialog.Show(Title, "No se ha etiquetado nada: " + ex.Message + "\n\nNo se ha modificado el modelo.");
            return Result.Failed;
        }

        string status = tagged.Count > 0
            ? $"{tagged.Count} {scope.TaggedPlural} en la vista «{view.Name}». Ctrl+Z deshace todo el etiquetado."
            : "No se ha podido etiquetar ningún elemento. No se ha modificado el modelo.";
        string tagNames = string.Join(" · ", groups.Where(g => g.Selected is not null).Select(g => $"{g.CategoryName}: {g.Selected!.FamilyName} : {g.Selected.Name}"));
        MepCurveTagEngine.ShowReport(ui, view, Profile, tagNames,
            tagged.OrderBy(r => r.System, StringComparer.CurrentCultureIgnoreCase).ThenBy(r => r.SortKey).ToList(), deviations, status);
        return tagged.Count > 0 ? Result.Succeeded : Result.Cancelled;
    }

    // ---------------------------------------------------------------- helpers

    private static List<FamilySymbol> TagTypes(Document doc, BuiltInCategory category)
        => new FilteredElementCollector(doc).OfCategory(category).WhereElementIsElementType().OfType<FamilySymbol>()
            .OrderBy(s => s.FamilyName, StringComparer.CurrentCultureIgnoreCase).ThenBy(s => s.Name, StringComparer.CurrentCultureIgnoreCase).ToList();

    private static BuiltInCategory? SpecificTagCategory(ElementId categoryId)
    {
        foreach (var (category, tag) in TagCategories)
            if (categoryId.Equals(new ElementId(category))) return tag;
        return null;
    }

    /// <summary>Projected boxes of the elements of a category visible in the view (read-only).</summary>
    private static IEnumerable<TagBox> VisibleBoxes(Document doc, View view, BuiltInCategory category, ViewProjector projector)
    {
        var boxes = new List<TagBox>();
        foreach (var element in new FilteredElementCollector(doc, view.Id).OfCategory(category).WhereElementIsNotElementType())
        {
            BoundingBoxXYZ? box = element.get_BoundingBox(view);
            if (box is not null) boxes.Add(projector.ProjectBox(box));
        }
        return boxes;
    }

    private static bool InCategories(Element element, BuiltInCategory[] categories)
    {
        ElementId? id = element.Category?.Id;
        return id is not null && categories.Any(c => id.Equals(new ElementId(c)));
    }

    internal static FamilyTagElementKind KindOf(Element element)
    {
        if (element is MEPCurve || InCategories(element, LinearRuns)) return FamilyTagElementKind.LinearRun;
        if (InCategories(element, RunFittings)) return FamilyTagElementKind.RunFitting;
        return element is FamilyInstance { Category: not null } ? FamilyTagElementKind.FamilyInstance : FamilyTagElementKind.NotLoadableFamily;
    }

    /// <summary>Elements tagged by any tag already in the view (any tag category): they are not tagged twice.</summary>
    private static HashSet<ElementId> TaggedInView(Document doc, View view)
    {
        var tagged = new HashSet<ElementId>();
        foreach (var tag in new FilteredElementCollector(doc, view.Id).OfClass(typeof(IndependentTag)).Cast<IndependentTag>())
        {
            try { foreach (var id in tag.GetTaggedLocalElementIds()) tagged.Add(id); }
            catch (Exception) { /* orphaned or unreadable tag */ }
        }
        return tagged;
    }

    private static void Apply(FamilyCandidate item, PipeTagPlacement placement, ViewProjector projector)
    {
        if (placement.LeaderEnd is not null)
        {
            item.Tag!.HasLeader = true;
            Reference? reference = item.Tag.GetTaggedReferences().FirstOrDefault();
            if (reference is not null && item.Tag.CanLeaderEndConditionBeAssigned(LeaderEndCondition.Free))
            {
                item.Tag.LeaderEndCondition = LeaderEndCondition.Free;
                item.Tag.SetLeaderEnd(reference, item.Anchor3d);
                item.FreeLeader = true;
            }
        }
        if ((placement.Head - item.MeasuredHead).Length > 1e-9)
            item.Tag!.TagHeadPosition = projector.ToModel(placement.Head, item.Tag.TagHeadPosition);
    }

    private static PipeTagPlacement ActualPlacement(FamilyCandidate item, PipeTagPlacement planned, View view, ViewProjector projector)
    {
        TagPoint head = projector.Project(item.Tag!.TagHeadPosition);
        TagBox box;
        TagPoint? leaderEnd = null;
        if (planned.LeaderEnd is null)
        {
            BoundingBoxXYZ? real = item.Tag.get_BoundingBox(view);
            box = real is null ? item.MeasuredBox.Translate(head - item.MeasuredHead) : projector.ProjectBox(real);
        }
        else
        {
            box = item.MeasuredBox.Translate(head - item.MeasuredHead);
            leaderEnd = planned.LeaderEnd;
            if (item.FreeLeader && item.Tag.GetTaggedReferences().FirstOrDefault() is { } reference)
            {
                try { leaderEnd = projector.Project(item.Tag.GetLeaderEnd(reference)); }
                catch (Exception) { /* keep the planned end */ }
            }
        }
        return new PipeTagPlacement { Key = item.Key, Head = head, Box = box, Kind = planned.Kind, LeaderEnd = leaderEnd, Target = item.Target };
    }

    private static void Remove(Document doc, Dictionary<string, FamilyCandidate> created, FamilyCandidate item,
        List<PipeTagDeviation> deviations, string reason, string fix)
    {
        try { if (item.Tag is not null) doc.Delete(item.Tag.Id); } catch (Exception) { /* rolled back if nothing remains */ }
        created.Remove(item.Key);
        deviations.Add(PipeTagDeviation.Failed(item.Instance, reason, fix));
    }

    private static PipeTagReportRow Row(FamilyCandidate item, PipeTagPlacementKind kind)
    {
        var instance = item.Instance;
        string level = instance.LevelId != ElementId.InvalidElementId ? instance.Document.GetElement(instance.LevelId)?.Name ?? "" : "";
        return new PipeTagReportRow
        {
            PipeId = instance.Id,
            TagId = item.Tag!.Id,
            Id = instance.Id.ToString(),
            SortKey = long.TryParse(instance.Id.ToString(), out long n) ? n : 0,
            System = instance.Category?.Name ?? "",
            PipeType = $"{instance.Symbol?.FamilyName} : {instance.Symbol?.Name}",
            Diameter = level,
            Length = PipeTagReportRow.SafeText(() => instance.get_Parameter(BuiltInParameter.ALL_MODEL_MARK)?.AsString() ?? ""),
            Tag = $"{item.Symbol!.FamilyName} : {item.Symbol.Name}",
            TagText = PipeTagReportRow.SafeText(() => item.Tag.TagText ?? ""),
            Placement = PipeTagReportRow.PlacementText(kind)
        };
    }

    private sealed class FamilyInstanceFilter : ISelectionFilter
    {
        private readonly BuiltInCategory? _only;
        internal FamilyInstanceFilter(BuiltInCategory? only) => _only = only;
        public bool AllowElement(Element element) => KindOf(element) == FamilyTagElementKind.FamilyInstance
                                                     && (_only is not { } only || InCategories(element, new[] { only }));
        public bool AllowReference(Reference reference, XYZ position) => false;
    }
}

/// <summary>
/// Etiquetado › 5 · Tags the lighting fixtures selected in the active view, exactly like button 4 (one tag per luminaire,
/// beside its symbol, no overlaps, leader if needed, report), but only luminaires, only lighting-fixture or multi-category
/// tags, and every luminaire visible in the view is avoided, selected or not.
/// </summary>
[Transaction(TransactionMode.Manual)]
public sealed class LuminaireTagSelectedCommand : IExternalCommand
{
    internal const string Title = "Etiquetar luminarias seleccionadas";

    internal static readonly FamilyTagScope Scope = new()
    {
        Title = Title,
        Profile = new MepTagProfile
        {
            Title = Title,
            Nouns = new MepTagNouns("luminaria", "luminarias", "una luminaria", "—", "—", "Etiquetas de luminaria", "las luminarias", "etiqueta de luminaria"),
            ReportHeaders = FamilyTagSelectedCommand.Profile.ReportHeaders
        },
        Only = BuiltInCategory.OST_LightingFixtures,
        NoTagsTitle = "El modelo no tiene etiquetas de luminaria cargadas.",
        NoTagsFix = "Se usan solo las etiquetas del propio modelo, y no hay ninguna de luminaria ni multicategoría. " +
                    "Cómo corregirlo: Insertar › Cargar familia y elige una etiqueta de luminaria (carpeta Anotaciones › Eléctrico) o una multicategoría. Después vuelve a pulsar el botón.",
        SelectHint = "Selecciona en la vista las luminarias que quieres etiquetar y pulsa «Finalizar» en la barra de opciones. " +
                     "Puedes seleccionar con ventana: solo se cogen las luminarias, lo demás se descarta solo. Esc cancela.",
        PickPrompt = "Selecciona las luminarias a etiquetar y pulsa Finalizar (Esc cancela).",
        Nothing = "No hay ninguna luminaria que etiquetar en la selección. No se ha modificado el modelo.",
        OutOfScope = "No es una luminaria",
        TaggedPlural = "luminarias etiquetadas"
    };

    public Result Execute(ExternalCommandData data, ref string message, ElementSet elements) =>
        FamilyTagSelectedCommand.Run(data, ref message, Scope);
}

/// <summary>What a family-tagging button tags and how it talks to the user.</summary>
internal sealed class FamilyTagScope
{
    internal string Title { get; init; } = "";
    internal MepTagProfile Profile { get; init; } = new();
    /// <summary>Null: every loadable family. Else only this category (and its elements in the view are obstacles).</summary>
    internal BuiltInCategory? Only { get; init; }
    internal string NoTagsTitle { get; init; } = "";
    internal string NoTagsFix { get; init; } = "";
    internal string SelectHint { get; init; } = "";
    internal string PickPrompt { get; init; } = "";
    internal string Nothing { get; init; } = "";
    internal string OutOfScope { get; init; } = "";
    internal string TaggedPlural { get; init; } = "";
}

internal sealed class FamilyCandidate
{
    internal FamilyCandidate(FamilyInstance instance, TagBox target, XYZ anchor3d)
    {
        Instance = instance;
        Target = target;
        Anchor3d = anchor3d;
        Key = instance.UniqueId;
        CategoryKey = instance.Category?.Id.ToString() ?? "";
    }

    internal FamilyInstance Instance { get; }
    internal TagBox Target { get; }
    internal XYZ Anchor3d { get; }
    internal string Key { get; }
    internal string CategoryKey { get; }
    internal FamilySymbol? Symbol { get; set; }
    internal IndependentTag? Tag { get; set; }
    internal TagPoint MeasuredHead { get; set; }
    internal TagBox MeasuredBox { get; set; }
    internal bool FreeLeader { get; set; }
}

/// <summary>One row of the review table: a category, how many elements and which tag of the model to use.</summary>
internal sealed class FamilyTagCategoryChoice
{
    internal FamilyTagCategoryChoice(string key, string name, int count, List<FamilySymbol> options, ElementId? preferred)
    {
        CategoryKey = key;
        CategoryName = name;
        Count = count;
        Options = options;
        Selected = options.FirstOrDefault(o => preferred is not null && o.Id.Equals(preferred)) ?? options.FirstOrDefault();
    }

    internal string CategoryKey { get; }
    internal string CategoryName { get; }
    internal int Count { get; }
    internal List<FamilySymbol> Options { get; }
    internal FamilySymbol? Selected { get; set; }
}

internal sealed class FamilyTagSetupForm : WF.Form
{
    internal const string NoTag = "(No etiquetar)";

    internal FamilyTagSetupForm(string viewName, int viewScale, List<FamilyTagCategoryChoice> groups, List<PipeTagDeviation> excluded)
    {
        Text = FamilyTagSelectedCommand.Title + " · Revisar antes de etiquetar";
        Width = 1050;
        Height = 650;
        StartPosition = WF.FormStartPosition.CenterScreen;
        MinimizeBox = false;
        AutoScaleMode = WF.AutoScaleMode.Dpi;

        int total = groups.Sum(g => g.Count);
        var header = new WF.Label
        {
            Dock = WF.DockStyle.Top,
            Height = 104,
            Padding = new WF.Padding(10),
            Text = $"Vista: {viewName} (escala 1:{viewScale})\n\n" +
                   $"Se etiquetarán {total} familias de {groups.Count} categorías. Elige en la tabla la etiqueta del modelo para cada categoría " +
                   "(las de su categoría y las multicategoría). Las categorías sin etiqueta cargada no se etiquetan.\n" +
                   "Cada etiqueta se coloca junto a su símbolo sin taparlo; si no cabe, se aparta con directriz. Si aun así no cabe, no se crea y aparecerá en desviaciones."
        };

        var grid = PipeTagReportForm.CreateGrid();
        grid.ReadOnly = false;
        grid.MultiSelect = false;
        grid.Dock = WF.DockStyle.Top;
        grid.Height = 220;
        grid.Columns.Add(new WF.DataGridViewTextBoxColumn { HeaderText = "Categoría", Width = 260, ReadOnly = true });
        grid.Columns.Add(new WF.DataGridViewTextBoxColumn { HeaderText = "Elementos", Width = 80, ReadOnly = true });
        var combo = new WF.DataGridViewComboBoxColumn { HeaderText = "Etiqueta del modelo", Width = 560, FlatStyle = WF.FlatStyle.Flat };
        grid.Columns.Add(combo);
        foreach (var group in groups)
        {
            int i = grid.Rows.Add(group.CategoryName, group.Count);
            var cell = (WF.DataGridViewComboBoxCell)grid.Rows[i].Cells[2];
            cell.Items.Add(NoTag);
            foreach (var option in group.Options) cell.Items.Add(Label(option));
            cell.Value = group.Selected is null ? NoTag : Label(group.Selected);
            grid.Rows[i].Tag = group;
            if (group.Options.Count == 0) grid.Rows[i].DefaultCellStyle.BackColor = System.Drawing.Color.LightYellow;
        }

        var excludedLabel = new WF.Label { Dock = WF.DockStyle.Top, Height = 28, Padding = new WF.Padding(10, 8, 0, 0), Text = $"No se etiquetarán ({excluded.Count}):" };
        var list = PipeTagReportForm.CreateGrid();
        PipeTagReportForm.AddColumns(list, ("Id", 80), ("Elemento", 280), ("Motivo", 420), ("Cómo seguir", 220));
        foreach (var d in excluded) list.Rows.Add(d.Id, d.Element, d.Reason, d.Fix);

        var ok = new WF.Button { Text = "Etiquetar", AutoSize = true };
        ok.Click += (_, _) =>
        {
            grid.EndEdit();
            foreach (WF.DataGridViewRow row in grid.Rows)
            {
                var group = (FamilyTagCategoryChoice)row.Tag!;
                string value = row.Cells[2].Value as string ?? NoTag;
                group.Selected = group.Options.FirstOrDefault(o => Label(o) == value);
            }
            if (groups.All(g => g.Selected is null))
            {
                WF.MessageBox.Show(this, "Elige una etiqueta para al menos una categoría.", Text);
                return;
            }
            DialogResult = WF.DialogResult.OK;
            Close();
        };
        var cancel = new WF.Button { Text = "Cancelar", AutoSize = true, DialogResult = WF.DialogResult.Cancel };
        var actions = new WF.FlowLayoutPanel { Dock = WF.DockStyle.Bottom, Height = 48, FlowDirection = WF.FlowDirection.RightToLeft, Padding = new WF.Padding(6) };
        actions.Controls.Add(ok);
        actions.Controls.Add(cancel);

        Controls.Add(list);
        Controls.Add(excludedLabel);
        Controls.Add(grid);
        Controls.Add(actions);
        Controls.Add(header);
        CancelButton = cancel;
    }

    private static string Label(FamilySymbol symbol)
    {
        bool multi = symbol.Category is not null && symbol.Category.Id.Equals(new ElementId(BuiltInCategory.OST_MultiCategoryTags));
        return $"{symbol.FamilyName} : {symbol.Name}" + (multi ? "   (multicategoría)" : "");
    }
}
