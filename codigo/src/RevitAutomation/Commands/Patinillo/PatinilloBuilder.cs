#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Plumbing;
using CadMepRevit.Revit;
using View = Autodesk.Revit.DB.View;

namespace RevitAutomation.Addin.Commands.Patinillo;

public sealed class PatinilloBuildResult
{
    public int PipeCount { get; internal set; }
    public int FittingCount { get; internal set; }
    public int CapCount { get; internal set; }
    public List<PatinilloDeviation> Deviations { get; } = new();
    public List<ElementId> ViewIds { get; } = new();
    public ElementId? ThreeDViewId { get; internal set; }
}

/// <summary>The command owns the group, including its placement work plane. This builder owns inner transactions only.</summary>
public static class PatinilloBuilder
{
    public static PatinilloBuildResult Build(Document doc, PatinilloSettings settings, PatinilloPlan plan,
        PatinilloRect rect, IReadOnlyList<Level> levels, ElementId pipeTypeId,
        IReadOnlyDictionary<PatinilloService, ElementId> systems)
    {
        if (!plan.Fits) throw new InvalidOperationException("El patinillo no cabe; no se puede modelar.");
        if (levels.Count != plan.Levels.Count || levels.Count == 0)
            throw new InvalidOperationException("Los niveles del modelo no coinciden con los del plan.");
        var result = new PatinilloBuildResult();
        void Note(string text) => result.Deviations.Add(new("", "Preferencias de enrutamiento", text, ""));
        try
        {
            var log = new List<string>();
            PatinilloTransactions.Run(doc, "Patinillo · preparar uniones", () =>
                log.AddRange(FittingSetup.Complete(doc, new[] { pipeTypeId }, null)));
            foreach (string line in log) Note(line);
        }
        catch (Autodesk.Revit.Exceptions.RegenerationFailedException) { throw; }
        catch (Exception ex) { Note("No se pudieron completar las uniones: " + ex.Message); }

        foreach (var placement in plan.Placements)
        {
            var p = placement;
            if (p.Pipe.FloorIndex < 0 || p.Pipe.FloorIndex >= levels.Count)
                throw new InvalidOperationException("Índice de planta inválido: «" + p.Pipe.Id + "».");
            XYZ start = World(rect, p.XMm, p.YMm, p.StartElevationMm);
            XYZ bend = World(rect, p.XMm, p.YMm, p.ExitElevationMm);
            XYZ end = World(rect, p.XMm, p.EndYMm, p.ExitElevationMm);
            Pipe? riser = null;
            Pipe? horizontal = null;
            PatinilloTransactions.Run(doc, "Patinillo · tuberías «" + p.Pipe.Id + "»", () =>
            {
                riser = Pipe.Create(doc, systems[p.Pipe.Service], pipeTypeId, levels[0].Id, start, bend);
                horizontal = Pipe.Create(doc, systems[p.Pipe.Service], pipeTypeId, levels[p.Pipe.FloorIndex].Id, bend, end);
                foreach (Pipe pipe in new[] { riser!, horizontal! })
                {
                    var diameter = pipe.get_Parameter(BuiltInParameter.RBS_PIPE_DIAMETER_PARAM);
                    if (diameter is null || diameter.IsReadOnly || !diameter.Set(Ft(p.Pipe.NominalMm)))
                        throw new InvalidOperationException("No se pudo asignar el diámetro de «" + p.Pipe.Id + "».");
                    var comments = pipe.get_Parameter(BuiltInParameter.ALL_MODEL_INSTANCE_COMMENTS);
                    if (comments is null || comments.IsReadOnly || !comments.Set("Patinillo · " + p.Pipe.Id))
                        throw new InvalidOperationException("No se pudo escribir el identificador de «" + p.Pipe.Id + "».");
                }
            });
            result.PipeCount += 2;
            bool elbow = false;
            try
            {
                PatinilloTransactions.Run(doc, "Patinillo · codo «" + p.Pipe.Id + "»", () =>
                {
                    doc.Regenerate();
                    var a = Nearest(riser!, bend);
                    var b = Nearest(horizontal!, bend);
                    if (a.IsConnected || b.IsConnected) throw new InvalidOperationException("Los conectores del encuentro ya están ocupados.");
                    _ = doc.Create.NewElbowFitting(a, b) ?? throw new InvalidOperationException("Revit no devolvió un codo.");
                });
                elbow = true;
                result.FittingCount++;
            }
            catch (Autodesk.Revit.Exceptions.RegenerationFailedException) { throw; }
            catch (Exception ex)
            {
                result.Deviations.Add(new(p.Pipe.Id, "Sin codo", ex.Message,
                    "Cargue un codo compatible y revise las preferencias de enrutamiento; los extremos coinciden y siguen abiertos."));
            }
            if (settings.End != PatinilloEnd.Tapon) continue;
            try
            {
                PatinilloTransactions.Run(doc, "Patinillo · tapón «" + p.Pipe.Id + "»", () =>
                {
                    // Never pass the riser: its bottom is the future connection to the meters.
                    PlumbingUtils.PlaceCapOnOpenEnds(doc, horizontal!.Id, ElementId.InvalidElementId);
                    doc.Regenerate();
                    if (!elbow)
                    {
                        // PlaceCapOnOpenEnds also caps the free elbow-side connector. Remove only that new cap.
                        var inner = Nearest(horizontal, bend);
                        var innerCaps = inner.AllRefs.Cast<Connector>().Select(c => c.Owner)
                            .OfType<FamilyInstance>().Select(e => e.Id).Distinct().ToList();
                        foreach (var id in innerCaps) doc.Delete(id);
                        doc.Regenerate();
                        if (Nearest(horizontal, bend).IsConnected)
                            throw new InvalidOperationException("No se pudo dejar abierto el extremo destinado al codo.");
                    }
                    if (!Nearest(horizontal, end).IsConnected)
                        throw new InvalidOperationException("Revit no colocó un tapón en el extremo exterior.");
                });
                result.CapCount++;
            }
            catch (Autodesk.Revit.Exceptions.RegenerationFailedException) { throw; }
            catch (Exception ex)
            {
                result.Deviations.Add(new(p.Pipe.Id, "Extremo abierto (no hay tapón en el tipo)", ex.Message,
                    "Cargue un tapón y asígnelo en las preferencias de enrutamiento del tipo de tubería."));
            }
        }
        if (plan.Placements.Count > 0) CreateViews(doc, rect, plan, settings, result);
        return result;
    }

