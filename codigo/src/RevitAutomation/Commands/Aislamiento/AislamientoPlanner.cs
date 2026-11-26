#nullable enable
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace RevitAutomation.Addin.Commands.Aislamiento;

public static class AislPlanner
{
    /// <summary>Motivo de la clasificación de los elementos sin sistema (también lo usan las desviaciones).</summary>
    public const string NoSystemReason = "Sin sistema asignado";
    private const double Tolerance = 0.05;

    private static bool IsClima(AislRed red) => red is AislRed.ClimaCaliente or AislRed.ClimaFrio or AislRed.ClimaMixto;

    public static IReadOnlyList<AislDecision> Decide(IReadOnlyList<AislItem> items,
        IReadOnlyDictionary<string, AislSystemInfo> systems, AislSettings s)
    {
        var classified = new Dictionary<string, AislClassification>(StringComparer.OrdinalIgnoreCase);
        var decisions = new List<AislDecision>(items.Count);
        foreach (var item in items)
        {
            if (string.IsNullOrEmpty(item.SystemName) || !systems.TryGetValue(item.SystemName, out var system))
            {
                decisions.Add(new AislDecision(item, new AislClassification(AislRed.SinClasificar, NoSystemReason, false),
                    null, AislAction.SinClasificar));
                continue;
            }
            if (!classified.TryGetValue(item.SystemName, out var c))
                classified[item.SystemName] = c = AislClassifier.Classify(system, s);

            if (IsClima(c.Red) && !s.IncludeHydronic)
                c = new AislClassification(AislRed.Excluida, "Clima por agua desactivado en el diálogo", c.ByUser);
            else if (item.Kind != AislElementKind.Tuberia && !s.IncludeFittingsAndAccessories)
                c = new AislClassification(AislRed.Excluida, "Piezas y accesorios desactivados en el diálogo", c.ByUser);

            if (c.Red == AislRed.SinClasificar) { decisions.Add(new AislDecision(item, c, null, AislAction.SinClasificar)); continue; }
            if (c.Red == AislRed.Excluida) { decisions.Add(new AislDecision(item, c, null, AislAction.Excluida)); continue; }

            var network = s.Network(c.Red);
            if (!network.Enabled)
            {
                var off = new AislRequirement(false, c.Red, AislRite.MaterialOf(c.Red), item.OuterMm, 0, s.Exterior, 0, 0, 0, 0,
                    "No se aísla: red desactivada en el diálogo");
                decisions.Add(new AislDecision(item, c, off, AislAction.NoExigido));
                continue;
            }
            var r = network.ThicknessMm is double fixedMm && fixedMm > 0
                ? AislRite.Fixed(c.Red, item.OuterMm, fixedMm, s)
                : AislRite.Requirement(c.Red, item.OuterMm, s);
            AislAction action = !r.Required ? AislAction.NoExigido
                : item.ExistingThicknessMm is null ? AislAction.Crear
                : item.ExistingThicknessMm.Value >= r.ThicknessMm - Tolerance ? AislAction.MantenerSuficiente
                : AislAction.Aumentar;
            decisions.Add(new AislDecision(item, c, r, action));
        }
        return decisions;
    }

    public static IReadOnlyList<AislSummaryRow> Summarize(IReadOnlyList<AislDecision> decisions) =>
        decisions
            .GroupBy(d => (System: d.Item.SystemName ?? "", d.Classification.Red, Outer: Math.Round(d.Item.OuterMm, 1),
                Thickness: d.Requirement?.ThicknessMm ?? 0, d.Action))
            .Select(g =>
            {
                var first = g.First();
                return new AislSummaryRow(g.Key.System, g.Key.Red, g.Key.Outer, first.Requirement?.TemperatureC ?? 0,
                    g.Key.Thickness, first.Requirement?.Rule ?? first.Classification.Reason, g.Key.Action, g.Count());
            })
            .OrderBy(r => r.SystemName, StringComparer.CurrentCultureIgnoreCase)
            .ThenBy(r => r.OuterMm)
            .ThenBy(r => r.Action)
            .ThenBy(r => r.ThicknessMm)
            .ToList();

    public static IReadOnlyList<AislDeviation> Deviations(IReadOnlyList<AislDecision> decisions)
    {
        var result = new List<AislDeviation>();
        var unclassified = decisions
            .Where(d => d.Action == AislAction.SinClasificar && d.Classification.Reason != NoSystemReason)
            .GroupBy(d => d.Item.SystemName ?? "", StringComparer.OrdinalIgnoreCase)
            .OrderBy(g => g.Key, StringComparer.CurrentCultureIgnoreCase);
        foreach (var g in unclassified)
        {
            int n = g.Count();
            result.Add(new AislDeviation(
                $"Sistema «{g.Key}» ({n} {(n == 1 ? "elemento" : "elementos")})",
                "Sin aislar: red sin clasificar",
                g.First().Classification.Reason,
                "Asigne la red del sistema en el diálogo (ACS, agua fría, clima calor, clima frío, clima mixto, frigorífica o excluida)."));
        }
        foreach (var d in decisions.Where(d => d.Action == AislAction.SinClasificar && d.Classification.Reason == NoSystemReason))
            result.Add(new AislDeviation(
                d.Item.ElementId.ToString(CultureInfo.InvariantCulture),
                "Sin aislar: elemento sin sistema",
                NoSystemReason,
                "Conecte el elemento a la red para que Revit le asigne un tipo de sistema de tuberías y vuelva a ejecutar."));
        return result;
    }
}
