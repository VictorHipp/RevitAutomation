using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace CadMepRevit.Core;

public enum PipeService { Unknown, Sanitary, Storm, Drain, Cold, Hot, Recirculation, Fire, Vent, Heating, Return, Gas, Irrigation }

public sealed record PipeNetworkDecision(int Id, IReadOnlyList<int> Segments, PipeService Service, string Reason, string? Material);

/// <summary>Interpreta las redes en planta antes de consultar los tipos disponibles en Revit.</summary>
public static class PipeNetworks
{
    private static readonly (PipeService Service, string Pattern)[] Rules =
    {
        (PipeService.Drain, "DRENAJE|DREN|DRAIN|DRAINAGE|DRN"),
        (PipeService.Storm, "PLUVIAL(?:ES)?|PLUV|STORM|STRM|LLUVIA(?:S)?"),
        (PipeService.Sanitary, "FECAL(?:ES)?|FEC|RESIDUAL(?:ES)?|SANEAM(?:IENTO)?|SANIT(?:ARIO[S]?|ARY)?|SAN|SANR|NEGRAS|WC"),
        (PipeService.Recirculation, @"RACS|RECIRC\w*|RETORNO[-_ ]+CALIENTE|HOT[-_ ]+WATER[-_ ]+RETURN"),
        (PipeService.Fire, @"PCI|BIES?|INCEND\w*|FIRE|ROCIA\w*|SPRINK\w*"),
        (PipeService.Hot, "ACS|CALIENTE|HOT"),
        (PipeService.Cold, "AF|AFS|AFC|FRIA|COLD"),
        (PipeService.Vent, @"VENTILACI\w*|VENT"),
        (PipeService.Return, "RETORNO|RETURN|RET"),
        (PipeService.Heating, @"CALEF\w*|IDA|HYDRONIC|SUPPLY"),
        (PipeService.Gas, "GAS"),
        (PipeService.Irrigation, "RIEGO|IRRIGATION")
    };

    public static string Normalize(string value) => string.Concat(value.Normalize(NormalizationForm.FormD)
        .Where(c => CharUnicodeInfo.GetUnicodeCategory(c) != UnicodeCategory.NonSpacingMark)).ToUpperInvariant();

    private static bool Word(string text, string pattern) => Regex.IsMatch(text, $@"(?<![A-Z])(?:{pattern})(?![A-Z])");
    public static PipeService Recognize(string value)
    {
        string words = Normalize(value);
        return Rules.FirstOrDefault(r => Word(words, r.Pattern)).Service;
    }

    public static bool IsDrainage(PipeService service) => service is PipeService.Sanitary or PipeService.Storm or PipeService.Drain;

    public static MepKind LayerKind(string layer)
    {
        var kind = MepKinds.Suggest(layer);
        if (!MepKinds.IsUnknownLayer(layer)) return kind;
        return Recognize(layer) is PipeService.Drain or PipeService.Sanitary or PipeService.Storm
            ? MepKind.Tuberia : kind;
    }

    internal static bool IsSanitaryFixture(CadBlockRef block) => block.Role == BlockRole.Sanitario &&
        (BlockNames.Role(block.BlockName) == BlockRole.Sanitario || UserRules.Current.BlockRule(block.BlockName) == BlockRole.Sanitario);

    public static P3 InPlan(P3 p) => new(p.X, p.Y, 0);
    internal static P3 ToMm(P3 p, double k, P3 offset) => InPlan(p * k + offset);

    public static List<PipeNetworkDecision> Infer(CadDrawing drawing, IReadOnlyList<NetSegment> pipes,
        IEnumerable<Junction> junctions, double k, P3 offset, double toleranceMm)
    {
        var components = NetworkComponents.Find(pipes.Select(p => p.Index), junctions);
        var owners = components.SelectMany((segments, i) => segments.Select(s => (s, i))).ToDictionary(x => x.s, x => x.i);
        var byIndex = pipes.ToDictionary(p => p.Index);
        var votes = components.Select(_ => new List<(PipeService Service, string? Material)>()).ToArray();
        foreach (var text in drawing.Texts)
        {
            var service = Recognize(text.Value);
            string? material = MaterialIn(text.Value);
            if (service == PipeService.Unknown && material is null) continue;
            var site = ToMm(text.Anchor ?? text.Center, k, offset);
            var owner = ClosestNetwork(pipes, owners, site, PlanDefaults.PipeSystemLabelMm, toleranceMm,
                p => text.Anchor is not null || IsPipeLabelLayer(text.Layer, p.Layer));
            if (owner is int network) votes[network].Add((service, material));
        }
        var fixtures = new HashSet<int>();
        foreach (var block in drawing.Blocks.Where(IsSanitaryFixture))
        {
            var site = ToMm(block.Position, k, offset);
            bool Touches(P3 p) => site.DistanceTo(p) <= toleranceMm ||
                block.Footprint is { } f && f.Contains((p.X - offset.X) / k, (p.Y - offset.Y) / k, toleranceMm / k);
            foreach (var p in pipes)
                if (Touches(InPlan(p.Start)) || Touches(InPlan(p.End))) fixtures.Add(owners[p.Index]);
        }
        return components.Select((ids, i) => Decide(drawing.Path, i + 1, ids,
            ids.Select(id => byIndex[id].Layer).Distinct(StringComparer.OrdinalIgnoreCase), votes[i], fixtures.Contains(i))).ToList();
    }

