#nullable enable
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using Autodesk.Revit.Attributes;
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Plumbing;
using Autodesk.Revit.UI;
using TaskDialog = Autodesk.Revit.UI.TaskDialog;
using DialogResult = System.Windows.Forms.DialogResult;
using View = Autodesk.Revit.DB.View;

namespace RevitAutomation.Addin.Commands.Aislamiento;

[Transaction(TransactionMode.Manual)]
[Regeneration(RegenerationOption.Manual)]
public sealed class AislamientoCommand : IExternalCommand
{
    internal const string Title = "Aislamiento RITE";
    private const string RiteNote = "Procedimiento simplificado del RITE (IT 1.2.4.2.1.2), válido hasta 70 kW; por encima, justifique con el procedimiento alternativo.";

    private static readonly BuiltInCategory[] Categories =
    {
        BuiltInCategory.OST_PipeCurves, BuiltInCategory.OST_PipeFitting, BuiltInCategory.OST_PipeAccessory
    };

    public Result Execute(ExternalCommandData data, ref string message, ElementSet elements)
    {
        UIDocument? ui = data.Application.ActiveUIDocument;
        if (ui is null || ui.Document.IsFamilyDocument)
        {
            Show("Abra un proyecto de Revit.", "Este botón trabaja en proyectos (.rvt), no en familias.");
            return Result.Cancelled;
        }
        Document doc = ui.Document;
        if (doc.IsReadOnly)
        {
            Show("El proyecto está abierto en solo lectura.", "Abra una copia editable y vuelva a pulsar «Aislamiento RITE».");
            return Result.Cancelled;
        }
        try { return Run(ui); }
        catch (Autodesk.Revit.Exceptions.OperationCanceledException) { return Result.Cancelled; }
        catch (Exception ex)
        {
            message = ex.ToString();
            Show("No se pudo completar el aislamiento.", ex.Message, ex.ToString());
            return Result.Failed;
        }
    }

