using System.Text.RegularExpressions;

namespace CadMepRevit.Core;

/// <summary>
/// Huella en planta de un bloque: rectángulo orientado que envuelve su geometría real,
/// en unidades del dibujo. Se mide en el CAD (no se supone nada).
/// </summary>
public sealed record Footprint(double CenterX, double CenterY, double AngleRad, double SizeX, double SizeY)
{
    /// <summary>Esquinas del rectángulo, en unidades del dibujo.</summary>
    public IEnumerable<(double X, double Y)> Corners()
    {
        double c = Math.Cos(AngleRad), s = Math.Sin(AngleRad);
        foreach (var (u, v) in new[] { (-0.5, -0.5), (0.5, -0.5), (0.5, 0.5), (-0.5, 0.5) })
        {
            double x = u * SizeX, y = v * SizeY;
            yield return (CenterX + x * c - y * s, CenterY + x * s + y * c);
        }
    }

    public bool Contains(double x, double y, double margin)
    {
        double c = Math.Cos(AngleRad), s = Math.Sin(AngleRad);
        double dx = x - CenterX, dy = y - CenterY;
        double u = dx * c + dy * s, v = -dx * s + dy * c;
        return Math.Abs(u) <= SizeX / 2 + margin && Math.Abs(v) <= SizeY / 2 + margin;
    }

    /// <summary>Rectángulo de área mínima entre los ángulos candidatos.</summary>
    public static Footprint? Fit(IReadOnlyList<(double X, double Y)> pts, IEnumerable<double> candidateAngles)
    {
        if (pts.Count == 0) return null;
        Footprint? best = null;
        double bestArea = double.MaxValue;
        foreach (double a0 in candidateAngles.Append(0.0))
        {
            double a = a0 % (Math.PI / 2);
            if (a < 0) a += Math.PI / 2;
            double c = Math.Cos(a), s = Math.Sin(a);
            double minU = double.MaxValue, maxU = double.MinValue, minV = double.MaxValue, maxV = double.MinValue;
            foreach (var (x, y) in pts)
            {
                double u = x * c + y * s, v = -x * s + y * c;
                minU = Math.Min(minU, u); maxU = Math.Max(maxU, u);
                minV = Math.Min(minV, v); maxV = Math.Max(maxV, v);
            }
            double area = Math.Max(maxU - minU, 1e-9) * Math.Max(maxV - minV, 1e-9);
            if (area >= bestArea * (1 - 1e-6)) continue;
            bestArea = area;
            double cu = (minU + maxU) / 2, cv = (minV + maxV) / 2;
            best = new Footprint(cu * c - cv * s, cu * s + cv * c, a, maxU - minU, maxV - minV);
        }
        return best;
    }
}

/// <summary>Para qué sirve un bloque, deducido de su nombre.</summary>
public enum BlockRole
{
    Otro,
    /// <summary>Codo, te, transición… dibujados como bloque: Revit los crea con las uniones.</summary>
    PiezaDeUnion,
    Equipo,
    Terminal,
    Accesorio,
    Dispositivo,
    Conector,
    /// <summary>Tomas de corriente, cuadros, cajas, telecomunicaciones…</summary>
    Electrico,
    /// <summary>Aparatos sanitarios, sumideros, botes sifónicos, arquetas…</summary>
    Sanitario,
    /// <summary>Luminarias: puntos de luz, downlights, plafones, tiras LED, luminarias de emergencia…</summary>
    Luminaria,
    /// <summary>Mandos de alumbrado: interruptores, conmutadores, detectores de presencia.</summary>
    MandoIluminacion,
    /// <summary>Protección contra incendios: extintores, BIE, detectores, pulsadores de alarma, sirenas, rociadores, señales.</summary>
    Pci
}

/// <summary>Interpreta los nombres de bloque (incluidos los que genera Revit al exportar a DWG).</summary>
public static class BlockNames
{
    // Revit exporta "Familia - Tipo-1234567-CLI - Vista": se quita el sufijo "-id-…".
    private static readonly Regex RevitSuffix = new(@"^(?<core>.+)-(?<id>V?\d+)-(?<rest>[^-]*)(\s-\s.*)?$", RegexOptions.Compiled);
    private static readonly Regex SizeRx = new(@"(\d{2,4})\s*[xX×]\s*(\d{2,4})", RegexOptions.Compiled);

