using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Electrical;
using Autodesk.Revit.DB.Mechanical;
using Autodesk.Revit.DB.Plumbing;
using CadMepRevit.Core;

namespace CadMepRevit.Revit;

/// <summary>
/// Sin codos, tes, cruces ni transiciones en las preferencias de enrutamiento de un tipo, Revit no puede unir los
/// tramos (es lo que pasa con la plantilla «Construcción»: sus tipos «Por defecto» no tienen ninguna). Antes de
/// modelar, a cada tipo que se va a usar y le falta alguna, se le asigna una familia de unión: la que ya esté
/// cargada en el proyecto o, si no hay, una de la biblioteca de Revit (FamilyLibrary). Necesita una transacción.
/// </summary>
public static class FittingSetup
{
    private sealed record Want(JunctionKind Kind, RoutingPreferenceRuleGroupType Group, string Label, PartType[] Parts, string[] Pipe, string[] Rect, string[] Round);

    private static readonly Want[] Wants =
    {
        new(JunctionKind.Codo, RoutingPreferenceRuleGroupType.Elbows, "codo", new[] { PartType.Elbow },
            new[] { "M_Codo - Genérico", "Codo - Genérico", "M_Codo - Soldado - Genérico" },
            new[] { "M_Codo rectangular - Radio", "M_Codo rectangular - Biselado", "Codo rectangular - Radio" },
            new[] { "M_Codo redondo", "M_Codo redondo - Segmentado", "Codo redondo" }),
        new(JunctionKind.Te, RoutingPreferenceRuleGroupType.Junctions, "te", new[] { PartType.Tee },
            new[] { "M_Te - Genérica", "Te - Genérica", "M_Te - Soldada - Genérica" },
            new[] { "M_Te rectangular", "M_Te rectangular - Empalme", "Te rectangular" },
            new[] { "M_Te redonda", "M_Te redonda - Recta", "Te redonda" }),
        new(JunctionKind.Cruz, RoutingPreferenceRuleGroupType.Crosses, "cruz", new[] { PartType.Cross },
            new[] { "M_Cruz - Genérica", "Cruz - Genérica", "M_Cruz - Soldada - Genérica" },
            new[] { "M_Cruz rectangular", "Cruz rectangular" },
            new[] { "M_Cruz redonda", "Cruz redonda" }),
        new(JunctionKind.Transicion, RoutingPreferenceRuleGroupType.Transitions, "transición", new[] { PartType.Transition },
            new[] { "M_Transición - Genérica", "Transición - Genérica" },
            new[] { "M_Transición rectangular - Ángulo", "M_Transición rectangular - Longitud", "Transición rectangular - Ángulo" },
            new[] { "M_Transición redonda - Ángulo", "M_Transición redonda - Longitud", "Transición redonda - Ángulo" })
    };

    /// <summary>Familias de la biblioteca de Revit para bandejas (canal / escalera) y tubos eléctricos, por unión.</summary>
    private static readonly Dictionary<string, (string Part, string[] Channel, string[] Ladder, string[] Conduit)> TrayConduitNames = new()
    {
        ["codo"] = ("Elbow", new[] { "M_Codo horizontal de canal" }, new[] { "M_Curvatura horizontal de escalera" },
                    new[] { "M_Codo de tubo - Sin uniones - EMT", "M_Codo de tubo - Acero", "M_Codo de tubo - Extremo liso - PVC" }),
        ["te"] = ("Tee", new[] { "M_Te horizontal de canal" }, new[] { "M_Te horizontal de escalera" },
                  new[] { "M_Cuerpo de tubo - Tipo T - PVC", "M_Caja de conexiones de tubo - Te - PVC", "M_Cuerpo de tubo - Tipo T - Aluminio" }),
        ["cruz"] = ("Cross", new[] { "M_Cruz horizontal de canal" }, new[] { "M_Cruz horizontal de escalera" },
                    new[] { "M_Caja de conexiones de tubo - Cruz - PVC", "M_Caja de conexiones de tubo - Cruz - Aluminio" }),
        ["transición"] = ("Transition", new[] { "M_Reductor de canal" }, new[] { "M_Reductor de escalera" },
                          new[] { "M_Caja de conexiones de tubo - Transición - PVC", "M_Caja de conexiones de tubo - Transición - Aluminio" }),
    };

