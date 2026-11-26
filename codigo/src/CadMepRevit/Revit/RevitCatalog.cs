using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Electrical;
using Autodesk.Revit.DB.Mechanical;
using Autodesk.Revit.DB.Plumbing;
using CadMepRevit.Core;

namespace CadMepRevit.Revit;

/// <summary>Elemento con nombre para mostrar en listas desplegables.</summary>
public sealed record NamedId(string Name, ElementId Id)
{
    public override string ToString() => Name;
}

/// <summary>
/// Lee del proyecto abierto los niveles, tipos y sistemas disponibles.
/// Solo lectura: no modifica el modelo.
/// </summary>
public sealed class RevitCatalog
{
    private Document? _document;
    /// <summary>Exterior definitivo antes de resolver cotas inferiores; mismas medidas del segmento del tipo.</summary>
    public double PipeOuterMm(ElementId? typeId, double nominalMm, double? drawnOuterMm, bool useNominal, out bool assumed)
    {
        assumed = false;
        if (_document?.GetElement(typeId) is PipeType type)
        {
            var routing = type.RoutingPreferenceManager;
            for (int i = 0; i < routing.GetNumberOfRules(RoutingPreferenceRuleGroupType.Segments); i++)
            {
                var rule = routing.GetRule(RoutingPreferenceRuleGroupType.Segments, i);
                if (_document.GetElement(rule.MEPPartId) is not PipeSegment segment) continue;
                var sizes = segment.GetSizes().ToList();
                if (sizes.Count == 0) continue;
                double Mm(double ft) => UnitUtils.ConvertFromInternalUnits(ft, UnitTypeId.Millimeters);
                var best = sizes.OrderBy(s => useNominal || drawnOuterMm == null ? Math.Abs(Mm(s.NominalDiameter) - nominalMm) :
                    Math.Min(Math.Abs(Mm(s.OuterDiameter) - drawnOuterMm.Value), Math.Abs(Mm(s.NominalDiameter) - drawnOuterMm.Value) + .01)).First();
                return Mm(best.OuterDiameter);
            }
        }
        assumed = drawnOuterMm == null;
        return drawnOuterMm ?? nominalMm;
    }
    public List<(string Name, ElementId Id, double ElevationFt)> Levels { get; } = new();
    public Dictionary<MepKind, List<NamedId>> Types { get; } = new();
    public Dictionary<MepKind, List<NamedId>> Systems { get; } = new();
    public Dictionary<ElementId, MEPSystemClassification> PipeClassifications { get; } = new();
    /// <summary>Formas de los tipos de conducto (para saber si llevan diámetro o ancho×alto).</summary>
    public Dictionary<ElementId, ConnectorProfileType> DuctShapes { get; } = new();
    /// <summary>Espesor (mm) de cada tipo de muro básico.</summary>
    public Dictionary<ElementId, double> WallWidthsMm { get; } = new();
    public List<NamedId> FamilySymbols { get; } = new();
    /// <summary>Familia y tipo de cada símbolo de <see cref="FamilySymbols"/>, para buscar por nombre de bloque.</summary>
    public List<(string Family, string Type, ElementId Id, string Display)> Symbols { get; } = new();
    /// <summary>
    /// Para tipos de tubería y conducto: uniones que NO tienen familia en sus preferencias de
    /// enrutamiento (sin ellas Revit no puede crear codos, tes…). Vacío = todo definido.
    /// </summary>
    public Dictionary<ElementId, List<string>> MissingFittings { get; } = new();

