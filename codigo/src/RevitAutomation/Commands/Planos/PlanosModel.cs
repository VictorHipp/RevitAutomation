using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.RegularExpressions;

namespace RevitAutomation.Addin.Commands.Planos;

// Pure logic of the PLANOS module (no Revit API: testable outside Revit and compatible with .NET Framework 4.8).
// Elements are classified by native Revit data — category, MEP system classification of their system type and the
// domain/system of their connectors — never by names. Category and enum values arrive as their API names
// (e.g. "OST_PipeCurves", "DomesticColdWater", "DomainPiping"). Lengths are in feet (Revit internal units) unless the
// name says mm (paper millimetres).

internal enum PlanosDiscipline { Fontaneria, Climatizacion, Electricidad, Iluminacion, Genericas }

/// <summary>Network of a plumbing plan. <see cref="Todo"/>: shown in every plan of its discipline (fixtures, equipment).</summary>
internal enum PlanosNetwork { Todo, AguaFriaAcs, Saneamiento, OtrasFontaneria }

/// <summary>What the run produces: views and sheets, views only, or sheets from the views of a previous run.</summary>
internal enum PlanosMode { Completo, SoloVistas, SoloLaminas }

/// <summary>Estilo visual de las vistas generadas (se traduce a DisplayStyle de Revit en PlanosBuilder).</summary>
internal enum PlanosStyle { LineasOcultas, Sombreado, ColoresCoherentes, Realista, Alambrico }

/// <summary>Nivel de detalle de las vistas generadas (ViewDetailLevel de Revit).</summary>
internal enum PlanosDetail { Bajo, Medio, Alto }

internal sealed class PlanosConnectorFact
{
    internal PlanosConnectorFact(string domain, string classification) { Domain = domain ?? ""; Classification = classification ?? ""; }
    internal string Domain { get; }
    internal string Classification { get; }
}

/// <summary>What the classifier needs to know about one model element.</summary>
internal sealed class PlanosElementFacts
{
    /// <summary>BuiltInCategory name, e.g. "OST_PipeCurves".</summary>
    internal string Category { get; set; } = "";
    /// <summary>MEPSystemClassification of the element's system type (pipes, ducts and their fittings), "" if none.</summary>
    internal string SystemClassification { get; set; } = "";
    internal List<PlanosConnectorFact> Connectors { get; } = new List<PlanosConnectorFact>();
    /// <summary>Family name (generic models: colour and legend).</summary>
    internal string FamilyName { get; set; } = "";
}

internal sealed class PlanosClassification
{
    /// <summary>null = not documented by any of the five buttons.</summary>
    internal PlanosDiscipline? Discipline { get; set; }
    internal PlanosNetwork Network { get; set; } = PlanosNetwork.Todo;
    /// <summary>Colour / legend key inside the discipline (system classification, category or family).</summary>
    internal string ColorKey { get; set; } = "";
    /// <summary>The discipline comes from the category alone (no system information, or a user-defined "Other" system).</summary>
    internal bool AssumedByCategory { get; set; }
}

internal static class PlanosClassifier
{
    internal static readonly string[] PipeCategories =
        { "OST_PipeCurves", "OST_PipeFitting", "OST_PipeAccessory", "OST_FlexPipeCurves", "OST_PipeInsulations", "OST_PlaceHolderPipes" };
    internal static readonly string[] DuctCategories =
        { "OST_DuctCurves", "OST_DuctFitting", "OST_DuctAccessory", "OST_FlexDuctCurves", "OST_DuctTerminal", "OST_DuctInsulations",
          "OST_DuctLinings", "OST_PlaceHolderDucts" };
    internal static readonly string[] ElectricalCategories =
        { "OST_CableTray", "OST_CableTrayFitting", "OST_Conduit", "OST_ConduitFitting", "OST_ElectricalEquipment", "OST_ElectricalFixtures",
          "OST_CommunicationDevices", "OST_DataDevices", "OST_SecurityDevices", "OST_TelephoneDevices", "OST_NurseCallDevices" };
    internal static readonly string[] LightingCategories = { "OST_LightingFixtures", "OST_LightingDevices" };
    /// <summary>Fire protection (the CAD «PCI» button): documented with the generic families, as the user asked.</summary>
    internal static readonly string[] FireCategories = { "OST_Sprinklers", "OST_FireProtection", "OST_FireAlarmDevices" };
    internal static readonly string[] PlumbingCategories = { "OST_PlumbingFixtures" };
    internal static readonly string[] MechanicalEquipment = { "OST_MechanicalEquipment" };
    internal static readonly string[] GenericCategories = { "OST_GenericModel" };
    /// <summary>Building elements kept (half-tone) as reference, or hidden when the user does not want context.</summary>
    internal static readonly string[] ContextCategories =
        { "OST_Walls", "OST_Floors", "OST_Roofs", "OST_Ceilings", "OST_Columns", "OST_StructuralColumns", "OST_StructuralFraming",
          "OST_StructuralFoundation", "OST_Stairs", "OST_Doors", "OST_Windows", "OST_CurtainWallPanels", "OST_CurtainWallMullions", "OST_Railings" };

