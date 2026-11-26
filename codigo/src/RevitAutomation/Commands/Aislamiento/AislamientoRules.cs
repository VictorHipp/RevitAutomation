#nullable enable
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace RevitAutomation.Addin.Commands.Aislamiento;

// RITE (RD 1027/2007, texto consolidado BOE 02/08/2022), IT 1.2.4.2.1.2 «procedimiento simplificado».
// Espesores mínimos de aislamiento (mm) para un material de referencia λ = 0,040 W/(m·K) a 10 °C.
// D = diámetro exterior de la tubería SIN aislar (mm).
public static class AislRite
{
    public const double LambdaRef = 0.040;
    private const double Tolerance = 0.05;
    private static readonly CultureInfo Spanish = CultureInfo.GetCultureInfo("es-ES");

    // Filas de las tablas 1 a 4: D ≤ 35 | 35 < D ≤ 60 | 60 < D ≤ 90 | 90 < D ≤ 140 | 140 < D.
    // Columnas calientes por temperatura MÁXIMA: 40…60 | >60…100 | >100…180.
    private static readonly double[,] HotInterior =   // Tabla 1.2.4.2.1
    {
        { 25, 25, 30 },
        { 30, 30, 40 },
        { 30, 30, 40 },
        { 30, 40, 50 },
        { 35, 40, 50 }
    };
    private static readonly double[,] HotExterior =   // Tabla 1.2.4.2.2
    {
        { 35, 35, 40 },
        { 40, 40, 50 },
        { 40, 40, 50 },
        { 40, 50, 60 },
        { 45, 50, 60 }
    };
    // Columnas frías por temperatura MÍNIMA: >−10…0 | >0…10 | >10.
    private static readonly double[,] ColdInterior =  // Tabla 1.2.4.2.3
    {
        { 30, 25, 20 },
        { 40, 30, 20 },
        { 40, 30, 30 },
        { 50, 40, 30 },
        { 50, 40, 30 }
    };
    private static readonly double[,] ColdExterior =  // Tabla 1.2.4.2.4
    {
        { 50, 45, 40 },
        { 60, 50, 40 },
        { 60, 50, 50 },
        { 70, 60, 50 },
        { 70, 60, 50 }
    };
    // Tabla 1.2.4.2.5 (circuitos frigoríficos de climatización), {interior, exterior}. Límites impresos:
    // D ≤ 13 | 13 < D < 26 | 26 < D < 35 | 35 < D < 90 | D > 90. El texto deja sin definir D = 26, 35 y 90; se asignan a
    // la fila SUPERIOR (criterio conservador): D ≤ 13 | 13 < D < 26 | 26 ≤ D < 35 | 35 ≤ D < 90 | D ≥ 90.
    private static readonly double[,] Refrigerant =
    {
        { 10, 15 },
        { 15, 20 },
        { 20, 25 },
        { 30, 40 },
        { 40, 50 }
    };

    // Tabla 1.2.4.2 (redes de ACS, 40…60 °C), por fila de diámetro.
    private static readonly double[] AcsInterior = { 30, 35, 35, 45, 45 };
    private static readonly double[] AcsExterior = { 40, 45, 45, 55, 55 };

    private static int DiameterRow(double d) => d <= 35 ? 0 : d <= 60 ? 1 : d <= 90 ? 2 : d <= 140 ? 3 : 4;
    private static int RefrigerantRow(double d) => d <= 13 ? 0 : d < 26 ? 1 : d < 35 ? 2 : d < 90 ? 3 : 4;