    private static Connector Nearest(Pipe pipe, XYZ point) => pipe.ConnectorManager.Connectors.Cast<Connector>()
        .Where(c => c.ConnectorType == ConnectorType.End).OrderBy(c => c.Origin.DistanceTo(point)).First();

    private static XYZ World(PatinilloRect rect, double xMm, double yMm, double zMm)
    {
        var xy = rect.ToWorld(Ft(xMm), Ft(yMm));
        return new XYZ(xy.X, xy.Y, Ft(zMm));
    }

    private static void CreateViews(Document doc, PatinilloRect rect, PatinilloPlan plan, PatinilloSettings settings,
        PatinilloBuildResult result)
    {
        double margin = Ft(500);
        double bottom = Ft(plan.Placements.Min(p => p.StartElevationMm)) - margin;
        double top = Ft(plan.Placements.Max(p => p.ExitElevationMm)) + margin;
        double outlet = Math.Max(Ft(settings.OutletLengthMm), -Ft(plan.Placements.Min(p => p.EndYMm)));
        var corners = new[] { rect.ToWorld(-margin, -outlet - margin), rect.ToWorld(rect.Width + margin, -outlet - margin),
            rect.ToWorld(-margin, rect.Depth + margin), rect.ToWorld(rect.Width + margin, rect.Depth + margin) };
        var families = new FilteredElementCollector(doc).OfClass(typeof(ViewFamilyType)).Cast<ViewFamilyType>().ToList();
        try
        {
            ElementId? created = null;
            PatinilloTransactions.Run(doc, "Patinillo · vista 3D", () =>
            {
                var family = families.FirstOrDefault(t => t.ViewFamily == ViewFamily.ThreeDimensional)
                    ?? throw new InvalidOperationException("No hay tipo de vista 3D.");
                var view = View3D.CreateIsometric(doc, family.Id);
                view.Name = UniqueViewName(doc, "Patinillo fontanería - 3D");
                using var box = new BoundingBoxXYZ
                {
                    Min = new XYZ(corners.Min(p => p.X), corners.Min(p => p.Y), bottom),
                    Max = new XYZ(corners.Max(p => p.X), corners.Max(p => p.Y), top)
                };
                view.SetSectionBox(box);
                view.IsSectionBoxActive = true;
                created = view.Id;
            });
            if (created != null) { result.ThreeDViewId = created; result.ViewIds.Add(created); }
        }
        catch (Autodesk.Revit.Exceptions.RegenerationFailedException) { throw; }
        catch (Exception ex) { result.Deviations.Add(new("", "Sin vista 3D", ex.Message, "Cree una vista 3D y ajuste su caja de sección.")); }
        try
        {
            ElementId? created = null;
            PatinilloTransactions.Run(doc, "Patinillo · alzado", () =>
            {
                var family = families.FirstOrDefault(t => t.ViewFamily == ViewFamily.Section)
                    ?? throw new InvalidOperationException("No hay tipo de vista de sección.");
                var mid = rect.ToWorld(rect.Width / 2, -outlet - margin);
                // Revit's view direction (BasisZ) points TOWARDS the viewer, as in a plan (+Z while looking down).
                // Camera outside the mouth looking into the shaft → BasisZ = −V. The cut plane is Max.Z = 0 (at the
                // origin, outside the comb ends) and the far clip reaches the back wall at negative Z.
                XYZ towardsViewer = new(-rect.VX, -rect.VY, 0);
                XYZ up = XYZ.BasisZ;
                var transform = Transform.Identity;
                transform.Origin = new XYZ(mid.X, mid.Y, bottom);
                transform.BasisX = up.CrossProduct(towardsViewer);
                transform.BasisY = up;
                transform.BasisZ = towardsViewer;
                using var box = new BoundingBoxXYZ
                {
                    Transform = transform,
                    Min = new XYZ(-rect.Width / 2 - margin, 0, -(outlet + rect.Depth + 2 * margin)),
                    Max = new XYZ(rect.Width / 2 + margin, top - bottom, 0)
                };
                for (int i = 0; i < 3; i++) { box.set_MinEnabled(i, true); box.set_MaxEnabled(i, true); }
                var view = ViewSection.CreateSection(doc, family.Id, box);
                view.Name = UniqueViewName(doc, "Patinillo fontanería - Alzado");
                created = view.Id;
            });
            if (created != null) result.ViewIds.Add(created);
        }
        catch (Autodesk.Revit.Exceptions.RegenerationFailedException) { throw; }
        catch (Exception ex) { result.Deviations.Add(new("", "Sin alzado", ex.Message, "Cree una sección mirando hacia la boca del patinillo.")); }
    }

