namespace CadMepRevit.Core;

/// <summary>
/// Qué modela cada botón de la cinta. Cada botón lee el mismo plano y solo construye lo suyo; los bloques van con
/// la red a la que se conectan (rejillas y equipos de clima con los conductos, aparatos sanitarios con las tuberías).
/// </summary>
public enum ModelScope
{
    Tuberias,
    Conductos,
    /// <summary>Bandejas de cables y tubos eléctricos.</summary>
    Bandejas,
    /// <summary>Luminarias e interruptores / detectores de presencia.</summary>
    Luminarias,
    /// <summary>Protección contra incendios (extintores, BIE, detectores, pulsadores, sirenas…), cuadros, tomas y dispositivos.</summary>
    EquiposPci
}

public static class ModelScopes
{
    public static bool AllowsFitting(ModelScope? scope, MepKind kind, JunctionKind joint)
    {
        if (joint is not (JunctionKind.Codo or JunctionKind.Te or JunctionKind.Cruz or JunctionKind.Transicion)) return false;
        if (scope is null) return kind is MepKind.Tuberia or MepKind.Conducto or MepKind.Bandeja or MepKind.TuboElectrico;
        // Tubos eléctricos (botón Bandejas): codos, tes, cruces y reducciones, como las bandejas (decisión del 27-11-2026).
        return Includes(scope.Value, kind) && !(kind == MepKind.Tuberia && joint == JunctionKind.Cruz);
    }

    public static bool AllowsAnyFitting(ModelScope? scope, MepKind kind) => AllowsFitting(scope, kind, JunctionKind.Codo);
    public static string Label(ModelScope s) => s switch
    {
        ModelScope.Tuberias => "Tuberías",
        ModelScope.Conductos => "Conductos",
        ModelScope.Bandejas => "Bandejas y tubos eléctricos",
        ModelScope.Luminarias => "Luminarias",
        ModelScope.EquiposPci => "Equipos y PCI",
        _ => s.ToString()
    };

    /// <summary>¿Este botón modela las capas de esta clase?</summary>
    public static bool Includes(ModelScope s, MepKind kind) => s switch
    {
        ModelScope.Tuberias => kind == MepKind.Tuberia,
        ModelScope.Conductos => kind == MepKind.Conducto,
        ModelScope.Bandejas => kind is MepKind.Bandeja or MepKind.TuboElectrico,
        _ => false
    };

    /// <summary>¿Este botón coloca los bloques de esta función?</summary>
    public static bool Includes(ModelScope s, BlockRole role, string blockName) => s switch
    {
        ModelScope.Tuberias => role == BlockRole.Sanitario || role == BlockRole.Equipo && IsWaterEquipment(blockName) ||
                               role == BlockRole.Accesorio && !BlockNames.IsDuctAccessory(blockName) && !BlockNames.IsTrayAccessory(blockName),
        ModelScope.Conductos => IsDuctBlock(role, blockName),
        ModelScope.Bandejas => role == BlockRole.Accesorio && BlockNames.IsTrayAccessory(blockName),
        ModelScope.Luminarias => role is BlockRole.Luminaria or BlockRole.MandoIluminacion,
        ModelScope.EquiposPci => role is BlockRole.Pci or BlockRole.Electrico or BlockRole.Dispositivo,
        _ => false
    };

    /// <summary>
    /// Bloques de la red de aire (lo que antes colocaba «Conductos»): terminales (rejillas, difusores), conectores, equipos
    /// que no son solo de agua y accesorios de conducto (compuertas…).
    /// </summary>
    public static bool IsDuctBlock(BlockRole role, string blockName) =>
        role is BlockRole.Terminal or BlockRole.Conector || role == BlockRole.Equipo && !IsWaterEquipment(blockName) ||
        role == BlockRole.Accesorio && BlockNames.IsDuctAccessory(blockName);

