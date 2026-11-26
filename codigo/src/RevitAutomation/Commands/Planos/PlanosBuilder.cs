using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using View = Autodesk.Revit.DB.View;
using Color = Autodesk.Revit.DB.Color;

namespace RevitAutomation.Addin.Commands.Planos;

/// <summary>What the user chose in the form, besides <see cref="PlanosOptions"/>.</summary>
internal sealed class PlanosChoices
{
    internal PlanosOptions Options { get; set; } = new PlanosOptions();
    /// <summary>Invalid = sheets without title block.</summary>
    internal ElementId TitleBlockId { get; set; } = ElementId.InvalidElementId;
    /// <summary>Template whose settings are copied (not assigned) to the new views; null = module settings.</summary>
    internal View? Template { get; set; }
}

internal sealed class PlanosResult
{
    internal int Views, Sheets, Schedules, Legends, Tags, Filters, ReplacedViews, ReplacedSheets;
    internal List<ElementId> SheetIds { get; } = new List<ElementId>();
    internal List<ElementId> ViewIds { get; } = new List<ElementId>();
    internal List<string> Notes { get; } = new List<string>();
    internal ViewSheet? FirstSheet;
    internal View? FirstView;
}

/// <summary>Thrown with the step that failed (discipline, view or sheet), so the user knows where.</summary>
internal sealed class PlanosStepException : Exception
{
    internal PlanosStepException(string step, Exception inner) : base(step + ": " + inner.Message, inner) => Step = step;
    internal string Step { get; }
}

/// <summary>Creates the documentation. Never edits model elements: only views, sheets, schedules, legends, filters and tags.</summary>
internal sealed class PlanosBuilder
{
    private const double Mm = 1 / PlanosScale.MmPerFoot;
    private readonly Document _doc;
    private readonly PlanosInventory _inv;
    private readonly PlanosChoices _choices;
    private readonly PlanosResult _result = new PlanosResult();
    private string _step = "preparación";
    private ElementId? _solid;
    private readonly Dictionary<(PlanosDiscipline, PlanosNetwork), (List<PlanosElement> Elements, PlanosBoundsIndex Index)> _hideCandidates =
        new Dictionary<(PlanosDiscipline, PlanosNetwork), (List<PlanosElement>, PlanosBoundsIndex)>();
    private ILookup<(PlanosDiscipline? Discipline, int Level), PlanosElement>? _byLevel;
    private readonly Dictionary<ElementId, Element?> _modelElements = new Dictionary<ElementId, Element?>();
    private readonly Dictionary<string, ElementId?> _categories = new Dictionary<string, ElementId?>(StringComparer.Ordinal);
    private Dictionary<string, (byte R, byte G, byte B, string Label)>? _genericLegend;
    private readonly Dictionary<(byte R, byte G, byte B, string Label), OverrideGraphicSettings> _paints = new Dictionary<(byte, byte, byte, string), OverrideGraphicSettings>();

    private IEnumerable<PlanosElement> OnLevel(PlanosDiscipline d, int level) =>
        (_byLevel ??= _inv.Elements.ToLookup(e => (e.Class.Discipline, e.Level)))[(d, level)];

    // Only model elements are cached, after cleanup; PLANOS never mutates these elements.
    private Element? ModelElement(ElementId id)
    {
        if (!_modelElements.TryGetValue(id, out var element)) _modelElements[id] = element = _doc.GetElement(id);
        return element;
    }

    private IEnumerable<PlanosElement> HiddenCandidates(PlanosDiscipline discipline, PlanosNetwork network, PlannedView? pv)
    {
        if (!_hideCandidates.TryGetValue((discipline, network), out var candidates))
        {
            var own = new HashSet<string>(PlanosClassifier.CategoriesOf(discipline), StringComparer.Ordinal);
            var elements = _inv.Elements.Where(e => e.Class.Discipline is not null &&
                !PlanosClassifier.ShownIn(e.Class, discipline, network) && own.Contains(e.Category)).ToList();
            candidates = (elements, new PlanosBoundsIndex(elements.Select(e => e.Box)));
            _hideCandidates.Add((discipline, network), candidates);
        }
        return pv is null ? candidates.Elements : candidates.Index.Query(pv.Crop).Select(i => candidates.Elements[i]);
    }

    private readonly Dictionary<ElementId, ElementId> _filterOfType = new Dictionary<ElementId, ElementId>();
    private readonly Dictionary<PlanosDiscipline, List<(View View, PlannedView? Plan, string Key)>> _plans = new Dictionary<PlanosDiscipline, List<(View, PlannedView?, string)>>();
    private readonly Dictionary<PlanosDiscipline, List<View>> _extras = new Dictionary<PlanosDiscipline, List<View>>(); // legend + schedules

    private PlanosOptions O => _choices.Options;

    internal PlanosBuilder(Document doc, PlanosInventory inv, PlanosChoices choices) { _doc = doc; _inv = inv; _choices = choices; }

    internal PlanosResult Run(PlanosPlan plan)
    {
        using var group = new TransactionGroup(_doc, "RevitAutomation · Planos");
        group.Start();
        try
        {
            Step("Retirar la documentación generada antes", Cleanup);
            if (O.Mode != PlanosMode.SoloLaminas)
            {
                Step("Filtros de sistemas", Filters);
                Step("Vistas en planta", () => Views(plan));
                if (O.View3D) Step("Vistas 3D", Views3D);
                if (O.Schedules) Step("Cuadros y leyendas", Extras);
                if (O.Tags) Step("Etiquetas", Tags);
            }
            else Step("Vistas de la generación anterior", Reuse);
            if (O.Mode != PlanosMode.SoloVistas) Step("Láminas", Sheets);
            group.Assimilate();
            return _result;
        }
        catch
        {
            if (group.HasStarted() && !group.HasEnded()) group.RollBack();
            throw;
        }
    }

    /// <summary>One transaction per step: if anything fails, the whole group is rolled back and the project stays as it was.</summary>
    private void Step(string name, Action body)
    {
        _step = name;
        using var t = new Transaction(_doc, "Planos · " + name);
        var fho = t.GetFailureHandlingOptions();
        fho.SetFailuresPreprocessor(new WarningSwallower());
        t.SetFailureHandlingOptions(fho);
        t.Start();
        try
        {
            body();
            if (t.Commit() != TransactionStatus.Committed) throw new InvalidOperationException("Revit no aceptó los cambios.");
        }
        catch (Exception ex)
        {
            if (t.HasStarted() && !t.HasEnded()) t.RollBack();
            throw ex is PlanosStepException ? ex : new PlanosStepException(_step, ex);
        }
    }

    private void Sub(string what) => _step = what;