    internal static string UniqueViewName(Document doc, string stem)
    {
        var names = new HashSet<string>(new FilteredElementCollector(doc).OfClass(typeof(View)).Cast<View>()
            .Select(v => v.Name), StringComparer.OrdinalIgnoreCase);
        string name = stem;
        for (int n = 2; names.Contains(name); n++) name = stem + " (" + n + ")";
        return name;
    }

    private static double Ft(double mm) => PatinilloCommand.ToFeet(mm);
}

/// <summary>Every inner transaction uses this policy. Errors roll back; warnings never open a modal dialog.</summary>
internal static class PatinilloTransactions
{
    internal static void Run(Document doc, string name, Action body)
    {
        using var transaction = new Transaction(doc, name);
        if (transaction.Start() != TransactionStatus.Started) throw new InvalidOperationException("No se pudo iniciar «" + name + "».");
        var preprocessor = new PatinilloFailures();
        var options = transaction.GetFailureHandlingOptions();
        options.SetFailuresPreprocessor(preprocessor);
        options.SetClearAfterRollback(true);
        transaction.SetFailureHandlingOptions(options);
        try
        {
            body();
            if (transaction.Commit() != TransactionStatus.Committed)
                throw new InvalidOperationException("Revit rechazó «" + name + "». " + string.Join("; ", preprocessor.Errors));
        }
        catch
        {
            if (transaction.GetStatus() == TransactionStatus.Started) transaction.RollBack();
            throw;
        }
    }
}

internal sealed class PatinilloFailures : IFailuresPreprocessor
{
    internal List<string> Errors { get; } = new();
    public FailureProcessingResult PreprocessFailures(FailuresAccessor accessor)
    {
        foreach (var failure in accessor.GetFailureMessages())
        {
            if (failure.GetSeverity() == FailureSeverity.Warning) accessor.DeleteWarning(failure);
            else if (failure.GetSeverity() == FailureSeverity.Error) Errors.Add(failure.GetDescriptionText());
        }
        return Errors.Count > 0 ? FailureProcessingResult.ProceedWithRollBack : FailureProcessingResult.Continue;
    }
}