    /// <summary>Equipo solo de agua (bombas, depósitos, grupos de presión, filtros, calderas…): lo coloca «Tuberías», no «Conductos».</summary>
    public static bool IsWaterEquipment(string blockName)
    {
        string u = BlockNames.Clean(blockName ?? "").ToUpperInvariant();
        return new[] { "BOMBA", "DEPOSITO", "DEPÓSITO", "GRUPOPRESION", "GRUPO DE PRESI", "FILTRO", "CALDERA", "ACUMULADOR",
                       "INTERCAMBIADOR", "HUMECTADOR", "DOSIFICADOR", "TERMO", "CALENTADOR" }.Any(u.Contains);
    }

    /// <summary>Botón que coloca los bloques de esta función (para explicar en el informe por qué otro no lo hace).</summary>
    public static string OwnerOf(BlockRole role, string blockName) =>
        string.Join(" y ", Enum.GetValues<ModelScope>().Where(s => Includes(s, role, blockName)).Select(s => "«" + Label(s) + "»"));

    public static string OwnerOf(MepKind kind) =>
        string.Join(" y ", Enum.GetValues<ModelScope>().Where(s => Includes(s, kind)).Select(s => "«" + Label(s) + "»"));
}

/// <summary>
/// Altura sobre el nivel a la que se coloca cada bloque. El plano en planta no la dice: salvo los equipos, terminales y
/// accesorios (que siguen a la altura de las redes, <see cref="PlanDefaults.HeightAboveLevelMm"/>), son valores
/// SUPUESTOS habituales que el informe señala para revisar.
/// </summary>
public static class BlockElevations
{
    /// <summary>Altura (mm) del punto de inserción sobre el nivel y si es un valor supuesto.</summary>
    public static (double Mm, bool Assumed) For(BlockRole role, string blockName)
    {
        string u = BlockNames.Clean(blockName ?? "").ToUpperInvariant();
        bool Any(params string[] k) => k.Any(u.Contains);
        switch (role)
        {
            case BlockRole.Luminaria:
                return (Any("APLIQUE", "EMERGENCIA", "BALIZA") ? 2200 : 2700, true);
            case BlockRole.MandoIluminacion:
                return (Any("DETECTOR", "SENSOR") ? 2700 : 1100, true);
            case BlockRole.Sanitario:
                return (0, false);
            case BlockRole.Electrico:
                if (Any("ARQUETA")) return (0, false);
                if (Any("TOMA", "ENCHUFE", "SCHUKO", "RJ45", "DATOS", "TV", "TELEF")) return (300, true);
                if (Any("CUADRO", "CGP", "EMBARRADO", "BORNERO", "_EQE_")) return (1500, true);
                return (1100, true);
            case BlockRole.Pci:
                if (Any("EXTINTOR", "EXTINGUISHER")) return (1000, true);
                if (Any("BIE")) return (1500, true);
                if (Any("PULSADOR")) return (1200, true);
                if (Any("SIRENA", "AVISADOR")) return (2300, true);
                if (Any("SEÑAL", "SENAL")) return (2100, true);
                if (Any("HIDRANTE")) return (0, true);
                if (Any("DETECTOR", "ROCIADOR", "SPRINKLER", "_DET_", "TERMOVELOCIM")) return (2700, true);
                return (1500, true);
            case BlockRole.Dispositivo:
                return (1500, true);
            default:
                return (PlanDefaults.HeightAboveLevelMm, false);
        }
    }

    /// <summary>¿Se monta en el techo? (si su familia es «basada en cara» se coloca en un plano horizontal mirando hacia abajo).</summary>
    public static bool IsCeilingMounted(BlockRole role, string blockName)
    {
        string u = BlockNames.Clean(blockName ?? "").ToUpperInvariant();
        bool Any(params string[] k) => k.Any(u.Contains);
        return role switch
        {
            // Las de emergencia van en pared (sobre puertas, 2,20 m), igual que apliques y balizas.
            BlockRole.Luminaria => !Any("APLIQUE", "BALIZA", "PIE", "MESA", "MURO", "PARED", "EMERGENCIA"),
            BlockRole.MandoIluminacion => Any("DETECTOR", "SENSOR"),
            BlockRole.Pci => Any("DETECTOR", "ROCIADOR", "SPRINKLER", "_DET_", "TERMOVELOCIM"),
            BlockRole.Terminal => true,
            _ => false
        };
    }
}