    /// <summary>Espesor de tabla (λ ref.) para Acs (= caliente + 5 mm), ClimaCaliente, ClimaFrio y Frigorifica; 0 en otro caso.</summary>
    public static double TableMm(AislRed red, double outerMm, double temperatureC, bool exterior,
        out string tableName, out bool outOfRange)
    {
        outOfRange = false;
        string place = exterior ? "exterior" : "interior";
        switch (red)
        {
            case AislRed.Acs:
            case AislRed.ClimaCaliente:
            {
                bool acs = red == AislRed.Acs;
                tableName = acs
                    ? $"Tabla 1.2.4.2 (ACS, {place})"
                    : exterior ? "Tabla 1.2.4.2.2 (fluidos calientes, exterior)" : "Tabla 1.2.4.2.1 (fluidos calientes, interior)";
                if (temperatureC < 40) return 0;
                int column = temperatureC <= 60 ? 0 : temperatureC <= 100 ? 1 : 2;
                if (temperatureC > 180) outOfRange = true;
                int row = DiameterRow(outerMm);
                double value = (exterior ? HotExterior : HotInterior)[row, column];
                if (!acs) return value;
                // ACS (funcionamiento continuo): tablas de fluidos calientes + 5 mm. La Tabla 1.2.4.2 del BOE (ACS, 40…60 °C)
                // NO coincide con «+5» para D > 90 (da 45/55 frente a 35-40/45-50): se toma el mayor de ambos criterios,
                // así a 40…60 °C resulta exactamente la Tabla 1.2.4.2 y por encima de 60 °C nunca menos de caliente + 5.
                return Math.Max(value + 5, (exterior ? AcsExterior : AcsInterior)[row]);
            }
            case AislRed.ClimaFrio:
            {
                tableName = exterior ? "Tabla 1.2.4.2.4 (fluidos fríos, exterior)" : "Tabla 1.2.4.2.3 (fluidos fríos, interior)";
                if (temperatureC <= -10) outOfRange = true;
                int column = temperatureC <= 0 ? 0 : temperatureC <= 10 ? 1 : 2;
                return (exterior ? ColdExterior : ColdInterior)[DiameterRow(outerMm), column];
            }
            case AislRed.Frigorifica:
                tableName = $"Tabla 1.2.4.2.5 (circuitos frigoríficos, {place})";
                return Refrigerant[RefrigerantRow(outerMm), exterior ? 1 : 0];
            default:
                tableName = "";
                return 0;
        }
    }

    /// <summary>
    /// Conversión a otra conductividad para secciones circulares (IT 1.2.4.2.1.2, punto 8):
    /// d = (D/2) · [ EXP( (λ/λref) · ln((D + 2·dref)/D) ) − 1 ].
    /// </summary>
    public static double ConvertLambda(double tableMm, double outerMm, double lambda)
    {
        if (tableMm <= 0) return 0;
        if (outerMm <= 0 || lambda <= 0) return tableMm;
        return outerMm / 2 * (Math.Exp(lambda / LambdaRef * Math.Log((outerMm + 2 * tableMm) / outerMm)) - 1);
    }

    /// <summary>Redondea AL ALZA al siguiente espesor comercial (tolerancia 0,05 mm); por encima del mayor, al múltiplo de 10 mm.</summary>
    public static double RoundCommercial(double mm, IReadOnlyList<double> commercial)
    {
        if (mm <= Tolerance) return 0;
        foreach (double c in commercial.Where(c => c > 0).OrderBy(c => c))
            if (c >= mm - Tolerance) return c;
        return Math.Ceiling((mm - Tolerance) / 10) * 10;
    }

    private static string N(double value) => value.ToString("0.#", Spanish);
    private static string L(double value) => value.ToString("0.000", Spanish);

    private static AislRequirement NotRequired(AislRed red, AislMaterialGroup material, double outerMm, double temperatureC,
        bool exterior, double lambda, string rule) =>
        new(false, red, material, outerMm, temperatureC, exterior, 0, lambda, 0, 0, rule);

    // Exigencia calculada desde una tabla (sin redondear), con su texto de justificación sin la parte comercial.
    private static (double Table, double Converted, string Text) Compute(AislRed red, double outerMm, double temperatureC,
        bool exterior, double lambda)
    {
        double table = TableMm(red, outerMm, temperatureC, exterior, out string tableName, out bool outOfRange);
        double converted = ConvertLambda(table, outerMm, lambda);
        string temperature = red == AislRed.Frigorifica ? "" : $", {N(temperatureC)} °C";
        string text = $"RITE {tableName}{temperature}, D {N(outerMm)} mm: {N(table)} mm; λ {L(lambda)} → {N(converted)} mm";
        if (red == AislRed.Acs && temperatureC > 60) text += "; ACS funcionamiento continuo +5 mm";
        if (outOfRange) text += "; fuera de tabla: use el procedimiento alternativo";
        return (table, converted, text);
    }

