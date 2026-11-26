using System.Globalization;
using CadMepRevit.Core;

static class NetworkFittingTests
{
    public static void Run(Action<bool, string> check)
    {
        Console.WriteLine("2p) Uniones por botón, cierre de huecos y accesorios de red");
        var (pipeDxf, pipes) = Read("tuberias", "P-PIPE", MepKind.Tuberia, 25, SizeShape.Redonda);
        check(Has(pipes, 1000, 0, JunctionKind.Codo), "DXF tuberías: codo en esquina");
        check(Has(pipes, 5000, 0, JunctionKind.Te), "DXF tuberías: te en derivación");
        check(Has(pipes, 9000, 0, JunctionKind.Cruz) &&
            !ModelScopes.AllowsFitting(ModelScope.Tuberias, MepKind.Tuberia, JunctionKind.Cruz),
            "DXF tuberías: cuatro extremos coincidentes reservados para resolver a mano");
        check(Has(pipes, 13200, 0, JunctionKind.Transicion), "DXF tuberías: DN32 → DN63 conserva el corte y genera reducción");
        check(pipes.Gaps.Any(g => g.Closed && g.Kind == GapKind.Esquina && g.ExtensionMm == 40) &&
            Has(pipes, 1000, 4000, JunctionKind.Codo), "DXF tuberías: cierre de esquina de 40 mm");
        check(pipes.Gaps.Any(g => !g.Closed && g.Kind == GapKind.Esquina && g.ExtensionMm == 600) &&
            !Has(pipes, 5000, 4000, JunctionKind.Codo), "DXF tuberías: esquina de 600 mm queda sin cerrar");
        check(!pipes.Gaps.Any(g => g.Closed && g.Point.DistanceTo(new P3(9000, 4000, 0)) < PlanDefaults.ToleranceMm),
            "DXF tuberías: se conserva el hueco en línea cuando cruza otro tubo");
        check(Has(pipes, 13000, 4000, JunctionKind.EnLinea), "DXF tuberías: hueco en línea sin cruce y de igual medida, sin pieza");
        check(Has(pipes, 5000, 8000, JunctionKind.Te), "DXF tuberías: el ramal se alarga y parte el principal para la te");
        check(Link(pipeDxf, pipes, ModelScope.Tuberias, "VALVULA_DE_PASO", 25) is
            { Site: NetworkBlockSite.Interior, Along: 0.5, Side: 0, Axis: { X: 1000, Y: 8000 } },
            "DXF tuberías: válvula en el eje y punto medio de su tubo");

        var (ductDxf, ducts) = Read("conductos", "CONDUCTOS", MepKind.Conducto, 300, SizeShape.AnchoAlto);
        check(Has(ducts, 1000, 0, JunctionKind.Codo) && Has(ducts, 5000, 0, JunctionKind.Te) &&
            Has(ducts, 9000, 0, JunctionKind.Cruz) && Has(ducts, 13200, 0, JunctionKind.Transicion),
            "DXF conductos: codo, te, cruz y reducción de tamaño");
        check(Has(ducts, 13200, 5000, JunctionKind.Transicion), "DXF conductos: cambio de sección rectangular a redonda");
        check(Link(ductDxf, ducts, ModelScope.Conductos, "REJILLA_EXTREMO", 300) is
            { Site: NetworkBlockSite.Inicio, Side: 0, Direction: { X: -1, Y: 0 } },
            "DXF conductos: rejilla en el extremo, orientada hacia fuera del eje");
        check(Link(ductDxf, ducts, ModelScope.Conductos, "REJILLA_COSTADO", 300) is
            { Site: NetworkBlockSite.Costado, Side: 1, Direction: { X: 0, Y: 1 } },
            "DXF conductos: rejilla en el costado izquierdo, orientada hacia fuera de la cara");
        check(Link(ductDxf, ducts, ModelScope.Conductos, "COMPUERTA_REGULACION", 300) is
            { Site: NetworkBlockSite.Interior, Side: 0, Direction.X: 1 } &&
            Link(ductDxf, ducts, ModelScope.Conductos, "VENTILADOR", 300) is { Site: NetworkBlockSite.Fin },
            "DXF conductos: compuerta en línea y ventilador al final del conducto");

        var trayInput = new[] { Segment("CABLE-TRAY", 0, 0, 2000, 0) };
        var tray = NetworkBuilder.Build(trayInput, new());
        const string trayAccessory = "ACCESORIO DE BANDEJA";
        var trayRole = BlockNames.Role(trayAccessory, "CABLE-TRAY");
        check(NetworkBlocks.For(ModelScope.Bandejas, trayRole, trayAccessory) is (MepKind.Bandeja, NetworkBlockKind.EnLinea) &&
            !ModelScopes.Includes(ModelScope.Tuberias, trayRole, trayAccessory) &&
            NetworkBlocks.Associate(new P3(750, 15, 0), NetworkBlockKind.EnLinea, 10, [(tray.Segments.Single(), 200)]) is
                { Site: NetworkBlockSite.Interior, Axis: { X: 750, Y: 0 } },
            "Accesorio de bandeja sintético: pertenece a Bandejas y se proyecta sobre su eje");
        var separated = NetworkBuilder.Build([Segment("A", 0, 0, 500, 0), Segment("B", 540, 0, 1000, 0)],
            new NetworkBuilder.Settings { GapLayers = new Dictionary<string, (MepKind, double)> { ["A"] = (MepKind.Tuberia, 25), ["B"] = (MepKind.Tuberia, 25) } });
        check(separated.Gaps.Count == 0, "Huecos: ninguna prolongación enlaza redes de capas distintas");
        var matching = NetworkBuilder.Build([Segment("A", 0, 0, 500, 0), Segment("A", 500, 0, 1000, 0)], new());
        check(matching.Segments.Count == 1 && matching.Junctions.Count == 0,
            "Dos tramos consecutivos alineados con igual tamaño de dibujo se fusionan como un solo tramo");
        var teeAtReducer = NetworkBuilder.Build([
            new CadSegment { Layer = "A", Start = new(0, 0, 0), End = new(600, 0, 0), WidthMm = 40 },
            new CadSegment { Layer = "A", Start = new(600, 0, 0), End = new(1200, 0, 0), WidthMm = 60 },
            Segment("A", 600, 50, 600, 500)],
            new NetworkBuilder.Settings { GapLayers = new Dictionary<string, (MepKind, double)> { ["A"] = (MepKind.Tuberia, 25) } });
        check(Has(teeAtReducer, 600, 0, JunctionKind.Te) && teeAtReducer.Segments.Count == 3,
            "Te perdida al final de dos tubos alineados de distinto diámetro: conserva los tres tramos");
    }