    /// <summary>
    /// Bandejas y tubos eléctricos: sus uniones son parámetros del tipo (no preferencias de enrutamiento). A los que les
    /// falte alguna se les asigna una ya cargada de su clase o, si no hay, la de la biblioteca de Revit.
    /// </summary>
    private static void CompleteTrayOrConduit(Document doc, MEPCurveType mt, FamilyLibrary? library, List<string> log)
    {
        bool tray = mt is CableTrayType;
        bool ladder = tray && ((CableTrayType)mt).ShapeType.ToString().Contains("Ladder", StringComparison.OrdinalIgnoreCase);
        var cat = tray ? BuiltInCategory.OST_CableTrayFitting : BuiltInCategory.OST_ConduitFitting;
        foreach (var (label, bips) in RevitCatalog.TrayConduitFittingParams)
        {
            var p = bips.Select(b => mt.get_Parameter(b)).FirstOrDefault(x => x != null);
            if (p is null || p.StorageType != StorageType.ElementId || p.AsElementId() != ElementId.InvalidElementId) continue;
            var (part, channel, ladderNames, conduit) = TrayConduitNames[label];
            var names = !tray ? conduit : ladder ? ladderNames : channel;
            FamilySymbol? symbol = null;
            string from = "ya cargada en el proyecto";
            foreach (var fs in new FilteredElementCollector(doc).OfClass(typeof(FamilySymbol)).OfCategory(cat).Cast<FamilySymbol>())
            {
                var pt = fs.Family?.get_Parameter(BuiltInParameter.FAMILY_CONTENT_PART_TYPE)?.AsInteger();
                if (pt is null) continue;
                string ptName = ((PartType)pt.Value).ToString();
                if (ptName.Contains("Vertical")) continue;
                bool ok = tray
                    ? ptName.EndsWith(part) && ptName.Contains(ladder ? "Ladder" : "Channel")
                    : ptName == part;
                if (ok) { symbol = fs; break; }
            }
            if (symbol is null && library != null)
            {
                foreach (var n in names)
                {
                    var fam = library.Ensure(doc, n, log);
                    if (fam is null) continue;
                    symbol = fam.GetFamilySymbolIds().Select(doc.GetElement).OfType<FamilySymbol>().FirstOrDefault();
                    from = "cargada de la biblioteca";
                    if (symbol != null) break;
                }
            }
            if (symbol is null)
            {
                log.Add($"Tipo '{mt.Name}': no hay ninguna familia de {label} cargada ni en la biblioteca ({string.Join(", ", names)}). " +
                        "Esas uniones no se crearán: cárguela con Insertar > Cargar familia y asígnela en Editar tipo.");
                continue;
            }
            try
            {
                if (!symbol.IsActive) symbol.Activate();
                if (p.IsReadOnly || !p.Set(symbol.Id)) throw new InvalidOperationException("el parámetro no admite el valor");
                log.Add($"Tipo '{mt.Name}': {label} = '{symbol.FamilyName} : {symbol.Name}' ({from}).");
            }
            catch (Exception ex) { log.Add($"Tipo '{mt.Name}': no se pudo asignar el {label} '{symbol.FamilyName}': {ex.Message}"); }
        }
    }