    public static PipeNetworkDecision InferLoose(CadDrawing drawing, int id, string layer, P3 site, double k, P3 offset, double tol)
    {
        var votes = drawing.Texts.Where(t => (t.Anchor is not null || IsPipeLabelLayer(t.Layer, layer)) &&
            ToMm(t.Anchor ?? t.Center, k, offset).DistanceTo(site) <= PlanDefaults.PipeSystemLabelMm)
            .Select(t => (Recognize(t.Value), MaterialIn(t.Value)));
        bool fixture = drawing.Blocks.Any(b => IsSanitaryFixture(b) &&
            (ToMm(b.Position, k, offset).DistanceTo(site) <= tol || b.Footprint is { } f &&
                f.Contains((site.X - offset.X) / k, (site.Y - offset.Y) / k, tol / k)));
        return Decide(drawing.Path, id, Array.Empty<int>(), new[] { layer }, votes, fixture);
    }

    private static PipeNetworkDecision Decide(string path, int id, IReadOnlyList<int> segments, IEnumerable<string> layers,
        IEnumerable<(PipeService Service, string? Material)> votes, bool fixture)
    {
        var labels = votes.ToList();
        string? material = Majority(labels.Select(v => v.Material).Where(m => m != null));
        var layer = Majority(layers.Select(Recognize).Where(s => s != PipeService.Unknown));
        if (layer != PipeService.Unknown) return new(id, segments, layer, "capa", material);
        var label = Majority(labels.Select(v => v.Service).Where(s => s != PipeService.Unknown));
        if (label != PipeService.Unknown) return new(id, segments, label, "mayoría de rótulos", material);
        if (fixture) return new(id, segments, PipeService.Sanitary, "aparato sanitario en un extremo", material);
        var file = Recognize(Path.GetFileNameWithoutExtension(path));
        if (file != PipeService.Unknown) return new(id, segments, file, "nombre del archivo", material);
        if (Word(Normalize(Path.GetFileNameWithoutExtension(path)), @"FONTANER\w*|PLUMBING") ||
            layers.Any(l => Word(Normalize(l), @"FONTANER\w*|PLUMBING|PLMB")))
            return new(id, segments, PipeService.Cold, "sistema SUPUESTO por la denominación de fontanería", material);
        return new(id, segments, PipeService.Unknown, "sistema SUPUESTO: capa y archivo sin una designación de sistema concluyente", material);
    }

    private static T? Majority<T>(IEnumerable<T> votes)
    {
        var groups = votes.GroupBy(t => t).OrderByDescending(g => g.Count()).Take(2).ToArray();
        return groups.Length != 0 && (groups.Length == 1 || groups[0].Count() > groups[1].Count()) ? groups[0].Key : default;
    }

    public static string? MaterialIn(string value)
    {
        string words = Normalize(value);
        return new[] { "PVC", "PP", "PEAD", "HDPE", "PE", "FUNDICION", "HORMIGON" }
            .FirstOrDefault(m => Word(words, m));
    }

    private static bool IsPipeLabelLayer(string textLayer, string pipeLayer)
    {
        if (string.Equals(textLayer, pipeLayer, StringComparison.OrdinalIgnoreCase)) return true;
        string layer = Normalize(textLayer);
        if (Regex.IsMatch(layer, @"^[ASE][-_ ]")) return false;
        return Word(layer, "PIPE|TUB\\w*|FONT\\w*|PLMB|PLUMBING") || Recognize(layer) != PipeService.Unknown ||
               Word(layer, @"(?:IDEN|ANNO|TXT|TEXT\w*|ROT\w*|LABEL\w*|TAG\w*)") &&
               Regex.Split(layer, "[^A-Z]+").Intersect(Regex.Split(Normalize(pipeLayer), "[^A-Z]+"))
                   .Any(w => w.Length >= 2);
    }

    private static int? ClosestNetwork(IEnumerable<NetSegment> pipes, Dictionary<int, int> owners, P3 site,
        double reach, double tol, Func<NetSegment, bool> accepts)
    {
        var ranked = pipes.Where(accepts)
            .Select(p => (Owner: owners[p.Index], D: NetworkBuilder.ProjectToSegment(site, InPlan(p.Start), InPlan(p.End)).Dist))
            .Where(p => p.D <= reach).GroupBy(p => p.Owner)
            .Select(g => (Owner: g.Key, D: g.Min(p => p.D))).OrderBy(p => p.D).Take(2).ToArray();
        return ranked.Length == 0 || ranked.Length == 2 && ranked[1].D <= Math.Max(ranked[0].D * 1.5, ranked[0].D + tol)
            ? null : ranked[0].Owner;
    }

    public static PipeService[] SystemPreferences(PipeService service) => service switch
    {
        PipeService.Drain => new[] { PipeService.Drain, PipeService.Storm, PipeService.Sanitary },
        PipeService.Storm => new[] { PipeService.Storm, PipeService.Sanitary },
        PipeService.Unknown => new[] { PipeService.Cold, PipeService.Sanitary, PipeService.Storm, PipeService.Drain, PipeService.Fire },
        _ => new[] { service }
    };

    public static string? DrainageType(IEnumerable<string> candidates, string? material)
    {
        var names = candidates.ToList();
        return (material is null ? null : names.FirstOrDefault(n => MaterialIn(n) == material)) ??
            names.FirstOrDefault(n => Regex.IsMatch(Normalize(n), "PVC|SANEAM"));
    }

    // Los catálogos se consultan por orden semántico: el respaldo nunca depende de la ordenación alfabética.
    public static string? SelectSystem(PipeService requested, IEnumerable<string> names)
    {
        var systems = names.Select(n => (Name: n, Service: Recognize(n))).ToList();
        foreach (var service in SystemPreferences(requested))
            if (systems.FirstOrDefault(s => s.Service == service).Name is string match) return match;
        return null;
    }
}