    // ───────────── 1 · Remove the previous run of these disciplines (only elements carrying the PLANOS mark) ─────────────

    private void Cleanup()
    {
        var viewports = new FilteredElementCollector(_doc).OfClass(typeof(Viewport)).Cast<Viewport>().ToLookup(vp => vp.ViewId);
        var ownedSheets = new HashSet<ElementId>(_inv.Owned.Where(o => o.Kind == PlanosMark.Sheet).Select(o => o.Id));
        foreach (var d in O.Disciplines)
        {
            foreach (var s in _inv.OwnedOf(d, PlanosMark.Sheet).ToList())
                if (_doc.GetElement(s.Id) is not null) { _doc.Delete(s.Id); _result.ReplacedSheets++; }
            if (O.Mode == PlanosMode.SoloLaminas) continue;
            foreach (var kind in new[] { PlanosMark.Plan, PlanosMark.Legend, PlanosMark.Schedule })
                foreach (var v in _inv.OwnedOf(d, kind).ToList())
                {
                    if (_doc.GetElement(v.Id) is null) continue;
                    // A generated view that the user placed on one of THEIR sheets is kept (their sheet is not touched).
                    if (viewports[v.Id].Any(vp => vp.IsValidObject && !ownedSheets.Contains(vp.SheetId)))
                    {
                        _result.Notes.Add($"La vista «{v.Name}» se conserva porque está en una lámina suya; la nueva lleva otro nombre.");
                        continue;
                    }
                    _doc.Delete(v.Id);
                    if (kind == PlanosMark.Plan) _result.ReplacedViews++;
                }
        }
    }

    // ───────────── 2 · One filter per MEP system type (shared by every discipline): colour and visibility by system ─────────────

    private void Filters()
    {
        _solid = new FilteredElementCollector(_doc).OfClass(typeof(FillPatternElement)).Cast<FillPatternElement>()
            .FirstOrDefault(f => { try { return f.GetFillPattern().IsSolidFill; } catch { return false; } })?.Id;
        if (_solid is null) _result.Notes.Add("El proyecto no tiene un patrón de relleno sólido: los colores se aplican solo a las líneas.");

        // Only plans with pipes or ducts need them; every used type gets one, because other networks must be hidden too.
        if (!O.Disciplines.Any(d => d is PlanosDiscipline.Fontaneria or PlanosDiscipline.Climatizacion or PlanosDiscipline.Genericas)) return;
        var used = _inv.SystemTypes.Values.Where(t => t.Uses > 0).Select(t => t.Id).ToList();
        if (used.Count == 0) return;

        var existing = new FilteredElementCollector(_doc).OfClass(typeof(ParameterFilterElement)).Cast<ParameterFilterElement>().ToList();
        var names = new HashSet<string>(existing.Select(f => f.Name), StringComparer.OrdinalIgnoreCase);
        var marked = new Dictionary<string, ParameterFilterElement>(StringComparer.Ordinal);
        foreach (var f in existing)
            if (PlanosMark.Parse(PlanosMark.Read(f)) is { Kind: PlanosMark.Filter } m && !marked.ContainsKey(m.Key))
                marked.Add(m.Key, f); // First match, just as FirstOrDefault did.
        var filterable = new Dictionary<bool, List<ElementId>>();
        var pipeCats = Categories(PlanosClassifier.PipeCategories);
        var ductCats = Categories(PlanosClassifier.DuctCategories);
        foreach (var typeId in used)
        {
            if (!_inv.SystemTypes.TryGetValue(typeId, out var st)) continue;
            Sub("Filtro del sistema «" + st.Name + "»");
            var param = new ElementId(st.IsPipe ? BuiltInParameter.RBS_PIPING_SYSTEM_TYPE_PARAM : BuiltInParameter.RBS_DUCT_SYSTEM_TYPE_PARAM);
            if (!filterable.TryGetValue(st.IsPipe, out var cats))
                filterable[st.IsPipe] = cats = (st.IsPipe ? pipeCats : ductCats)
                    .Where(c => ParameterFilterUtilities.GetFilterableParametersInCommon(_doc, new List<ElementId> { c }).Contains(param)).ToList();
            if (cats.Count == 0) continue;
            string key = typeId.ToString();
            marked.TryGetValue(key, out var mine);
            if (mine is null)
            {
                string name = PlanosNaming.UniqueName("RA Planos - " + (st.IsPipe ? "Tubería" : "Conducto") + " - " + st.Name, names);
                names.Add(name);
                mine = ParameterFilterElement.Create(_doc, name, cats, new ElementParameterFilter(ParameterFilterRuleFactory.CreateEqualsRule(param, typeId)));
                PlanosMark.Set(mine, "*", PlanosMark.Filter, key);
                _result.Filters++;
            }
            _filterOfType[typeId] = mine.Id;
        }
    }

    private List<ElementId> Categories(IEnumerable<string> names)
    {
        var result = new List<ElementId>();
        foreach (string n in names)
        {
            if (!_categories.TryGetValue(n, out var id))
            {
                id = null;
                if (Enum.TryParse(n, out BuiltInCategory bic))
                    try { id = Category.GetCategory(_doc, bic)?.Id; } catch { }
                _categories[n] = id;
            }
            if (id is not null) result.Add(id);
        }
        return result;
    }

    private OverrideGraphicSettings Paint((byte R, byte G, byte B, string Label) c, bool lines = true)
    {
        if (lines && _paints.TryGetValue(c, out var cached)) return cached;
        var colour = new Color(c.R, c.G, c.B);
        var o = new OverrideGraphicSettings();
        if (lines) o.SetProjectionLineColor(colour).SetCutLineColor(colour);
        if (_solid is not null) o.SetSurfaceForegroundPatternId(_solid).SetSurfaceForegroundPatternColor(colour).SetCutForegroundPatternId(_solid).SetCutForegroundPatternColor(colour);
        if (lines) _paints[c] = o;
        return o;
    }

    // ───────────── 3 · Plan views ─────────────