    public static readonly BuiltInCategory[] PlaceableCategories =
    {
        BuiltInCategory.OST_PipeAccessory,
        BuiltInCategory.OST_PlumbingFixtures,
        BuiltInCategory.OST_MechanicalEquipment,
        BuiltInCategory.OST_DuctAccessory,
        BuiltInCategory.OST_DuctTerminal,
        BuiltInCategory.OST_Sprinklers,
        BuiltInCategory.OST_ElectricalFixtures,
        BuiltInCategory.OST_ElectricalEquipment,
        BuiltInCategory.OST_LightingFixtures,
        BuiltInCategory.OST_LightingDevices,
        BuiltInCategory.OST_CommunicationDevices,
        BuiltInCategory.OST_DataDevices,
        BuiltInCategory.OST_SpecialityEquipment,
        BuiltInCategory.OST_GenericModel,
        // Protección contra incendios, seguridad y señalización: sin ellas, una familia cargada de extintor o detector
        // no se encontraría por su nombre y el bloque quedaría como marcador.
        BuiltInCategory.OST_FireAlarmDevices,
        BuiltInCategory.OST_FireProtection,
        BuiltInCategory.OST_SecurityDevices,
        BuiltInCategory.OST_NurseCallDevices,
        BuiltInCategory.OST_TelephoneDevices,
        BuiltInCategory.OST_Signage,
        BuiltInCategory.OST_MechanicalControlDevices,
        BuiltInCategory.OST_AudioVisualDevices,
    };

    public static RevitCatalog Load(Document doc)
    {
        var c = new RevitCatalog { _document = doc };

        foreach (var level in new FilteredElementCollector(doc).OfClass(typeof(Level)).Cast<Level>()
                     .OrderBy(l => l.ProjectElevation))
            c.Levels.Add((level.Name, level.Id, level.ProjectElevation));

        c.Types[MepKind.Tuberia] = Named<PipeType>(doc);
        c.Types[MepKind.Conducto] = Named<DuctType>(doc);
        c.Types[MepKind.Bandeja] = Named<CableTrayType>(doc);
        c.Types[MepKind.TuboElectrico] = Named<ConduitType>(doc);
        c.Systems[MepKind.Tuberia] = Named<PipingSystemType>(doc);
        foreach (var entry in c.Systems[MepKind.Tuberia])
            if (doc.GetElement(entry.Id) is PipingSystemType system) c.PipeClassifications[entry.Id] = system.SystemClassification;
        c.Systems[MepKind.Conducto] = Named<MechanicalSystemType>(doc);
        c.Systems[MepKind.Bandeja] = new List<NamedId>();
        c.Systems[MepKind.TuboElectrico] = new List<NamedId>();
        c.Systems[MepKind.Muro] = new List<NamedId>();
        var walls = new FilteredElementCollector(doc).OfClass(typeof(WallType)).Cast<WallType>()
            .Where(w => w.Kind == WallKind.Basic).ToList();
        c.Types[MepKind.Muro] = walls.Select(w => new NamedId(w.Name, w.Id))
            .OrderBy(n => n.Name, StringComparer.CurrentCultureIgnoreCase).ToList();
        foreach (var w in walls)
            c.WallWidthsMm[w.Id] = UnitUtils.ConvertFromInternalUnits(w.Width, UnitTypeId.Millimeters);

        foreach (var dt in new FilteredElementCollector(doc).OfClass(typeof(DuctType)).Cast<DuctType>())
            c.DuctShapes[dt.Id] = dt.Shape;

        foreach (var mt in new FilteredElementCollector(doc).OfClass(typeof(MEPCurveType)).Cast<MEPCurveType>())
        {
            if (mt is not (PipeType or DuctType or CableTrayType or ConduitType)) continue;
            c.MissingFittings[mt.Id] = MissingFittingsOf(mt);
        }

        foreach (var bic in PlaceableCategories)
        {
            foreach (var fs in new FilteredElementCollector(doc).OfClass(typeof(FamilySymbol))
                         .OfCategory(bic).Cast<FamilySymbol>())
            {
                string cat = fs.Category?.Name ?? bic.ToString();
                string display = cat + ": " + fs.FamilyName + " : " + fs.Name;
                c.FamilySymbols.Add(new NamedId(display, fs.Id));
                c.Symbols.Add((fs.FamilyName, fs.Name, fs.Id, display));
            }
        }
        c.FamilySymbols.Sort((a, b) => string.Compare(a.Name, b.Name, StringComparison.CurrentCultureIgnoreCase));
        return c;
    }

