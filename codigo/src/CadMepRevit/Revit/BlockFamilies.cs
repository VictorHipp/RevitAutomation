using Autodesk.Revit.DB;
using CadMepRevit.Core;

namespace CadMepRevit.Revit;

/// <summary>
/// Antes de modelar, carga desde la biblioteca las familias de los bloques del plano que el proyecto no tiene.
/// Los planos exportados de Revit nombran cada bloque «Familia - Tipo-123-Vista», así que se busca la familia por su
/// nombre. Si no está, se carga una familia GENÉRICA de la biblioteca de Revit según la función del bloque (rejilla,
/// difusor, luminaria, interruptor, toma, cuadro, extintor, BIE, detector, pulsador, sirena, señal…), para que sea un
/// elemento real de su categoría y no una caja. Entre las genéricas se prefieren las que no necesitan anfitrión
/// (basadas en nivel o en plano de trabajo); una «basada en cara» solo se acepta si el elemento va en el techo.
/// Necesita una transacción abierta.
/// </summary>
public static class BlockFamilies
{
    private static readonly string[] ExhaustGrilles =
        { "M_Rejilla de escape", "M_Rejilla de ventilación - Rectangular - Montada en suelo", "M_Difusor de escape - Con anfitrión" };
    private static readonly string[] SupplyDiffusers =
        { "M_Difusor de suministro - Cuello rectangular de cara rectangular", "M_Difusor de suministro", "M_Difusor de suministro - Cuello redondo de cara rectangular" };

    /// <summary>Familias genéricas de la biblioteca de Revit (en orden de preferencia) para un bloque sin familia propia.</summary>
    public static IReadOnlyList<string> GenericFamiliesFor(BlockRole role, string blockName)
    {
        string u = BlockNames.Clean(blockName).ToUpperInvariant();
        bool Any(params string[] k) => k.Any(u.Contains);
        switch (role)
        {
            case BlockRole.Terminal:
                return (Any("SUMIN", "IMPUL", "DIFUSOR") ? SupplyDiffusers.Concat(ExhaustGrilles) : ExhaustGrilles.Concat(SupplyDiffusers)).ToList();
            case BlockRole.Luminaria:
                if (Any("EMERGENCIA")) return new[] { "M_Aplique de emergencia", "M_Luminaria empotrada de emergencia" }; // en pared, 2,20 m
                if (Any("TIRA", "LINEAL")) return new[] { "M_Luminarias de tira", "M_Luz indirecta - Tira", "M_Luz de techo - Cuadro lineal" };
                if (Any("DOWNLIGHT", "FOCO", "EMPOTRAD")) return new[] { "M_Foco empotrado - Foco", "M_Foco empotrado - Portalámparas empotrado", "M_Luz de techo - Redonda plana" };
                if (Any("APLIQUE")) return new[] { "M_Aplique - Redondo plano", "M_Aplique - Esfera", "M_Lámpara de muro - Soporte" };
                if (Any("COLGANTE")) return new[] { "M_Luminaria colgante", "M_Luz colgante - Disco", "M_Luz de techo - Redonda plana" };
                return new[] { "M_Luz de techo - Redonda plana", "M_Luminarias de superficie lisas", "M_Plafón - Lente", "M_Luminarias empotradas lisas" };
            case BlockRole.MandoIluminacion:
                if (Any("DETECTOR", "SENSOR")) return new[] { "M_Sensor de ocupación de techo - Voltaje normal", "M_Sensor de ocupación de muro - Voltaje normal" };
                return new[] { "M_Interruptores de iluminación", "M_Interruptor" };
            case BlockRole.Sanitario when BlockNames.IsDrainageChamber(blockName):
            case BlockRole.Electrico when Any("ARQUETA"):
                var specialized = Any("SIFON", "SIFÓN") ? new[] { "F-EVRO-Arqueta Sifonica", "Arqueta sifónica" }
                    : Any("BOMBEO") ? new[] { "Arqueta de bombeo", "Pozo de bombeo" }
                    : Any("POZO", "MANHOLE") ? new[] { "E-EVRO-Pozos", "Pozo" }
                    : Any("SUMIDERO") && Any("LINEAL") ? new[] { "Sumidero lineal", "F-EVRO-Arqueta Sumidero", "Sumidero" }
                    : Any("SUMIDERO") ? new[] { "F-EVRO-Arqueta Sumidero", "Sumidero" }
                    : role == BlockRole.Electrico ? new[] { "Arqueta eléctrica", "Arqueta eléctrica enterrada", "Arqueta cuadrada sin conexiones" }
                    : Array.Empty<string>();
                return specialized.Concat(new[] { "Arqueta", "Arqueta cuadrada", "Arqueta cuadrada sin conexiones", "F-EVRO-Arqueta Acometida" }).Distinct().ToList();
            case BlockRole.Sanitario:
                if (Any("INODORO", "WC")) return new[] { "M_Inodoro", "Inodoro" };
                if (Any("LAVABO")) return new[] { "M_Lavabo", "Lavabo" };
                return Array.Empty<string>();
            case BlockRole.Electrico:
                if (Any("RJ45", "DATOS")) return new[] { "M_Toma de datos" };
                if (Any("TELEF")) return new[] { "M_Toma de teléfono" };
                if (Any("TV")) return new[] { "M_TV - Cuadrado", "M_Toma de datos" };
                if (Any("TOMA", "ENCHUFE", "SCHUKO")) return new[] { "M_Toma doble", "M_Toma simple" };
                if (Any("CUADRO", "CGP", "EMBARRADO", "_EQE_", "PANEL"))
                    return new[] { "M_Panel de fase única - 120V MCB - Superficie", "M_Cuadro de control de accesorios e iluminación - 208V MCB - Superficie" };
                return new[] { "M_Cajas de conexiones - Sin carga", "M_Caja de conexiones simple redonda" };
            case BlockRole.Pci:
                if (Any("EXTINTOR", "EXTINGUISHER")) return new[] { "Extintor C02", "Armario de extintores" };
                if (Any("BIE")) return new[] { "M_Armario para carrete de manguera - Montado en superficie", "Armario de manguera de incendios" };
                if (Any("PULSADOR", "MANUAL")) return new[] { "M_Estación manual", "Botón de alarma", "M_Estación de accionador manual" };
                if (Any("SIRENA", "AVISADOR", "VISUAL")) return new[] { "M_Avisador acústico de alarma de incendios - Montado en muro", "M_Minibocina", "Alarma de incendios - Muro" };
                if (Any("ROCIADOR", "SPRINKLER")) return new[] { "Rociador" };
                if (Any("SEÑAL", "SENAL")) return new[] { "Señal de salida" };
                if (Any("TERMOVELOCIM", "TÉRMIC", "TERMIC", "CALOR")) return new[] { "M_Detector de calor", "M_Detector de humos", "Detector - Humo y calor" };
                if (Any("DETECTOR", "_DET_")) return new[] { "M_Detector de humos", "Detector de humos", "Detector - Humo y calor" };
                if (Any("CENTRAL", "PANEL")) return new[] { "M_Panel de control de alarma de incendios", "Panel de control de alarma de incendios-Muro" };
                return Array.Empty<string>();
            default:
                return Array.Empty<string>();
        }
    }