    /// <summary>Nombre sin el sufijo de exportación de Revit.</summary>
    public static string Clean(string blockName)
    {
        var m = RevitSuffix.Match(blockName);
        if (m.Success) return m.Groups["core"].Value.Trim();
        // Vistas con guiones en su nombre («…-1000102-PL01-02-ALUMBRADO-00_PLANTA BAJA»): se corta en el identificador.
        var m2 = RevitSuffixLoose.Match(blockName);
        return m2.Success ? m2.Groups["core"].Value.Trim() : blockName.Trim();
    }

    private static readonly Regex RevitSuffixLoose = new(@"^(?<core>.+? - .+?)-(V\d+|\d{4,})-.+$", RegexOptions.Compiled);

    /// <summary>Posibles parejas (familia, tipo) del nombre, de la más larga a la más corta.</summary>
    public static List<(string Family, string Type)> Candidates(string blockName)
    {
        string core = Clean(blockName);
        var parts = core.Split(new[] { " - " }, StringSplitOptions.None);
        var list = new List<(string, string)>();
        for (int k = parts.Length - 1; k >= 1; k--)
            list.Add((string.Join(" - ", parts.Take(k)).Trim(), string.Join(" - ", parts.Skip(k)).Trim()));
        list.Add((core, ""));
        return list;
    }

    /// <summary>Función del bloque según su nombre y, si el nombre no lo dice, según su capa.</summary>
    /// <summary>
    /// Bloque que no es un elemento: leyendas de los planos exportados de Revit («Family - ___-123-… LEYENDA …»)
    /// y referencias externas insertadas como bloque («…_rvt-1-…»). No se colocan nunca.
    /// </summary>
    public static bool IsLegendOrReference(string blockName)
    {
        string u = (blockName ?? "").ToUpperInvariant();
        return u.Contains("LEYENDA") || u.Contains("LEGEND") || Regex.IsMatch(u, @"^FAMILY\s*-\s*_+") || Regex.IsMatch(u, @"_RVT-\d");
    }

    /// <summary>Flechas (señalización de aparcamiento, sentido del aire…): símbolos, no aparatos, sea cual sea su capa.</summary>
    public static bool IsArrowSymbol(string blockName) =>
        Regex.IsMatch(Clean(blockName ?? "").ToUpperInvariant(), @"(^|[^A-ZÁÉÍÓÚÑ])(FLECHA|FLECHAS|ARROW)([^A-ZÁÉÍÓÚÑ]|$)");

    /// <summary>
    /// Capa de anotaciones de los planos exportados de Revit (-ANNO, -IDEN, -NOTE): los bloques que hay en ella son
    /// símbolos (p. ej. «SimboloDePuertasconRejilla» en A-ANNO-NOTE), aunque su nombre parezca un elemento.
    /// </summary>
    public static bool IsAnnotationLayer(string layer) =>
        Regex.IsMatch((layer ?? "").ToUpperInvariant(), @"(^|[^A-Z])(ANNO|IDEN|NOTE)([^A-Z]|$)");

    /// <summary>Bloque anónimo («A$C…», «*U…»): trozo de dibujo de una referencia, no un aparato con nombre.</summary>
    public static bool IsAnonymous(string blockName) => Regex.IsMatch(blockName ?? "", @"^(A\$C|\*)", RegexOptions.IgnoreCase);