    private void Views(PlanosPlan plan)
    {
        if (_inv.FloorPlanType is null) throw new InvalidOperationException("El proyecto no tiene ningún tipo de vista de planta.");
        var names = new HashSet<string>(new FilteredElementCollector(_doc).OfClass(typeof(View)).Cast<View>().Where(v => v.ViewType is ViewType.FloorPlan or ViewType.CeilingPlan).Select(v => v.Name), StringComparer.OrdinalIgnoreCase);
        int order = 0;
        foreach (var pv in plan.Views)
        {
            Sub("Vista «" + pv.Name + "»");
            bool ceiling = pv.Discipline == PlanosDiscipline.Iluminacion && _inv.CeilingPlanType is not null;
            var level = _inv.Levels[pv.Level];
            var view = ViewPlan.Create(_doc, (ceiling ? _inv.CeilingPlanType! : _inv.FloorPlanType).Id, level.Id);
            view.ViewTemplateId = ElementId.InvalidElementId;
            if (_choices.Template is { } template)
            {
                try { view.ApplyViewTemplateParameters(template); }
                catch (Exception ex) { Note($"La plantilla «{template.Name}» no se pudo copiar a «{pv.Name}» ({ex.Message}); se usan los ajustes del módulo."); }
                view.ViewTemplateId = ElementId.InvalidElementId;
            }
            view.Name = PlanosNaming.UniqueName(pv.Name, names);
            names.Add(view.Name);
            Presentation(view, pv.Discipline);
            view.Scale = pv.Scale;
            Range(view, pv.Level, ceiling);
            Crop(view, pv.Crop);
            Visibility(view, pv);
            string key = (order++).ToString("0000") + "|" + pv.SheetName;
            PlanosMark.Set(view, pv.Discipline.ToString(), PlanosMark.Plan, key);
            if (!_plans.TryGetValue(pv.Discipline, out var list)) _plans[pv.Discipline] = list = new List<(View, PlannedView?, string)>();
            list.Add((view, pv, key));
            _result.ViewIds.Add(view.Id);
            _result.Views++;
            _result.FirstView ??= view;
        }
        if (O.Disciplines.Contains(PlanosDiscipline.Iluminacion) && _inv.CeilingPlanType is null)
            Note("El proyecto no tiene tipo de vista de techo reflejado: la iluminación se documenta en planta.");
    }

    private void Presentation(View view, PlanosDiscipline discipline)
    {
        Try("disciplina de la vista", () => view.Discipline = discipline switch
        {
            PlanosDiscipline.Fontaneria => ViewDiscipline.Plumbing,
            PlanosDiscipline.Climatizacion => ViewDiscipline.Mechanical,
            PlanosDiscipline.Genericas => ViewDiscipline.Coordination,
            _ => ViewDiscipline.Electrical
        });
        Try("nivel de detalle", () => view.DetailLevel = O.Detail switch
        {
            PlanosDetail.Bajo => ViewDetailLevel.Coarse,
            PlanosDetail.Alto => ViewDetailLevel.Fine,
            _ => ViewDetailLevel.Medium
        });
        Try("estilo visual", () => view.DisplayStyle = O.Style switch
        {
            PlanosStyle.Sombreado => DisplayStyle.Shading,
            PlanosStyle.ColoresCoherentes => DisplayStyle.FlatColors,
            PlanosStyle.Realista => DisplayStyle.Realistic,
            PlanosStyle.Alambrico => DisplayStyle.Wireframe,
            _ => DisplayStyle.HLR
        });
    }

    private void Views3D()
    {
        var type = new FilteredElementCollector(_doc).OfClass(typeof(ViewFamilyType)).Cast<ViewFamilyType>()
            .FirstOrDefault(t => t.ViewFamily == ViewFamily.ThreeDimensional);
        if (type is null) { Note("El proyecto no tiene ningún tipo de vista 3D: no se generan vistas 3D."); return; }
        var names = new HashSet<string>(new FilteredElementCollector(_doc).OfClass(typeof(View3D)).Cast<View3D>()
            .Select(v => v.Name), StringComparer.OrdinalIgnoreCase);
        foreach (var d in O.Disciplines.Distinct())
        {
            if (!_plans.TryGetValue(d, out var list) || !list.Any(p => p.Plan is not null)) continue;
            string name = "3D - " + PlanosNaming.DisciplineLabel(d);
            Sub("Vista «" + name + "»");
            var view = View3D.CreateIsometric(_doc, type.Id);
            view.ViewTemplateId = ElementId.InvalidElementId;
            if (_choices.Template is { IsTemplate: true, ViewType: ViewType.ThreeD } template)
            {
                try { view.ApplyViewTemplateParameters(template); }
                catch (Exception ex) { Note($"La plantilla «{template.Name}» no se pudo copiar a «{name}» ({ex.Message}); se usan los ajustes del módulo."); }
                view.ViewTemplateId = ElementId.InvalidElementId;
            }
            view.Name = PlanosNaming.UniqueName(name, names);
            names.Add(view.Name);
            Presentation(view, d);
            view.Scale = list.Where(p => p.Plan is not null).Select(p => p.Plan!.Scale).DefaultIfEmpty(100).Max();
            Visibility(view, d, PlanosNetwork.Todo, null);
            // Inventory bounds are XY only: there is no reliable Z extent for a section box.
            string key = "9999|" + name;
            PlanosMark.Set(view, d.ToString(), PlanosMark.Plan, key);
            list.Add((view, null, key));
            _result.ViewIds.Add(view.Id);
            _result.Views++;
            _result.FirstView ??= view;
        }
    }

    /// <summary>
    /// Floor plans: from the level down to 1 m below it (drainage under the slab, drawn as "beyond") and up to the next
    /// level, cutting just under it so pipes and ducts in the ceiling void are drawn. Ceiling plans: cut at 1 m, looking up.
    /// </summary>
    private void Range(ViewPlan view, int levelIndex, bool ceiling)
    {
        var level = _inv.Levels[levelIndex];
        Level? next = levelIndex + 1 < _inv.Levels.Count ? _inv.Levels[levelIndex + 1] : null;
        double storey = next is not null ? next.Elevation - level.Elevation : 4 / 0.3048;
        if (storey < 1.5 / 0.3048) { Note($"La altura entre «{level.Name}» y el nivel superior es muy pequeña: se deja el rango de vista de Revit."); return; }
        Try("rango de vista", () =>
        {
            var r = view.GetViewRange();
            r.SetLevelId(PlanViewPlane.TopClipPlane, next?.Id ?? level.Id);
            r.SetOffset(PlanViewPlane.TopClipPlane, next is null ? storey : 0);
            r.SetLevelId(PlanViewPlane.CutPlane, level.Id);
            if (ceiling) r.SetOffset(PlanViewPlane.CutPlane, 1.0 / 0.3048);
            else
            {
                r.SetOffset(PlanViewPlane.CutPlane, storey - 0.05 / 0.3048);
                r.SetLevelId(PlanViewPlane.BottomClipPlane, level.Id);
                r.SetOffset(PlanViewPlane.BottomClipPlane, 0);
                r.SetLevelId(PlanViewPlane.ViewDepthPlane, level.Id);
                r.SetOffset(PlanViewPlane.ViewDepthPlane, -1.0 / 0.3048);
            }
            view.SetViewRange(r);
        });
    }