    /// <summary>
    /// ¿Se puede colocar esta familia sin anfitrión? Las basadas en nivel, sí. Las basadas en plano de trabajo también,
    /// salvo las «basadas en cara» (en la API son de plano de trabajo con comportamiento de anfitrión distinto de 0),
    /// que solo se aceptan si el elemento va en el techo: se colocan en un plano horizontal mirando hacia abajo.
    /// Las que necesitan muro, suelo o techo (OneLevelBasedHosted) no se pueden colocar sin ellos.
    /// </summary>
    public static bool Placeable(Family f, bool ceilingMounted) => f.FamilyPlacementType switch
    {
        FamilyPlacementType.OneLevelBased => true,
        FamilyPlacementType.WorkPlaneBased => ceilingMounted || HostingBehavior(f) is null or 0,
        _ => false
    };

    /// <summary>Valor del parámetro «comportamiento de anfitrión» de la familia (0 = ninguno), o null si no se puede leer.</summary>
    public static int? HostingBehavior(Family f)
    {
        try { return f.get_Parameter(BuiltInParameter.FAMILY_HOSTING_BEHAVIOR) is { StorageType: StorageType.Integer } p ? p.AsInteger() : null; }
        catch { return null; }
    }

    /// <summary>
    /// Devuelve, para los bloques que usan una familia de sustitución, su nombre limpio → familia. Solo trata los bloques
    /// que coloca este botón (<paramref name="scope"/>).
    /// </summary>
    public static Dictionary<string, string> Prepare(Document doc, CadDrawing drawing, RevitCatalog catalog, FamilyLibrary library, List<string> log,
        ModelScope? scope = null)
    {
        var fallback = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var missing = new List<string>();
        var rejected = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var groups = drawing.Blocks.GroupBy(b => b.BlockName, StringComparer.OrdinalIgnoreCase);
        foreach (var group in groups)
        {
            var block = (scope is ModelScope requested
                ? group.FirstOrDefault(b => ModelScopes.Includes(requested, b.Role, b.BlockName))
                : null) ?? group.First();
            string name = block.BlockName;
            var role = block.Role;
            if (role == BlockRole.PiezaDeUnion) continue; // la crea Revit al unir los tramos
            if (scope is ModelScope s && !ModelScopes.Includes(s, role, name)) continue;
            if (role == BlockRole.Otro && !LooksLikeRevitFamily(name)) continue;
            string clean = BlockNames.Clean(name);
            if (fallback.ContainsKey(clean)) continue;
            bool followsNetwork = NetworkBlocks.For(scope, role, name) != null;
            bool ceiling = !followsNetwork && BlockElevations.IsCeilingMounted(role, name);
            bool sanitary = scope == ModelScope.Tuberias && role == BlockRole.Sanitario;
            bool substitute = sanitary || followsNetwork || scope is ModelScope.Luminarias or ModelScope.EquiposPci;
            bool AcceptOriginal(Family f) => sanitary ? Placeable(f, false)
                : f.FamilyPlacementType is FamilyPlacementType.OneLevelBased or FamilyPlacementType.WorkPlaneBased;
            if (catalog.MatchBlock(name) is string display)
            {
                if (!substitute) continue;
                // Familia del proyecto que necesita un muro o un techo de anfitrión (no hay ninguno en el proyecto: la
                // arquitectura suele estar vinculada): no se puede colocar. Se sustituye por una genérica de su clase.
                var own = (doc.GetElement(catalog.Symbols.FirstOrDefault(s => s.Display == display).Id ?? ElementId.InvalidElementId) as FamilySymbol)?.Family;
                if (own is null || AcceptOriginal(own)) continue;
                var sub = GenericFamiliesFor(role, name).Select(n => library.Ensure(doc, n, log)).FirstOrDefault(f => f != null && Placeable(f, ceiling));
                if (sub is null)
                {
                    if (sanitary) fallback[clean] = "";
                    log.Add($"Bloque '{clean}': su familia '{own.Name}' necesita anfitrión ({own.FamilyPlacementType}) y no hay genérica que la sustituya: quedará como marcador 3D.");
                    continue;
                }
                fallback[clean] = sub.Name;
                log.Add($"Bloque '{clean}': su familia '{own.Name}' necesita un muro o techo de anfitrión ({own.FamilyPlacementType}), que este proyecto no tiene " +
                        $"(¿arquitectura vinculada?); se usa la genérica de Revit '{sub.Name}'. Sustitúyala por la suya si la coloca a mano.");
                continue;
            }
            Family? fam = null;
            foreach (var (family, _) in BlockNames.Candidates(name))
            {
                if (family.Length < 3) continue;
                fam = library.Ensure(doc, family, log);
                if (fam != null) break;
            }
            if (fam != null && (!substitute || AcceptOriginal(fam))) continue;
            if (fam != null)
                log.Add($"Bloque '{clean}': su familia '{fam.Name}' (cargada de la biblioteca) necesita anfitrión ({fam.FamilyPlacementType}); se busca una genérica que la sustituya.");
            foreach (var n in GenericFamiliesFor(role, name))
            {
                var f = library.Ensure(doc, n, log);
                if (f is null) continue;
                if (!Placeable(f, ceiling))
                {
                    rejected[n] = f.FamilyPlacementType.ToString();
                    continue;
                }
                fallback[clean] = f.Name;
                log.Add($"Bloque '{clean}' ({BlockNames.Label(role)}): su familia no está en la biblioteca; se usa la genérica de Revit '{f.Name}' " +
                        $"(colocación: {f.FamilyPlacementType}).");
                break;
            }
            if (!fallback.ContainsKey(clean))
            {
                missing.Add(clean);
                if (sanitary && fam != null) fallback[clean] = "";
            }
        }
        foreach (var (n, type) in rejected)
            log.Add($"Familia genérica '{n}' descartada: necesita anfitrión ({type}) y no hay muro ni techo donde colocarla.");
        if (missing.Count > 0)
            log.Add($"Sin familia en el proyecto ni en la biblioteca ({missing.Count} bloques; quedan como marcador 3D si se reconoce su función): " +
                    string.Join(", ", missing.Take(15)) + (missing.Count > 15 ? "…" : ""));
        return fallback;
    }

    /// <summary>Nombre de bloque exportado de Revit: «Familia - Tipo-123-Vista».</summary>
    private static bool LooksLikeRevitFamily(string name) => name.Contains(" - ") && !name.StartsWith("Family", StringComparison.OrdinalIgnoreCase);
}