    private static (CadDrawing Drawing, NetworkResult Network) Read(string sample, string layer, MepKind kind, double width, SizeShape shape)
    {
        var drawing = CadReader.Read(Path.Combine(AppContext.BaseDirectory, "samples", $"{sample}_uniones_accesorios.dxf"));
        Dictionary<NetSegment, (double Width, double? Height)> Sizes(IReadOnlyList<NetSegment> segments)
        {
            var targets = segments.Select((s, i) => new SizeTarget
            {
                Id = i.ToString(CultureInfo.InvariantCulture), Layer = s.Layer, Start = s.Start, End = s.End,
                Shape = shape, EitherShape = kind == MepKind.Conducto
            }).ToList();
            return SizeAnnotations.Associate(drawing, targets, new()).Decisions
                .ToDictionary(p => segments[int.Parse(p.Key, CultureInfo.InvariantCulture)], p => (p.Value.ValueMm, p.Value.HeightMm));
        }
        var net = NetworkBuilder.Build(drawing.Segments, new NetworkBuilder.Settings
        {
            GapLayers = new Dictionary<string, (MepKind, double)> { [layer] = (kind, width) },
            ReadSizesBeforeMerge = Sizes
        });
        var final = Sizes(net.Segments);
        NetworkBuilder.ReclassifySizes(net, s => final.TryGetValue(s, out var d) ? d : (width, kind == MepKind.Conducto ? 200 : null));
        return (drawing, net);
    }

    private static CadSegment Segment(string layer, double x1, double y1, double x2, double y2) =>
        new() { Layer = layer, Start = new P3(x1, y1, 0), End = new P3(x2, y2, 0) };

    private static bool Has(NetworkResult n, double x, double y, JunctionKind kind) =>
        n.Junctions.Any(j => j.Kind == kind && j.Point.DistanceTo(new P3(x, y, 0)) < PlanDefaults.ToleranceMm);

    private static NetworkBlockLink? Link(CadDrawing drawing, NetworkResult net, ModelScope scope, string name, double width)
    {
        var block = drawing.Blocks.Single(b => b.BlockName == name);
        var network = NetworkBlocks.For(scope, block.Role, name);
        return network == null ? null : NetworkBlocks.Associate(block.Position, network.Value.Use, PlanDefaults.ToleranceMm,
            net.Segments.Select(s => (s, width)), block.RotationRadians);
    }
}
