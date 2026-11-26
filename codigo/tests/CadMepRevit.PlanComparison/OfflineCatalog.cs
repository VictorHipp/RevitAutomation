using Autodesk.Revit.DB;
using CadMepRevit.Core;

namespace Autodesk.Revit.DB
{
    // Solo las formas de datos que consume AutoPlanner. Aquí no hay documento ni transacciones de Revit.
    public sealed record ElementId(long Value)
    {
        public static ElementId InvalidElementId { get; } = new(-1);
    }

    public sealed class Transform;

    public enum BuiltInCategory
    {
        OST_MechanicalEquipment, OST_DuctTerminal, OST_DuctAccessory, OST_PipeAccessory,
        OST_ElectricalFixtures, OST_LightingFixtures, OST_LightingDevices, OST_ElectricalEquipment,
        OST_FireProtection, OST_FireAlarmDevices, OST_PlumbingFixtures, OST_GenericModel
    }

    public enum ConnectorProfileType { Rectangular, Round, Oval }

    public enum MEPSystemClassification
    {
        Sanitary, Storm, DomesticColdWater, DomesticHotWater, Recirculation, FireProtectWet, FireProtectOther,
        FireProtectDry, FireProtectPreaction, Vent, SupplyHydronic, ReturnHydronic, OtherPipe
    }
}

namespace CadMepRevit.Revit
{
    public sealed record NamedId(string Name, ElementId Id);

    public sealed class RevitCatalog
    {
        public List<(string Name, ElementId Id, double ElevationFt)> Levels { get; } =
            [("Sótano", new(1), -10), ("Planta baja", new(2), 0), ("Planta primera", new(3), 10)];

        public Dictionary<MepKind, List<NamedId>> Types { get; } = new()
        {
            [MepKind.Tuberia] = [new("Cobre", new(11)), new("PVC Saneamiento", new(12))],
            [MepKind.Conducto] = [new("Rectangular", new(13)), new("Redondo", new(14))],
            [MepKind.Bandeja] = [new("Bandeja", new(15))],
            [MepKind.TuboElectrico] = [new("Tubo eléctrico", new(16))]
        };

        public Dictionary<MepKind, List<NamedId>> Systems { get; } = new()
        {
            [MepKind.Tuberia] = Names(21, "Agua caliente", "Agua fría", "Retorno agua caliente", "Drenaje", "Incendios", "Pluvial", "Sanitario"),
            [MepKind.Conducto] = Names(31, "Aire de retorno", "Extracción", "Impulsión")
        };

        public Dictionary<ElementId, MEPSystemClassification> PipeClassifications { get; } = new();
        public List<NamedId> FamilySymbols { get; } =
            [new("XX_TER_RejillaExtraccion", new(41)), new("Cuadro eléctrico", new(42))];

        public static bool NeedsSystem(MepKind kind) => kind is MepKind.Tuberia or MepKind.Conducto;
        public static string HowToGetTypes(MepKind kind) => throw new InvalidOperationException($"Falta un tipo del catálogo de prueba para {kind}");
        public List<ConnectorProfileType> DuctShapesAvailable() => [ConnectorProfileType.Rectangular, ConnectorProfileType.Round];
        public List<NamedId> DuctTypesOf(ConnectorProfileType shape) =>
            [Types[MepKind.Conducto][shape == ConnectorProfileType.Round ? 1 : 0]];
        public bool UsesWidthHeight(MepKind kind, ElementId? typeId) =>
            kind is MepKind.Muro or MepKind.Bandeja || kind == MepKind.Conducto && typeId == Types[MepKind.Conducto][0].Id;
        public string? MatchBlock(string block) =>
            FamilySymbols.FirstOrDefault(s => s.Name.Equals(BlockNames.Clean(block), StringComparison.OrdinalIgnoreCase))?.Name;
        public string? FittingWarning(string name, ElementId? id) => null;

        public double PipeOuterMm(ElementId? typeId, double nominalMm, double? drawnOuterMm, bool useNominal, out bool assumed)
        {
            assumed = drawnOuterMm is null;
            return drawnOuterMm ?? nominalMm;
        }

        private static List<NamedId> Names(int start, params string[] names) =>
            names.Select((name, i) => new NamedId(name, new ElementId(start + i))).ToList();
    }
}