    private static List<NamedId> Named<T>(Document doc) where T : Element =>
        new FilteredElementCollector(doc).OfClass(typeof(T)).Cast<T>()
            .Select(e => new NamedId(e.Name, e.Id))
            .OrderBy(n => n.Name, StringComparer.CurrentCultureIgnoreCase)
            .ToList();

    /// <summary>¿Este tipo de elemento necesita tipo de sistema?</summary>
    public static bool NeedsSystem(MepKind kind) => kind is MepKind.Tuberia or MepKind.Conducto;

    /// <summary>Tipo de muro básico de espesor más parecido (null si no hay ninguno).</summary>
    public static (ElementId Id, double WidthMm)? NearestWallType(IReadOnlyDictionary<ElementId, double> widths, double mm)
    {
        if (widths.Count == 0) return null;
        var best = widths.OrderBy(kv => Math.Abs(kv.Value - mm)).First();
        return (best.Key, best.Value);
    }

    /// <summary>Formas de sección que tienen los tipos de conducto del proyecto (rectangular, redonda, oval).</summary>
    public List<ConnectorProfileType> DuctShapesAvailable() =>
        new[] { ConnectorProfileType.Rectangular, ConnectorProfileType.Round, ConnectorProfileType.Oval }
            .Where(s => DuctShapes.Values.Contains(s)).ToList();

    /// <summary>Tipos de conducto del proyecto con la sección indicada.</summary>
    public List<NamedId> DuctTypesOf(ConnectorProfileType shape) =>
        Types.GetValueOrDefault(MepKind.Conducto, new List<NamedId>())
            .Where(t => DuctShapes.TryGetValue(t.Id, out var s) && s == shape).ToList();

    /// <summary>¿Este tipo de elemento usa ancho × alto en lugar de diámetro?</summary>
    public bool UsesWidthHeight(MepKind kind, ElementId? typeId) => kind switch
    {
        MepKind.Bandeja => true,
        MepKind.Conducto => typeId != null && DuctShapes.TryGetValue(typeId, out var s) && s != ConnectorProfileType.Round,
        MepKind.Muro => true, // el "alto" es la altura del muro
        _ => false
    };

    /// <summary>
    /// Familia cargada que corresponde a un bloque por su nombre (p. ej. bloques exportados desde Revit
    /// "Familia - Tipo-123-…"). Primero familia y tipo exactos; si no, la familia con cualquier tipo.
    /// Devuelve el texto que se muestra en la lista, o null si no hay ninguna.
    /// </summary>
    public string? MatchBlock(string blockName)
    {
        // Sustituta decidida al preparar el proyecto (no había familia, o la del proyecto necesita anfitrión): manda.
        if (FamilyFallback.TryGetValue(BlockNames.Clean(blockName), out var fallback))
        {
            if (fallback.Length == 0) return null;
            if (FallbackMatch(blockName) is string substitute) return substitute;
        }
        var cands = BlockNames.Candidates(blockName);
        foreach (var (fam, type) in cands)
        {
            var hit = Symbols.FirstOrDefault(s => s.Family.Equals(fam, StringComparison.OrdinalIgnoreCase) &&
                                                  s.Type.Equals(type, StringComparison.OrdinalIgnoreCase));
            if (hit.Id != null) return hit.Display;
        }
        foreach (var (fam, _) in cands)
        {
            var hit = Symbols.FirstOrDefault(s => s.Family.Equals(fam, StringComparison.OrdinalIgnoreCase));
            if (hit.Id != null) return hit.Display;
        }
        return null;
    }