    internal static readonly string[] ColdHotSystems = { "DomesticColdWater", "DomesticHotWater", "Recirculation" };
    internal static readonly string[] DrainageSystems = { "Sanitary", "Vent", "Storm" };
    internal static readonly string[] HydronicSystems = { "SupplyHydronic", "ReturnHydronic", "CondensateDrain" };
    internal static readonly string[] FireSystems = { "FireProtectWet", "FireProtectDry", "FireProtectPreaction", "FireProtectOther" };
    // "OtherPipe" is a user-defined system (gas, refrigerant, irrigation…): it does not say the discipline.
    internal const string OtherPipe = "OtherPipe";
    private static readonly string[] Undefined = { "", "UndefinedSystemClassification", "UndefinedSystemType", "Fitting", "Global" };

    /// <summary>Every category any of the five buttons documents.</summary>
    internal static IEnumerable<string> AllCategories =>
        PipeCategories.Concat(DuctCategories).Concat(ElectricalCategories).Concat(LightingCategories).Concat(FireCategories)
            .Concat(PlumbingCategories).Concat(MechanicalEquipment).Concat(GenericCategories);

    /// <summary>Categories whose elements can belong to <paramref name="d"/> (what its plans must show).</summary>
    internal static string[] CategoriesOf(PlanosDiscipline d) => d switch
    {
        PlanosDiscipline.Fontaneria => PipeCategories.Concat(PlumbingCategories).Concat(MechanicalEquipment).ToArray(),
        PlanosDiscipline.Climatizacion => DuctCategories.Concat(PipeCategories).Concat(MechanicalEquipment).ToArray(),
        PlanosDiscipline.Electricidad => ElectricalCategories,
        PlanosDiscipline.Iluminacion => LightingCategories,
        _ => GenericCategories.Concat(FireCategories).Concat(PipeCategories).Concat(MechanicalEquipment).ToArray()
    };

    /// <summary>Documented categories that the plans of <paramref name="d"/> hide (other disciplines).</summary>
    internal static string[] HiddenIn(PlanosDiscipline d)
    {
        var own = new HashSet<string>(CategoriesOf(d));
        return AllCategories.Distinct().Where(c => !own.Contains(c)).ToArray();
    }

    internal static bool IsContext(string category) => ContextCategories.Contains(category);

    /// <summary>
    /// Whether an element is drawn in the plan of discipline <paramref name="d"/> and network <paramref name="n"/>.
    /// Categories are shared (mechanical equipment, pipes without system…), so the element's own classification decides:
    /// only its discipline, and in plumbing only its network (fixtures and equipment, network «Todo», in every plan).
    /// </summary>
    internal static bool ShownIn(PlanosClassification c, PlanosDiscipline d, PlanosNetwork n) =>
        c.Discipline == d && (d != PlanosDiscipline.Fontaneria || n == PlanosNetwork.Todo || c.Network == PlanosNetwork.Todo || c.Network == n);

    /// <summary>Discipline (and plumbing network) of a pipe/duct system classification; null if it says nothing.</summary>
    internal static (PlanosDiscipline Discipline, PlanosNetwork Network)? OfSystem(string cls)
    {
        if (ColdHotSystems.Contains(cls)) return (PlanosDiscipline.Fontaneria, PlanosNetwork.AguaFriaAcs);
        if (DrainageSystems.Contains(cls)) return (PlanosDiscipline.Fontaneria, PlanosNetwork.Saneamiento);
        if (cls == OtherPipe) return (PlanosDiscipline.Fontaneria, PlanosNetwork.OtrasFontaneria);
        if (HydronicSystems.Contains(cls)) return (PlanosDiscipline.Climatizacion, PlanosNetwork.Todo);
        if (FireSystems.Contains(cls)) return (PlanosDiscipline.Genericas, PlanosNetwork.Todo);
        if (cls is "SupplyAir" or "ReturnAir" or "ExhaustAir" or "OtherAir") return (PlanosDiscipline.Climatizacion, PlanosNetwork.Todo);
        return null;
    }

    internal static PlanosClassification Classify(PlanosElementFacts e)
    {
        string cat = e.Category ?? "";
        string sys = e.SystemClassification ?? "";
        if (PipeCategories.Contains(cat))
        {
            if (FireSystems.Contains(sys)) return In(PlanosDiscipline.Genericas, "PCI · Tuberías");
            if (!Undefined.Contains(sys) && OfSystem(sys) is { } s)
                return new PlanosClassification { Discipline = s.Discipline, Network = s.Network, ColorKey = sys, AssumedByCategory = sys == OtherPipe };
            // Without a system (an unconnected fitting): its connectors may still say it.
            if (PipingFromConnectors(e.Connectors) is { } fromConnectors) return fromConnectors;
            return new PlanosClassification { Discipline = PlanosDiscipline.Fontaneria, Network = PlanosNetwork.OtrasFontaneria, ColorKey = OtherPipe, AssumedByCategory = true };
        }
        if (PlumbingCategories.Contains(cat)) return In(PlanosDiscipline.Fontaneria, "Aparatos sanitarios");
        if (cat == "OST_Sprinklers") return In(PlanosDiscipline.Genericas, "PCI · Rociadores");
        if (cat == "OST_FireProtection") return In(PlanosDiscipline.Genericas, "PCI · Protección contra incendios");
        if (cat == "OST_FireAlarmDevices") return In(PlanosDiscipline.Genericas, "PCI · Detección y alarma");
        if (DuctCategories.Contains(cat)) return In(PlanosDiscipline.Climatizacion, Undefined.Contains(sys) ? "OtherAir" : sys);
        if (cat == "OST_MechanicalEquipment") return Equipment(e.Connectors);
        if (ElectricalCategories.Contains(cat)) return In(PlanosDiscipline.Electricidad, cat);
        if (LightingCategories.Contains(cat)) return In(PlanosDiscipline.Iluminacion, cat);
        if (cat == "OST_GenericModel") return In(PlanosDiscipline.Genericas, string.IsNullOrWhiteSpace(e.FamilyName) ? "(sin familia)" : e.FamilyName);
        return new PlanosClassification();
    }