    /// <summary>Completa las uniones de estos tipos. Devuelve lo hecho y lo que no se pudo, para el informe.</summary>
    public static List<string> Complete(Document doc, IEnumerable<ElementId> typeIds, FamilyLibrary? library, ModelScope? scope = null)
    {
        var log = new List<string>();
        foreach (var id in typeIds.Distinct())
        {
            if (doc.GetElement(id) is not MEPCurveType mt) continue;
            var kind = mt switch
            {
                PipeType => MepKind.Tuberia,
                DuctType => MepKind.Conducto,
                CableTrayType => MepKind.Bandeja,
                ConduitType => MepKind.TuboElectrico,
                _ => MepKind.NoImportar
            };
            if (!ModelScopes.AllowsAnyFitting(scope, kind)) continue;
            if (kind is MepKind.Bandeja or MepKind.TuboElectrico)
            {
                CompleteTrayOrConduit(doc, mt, library, log);
                continue;
            }
            RoutingPreferenceManager rpm;
            try { rpm = mt.RoutingPreferenceManager; }
            catch (Exception ex) { log.Add($"Tipo '{mt.Name}': no se pueden leer sus preferencias de enrutamiento ({ex.Message})."); continue; }
            var shape = mt is DuctType dt ? dt.Shape : ConnectorProfileType.Round;
            var cat = mt is PipeType ? BuiltInCategory.OST_PipeFitting : BuiltInCategory.OST_DuctFitting;
            foreach (var w in Wants)
            {
                if (!ModelScopes.AllowsFitting(scope, kind, w.Kind)) continue;
                if (Has(doc, rpm, w))
                {
                    if (w.Kind == JunctionKind.Te && rpm.PreferredJunctionType == PreferredJunctionType.Tap)
                        rpm.PreferredJunctionType = PreferredJunctionType.Tee;
                    continue;
                }
                var names = mt is PipeType ? w.Pipe : shape == ConnectorProfileType.Rectangular ? w.Rect : shape == ConnectorProfileType.Round ? w.Round : Array.Empty<string>();
                var symbol = LoadedSymbol(doc, cat, w.Parts, shape, mt is PipeType);
                string from = "ya cargada en el proyecto";
                if (symbol is null && library != null)
                {
                    foreach (var n in names)
                    {
                        var fam = library.Ensure(doc, n, log);
                        if (fam is null) continue;
                        symbol = fam.GetFamilySymbolIds().Select(doc.GetElement).OfType<FamilySymbol>().FirstOrDefault();
                        from = "cargada de la biblioteca";
                        if (symbol != null) break;
                    }
                }
                // Sin el nombre exacto: cualquier familia de la biblioteca que por su nombre sea esa pieza y esa sección
                // (las oficinas renombran: «M_Codo rectangular - Radio 1», «M_Te rectangular - Recto»…). Se comprueba
                // después de cargarla que de verdad es de esa categoría y clase de pieza.
                if (symbol is null && library != null)
                {
                    foreach (var file in library.FindLike(n => LooksLike(n, w, mt is PipeType, shape)).Take(4))
                    {
                        var fam = library.LoadFile(doc, file, log);
                        if (fam is null) continue;
                        if (fam.FamilyCategory?.Id.Value != (long)cat ||
                            fam.get_Parameter(BuiltInParameter.FAMILY_CONTENT_PART_TYPE)?.AsInteger() is not int part ||
                            !w.Parts.Contains((PartType)part))
                        {
                            log.Add($"Familia '{fam.Name}' ({file}): por su nombre parecía un {w.Label}, pero no lo es; no se usa.");
                            continue;
                        }
                        symbol = fam.GetFamilySymbolIds().Select(doc.GetElement).OfType<FamilySymbol>().FirstOrDefault();
                        from = file.StartsWith(FamilyLibrary.BundledFolder ?? "\0", StringComparison.OrdinalIgnoreCase)
                            ? "familia de reserva instalada con el complemento" : "cargada de la biblioteca por su nombre parecido";
                        if (symbol != null) break;
                    }
                }
                if (symbol is null)
                {
                    log.Add($"Tipo '{mt.Name}': no hay ninguna familia de {w.Label} cargada ni en la biblioteca ({string.Join(", ", names)}, " +
                            $"ni otra con '{w.Label}' y su sección en el nombre). Esas uniones no se crearán: cárguela con Insertar > Cargar familia y repita, " +
                            $"o añada la carpeta de familias de su oficina a %APPDATA%\\CadMepRevit\\bibliotecas.txt.");
                    continue;
                }
                try
                {
                    if (!symbol.IsActive) symbol.Activate();
                    var rule = new RoutingPreferenceRule(symbol.Id, "CAD-REV");
                    if (w.Kind == JunctionKind.Te)
                    {
                        rpm.AddRule(w.Group, rule, 0);
                        rpm.PreferredJunctionType = PreferredJunctionType.Tee;
                    }
                    else rpm.AddRule(w.Group, rule);
                    log.Add($"Tipo '{mt.Name}': {w.Label} = '{symbol.FamilyName} : {symbol.Name}' ({from}).");
                }
                catch (Exception ex) { log.Add($"Tipo '{mt.Name}': no se pudo asignar el {w.Label} '{symbol.FamilyName}': {ex.Message}"); }
            }
        }
        return log;
    }

    private static bool Has(Document doc, RoutingPreferenceManager rpm, Want want)
    {
        int n = rpm.GetNumberOfRules(want.Group);
        for (int i = 0; i < n; i++)
        {
            var id = rpm.GetRule(want.Group, i).MEPPartId;
            if (id == ElementId.InvalidElementId) continue;
            if (want.Kind != JunctionKind.Te) return true;
            if (doc.GetElement(id) is FamilySymbol symbol &&
                symbol.Family.get_Parameter(BuiltInParameter.FAMILY_CONTENT_PART_TYPE)?.AsInteger() == (int)PartType.Tee)
                return true;
        }
        return false;
    }