    private void Crop(View view, PlanosRect crop)
    {
        Try("recorte", () =>
        {
            var box = view.CropBox;
            var inverse = box.Transform.Inverse;
            var p = inverse.OfPoint(new XYZ(crop.MinX, crop.MinY, 0));
            var q = inverse.OfPoint(new XYZ(crop.MaxX, crop.MaxY, 0));
            box.Min = new XYZ(Math.Min(p.X, q.X), Math.Min(p.Y, q.Y), box.Min.Z);
            box.Max = new XYZ(Math.Max(p.X, q.X), Math.Max(p.Y, q.Y), box.Max.Z);
            view.CropBox = box;
            view.CropBoxActive = true;
            view.CropBoxVisible = false;
        });
        Try("recorte de anotaciones", () => view.get_Parameter(BuiltInParameter.VIEWER_ANNOTATION_CROP_ACTIVE)?.Set(1));
    }

    private void Visibility(View view, PlannedView pv) => Visibility(view, pv.Discipline, pv.Network, pv);

    private void Visibility(View view, PlanosDiscipline discipline, PlanosNetwork network, PlannedView? pv)
    {
        // Other disciplines' installations are hidden; the building stays (half-tone) only in plans when requested.
        foreach (var id in Categories(PlanosClassifier.HiddenIn(discipline)))
            Try(null, () => { if (view.CanCategoryBeHidden(id)) view.SetCategoryHidden(id, true); });
        var context = Categories(pv is null
            ? PlanosClassifier.ContextCategories.Concat(new[] { "OST_Levels", "OST_Grids" })
            : PlanosClassifier.ContextCategories);
        foreach (var id in context)
            Try(null, () =>
            {
                if (pv is null || !O.Context) { if (view.CanCategoryBeHidden(id)) view.SetCategoryHidden(id, true); }
                else view.SetCategoryOverrides(id, new OverrideGraphicSettings().SetHalftone(true));
            });

        // Shared categories (equipment, pipes and fittings without system…): elements of another discipline or network
        // are hidden one by one in this view only. Plans check only elements whose box reaches the crop; 3D checks the whole model.
        var hide = new List<ElementId>();
        foreach (var e in HiddenCandidates(discipline, network, pv))
        {
            if (ModelElement(e.Id) is { } element && element.CanBeHidden(view)) hide.Add(e.Id);
        }
        if (hide.Count > 0) Try("ocultación de elementos de otras disciplinas", () => view.HideElements(hide));

        // Pipes and ducts: one filter per system type; shown and coloured only if the system belongs to this plan.
        foreach (var (typeId, filterId) in _filterOfType)
        {
            var st = _inv.SystemTypes[typeId];
            var owner = PlanosClassifier.OfSystem(st.Classification);
            bool visible = owner is { } o && o.Discipline == discipline &&
                           (discipline != PlanosDiscipline.Fontaneria || network == PlanosNetwork.Todo || o.Network == network);
            Try("filtro «" + st.Name + "»", () =>
            {
                view.AddFilter(filterId);
                view.SetFilterVisibility(filterId, visible);
                if (visible) view.SetFilterOverrides(filterId, Paint(PlanosPalette.Legend(new[] { st.Classification })[st.Classification]));
            });
        }

        // Electrical and lighting: colour by category. Generic families: colour by family (view overrides only).
        if (discipline is PlanosDiscipline.Electricidad or PlanosDiscipline.Iluminacion)
            foreach (string cat in PlanosClassifier.CategoriesOf(discipline))
            {
                var ids = Categories(new[] { cat });
                if (ids.Count == 0) continue;
                var colour = PlanosPalette.Legend(new[] { cat })[cat];
                Try(null, () => view.SetCategoryOverrides(ids[0], Paint(colour)));
            }
        if (discipline == PlanosDiscipline.Genericas)
        {
            var mine = (pv is null ? _inv.Of(PlanosDiscipline.Genericas) : OnLevel(PlanosDiscipline.Genericas, pv.Level))
                .Where(e => e.Category != "OST_PipeCurves" && !PlanosClassifier.PipeCategories.Contains(e.Category));
            var legend = _genericLegend ??= PlanosPalette.Legend(_inv.Of(PlanosDiscipline.Genericas).Select(e => e.Class.ColorKey));
            foreach (var e in mine) Try(null, () => view.SetElementOverrides(e.Id, Paint(legend[e.Class.ColorKey])));
        }
    }

    // ───────────── 4 · Schedules and colour legend ─────────────

    private sealed class ScheduleSpec
    {
        internal ScheduleSpec(string title, string category, bool itemized, params string[] fields) { Title = title; Category = category; Itemized = itemized; Fields = fields; }
        internal string Title { get; }
        internal string Category { get; }
        internal bool Itemized { get; }
        /// <summary>Only pipe schedules use this; Todo means all systems of the discipline.</summary>
        internal PlanosNetwork Network { get; set; } = PlanosNetwork.Todo;
        /// <summary>BuiltInParameter names; "COUNT" = count; "LENGTH:..." = length with totals.</summary>
        internal string[] Fields { get; }
    }