    public static BlockRole Role(string blockName, string layer)
    {
        // Lo que la oficina ha dicho que es este bloque manda sobre todo lo demás.
        if (UserRules.Current.BlockRule(blockName) is BlockRole ruled) return ruled;
        if (IsLegendOrReference(blockName) || IsAnnotationLayer(layer) || IsArrowSymbol(blockName)) return BlockRole.Otro;
        if (IsElectricalChamber(blockName, layer)) return BlockRole.Electrico;
        var r = Role(blockName);
        if (r != BlockRole.Otro) return r;
        string l = (layer ?? "").ToUpperInvariant();
        bool L(params string[] keys) => keys.Any(k => l.Contains(k));
        if (L("TEXT", "COTA", "CAJETIN", "CAJETÍN", "SIMBOLOG", "LEYENDA")) return BlockRole.Otro;
        // Capas de los planos exportados de Revit (norma AIA): E-LITE-EQPM luminarias, E-LITE mandos de alumbrado,
        // E-FIRE detección de incendios, E-ELEC / E-POWR / E-COMM electricidad y telecomunicaciones, M-EQPM equipos.
        bool W(params string[] keys) => keys.Any(k => Regex.IsMatch(l, $@"(^|[^A-Z]){k}([^A-Z]|$)"));
        // Los bloques anónimos («A$C…», «*U…») de las referencias externas son trozos de dibujo, no aparatos: la capa
        // no basta para darles función de luminaria o PCI.
        bool anonymous = IsAnonymous(blockName ?? "");
        if (!anonymous && L("LITE-EQPM", "ILUMINAC", "ALUMBR", "LUMINAR")) return BlockRole.Luminaria;
        // Capa de emergencias («EMERGENCIAS», «07-EMERGENCIAS», «E-EMERG»…): luminarias de emergencia.
        if (!anonymous && Regex.IsMatch(l, @"(^|[^A-Z])EMERG") && !L("PCI", "INCEND", "FIRE")) return BlockRole.Luminaria;
        if (!anonymous && W("LITE", "LGHT")) return BlockRole.MandoIluminacion;
        if (!anonymous && (W("FIRE", "PCI") || L("INCEND", "EXTINC", "EXTINT"))) return BlockRole.Pci;
        if (L("FANCOIL", "EQUIPO", "MAQUINA", "MÁQUINA", "CLIMATIZ") || W("EQPM") && W("M")) return BlockRole.Equipo;
        if (L("_IE_", "ELECTR", "FUERZA", "_TEL", "TELECOM", "ICT") || W("ELEC", "POWR", "COMM")) return BlockRole.Electrico;
        if (L("ARQUETA", "SUMIDERO", "_IS ", "_IS_", "RESIDUAL", "APARATOS SANIT") || W("SANR", "PLMB")) return BlockRole.Sanitario;
        if (L("REJILLA", "DIFUSOR", "TERMINAL")) return BlockRole.Terminal;
        return BlockRole.Otro;
    }