    private static bool LooksLike(string familyName, Want want, bool pipe, ConnectorProfileType shape) =>
        FittingNames.LooksLike(familyName, want.Kind, pipe ? FittingShape.Tuberia : shape switch
        {
            ConnectorProfileType.Rectangular => FittingShape.Rectangular,
            ConnectorProfileType.Round => FittingShape.Redonda,
            _ => FittingShape.Oval
        });

    /// <summary>Familia de unión ya cargada de esa clase (codo, te…) y, en conductos, de la misma sección.</summary>
    private static FamilySymbol? LoadedSymbol(Document doc, BuiltInCategory cat, PartType[] parts, ConnectorProfileType shape, bool pipe)
    {
        foreach (var fs in new FilteredElementCollector(doc).OfClass(typeof(FamilySymbol)).OfCategory(cat).Cast<FamilySymbol>())
        {
            var pt = fs.Family?.get_Parameter(BuiltInParameter.FAMILY_CONTENT_PART_TYPE)?.AsInteger();
            if (pt is null || !parts.Contains((PartType)pt.Value)) continue;
            if (!pipe)
            {
                string n = fs.FamilyName.ToUpperInvariant();
                bool rect = n.Contains("RECTANG"), round = n.Contains("REDOND") || n.Contains("ROUND"), oval = n.Contains("OVAL");
                if (shape == ConnectorProfileType.Rectangular && !rect) continue;
                if (shape == ConnectorProfileType.Round && !round) continue;
                if (shape == ConnectorProfileType.Oval && !oval) continue;
                if (n.Contains(" A ") || n.Contains("MULTIFORMA")) continue; // piezas de cambio de sección
            }
            return fs;
        }
        return null;
    }

    /// <summary>
    /// Tubo eléctrico dibujado con sus dos bordes: lo medido es el diámetro EXTERIOR. Devuelve el diámetro nominal (tamaño
    /// comercial) de la norma del tipo cuyo exterior se parece más; si la norma no tiene tamaños, busca en todas.
    /// Null si el proyecto no tiene tamaños de tubo (entonces se pide lo dibujado y Revit elige el más cercano).
    /// </summary>
    public static double? ConduitNominalForOuterMm(Autodesk.Revit.DB.Electrical.Conduit conduit, double outerMm)
    {
        try
        {
            var doc = conduit.Document;
            string? standard = doc.GetElement(conduit.GetTypeId())?.get_Parameter(BuiltInParameter.CONDUIT_STANDARD_TYPE_PARAM)?.AsValueString();
            var settings = Autodesk.Revit.DB.Electrical.ConduitSizeSettings.GetConduitSizeSettings(doc);
            double? Search(bool onlyStandard)
            {
                double best = double.MaxValue; double? nominal = null;
                foreach (var kv in settings)
                {
                    if (onlyStandard && !string.Equals(kv.Key, standard, StringComparison.OrdinalIgnoreCase)) continue;
                    foreach (var size in kv.Value)
                    {
                        if (!size.UsedInSizeLists) continue;
                        double od = UnitUtils.ConvertFromInternalUnits(size.OuterDiameter, UnitTypeId.Millimeters);
                        double nd = UnitUtils.ConvertFromInternalUnits(size.NominalDiameter, UnitTypeId.Millimeters);
                        double d = Math.Min(Math.Abs(od - outerMm), Math.Abs(nd - outerMm) + 0.01);
                        if (d < best) { best = d; nominal = nd; }
                    }
                }
                return nominal;
            }
            return (standard != null ? Search(true) : null) ?? Search(false);
        }
        catch { return null; }
    }

    /// <summary>
    /// Diámetro nominal del segmento de la tubería cuyo diámetro EXTERIOR se parece más al dibujado (los planos
    /// exportados de Revit dibujan el exterior). Null si el segmento no tiene tamaños.
    /// </summary>
    public static double? NominalForOuterMm(Pipe pipe, double outerMm)
    {
        try
        {
            var seg = pipe.PipeSegment;
            if (seg is null) return null;
            double best = double.MaxValue; double? nominal = null;
            foreach (MEPSize size in seg.GetSizes())
            {
                double od = UnitUtils.ConvertFromInternalUnits(size.OuterDiameter, UnitTypeId.Millimeters);
                double nd = UnitUtils.ConvertFromInternalUnits(size.NominalDiameter, UnitTypeId.Millimeters);
                double d = Math.Min(Math.Abs(od - outerMm), Math.Abs(nd - outerMm) + 0.01);
                if (d < best) { best = d; nominal = nd; }
            }
            return nominal;
        }
        catch { return null; }
    }
}
