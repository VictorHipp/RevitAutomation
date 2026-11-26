using System;
using System.Collections.Generic;

namespace RevitAutomation.Addin.Commands.Aislamiento;

// Shared data contracts of «Aislamiento RITE». Pure .NET (no Revit types) so the rules, the forms and the harness share
// them. Lengths in MILLIMETRES, temperatures in °C, conductivities in W/(m·K).
// Source of the rules: RITE (RD 1027/2007, consolidated BOE text updated 02/08/2022), IT 1.2.4.2.1 «Aislamiento térmico
// de redes de tuberías», procedimiento simplificado (IT 1.2.4.2.1.2), tables 1.2.4.2 and 1.2.4.2.1 to 1.2.4.2.5.

/// <summary>Kind of network, decided from the Revit piping system type.</summary>
public enum AislRed
{
    /// <summary>Not classified yet: the user must assign it in the dialog (nothing is insulated meanwhile).</summary>
    SinClasificar,
    /// <summary>Domestic hot water (and its return): continuous operation, table 1.2.4.2 (= hot table + 5 mm).</summary>
    Acs,
    /// <summary>Domestic cold water: RITE has no table, only «avoid condensation» (IT 1.2.4.2.1.2 item 9).</summary>
    AguaFria,
    /// <summary>Hydronic heating water (supply or return): hot-fluid tables by max temperature.</summary>
    ClimaCaliente,
    /// <summary>Hydronic chilled water (supply or return): cold-fluid tables by min temperature.</summary>
    ClimaFrio,
    /// <summary>Two-pipe changeover (hot and cold): the most demanding of both (IT 1.2.4.2.1.2 item 4).</summary>
    ClimaMixto,
    /// <summary>Refrigerant lines of air-conditioning equipment: table 1.2.4.2.5.</summary>
    Frigorifica,
    /// <summary>Not to be insulated by this tool (sanitation, fire protection, gas, etc.).</summary>
    Excluida
}

/// <summary>Kind of Revit element that receives the insulation.</summary>
public enum AislElementKind
{
    Tuberia,
    Pieza,
    Accesorio
}

/// <summary>Piping system type as read from Revit (names as shown in Revit).</summary>
/// <param name="Classification">Revit MEPSystemClassification as text (e.g. "DomesticHotWater", "SupplyHydronic", "ReturnHydronic", "OtherPipe").</param>
/// <param name="FluidName">Fluid type name, may be empty.</param>
/// <param name="FluidTemperatureC">Fluid temperature from Revit, informative only (templates often leave 15,56 °C).</param>
public sealed record AislSystemInfo(string Name, string Classification, string FluidName, double? FluidTemperatureC);

/// <summary>What the dialog asks per network: insulate or not, material and thickness.</summary>
public sealed class AislNetworkSettings
{
    public bool Enabled { get; set; } = true;
    /// <summary>Revit PipeInsulationType name; empty = chosen automatically.</summary>
    public string InsulationType { get; set; } = "";
    /// <summary>Fixed thickness (mm); null = the RITE thickness for each diameter.</summary>
    public double? ThicknessMm { get; set; }
    public AislNetworkSettings Clone() => (AislNetworkSettings)MemberwiseClone();
}

/// <summary>Every editable value of the dialog. Persisted as JSON between uses.</summary>
public sealed class AislSettings
{
    // Scope.
    public bool OnlySelection { get; set; } = false;
    public string LevelName { get; set; } = "";
    public bool Exterior { get; set; } = false;
    public bool IncludeHydronic { get; set; } = true;
    public bool IncludeFittingsAndAccessories { get; set; } = true;

    // Design temperatures per network (°C). Hot networks use the max temperature, cold ones the min temperature.
    public double AcsTemperatureC { get; set; } = 60;
    public double HeatingTemperatureC { get; set; } = 80;
    public double ChilledTemperatureC { get; set; } = 7;

    // Domestic cold water (not in the RITE tables): anti-condensation thickness, can be disabled.
    public bool InsulateColdWater { get; set; } = true;
    public double ColdWaterThicknessMm { get; set; } = 9;

    // Insulation material per group: Revit PipeInsulationType name (empty = first one) and its conductivity.
    public string HotInsulationType { get; set; } = "";
    public double HotLambda { get; set; } = 0.040;
    public string ColdInsulationType { get; set; } = "";
    public double ColdLambda { get; set; } = 0.036;
    public string RefrigerantInsulationType { get; set; } = "";
    public double RefrigerantLambda { get; set; } = 0.036;

    /// <summary>Commercial thicknesses (mm): the computed thickness is rounded UP to the next one.</summary>
    public List<double> CommercialThicknessesMm { get; set; } = new() { 6, 9, 10, 13, 15, 19, 20, 25, 30, 32, 40, 50, 60, 70, 80, 100 };

    /// <summary>Networks the dialog shows, in order.</summary>
    public static readonly AislRed[] DialogNetworks =
        { AislRed.Acs, AislRed.AguaFria, AislRed.ClimaCaliente, AislRed.ClimaFrio, AislRed.ClimaMixto, AislRed.Frigorifica };