    private static IEnumerable<ScheduleSpec> SchedulesOf(PlanosDiscipline d) => d switch
    {
        PlanosDiscipline.Fontaneria => new[]
        {
            new ScheduleSpec("Agua fría y ACS - Tuberías por sistema y diámetro", "OST_PipeCurves", false, "RBS_PIPING_SYSTEM_TYPE_PARAM", "RBS_PIPE_DIAMETER_PARAM", "COUNT", "LENGTH:CURVE_ELEM_LENGTH") { Network = PlanosNetwork.AguaFriaAcs },
            new ScheduleSpec("Saneamiento y evacuación - Tuberías por sistema y diámetro", "OST_PipeCurves", false, "RBS_PIPING_SYSTEM_TYPE_PARAM", "RBS_PIPE_DIAMETER_PARAM", "COUNT", "LENGTH:CURVE_ELEM_LENGTH") { Network = PlanosNetwork.Saneamiento },
            new ScheduleSpec("Otras redes de fontanería - Tuberías por sistema y diámetro", "OST_PipeCurves", false, "RBS_PIPING_SYSTEM_TYPE_PARAM", "RBS_PIPE_DIAMETER_PARAM", "COUNT", "LENGTH:CURVE_ELEM_LENGTH") { Network = PlanosNetwork.OtrasFontaneria },
            new ScheduleSpec("Aparatos sanitarios", "OST_PlumbingFixtures", false, "ELEM_FAMILY_AND_TYPE_PARAM", "FAMILY_LEVEL_PARAM", "COUNT"),
        },
        PlanosDiscipline.Climatizacion => new[]
        {
            new ScheduleSpec("Tuberías de agua de climatización por sistema y diámetro", "OST_PipeCurves", false, "RBS_PIPING_SYSTEM_TYPE_PARAM", "RBS_PIPE_DIAMETER_PARAM", "COUNT", "LENGTH:CURVE_ELEM_LENGTH"),
            new ScheduleSpec("Conductos por sistema y tamaño", "OST_DuctCurves", false, "RBS_DUCT_SYSTEM_TYPE_PARAM", "RBS_CALCULATED_SIZE", "COUNT", "LENGTH:CURVE_ELEM_LENGTH"),
            new ScheduleSpec("Equipos de climatización", "OST_MechanicalEquipment", true, "ALL_MODEL_MARK", "ELEM_FAMILY_AND_TYPE_PARAM", "FAMILY_LEVEL_PARAM"),
            new ScheduleSpec("Rejillas y difusores", "OST_DuctTerminal", false, "ELEM_FAMILY_AND_TYPE_PARAM", "COUNT"),
        },
        PlanosDiscipline.Electricidad => new[]
        {
            new ScheduleSpec("Cuadros y equipos eléctricos", "OST_ElectricalEquipment", true, "RBS_ELEC_PANEL_NAME", "ELEM_FAMILY_AND_TYPE_PARAM", "FAMILY_LEVEL_PARAM"),
            new ScheduleSpec("Circuitos", "OST_ElectricalCircuit", true, "RBS_ELEC_CIRCUIT_PANEL_PARAM", "RBS_ELEC_CIRCUIT_NUMBER", "RBS_ELEC_CIRCUIT_NAME", "RBS_ELEC_APPARENT_LOAD"),
            new ScheduleSpec("Bandejas por tipo y tamaño", "OST_CableTray", false, "ELEM_FAMILY_AND_TYPE_PARAM", "RBS_CABLETRAY_WIDTH_PARAM", "RBS_CABLETRAY_HEIGHT_PARAM", "COUNT", "LENGTH:CURVE_ELEM_LENGTH"),
        },
        PlanosDiscipline.Iluminacion => new[]
        {
            new ScheduleSpec("Tipos de luminaria", "OST_LightingFixtures", false, "ELEM_FAMILY_AND_TYPE_PARAM", "FAMILY_LEVEL_PARAM", "COUNT"),
        },
        _ => new[]
        {
            new ScheduleSpec("Familias genéricas y tipos", "OST_GenericModel", false, "ELEM_FAMILY_PARAM", "ELEM_TYPE_PARAM", "FAMILY_LEVEL_PARAM", "COUNT"),
            new ScheduleSpec("PCI - Tuberías", "OST_PipeCurves", false, "RBS_PIPING_SYSTEM_TYPE_PARAM", "RBS_PIPE_DIAMETER_PARAM", "COUNT", "LENGTH:CURVE_ELEM_LENGTH"),
            new ScheduleSpec("PCI - Rociadores", "OST_Sprinklers", false, "ELEM_FAMILY_AND_TYPE_PARAM", "FAMILY_LEVEL_PARAM", "COUNT"),
            new ScheduleSpec("PCI - Extintores, BIE y equipos", "OST_FireProtection", false, "ELEM_FAMILY_AND_TYPE_PARAM", "FAMILY_LEVEL_PARAM", "COUNT"),
            new ScheduleSpec("PCI - Detección y alarma", "OST_FireAlarmDevices", false, "ELEM_FAMILY_AND_TYPE_PARAM", "FAMILY_LEVEL_PARAM", "COUNT"),
        }
    };