    /// <summary>
    /// Familia de sustitución cargada para este bloque (p. ej. una rejilla genérica de la biblioteca de Revit):
    /// el tipo cuyo nombre lleva las mismas medidas que el bloque («R5_300x150» → «300x150»), o el primero.
    /// </summary>
    private string? FallbackMatch(string blockName)
    {
        if (FamilyFallback.TryGetValue(BlockNames.Clean(blockName), out var family))
        {
            var types = Symbols.Where(s => s.Family.Equals(family, StringComparison.OrdinalIgnoreCase)).ToList();
            if (types.Count == 0) return null;
            var m = System.Text.RegularExpressions.Regex.Match(blockName, @"(\d{2,4})\s*[xX×]\s*(\d{2,4})");
            if (m.Success)
            {
                var same = types.FirstOrDefault(t => System.Text.RegularExpressions.Regex.IsMatch(t.Type,
                    $@"(^|\D){m.Groups[1].Value}\s*[xX×]\s*{m.Groups[2].Value}(\D|$)"));
                if (same.Id != null) return same.Display;
            }
            return types[0].Display;
        }
        return null;
    }

    /// <summary>Bloque (nombre limpio) → familia de sustitución cargada para él cuando no existe la suya.</summary>
    public Dictionary<string, string> FamilyFallback { get; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Uniones sin familia en las preferencias de enrutamiento del tipo (vacío = todo definido).</summary>
    public static List<string> MissingFittingsOf(MEPCurveType mt)
    {
        var missing = new List<string>();
        // Bandejas y tubos eléctricos no tienen preferencias de enrutamiento: sus uniones son parámetros del tipo.
        if (mt is CableTrayType or ConduitType)
        {
            foreach (var (label, bips) in TrayConduitFittingParams)
            {
                var p = bips.Select(b => mt.get_Parameter(b)).FirstOrDefault(x => x != null);
                if (p != null && p.StorageType == StorageType.ElementId && p.AsElementId() == ElementId.InvalidElementId) missing.Add(label);
            }
            return missing;
        }
        try
        {
            var rpm = mt.RoutingPreferenceManager;
            bool Has(RoutingPreferenceRuleGroupType g)
            {
                int n = rpm.GetNumberOfRules(g);
                for (int i = 0; i < n; i++)
                    if (rpm.GetRule(g, i).MEPPartId != ElementId.InvalidElementId) return true;
                return false;
            }
            if (!Has(RoutingPreferenceRuleGroupType.Elbows)) missing.Add("codo");
            if (rpm.PreferredJunctionType == PreferredJunctionType.Tap)
                missing.Add("te (el tipo está configurado con DERIVACIONES/tomas en lugar de tes)");
            else if (!Has(RoutingPreferenceRuleGroupType.Junctions)) missing.Add("te");
            if (!Has(RoutingPreferenceRuleGroupType.Crosses)) missing.Add("cruz");
            if (!Has(RoutingPreferenceRuleGroupType.Transitions)) missing.Add("transición");
        }
        catch { /* sin preferencias legibles: no se avisa */ }
        return missing;
    }

    /// <summary>
    /// Parámetros de tipo de bandejas y tubos eléctricos con su familia de unión (el primero que exista en el tipo).
    /// Codo: en bandejas «curva horizontal»; en tubos, «curva» o «codo».
    /// </summary>
    public static readonly (string Label, BuiltInParameter[] Params)[] TrayConduitFittingParams =
    {
        ("codo", new[] { BuiltInParameter.RBS_CURVETYPE_DEFAULT_HORIZONTAL_BEND_PARAM, BuiltInParameter.RBS_CURVETYPE_DEFAULT_BEND_PARAM, BuiltInParameter.RBS_CURVETYPE_DEFAULT_ELBOW_PARAM }),
        ("te", new[] { BuiltInParameter.RBS_CURVETYPE_DEFAULT_TEE_PARAM }),
        ("cruz", new[] { BuiltInParameter.RBS_CURVETYPE_DEFAULT_CROSS_PARAM }),
        ("transición", new[] { BuiltInParameter.RBS_CURVETYPE_DEFAULT_TRANSITION_PARAM }),
    };

    /// <summary>
    /// Preferencias de enrutamiento del tipo, en texto, para el informe: qué unión prefiere (te o derivación)
    /// y qué familias tiene para codos, tes, cruces y transiciones.
    /// </summary>
    public static string DescribeRouting(Document doc, MEPCurveType mt)
    {
        try
        {
            var rpm = mt.RoutingPreferenceManager;
            string Group(RoutingPreferenceRuleGroupType g)
            {
                var names = new List<string>();
                int n = rpm.GetNumberOfRules(g);
                for (int i = 0; i < n; i++)
                {
                    var id = rpm.GetRule(g, i).MEPPartId;
                    if (id == ElementId.InvalidElementId) { names.Add("(ninguna)"); continue; }
                    var e = doc.GetElement(id);
                    names.Add(e is FamilySymbol fs ? fs.FamilyName + " : " + fs.Name : e?.Name ?? "?");
                }
                return names.Count == 0 ? "(sin reglas)" : string.Join(", ", names);
            }
            return $"unión preferida {rpm.PreferredJunctionType}; codos: {Group(RoutingPreferenceRuleGroupType.Elbows)}; " +
                   $"tes o derivaciones: {Group(RoutingPreferenceRuleGroupType.Junctions)}; cruces: {Group(RoutingPreferenceRuleGroupType.Crosses)}; " +
                   $"transiciones: {Group(RoutingPreferenceRuleGroupType.Transitions)}";
        }
        catch (Exception ex) { return "no se pudieron leer sus preferencias de enrutamiento (" + ex.Message + ")"; }
    }

    /// <summary>Texto de aviso si al tipo le faltan familias de unión; null si no falta nada.</summary>
    public string? FittingWarning(string typeName, ElementId? typeId) =>
        typeId != null && MissingFittings.TryGetValue(typeId, out var m) && m.Count > 0
            ? $"El tipo '{typeName}' no tiene definida la familia de: {string.Join(", ", m)}. Esas uniones fallarán. " +
              "Corríjalo en Revit: seleccione el tipo > Editar tipo > Preferencias de enrutamiento > Editar (tuberías y conductos) " +
              "o, en bandejas y tubos eléctricos, los parámetros de unión del tipo (Curva horizontal, Te, Cruz, Transición), y elija " +
              "una familia para cada uno (o cárguelas con Insertar > Cargar familia). También puede elegir otro 'Tipo' en la tabla."
            : null;

    /// <summary>Indica cómo conseguir tipos si el proyecto no tiene ninguno.</summary>
    public static string HowToGetTypes(MepKind kind) => kind switch
    {
        MepKind.Tuberia => "El proyecto no tiene tipos de tubería o de sistema de tuberías. Abra un proyecto creado con la plantilla 'Sistemas' o 'Mecánica'/'Fontanería', o use Gestionar > Transferir normas de proyecto desde uno que los tenga.",
        MepKind.Conducto => "El proyecto no tiene tipos de conducto o de sistema de conductos. Use una plantilla 'Mecánica'/'Sistemas' o transfiera los tipos con Gestionar > Transferir normas de proyecto.",
        MepKind.Bandeja => "El proyecto no tiene tipos de bandeja de cables. Use una plantilla 'Eléctrica'/'Sistemas' o transfiera los tipos con Gestionar > Transferir normas de proyecto.",
        MepKind.Muro => "El proyecto no tiene tipos de muro básico. Use una plantilla de arquitectura o transfiera los tipos de muro con Gestionar > Transferir normas de proyecto.",
        MepKind.TuboElectrico => "El proyecto no tiene tipos de tubo eléctrico. Use una plantilla 'Eléctrica'/'Sistemas' o transfiera los tipos con Gestionar > Transferir normas de proyecto.",
        _ => ""
    };
}
