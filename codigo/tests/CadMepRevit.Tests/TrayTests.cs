using CadMepRevit.Core;

static class TrayTests
{
    public static void Run(Action<bool, string> check)
    {
        Console.WriteLine("2o) Bandejas, tubos eléctricos y capas de cuadros");
        string[] equipmentLayers = ["CUADROS ELECTRICOS", "E-POWR-PANL", "CUADRO GENERAL", "BT_CGP", "E-CGMP",
            "Electrical panels", "Paneles eléct.", "E-SWITCHBOARD"];
        foreach (string layer in equipmentLayers)
            check(MepKinds.Suggest(layer) == MepKind.NoImportar && !MepKinds.IsUnknownLayer(layer),
                $"'{layer}': dibujo de cuadros, excluido de los tramos");
        check(MepKinds.Suggest("E-POWR-CNDT") == MepKind.TuboElectrico, "E-POWR-CNDT sigue siendo tubo eléctrico");
        check(MepKinds.Suggest("BANDEJA") == MepKind.Bandeja, "BANDEJA sigue siendo bandeja");
        check(MepKinds.Suggest("TUBO CUADRO") == MepKind.Tuberia, "TUBO CUADRO conserva la clasificación anterior por TUBO");
        check(BlockNames.Role("Cuadro eléctrico", "CUADROS ELECTRICOS") == BlockRole.Electrico &&
              ModelScopes.Includes(ModelScope.EquiposPci, BlockRole.Electrico, "Cuadro eléctrico"),
            "Los bloques de cuadros se siguen colocando con Equipos y PCI");

        var drawing = CadReader.Read(Path.Combine(AppContext.BaseDirectory, "samples", "bandejas_uniones.dxf"));
        double k = DrawingUnits.ToMillimeters(drawing.HeaderUnit ?? DrawingUnit.Milimetros);
        double tol = PlanDefaults.ToleranceMm;
        var byLayer = LayerPlanning.ByLayer(drawing);
        var layers = drawing.SegmentLayers.Select(layer =>
        {
            var kind = MepKinds.Suggest(layer);
            return (layer, kind, DoubleLine: LayerPlanning.LooksDoubleLine(byLayer[layer].ToList(), kind, k, tol));
        }).Where(l => ModelScopes.Includes(ModelScope.Bandejas, l.kind)).ToList();
        check(layers.Count == 3 && layers.Single(l => l.layer == "E-CABL-TRAY").DoubleLine &&
              !layers.Single(l => l.layer == "BANDEJA").DoubleLine,
            "DXF: se distingue la bandeja por su eje de la dibujada por sus bordes");
        var network = NetworkBuilder.Build(LayerPlanning.NetworkInput(byLayer, layers, k, tol, null),
            new NetworkBuilder.Settings { UnitToMm = k, ToleranceMm = tol, MinLengthMm = PlanDefaults.MinLengthMm });
        var trays = network.Junctions.Where(j => MepKinds.Suggest(j.Layer) == MepKind.Bandeja).ToList();
        var expected = new (string Layer, JunctionKind Kind, P3 Point)[]
        {
            ("BANDEJA", JunctionKind.Codo, new P3(3000, 0, 0)),
            ("BANDEJA", JunctionKind.Te, new P3(9000, 0, 0)),
            ("BANDEJA", JunctionKind.Cruz, new P3(17000, 0, 0)),
            ("E-CABL-TRAY", JunctionKind.Codo, new P3(3700, 8000, 0)),
            ("E-CABL-TRAY", JunctionKind.Transicion, new P3(9000, 8200, 0))
        };
        check(trays.Count == expected.Length, $"DXF: {trays.Count} encuentros de bandeja, esperados {expected.Length}");
        foreach (var (layer, kind, point) in expected)
        {
            var match = trays.SingleOrDefault(j => j.Layer == layer && j.Kind == kind && j.Point.DistanceTo(point) <= tol);
            check(match is not null && match.Ordered.All(e => network.Segments[e.Segment].Length >= 2000),
                $"DXF: {kind} en '{layer}' en {point}, con tramos de al menos 2 m");
        }
        // Tubos eléctricos (27-11-2026): sus encuentros llevan pieza en el botón Bandejas, igual que las bandejas.
        var conduits = network.Junctions.Where(j => MepKinds.Suggest(j.Layer) == MepKind.TuboElectrico).ToList();
        check(conduits.Any(j => j.Kind == JunctionKind.Codo) && conduits.Any(j => j.Kind == JunctionKind.Te) &&
              conduits.Where(j => j.Kind != JunctionKind.EnLinea)
                  .All(j => ModelScopes.AllowsFitting(ModelScope.Bandejas, MepKind.TuboElectrico, j.Kind)),
            "DXF: el codo y la te del tubo eléctrico se crean con el botón Bandejas");
    }
}