    /// <summary>
    /// Mechanical equipment: HVAC if any connector is air or hydronic; plumbing if its connectors are domestic water or
    /// drainage (water heaters, pumps); fire protection (PCI, with the generic families) if only fire connectors.
    /// Without readable connectors: HVAC by its category, marked as an assumption.
    /// </summary>
    private static PlanosClassification Equipment(List<PlanosConnectorFact> connectors)
    {
        bool hvac = connectors.Any(c => c.Domain == "DomainHvac" || (c.Domain == "DomainPiping" && HydronicSystems.Contains(c.Classification)));
        var plumbing = connectors.Where(c => c.Domain == "DomainPiping" && (ColdHotSystems.Contains(c.Classification) || DrainageSystems.Contains(c.Classification))).ToList();
        bool fire = connectors.Any(c => c.Domain == "DomainPiping" && FireSystems.Contains(c.Classification));
        if (hvac) return In(PlanosDiscipline.Climatizacion, "Equipos");
        if (plumbing.Count > 0) return In(PlanosDiscipline.Fontaneria, "Equipos");
        if (fire) return In(PlanosDiscipline.Genericas, "PCI · Equipos");
        return new PlanosClassification { Discipline = PlanosDiscipline.Climatizacion, ColorKey = "Equipos", AssumedByCategory = true };
    }

    private static PlanosClassification? PipingFromConnectors(List<PlanosConnectorFact> connectors)
    {
        var piping = connectors.Where(c => c.Domain == "DomainPiping" && !Undefined.Contains(c.Classification) && c.Classification != OtherPipe).ToList();
        if (piping.Count == 0) return null;
        var kinds = piping.Select(c => OfSystem(c.Classification)).ToList();
        if (kinds.Any(k => k is null)) return null;
        var first = kinds[0]!.Value;
        if (kinds.All(k => k!.Value.Discipline == first.Discipline && k.Value.Network == first.Network))
            return first.Discipline == PlanosDiscipline.Genericas
                ? In(PlanosDiscipline.Genericas, "PCI · Tuberías")
                : new PlanosClassification { Discipline = first.Discipline, Network = first.Network, ColorKey = piping[0].Classification };
        return null; // mixed: decided by category
    }

    private static PlanosClassification In(PlanosDiscipline d, string key) => new PlanosClassification { Discipline = d, ColorKey = key };
}

/// <summary>Technical colours and their legend labels.</summary>
internal static class PlanosPalette
{
    private static readonly Dictionary<string, (byte R, byte G, byte B, string Label)> Fixed = new Dictionary<string, (byte, byte, byte, string)>(StringComparer.Ordinal)
    {
        ["DomesticColdWater"] = (0, 112, 192, "Agua fría"),
        ["DomesticHotWater"] = (215, 40, 40, "Agua caliente sanitaria"),
        ["Recirculation"] = (200, 90, 160, "Retorno de ACS"),
        ["Sanitary"] = (125, 80, 40, "Saneamiento"),
        ["Vent"] = (110, 160, 60, "Ventilación de saneamiento"),
        ["Storm"] = (40, 150, 150, "Pluviales"),
        ["OtherPipe"] = (110, 118, 128, "Otras tuberías (sistema «Otro» o sin sistema)"),
        ["Aparatos sanitarios"] = (90, 110, 140, "Aparatos sanitarios"),
        ["SupplyAir"] = (60, 140, 230, "Aire de impulsión"),
        ["ReturnAir"] = (190, 70, 190, "Aire de retorno"),
        ["ExhaustAir"] = (225, 160, 30, "Aire de extracción"),
        ["OtherAir"] = (130, 135, 140, "Otros conductos / sin sistema"),
        ["SupplyHydronic"] = (230, 110, 30, "Agua de climatización: impulsión"),
        ["ReturnHydronic"] = (120, 70, 200, "Agua de climatización: retorno"),
        ["CondensateDrain"] = (80, 170, 170, "Condensados"),
        ["Equipos"] = (70, 75, 85, "Equipos"),
        ["FireProtectWet"] = (200, 0, 0, "PCI: tubería húmeda"),
        ["FireProtectDry"] = (200, 0, 0, "PCI: tubería seca"),
        ["FireProtectPreaction"] = (200, 0, 0, "PCI: preacción"),
        ["FireProtectOther"] = (200, 0, 0, "PCI: otras"),
        ["OST_CableTray"] = (230, 140, 30, "Bandejas"),
        ["OST_CableTrayFitting"] = (230, 140, 30, "Bandejas"),
        ["OST_Conduit"] = (200, 170, 40, "Tubos eléctricos"),
        ["OST_ConduitFitting"] = (200, 170, 40, "Tubos eléctricos"),
        ["OST_ElectricalEquipment"] = (180, 40, 40, "Cuadros y equipos eléctricos"),
        ["OST_ElectricalFixtures"] = (40, 120, 200, "Tomas y aparatos eléctricos"),
        ["OST_CommunicationDevices"] = (60, 160, 90, "Comunicaciones"),
        ["OST_DataDevices"] = (60, 160, 90, "Comunicaciones"),
        ["OST_TelephoneDevices"] = (60, 160, 90, "Comunicaciones"),
        ["OST_SecurityDevices"] = (150, 60, 160, "Seguridad"),
        ["OST_NurseCallDevices"] = (150, 60, 160, "Seguridad"),
        ["OST_LightingFixtures"] = (225, 170, 0, "Luminarias"),
        ["OST_LightingDevices"] = (120, 115, 40, "Mandos de iluminación"),
        ["PCI · Tuberías"] = (200, 0, 0, "PCI: tuberías"),
        ["PCI · Rociadores"] = (230, 50, 50, "PCI: rociadores"),
        ["PCI · Protección contra incendios"] = (180, 20, 20, "PCI: extintores, BIE y equipos"),
        ["PCI · Detección y alarma"] = (240, 90, 60, "PCI: detección y alarma"),
        ["PCI · Equipos"] = (150, 0, 0, "PCI: equipos"),
    };

