using CadMepRevit.Compat; // RevitIds: ElementId de 32 bits en Revit 2023
#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Plumbing;
using RevitAutomation.Addin.Commands.Patinillo;

namespace RevitAutomation.Addin.Commands.Aislamiento;

/// <summary>What the builder did. Kept = the element already had enough insulation when it was written.</summary>
internal sealed record AislBuildResult(int Created, int Increased, int Kept, int Failed, IReadOnlyList<AislDeviation> Deviations,
    IReadOnlyList<ElementId> InsulationIds);

/// <summary>Writes the decided insulation into the model: creates it or thickens the existing one, never reduces or deletes.</summary>
internal static class AislamientoBuilder
{
    internal const int BatchSize = 200;
    private const double ToleranceMm = 0.01;
    private const int MaxCommentLength = 250;

    private enum Outcome { Created, Increased, Kept }

    internal static AislBuildResult Apply(Document doc, IReadOnlyList<AislDecision> decisions,
        IReadOnlyDictionary<AislRed, ElementId> typeIdsByRed)
    {
        var insulationIds = new List<ElementId>();
        var deviations = new List<AislDeviation>();
        var work = new List<AislDecision>();
        foreach (var d in decisions.Where(d => d.Action == AislAction.Crear || d.Action == AislAction.Aumentar))
        {
            if (d.Requirement is { Required: true, ThicknessMm: > 0 }) work.Add(d);
            else deviations.Add(new(d.Item.ElementId.ToString(), "No se pudo aislar", "La decisión no tiene un espesor exigido.",
                "Revise la clasificación del sistema y repita."));
        }
        int failed = deviations.Count;
        if (work.Count == 0) return new AislBuildResult(0, 0, 0, failed, deviations, insulationIds);

        int created = 0, increased = 0, kept = 0;
        void Count(Outcome o)
        {
            if (o == Outcome.Created) created++;
            else if (o == Outcome.Increased) increased++;
            else kept++;
        }

        using var group = new TransactionGroup(doc, AislamientoCommand.Title);
        if (group.Start() != TransactionStatus.Started) throw new InvalidOperationException("No se pudo iniciar el aislamiento.");
        try
        {
            for (int start = 0; start < work.Count; start += BatchSize)
            {
                var batch = work.Skip(start).Take(BatchSize).ToList();
                int end = Math.Min(start + batch.Count, work.Count);
                var outcomes = new List<Outcome>();
                var ids = new List<ElementId>();
                try
                {
                    PatinilloTransactions.Run(doc, $"Aislamiento RITE · {start + 1}-{end}", () =>
                    {
                        outcomes.Clear();
                        ids.Clear();
                        foreach (var d in batch) outcomes.Add(ApplyOne(doc, d, typeIdsByRed, ids));
                    });
                    outcomes.ForEach(Count);
                    insulationIds.AddRange(ids);
                }
                catch (Exception)
                {
                    // The whole batch was rolled back: retry element by element so one bad element does not block the rest.
                    foreach (var d in batch)
                    {
                        Outcome outcome = Outcome.Kept;
                        var one = new List<ElementId>();
                        try
                        {
                            PatinilloTransactions.Run(doc, $"Aislamiento RITE · {d.Item.ElementId}", () =>
                            {
                                one.Clear();
                                outcome = ApplyOne(doc, d, typeIdsByRed, one);
                            });
                            Count(outcome);
                            insulationIds.AddRange(one);
                        }
                        catch (Exception ex)
                        {
                            failed++;
                            deviations.Add(new(d.Item.ElementId.ToString(), "No se pudo aislar", ex.Message,
                                "Revise el elemento (grupo, subproyecto prestado, conexiones) y aísle a mano o repita."));
                        }
                    }
                }
            }
            if (group.Assimilate() != TransactionStatus.Committed) throw new InvalidOperationException("Revit no aceptó el grupo del aislamiento.");
        }
        catch
        {
            if (group.GetStatus() == TransactionStatus.Started) group.RollBack();
            throw;
        }
        return new AislBuildResult(created, increased, kept, failed, deviations, insulationIds);
    }

    private static Outcome ApplyOne(Document doc, AislDecision decision, IReadOnlyDictionary<AislRed, ElementId> typeIdsByRed,
        List<ElementId> insulationIds)
    {
        var req = decision.Requirement!;
        var hostId = RevitIds.FromValue(decision.Item.ElementId);
        if (doc.GetElement(hostId) is null) throw new InvalidOperationException("El elemento ya no existe en el modelo.");
        double thicknessFeet = ToFeet(req.ThicknessMm);
        // Read the insulation now (not the planner's snapshot): it decides between create and increase, never reduce.
        PipeInsulation? existing = InsulationLiningBase.GetInsulationIds(doc, hostId)
            .Select(id => doc.GetElement(id)).OfType<PipeInsulation>().FirstOrDefault();
        Outcome outcome;
        PipeInsulation insulation;
        if (existing is null)
        {
            ElementId typeId = TypeFor(req.Red, typeIdsByRed);
            if (typeId == ElementId.InvalidElementId) throw new InvalidOperationException("No hay tipo de aislamiento de tubería en el proyecto.");
            insulation = PipeInsulation.Create(doc, hostId, typeId, thicknessFeet)
                ?? throw new InvalidOperationException("Revit no creó el aislamiento.");
            outcome = Outcome.Created;
        }
        else
        {
            insulation = existing;
            if (ToMm(existing.Thickness) + ToleranceMm >= req.ThicknessMm) { insulationIds.Add(existing.Id); return Outcome.Kept; }
            existing.Thickness = thicknessFeet; // keeps its insulation type
            outcome = Outcome.Increased;
        }
        Parameter? comments = insulation.get_Parameter(BuiltInParameter.ALL_MODEL_INSTANCE_COMMENTS);
        if (comments is { IsReadOnly: false } && !string.IsNullOrWhiteSpace(req.Rule))
            comments.Set(req.Rule.Length > MaxCommentLength ? req.Rule.Substring(0, MaxCommentLength) : req.Rule);
        insulationIds.Add(insulation.Id);
        return outcome;
    }

    private static ElementId TypeFor(AislRed red, IReadOnlyDictionary<AislRed, ElementId> typeIdsByRed)
    {
        if (typeIdsByRed.TryGetValue(red, out var id) && id != ElementId.InvalidElementId) return id;
        return typeIdsByRed.Values.FirstOrDefault(v => v != ElementId.InvalidElementId) ?? ElementId.InvalidElementId;
    }

    internal static double ToFeet(double mm) => UnitUtils.ConvertToInternalUnits(mm, UnitTypeId.Millimeters);
    internal static double ToMm(double feet) => UnitUtils.ConvertFromInternalUnits(feet, UnitTypeId.Millimeters);
}