    public static BlockRole Role(string blockName)
    {
        string u = Clean(blockName).ToUpperInvariant();
        bool Any(params string[] keys) => keys.Any(k => u.Contains(k));
        bool Word(params string[] keys) => keys.Any(k => Regex.IsMatch(u, $@"(^|[^A-ZÁÉÍÓÚÑ]){k}([^A-ZÁÉÍÓÚÑ]|$)"));
        if (IsLegendOrReference(blockName)) return BlockRole.Otro;
        if (IsArrowSymbol(blockName)) return BlockRole.Otro;
        if (IsTrayAccessory(blockName)) return BlockRole.Accesorio;
        if (Any("CONECTOR", "CONNECTOR")) return BlockRole.Conector;
        // Piezas de unión antes que todo lo demás: «M_Codo - Soldado - Genérico1 - PCI», «M_Toma de derivación…».
        if (Any("CODO", "ELBOW", "TRANSICI", "TRANSITION", "CRUZ", "CROSS", "REDUCC", "REDUCTOR", "MANGUITO", "UNION FITTING", "DERIVACION",
                "DERIVACIÓN", "REMATE", "INJERTO")
            || (Any("EMPALME") && !Any("CAJA", "BORNE", "REGLETA"))   // «Caja de empalme» es eléctrica, no una pieza de tubería
            || Regex.IsMatch(u, @"(^|[^A-Z])(TE|TEE|TAPON|TAPÓN)([^A-Z]|$)")) return BlockRole.PiezaDeUnion;
        // Luminarias antes que «eléctrico» (las tomas y los cuadros siguen siendo eléctricos).
        if (Any("LUMINARIA", "LUM_", "DOWNLIGHT", "PUNTO DE LUZ", "PUNTODELUZ", "TIRA LED", "TIRALED", "PLAFON", "PLAFÓN", "APLIQUE",
                "PANTALLA LED", "PROYECTOR", "LAMPARA", "LÁMPARA", "LIGHT FIXTURE", "LUZ DE TECHO", "LUZ COLGANTE", "BALIZA")
            || Word("FOCO"))
            return BlockRole.Luminaria;
        // Alumbrado de emergencia: «Emergencia», «Emergency», «Autónomo»… salvo lo que no es una luminaria (pulsador o
        // parada de emergencia, duchas y lavaojos, puertas y salidas, escaleras, teléfonos).
        if (Any("EMERGENCIA", "EMERGENCY", "AUTONOMO", "AUTÓNOMO", "LUZ DE EMERG", "ALUMBRADO DE EMERG")
            && !Any("PULSADOR", "PARADA", "SETA", "BOTON", "BOTÓN", "DUCHA", "LAVAOJOS", "PUERTA", "DOOR", "SALIDA DE EMERG",
                    "ESCALERA", "TELEFONO", "TELÉFONO", "INTERFONO", "INTERCOM", "CORTE"))
            return BlockRole.Luminaria;
        if (Any("INTERRUPTOR", "CONMUTADOR", "PULSADOR DE LUZ", "DETECTOR DE PRESENCIA", "DETECTOR DE MOVIMIENTO", "REGULADOR DE LUZ",
                "DIMMER", "_DIL_"))
            return BlockRole.MandoIluminacion;
        if (IsDrainageChamber(u) && !u.Contains("SEPARADOR DE AIRE")) return BlockRole.Sanitario;
        // Equipos de las salas de bombas (también los de PCI: grupo de presión, depósitos): se conectan a sus tuberías.
        if (Any("EQM_", "DEPOSITO", "DEPÓSITO", "GRUPOPRESION", "GRUPO DE PRESION", "GRUPO DE PRESIÓN", "BOMBA"))
            return BlockRole.Equipo;
        if (Any("EXTINTOR", "EXTINGUISHER", "HIDRANTE", "ROCIADOR", "SPRINKLER", "SIRENA", "DETECTOR DE HUMO", "DETECTOR DE INCENDIO",
                "DETECTORINCENDIO", "DETECTOR TERMO", "TERMOVELOCIM", "DETECTOR OPTICO", "DETECTOR ÓPTICO", "PULSADOR DE ALARMA",
                "PULSADOR ALARMA", "ALARMA DE INCENDIO", "AVISADOR", "CENTRAL DE INCENDIO", "CENTRAL DE DETEC", "CENTRALITA DE INCEN",
                "_PCI", "PCI_", "_DAI_", "_DET_", "SEÑAL DE EVAC", "SEÑAL DE SALIDA", "EXTINCION", "EXTINCIÓN")
            || Word("BIE", "PCI"))
            return BlockRole.Pci;
        if (Any("TOMA DE CORRIENTE", "ENCHUFE", "PULSADOR", "CUADRO", "RJ45", "TOMA TV", "TOMA DE DATOS", "TELEF", "SCHUKO",
                "BASE DE ENCHUFE", "_EQE_", "EMBARRADO", "BORNERO", "CAJA DE REGISTRO", "CAJA DE CONEX")
            || Word("CGP", "TOMA"))
            return BlockRole.Electrico;
        if (Any("LAVABO", "INODORO", "DUCHA", "BAÑERA", "BANERA", "FREGADERO", "BIDE", "BIDÉ", "URINARIO", "SUMIDERO",
                "BOTE", "SIFON", "SIFÓN", "ARQUETA", "VERTEDERO") || Word("WC"))
            return BlockRole.Sanitario;
        if (Any("REJILLA", "DIFUSOR", "DIFFUSER", "GRILLE", "TOBERA", "BOCA", "TER_", "TERMINAL")) return BlockRole.Terminal;
        // Accesorios antes que dispositivos: «XX_ACC_CompuertaCortafuegos» es una compuerta, no un sensor.
        if (IsDuctAccessory(blockName) || Any("VALVULA", "VÁLVULA", "VALVE", "FILTRO", "GRIFO", "LLAVE DE PASO", "CONTADOR")) return BlockRole.Accesorio;
        if (Any("TERMOSTATO", "THERMOSTAT", "SENSOR", "SONDA", "DETECTOR", "ACC_")) return BlockRole.Dispositivo;
        if (Any("FANCOIL", "FAN COIL", "VENTILADOR", "EXTRACTOR", "EQM", "EQUIPO", "CALDERA", "CLIMATIZADOR", "RECUPERADOR", "UTA", "SPLIT", "CASSETTE") || Word("FAN"))
            return BlockRole.Equipo;
        return BlockRole.Otro;
    }

