#nullable enable
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using Autodesk.Revit.Attributes;
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Architecture;
using Autodesk.Revit.DB.Plumbing;
using Autodesk.Revit.UI;
using Autodesk.Revit.UI.Selection;
using TaskDialog = Autodesk.Revit.UI.TaskDialog;
using DialogResult = System.Windows.Forms.DialogResult;
using View = Autodesk.Revit.DB.View;

namespace RevitAutomation.Addin.Commands.Patinillo;

[Transaction(TransactionMode.Manual)]
[Regeneration(RegenerationOption.Manual)]
public sealed class PatinilloCommand : IExternalCommand
{
    internal const string Title = "Patinillo de fontanería";

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
            Show("El proyecto está abierto en solo lectura.", "Abra una copia editable y vuelva a pulsar «Patinillo de fontanería».");
            return Result.Cancelled;
        }
        try { return Run(ui); }
        catch (Autodesk.Revit.Exceptions.OperationCanceledException) { return Result.Cancelled; }
        catch (Exception ex)
        {
            message = ex.ToString();
            Show("No se pudo completar el patinillo.", ex.Message, ex.ToString());
            return Result.Failed;
        }
    }

    private static Result Run(UIDocument ui)
    {
        Document doc = ui.Document;
        View view = ui.ActiveView;
        var levels = new FilteredElementCollector(doc).OfClass(typeof(Level)).Cast<Level>()
            .OrderBy(l => l.Elevation).ThenBy(l => l.Name).ToList();
        var modelLevels = levels.Select(l => new PatinilloLevel(l.Name, ToMm(l.Elevation))).ToList();
        var types = new FilteredElementCollector(doc).OfClass(typeof(PipeType)).Cast<PipeType>().OrderBy(t => t.Name).ToList();
        var systems = new FilteredElementCollector(doc).OfClass(typeof(PipingSystemType)).Cast<PipingSystemType>().OrderBy(t => t.Name).ToList();
        var cold = systems.Where(t => t.SystemClassification == MEPSystemClassification.DomesticColdWater).ToList();
        var hot = systems.Where(t => t.SystemClassification == MEPSystemClassification.DomesticHotWater).ToList();
        var returns = systems.Where(t => t.SystemClassification == MEPSystemClassification.DomesticHotWater ||
            t.SystemClassification == MEPSystemClassification.OtherPipe ||
            t.Name.Contains("retorno", StringComparison.OrdinalIgnoreCase) || t.Name.Contains("recirc", StringComparison.OrdinalIgnoreCase)).ToList();
        bool canTest = levels.Count <= 2 && !new FilteredElementCollector(doc).OfClass(typeof(Floor)).Any();
        bool isPlan = view is ViewPlan && !view.IsTemplate && view.ViewType == ViewType.FloorPlan;
        // The empty-project exception must reach the dialog even from the initial 3D view.
        if (!isPlan && !canTest) return NeedPlan(view);
        var selected = ui.Selection.GetElementIds().Select(id => doc.GetElement(id)).ToList();
        Room? room = selected.OfType<Room>().FirstOrDefault();
        var curves = selected.OfType<CurveElement>().Select(e => e.GeometryCurve).ToList();
        bool allCurves = selected.Count > 0 && selected.Count == curves.Count;
        bool closed = allCurves && TryLoop(curves, out _);
        var options = new PatinilloModelOptions
        {
            Levels = modelLevels,
            PipeTypeNames = types.Select(t => t.Name).ToList(),
            ColdSystemNames = cold.Select(t => t.Name).ToList(),
            HotSystemNames = hot.Select(t => t.Name).ToList(),
            ReturnSystemNames = returns.Select(t => t.Name).ToList(),
            CanCreateTestBuilding = canTest,
            ShaftSourceText = room != null ? "Habitación seleccionada: " + room.Name : closed
                ? $"Contorno de {curves.Count} líneas seleccionado"
                : "Sin selección: dibuje un rectángulo o coloque el patinillo con un clic"
        };
        PatinilloSettings settings;
        PatinilloAction action;
        using (var form = new PatinilloForm(PatinilloSettingsStore.Load(), options))
        {
            if (form.ShowDialog() != DialogResult.OK) return Result.Cancelled;
            settings = form.Settings;
            action = form.Action;
        }
        bool saved = PatinilloSettingsStore.Save(settings);
        if (action == PatinilloAction.Cancelar) return Result.Cancelled;
        if (action == PatinilloAction.CrearEdificioPrueba)
        {
            Show("Edificio de prueba", PatinilloTestBuilding.Create(doc, settings));
            return Result.Succeeded;
        }
        if (!isPlan) return NeedPlan(view);
        var validation = PatinilloPlanner.Validate(settings);
        if (validation.Count > 0)
        {
            Show("Revise los datos del patinillo.", string.Join("\n", validation));
            return Result.Cancelled;
        }
        if (types.Count == 0 || cold.Count == 0 || hot.Count == 0 || (settings.HotReturn && returns.Count == 0))
        {
            Show("Faltan tipos para modelar las tuberías.",
                "El proyecto necesita un tipo de tubería y sistemas de agua fría y ACS" +
                (settings.HotReturn ? ", además de un sistema de retorno" : "") +
                ". Cárguelos o créelos en el navegador de proyectos y repita.",
                $"Tipos de tubería: {types.Count}; fría: {cold.Count}; ACS: {hot.Count}; retorno: {returns.Count}.");
            return Result.Cancelled;
        }
        if (levels.Count < 2)
        {
            Show("Faltan niveles en el proyecto.", "Cree al menos la planta baja y el nivel superior del forjado.");
            return Result.Cancelled;
        }
        PipeType type = ByName(types, settings.PipeTypeName);
        var serviceIds = new Dictionary<PatinilloService, ElementId>
        {
            [PatinilloService.AguaFria] = ByName(cold, settings.ColdSystemName).Id,
            [PatinilloService.Acs] = ByName(hot, settings.HotSystemName).Id
        };
        if (settings.HotReturn) serviceIds[PatinilloService.RetornoAcs] = ByName(returns, settings.ReturnSystemName).Id;
        var deviations = new List<PatinilloDeviation>();
        if (!saved) deviations.Add(new("", "Preferencias no guardadas", "No se pudo guardar la configuración.", "Compruebe los permisos de la carpeta de configuración."));
        PatinilloPlan plan;
        PatinilloBuildResult result;
        // Start before creating the temporary sketch plane. Every early return disposes/rolls back the group.
        using (var group = new TransactionGroup(doc, Title))
        {
            if (group.Start() != TransactionStatus.Started) throw new InvalidOperationException("No se pudo iniciar el patinillo.");
            try
            {
                PatinilloRect rect;
                if (room != null)
                {
                    using var boundaryOptions = new SpatialElementBoundaryOptions { SpatialElementBoundaryLocation = SpatialElementBoundaryLocation.Finish };
                    var loops = room.GetBoundarySegments(boundaryOptions);
                    if (loops is null || loops.Count == 0) throw new InvalidOperationException("La habitación seleccionada no tiene un contorno cerrado. Revise sus límites.");
                    var outlines = loops.Select(loop => loop.SelectMany(s => s.GetCurve().Tessellate()).Select(p => (p.X, p.Y)).ToList()).ToList();
                    rect = PatinilloGeometry.FromOutline(outlines.OrderByDescending(Area).First());
                }
                else if (allCurves)
                {
                    if (!TryLoop(curves, out var points))
                    {
                        Show("Las líneas seleccionadas no cierran un único contorno.", "Seleccione solo las líneas de un contorno cerrado, sin ramificaciones ni huecos.");
                        group.RollBack();
                        return Result.Cancelled;
                    }
                    rect = PatinilloGeometry.FromOutline(points);
                }
                else
                {
                    var choice = new TaskDialog(Title)
                    {
                        MainInstruction = "Indique cómo colocar el patinillo.",
                        CommonButtons = TaskDialogCommonButtons.Cancel
                    };
                    choice.AddCommandLink(TaskDialogCommandLinkId.CommandLink1, "Dibujar un rectángulo");
                    choice.AddCommandLink(TaskDialogCommandLinkId.CommandLink2,
                        $"Colocar con un clic (medidas del diálogo: {settings.ShaftWidthMm:0.##} × {settings.ShaftDepthMm:0.##} mm)");
                    var answer = choice.Show();
                    if (answer != TaskDialogResult.CommandLink1 && answer != TaskDialogResult.CommandLink2)
                    {
                        group.RollBack();
                        return Result.Cancelled;
                    }
                    EnsureWorkPlane(doc, (ViewPlan)view);
                    if (answer == TaskDialogResult.CommandLink1)
                    {
                        var box = ui.Selection.PickBox(PickBoxStyle.Directional, "Dibuje el rectángulo interior del patinillo (Esc para cancelar).");
                        rect = PatinilloGeometry.FromCorners(box.Min.X, box.Min.Y, box.Max.X, box.Max.Y);
                    }
                    else
                    {
                        XYZ center = ui.Selection.PickPoint("Pinche el centro del patinillo (Esc para cancelar).");
                        rect = PatinilloGeometry.FromCenter(center.X, center.Y, ToFeet(settings.ShaftWidthMm), ToFeet(settings.ShaftDepthMm));
                    }
                }
                EnsureWorkPlane(doc, (ViewPlan)view);
                XYZ mouth = ui.Selection.PickPoint("Pinche junto al lado largo por donde salen las tuberías (boca)");
                rect = PatinilloGeometry.WithMouthNear(rect, mouth.X, mouth.Y);
                var estimated = new HashSet<double>();
                var outerByNominal = new Dictionary<double, double>();
                double Outer(double nominal)
                {
                    if (outerByNominal.TryGetValue(nominal, out double known)) return known;
                    try
                    {
                        var rpm = type.RoutingPreferenceManager;
                        if (rpm.GetNumberOfRules(RoutingPreferenceRuleGroupType.Segments) > 0 &&
                            doc.GetElement(rpm.GetRule(RoutingPreferenceRuleGroupType.Segments, 0).MEPPartId) is PipeSegment segment)
                        {
                            var size = segment.GetSizes().Where(s => s.OuterDiameter > 0)
                                .OrderBy(s => Math.Abs(ToMm(s.NominalDiameter) - nominal)).FirstOrDefault();
                            if (size != null)
                            {
                                double outer = ToMm(size.OuterDiameter);
                                outerByNominal[nominal] = outer;
                                return outer;
                            }
                        }
                    }
                    catch (Exception) { /* A missing segment is handled by the explicit estimate below. */ }
                    estimated.Add(nominal);
                    return nominal * 1.25;
                }
                plan = PatinilloPlanner.Plan(settings, ToMm(rect.Width), ToMm(rect.Depth), modelLevels, Outer);
                foreach (var pipe in plan.Pipes.Where(p => estimated.Contains(p.NominalMm)))
                    deviations.Add(new(pipe.Id, "Diámetro exterior estimado", $"DN {pipe.NominalMm:0.##} mm × 1,25.", "Asigne un segmento con tamaños al tipo de tubería."));
                foreach (string note in plan.Notes) deviations.Add(new("", "Nota del plan", note, ""));
                using var preview = new PatinilloPreviewForm(plan);
                var confirmed = preview.ShowDialog();
                if (!plan.Fits)
                {
                    group.RollBack();
                    deviations.AddRange(plan.Pipes.Select(p => new PatinilloDeviation(p.Id, "No modelada · no cabe", plan.Message,
                        $"Use al menos {plan.MinWidthMm:0.##} × {plan.MinDepthMm:0.##} mm.")));
                    WriteReport(plan, deviations, null, null);
                    return Result.Cancelled;
                }
                if (confirmed != DialogResult.OK)
                {
                    group.RollBack();
                    return Result.Cancelled;
                }
                result = PatinilloBuilder.Build(doc, settings, plan, rect, levels, type.Id, serviceIds);
                deviations.AddRange(result.Deviations);
                if (group.Assimilate() != TransactionStatus.Committed) throw new InvalidOperationException("Revit no aceptó el grupo del patinillo.");
            }
            catch
            {
                if (group.GetStatus() == TransactionStatus.Started) group.RollBack();
                throw;
            }
        }
        // Reporting failure must not claim that an already assimilated model was rolled back.
        WriteReport(plan, deviations, result, ui);
        return Result.Succeeded;
    }

    private static Result NeedPlan(View view)
    {
        Show("Abra una vista en planta…", "Abra una planta de piso que no sea plantilla y repita. Vista activa: «" + view.Name + "».");
        return Result.Cancelled;
    }

    private static T ByName<T>(IReadOnlyList<T> items, string name) where T : Element =>
        items.FirstOrDefault(t => string.Equals(t.Name, name, StringComparison.OrdinalIgnoreCase)) ?? items[0];

    private static void EnsureWorkPlane(Document doc, ViewPlan view)
    {
        if (view.SketchPlane != null) return;
        PatinilloTransactions.Run(doc, "Patinillo · plano de trabajo", () =>
        {
            var level = view.GenLevel ?? throw new InvalidOperationException("La planta no tiene un nivel asociado.");
            using var plane = Plane.CreateByNormalAndOrigin(XYZ.BasisZ, new XYZ(0, 0, level.Elevation));
            view.SketchPlane = SketchPlane.Create(doc, plane);
        });
    }

    // Chain arbitrary selection order in either direction; reject branches, disconnected loops and open ends.
    private static bool TryLoop(IReadOnlyList<Curve> curves, out List<(double X, double Y)> points)
    {
        points = new();
        if (curves.Count == 0) return false;
        double tol = ToFeet(1);
        var remaining = curves.Select(c => c.Tessellate().ToList()).ToList();
        if (remaining.Any(p => p.Count < 2)) return false;
        var chain = remaining[0];
        remaining.RemoveAt(0);
        while (remaining.Count > 0)
        {
            XYZ end = chain[chain.Count - 1];
            var matches = remaining.Select((p, i) => (p, i)).Where(v =>
                v.p[0].DistanceTo(end) <= tol || v.p[v.p.Count - 1].DistanceTo(end) <= tol).ToList();
            if (matches.Count != 1 || end.DistanceTo(chain[0]) <= tol) return false;
            var next = matches[0];
            if (next.p[0].DistanceTo(end) > tol) next.p.Reverse();
            chain.AddRange(next.p.Skip(1));
            remaining.RemoveAt(next.i);
        }
        if (chain.Count < 4 || chain[0].DistanceTo(chain[chain.Count - 1]) > tol) return false;
        if (chain.Any(p => Math.Abs(p.Z - chain[0].Z) > tol)) return false;
        points = chain.Select(p => (p.X, p.Y)).ToList();
        return true;
    }

    private static double Area(List<(double X, double Y)> pts)
    {
        double sum = 0;
        for (int i = 0; i < pts.Count; i++)
        {
            var a = pts[i]; var b = pts[(i + 1) % pts.Count];
            sum += a.X * b.Y - b.X * a.Y;
        }
        return Math.Abs(sum);
    }

    private static void WriteReport(PatinilloPlan plan, IReadOnlyList<PatinilloDeviation> deviations,
        PatinilloBuildResult? result, UIDocument? ui)
    {
        string paths;
        bool written = false;
        try
        {
            PatinilloReport.EnsureFolders();
            var report = PatinilloReport.Write(plan, deviations, PatinilloReport.OutputFolder, DateTime.Now);
            paths = $"Informe: {report.ReportPath}\nDesviaciones: {report.DeviationsPath}";
            written = true;
        }
        catch (Exception ex) { paths = "No se pudieron escribir los informes: " + ex.Message; }
        var dialog = new TaskDialog(Title)
        {
            MainInstruction = result is null ? "El patinillo no cabe. No se ha modificado el proyecto." : "Patinillo creado.",
            MainContent = (result is null ? plan.Message :
                $"Tuberías: {result.PipeCount} · Codos: {result.FittingCount} · Tapones: {result.CapCount}\nVistas: {result.ViewIds.Count} · Plantas servidas: {plan.Placements.Select(p => p.Pipe.FloorIndex).Distinct().Count()}\nCtrl+Z deshace todo lo creado.") + "\n\n" + paths,
            CommonButtons = TaskDialogCommonButtons.Close
        };
        if (deviations.Count > 0)
            dialog.ExpandedContent = string.Join("\n", deviations.Take(40).Select(d => $"{d.PipeId} · {d.What}: {d.Reason} {d.HowToFix}")) +
                (deviations.Count > 40 ? $"\n… y {deviations.Count - 40} más en el CSV." : "");
        if (written) dialog.AddCommandLink(TaskDialogCommandLinkId.CommandLink1, "Abrir la carpeta de informes");
        if (dialog.Show() == TaskDialogResult.CommandLink1)
        {
            try
            {
                var start = new ProcessStartInfo("explorer.exe") { UseShellExecute = true };
                start.ArgumentList.Add(PatinilloReport.OutputFolder);
                Process.Start(start);
            }
            catch (Exception ex) { Show("No se pudo abrir la carpeta de informes.", PatinilloReport.OutputFolder, ex.Message); }
        }
        if (ui != null && result?.ThreeDViewId is { } id && ui.Document.GetElement(id) is View newView)
        {
            try { ui.ActiveView = newView; }
            catch (Exception ex) { Show("El modelo está creado; no se pudo activar su vista 3D.", "Abra «" + newView.Name + "» en el navegador.", ex.Message); }
        }
    }

    internal static double ToFeet(double mm) => UnitUtils.ConvertToInternalUnits(mm, UnitTypeId.Millimeters);
    internal static double ToMm(double feet) => UnitUtils.ConvertFromInternalUnits(feet, UnitTypeId.Millimeters);
    internal static void Show(string instruction, string content, string? details = null)
    {
        var dialog = new TaskDialog(Title) { MainInstruction = instruction, MainContent = content };
        if (!string.IsNullOrWhiteSpace(details)) dialog.ExpandedContent = details;
        dialog.Show();
    }
}