    /// <summary>Distinct colours for generic families (alphabetical order of family), then repeated.</summary>
    internal static readonly (byte R, byte G, byte B)[] Families =
    {
        (31, 119, 180), (255, 127, 14), (44, 160, 44), (148, 103, 189), (140, 86, 75),
        (227, 119, 194), (127, 127, 127), (188, 189, 34), (23, 190, 207), (57, 59, 121)
    };

    /// <summary>Colour and label for each key. Unknown keys (generic families) get the family palette in alphabetical order.</summary>
    internal static Dictionary<string, (byte R, byte G, byte B, string Label)> Legend(IEnumerable<string> keys)
    {
        var result = new Dictionary<string, (byte, byte, byte, string)>(StringComparer.Ordinal);
        int next = 0;
        foreach (string key in keys.Distinct(StringComparer.Ordinal).OrderBy(k => k, StringComparer.CurrentCultureIgnoreCase))
        {
            if (Fixed.TryGetValue(key, out var c)) { result[key] = c; continue; }
            var f = Families[next++ % Families.Length];
            result[key] = (f.R, f.G, f.B, "Familia «" + key + "»");
        }
        return result;
    }
}

internal static class PlanosNaming
{
    internal static string DisciplineLabel(PlanosDiscipline d) => d switch
    {
        PlanosDiscipline.Fontaneria => "Fontanería",
        PlanosDiscipline.Climatizacion => "Climatización",
        PlanosDiscipline.Electricidad => "Electricidad",
        PlanosDiscipline.Iluminacion => "Iluminación",
        _ => "Familias genéricas y PCI"
    };

    /// <summary>Sheet number prefix of each discipline (F-01, C-01, E-01, I-01, FG-01).</summary>
    internal static string Prefix(PlanosDiscipline d) => d switch
    {
        PlanosDiscipline.Fontaneria => "F",
        PlanosDiscipline.Climatizacion => "C",
        PlanosDiscipline.Electricidad => "E",
        PlanosDiscipline.Iluminacion => "I",
        _ => "FG"
    };

    internal static string NetworkLabel(PlanosNetwork n) => n switch
    {
        PlanosNetwork.AguaFriaAcs => "Agua fría y ACS",
        PlanosNetwork.Saneamiento => "Saneamiento y evacuación",
        PlanosNetwork.OtrasFontaneria => "Otras redes de fontanería",
        _ => ""
    };

    /// <summary>Title of the plan: "Planta de agua fría y ACS", "Planta de climatización"…</summary>
    internal static string PlanTitle(PlanosDiscipline d, PlanosNetwork n) => d switch
    {
        PlanosDiscipline.Fontaneria => n == PlanosNetwork.Todo ? "Planta de fontanería" : "Planta de " + Lower(NetworkLabel(n)),
        PlanosDiscipline.Climatizacion => "Planta de climatización",
        PlanosDiscipline.Electricidad => "Planta eléctrica",
        PlanosDiscipline.Iluminacion => "Planta de iluminación",
        _ => "Familias genéricas y PCI"
    };

    private static string Lower(string s) => s.Length == 0 ? s : char.ToLowerInvariant(s[0]) + s.Substring(1);

    internal static string Part(int index, int count) => count <= 1 ? "" : $" (parte {index + 1} de {count})";

    internal static string SheetName(PlanosDiscipline d, PlanosNetwork n, string level, int part, int parts) =>
        PlanTitle(d, n) + " - " + level + Part(part, parts);

    internal static string ViewName(PlanosDiscipline d, PlanosNetwork n, string level, int part, int parts) =>
        DisciplineLabel(d) + (n == PlanosNetwork.Todo ? "" : " - " + NetworkLabel(n)) + " - " + level + Part(part, parts);

    internal static string LegendSheetName(PlanosDiscipline d) => "Leyendas y cuadros - " + DisciplineLabel(d);
    internal static string LegendViewName(PlanosDiscipline d) => DisciplineLabel(d) + " - Leyenda de colores";

    /// <summary><paramref name="wanted"/> if free; otherwise "wanted (2)", "wanted (3)"…</summary>
    internal static string UniqueName(string wanted, ICollection<string> existing)
    {
        if (!existing.Contains(wanted)) return wanted;
        for (int i = 2; ; i++)
        {
            string candidate = wanted + " (" + i.ToString(CultureInfo.InvariantCulture) + ")";
            if (!existing.Contains(candidate)) return candidate;
        }
    }
}