    private static Result Run(UIDocument ui)
    {
        Document doc = ui.Document;
        var insulationTypes = new FilteredElementCollector(doc).OfClass(typeof(PipeInsulationType)).Cast<PipeInsulationType>()
            .OrderBy(t => t.Name).ToList();
        if (insulationTypes.Count == 0)
        {
            Show("El proyecto no tiene tipos de aislamiento de tubería.",
                "Cargue al menos uno y repita: pestaña Sistemas > panel Fontanería y tuberías > Aislamiento de tubería (o " +
                "Gestionar > Transferir normas de proyecto > «Tipos de aislamiento de tubería» desde una plantilla de MEP).");
            return Result.Cancelled;
        }
        var levels = new FilteredElementCollector(doc).OfClass(typeof(Level)).Cast<Level>()
            .OrderBy(l => l.Elevation).ThenBy(l => l.Name).ToList();
        var levelNames = levels.ToDictionary(l => l.Id, l => l.Name);

        // Automático: lo único que se pregunta es si las tuberías elegidas van por el interior o el exterior del edificio.
        // 1 · Tuberías: las ya seleccionadas; si no hay, se eligen con el ratón (Esc cancela).
        var selected = ui.Selection.GetElementIds().Select(id => doc.GetElement(id))
            .Where(e => e is not null && PipeFilter.Accepts(e)).ToList();
        if (selected.Count == 0)
        {
            var picked = ui.Selection.PickObjects(Autodesk.Revit.UI.Selection.ObjectType.Element, new PipeFilter(),
                "Aislamiento RITE: seleccione las tuberías a aislar y pulse «Finalizar» (Esc cancela)");
            var ids = picked.Select(r => r.ElementId).Distinct().ToList();
            if (ids.Count > 0) selected = Collect(new FilteredElementCollector(doc, ids));
        }
        else selected = Collect(new FilteredElementCollector(doc, selected.Select(e => e!.Id).Distinct().ToList()));
        if (selected.Count == 0)
        {
            Show("No se ha seleccionado ninguna tubería.", "Seleccione tuberías, codos, tes o accesorios de tubería y vuelva a pulsar «Aislamiento RITE».");
            return Result.Cancelled;
        }

        // 2 · La única pregunta: interior o exterior (tablas 1.2.4.2.1/3 o 1.2.4.2.2/4; frigoríficas 1.2.4.2.5).
        // Valores por defecto del RITE (sin diálogo): de lo guardado solo se recuerdan interior/exterior y las redes asignadas a mano.
        var stored = AislSettingsStore.Load();
        var settings = new AislSettings { Exterior = stored.Exterior };
        foreach (var kv in stored.SystemOverrides) settings.SystemOverrides[kv.Key] = kv.Value;
        foreach (var red in AislSettings.DialogNetworks) settings.Network(red).InsulationType = stored.Network(red).InsulationType;
        var where = new TaskDialog(Title)
        {
            MainInstruction = $"¿Por dónde discurren las {selected.Count} tuberías seleccionadas?",
            MainContent = "Con esto se elige la tabla del RITE. Todo lo demás (red, temperatura, diámetro, espesor y tipo de aislamiento) es automático.",
            CommonButtons = TaskDialogCommonButtons.Cancel,
            DefaultButton = settings.Exterior ? TaskDialogResult.CommandLink2 : TaskDialogResult.CommandLink1
        };
        where.AddCommandLink(TaskDialogCommandLinkId.CommandLink1, "Interior del edificio", "Tablas de fluidos por el INTERIOR (y columna interior de circuitos frigoríficos).");
        where.AddCommandLink(TaskDialogCommandLinkId.CommandLink2, "Exterior del edificio", "Tablas de fluidos por el EXTERIOR (y columna exterior de circuitos frigoríficos): más espesor.");
        var whereAnswer = where.Show();
        if (whereAnswer != TaskDialogResult.CommandLink1 && whereAnswer != TaskDialogResult.CommandLink2) return Result.Cancelled;
        settings.Exterior = whereAnswer == TaskDialogResult.CommandLink2;
        settings.OnlySelection = true;
        settings.ReviewBeforeApplying = false;
        bool saved = AislSettingsStore.Save(settings);

        // Read-only planning phase: resolve each host parameter and system type only once.
        var hostSystems = new Dictionary<ElementId, PipingSystemType?>();
        var systemTypes = new Dictionary<ElementId, PipingSystemType?>();
        PipingSystemType? SystemType(Element e)
        {
            if (hostSystems.TryGetValue(e.Id, out var known)) return known;
            var id = e.get_Parameter(BuiltInParameter.RBS_PIPING_SYSTEM_TYPE_PARAM)?.AsElementId();
            PipingSystemType? type = null;
            if (id != null && id != ElementId.InvalidElementId && !systemTypes.TryGetValue(id, out type))
                systemTypes[id] = type = doc.GetElement(id) as PipingSystemType;
            hostSystems[e.Id] = type;
            return type;
        }
        var systems = new Dictionary<string, AislSystemInfo>(StringComparer.OrdinalIgnoreCase);
        foreach (var e in selected)
            if (SystemType(e) is { } st && !systems.ContainsKey(st.Name)) systems[st.Name] = SystemInfo(doc, st);

        List<Element> scope = selected;

        // Circuitos frigoríficos: si el recorrido supera 25 m, el RITE pide aumentar el espesor (nunca menos de 5 mm).
        var refrigerantRuns = new List<AislDeviation>();
        var refrigerantSystems = new HashSet<string>(systems.Where(kv => AislClassifier.Classify(kv.Value, settings).Red == AislRed.Frigorifica)
            .Select(kv => kv.Key), StringComparer.OrdinalIgnoreCase);
        foreach (var g in selected.OfType<Pipe>().Where(p => SystemType(p) is { } st && refrigerantSystems.Contains(st.Name))
                                  .GroupBy(p => p.MEPSystem?.Name ?? SystemType(p)!.Name))
        {
            double m = g.Sum(p => p.get_Parameter(BuiltInParameter.CURVE_ELEM_LENGTH)?.AsDouble() ?? 0) * 0.3048;
            if (m > 25)
                refrigerantRuns.Add(new(g.Key, "Recorrido frigorífico de " + m.ToString("0.#") + " m (> 25 m)",
                    "La tabla 1.2.4.2.5 vale hasta 25 m de recorrido; por encima el RITE pide aumentar el espesor (nunca menos de 5 mm).",
                    "Aumente el espesor de este circuito (p. ej. al siguiente comercial) o justifíquelo con el procedimiento alternativo."));
        }
        string scopeText = "Tuberías seleccionadas (" + (settings.Exterior ? "exterior" : "interior") + ")";
        if (!settings.IncludeFittingsAndAccessories) scope = scope.Where(e => e is Pipe).ToList();

        // Items.
        var deviations = new List<AislDeviation>();
        if (!saved) deviations.Add(new("", "Preferencias no guardadas", "No se pudo guardar la configuración.", "Compruebe los permisos de la carpeta de configuración."));
        var items = new List<AislItem>();
        foreach (var e in scope)
        {
            AislElementKind kind = e is Pipe ? AislElementKind.Tuberia
                : IsCategory(e, BuiltInCategory.OST_PipeFitting) ? AislElementKind.Pieza : AislElementKind.Accesorio;
            double outer = OuterMm(e, out bool estimated);
            if (outer <= 0)
            {
                deviations.Add(new(e.Id.Value.ToString(), "Sin diámetro", "No se pudo leer el diámetro exterior ni el de sus conectores.",
                    "Conecte el elemento a una tubería o revise sus conectores."));
                continue;
            }
            if (estimated)
                deviations.Add(new(e.Id.Value.ToString(), "Diámetro estimado por conector",
                    $"Sin tubería conectada: 2 × radio del conector = {outer:0.##} mm.", "Conecte la pieza a sus tuberías para usar su diámetro exterior."));
            string levelName = LevelIdOf(e) is { } lid && levelNames.TryGetValue(lid, out var ln) ? ln : "";
            items.Add(new AislItem(e.Id.Value, kind, SystemType(e)?.Name ?? "", levelName, outer, ExistingThicknessMm(doc, e.Id)));
        }
        scopeText += $": {items.Count} elementos";
        if (items.Count == 0)
        {
            Show("No hay elementos que aislar.", scopeText + ". " + (settings.OnlySelection
                ? "La selección no contiene tuberías, piezas ni accesorios de tubería."
                : "La planta elegida no tiene tuberías, piezas ni accesorios de tubería."));
            return Result.Cancelled;
        }

        var decisions = AislPlanner.Decide(items, systems, settings);
        var summary = AislPlanner.Summarize(decisions);
        deviations.AddRange(AislPlanner.Deviations(decisions));
        deviations.AddRange(refrigerantRuns);
        if (settings.ReviewBeforeApplying)
        {
            using var preview = new AislamientoPreviewForm(scopeText, summary, deviations);
            if (preview.ShowDialog() != DialogResult.OK) return Result.Cancelled;
        }

        // Insulation type per network: the one chosen in the dialog, or guessed by its name.
        var typeNames = insulationTypes.Select(t => t.Name).ToList();
        var typeIds = new Dictionary<AislRed, ElementId>();
        foreach (var red in AislSettings.DialogNetworks)
        {
            string wanted = settings.Network(red).InsulationType;
            var type = insulationTypes.FirstOrDefault(t => string.Equals(t.Name, wanted, StringComparison.OrdinalIgnoreCase))
                ?? ByName(insulationTypes, AislamientoForm.GuessType(red, typeNames) ?? "");
            typeIds[red] = type.Id;
        }
        AislBuildResult result;
        result = AislamientoBuilder.Apply(doc, decisions, typeIds);
        deviations.AddRange(result.Deviations);
        // Select the insulation so it can be seen (and checked) right away.
        if (result.InsulationIds.Count > 0)
        {
            try { ui.Selection.SetElementIds(result.InsulationIds.Distinct().ToList()); } catch (Exception) { /* selection is a courtesy */ }
        }
        // Reporting failure must not claim that an already assimilated model was rolled back.
        WriteReport(doc, ui.ActiveView, decisions, deviations, result, scopeText);
        return Result.Succeeded;
    }