    private void Extras()
    {
        var viewNames = new HashSet<string>(new FilteredElementCollector(_doc).OfClass(typeof(View)).Cast<View>().Select(v => v.Name), StringComparer.OrdinalIgnoreCase);
        foreach (var d in O.Disciplines)
        {
            if (!_plans.ContainsKey(d)) continue; // no plans, no legend sheet
            var list = _extras[d] = new List<View>();
            if (Legend(d, viewNames) is { } legend) list.Add(legend);
            ViewSchedule? allPipes = null; // Share the honest fallback when several networks exceed the filter limit.
            foreach (var spec in SchedulesOf(d))
            {
                bool pipes = spec.Category == "OST_PipeCurves";
                var allowedTypes = new HashSet<ElementId>();
                if (pipes)
                    foreach (var st in _inv.SystemTypes.Values.Where(t => t.IsPipe))
                        if (PlanosClassifier.OfSystem(st.Classification) is { } owner && owner.Discipline == d &&
                            (spec.Network == PlanosNetwork.Todo || owner.Network == spec.Network))
                            allowedTypes.Add(st.Id);
                bool hasData = spec.Category == "OST_ElectricalCircuit" ? _inv.ElectricalCircuits > 0
                    : _inv.Of(d).Any(e => e.Category == spec.Category && (!pipes || allowedTypes.Contains(e.SystemTypeId)));
                if (!hasData || !Enum.TryParse(spec.Category, out BuiltInCategory bic)) continue;
                Sub("Cuadro «" + spec.Title + "»");
                var catId = new ElementId(bic);
                if (!ViewSchedule.IsValidCategoryForSchedule(catId)) continue;
                var schedule = ViewSchedule.CreateSchedule(_doc, catId);
                schedule.Name = PlanosNaming.UniqueName(PlanosNaming.DisciplineLabel(d) + " - " + spec.Title, viewNames);
                viewNames.Add(schedule.Name);
                var def = schedule.Definition;
                var available = def.GetSchedulableFields();
                var missing = new List<string>();
                ScheduleField? systemField = null;
                foreach (string f in spec.Fields)
                {
                    if (f == "COUNT") { def.AddField(ScheduleFieldType.Count); continue; }
                    bool length = f.StartsWith("LENGTH:", StringComparison.Ordinal);
                    string bipName = length ? f.Substring(7) : f;
                    if (!Enum.TryParse(bipName, out BuiltInParameter bip)) { missing.Add(bipName); continue; }
                    var pid = new ElementId(bip);
                    var sf = available.FirstOrDefault(a => a.ParameterId == pid);
                    if (sf is null) { missing.Add(bipName); continue; }
                    var field = def.AddField(sf);
                    if (bipName == "RBS_PIPING_SYSTEM_TYPE_PARAM") systemField = field;
                    if (length) Try(null, () => field.DisplayType = ScheduleFieldDisplayType.Totals);
                    else if (!spec.Itemized) def.AddSortGroupField(new ScheduleSortGroupField(field.FieldId));
                    else if (def.GetSortGroupFieldCount() == 0) def.AddSortGroupField(new ScheduleSortGroupField(field.FieldId));
                }
                if (pipes)
                {
                    // All filters are ANDed. Exclude every foreign type, including unused types: schedules are project-wide.
                    // Also exclude an unset type; otherwise a complement is not membership in the allowed set.
                    var excludedTypes = _inv.SystemTypes.Values.Where(t => t.IsPipe && !allowedTypes.Contains(t.Id)).Select(t => t.Id)
                        .Concat(new[] { ElementId.InvalidElementId }).Distinct().ToList();
                    int initialFilters = def.GetFilterCount();
                    int needed = allowedTypes.Count == 1 ? 1 : excludedTypes.Count;
                    string? reason = null;
                    if (systemField is null || !def.CanFilterByValue(systemField.FieldId))
                        reason = "Revit no ofrece un campo de tipo de sistema filtrable";
                    else if (initialFilters + needed > 8) // Revit 2023 schedule limit; also used for later versions.
                        reason = $"se necesitan {needed} filtros y quedan {8 - initialFilters} de los 8 admitidos";
                    else
                    {
                        try
                        {
                            if (allowedTypes.Count == 1)
                                def.AddFilter(new ScheduleFilter(systemField.FieldId, ScheduleFilterType.Equal, allowedTypes.Single()));
                            else
                                foreach (var id in excludedTypes)
                                    def.AddFilter(new ScheduleFilter(systemField.FieldId, ScheduleFilterType.NotEqual, id));
                        }
                        catch (Exception ex)
                        {
                            // Never leave a partly filtered schedule with a network-specific title.
                            while (def.GetFilterCount() > initialFilters) def.RemoveFilter(def.GetFilterCount() - 1);
                            reason = "Revit rechazó los filtros de tipo de sistema (" + ex.Message + ")";
                        }
                    }
                    if (reason is not null)
                    {
                        if (allPipes is not null)
                        {
                            Note($"Cuadro «{spec.Title}»: {reason}; consulte «{allPipes.Name}», que incluye todas las redes del proyecto. No bloquea la generación; revise sus filtros en Propiedades > Filtro.");
                            viewNames.Remove(schedule.Name);
                            _doc.Delete(schedule.Id);
                            continue;
                        }
                        viewNames.Remove(schedule.Name);
                        schedule.Name = PlanosNaming.UniqueName(PlanosNaming.DisciplineLabel(d) + " - Tuberías por sistema y diámetro (todas las redes del proyecto)", viewNames);
                        viewNames.Add(schedule.Name);
                        allPipes = schedule;
                        Note($"Cuadro «{spec.Title}»: {reason}; se crea «{schedule.Name}» sin filtrar. Incluye tuberías de todas las disciplinas y redes. No bloquea la generación; revise sus filtros en Propiedades > Filtro antes de utilizar sus cantidades.");
                    }
                }
                def.IsItemized = spec.Itemized;
                Try(null, () => { def.ShowGrandTotal = true; def.ShowGrandTotalCount = true; });
                if (missing.Count > 0) Note($"Cuadro «{schedule.Name}»: Revit no ofrece los campos {string.Join(", ", missing)} para esta categoría; se omiten.");
                PlanosMark.Set(schedule, d.ToString(), PlanosMark.Schedule, spec.Category);
                list.Add(schedule);
                _result.Schedules++;
            }
        }
    }

    /// <summary>Drafting view with a colour swatch and the name of each system/category/family shown in colour in the plans.</summary>
    private View? Legend(PlanosDiscipline d, HashSet<string> viewNames)
    {
        if (d == PlanosDiscipline.Iluminacion) return null; // luminaire types: «Tipos de luminaria» schedule
        Sub("Leyenda de " + PlanosNaming.DisciplineLabel(d));
        var rows = new List<(string Label, (byte R, byte G, byte B, string Label) Colour)>();
        var mine = _inv.Of(d).ToList();
        if (d is PlanosDiscipline.Fontaneria or PlanosDiscipline.Climatizacion or PlanosDiscipline.Genericas)
            foreach (var st in mine.Where(e => e.SystemTypeId != ElementId.InvalidElementId && _filterOfType.ContainsKey(e.SystemTypeId))
                         .Select(e => _inv.SystemTypes[e.SystemTypeId]).Distinct().OrderBy(t => t.Classification).ThenBy(t => t.Name))
            {
                var c = PlanosPalette.Legend(new[] { st.Classification })[st.Classification];
                rows.Add((st.Name + " - " + c.Label, c));
            }
        if (d is PlanosDiscipline.Electricidad)
            foreach (var g in PlanosPalette.Legend(mine.Select(e => e.Category)).Values.GroupBy(v => v.Label)) rows.Add((g.Key, g.First()));
        if (d is PlanosDiscipline.Genericas)
        {
            var legend = PlanosPalette.Legend(mine.Select(e => e.Class.ColorKey));
            foreach (var kv in legend.Where(kv => !kv.Key.StartsWith("PCI · Tuberías", StringComparison.Ordinal)).Take(40)) rows.Add((kv.Value.Label, kv.Value));
            if (legend.Count > 40) Note("Hay más de 40 familias genéricas: la leyenda muestra las 40 primeras; el cuadro las recoge todas.");
        }
        if (rows.Count == 0) return null;
        if (_inv.DraftingType is null) { Note("El proyecto no tiene tipo de vista de diseño: no se crea la leyenda de colores."); return null; }
        var regionType = new FilteredElementCollector(_doc).OfClass(typeof(FilledRegionType)).FirstElementId();
        var textType = new FilteredElementCollector(_doc).OfClass(typeof(TextNoteType)).FirstElementId();
        if (textType == ElementId.InvalidElementId) { Note("El proyecto no tiene tipos de texto: no se crea la leyenda de colores."); return null; }

        var view = ViewDrafting.Create(_doc, _inv.DraftingType.Id);
        view.Name = PlanosNaming.UniqueName(PlanosNaming.LegendViewName(d), viewNames);
        viewNames.Add(view.Name);
        view.Scale = 1; // 1:1: positions are paper millimetres
        TextNote.Create(_doc, view.Id, new XYZ(0, 8 * Mm, 0), "LEYENDA DE COLORES - " + PlanosNaming.DisciplineLabel(d).ToUpperInvariant(), textType);
        for (int i = 0; i < rows.Count; i++)
        {
            double y = -i * 8 * Mm;
            if (regionType != ElementId.InvalidElementId)
            {
                var loop = CurveLoop.Create(new List<Curve>
                {
                    Line.CreateBound(new XYZ(0, y, 0), new XYZ(15 * Mm, y, 0)), Line.CreateBound(new XYZ(15 * Mm, y, 0), new XYZ(15 * Mm, y + 5 * Mm, 0)),
                    Line.CreateBound(new XYZ(15 * Mm, y + 5 * Mm, 0), new XYZ(0, y + 5 * Mm, 0)), Line.CreateBound(new XYZ(0, y + 5 * Mm, 0), new XYZ(0, y, 0))
                });
                var region = FilledRegion.Create(_doc, regionType, view.Id, new List<CurveLoop> { loop });
                var colour = rows[i].Colour;
                Try(null, () => view.SetElementOverrides(region.Id, Paint(colour)));
            }
            TextNote.Create(_doc, view.Id, new XYZ(20 * Mm, y + 4 * Mm, 0), rows[i].Label, textType);
        }
        if (regionType == ElementId.InvalidElementId) Note("El proyecto no tiene tipos de región rellena: la leyenda muestra solo los nombres.");
        PlanosMark.Set(view, d.ToString(), PlanosMark.Legend, "colores");
        _result.Legends++;
        return view;
    }