/// <summary>Sheet numbering convention found in the project ("F-01", "E.001", "I01"…).</summary>
internal sealed class SheetNumbering
{
    internal SheetNumbering(string separator, int digits) { Separator = separator ?? "-"; Digits = Math.Max(1, Math.Min(4, digits)); }
    internal string Separator { get; }
    internal int Digits { get; }
    /// <summary>True when it comes from the project's own sheets (otherwise it is the default "-" and 2 digits).</summary>
    internal bool Detected { get; private set; }

    private static readonly Regex Pattern = new Regex(@"^([A-Za-z]{1,4})([-._ ]?)(\d{1,4})$", RegexOptions.CultureInvariant);

    internal static SheetNumbering Detect(IEnumerable<string> existing)
    {
        var votes = existing.Select(n => Pattern.Match((n ?? "").Trim())).Where(m => m.Success)
            .GroupBy(m => (m.Groups[2].Value, m.Groups[3].Value.Length)).OrderByDescending(g => g.Count()).ThenBy(g => g.Key.Item1).FirstOrDefault();
        if (votes is null) return new SheetNumbering("-", 2);
        return new SheetNumbering(votes.Key.Item1, votes.Key.Item2) { Detected = true };
    }

    internal string Format(string prefix, int n) => prefix + Separator + n.ToString(new string('0', Digits), CultureInfo.InvariantCulture);

    /// <summary>The next <paramref name="count"/> free numbers for <paramref name="prefix"/>, starting at 1 and skipping used ones.</summary>
    internal List<string> Next(string prefix, int count, ICollection<string> used)
    {
        var result = new List<string>();
        var taken = new HashSet<string>(used, StringComparer.OrdinalIgnoreCase);
        for (int n = 1; result.Count < count && n < 10000; n++)
        {
            string s = Format(prefix, n);
            if (taken.Contains(s)) continue;
            result.Add(s);
            taken.Add(s);
        }
        return result;
    }
}

/// <summary>Rectangle in plan (feet) or on paper (mm), Min ≤ Max.</summary>
internal readonly struct PlanosRect
{
    internal PlanosRect(double minX, double minY, double maxX, double maxY) { MinX = minX; MinY = minY; MaxX = maxX; MaxY = maxY; }
    internal double MinX { get; }
    internal double MinY { get; }
    internal double MaxX { get; }
    internal double MaxY { get; }
    internal double Width => MaxX - MinX;
    internal double Height => MaxY - MinY;
    internal bool IsValid => MaxX >= MinX && MaxY >= MinY && !double.IsNaN(MinX + MinY + MaxX + MaxY) && !double.IsInfinity(MinX + MinY + MaxX + MaxY);
    internal PlanosRect Grow(double d) => new PlanosRect(MinX - d, MinY - d, MaxX + d, MaxY + d);
    internal bool Contains(PlanosRect r, double tol = 1e-9) => r.MinX >= MinX - tol && r.MinY >= MinY - tol && r.MaxX <= MaxX + tol && r.MaxY <= MaxY + tol;
    internal bool Overlaps(PlanosRect r) => r.MinX < MaxX && MinX < r.MaxX && r.MinY < MaxY && MinY < r.MaxY;

    internal static PlanosRect? Union(IEnumerable<PlanosRect> rects)
    {
        bool any = false;
        double a = double.MaxValue, b = double.MaxValue, c = double.MinValue, d = double.MinValue;
        foreach (var r in rects)
        {
            if (!r.IsValid) continue;
            any = true;
            a = Math.Min(a, r.MinX); b = Math.Min(b, r.MinY); c = Math.Max(c, r.MaxX); d = Math.Max(d, r.MaxY);
        }
        return any ? new PlanosRect(a, b, c, d) : (PlanosRect?)null;
    }
}

/// <summary>Immutable XY bounds tree. Query results retain the original inventory order.</summary>
internal sealed class PlanosBoundsIndex
{
    private sealed class Node
    {
        internal PlanosRect Box;
        internal int Start, Count;
        internal Node? Left, Right;
    }

    private readonly PlanosRect[] _boxes;
    private readonly int[] _order;
    private readonly int[] _fallback;
    private readonly Node? _root;

    internal PlanosBoundsIndex(IEnumerable<PlanosRect> boxes)
    {
        _boxes = boxes.ToArray();
        _order = Enumerable.Range(0, _boxes.Length).Where(i => _boxes[i].IsValid).ToArray();
        _fallback = Enumerable.Range(0, _boxes.Length).Where(i => !_boxes[i].IsValid).ToArray();
        _root = Build(0, _order.Length, false);
    }

    private Node? Build(int start, int count, bool y)
    {
        if (count == 0) return null;
        var box = _boxes[_order[start]];
        double minX = box.MinX, minY = box.MinY, maxX = box.MaxX, maxY = box.MaxY;
        for (int i = start + 1; i < start + count; i++)
        {
            var b = _boxes[_order[i]];
            minX = Math.Min(minX, b.MinX); minY = Math.Min(minY, b.MinY);
            maxX = Math.Max(maxX, b.MaxX); maxY = Math.Max(maxY, b.MaxY);
        }
        var node = new Node { Box = new PlanosRect(minX, minY, maxX, maxY), Start = start, Count = count };
        if (count <= 16) return node;
        Array.Sort(_order, start, count, Comparer<int>.Create((a, b) =>
        {
            int c = (y ? _boxes[a].MinY : _boxes[a].MinX).CompareTo(y ? _boxes[b].MinY : _boxes[b].MinX);
            return c != 0 ? c : a.CompareTo(b);
        }));
        int half = count / 2;
        node.Left = Build(start, half, !y);
        node.Right = Build(start + half, count - half, !y);
        return node;
    }