    /// <summary>Per network: insulate or not, material and thickness (fixed or RITE).</summary>
    public Dictionary<AislRed, AislNetworkSettings> Networks { get; set; } = DefaultNetworks();

    /// <summary>Show the detailed list before changing the model (off: apply straight away).</summary>
    public bool ReviewBeforeApplying { get; set; } = false;

    public static Dictionary<AislRed, AislNetworkSettings> DefaultNetworks()
    {
        var d = new Dictionary<AislRed, AislNetworkSettings>();
        foreach (var red in DialogNetworks) d[red] = new AislNetworkSettings();
        d[AislRed.AguaFria].ThicknessMm = 9; // no RITE table for domestic cold water: anti-condensation
        return d;
    }

    /// <summary>The settings of one network (defaults when missing).</summary>
    public AislNetworkSettings Network(AislRed red)
    {
        Networks ??= DefaultNetworks();
        if (!Networks.TryGetValue(red, out var n) || n is null)
            Networks[red] = n = DefaultNetworks().TryGetValue(red, out var def) ? def : new AislNetworkSettings();
        return n;
    }

    /// <summary>User assignment by system type name (case-insensitive), overrides the automatic classification.</summary>
    public Dictionary<string, AislRed> SystemOverrides { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    public AislSettings Clone()
    {
        var c = (AislSettings)MemberwiseClone();
        c.CommercialThicknessesMm = new List<double>(CommercialThicknessesMm);
        c.SystemOverrides = new Dictionary<string, AislRed>(SystemOverrides, StringComparer.OrdinalIgnoreCase);
        c.Networks = new Dictionary<AislRed, AislNetworkSettings>();
        foreach (var kv in Networks ?? DefaultNetworks()) if (kv.Value is not null) c.Networks[kv.Key] = kv.Value.Clone();
        return c;
    }
}

/// <summary>Result of classifying one system type.</summary>
/// <param name="Reason">Short Spanish explanation («Clasificación de Revit: agua caliente sanitaria», «nombre contiene "frigor"», «asignado por el usuario»).</param>
public sealed record AislClassification(AislRed Red, string Reason, bool ByUser);

/// <summary>Group of insulation material used for a network.</summary>
public enum AislMaterialGroup
{
    Caliente,
    Frio,
    Frigorifico
}

/// <summary>Required insulation for one outer diameter of one network.</summary>
/// <param name="Required">False when the RITE does not require insulation (e.g. cold water disabled, hydronic between 20 and 40 °C).</param>
/// <param name="TableMm">Thickness read from the RITE table (reference λ = 0,040), before λ conversion; 0 if none.</param>
/// <param name="ConvertedMm">Thickness after the λ conversion for circular sections, before rounding.</param>
/// <param name="ThicknessMm">Final thickness: ConvertedMm rounded up to the next commercial thickness.</param>
/// <param name="Rule">Spanish justification, e.g. «RITE Tabla 1.2.4.2 (ACS, interior), D ≤ 35 mm: 30 mm; λ 0,036 → 26,4 mm; comercial 30 mm».</param>
public sealed record AislRequirement(
    bool Required,
    AislRed Red,
    AislMaterialGroup Material,
    double OuterMm,
    double TemperatureC,
    bool Exterior,
    double TableMm,
    double LambdaUsed,
    double ConvertedMm,
    double ThicknessMm,
    string Rule);

/// <summary>One Revit element to insulate, as read by the adapter.</summary>
/// <param name="ExistingThicknessMm">Current insulation thickness, null if none.</param>
public sealed record AislItem(
    long ElementId,
    AislElementKind Kind,
    string SystemName,
    string LevelName,
    double OuterMm,
    double? ExistingThicknessMm);

/// <summary>What to do with one element.</summary>
public enum AislAction
{
    Crear,
    Aumentar,
    MantenerSuficiente,
    NoExigido,
    SinClasificar,
    Excluida
}

/// <summary>Decision for one element.</summary>
public sealed record AislDecision(AislItem Item, AislClassification Classification, AislRequirement? Requirement, AislAction Action);

/// <summary>Row of the preview: identical network + diameter + action aggregated.</summary>
public sealed record AislSummaryRow(
    string SystemName,
    AislRed Red,
    double OuterMm,
    double TemperatureC,
    double ThicknessMm,
    string Rule,
    AislAction Action,
    int Count);

/// <summary>What the model offers to the dialog.</summary>
public sealed class AislModelOptions
{
    public IReadOnlyList<string> LevelNames { get; init; } = Array.Empty<string>();
    /// <summary>Level of the active view, preselected when the saved one does not exist.</summary>
    public string ActiveLevelName { get; init; } = "";
    public IReadOnlyList<string> InsulationTypeNames { get; init; } = Array.Empty<string>();
    /// <summary>System types present in the candidate elements, with their automatic classification.</summary>
    public IReadOnlyList<AislSystemInfo> Systems { get; init; } = Array.Empty<AislSystemInfo>();
    public int SelectedPipeCount { get; init; }
}

/// <summary>Something not insulated (or not as asked) and why. Written to the deviations CSV.</summary>
public sealed record AislDeviation(string Element, string What, string Reason, string HowToFix);
