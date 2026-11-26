namespace CadMepRevit.Core;

/// <summary>Qué se construye en Revit a partir de las líneas de una capa CAD.</summary>
public enum MepKind
{
    NoImportar,
    Tuberia,
    Conducto,
    Bandeja,
    TuboElectrico,
    /// <summary>Muro de arquitectura (dibujado a doble línea; el espesor se mide en el CAD).</summary>
    Muro
}

public static class MepKinds
{
    public static string Label(MepKind kind) => kind switch
    {
        MepKind.NoImportar => "No importar",
        MepKind.Tuberia => "Tubería",
        MepKind.Conducto => "Conducto (HVAC)",
        MepKind.Bandeja => "Bandeja de cables",
        MepKind.TuboElectrico => "Tubo eléctrico (conduit)",
        MepKind.Muro => "Muro (arquitectura)",
        _ => kind.ToString()
    };

    public static readonly MepKind[] All =
        { MepKind.NoImportar, MepKind.Tuberia, MepKind.Conducto, MepKind.Bandeja, MepKind.TuboElectrico, MepKind.Muro };

    /// <summary>
    /// Sugerencia inicial según el nombre de la capa. Es solo una propuesta:
    /// el usuario siempre la revisa en la ventana de mapeo antes de construir.
    /// </summary>
    public static MepKind Suggest(string layer) =>
        UserRules.Current.LayerRule(layer) ?? ByName(layer) ?? MepKind.NoImportar;

    /// <summary>
    /// ¿El programa no sabe qué es esta capa? Ni regla de la oficina, ni palabra de instalación, anotación o arquitectura.
    /// Es lo que se pregunta al usuario (y se recuerda).
    /// </summary>
    public static bool IsUnknownLayer(string layer) =>
        UserRules.Current.LayerRule(layer) is null && ByName(layer) is null;