    // Deliberately inclusive: touching a crop edge was included by the original visibility loop.
    private static bool Reaches(PlanosRect b, PlanosRect crop) =>
        !(b.MaxX < crop.MinX || b.MinX > crop.MaxX || b.MaxY < crop.MinY || b.MinY > crop.MaxY);

    internal List<int> Query(PlanosRect crop)
    {
        var result = new List<int>();
        // Preserve the original comparison semantics even for malformed/non-finite rectangles.
        if (!crop.IsValid || (_root is not null && crop.Contains(_root.Box, 0)))
        {
            for (int i = 0; i < _boxes.Length; i++) if (Reaches(_boxes[i], crop)) result.Add(i);
            return result;
        }
        Visit(_root, crop, result);
        foreach (int i in _fallback) if (Reaches(_boxes[i], crop)) result.Add(i);
        if (result.Count > _boxes.Length / 8)
        {
            // Dense queries must not turn the old linear scan into a large comparison sort.
            var included = new bool[_boxes.Length];
            foreach (int i in result) included[i] = true;
            result.Clear();
            for (int i = 0; i < included.Length; i++) if (included[i]) result.Add(i);
        }
        else result.Sort();
        return result;
    }

    private void Visit(Node? node, PlanosRect crop, List<int> result)
    {
        if (node is null || !Reaches(node.Box, crop)) return;
        if (node.Left is not null)
        {
            Visit(node.Left, crop, result); Visit(node.Right, crop, result);
            return;
        }
        for (int j = node.Start; j < node.Start + node.Count; j++)
        {
            int i = _order[j];
            if (Reaches(_boxes[i], crop)) result.Add(i);
        }
    }
}

/// <summary>Paper formats (landscape, mm).</summary>
internal static class PaperFormats
{
    internal static readonly (string Name, double W, double H)[] All =
    {
        ("A0", 1189, 841), ("A1", 841, 594), ("A2", 594, 420), ("A3", 420, 297), ("A4", 297, 210)
    };

    /// <summary>The ISO format closest to a measured sheet (either orientation), or null if none is within 3 %.</summary>
    internal static string? Closest(double wMm, double hMm)
    {
        double w = Math.Max(wMm, hMm), h = Math.Min(wMm, hMm);
        foreach (var f in All)
            if (Math.Abs(w - f.W) / f.W < 0.03 && Math.Abs(h - f.H) / f.H < 0.03) return f.Name;
        return null;
    }
}

/// <summary>Scale and division of a plan so that it fits the drawing area of the sheet.</summary>
internal static class PlanosScale
{
    internal const double MmPerFoot = 304.8;
    internal static readonly int[] Standard = { 50, 75, 100, 150, 200 };
    /// <summary>Space under the view for its title on the sheet (mm).</summary>
    internal const double TitleMm = 15;
    /// <summary>Margin around the elements in the plan (1.5 m, in feet).</summary>
    internal const double CropMarginFeet = 1.5 / 0.3048;

    internal static double PaperMm(double feet, int scale) => feet * MmPerFoot / scale;

    internal static bool Fits(PlanosRect crop, int scale, double areaWMm, double areaHMm) =>
        PaperMm(crop.Width, scale) <= areaWMm && PaperMm(crop.Height, scale) + TitleMm <= areaHMm;

    /// <summary>
    /// Scale and tiles for one plan. Automatic (<paramref name="preferred"/> = 0): the most detailed standard scale that
    /// fits; if not even 1:200 fits, the plan is divided at 1:100. A chosen scale is kept and the plan divided if needed.
    /// Tiles are equal parts of the crop, in reading order (top-left first).
    /// </summary>
    internal static (int Scale, List<PlanosRect> Tiles) Choose(PlanosRect crop, int preferred, double areaWMm, double areaHMm)
    {
        if (areaWMm <= 10 || areaHMm <= TitleMm + 10) throw new ArgumentException("La zona de dibujo de la lámina es demasiado pequeña.");
        if (preferred <= 0)
        {
            foreach (int s in Standard)
                if (Fits(crop, s, areaWMm, areaHMm)) return (s, new List<PlanosRect> { crop });
            preferred = 100;
        }
        else if (Fits(crop, preferred, areaWMm, areaHMm)) return (preferred, new List<PlanosRect> { crop });

        int nx = Math.Max(1, (int)Math.Ceiling(PaperMm(crop.Width, preferred) / areaWMm));
        int ny = Math.Max(1, (int)Math.Ceiling(PaperMm(crop.Height, preferred) / (areaHMm - TitleMm)));
        double w = crop.Width / nx, h = crop.Height / ny;
        var tiles = new List<PlanosRect>();
        for (int j = ny - 1; j >= 0; j--)
            for (int i = 0; i < nx; i++)
                tiles.Add(new PlanosRect(crop.MinX + i * w, crop.MinY + j * h, crop.MinX + (i + 1) * w, crop.MinY + (j + 1) * h));
        return (preferred, tiles);
    }
}

