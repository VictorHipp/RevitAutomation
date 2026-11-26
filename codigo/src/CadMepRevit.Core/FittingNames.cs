using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace CadMepRevit.Core;

/// <summary>Sección de un tramo, para elegir sus piezas de unión.</summary>
public enum FittingShape { Tuberia, Rectangular, Redonda, Oval }

/// <summary>
/// Reconoce por el NOMBRE de una familia si es una pieza de unión (codo, te, cruz o transición) de una sección. Sirve
/// para encontrar en las bibliotecas de cada oficina las familias con nombres distintos de los de Revit
/// («M_Codo rectangular - Radio 1», «M_Te rectangular - Recto»…). Revit comprueba después que lo es de verdad.
/// </summary>
public static class FittingNames
{
    private static string[] Tokens(string name)
    {
        string plain = new string(name.Normalize(NormalizationForm.FormD)
            .Where(ch => CharUnicodeInfo.GetUnicodeCategory(ch) != UnicodeCategory.NonSpacingMark).ToArray()).ToUpperInvariant();
        return Regex.Split(plain, "[^A-Z0-9]+").Where(t => t.Length > 0).ToArray();
    }

    /// <summary>
    /// ¿El nombre dice que es esa pieza de esa sección? Descarta los cambios de sección (rectangular a redonda), las piezas
    /// de bandeja y de tubo eléctrico, los remates y las tes pantalón.
    /// </summary>
    public static bool LooksLike(string familyName, JunctionKind kind, FittingShape shape)
    {
        var t = Tokens(familyName);
        bool Starts(params string[] keys) => t.Any(x => keys.Any(k => x.StartsWith(k, StringComparison.Ordinal)));
        bool kindOk = kind switch
        {
            JunctionKind.Codo => Starts("CODO", "ELBOW"),
            JunctionKind.Te => t.Contains("TE") || t.Contains("TEE"),
            JunctionKind.Cruz => Starts("CRUZ", "CROSS"),
            JunctionKind.Transicion => Starts("TRANSICION", "TRANSITION", "REDUCCION", "REDUCER"),
            _ => false
        };
        if (!kindOk) return false;
        if (Starts("CANAL", "ESCALERA", "BANDEJA", "TRAY", "CONDUIT", "CAJA", "REMATE", "TAPA", "CAP", "PANTALON", "MULTIFORMA", "VERTICAL"))
            return false;
        if (t.Contains("TUBO")) return false; // «Codo de tubo»: tubo eléctrico
        bool rect = Starts("RECTANG"), round = Starts("REDOND", "ROUND", "CIRCUL"), oval = Starts("OVAL");
        int shapes = (rect ? 1 : 0) + (round ? 1 : 0) + (oval ? 1 : 0);
        if (shapes > 1) return false; // cambio de sección
        return shape switch
        {
            FittingShape.Tuberia => shapes == 0 && !Starts("CONDUCTO", "DUCT"),
            FittingShape.Rectangular => rect,
            FittingShape.Redonda => round,
            FittingShape.Oval => oval,
            _ => false
        };
    }
}