    // ───────────── 5 · Tags (project tag families only) ─────────────

    private void Tags()
    {
        var missing = new HashSet<string>();
        foreach (var (d, list) in _plans)
            foreach (var (view, pv, _) in list)
            {
                if (pv is null) continue;
                Sub("Etiquetas de «" + view.Name + "»");
                var crop = pv.Crop;
                var candidates = new List<PlanosTagPicker.Candidate>();
                var elements = new List<(PlanosElement E, FamilySymbol Tag)>();
                foreach (var e in OnLevel(d, pv.Level))
                {
                    if (e.Level != pv.Level || e.Point is null || !PlanosInventory.TagCategoryOf.TryGetValue(e.Category, out string? tagCat)) continue;
                    if (d == PlanosDiscipline.Fontaneria && pv.Network != PlanosNetwork.Todo && e.Class.Network != PlanosNetwork.Todo && e.Class.Network != pv.Network) continue;
                    if (d == PlanosDiscipline.Genericas && e.Category == "OST_PipeCurves") continue; // fire pipes: legend + filter colour
                    if (e.Point.X < crop.MinX || e.Point.X > crop.MaxX || e.Point.Y < crop.MinY || e.Point.Y > crop.MaxY) continue;
                    if (!_inv.TagTypes.TryGetValue(tagCat, out var symbol)) { missing.Add(tagCat); continue; }
                    bool run = e.Length > 0;
                    if (run && PlanosScale.PaperMm(e.Length, pv.Scale) < 25) continue; // short pieces: the run is tagged elsewhere
                    candidates.Add(new PlanosTagPicker.Candidate(elements.Count, e.Point.X, e.Point.Y, run ? e.Length : 1e6));
                    elements.Add((e, symbol));
                }
                double spacing = 25 * pv.Scale * Mm;
                foreach (int i in PlanosTagPicker.Pick(candidates, spacing, 300))
                {
                    var (e, symbol) = elements[i];
                    if (!symbol.IsActive) symbol.Activate();
                    try
                    {
                        var element = ModelElement(e.Id);
                        IndependentTag.Create(_doc, symbol.Id, view.Id, new Reference(element), false, TagOrientation.Horizontal, e.Point!);
                        _result.Tags++;
                    }
                    catch { /* an element that cannot be tagged in this view is skipped */ }
                }
            }
        foreach (string m in missing.OrderBy(x => x))
            Note($"No hay ninguna familia de {PlanosInventory.TagLabel(m)} cargada: esos elementos no se etiquetan. Cárguela (Insertar > Cargar familia > Anotaciones) y vuelva a generar.");
    }

    // ───────────── 6 · Reuse the views of a previous run (mode «solo láminas») ─────────────

    private void Reuse()
    {
        foreach (var d in O.Disciplines)
        {
            var plans = _inv.OwnedOf(d, PlanosMark.Plan).OrderBy(o => o.Key, StringComparer.Ordinal)
                .Select(o => (_doc.GetElement(o.Id) as View, o.Key)).Where(x => x.Item1 is not null).Select(x => (x.Item1!, (PlannedView?)null, x.Key)).ToList();
            if (plans.Count == 0) { Note($"{PlanosNaming.DisciplineLabel(d)}: no hay vistas generadas antes; use «Vistas y láminas»."); continue; }
            _plans[d] = plans;
            var extras = _inv.OwnedOf(d, PlanosMark.Legend).Concat(_inv.OwnedOf(d, PlanosMark.Schedule)).Select(o => _doc.GetElement(o.Id) as View).Where(v => v is not null).Select(v => v!).ToList();
            if (O.Schedules && extras.Count > 0) _extras[d] = extras;
        }
    }

    // ───────────── 7 · Sheets: one plan per sheet, centred in the drawing area; legends and schedules packed ─────────────

    private void Sheets()
    {
        var used = new HashSet<string>(new FilteredElementCollector(_doc).OfClass(typeof(ViewSheet)).Cast<ViewSheet>().Select(s => s.SheetNumber), StringComparer.OrdinalIgnoreCase);
        foreach (var d in O.Disciplines)
        {
            if (!_plans.TryGetValue(d, out var plans) || plans.Count == 0) continue;
            string prefix = PlanosNaming.Prefix(d);
            var numbers = new Queue<string>(O.Numbering.Next(prefix, plans.Count + 20, used));
            foreach (var (view, _, key) in plans)
            {
                string name = key.Contains("|") ? key.Substring(key.IndexOf('|') + 1) : view.Name;
                Sub("Lámina «" + name + "»");
                var sheet = NewSheet(d, numbers, used, name);
                if (!Viewport.CanAddViewToSheet(_doc, sheet.Id, view.Id)) { Note($"La vista «{view.Name}» no se puede colocar en una lámina (¿ya está en otra?)."); continue; }
                var area = Area(sheet);
                var vp = Viewport.Create(_doc, sheet.Id, view.Id, Center(area));
                _doc.Regenerate();
                var box = Box(vp.GetBoxOutline());
                // Centred, leaving room for the view title under it.
                vp.SetBoxCenter(new XYZ((area.MinX + area.MaxX) / 2, (area.MinY + area.MaxY) / 2 + PlanosScale.TitleMm * Mm / 2, 0));
                _doc.Regenerate();
                box = Box(vp.GetBoxOutline());
                if (!area.Contains(box, 1 * Mm))
                    Note($"Lámina {sheet.SheetNumber}: la vista mide {box.Width / Mm:0} × {box.Height / Mm:0} mm y la zona de dibujo {area.Width / Mm:0} × {area.Height / Mm:0} mm; revise la escala o el formato.");
            }
            if (_extras.TryGetValue(d, out var extras) && extras.Count > 0) LegendSheets(d, extras, numbers, used);
        }
    }