    /// <summary>Only pipes, pipe fittings and pipe accessories can be picked.</summary>
    private sealed class PipeFilter : Autodesk.Revit.UI.Selection.ISelectionFilter
    {
        internal static bool Accepts(Element e) =>
            e is not InsulationLiningBase && (e is Pipe || e is FamilyInstance) && e.Category is { } c
            && Categories.Any(b => c.Id.Value == (long)b);
        public bool AllowElement(Element elem) => Accepts(elem);
        public bool AllowReference(Reference reference, XYZ position) => false;
    }

    // Pipes, fittings and accessories (never insulation/lining elements themselves).
    private static List<Element> Collect(FilteredElementCollector collector) =>
        collector.WhereElementIsNotElementType()
            .WherePasses(new ElementMulticategoryFilter(Categories))
            .Where(e => e is not InsulationLiningBase && (e is Pipe || e is FamilyInstance))
            .ToList();

    private static bool IsCategory(Element e, BuiltInCategory category) => e.Category?.Id.Value == (long)category;

    private static ElementId? LevelIdOf(Element e)
    {
        if (e is Pipe pipe) return pipe.ReferenceLevel?.Id;
        if (e.LevelId != ElementId.InvalidElementId) return e.LevelId;
        foreach (var bip in new[] { BuiltInParameter.FAMILY_LEVEL_PARAM, BuiltInParameter.INSTANCE_REFERENCE_LEVEL_PARAM, BuiltInParameter.RBS_START_LEVEL_PARAM })
        {
            var id = e.get_Parameter(bip)?.AsElementId();
            if (id != null && id != ElementId.InvalidElementId) return id;
        }
        return null;
    }

