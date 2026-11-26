using System;
using System.Collections.Generic;

namespace RevitAutomation.Addin.Commands.Patinillo;

// Shared data contracts of «Patinillo de fontanería». Pure .NET (no Revit types) so the planner, the forms and the
// harness share them. All lengths in MILLIMETRES; the Revit adapter converts to internal feet.
//
// Shaft local frame (plan view): X runs along the mouth (the long side, 0..WidthMm); Y runs from the inner face of the
// mouth (Y = 0) to the back wall (Y = DepthMm). Pipes leave the shaft towards negative Y.

/// <summary>Water service of one riser.</summary>
public enum PatinilloService
{
    AguaFria,
    Acs,
    RetornoAcs
}

/// <summary>How the comb branch ends outside the shaft.</summary>
public enum PatinilloEnd
{
    Tapon,
    Abierto
}

/// <summary>What the user asked for in the main dialog.</summary>
public enum PatinilloAction
{
    Cancelar,
    Modelar,
    CrearEdificioPrueba
}

/// <summary>Every editable value of the dialog. Persisted as JSON between uses (see PatinilloSettingsStore).</summary>
public sealed class PatinilloSettings
{
    // Building (dwellings). Floors come from the model levels; the last two only drive the test building.
    public int DwellingsPerFloor { get; set; } = 2;
    public bool GroundFloorHasDwellings { get; set; } = false;
    public int TestFloorsAboveGround { get; set; } = 4;
    public double TestFloorHeightMm { get; set; } = 3000;
    public double SlabThicknessMm { get; set; } = 300;

    // Pipes per dwelling (nominal diameters).
    public double ColdDiameterMm { get; set; } = 25;
    public double HotDiameterMm { get; set; } = 20;
    public bool HotReturn { get; set; } = false;
    public double ReturnDiameterMm { get; set; } = 20;

    // Model types (names as shown in Revit; empty = first suitable one found).
    public string PipeTypeName { get; set; } = "";
    public string ColdSystemName { get; set; } = "";
    public string HotSystemName { get; set; } = "";
    public string ReturnSystemName { get; set; } = "";

    // Shaft (inner size, used when placing with one click; a selected outline overrides it).
    public double ShaftWidthMm { get; set; } = 600;
    public double ShaftDepthMm { get; set; } = 400;
    /// <summary>Axis spacing = largest outer diameter + this gap.</summary>
    public double GapMm { get; set; } = 50;

    // Comb outlet.
    public double OutletBelowSlabMm { get; set; } = 300;
    public double OutletLengthMm { get; set; } = 500;
    public PatinilloEnd End { get; set; } = PatinilloEnd.Tapon;

    // Start of the risers.
    public double StartAboveFloorMm { get; set; } = 500;

    public PatinilloSettings Clone() => (PatinilloSettings)MemberwiseClone();
}

/// <summary>One model level, sorted bottom-up by the caller or the planner. ElevationMm = level elevation (top of slab).</summary>
public sealed record PatinilloLevel(string Name, double ElevationMm);

/// <summary>One riser to model: a dwelling × service on a floor.</summary>
/// <param name="Id">Readable id, e.g. «P2-V1-AF» (floor name prefix, dwelling, service code AF/ACS/RET).</param>
/// <param name="FloorIndex">Index into the sorted level list of the floor served.</param>
public sealed record PatinilloPipe(
    string Id,
    int FloorIndex,
    string FloorName,
    int Dwelling,
    PatinilloService Service,
    double NominalMm,
    double OuterMm);

/// <summary>Where a riser goes inside the shaft and how it leaves.</summary>
/// <param name="Row">0 = row next to the mouth; grows towards the back.</param>
/// <param name="Column">0 = smallest X.</param>
/// <param name="Tier">Number of risers of the SAME floor in front of it in the same column; it exits Tier × pitch higher.</param>
/// <param name="EndYMm">Y of the open end / cap: −OutletLengthMm (outside the mouth).</param>
public sealed record PatinilloPlacement(
    PatinilloPipe Pipe,
    int Row,
    int Column,
    double XMm,
    double YMm,
    int Tier,
    double StartElevationMm,
    double ExitElevationMm,
    double EndYMm);

/// <summary>Result of planning. When Fits is false nothing must be modelled; Message says why and MinWidthMm/MinDepthMm
/// give the smallest shaft (keeping the given width when possible) that would hold every riser.</summary>
public sealed class PatinilloPlan
{
    public bool Fits { get; init; }
    public string Message { get; init; } = "";
    public double WidthMm { get; init; }
    public double DepthMm { get; init; }
    public double PitchMm { get; init; }
    public int Rows { get; init; }
    public int Columns { get; init; }
    public double MinWidthMm { get; init; }
    public double MinDepthMm { get; init; }
    public IReadOnlyList<PatinilloLevel> Levels { get; init; } = Array.Empty<PatinilloLevel>();
    public IReadOnlyList<PatinilloPipe> Pipes { get; init; } = Array.Empty<PatinilloPipe>();
    public IReadOnlyList<PatinilloPlacement> Placements { get; init; } = Array.Empty<PatinilloPlacement>();
    /// <summary>Non-blocking remarks found while planning (e.g. a level without a floor above, ignored).</summary>
    public IReadOnlyList<string> Notes { get; init; } = Array.Empty<string>();
}

/// <summary>Something that was not modelled (or not as asked) and why. Written to the deviations CSV.</summary>
public sealed record PatinilloDeviation(string PipeId, string What, string Reason, string HowToFix);

/// <summary>What the model offers to the dialog (names as shown in Revit).</summary>
public sealed class PatinilloModelOptions
{
    public IReadOnlyList<string> PipeTypeNames { get; init; } = Array.Empty<string>();
    public IReadOnlyList<string> ColdSystemNames { get; init; } = Array.Empty<string>();
    public IReadOnlyList<string> HotSystemNames { get; init; } = Array.Empty<string>();
    public IReadOnlyList<string> ReturnSystemNames { get; init; } = Array.Empty<string>();
    /// <summary>Model levels bottom-up, shown read-only in the dialog.</summary>
    public IReadOnlyList<PatinilloLevel> Levels { get; init; } = Array.Empty<PatinilloLevel>();
    /// <summary>One line telling how the shaft will be taken (selected room, selected lines, or click/rectangle).</summary>
    public string ShaftSourceText { get; init; } = "";
    /// <summary>False when the project already has floors or more than two levels (the test building is not offered).</summary>
    public bool CanCreateTestBuilding { get; init; }
}

/// <summary>Shaft rectangle in plan, in any consistent unit (the Revit adapter uses feet). Origin = mouth corner with X = 0;
/// U = unit vector along the mouth; V = unit vector from the mouth into the shaft.</summary>
public sealed record PatinilloRect(
    double OriginX, double OriginY,
    double UX, double UY,
    double VX, double VY,
    double Width, double Depth)
{
    public (double X, double Y) ToWorld(double x, double y) => (OriginX + UX * x + VX * y, OriginY + UY * x + VY * y);
}