    public static AislRequirement Requirement(AislRed red, double outerMm, AislSettings s)
    {
        bool ext = s.Exterior;
        switch (red)
        {
            case AislRed.Acs:
            case AislRed.ClimaCaliente:
            {
                double t = red == AislRed.Acs ? s.AcsTemperatureC : s.HeatingTemperatureC;
                if (t < 40)
                    return NotRequired(red, AislMaterialGroup.Caliente, outerMm, t, ext, s.HotLambda,
                        "No exigido: fluido por debajo de 40 °C");
                return Build(red, AislMaterialGroup.Caliente, outerMm, t, ext, s.HotLambda, s, Compute(red, outerMm, t, ext, s.HotLambda), "");
            }
            case AislRed.ClimaFrio:
            {
                double t = s.ChilledTemperatureC;
                return Build(red, AislMaterialGroup.Frio, outerMm, t, ext, s.ColdLambda, s, Compute(red, outerMm, t, ext, s.ColdLambda), "");
            }
            case AislRed.ClimaMixto:
            {
                // IT 1.2.4.2.1.2 punto 4: redes que funcionan en frío y en calor → condiciones más exigentes.
                // Ambas se convierten con el material de frío (necesita barrera de vapor).
                var hot = s.HeatingTemperatureC < 40 ? ((double, double, string)?)null
                    : Compute(AislRed.ClimaCaliente, outerMm, s.HeatingTemperatureC, ext, s.ColdLambda);
                var cold = Compute(AislRed.ClimaFrio, outerMm, s.ChilledTemperatureC, ext, s.ColdLambda);
                bool hotGoverns = hot.HasValue && hot.Value.Item2 > cold.Converted;
                var governing = hotGoverns ? hot!.Value : cold;
                var other = hotGoverns ? cold : hot;
                string note = other.HasValue
                    ? $" (frente a {N(other.Value.Item2)} mm en {(hotGoverns ? "frío" : "calor")})"
                    : " (calor por debajo de 40 °C: no exigido)";
                double t = hotGoverns ? s.HeatingTemperatureC : s.ChilledTemperatureC;
                return Build(red, AislMaterialGroup.Frio, outerMm, t, ext, s.ColdLambda, s, governing,
                    "Clima mixto, condición más exigente: ", note);
            }
            case AislRed.Frigorifica:
                return Build(red, AislMaterialGroup.Frigorifico, outerMm, 0, ext, s.RefrigerantLambda, s,
                    Compute(red, outerMm, 0, ext, s.RefrigerantLambda), "");
            case AislRed.AguaFria:
                if (!s.InsulateColdWater)
                    return NotRequired(red, AislMaterialGroup.Frio, outerMm, 0, ext, s.ColdLambda,
                        "No exigido: aislamiento de agua fría desactivado");
                return new AislRequirement(true, red, AislMaterialGroup.Frio, outerMm, 0, ext, 0, s.ColdLambda,
                    s.ColdWaterThicknessMm, s.ColdWaterThicknessMm,
                    "Agua fría sanitaria: el RITE no fija espesor; aislamiento anticondensación (IT 1.2.4.2.1.2, punto 9)");
            case AislRed.Excluida:
                return NotRequired(red, AislMaterialGroup.Caliente, outerMm, 0, ext, 0, "No exigido: red excluida");
            default:
                return NotRequired(red, AislMaterialGroup.Caliente, outerMm, 0, ext, 0, "No exigido: red sin clasificar");
        }
    }

    /// <summary>Material group a network uses.</summary>
    public static AislMaterialGroup MaterialOf(AislRed red) => red switch
    {
        AislRed.Acs or AislRed.ClimaCaliente => AislMaterialGroup.Caliente,
        AislRed.Frigorifica => AislMaterialGroup.Frigorifico,
        _ => AislMaterialGroup.Frio
    };

    /// <summary>
    /// Thickness fixed in the dialog. Rounded up to a commercial one; the rule says what the RITE would ask when it asks more.
    /// </summary>
    public static AislRequirement Fixed(AislRed red, double outerMm, double thicknessMm, AislSettings s)
    {
        double thickness = RoundCommercial(thicknessMm, s.CommercialThicknessesMm);
        var rite = red == AislRed.AguaFria ? null : Requirement(red, outerMm, s);
        string rule = $"Espesor fijado en el diálogo: {N(thickness)} mm";
        if (red == AislRed.AguaFria) rule += " (agua fría: el RITE no fija espesor; anticondensación, IT 1.2.4.2.1.2 punto 9)";
        else if (rite is { Required: true } && rite.ThicknessMm > thickness + Tolerance)
            rule += $" — ATENCIÓN: el RITE pide {N(rite.ThicknessMm)} mm ({rite.Rule})";
        return new AislRequirement(thickness > 0, red, MaterialOf(red), outerMm, rite?.TemperatureC ?? 0, s.Exterior, 0,
            rite?.LambdaUsed ?? 0, thickness, thickness, rule);
    }

    private static AislRequirement Build(AislRed red, AislMaterialGroup material, double outerMm, double t, bool ext,
        double lambda, AislSettings s, (double Table, double Converted, string Text) c, string prefix, string suffix = "")
    {
        double thickness = RoundCommercial(c.Converted, s.CommercialThicknessesMm);
        string rule = $"{prefix}{c.Text}; comercial {N(thickness)} mm{suffix}";
        return new AislRequirement(thickness > 0, red, material, outerMm, t, ext, c.Table, lambda, c.Converted, thickness, rule);
    }
}