/// <summary>Level of an element when Revit gives none: the highest level at or below its height (+0.10 m tolerance, so a
/// pipe in the ceiling void stays on its own floor and a floor-mounted element a few cm low is not sent downstairs).</summary>
internal static class PlanosLevels
{
    internal const double ToleranceFeet = 0.10 / 0.3048;

    /// <summary>Index into <paramref name="elevations"/> (sorted ascending, feet); the lowest level if it is below all.</summary>
    internal static int Assign(double z, IReadOnlyList<double> elevations)
    {
        if (elevations.Count == 0 || double.IsNaN(z)) return -1;
        int best = 0;
        for (int i = 0; i < elevations.Count; i++) if (elevations[i] <= z + ToleranceFeet) best = i;
        return best;
    }
}

/// <summary>Shelf packing of measured boxes (legends, schedules) into sheet areas, without overlaps.</summary>
internal static class PlanosPacker
{
    internal readonly struct Placement
    {
        internal Placement(int page, double x, double y, bool oversize) { Page = page; X = x; Y = y; Oversize = oversize; }
        /// <summary>0-based sheet index.</summary>
        internal int Page { get; }
        /// <summary>Top-left corner, measured from the top-left of the area (mm, Y downwards).</summary>
        internal double X { get; }
        internal double Y { get; }
        /// <summary>Larger than the whole area: placed alone on its sheet, it will stick out.</summary>
        internal bool Oversize { get; }
    }

    internal static List<Placement> Pack(IReadOnlyList<(double W, double H)> boxes, double areaW, double areaH, double gap)
    {
        var result = new List<Placement>();
        int page = 0;
        double x = 0, y = 0, shelf = 0;
        bool pageUsed = false;
        foreach (var (w, h) in boxes)
        {
            if (w > areaW || h > areaH)
            {
                if (pageUsed) { page++; }
                result.Add(new Placement(page, 0, 0, true));
                page++; x = 0; y = 0; shelf = 0; pageUsed = false;
                continue;
            }
            if (x > 0 && x + w > areaW) { x = 0; y += shelf + gap; shelf = 0; }
            if (y + h > areaH) { page++; x = 0; y = 0; shelf = 0; }
            result.Add(new Placement(page, x, y, false));
            pageUsed = true;
            x += w + gap;
            shelf = Math.Max(shelf, h);
        }
        return result;
    }

    internal static int Pages(IEnumerable<Placement> placements) => placements.Select(p => p.Page).DefaultIfEmpty(-1).Max() + 1;
}

/// <summary>Where to put tags: longest runs first, never two tags closer than the given spacing.</summary>
internal static class PlanosTagPicker
{
    internal sealed class Candidate
    {
        internal Candidate(int index, double x, double y, double priority) { Index = index; X = x; Y = y; Priority = priority; }
        internal int Index { get; }
        internal double X { get; }
        internal double Y { get; }
        /// <summary>Higher first (pipe/duct length; equipment get a fixed priority).</summary>
        internal double Priority { get; }
    }

    internal static List<int> Pick(IEnumerable<Candidate> candidates, double minSpacing, int max)
    {
        var chosen = new List<Candidate>();
        double s2 = minSpacing * minSpacing;
        foreach (var c in candidates.OrderByDescending(c => c.Priority).ThenBy(c => c.Index))
        {
            if (chosen.Count >= max) break;
            if (chosen.Any(o => (o.X - c.X) * (o.X - c.X) + (o.Y - c.Y) * (o.Y - c.Y) < s2)) continue;
            chosen.Add(c);
        }
        return chosen.Select(c => c.Index).ToList();
    }
}

// ───────────────────────────── Planning: what will be generated (shown before anything is created) ─────────────────────────────

internal sealed class PlanosLevel
{
    internal PlanosLevel(string key, string name, double elevation) { Key = key; Name = name; Elevation = elevation; }
    internal string Key { get; }
    internal string Name { get; }
    internal double Elevation { get; }
}

/// <summary>One documented element, reduced to what the planner needs.</summary>
internal sealed class PlanosItem
{
    internal PlanosDiscipline Discipline { get; set; }
    internal PlanosNetwork Network { get; set; }
    /// <summary>Index in the sorted level list; -1 when the project has no levels.</summary>
    internal int Level { get; set; } = -1;
    internal PlanosRect Box { get; set; }
}

internal sealed class PlanosOptions
{
    internal List<PlanosDiscipline> Disciplines { get; } = new List<PlanosDiscipline>();
    internal HashSet<string> LevelKeys { get; } = new HashSet<string>(StringComparer.Ordinal);
    /// <summary>0 = automatic.</summary>
    internal int Scale { get; set; }
    internal double PaperWMm { get; set; } = 841;
    internal double PaperHMm { get; set; } = 594;
    /// <summary>Margin inside the sheet border (mm) and bands kept free for the title block.</summary>
    internal double MarginMm { get; set; } = 10;
    internal double ReserveBottomMm { get; set; } = 50;
    internal double ReserveRightMm { get; set; }
    internal bool Context { get; set; } = true;
    internal bool Schedules { get; set; } = true;
    internal bool Tags { get; set; } = true;
    internal PlanosMode Mode { get; set; } = PlanosMode.Completo;
    internal SheetNumbering Numbering { get; set; } = new SheetNumbering("-", 2);
    /// <summary>Visualización («render») de las vistas en planta y 3D generadas.</summary>
    internal PlanosStyle Style { get; set; } = PlanosStyle.LineasOcultas;
    internal PlanosDetail Detail { get; set; } = PlanosDetail.Medio;
    /// <summary>Además de las plantas, una vista 3D isométrica por disciplina (solo sus instalaciones), con su lámina.</summary>
    internal bool View3D { get; set; }
    /// <summary>Al terminar, PDF combinado de las láminas (o de las vistas, en «Solo vistas») en el Escritorio.</summary>
    internal bool Pdf { get; set; } = true;