    private static AislSystemInfo SystemInfo(Document doc, PipingSystemType st)
    {
        string fluid = "";
        double? temperature = null;
        try
        {
            if (st.FluidType is { } fid && fid != ElementId.InvalidElementId) fluid = doc.GetElement(fid)?.Name ?? "";
        }
        catch (Exception) { /* Fluid is informative only. */ }
        try
        {
            double kelvin = st.FluidTemperature;
            if (kelvin > 0 && !double.IsNaN(kelvin) && !double.IsInfinity(kelvin))
                temperature = Math.Round(UnitUtils.ConvertFromInternalUnits(kelvin, UnitTypeId.Celsius), 2);
        }
        catch (Exception) { /* Temperature is informative only. */ }
        return new AislSystemInfo(st.Name, st.SystemClassification.ToString(), fluid, temperature);
    }

    // Pipe: its outer diameter. Fitting/accessory: largest outer diameter of the pipes on its connectors; fallback 2 × radius.
    private static double OuterMm(Element e, out bool estimated)
    {
        estimated = false;
        if (e is Pipe pipe) return PipeOuterMm(pipe);
        var connectors = (e as FamilyInstance)?.MEPModel?.ConnectorManager?.Connectors;
        if (connectors is null) return 0;
        double best = 0, radius = 0;
        foreach (Connector c in connectors)
        {
            try
            {
                foreach (Connector r in c.AllRefs)
                    if (r.Owner is Pipe p && p.Id != e.Id) best = Math.Max(best, PipeOuterMm(p));
            }
            catch (Exception) { /* Some connectors do not expose references. */ }
            try { if (c.Shape == ConnectorProfileType.Round) radius = Math.Max(radius, c.Radius); }
            catch (Exception) { /* Non-round connector. */ }
        }
        if (best > 0) return best;
        estimated = radius > 0;
        return estimated ? AislamientoBuilder.ToMm(2 * radius) : 0;
    }

    private static double PipeOuterMm(Pipe pipe)
    {
        var p = pipe.get_Parameter(BuiltInParameter.RBS_PIPE_OUTER_DIAMETER);
        double feet = p is { HasValue: true } ? p.AsDouble() : 0;
        return feet > 0 ? AislamientoBuilder.ToMm(feet) : 0;
    }

    private static double? ExistingThicknessMm(Document doc, ElementId id)
    {
        var insulation = InsulationLiningBase.GetInsulationIds(doc, id)
            .Select(i => doc.GetElement(i)).OfType<PipeInsulation>().FirstOrDefault();
        return insulation is null ? null : AislamientoBuilder.ToMm(insulation.Thickness);
    }

    private static T ByName<T>(IReadOnlyList<T> items, string name) where T : Element =>
        items.FirstOrDefault(t => string.Equals(t.Name, name, StringComparison.OrdinalIgnoreCase)) ?? items[0];

    /// <summary>True if the view (or its template) hides the pipe insulation category: the insulation exists but is not seen.</summary>
    private static bool InsulationHidden(Document doc, View? view)
    {
        if (view is null) return false;
        var category = new ElementId(BuiltInCategory.OST_PipeInsulations);
        try
        {
            if (view.GetCategoryHidden(category)) return true;
            if (view.ViewTemplateId != ElementId.InvalidElementId && doc.GetElement(view.ViewTemplateId) is View template
                && template.GetCategoryHidden(category)) return true;
        }
        catch (Exception) { /* views without visibility/graphics */ }
        return false;
    }