    /// <summary>Clase según las palabras del nombre; null si el nombre no dice nada conocido.</summary>
    private static MepKind? ByName(string layer)
    {
        string n = (layer ?? "").ToUpperInvariant();
        bool Has(params string[] keys) => keys.Any(k => n.Contains(k));
        bool Word(params string[] keys) => keys.Any(k => System.Text.RegularExpressions.Regex.IsMatch(n, $@"(^|[^A-Z]){k}([^A-Z]|$)"));

        // Anotaciones: nunca son elementos MEP aunque el nombre lleve "GAS", "AF"…
        if (Has("TEXT", "COTA", "ROTUL", "CAJETIN", "CAJETÍN", "SIMBOLOG", "LEYENDA", "ANOTA", "EJES", "REJILLA CAZ", "BASE ARQ", "MAA", "EQUIPOS"))
            return MepKind.NoImportar;
        // Capas de los planos exportados de Revit (norma AIA): rótulos y directrices (-IDEN), anotaciones (-ANNO, -NOTE),
        // cotas (-DIMS), símbolos (-SYMB), equipos y difusores dibujados como líneas (M-EQPM, M-HVAC-CDFF…), que
        // llegan también como bloques. No son tramos.
        // RISE/DROP: símbolos de subidas y bajadas; FIXT/LITE/FIRE/COMM: aparatos y luminarias dibujados como líneas
        // (llegan también como bloques). RADIO: radios de cobertura de extintores, BIE o detectores.
        if (Word("IDEN", "ANNO", "NOTE", "DIMS", "SYMB", "TAG", "PATT", "EQPM", "CDFF", "RDFF", "GRIL", "DETL", "RISE", "DROP", "FIXT", "LITE", "FIRE", "COMM")
            || Has("RADIO"))
            return MepKind.NoImportar;
        // Capas con el nombre de una categoría de Revit que NO es un tramo (planos exportados con la tabla de capas por
        // categoría, en español o inglés): etiquetas, uniones y accesorios (llegan también como bloques), y rótulos (ROT).
        if (Has("ETIQUETA", "UNIONES DE", "ACCESORIOS DE", "TAGS", "FITTING", "ACCESSOR") || Word("ROT", "ROTULO", "ROTULOS", "RÓTULO", "RÓTULOS", "FIT", "FITT"))
            return MepKind.NoImportar;
        // Símbolos, esquemas de principio (no son la planta), rejillas dibujadas con líneas (llegan como bloques),
        // valvulería, subidas/bajadas y equipos dibujados con líneas: no son tramos.
        if (Has("ESQUEMA", "SIMBOLO", "SÍMBOLO", "TERMINALES DE AIRE", "VALVUL", "VÁLVUL", "EQUIPOS MEC", "MECH_EQ", "MECH-EQ")
            || Word("SIMB", "SYM", "SUBIDA", "SUBIDAS", "BAJADA", "BAJADAS", "EQP"))
            return MepKind.NoImportar;
        // El nombre de un cuadro puede indicar el destino de un recorrido si también se nombra el tramo.
        if (n.EndsWith("-PANL", StringComparison.Ordinal) ||
            Word("CUADRO", "CUADROS", "PANEL", "PANELS", "PANELES", "CGP", "CGMP", "SWITCHBOARD", "SWITCHBOARDS") &&
            !Word("TUB", "TUBO", "TUBOS", "TUBERIA", "TUBERÍA", "TUBERIAS", "TUBERÍAS", "CNDT", "CONDUIT", "CONDUITS", "CORRUGADO",
                "BANDEJA", "BANDEJAS", "TRAY", "TRAYS", "CABLETRAY", "REJIBAND"))
            return MepKind.NoImportar;
        if (Has("MURO", "WALL", "TABIQUE", "CERRAMIENTO", "FACHADA", "PARED", "FABRICA", "FÁBRICA", "LADRILLO", "TRASDOS")) return MepKind.Muro;
        if (Has("BANDEJA", "TRAY", "REJIBAND", "CABLETRAY", "CANALETA")) return MepKind.Bandeja;
        // E-POWR-CNDT / E-POWR-CNTR: tubos eléctricos de los planos exportados de Revit.
        if (Has("CONDUIT", "TUBO_ELEC", "TUBO-ELEC", "TUBOELEC", "CORRUGADO") || Word("CNDT", "POWR")) return MepKind.TuboElectrico;
        if (Has("CONDUCTO", "DUCT")) return MepKind.Conducto;
        // Fontanería y saneamiento antes que palabras ambiguas como "RETORNO"
        if (Has("TUBERIA", "TUBERÍA", "PIPE", "FONT", "SANEAM", "PLUVIAL", "FECAL", "RIEGO", "CALIENTE", "FRIA", "FRÍA",
                "RACS", "AFS", "AFC", "BIE", "ROCIA", "SPRINK", "CALEF", "RADIADOR", "SUELO_RAD", "AGUA", "_IS ", "_IS_", "CONDENSAD")
            || Word("AF", "ACS", "GAS", "PCI", "TUB", "TUBO", "TUBOS", "SAN"))
            return MepKind.Tuberia;
        if (Has("CLIMA", "HVAC", "VENTIL", "IMPULSION", "IMPULSIÓN", "RETORNO", "EXTRACC", "_CL_", "_VT_", "AIRE"))
            return MepKind.Conducto;
        // Electricidad y telecomunicaciones: las líneas son recorridos de circuitos → tubo eléctrico
        if (Has("_IE_", "ELECTR", "FUERZA", "ALUMBR", "_TEL", "TELECOM", "ICT", "DATOS", "BAJA TENSION", "BT_"))
            return MepKind.TuboElectrico;
        // Conocidas que NO son instalaciones (no se pregunta por ellas): disciplinas de arquitectura, estructura, civil,
        // interiores y general de la norma AIA (A-, S-, C-, I-, G-, L-), y las categorías de arquitectura más comunes.
        if (System.Text.RegularExpressions.Regex.IsMatch(n, @"^[ASCIGL]-") || n == "DEFPOINTS" || Word("EST") ||
            Has("PUERTA", "VENTANA", "MOBILIAR", "MUEBLE", "ESCALER", "SUELO", "TECHO", "CUBIERTA", "PILAR", "VIGA", "FORJADO",
                "ESTRUCT", "HABITAC", "CARPINTER", "ARQ", "SANITARIO", "DOOR", "WINDOW", "FURN", "STAIR", "COLUMN", "BEAM",
                "FLOOR", "CEILING", "ROOF", "GRID", "PARKING", "APARCAM", "CERRAJ", "BARANDILL", "RAILING", "LÍNEAS", "LINEAS",
                "MÁS ALLÁ", "MAS ALLA", "OCULT", "HIDDEN", "FLECHA", "SOMBREAD", "HATCH", "IMAGE", "XREF"))
            return MepKind.NoImportar;
        // Luminarias dibujadas con líneas (las coloca «Luminarias», ver LooseFixtures) y lo que exportan con ellas los
        // programas de cálculo de alumbrado (DIALux, Litestar…): contornos, recintos, límites de área. No son tramos.
        if (LooseFixtures.IsFixtureLayer(layer ?? "") || Word("LUM", "CONT", "ROOM", "BNDY") || Has("AREA-", "_AREA", "-AREA"))
            return MepKind.NoImportar;
        return null;
    }
    /// <summary>Diámetro por defecto (mm) sugerido según el tipo.</summary>
    public static double DefaultSizeMm(MepKind kind) => kind switch
    {
        MepKind.Tuberia => 25,
        MepKind.Conducto => 200,
        MepKind.Bandeja => 200,
        MepKind.TuboElectrico => 25,
        _ => 0
    };

    public static double DefaultHeightMm(MepKind kind) => kind switch
    {
        MepKind.Conducto => 200,
        MepKind.Bandeja => 60,
        MepKind.Muro => 3000,
        _ => 0
    };
}