    /// <summary>Drawing area of the sheet (mm).</summary>
    internal double AreaWMm => PaperWMm - 2 * MarginMm - ReserveRightMm;
    internal double AreaHMm => PaperHMm - 2 * MarginMm - ReserveBottomMm;
}

internal sealed class PlannedView
{
    internal PlanosDiscipline Discipline { get; set; }
    internal PlanosNetwork Network { get; set; }
    internal int Level { get; set; }
    internal string LevelName { get; set; } = "";
    internal string Name { get; set; } = "";
    internal string SheetName { get; set; } = "";
    internal int Scale { get; set; }
    internal PlanosRect Crop { get; set; }
    internal int Part { get; set; }
    internal int Parts { get; set; } = 1;
    internal int Elements { get; set; }
}

internal sealed class PlanosPlan
{
    internal List<PlannedView> Views { get; } = new List<PlannedView>();
    /// <summary>Disciplines that get a «Leyendas y cuadros» sheet (at least one; more only if the content does not fit).</summary>
    internal List<PlanosDiscipline> LegendSheets { get; } = new List<PlanosDiscipline>();
    internal List<string> Notes { get; } = new List<string>();
    internal int PlanSheets => Views.Count;
}

internal static class PlanosPlanner
{
    internal static readonly PlanosNetwork[] PlumbingNetworks = { PlanosNetwork.AguaFriaAcs, PlanosNetwork.Saneamiento, PlanosNetwork.OtrasFontaneria };

    /// <summary>
    /// One plan per discipline, level and (plumbing) network that has elements; nothing for empty combinations. Fixtures
    /// and equipment (network «Todo») appear in every plumbing plan of their level; a level with only those gets one
    /// «Planta de fontanería».
    /// </summary>
    internal static PlanosPlan Plan(IReadOnlyList<PlanosLevel> levels, IReadOnlyList<PlanosItem> items, PlanosOptions options)
    {
        var plan = new PlanosPlan();
        if (options.AreaWMm < 60 || options.AreaHMm < 60)
        {
            plan.Notes.Add($"La zona de dibujo de la lámina ({options.AreaWMm:0} × {options.AreaHMm:0} mm) es demasiado pequeña: reduzca los márgenes o la reserva del cajetín, o elija un formato mayor.");
            return plan;
        }
        int unlevelled = items.Count(i => i.Level < 0 && options.Disciplines.Contains(i.Discipline));
        if (unlevelled > 0) plan.Notes.Add($"{unlevelled} elementos sin nivel ni altura medible no se pueden situar en ninguna planta.");

        // Lookup groups retain input order, including duplicate disciplines in the options.
        var byLevel = items.ToLookup(i => (i.Discipline, i.Level));
        foreach (var d in options.Disciplines)
        {
            for (int l = 0; l < levels.Count; l++)
            {
                if (!options.LevelKeys.Contains(levels[l].Key)) continue;
                var onLevel = byLevel[(d, l)].ToList();
                if (onLevel.Count == 0) continue;
                var networks = d == PlanosDiscipline.Fontaneria
                    ? PlumbingNetworks.Where(n => onLevel.Any(i => i.Network == n)).ToList()
                    : new List<PlanosNetwork> { PlanosNetwork.Todo };
                if (networks.Count == 0) networks.Add(PlanosNetwork.Todo);
                foreach (var n in networks)
                {
                    var shown = onLevel.Where(i => n == PlanosNetwork.Todo || i.Network == n || i.Network == PlanosNetwork.Todo).ToList();
                    if (PlanosRect.Union(shown.Select(i => i.Box)) is not { } box) continue;
                    var crop = box.Grow(PlanosScale.CropMarginFeet);
                    var (scale, tiles) = PlanosScale.Choose(crop, options.Scale, options.AreaWMm, options.AreaHMm);
                    for (int t = 0; t < tiles.Count; t++)
                        plan.Views.Add(new PlannedView
                        {
                            Discipline = d, Network = n, Level = l, LevelName = levels[l].Name, Scale = scale, Crop = tiles[t], Part = t, Parts = tiles.Count,
                            Elements = shown.Count,
                            Name = PlanosNaming.ViewName(d, n, levels[l].Name, t, tiles.Count),
                            SheetName = PlanosNaming.SheetName(d, n, levels[l].Name, t, tiles.Count)
                        });
                    if (tiles.Count > 1)
                        plan.Notes.Add($"{PlanosNaming.PlanTitle(d, n)} - {levels[l].Name}: no cabe en una lámina a 1:{scale}; se divide en {tiles.Count} partes.");
                }
            }
            if (options.Schedules && plan.Views.Any(v => v.Discipline == d)) plan.LegendSheets.Add(d);
        }
        return plan;
    }
}
