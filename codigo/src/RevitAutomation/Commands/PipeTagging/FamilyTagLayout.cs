using System;
using System.Collections.Generic;
using System.Linq;

namespace RevitAutomation.Addin.Commands.PipeTagging;

// Pure logic for "Etiquetar familias seleccionadas" (lighting fixtures, extinguishers, equipment…).
// No Revit API; shares TagBox, PipeTagPlacement and the overlap checks with the pipe/duct/tray tools.

internal enum FamilyTagElementKind { FamilyInstance, LinearRun, RunFitting, NotLoadableFamily }

/// <summary>One point element to tag: its symbol in the view and the tag box measured in Revit at its first head.</summary>
internal sealed class FamilyTagRequest
{
    internal string Key { get; init; } = "";
    /// <summary>Box of the element's symbol in the view; the tag goes beside it, never on it.</summary>
    internal TagBox Target { get; init; }
    /// <summary>Where a leader points: the element's insertion point or the centre of its symbol.</summary>
    internal TagPoint Anchor { get; init; }
    internal TagPoint MeasuredHead { get; init; }
    internal TagBox MeasuredBox { get; init; }
}

internal static class FamilyTagRules
{
    /// <summary>Why a selected element is not tagged by this tool, or null when it must be tagged.</summary>
    internal static string? ExclusionReason(FamilyTagElementKind kind, bool alreadyTagged, bool hasTagType, bool seenInView)
    {
        switch (kind)
        {
            case FamilyTagElementKind.LinearRun:
                return "Es una tubería, conducto, bandeja o tubo: usa los botones 1, 2 o 3 de Etiquetado, que colocan la etiqueta a lo largo del tramo.";
            case FamilyTagElementKind.RunFitting:
                return "Es una pieza de unión de una red (codo, te, cruz, reducción…): no se etiqueta.";
            case FamilyTagElementKind.NotLoadableFamily:
                return "No es un ejemplar de familia cargable (muro, suelo, cota, línea…): esta herramienta etiqueta familias como luminarias, extintores o equipos.";
        }
        if (alreadyTagged) return "Ya tiene una etiqueta en esta vista; no se duplica.";
        if (!seenInView) return "No tiene símbolo visible en esta vista, así que no hay dónde colocar la etiqueta junto a él.";
        if (!hasTagType) return "No hay ninguna etiqueta cargada para su categoría (ni multicategoría), o elegiste «No etiquetar».";
        return null;
    }
}

internal static class FamilyTagLayout
{
    /// <summary>Directions around the symbol, in reading order: above, right, below, left, then the four corners.</summary>
    internal static readonly (int X, int Y)[] Sides =
    {
        (0, 1), (1, 0), (0, -1), (-1, 0), (1, 1), (1, -1), (-1, 1), (-1, -1)
    };
    internal const int MaxLeaderSteps = 3;

    /// <summary>Tag box centred at <paramref name="centre"/> (rigid translation of the measured box) and the matching head.</summary>
    internal static (TagPoint Head, TagBox Box) At(FamilyTagRequest request, TagPoint centre)
    {
        var shift = centre - request.MeasuredBox.Center;
        return (request.MeasuredHead + shift, request.MeasuredBox.Translate(shift));
    }

    /// <summary>
    /// Centre of the tag box touching the symbol on side <paramref name="side"/>, separated by <paramref name="distance"/>.
    /// </summary>
    internal static TagPoint CentreBeside(TagBox target, TagBox tag, (int X, int Y) side, double distance)
    {
        double halfW = tag.Width / 2, halfH = tag.Height / 2;
        double x = side.X switch
        {
            > 0 => target.MaxX + distance + halfW,
            < 0 => target.MinX - distance - halfW,
            _ => target.Center.X
        };
        double y = side.Y switch
        {
            > 0 => target.MaxY + distance + halfH,
            < 0 => target.MinY - distance - halfH,
            _ => target.Center.Y
        };
        return new TagPoint(x, y);
    }

    /// <summary>Ordered candidates: beside the symbol without leader, then further out with a leader to the element.</summary>
    internal static IEnumerable<PipeTagPlacement> Candidates(FamilyTagRequest request, double gap)
    {
        foreach (var side in Sides)
        {
            var (head, box) = At(request, CentreBeside(request.Target, request.MeasuredBox, side, gap));
            yield return new PipeTagPlacement { Key = request.Key, Head = head, Box = box, Kind = PipeTagPlacementKind.Beside, Target = request.Target };
        }
        double step = Math.Max(request.MeasuredBox.Width, request.MeasuredBox.Height) + gap;
        for (int k = 1; k <= MaxLeaderSteps; k++)
        {
            foreach (var side in Sides)
            {
                var (head, box) = At(request, CentreBeside(request.Target, request.MeasuredBox, side, gap + k * step));
                yield return new PipeTagPlacement
                {
                    Key = request.Key, Head = head, Box = box, Kind = PipeTagPlacementKind.OffsetWithLeader,
                    LeaderEnd = request.Anchor, Target = request.Target
                };
            }
        }
    }

    internal static PipeTagPlacement? Place(FamilyTagRequest request, IReadOnlyList<TagBox> obstacles, IReadOnlyList<PipeTagPlacement> placed, double gap, TagBox? limit)
        => Candidates(request, gap).FirstOrDefault(c => PipeTagLayout.IsFree(c, obstacles, placed, gap, limit));

    /// <summary>
    /// Places every request in a stable order (top to bottom, left to right, as the drawing is read).
    /// <paramref name="obstacles"/> must already contain every symbol box so no tag covers another element.
    /// </summary>
    internal static (List<PipeTagPlacement> Placed, List<string> Unplaced) PlaceAll(
        IEnumerable<FamilyTagRequest> requests, IReadOnlyList<TagBox> obstacles, double gap, TagBox? limit)
    {
        var placed = new List<PipeTagPlacement>();
        var unplaced = new List<string>();
        var obstacleIndex = PipeTagLayout.IndexObstacles(obstacles);
        var placedIndex = PipeTagLayout.IndexPlacements();
        foreach (var request in requests.OrderByDescending(r => Math.Round(r.Target.Center.Y, 3)).ThenBy(r => r.Target.Center.X).ThenBy(r => r.Key, StringComparer.Ordinal))
        {
            var placement = Place(request, obstacleIndex, placedIndex, gap, limit);
            if (placement is null) unplaced.Add(request.Key);
            else { placed.Add(placement); placedIndex.Add(placement); }
        }
        return (placed, unplaced);
    }
}
