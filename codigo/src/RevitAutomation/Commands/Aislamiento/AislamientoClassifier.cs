#nullable enable
using System;
using System.Globalization;
using System.Linq;
using System.Text;

namespace RevitAutomation.Addin.Commands.Aislamiento;

// Clasificación automática de un tipo de sistema de tuberías de Revit en una red del RITE.
// Las palabras clave se buscan al INICIO de una palabra (para que «bie» no case con «ambiente»); las marcadas como
// palabra completa exigen además que no siga ninguna letra.
public static class AislClassifier
{
    private static readonly string[] RefrigerantStems =
    {
        "refriger", "frigor", "r32", "r-32", "r410", "r-410", "r407", "r-407", "r134", "r-134", "r290",
        "expansion directa", "linea de gas", "linea de liquido", "succion", "suction"
    };
    private static readonly string[] RefrigerantWords = { "vrv", "vrf", "split" };
    private static readonly string[] ColdStems = { "frio", "fria", "cold", "chilled", "refrigeracion", "enfriadora" };
    private static readonly string[] HotStems = { "calef", "calor", "caliente", "heat", "radiador", "suelo radiante" };
    private static readonly string[] HotWords = { "acs" };
    private static readonly string[] MixedStems = { "mixto", "2 tubos", "dos tubos", "changeover" };
    private static readonly string[] ExcludedStems = { "saneamiento", "pluvial", "fecal", "ventilacion", "incendio", "rociador", "gas natural" };
    private static readonly string[] ExcludedWords = { "bie" };
    private static readonly string[] AcsStems = { "agua caliente", "retorno acs", "recircul" };
    private static readonly string[] AcsWords = { "acs" };
    private static readonly string[] ColdWaterStems = { "agua fria" };
    private static readonly string[] ColdWaterWords = { "afs" };

    public static AislClassification Classify(AislSystemInfo system, AislSettings s)
    {
        if (s.SystemOverrides.TryGetValue(system.Name, out var forced))
            return new AislClassification(forced, "Asignado por el usuario", true);

        string text = Normalize(system.Name + " " + system.FluidName);
        string cls = system.Classification ?? "";

        if (cls != "DomesticHotWater" && cls != "DomesticColdWater")
        {
            string? hit = FindRefrigerant(text);
            if (hit != null) return Auto(AislRed.Frigorifica, $"Nombre contiene «{hit}»: circuito frigorífico");
        }

        if (cls == "DomesticHotWater") return Auto(AislRed.Acs, "Clasificación de Revit: agua caliente sanitaria");
        if (cls == "DomesticColdWater") return Auto(AislRed.AguaFria, "Clasificación de Revit: agua fría sanitaria");

        // Revit: MEPSystemClassification.SupplyHydronic / ReturnHydronic (also accept the reversed spelling).
        bool hydronic = cls is "SupplyHydronic" or "ReturnHydronic" or "HydronicSupply" or "HydronicReturn";
        if (hydronic)
        {
            string? cold = Find(text, ColdStems, false);
            string? hot = Find(text, HotStems, false) ?? Find(text, HotWords, true);
            string? mixed = Find(text, MixedStems, false);
            if (mixed != null) return Auto(AislRed.ClimaMixto, $"Clima por agua; nombre contiene «{mixed}»: frío y calor");
            if (cold != null && hot != null)
                return Auto(AislRed.ClimaMixto, $"Clima por agua; nombre contiene «{cold}» y «{hot}»: frío y calor");
            if (cold != null) return Auto(AislRed.ClimaFrio, $"Clima por agua; nombre contiene «{cold}»");
            if (hot != null) return Auto(AislRed.ClimaCaliente, $"Clima por agua; nombre contiene «{hot}»");
            return Auto(AislRed.ClimaMixto, "Clima por agua sin indicar frío o calor: se aplica la condición más exigente (frío y calor)");
        }

        if (cls == "Sanitary" || cls == "Vent" || cls.StartsWith("FireProtect", StringComparison.Ordinal))
            return Auto(AislRed.Excluida, $"Clasificación de Revit «{cls}»: no se aísla");
        string? excluded = Find(text, ExcludedStems, false) ?? Find(text, ExcludedWords, true);
        if (excluded != null) return Auto(AislRed.Excluida, $"Nombre contiene «{excluded}»: no se aísla");

        string? acs = Find(text, AcsStems, false) ?? Find(text, AcsWords, true);
        if (acs != null) return Auto(AislRed.Acs, $"Nombre contiene «{acs}»: agua caliente sanitaria");
        string? afs = Find(text, ColdWaterStems, false) ?? Find(text, ColdWaterWords, true);
        if (afs != null) return Auto(AislRed.AguaFria, $"Nombre contiene «{afs}»: agua fría sanitaria");

        // Clima por agua dibujado con otra clasificación: por el nombre.
        string? coldName = Find(text, ColdStems, false);
        string? hotName = Find(text, HotStems, false);
        string? mixedName = Find(text, MixedStems, false);
        if (mixedName != null || (coldName != null && hotName != null))
            return Auto(AislRed.ClimaMixto, $"Nombre contiene «{mixedName ?? coldName + "» y «" + hotName}»: clima frío y calor");
        if (hotName != null) return Auto(AislRed.ClimaCaliente, $"Nombre contiene «{hotName}»: calefacción");
        if (coldName != null) return Auto(AislRed.ClimaFrio, $"Nombre contiene «{coldName}»: clima frío");

        return Auto(AislRed.SinClasificar, "No se reconoce la red por su clasificación ni su nombre: asígnela");
    }

    private static AislClassification Auto(AislRed red, string reason) => new(red, reason, false);

    // «refriger» no debe capturar «refrigeración» (agua de refrigeración = clima frío por agua, no circuito frigorífico).
    private static string? FindRefrigerant(string text)
    {
        foreach (string stem in RefrigerantStems)
            foreach (int i in Starts(text, stem))
            {
                if (stem == "refriger" && string.CompareOrdinal(text, i, "refrigeracion", 0, 13) == 0) continue;
                return stem;
            }
        return Find(text, RefrigerantWords, true);
    }

    private static string? Find(string text, string[] keys, bool wholeWord)
    {
        foreach (string key in keys)
            foreach (int i in Starts(text, key))
            {
                int end = i + key.Length;
                if (!wholeWord || end >= text.Length || !char.IsLetter(text[end])) return key;
            }
        return null;
    }

    // Posiciones donde aparece «key» al inicio de una palabra (precedida de algo que no es letra ni dígito).
    private static System.Collections.Generic.IEnumerable<int> Starts(string text, string key)
    {
        int i = text.IndexOf(key, StringComparison.Ordinal);
        while (i >= 0)
        {
            if (i == 0 || !char.IsLetterOrDigit(text[i - 1])) yield return i;
            i = text.IndexOf(key, i + 1, StringComparison.Ordinal);
        }
    }

    /// <summary>Minúsculas, sin acentos y con «_» como espacio.</summary>
    public static string Normalize(string? value)
    {
        string decomposed = (value ?? "").ToLowerInvariant().Normalize(NormalizationForm.FormD);
        var text = new StringBuilder(decomposed.Length);
        foreach (char c in decomposed)
        {
            if (CharUnicodeInfo.GetUnicodeCategory(c) == UnicodeCategory.NonSpacingMark) continue;
            text.Append(c == '_' ? ' ' : c);
        }
        return text.ToString().Normalize(NormalizationForm.FormC);
    }
}