    private static void WriteReport(Document doc, View? view, IReadOnlyList<AislDecision> decisions,
        IReadOnlyList<AislDeviation> deviations, AislBuildResult result, string scopeText)
    {
        string paths;
        bool written = false;
        try
        {
            AislReport.EnsureFolders();
            var report = AislReport.Write(decisions, deviations, AislReport.OutputFolder, DateTime.Now);
            paths = $"Informe: {report.ReportPath}\nDesviaciones: {report.DeviationsPath}";
            written = true;
        }
        catch (Exception ex) { paths = "No se pudieron escribir los informes: " + ex.Message; }
        int Count(AislAction a) => decisions.Count(d => d.Action == a);
        int kept = Count(AislAction.MantenerSuficiente) + result.Kept;
        // Per network: elements now insulated.
        string perNetwork = string.Join("\n", decisions
            .Where(d => d.Action is AislAction.Crear or AislAction.Aumentar or AislAction.MantenerSuficiente)
            .GroupBy(d => d.Classification.Red).OrderBy(g => (int)g.Key)
            .Select(g => $"  {AislReport.RedText(g.Key)}: {g.Count()} elementos"));
        string failures = result.Failed == 0 ? "" :
            "\nMotivo de los fallos: " + string.Join(" | ", result.Deviations.Select(d => d.Reason).Where(r => !string.IsNullOrWhiteSpace(r))
                .GroupBy(r => r).OrderByDescending(g => g.Count()).Take(3).Select(g => $"{g.Key} ({g.Count()})")) + "\n";
        string hiddenNote = result.Created + result.Increased > 0 && InsulationHidden(doc, view)
            ? "\nOJO: la vista activa (o su plantilla) oculta la categoría «Aislamientos de tubería»: el aislamiento existe pero "
              + "aquí no se ve. Actívela en Visibilidad/Gráficos (VV) o mírelo en una vista 3D.\n"
            : "";
        var dialog = new TaskDialog(Title)
        {
            MainInstruction = result.Created + result.Increased > 0
                ? $"Aislamiento aplicado: {result.Created} creados, {result.Increased} aumentados (quedan seleccionados)."
                : result.Failed > 0 ? "No se pudo crear el aislamiento." : "No se ha modificado ningún aislamiento.",
            MainContent = scopeText + "\n" + (perNetwork.Length > 0 ? perNetwork + "\n" : "") + failures + hiddenNote +
                $"Creados: {result.Created} · Aumentados: {result.Increased} · Ya suficientes: {kept}\n" +
                $"No exigidos: {Count(AislAction.NoExigido)} · Sin clasificar: {Count(AislAction.SinClasificar)} · " +
                $"Excluidos: {Count(AislAction.Excluida)} · Fallidos: {result.Failed}\n" +
                (result.Created + result.Increased > 0 ? "Ctrl+Z deshace todo el aislamiento.\n" : "") +
                "\n" + paths + "\n\n" + RiteNote,
            CommonButtons = TaskDialogCommonButtons.Close
        };
        if (deviations.Count > 0)
            dialog.ExpandedContent = string.Join("\n", deviations.Take(40).Select(d => $"{d.Element} · {d.What}: {d.Reason} {d.HowToFix}")) +
                (deviations.Count > 40 ? $"\n… y {deviations.Count - 40} más en el CSV." : "");
        if (written) dialog.AddCommandLink(TaskDialogCommandLinkId.CommandLink1, "Abrir la carpeta de informes");
        if (dialog.Show() == TaskDialogResult.CommandLink1)
        {
            try
            {
                var start = new ProcessStartInfo("explorer.exe") { UseShellExecute = true };
                start.ArgumentList.Add(AislReport.OutputFolder);
                Process.Start(start);
            }
            catch (Exception ex) { Show("No se pudo abrir la carpeta de informes.", AislReport.OutputFolder, ex.Message); }
        }
    }

    internal static void Show(string instruction, string content, string? details = null)
    {
        var dialog = new TaskDialog(Title) { MainInstruction = instruction, MainContent = content };
        if (!string.IsNullOrWhiteSpace(details)) dialog.ExpandedContent = details;
        dialog.Show();
    }
}