    private ViewSheet NewSheet(PlanosDiscipline d, Queue<string> numbers, HashSet<string> used, string name)
    {
        var sheet = ViewSheet.Create(_doc, _choices.TitleBlockId);
        string number = numbers.Count > 0 ? numbers.Dequeue() : O.Numbering.Next(PlanosNaming.Prefix(d), 1, used)[0];
        sheet.SheetNumber = number;
        used.Add(number);
        sheet.Name = name;
        PlanosMark.Set(sheet, d.ToString(), PlanosMark.Sheet, number);
        _result.SheetIds.Add(sheet.Id);
        _result.Sheets++;
        _result.FirstSheet ??= sheet;
        return sheet;
    }

    /// <summary>Drawing area of a sheet (feet, sheet coordinates): the title block outline minus margin and the reserved bands.</summary>
    private PlanosRect Area(ViewSheet sheet)
    {
        _doc.Regenerate();
        PlanosRect paper = new PlanosRect(0, 0, O.PaperWMm * Mm, O.PaperHMm * Mm);
        var tb = new FilteredElementCollector(_doc, sheet.Id).OfCategory(BuiltInCategory.OST_TitleBlocks).WhereElementIsNotElementType().FirstElement();
        if (tb?.get_BoundingBox(sheet) is { } bb) paper = new PlanosRect(bb.Min.X, bb.Min.Y, bb.Max.X, bb.Max.Y);
        return new PlanosRect(paper.MinX + O.MarginMm * Mm, paper.MinY + (O.MarginMm + O.ReserveBottomMm) * Mm,
                              paper.MaxX - (O.MarginMm + O.ReserveRightMm) * Mm, paper.MaxY - O.MarginMm * Mm);
    }

    private static XYZ Center(PlanosRect r) => new XYZ((r.MinX + r.MaxX) / 2, (r.MinY + r.MaxY) / 2, 0);
    private static PlanosRect Box(Outline o) => new PlanosRect(o.MinimumPoint.X, o.MinimumPoint.Y, o.MaximumPoint.X, o.MaximumPoint.Y);

    private void LegendSheets(PlanosDiscipline d, List<View> extras, Queue<string> numbers, HashSet<string> used)
    {
        string name = PlanosNaming.LegendSheetName(d);
        Sub("Lámina «" + name + "»");
        var sheets = new List<ViewSheet> { NewSheet(d, numbers, used, name) };
        var area = Area(sheets[0]);
        // 1) place everything on the first sheet to measure its real size
        var placed = new List<(View View, Element Item)>();
        foreach (var v in extras)
        {
            if (v is ViewSchedule s) placed.Add((v, ScheduleSheetInstance.Create(_doc, sheets[0].Id, s.Id, Center(area))));
            else if (Viewport.CanAddViewToSheet(_doc, sheets[0].Id, v.Id)) placed.Add((v, Viewport.Create(_doc, sheets[0].Id, v.Id, Center(area))));
        }
        _doc.Regenerate();
        var sizes = placed.Select(p => Measure(p.Item, sheets[0])).ToList();
        // 2) pack with the measured sizes (mm), 8 mm apart; what does not fit goes to further sheets
        var layout = PlanosPacker.Pack(sizes.Select(s => (s.Width / Mm, s.Height / Mm)).ToList(), area.Width / Mm, area.Height / Mm, 8);
        for (int i = 0; i < placed.Count; i++)
        {
            var at = layout[i];
            while (sheets.Count <= at.Page) sheets.Add(NewSheet(d, numbers, used, name + " (" + (sheets.Count + 1) + ")"));
            var item = placed[i].Item;
            if (at.Page > 0)
            {
                _doc.Delete(item.Id);
                item = placed[i].View is ViewSchedule s
                    ? ScheduleSheetInstance.Create(_doc, sheets[at.Page].Id, s.Id, Center(area))
                    : Viewport.Create(_doc, sheets[at.Page].Id, placed[i].View.Id, Center(area));
                _doc.Regenerate();
            }
            var sheet = sheets[at.Page];
            var size = Measure(item, sheet);
            double left = area.MinX + at.X * Mm, top = area.MaxY - at.Y * Mm;
            if (item is Viewport vp) vp.SetBoxCenter(new XYZ(left + size.Width / 2, top - size.Height / 2, 0));
            else if (item is ScheduleSheetInstance si) si.Point = si.Point + new XYZ(left - size.MinX, top - size.MaxY, 0);
            if (at.Oversize) Note($"«{placed[i].View.Name}» no cabe en la zona de dibujo de una lámina: va sola en la lámina {sheet.SheetNumber} y sobresale; revíselo.");
        }
        _doc.Regenerate();
        // 3) check: nothing overlaps on any sheet
        foreach (var sheet in sheets)
        {
            var onSheet = new FilteredElementCollector(_doc, sheet.Id).WhereElementIsNotElementType().ToElements()
                .Where(e => e is Viewport || e is ScheduleSheetInstance).Select(e => Measure(e, sheet)).ToList();
            for (int a = 0; a < onSheet.Count; a++)
                for (int b = a + 1; b < onSheet.Count; b++)
                    if (onSheet[a].Grow(-0.5 * Mm).Overlaps(onSheet[b].Grow(-0.5 * Mm)))
                        Note($"Lámina {sheet.SheetNumber}: dos cuadros se solapan tras colocarlos; revíselos.");
        }
    }

    private PlanosRect Measure(Element item, ViewSheet sheet)
    {
        if (item is Viewport vp) return Box(vp.GetBoxOutline());
        var bb = item.get_BoundingBox(sheet);
        return bb is null ? new PlanosRect(0, 0, 100 * Mm, 50 * Mm) : new PlanosRect(bb.Min.X, bb.Min.Y, bb.Max.X, bb.Max.Y);
    }

    // ───────────── helpers ─────────────

    private void Note(string text) { if (!_result.Notes.Contains(text)) _result.Notes.Add(text); }

    private void Try(string? what, Action action)
    {
        try { action(); }
        catch (Exception ex) { if (what is not null) Note($"{_step}: no se pudo aplicar {what} ({ex.Message})."); }
    }

    /// <summary>Warnings (not errors) are accepted silently so a long generation is not interrupted; errors still roll back.</summary>
    private sealed class WarningSwallower : IFailuresPreprocessor
    {
        public FailureProcessingResult PreprocessFailures(FailuresAccessor accessor)
        {
            foreach (var f in accessor.GetFailureMessages())
                if (f.GetSeverity() == FailureSeverity.Warning) accessor.DeleteWarning(f);
            return FailureProcessingResult.Continue;
        }
    }
}