    /// <summary>Accesorio que va en un conducto (compuerta, regulador, silenciador); los demás van en tuberías.</summary>
    public static bool IsDuctAccessory(string blockName)
    {
        string u = Clean(blockName).ToUpperInvariant();
        return !new[] { "VÁLVULA", "VALVULA", "VALVE", "GRIFO" }.Any(u.Contains) &&
            new[] { "COMPUERTA", "DAMPER", "REGULADOR DE CAUDAL", "SILENCIADOR", "CORTAFUEGO", "CORTA-FUEGO" }.Any(u.Contains);
    }

    public static bool IsTrayAccessory(string blockName)
    {
        string name = PipeNetworks.Normalize(Clean(blockName));
        return Regex.IsMatch(name, @"\b(BANDEJA|CABLE[ -]+TRAY)\b") &&
            Regex.IsMatch(name, @"\b(ACCESORIO|ACCESSORY|ACCESSORIES)\b");
    }

    public static string Label(BlockRole r) => r switch
    {
        BlockRole.PiezaDeUnion => "Pieza de unión",
        BlockRole.Equipo => "Equipo",
        BlockRole.Terminal => "Terminal de aire",
        BlockRole.Accesorio => "Accesorio",
        BlockRole.Dispositivo => "Dispositivo",
        BlockRole.Conector => "Conector",
        BlockRole.Electrico => "Eléctrico",
        BlockRole.Sanitario => "Sanitario",
        BlockRole.Luminaria => "Luminaria",
        BlockRole.MandoIluminacion => "Mando de alumbrado",
        BlockRole.Pci => "Protección contra incendios",
        _ => "Otro"
    };

    public static bool IsElectricalChamber(string blockName, string layer) =>
        Clean(blockName).Contains("ARQUETA", StringComparison.OrdinalIgnoreCase) &&
        Regex.IsMatch(PipeNetworks.Normalize(layer), @"^E[-_ ]|(?<![A-Z])(?:ELEC|ELECTRIC\w*|POWR|COMM|BT|TELECOM\w*|ICT|FUERZA)(?![A-Z])");

    public static bool IsDrainageChamber(string blockName) =>
        Regex.IsMatch(PipeNetworks.Normalize(Clean(blockName)), @"ARQUETA|SUMIDERO|SIFON|CANALETA|SEPARADOR|ARENERO|MANHOLE|FLOOR DRAIN|(?<![A-Z])(?:BOTE|POZO)(?![A-Z])|POZODEBOMBEO");

    public static (double Mm, bool Assumed) ChamberDepth(string blockName)
    {
        string name = PipeNetworks.Normalize(Clean(blockName));
        var size = Regex.Match(name, @"(\d{2,4})\s*[X×]\s*[A-Z]*\s*(\d{2,4})\s*[X×]\s*[A-Z]*\s*(\d{2,4})(?!\d)");
        if (size.Success && int.TryParse(size.Groups[3].Value, out int depth) && depth > 0) return (depth, false);
        return (name.Contains("POZO") || name.Contains("MANHOLE") ? PlanDefaults.AssumedManholeDepthMm : PlanDefaults.AssumedChamberDepthMm, true);
    }

    /// <summary>Medida "600x100" escrita en el nombre (ancho, alto), si la hay.</summary>
    public static (double W, double H)? SizeInName(string blockName)
    {
        var m = SizeRx.Match(Clean(blockName));
        return m.Success ? (double.Parse(m.Groups[1].Value), double.Parse(m.Groups[2].Value)) : null;
    }
}
