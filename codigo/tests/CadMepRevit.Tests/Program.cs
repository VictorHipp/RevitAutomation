using ACadSharp;
using ACadSharp.Entities;
using ACadSharp.IO;
using ACadSharp.Tables;
using CadMepRevit.Core;
using CSMath;

// Pruebas automáticas del núcleo (lectura CAD + topología). NO ejecutan Revit.
// Uso: dotnet run --project tests/CadMepRevit.Tests [carpeta_ejemplos]

int failures = 0;
void Check(bool ok, string name)
{
    Console.WriteLine((ok ? "  OK   " : "  FALLO ") + name);
    if (!ok) failures++;
}

string samplesDir = args.Length > 0 ? args[0] : Path.Combine(Path.GetTempPath(), "CadMepRevitTests");
Directory.CreateDirectory(samplesDir);

Console.WriteLine("1) Generar plano de ejemplo (DXF y DWG)");
var doc = SampleDrawing.Create();
string dxf = Path.Combine(samplesDir, "ejemplo_mep.dxf");
string dwg = Path.Combine(samplesDir, "ejemplo_mep.dwg");
DxfWriter.Write(dxf, doc, false);
Check(File.Exists(dxf), "DXF escrito: " + dxf);
bool dwgOk = false;
try
{
    DwgWriter.Write(dwg, SampleDrawing.Create());
    dwgOk = File.Exists(dwg);
}
catch (Exception ex) { Console.WriteLine("  (aviso) no se pudo escribir DWG: " + ex.Message); }
Check(dwgOk, "DWG escrito: " + dwg);

foreach (var path in dwgOk ? new[] { dxf, dwg } : new[] { dxf })
{
    Console.WriteLine("2) Leer " + Path.GetFileName(path));
    var drawing = CadReader.Read(path);
    Check(drawing.HeaderUnit == DrawingUnit.Milimetros, "Unidades de cabecera = mm (leído: " + drawing.HeaderUnitText + ")");
    var layers = drawing.SegmentLayers.ToList();
    Check(layers.Contains("M-TUBERIA-AF") && layers.Contains("M-TUBERIA-ACS") && layers.Contains("M-CONDUCTO-IMP")
          && layers.Contains("E-BANDEJA"), "Capas con líneas: " + string.Join(", ", layers));
    Check(drawing.Blocks.Count == 2, "Bloques leídos (valvulas): " + drawing.Blocks.Count);
    Check(drawing.Blocks.All(b => b.Role == BlockRole.Accesorio && b.Footprint != null &&
                                  Math.Abs(b.Footprint.SizeX - 200) < 1 && Math.Abs(b.Footprint.SizeY - 200) < 1),
        "Válvulas: función Accesorio y huella 200×200 medida");
    Check(MepKinds.Suggest("M-TUBERIA-AF") == MepKind.Tuberia, "Sugerencia AF -> Tubería");
    Check(MepKinds.Suggest("M-CONDUCTO-IMP") == MepKind.Conducto, "Sugerencia conducto -> Conducto");
    Check(MepKinds.Suggest("E-BANDEJA") == MepKind.Bandeja, "Sugerencia bandeja -> Bandeja");
    Check(MepKinds.Suggest("COTAS") == MepKind.NoImportar, "Sugerencia COTAS -> No importar");

    var af = NetworkBuilder.Build(drawing.Segments.Where(s => s.Layer == "M-TUBERIA-AF"),
        new NetworkBuilder.Settings { UnitToMm = 1, ToleranceMm = 10, MinLengthMm = 50 });
    // AF: polilínea en L (codo) + ramal que termina en mitad del tramo horizontal (te)
    // + un tramo partido en dos líneas colineales (se unen)
    Console.WriteLine($"     AF: {af.Segments.Count} tramos, codos={af.Count(JunctionKind.Codo)}, tes={af.Count(JunctionKind.Te)}, cruces={af.Count(JunctionKind.Cruz)}, partidos={af.SplitAtTee}, unidos={af.MergedCollinear}, cortos={af.RemovedShort}");
    Check(af.Count(JunctionKind.Codo) == 1, "AF tiene 1 codo");
    Check(af.Count(JunctionKind.Te) == 1, "AF tiene 1 te (tramo partido en la intersección)");
    Check(af.SplitAtTee == 1, "AF: 1 tramo partido");
    Check(af.MergedCollinear >= 1, "AF: tramos colineales unidos");
    Check(af.RemovedShort == 1, "AF: 1 tramo demasiado corto descartado");
    Check(af.Segments.Count == 4, "AF: 4 tramos finales");

    var acs = NetworkBuilder.Build(drawing.Segments.Where(s => s.Layer == "M-TUBERIA-ACS"),
        new NetworkBuilder.Settings { UnitToMm = 1, ToleranceMm = 10, MinLengthMm = 50 });
    Check(acs.Count(JunctionKind.Cruz) == 1 && acs.Segments.Count == 4, $"ACS: cruz con 4 tramos ({acs.Segments.Count} tramos)");

    var meters = NetworkBuilder.Build(drawing.Segments.Where(s => s.Layer == "M-TUBERIA-AF"),
        new NetworkBuilder.Settings { UnitToMm = 1000, ToleranceMm = 10, MinLengthMm = 50 });
    Check(Math.Abs(meters.Segments.Max(s => s.Length) - af.Segments.Max(s => s.Length) * 1000) < 1, "Factor de unidades aplicado");

    // Conducto dibujado a doble línea (contorno, como exporta Revit), con huecos de te, codo y transición
    var outlineSegs = drawing.Segments.Where(s => s.Layer == "M-CONDUCTO-RET").ToList();
    var outline = OutlineDetector.Detect(outlineSegs, new OutlineDetector.Settings { UnitToMm = 1 });
    Console.WriteLine($"     Doble línea: {outline.InputLines} líneas -> {outline.Centerlines.Count} ejes, testas={outline.Caps}, huecos={outline.GapsClosed}, reorientados={outline.Reoriented}, emparejado={outline.PairedFraction:P0}");
    Check(outline.PairedFraction >= 0.9, "Doble línea detectada en M-CONDUCTO-RET");
    Check(OutlineDetector.Detect(drawing.Segments.Where(s => s.Layer == "M-TUBERIA-AF"), new OutlineDetector.Settings { UnitToMm = 1 }).PairedFraction < 0.6,
        "AF (línea simple) NO se toma por doble línea");
    Check(outline.Reoriented == 1, "Rectángulo corto y ancho leído en la dirección de su vecino");
    var ret = NetworkBuilder.Build(outline.Centerlines, new NetworkBuilder.Settings { UnitToMm = 1 });
    Console.WriteLine($"     RET: {ret.Segments.Count} tramos, anchos={string.Join("/", ret.Segments.Select(s => s.WidthMm))}, te={ret.Count(JunctionKind.Te)}, codo={ret.Count(JunctionKind.Codo)}, transición={ret.Count(JunctionKind.Transicion)}, mal={ret.Count(JunctionKind.NoAdmitida)}");
    Check(ret.Segments.Count == 5, "RET: 5 tramos (el rectángulo corto se une al tramo alineado)");
    Check(ret.Count(JunctionKind.Te) == 1 && ret.Junctions.Single(j => j.Kind == JunctionKind.Te).Point.DistanceTo(new P3(2300, 24000, 0)) < 1, "RET: te en el centro del hueco (2300, 24000)");
    Check(ret.Count(JunctionKind.Codo) == 1 && ret.Junctions.Single(j => j.Kind == JunctionKind.Codo).Point.DistanceTo(new P3(5450, 24000, 0)) < 1, "RET: codo en (5450, 24000)");
    Check(ret.Count(JunctionKind.Transicion) == 1, "RET: transición 600 → 400");
    Check(ret.Count(JunctionKind.NoAdmitida) == 0, "RET: ninguna unión imposible");
    Check(ret.Segments.Any(s => s.WidthMm == 400) && ret.Segments.Count(s => s.WidthMm == 600) == 3, "RET: anchos medidos 600 y 400 mm");
}

Console.WriteLine("2c) Sugerencias, funciones de bloque y coordenadas reflejadas");
Check(MepKinds.Suggest("03-TEXTOS GAS") == MepKind.NoImportar, "'03-TEXTOS GAS' → No importar (es texto)");
Check(MepKinds.Suggest("RACS_Retorno_Caliente") == MepKind.Tuberia, "'RACS_Retorno_Caliente' → Tubería (no conducto)");
Check(MepKinds.Suggest("AFS_Fria") == MepKind.Tuberia && MepKinds.Suggest("SANEAMIENTO PLUVIALES") == MepKind.Tuberia, "Fontanería y saneamiento → Tubería");
Check(MepKinds.Suggest("-P_CL_CONDUCTO-AIRE PRIMARIO IMP") == MepKind.Conducto, "Conducto de climatización → Conducto");
Check(MepKinds.Suggest("-P_IE_FUERZA") == MepKind.TuboElectrico, "Electricidad (fuerza) → Tubo eléctrico");
Check(MepKinds.Suggest("A-MUROS") == MepKind.Muro && MepKinds.Suggest("TABIQUERIA") == MepKind.Muro, "Muros y tabiques → Muro");
Check(MepKinds.Suggest("-P_CL_TEXTOS") == MepKind.NoImportar && MepKinds.Suggest("-P_CL_EQUIPOS MAA") == MepKind.NoImportar, "Textos y contornos de equipos → No importar");
Check(BlockNames.Role("TOMA DE CORRIENTE DOBLE DE 16A") == BlockRole.Electrico, "Toma de corriente → Eléctrico");
Check(BlockNames.Role("-S-BOTE") == BlockRole.Sanitario && BlockNames.Role("ARQUETA SUMIDERO") == BlockRole.Sanitario, "Bote sifónico y arqueta → Sanitario");
Check(BlockNames.Role("Derivacion-Fria") == BlockRole.PiezaDeUnion, "Derivación → pieza de unión");
Check(BlockNames.Role("I18T", "-P_CL_FANCOILS") == BlockRole.Equipo, "Bloque sin nombre claro en capa de fancoils → Equipo");
var mirrored = Ocs.ToWcs(new XYZ(0, 0, -1), 100, 50, 0);
Check(Math.Abs(mirrored.X + 100) < 1e-9 && Math.Abs(mirrored.Y - 50) < 1e-9, $"Polilínea reflejada (normal -Z): X invertida correctamente {mirrored}");

Console.WriteLine("2d) Muros a doble línea (ejemplo sintético)");
{
    // Habitación de 4×3 m con muros de 200 mm (contorno exterior e interior) y un tabique de 100 mm en T
    var w = new List<CadSegment>();
    void WL(double x1, double y1, double x2, double y2) => w.Add(new CadSegment { Layer = "MUROS", Start = new P3(x1, y1, 0), End = new P3(x2, y2, 0) });
    WL(0, 0, 4000, 0); WL(4000, 0, 4000, 3000); WL(4000, 3000, 0, 3000); WL(0, 3000, 0, 0);               // exterior
    WL(200, 200, 3800, 200); WL(3800, 200, 3800, 2800); WL(3800, 2800, 200, 2800); WL(200, 2800, 200, 200); // interior
    WL(1950, 200, 1950, 2800); WL(2050, 200, 2050, 2800);                                                   // tabique 100
    WL(5000, 0, 5300, 0); WL(5300, 0, 5300, 300); WL(5300, 300, 5000, 300); WL(5000, 300, 5000, 0);         // pilar 300×300
    var wo = OutlineDetector.Detect(w, OutlineDetector.Settings.ForWalls(1, 10));
    var wn = NetworkBuilder.Build(wo.Centerlines, new NetworkBuilder.Settings { UnitToMm = 1 });
    Console.WriteLine($"     {w.Count} líneas → {wo.Centerlines.Count} ejes ({string.Join("/", wo.Centerlines.Select(c => c.WidthMm))} mm), pilares={wo.Pillars}");
    Check(wo.Pillars == 1, "El pilar cuadrado no se toma por muro");
    Check(wo.Centerlines.Count(c => c.WidthMm == 200) >= 4 && wo.Centerlines.Count(c => c.WidthMm == 100) == 1, "4 muros de 200 mm y 1 tabique de 100 mm");
    var tab = wo.Centerlines.Single(c => c.WidthMm == 100);
    Check(Math.Min(tab.Start.Y, tab.End.Y) <= 101 && Math.Max(tab.Start.Y, tab.End.Y) >= 2899, "El tabique llega hasta el eje de los muros (une en T)");
    double perim = wn.Segments.Where(s => s.WidthMm == 200).Sum(s => s.Length);
    Check(Math.Abs(perim - 2 * (3800 + 2800)) < 50, $"Ejes de los muros forman el rectángulo de 3,8×2,8 m (perímetro {perim:0} mm)");
}

Console.WriteLine("2e bis) Conductos con dibujo interior (eje con flechas, rejillas, aspas, codo curvo) y unidades absurdas");
{
    var dl = new List<CadSegment>();
    void D(double x1, double y1, double x2, double y2, int arc = 0) => dl.Add(new CadSegment { Layer = "CONDUCTO EXTR", Start = new P3(x1, y1, 0), End = new P3(x2, y2, 0), ArcId = arc });
    void Rect(double x1, double y1, double x2, double y2) { D(x1, y1, x2, y1); D(x2, y1, x2, y2); D(x2, y2, x1, y2); D(x1, y2, x1, y1); }
    // Conducto de 5000 × 750 con su EJE dibujado y una rejilla rayada de 825 × 225 dentro (como en los planos de ventilación de garaje)
    Rect(0, 0, 5000, 750); D(0, 375, 5000, 375);
    Rect(100, 262, 925, 488); for (int i = 1; i <= 4; i++) D(100, 262 + 45 * i, 925, 262 + 45 * i);
    var o1 = OutlineDetector.Detect(dl, new OutlineDetector.Settings { UnitToMm = 1 });
    Check(o1.Centerlines.Count == 1 && Math.Abs(o1.Centerlines[0].WidthMm!.Value - 750) < 1 &&
          Math.Abs(o1.Centerlines[0].Start.Y - 375) < 1 && Math.Abs(o1.Centerlines[0].End.Y - 375) < 1,
          $"Conducto con eje dibujado y rejilla dentro: UN conducto de 750 centrado (salen {o1.Centerlines.Count}: {string.Join("/", o1.Centerlines.Select(c => c.WidthMm))})");
    // Dos conductos paralelos de 300 separados 300 (podrían confundirse con uno de 900 con eje): siguen siendo dos
    dl.Clear(); Rect(0, 0, 6000, 300); Rect(0, 600, 6000, 900);
    var o2 = OutlineDetector.Detect(dl, new OutlineDetector.Settings { UnitToMm = 1 });
    Check(o2.Centerlines.Count == 2 && o2.Centerlines.All(c => Math.Abs(c.WidthMm!.Value - 300) < 1), "Dos conductos paralelos de 300 siguen siendo dos");
    // Rectángulo con aspa (subida / bajada) al final de un conducto: no es un tramo
    dl.Clear(); Rect(0, 0, 4000, 600); Rect(6000, 0, 6600, 600); D(6000, 0, 6600, 600); D(6000, 600, 6600, 0);
    var o3 = OutlineDetector.Detect(dl, new OutlineDetector.Settings { UnitToMm = 1 });
    Check(o3.CrossedSymbols >= 1 && o3.Centerlines.Count == 1 && Math.Max(o3.Centerlines[0].Start.X, o3.Centerlines[0].End.X) <= 4001,
          $"Rectángulo con aspa (subida/bajada): símbolo, no tramo ({o3.Centerlines.Count} ejes)");
    // Codo curvo dibujado con arcos (cuerdas marcadas como curva): sin racimo de trocitos, los dos tramos llegan a la esquina
    dl.Clear();
    D(0, 0, 3000, 0); D(0, 600, 3000, 600);           // tramo horizontal, ancho 600
    D(3600, 1200, 3600, 4000); D(4200, 1200, 4200, 4000); // tramo vertical
    for (int i = 0; i < 6; i++)
    {
        double a0 = -Math.PI / 2 + i * Math.PI / 12, a1 = a0 + Math.PI / 12;
        D(3000 + 1200 * Math.Cos(a0), 1200 + 1200 * Math.Sin(a0), 3000 + 1200 * Math.Cos(a1), 1200 + 1200 * Math.Sin(a1), 7);
        D(3000 + 600 * Math.Cos(a0), 1200 + 600 * Math.Sin(a0), 3000 + 600 * Math.Cos(a1), 1200 + 600 * Math.Sin(a1), 8);
    }
    var o4 = OutlineDetector.Detect(dl, new OutlineDetector.Settings { UnitToMm = 1 });
    Check(o4.Centerlines.Count == 2 && o4.Centerlines.All(c => c.Start.DistanceTo(new P3(3900, 300, 0)) < 5 || c.End.DistanceTo(new P3(3900, 300, 0)) < 5),
          $"Codo curvo: dos tramos rectos que se cortan en la esquina ({o4.Centerlines.Count} ejes)");
    // Las tuberías conservan el emparejamiento de siempre (estas reglas se aplican a conductos y bandejas)
    Check(!OutlineDetector.Settings.ForPipes(1, 10).CleanDrawing && !OutlineDetector.Settings.ForWalls(1, 10).CleanDrawing &&
          LayerPlanning.OutlineSettings(MepKind.Conducto, 1, 10).CleanDrawing && LayerPlanning.OutlineSettings(MepKind.Bandeja, 1, 10).CleanDrawing,
          "Dibujo interior: reglas para conductos y bandejas; tuberías y muros como antes");
    // Unidades: la cabecera dice mm y el dibujo (159 × 23 unidades) es un garaje en metros
    var ud = new CadDrawing { HeaderUnit = DrawingUnit.Milimetros };
    for (int i = 0; i < 20; i++) ud.Segments.Add(new CadSegment { Layer = "X", Start = new P3(i * 159.0 / 19, 0, 0), End = new P3(i * 159.0 / 19, 23, 0) });
    string? fix = LayerPlanning.FixImplausibleUnits(ud);
    Check(fix != null && ud.HeaderUnit == DrawingUnit.Metros && ud.UnitCorrection.Length > 0, "Unidades absurdas (mm para un garaje de 159 unidades): se lee en metros y se explica");
    var okd = new CadDrawing { HeaderUnit = DrawingUnit.Milimetros };
    for (int i = 0; i < 20; i++) okd.Segments.Add(new CadSegment { Layer = "X", Start = new P3(i * 50000.0 / 19, 0, 0), End = new P3(i * 50000.0 / 19, 8000, 0) });
    Check(LayerPlanning.FixImplausibleUnits(okd) is null && okd.HeaderUnit == DrawingUnit.Milimetros, "Unidades razonables (50 m en mm): no se tocan");
}

Console.WriteLine("2f) Líneas alineadas que se pisan (capa de línea simple)");
{
    CadSegment L(double x1, double y1, double x2, double y2, double? w = null) =>
        new() { Layer = "AF", Start = new P3(x1, y1, 0), End = new P3(x2, y2, 0), WidthMm = w };
    var cfg = new NetworkBuilder.Settings();
    var o1 = NetworkBuilder.Build(new[] { L(0, 0, 1000, 0), L(0, 0, 500, 0) }, cfg);
    Check(o1.Segments.Count == 1 && Math.Abs(o1.Segments[0].Length - 1000) < 1 && o1.Junctions.Count == 0 && o1.MergedOverlaps == 1,
        $"Línea corta dibujada sobre el principio de otra: 1 tramo de 1000 mm (salen {o1.Segments.Count} tramos, {o1.Segments.Sum(x => x.Length):0} mm)");
    var o2 = NetworkBuilder.Build(new[] { L(0, 0, 1000, 0), L(200, 0, 700, 0) }, cfg);
    Check(o2.Segments.Count == 1 && Math.Abs(o2.Segments[0].Length - 1000) < 1 && o2.Junctions.Count == 0,
        $"Línea dibujada dentro de otra: 1 tramo y ninguna te falsa (salen {o2.Segments.Count} tramos, {o2.Junctions.Count} uniones)");
    var o3 = NetworkBuilder.Build(new[] { L(0, 0, 600, 0), L(1000, 0, 400, 0) }, cfg);
    Check(o3.Segments.Count == 1 && Math.Abs(o3.Segments[0].Length - 1000) < 1 && o3.Junctions.Count == 0,
        $"Dos líneas que se pisan en parte (una dibujada al revés): 1 tramo de 1000 mm (salen {o3.Segments.Count} tramos, {o3.Segments.Sum(x => x.Length):0} mm)");
    var o4 = NetworkBuilder.Build(new[] { L(0, 0, 600, 0), L(400, 0, 1000, 0), L(500, 0, 500, 800) }, cfg);
    Check(o4.Segments.Count == 3 && o4.Junctions.Count == 1 && o4.Count(JunctionKind.Te) == 1 &&
          o4.Junctions[0].Point.DistanceTo(new P3(500, 0, 0)) < 1,
        "Solape con un ramal en medio: 3 tramos y 1 te verdadera en (500, 0)");
    var o5 = NetworkBuilder.Build(new[] { L(0, 0, 1000, 0, 600), L(0, 0, 500, 0, 400) }, cfg);
    Check(o5.Segments.Count == 1 && Math.Abs(o5.Segments[0].Length - 1000) < 1 && o5.Segments[0].WidthMm == 600 &&
          o5.CoaxialResolved == 1 && o5.Junctions.Count == 0,
        "Tramo dibujado DENTRO de otro de distinto ancho (el mismo leído dos veces): queda uno, con el ancho que continúa (600)");

    // --- Cuerpos de pieza y uniones (reglas medidas con los planos reales: ver Pruebas\RESULTADO_UNIONES…)
    var u6 = NetworkBuilder.Build(new[] { L(0, 0, 1000, 0), L(1000, 0, 2000, 0), L(1000, 0, 1000, 30) }, cfg);
    Check(u6.Segments.Count == 1 && Math.Abs(u6.Segments[0].Length - 2000) < 1 && u6.Junctions.Count == 0,
        $"Ramal más corto que el mínimo en una te: se quita y los dos tramos alineados quedan unidos en uno (salen {u6.Segments.Count})");
    var u7 = NetworkBuilder.Build(new[] { L(0, 0, 2000, 0, 110), L(2000, 0, 2060, 0, 135) }, cfg);
    Check(u7.Segments.Count == 1 && Math.Abs(u7.Segments[0].Length - 2060) < 1 && u7.Segments[0].WidthMm == 110 && u7.StubsAbsorbed == 1 &&
          u7.Count(JunctionKind.Transicion) == 0,
        "Trocito corto más ancho al final de un tubo (embocadura de una pieza): toma el ancho del tubo, sin transición falsa");
    var u8 = NetworkBuilder.Build(new[] { L(0, 0, 2000, 0, 110), L(2000, 0, 2060, 0, 135) },
        new NetworkBuilder.Settings { TransitionHints = new[] { new P3(2010, 0, 0) } });
    Check(u8.Segments.Count == 2 && u8.Count(JunctionKind.Transicion) == 1 && u8.StubsAbsorbed == 0,
        "El mismo caso con un bloque de transición dibujado ahí: el cambio de ancho es real y se respeta");
    var u9 = NetworkBuilder.Build(new[] { L(0, 0, 3000, 0, 110), L(3000, 0, 6000, 0, 90) }, cfg);
    Check(u9.Segments.Count == 2 && u9.Count(JunctionKind.Transicion) == 1,
        "Cambio de diámetro entre dos tubos largos: transición verdadera, no se toca");
    var u10 = NetworkBuilder.Build(new[] { L(0, 0, 5000, 0, 1000), L(5000, 0, 10000, 150, 900) }, cfg);
    Check(u10.Count(JunctionKind.Transicion) == 1 && u10.Count(JunctionKind.Codo) == 0,
        "Dos tramos casi en línea (reducción excéntrica, 1,7°) de distinto ancho: transición, no un codo de 2°");
    var u11 = NetworkBuilder.Build(new[] { L(0, 0, 6000, 0, 1000), L(6000, 0, 8600, 0, 900) }, cfg);
    Check(u11.Segments.Count == 2 && u11.Count(JunctionKind.Transicion) == 1 && u11.StubsAbsorbed == 0,
        "Conducto de 900 mm y 2,6 m de largo tras otro de 1000: es un tramo real (no el cuerpo de una pieza) y hay transición");
    Check(NetworkBuilder.Build(new[] { L(0, 0, 2000, 0, 20), L(2000, 0, 2055, 0, 33) }, cfg).Segments.Count == 1,
        "Cuerpo de 55 mm y Ø33 al final de un tubo de Ø20: se absorbe en el tubo");

    // Cuerpo de una te dibujado con líneas entre dos trozos de tubo (doble línea): no es un tramo
    CadSegment O(double x1, double y1, double x2, double y2) => new() { Layer = "P-PIPE", Start = new P3(x1, y1, 0), End = new P3(x2, y2, 0) };
    var body = new List<CadSegment>
    {
        O(0, 10, 1000, 10), O(0, -10, 1000, -10), O(0, -10, 0, 10),            // tubo Ø20, 0–1000
        O(1060, 10, 2000, 10), O(1060, -10, 2000, -10), O(2000, -10, 2000, 10),  // tubo Ø20, 1060–2000
        O(1000, 24, 1060, 24), O(1000, -10, 1060, -10), O(1000, -10, 1000, 24), O(1060, -10, 1060, 24) // cuerpo excéntrico de 34 mm
    };
    var bo = OutlineDetector.Detect(body, OutlineDetector.Settings.ForPipes(1, 10));
    var bn = NetworkBuilder.Build(bo.Centerlines, cfg);
    Console.WriteLine($"     cuerpo de te: cuerpos descartados {bo.FittingBodies}, huecos cerrados {bo.GapsClosed}, tramos {bn.Segments.Count} ({string.Join("/", bn.Segments.Select(x => $"{x.Length:0}×Ø{x.WidthMm}"))})");
    Check(bo.FittingBodies == 1 && bn.Segments.Count == 1 && Math.Abs(bn.Segments[0].Length - 2000) < 2 && bn.Segments[0].WidthMm == 20,
        "Cuerpo de una te dibujado con líneas entre dos trozos de tubo: se descarta y queda UN tubo de 2000 mm, Ø20");

    // Reducción excéntrica de conducto (una cara enrasada): los dos tramos se unen con una transición
    CadSegment D(double x1, double y1, double x2, double y2) => new() { Layer = "M-HVAC-DUCT", Start = new P3(x1, y1, 0), End = new P3(x2, y2, 0) };
    var red = new List<CadSegment>
    {
        D(0, 500, 3000, 500), D(0, -500, 3000, -500), D(0, -500, 0, 500), D(3000, -500, 3000, 500),       // 1000 de ancho
        D(3110, 500, 6000, 500), D(3110, -400, 6000, -400), D(3110, -400, 3110, 500), D(6000, -400, 6000, 500) // 900, cara de arriba enrasada
    };
    var ro2 = OutlineDetector.Detect(red, new OutlineDetector.Settings { UnitToMm = 1 });
    var rn2 = NetworkBuilder.Build(ro2.Centerlines, cfg);
    Console.WriteLine($"     reducción excéntrica: tramos {rn2.Segments.Count}, transiciones {rn2.Count(JunctionKind.Transicion)}, codos {rn2.Count(JunctionKind.Codo)}, no admitidas {rn2.Count(JunctionKind.NoAdmitida)}");
    Check(rn2.Segments.Count == 2 && rn2.Count(JunctionKind.Transicion) == 1 && rn2.Junctions.Count == 1,
        "Reducción excéntrica 1000 → 900 con hueco de 110 mm: 2 conductos unidos por 1 transición");
    var o6 = NetworkBuilder.Build(new[] { L(0, 0, 1000, 0), L(500, -500, 500, 500) }, cfg);
    Check(o6.Segments.Count == 2 && o6.Junctions.Count == 0, "Cruce en X sin vértice común: dos tramos que se cruzan sin unirse (no se inventa una cruz)");
}

Console.WriteLine("2g) Curvas que hacen de codo (arcos y tramos curvos de polilínea)");
{
    var cfg = new NetworkBuilder.Settings();
    foreach (double radius in new[] { 100.0, 30.0 })
    {
        // Línea horizontal + arco de 90° + línea vertical: un codo dibujado con su radio
        var d = new CadDocument();
        var lay = new Layer("M-TUBERIA-AF"); d.Layers.Add(lay);
        d.Entities.Add(new Line(new XYZ(-1000, radius, 0), new XYZ(0, radius, 0)) { Layer = lay });
        d.Entities.Add(new Arc { Center = new XYZ(0, 0, 0), Radius = radius, StartAngle = 0, EndAngle = Math.PI / 2, Layer = lay });
        d.Entities.Add(new Line(new XYZ(radius, 0, 0), new XYZ(radius, -1000, 0)) { Layer = lay });
        var net = NetworkBuilder.Build(CadReader.Extract(d, "memoria").Segments, cfg);
        Check(net.Segments.Count == 2 && net.Junctions.Count == 1 && net.Count(JunctionKind.Codo) == 1 && net.FilletsCollapsed == 1 &&
              net.Junctions[0].Point.DistanceTo(new P3(radius, radius, 0)) < 1,
            $"Arco de radio {radius:0} mm entre dos líneas: 2 tramos y 1 codo en la esquina ({radius:0}, {radius:0}) (salen {net.Segments.Count} tramos, {net.Junctions.Count} uniones)");
    }
    {
        // Polilínea con una curva de 90° (bulge) entre dos tramos rectos
        var d = new CadDocument();
        var lay = new Layer("M-TUBERIA-ACS"); d.Layers.Add(lay);
        var poly = new LwPolyline { Layer = lay };
        poly.Vertices.Add(new LwPolyline.Vertex(new XY(0, 0)));
        poly.Vertices.Add(new LwPolyline.Vertex(new XY(900, 0)) { Bulge = Math.Tan(Math.PI / 8) });
        poly.Vertices.Add(new LwPolyline.Vertex(new XY(1000, 100)));
        poly.Vertices.Add(new LwPolyline.Vertex(new XY(1000, 1000)));
        d.Entities.Add(poly);
        var net = NetworkBuilder.Build(CadReader.Extract(d, "memoria").Segments, cfg);
        Check(net.Segments.Count == 2 && net.Junctions.Count == 1 && net.Count(JunctionKind.Codo) == 1 &&
              net.Junctions[0].Point.DistanceTo(new P3(1000, 0, 0)) < 1,
            $"Polilínea con una curva de 90°: 2 tramos y 1 codo en la esquina (1000, 0) (salen {net.Segments.Count} tramos, {net.Junctions.Count} uniones)");
    }
    {
        // Arco suelto: no une dos tramos rectos, así que se queda como cuerda
        var d = new CadDocument();
        var lay = new Layer("M-TUBERIA-AF"); d.Layers.Add(lay);
        d.Entities.Add(new Arc { Center = new XYZ(0, 0, 0), Radius = 500, StartAngle = 0, EndAngle = Math.PI / 2, Layer = lay });
        var net = NetworkBuilder.Build(CadReader.Extract(d, "memoria").Segments, cfg);
        Check(net.Segments.Count == 1 && net.FilletsCollapsed == 0, "Arco suelto (no une dos tramos rectos): se mantiene como cuerda");
    }
    {
        // Curva grande (radio 5 m): prolongar los tramos 5 m cambiaría el trazado; se deja como cuerda
        var d = new CadDocument();
        var lay = new Layer("M-TUBERIA-AF"); d.Layers.Add(lay);
        d.Entities.Add(new Line(new XYZ(-1000, 5000, 0), new XYZ(0, 5000, 0)) { Layer = lay });
        d.Entities.Add(new Arc { Center = new XYZ(0, 0, 0), Radius = 5000, StartAngle = 0, EndAngle = Math.PI / 2, Layer = lay });
        d.Entities.Add(new Line(new XYZ(5000, 0, 0), new XYZ(5000, -1000, 0)) { Layer = lay });
        var net = NetworkBuilder.Build(CadReader.Extract(d, "memoria").Segments, cfg);
        Check(net.Segments.Count == 3 && net.FilletsCollapsed == 0, "Curva de radio 5 m: no se convierte en esquina (se mantiene la cuerda)");
    }
}

Console.WriteLine("2h) Doble línea: dos conductos paralelos cercanos");
{
    var two = new List<CadSegment>();
    void DL(double x1, double y1, double x2, double y2) => two.Add(new CadSegment { Layer = "D", Start = new P3(x1, y1, 0), End = new P3(x2, y2, 0) });
    foreach (double y in new[] { 0.0, 1500.0 })
    {
        DL(0, y, 2000, y); DL(2000, y, 2000, y + 400); DL(2000, y + 400, 0, y + 400); DL(0, y + 400, 0, y);
    }
    var po = OutlineDetector.Detect(two, new OutlineDetector.Settings());
    Check(po.Centerlines.Count == 2 && po.Reoriented == 0 && po.Centerlines.All(c => c.WidthMm == 400 && Math.Abs(c.Length - 2000) < 1),
        $"Dos conductos de 2000×400 en paralelo a 1,1 m: 2 ejes de 400 mm de ancho, sin leerlos girados (anchos {string.Join("/", po.Centerlines.Select(c => c.WidthMm))})");
}

Console.WriteLine("2i) Cotas e inventario al modelar (núcleo, fuera de Revit)");
{
    // Lectura de textos y cotas: se guardan además de contarse, y cada tramo recuerda su entidad.
    var d = new CadDocument();
    d.Header.InsUnits = ACadSharp.Types.Units.UnitsType.Millimeters;
    var duct = new Layer("M-CONDUCTO-IMP"); d.Layers.Add(duct);
    var txt = new Layer("TEXTOS"); d.Layers.Add(txt);
    d.Entities.Add(new Line(new XYZ(0, 0, 0), new XYZ(2000, 0, 0)) { Layer = duct });
    d.Entities.Add(new Line(new XYZ(0, -200, 0), new XYZ(2000, -200, 0)) { Layer = duct });   // contorno del conducto
    d.Entities.Add(new Line(new XYZ(0, 200, 0), new XYZ(2000, 200, 0)) { Layer = duct });
    d.Entities.Add(new TextEntity { Value = "AFS:%%C16", InsertPoint = new XYZ(0, 500, 0), Height = 100, Layer = txt });
    d.Entities.Add(new MText { Value = "{\\fArial|b0;\\T1.1;Conducto 300x200}\\P\\U+00D8100", InsertPoint = new XYZ(0, 800, 0), Height = 100, Layer = txt });
    d.Entities.Add(new DimensionAligned { FirstPoint = new XYZ(1000, -200, 0), SecondPoint = new XYZ(1000, 200, 0), Layer = txt });
    d.Entities.Add(new DimensionLinear { FirstPoint = new XYZ(0, 0, 0), SecondPoint = new XYZ(0, 400, 0), Rotation = Math.PI / 2, Layer = txt });
    var r = CadReader.Extract(d, "memoria");
    Check(r.Entities.Count == 7 && r.Segments.Count == 3 && r.Segments[0].EntityIndex == r.Entities[0].Index,
        $"Inventario: 7 entidades numeradas y cada tramo recuerda su entidad ({r.Entities.Count})");
    Check(r.Texts.Count == 2 && r.Texts[0].Value == "AFS:Ø16" && r.Texts[1].Value == "Conducto 300x200 Ø100",
        $"Textos leídos sin códigos de formato: «{string.Join("» «", r.Texts.Select(t => t.Value))}»");
    Check(r.Dimensions.Count == 2 && Math.Abs(r.Dimensions[0].Measurement - 400) < 1e-6 && r.Dimensions[0].Kind == "alineada",
        $"Cota alineada: valor medido 400 ({(r.Dimensions.Count > 0 ? r.Dimensions[0].Measurement : double.NaN)})");
    Check(r.Dimensions.Count == 2 && Math.Abs(r.Dimensions[1].Measurement - 400) < 1e-6 && r.Dimensions[1].Direction is P3 dl && Math.Abs(dl.Y - 1) < 1e-9,
        $"Cota lineal girada 90° (giro en radianes): mide 400 en Y ({(r.Dimensions.Count > 1 ? r.Dimensions[1].Measurement : double.NaN)})");
    Check(r.IgnoredEntityCounts.Values.Sum() == 4, "Textos y cotas se siguen contando como antes (avisos idénticos)");

    // Rótulos de tamaño
    var l1 = SizeAnnotations.ParseLabel("AFS:Ø16");
    var l2 = SizeAnnotations.ParseLabel("200x200");
    var l3 = SizeAnnotations.ParseLabel("DN25");
    Check(l1 is { Readable: true, Shape: SizeShape.Redonda, ValueMm: 16, Prefix: "AFS" } && l2 is { Readable: true, Shape: SizeShape.AnchoAlto, ValueMm: 200, HeightMm: 200 } &&
          l3 is { Readable: true, ValueMm: 25 }, "Rótulos: «AFS:Ø16» → Ø16 (AFS), «200x200» → 200×200, «DN25» → Ø25");
    Check(SizeAnnotations.ParseLabel("Ø16 / Ø20") is { Readable: false } && SizeAnnotations.ParseLabel("SUMIDERO") is null &&
          SizeAnnotations.ParseLabel("1x50mm2") is null, "Rótulos: dos diámetros = ilegible; texto normal o «1x50mm2» = no es un tamaño");

    // Cotas: solo se aplican si cruzan el tramo de lado a lado
    var targets = new List<SizeTarget> { new() { Id = "A", Layer = "M-CONDUCTO-IMP", Start = new P3(0, 0, 0), End = new P3(2000, 0, 0), Shape = SizeShape.AnchoAlto } };
    var a = SizeAnnotations.Associate(r, targets, new SizeAnnotations.Settings { UnitToMm = 1 });
    Check(a.Decisions.TryGetValue("A", out var dec) && dec.ValueMm == 400 && dec.Source == SizeSource.Cota,
        "Cota alineada que cruza el conducto: ancho 400 mm (valor medido)");
    Check(a.Annotations.Count(x => !x.Applied) == 2 + 1 && a.Annotations.Count == a.Annotations.Count(x => x.Applied) + a.Annotations.Count(x => !x.Applied),
        $"La cota lineal que no cruza el tramo y los rótulos sobrantes se anotan como no aplicados ({string.Join(" | ", a.Annotations.Where(x => !x.Applied).Select(x => x.Reason))})");
    var d2 = new CadDrawing();
    d2.Dimensions.Add(new CadDimension { Kind = "alineada", Measurement = 400, Text = "350", FirstPoint = new P3(1000, -200, 0), SecondPoint = new P3(1000, 200, 0), Direction = new P3(0, 1, 0) });
    var a2 = SizeAnnotations.Associate(d2, targets, new SizeAnnotations.Settings());
    Check(a2.Decisions.Count == 0 && a2.Annotations[0].Reason.StartsWith("contradictoria"), "Cota cuyo texto escrito (350) contradice lo que mide (400): no se aplica");
    // Cota de pared a pared (sus puntos en otra capa) que pasa centrada por encima del conducto: no mide el conducto
    var d2b = new CadDrawing();
    d2b.Segments.Add(new CadSegment { Layer = "M-CONDUCTO-IMP", Start = new P3(0, -200, 0), End = new P3(2000, -200, 0) });
    d2b.Segments.Add(new CadSegment { Layer = "M-CONDUCTO-IMP", Start = new P3(0, 200, 0), End = new P3(2000, 200, 0) });
    d2b.Segments.Add(new CadSegment { Layer = "A-MUROS", Start = new P3(0, -750, 0), End = new P3(2000, -750, 0) });
    d2b.Segments.Add(new CadSegment { Layer = "A-MUROS", Start = new P3(0, 750, 0), End = new P3(2000, 750, 0) });
    d2b.Dimensions.Add(new CadDimension { Kind = "alineada", Measurement = 1500, FirstPoint = new P3(1000, -750, 0), SecondPoint = new P3(1000, 750, 0), Direction = new P3(0, 1, 0) });
    var a2b = SizeAnnotations.Associate(d2b, targets, new SizeAnnotations.Settings());
    Check(a2b.Decisions.Count == 0 && a2b.Annotations[0].Reason.Contains("no están sobre las líneas"),
        "Cota de pared a pared (1500 mm) que cruza el conducto: no se aplica (sus puntos no están en el contorno del conducto)");

    // Rótulos: prefijo como palabra completa, capa relacionada y ambigüedad entre dos tuberías paralelas
    var pipes = new List<SizeTarget>
    {
        new() { Id = "AFS", Layer = "AFS_Fria", Start = new P3(0, 0, 0), End = new P3(3000, 0, 0), Shape = SizeShape.Redonda },
        new() { Id = "AFS2", Layer = "AFS_Retorno", Start = new P3(0, -100, 0), End = new P3(3000, -100, 0), Shape = SizeShape.Redonda },
        new() { Id = "RACS", Layer = "RACS_Retorno", Start = new P3(0, 100, 0), End = new P3(3000, 100, 0), Shape = SizeShape.Redonda },
        new() { Id = "ACS", Layer = "ACS_Caliente", Start = new P3(0, 300, 0), End = new P3(3000, 300, 0), Shape = SizeShape.Redonda },
        new() { Id = "DUCT", Layer = "-P_CL_CONDUCTO", Start = new P3(0, 2000, 0), End = new P3(3000, 2000, 0), Shape = SizeShape.AnchoAlto }
    };
    var d3 = new CadDrawing();
    d3.Texts.Add(new CadText { EntityIndex = 1, Layer = "AFS_TEXTOS", Value = "ACS:Ø20", Position = new P3(500, 120, 0) });
    d3.Texts.Add(new CadText { EntityIndex = 2, Layer = "AFS_TEXTOS", Value = "Ø16", Position = new P3(800, -50, 0) });
    d3.Texts.Add(new CadText { EntityIndex = 3, Layer = "E-VIGAS", Value = "V 30x50", Position = new P3(800, 2100, 0) });
    d3.Texts.Add(new CadText { EntityIndex = 4, Layer = "-P_CL_TEXTOS", Value = "400x300", Position = new P3(1500, 2150, 0) });
    var a3 = SizeAnnotations.Associate(d3, pipes, new SizeAnnotations.Settings());
    Check(a3.Decisions.TryGetValue("ACS", out var acs) && acs.ValueMm == 20 && !a3.Decisions.ContainsKey("RACS"),
        "Rótulo «ACS:Ø20»: va a la capa ACS_Caliente (no a RACS_Retorno aunque esté más cerca)");
    Check(!a3.Decisions.ContainsKey("AFS") && !a3.Decisions.ContainsKey("AFS2") && a3.Annotations.Single(x => x.EntityIndex == 2).Reason.Contains("misma distancia"),
        "Rótulo «Ø16» entre dos tuberías AFS a la misma distancia: ambiguo, no se aplica");
    Check(a3.Annotations.Single(x => x.EntityIndex == 3).Reason.Contains("ni su prefijo ni su capa") &&
          a3.Decisions.TryGetValue("DUCT", out var ductDec) && ductDec.ValueMm == 400 && ductDec.HeightMm == 300,
        "Rótulo de estructura «V 30x50» (capa E-VIGAS) no se aplica al conducto; «400x300» de la capa -P_CL_TEXTOS sí");

    // ---- Diámetros escritos junto a las tuberías (versión combinada)
    Console.WriteLine("2j) Rótulos de diámetro junto a las tuberías");
    Check(SizeAnnotations.ParseLabel("Ø110x10") is { Readable: true, Shape: SizeShape.Redonda, ValueMm: 110 } &&
          SizeAnnotations.ParseLabel("PE Ø32x2,9") is { Readable: true, ValueMm: 32 },
        "«Ø110x10» y «PE Ø32x2,9» (diámetro × espesor) → diámetro 110 y 32, no un ancho × alto");
    Check(SizeAnnotations.ParseLabel("Ø1/2\"") is { Readable: true, ValueMm: 15 } && SizeAnnotations.ParseLabel("DN 1 1/4\"") is { Readable: true, ValueMm: 32 } &&
          SizeAnnotations.ParseLabel("DN-25") is { Readable: true, ValueMm: 25 } && SizeAnnotations.ParseLabel("Diámetro 40") is { Readable: true, ValueMm: 40 },
        "Pulgadas «Ø1/2\"» → 15, «DN 1 1/4\"» → 32; «DN-25» → 25; «Diámetro 40» → 40");
    var multi = SizeAnnotations.ParseLabels("AFS Ø16 ACS Ø20");
    Check(multi is { Count: 2 } && multi[0] is { Prefix: "AFS", ValueMm: 16 } && multi[1] is { Prefix: "ACS", ValueMm: 20 },
        "«AFS Ø16 ACS Ø20» → dos medidas, cada una con su capa");

    // Lectura: centro del texto (no su esquina), atributos de bloque y directrices
    var dLbl = new CadDocument();
    dLbl.Header.InsUnits = ACadSharp.Types.Units.UnitsType.Millimeters;
    var tl = new Layer("TEXTOS"); dLbl.Layers.Add(tl);
    dLbl.Entities.Add(new TextEntity { Value = "%%C25", InsertPoint = new XYZ(1000, 1000, 0), Height = 100, Rotation = Math.PI / 2, Layer = tl });
    dLbl.Entities.Add(new TextEntity
    {
        Value = "DN32", InsertPoint = new XYZ(0, 0, 0), AlignmentPoint = new XYZ(5000, 3000, 0), Height = 100,
        HorizontalAlignment = TextHorizontalAlignment.Center, VerticalAlignment = TextVerticalAlignmentType.Middle, Layer = tl
    });
    var tagBlock = new BlockRecord("ETIQUETA_TUBO");
    dLbl.BlockRecords.Add(tagBlock);
    var tag = new Insert(tagBlock) { InsertPoint = new XYZ(8000, 0, 0), Layer = tl };
    tag.Attributes.Add(new AttributeEntity { Tag = "DIAM", Value = "Ø40", InsertPoint = new XYZ(8000, 50, 0), Height = 100 });
    dLbl.Entities.Add(tag);
    dLbl.Entities.Add(new TextEntity { Value = "Ø50", InsertPoint = new XYZ(12000, 2000, 0), Height = 100, Layer = tl });
    dLbl.Entities.Add(new Leader { Vertices = new List<XYZ> { new(12500, 0, 0), new(12100, 1950, 0) }, Layer = tl });
    var rl = CadReader.Extract(dLbl, "rotulos");
    var t25 = rl.Texts.FirstOrDefault(t => t.Value == "Ø25");
    Check(t25 != null && Math.Abs(t25.Center.X - 950) < 1 && Math.Abs(t25.Center.Y - 1112.5) < 1 && Math.Abs(t25.RotationRad - Math.PI / 2) < 1e-9,
        $"TEXT vertical «Ø25»: se mide desde su centro (950; 1112,5), no desde su esquina ({t25?.Center})");
    var t32 = rl.Texts.FirstOrDefault(t => t.Value == "DN32");
    Check(t32 != null && t32.Center.DistanceTo(new P3(5000, 3000, 0)) < 1, $"TEXT centrado «DN32»: centro en su punto de alineación ({t32?.Center})");
    Check(rl.Texts.Any(t => t.Value == "Ø40" && t.Source == "ATTRIB" && t.EntityIndex == rl.Blocks.Single().EntityIndex),
        "Atributo «Ø40» de un bloque de etiqueta: se lee como rótulo");
    var t50 = rl.Texts.FirstOrDefault(t => t.Value == "Ø50");
    Check(t50?.Anchor is P3 an && an.DistanceTo(new P3(12500, 0, 0)) < 1e-6, "Directriz (LEADER) cuyo final toca el texto «Ø50»: el rótulo señala la punta de la flecha");
    Check(rl.Entities.Count == 5, $"Inventario: 5 entidades (4 textos o bloques y la directriz); los atributos no añaden entidades ({rl.Entities.Count})");

    // Asociación: capa de textos genérica, centro del texto, directriz, recorrido y te
    //   Tubería AF: A (0,0)-(4000,0) → codo → B (4000,0)-(4000,3000) → te en (4000,3000) con C y D.
    //   Tubería SAN dibujada con sus dos bordes (Ø110) lejos de las demás.
    var tubes = new List<SizeTarget>
    {
        new() { Id = "A", Layer = "FONTANERIA AF", Start = new P3(0, 0, 0), End = new P3(4000, 0, 0), Shape = SizeShape.Redonda },
        new() { Id = "B", Layer = "FONTANERIA AF", Start = new P3(4000, 0, 0), End = new P3(4000, 3000, 0), Shape = SizeShape.Redonda },
        new() { Id = "C", Layer = "FONTANERIA AF", Start = new P3(4000, 3000, 0), End = new P3(4000, 6000, 0), Shape = SizeShape.Redonda },
        new() { Id = "D", Layer = "FONTANERIA AF", Start = new P3(4000, 3000, 0), End = new P3(8000, 3000, 0), Shape = SizeShape.Redonda },
        new() { Id = "S1", Layer = "SANEAMIENTO", Start = new P3(20000, 0, 0), End = new P3(24000, 0, 0), Shape = SizeShape.Redonda },
        new() { Id = "S2", Layer = "SANEAMIENTO", Start = new P3(20000, 110, 0), End = new P3(24000, 110, 0), Shape = SizeShape.Redonda }
    };
    var dt = new CadDrawing();
    // Texto horizontal de 1200 mm de largo: su esquina está a 1100 mm del tubo A, pero su centro a 200 mm.
    dt.Texts.Add(new CadText { EntityIndex = 1, Layer = "TEXTOS", Value = "Ø25", Position = new P3(-400, 200, 0), CenterPoint = new P3(1000, 200, 0) });
    // Rótulo con directriz cuya flecha toca el tubo D (el texto queda más cerca de C).
    dt.Texts.Add(new CadText { EntityIndex = 2, Layer = "TEXTOS", Value = "Ø20", Position = new P3(4300, 3600, 0), Anchor = new P3(6000, 3000, 0) });
    dt.Texts.Add(new CadText { EntityIndex = 3, Layer = "TEXTOS", Value = "Ø110", Position = new P3(22000, 300, 0) });
    var at = SizeAnnotations.Associate(dt, tubes, new SizeAnnotations.Settings());
    Check(at.Decisions.TryGetValue("A", out var dA) && dA.ValueMm == 25 && dA.Source == SizeSource.Rotulo,
        "Rótulo «Ø25» en una capa de textos genérica, medido desde el centro del texto: se aplica al tubo A");
    Check(at.Decisions.TryGetValue("B", out var dB) && dB.ValueMm == 25 && dB.Source == SizeSource.Recorrido,
        "El tubo B, unido a A por un codo, toma la misma medida (mismo recorrido)");
    Check(!at.Decisions.ContainsKey("C"), "El tubo C, después de la te, no la toma (la te separa recorridos)");
    Check(at.Decisions.TryGetValue("D", out var dD) && dD.ValueMm == 20, "Rótulo con directriz: se aplica al tubo que señala la flecha (D)");
    Check(at.Decisions.TryGetValue("S1", out var s1) && s1.ValueMm == 110 && at.Decisions.TryGetValue("S2", out var s2) && s2.ValueMm == 110,
        "Tubo dibujado con sus dos bordes: el rótulo «Ø110» vale para las dos líneas");
    var noProp = SizeAnnotations.Associate(dt, tubes, new SizeAnnotations.Settings { PropagateAlongRuns = false });
    Check(!noProp.Decisions.ContainsKey("B"), "Sin propagar por el recorrido, B no se toca (comprobación de la prueba anterior)");
    // Recorrido con dos rótulos distintos: cada tramo toma el más próximo; el del medio, si está a la misma distancia, no se toca
    var chain = Enumerable.Range(0, 5).Select(i => new SizeTarget
    {
        Id = "R" + i, Layer = "AF", Shape = SizeShape.Redonda, Start = new P3(i * 1000, 0, 0), End = new P3(i * 1000 + 1000, 0, 0)
    }).ToList();
    var dc = new CadDrawing();
    dc.Texts.Add(new CadText { EntityIndex = 1, Layer = "AF", Value = "Ø32", Position = new P3(500, 150, 0) });
    dc.Texts.Add(new CadText { EntityIndex = 2, Layer = "AF", Value = "Ø25", Position = new P3(4500, 150, 0) });
    var ac = SizeAnnotations.Associate(dc, chain, new SizeAnnotations.Settings());
    Check(ac.Decisions["R1"].ValueMm == 32 && ac.Decisions["R3"].ValueMm == 25 && !ac.Decisions.ContainsKey("R2"),
        "Recorrido con Ø32 y Ø25: cada tramo toma el más próximo; el del medio (empate) no se toca");

    // ---- Planos exportados de Revit (como un plano de fontanería exportado)
    Console.WriteLine("2k) Planos exportados de Revit: tuberías con sus dos bordes, rótulos con directriz de líneas, capas -IDEN/-CNTR");
    Check(MepKinds.Suggest("P-PIPE") == MepKind.Tuberia && MepKinds.Suggest("P-PIPE-IDEN") == MepKind.NoImportar &&
          MepKinds.Suggest("M-HVAC-DUCT-ANNO") == MepKind.NoImportar && MepKinds.Suggest("M-HVAC-CDFF") == MepKind.NoImportar &&
          MepKinds.Suggest("M-HVAC-DUCT") == MepKind.Conducto,
        "Capas de Revit: P-PIPE = tubería; -IDEN (rótulos y directrices), -ANNO y difusores dibujados = no se modelan");
    Check(LayerPlanning.CenterlineDuplicateOf("M-HVAC-DUCT-CNTR", new[] { "M-HVAC-DUCT", "M-HVAC-DUCT-CNTR" }) == "M-HVAC-DUCT" &&
          LayerPlanning.CenterlineDuplicateOf("P-PIPE-CNTR", new[] { "P-PIPE-CNTR" }) is null,
        "Capa de ejes -CNTR: no se modela si existe la capa del contorno (sí si es la única)");
    // Tubería Ø20 dibujada como rectángulos cerrados (bordes + testas) con un codo-bloque entre ellos (hueco de 30 mm)
    var pipeRects = new List<CadSegment>();
    void Rect(double x0, double y0, double x1, double y1)
    {
        pipeRects.Add(new CadSegment { Layer = "P-PIPE", Start = new P3(x0, y0, 0), End = new P3(x1, y0, 0) });
        pipeRects.Add(new CadSegment { Layer = "P-PIPE", Start = new P3(x1, y0, 0), End = new P3(x1, y1, 0) });
        pipeRects.Add(new CadSegment { Layer = "P-PIPE", Start = new P3(x1, y1, 0), End = new P3(x0, y1, 0) });
        pipeRects.Add(new CadSegment { Layer = "P-PIPE", Start = new P3(x0, y1, 0), End = new P3(x0, y0, 0) });
    }
    Rect(0, -10, 2000, 10);          // horizontal, eje y=0, Ø20, termina en x=2000
    Rect(2020, 30, 2040, 1500);      // vertical, eje x=2030, empieza en y=30 (codo en (2030, 0))
    Check(LayerPlanning.LooksDoubleLine(pipeRects, MepKind.Tuberia, 1, 10), "Tubería dibujada con sus dos bordes y testas: se detecta (un tubo por el eje)");
    var po = OutlineDetector.Detect(pipeRects, LayerPlanning.OutlineSettings(MepKind.Tuberia, 1, 10));
    var pn = NetworkBuilder.Build(po.Centerlines, new NetworkBuilder.Settings { UnitToMm = 1 });
    Check(pn.Segments.Count == 2 && pn.Segments.All(s => Math.Abs((s.WidthMm ?? 0) - 20) < 0.5) && pn.Count(JunctionKind.Codo) == 1,
        $"Dos tubos Ø20 separados por un codo dibujado como bloque: 2 tramos de 20 mm y 1 codo ({pn.Segments.Count} tramos, {pn.Count(JunctionKind.Codo)} codos)");
    var twoPipes = new List<CadSegment>();
    for (int i = 0; i < 6; i++)
    {
        twoPipes.Add(new CadSegment { Layer = "AF", Start = new P3(0, i * 2000, 0), End = new P3(3000, i * 2000, 0) });
        twoPipes.Add(new CadSegment { Layer = "AF", Start = new P3(0, i * 2000 + 120, 0), End = new P3(3000, i * 2000 + 120, 0) });
    }
    twoPipes.Add(new CadSegment { Layer = "AF", Start = new P3(5000, 0, 0), End = new P3(5000, 3000, 0) });
    Check(!LayerPlanning.LooksDoubleLine(twoPipes, MepKind.Tuberia, 1, 10),
        "Dos tuberías de línea simple que van juntas (ida y retorno, sin testas): NO se toman por los bordes de un tubo");
    // Rótulo de Revit con directriz de líneas en su capa: hombro horizontal desde el lado izquierdo del texto y pata hasta el tubo
    var dr = new CadDocument();
    dr.Header.InsUnits = ACadSharp.Types.Units.UnitsType.Millimeters;
    var iden = new Layer("P-PIPE-IDEN"); dr.Layers.Add(iden);
    dr.Entities.Add(new MText { Value = "FON. AFS %%C32", InsertPoint = new XYZ(1000, 800, 0), Height = 100, AttachmentPoint = AttachmentPointType.TopLeft, Layer = iden });
    dr.Entities.Add(new Line(new XYZ(900, 750, 0), new XYZ(1000, 750, 0)) { Layer = iden });   // hombro
    dr.Entities.Add(new Line(new XYZ(900, 750, 0), new XYZ(900, 0, 0)) { Layer = iden });      // pata hasta el tubo (y = 0)
    var rr = CadReader.Extract(dr, "rotulo revit");
    var revitTag = rr.Texts.Single();
    Check(revitTag.Anchor is P3 tip && tip.DistanceTo(new P3(900, 0, 0)) < 1e-6, $"Rótulo «FON. AFS Ø32» con directriz de líneas: señala el punto (900, 0) del tubo ({revitTag.Anchor})");
    var revitPipes = new List<SizeTarget>
    {
        new() { Id = "AFS", Layer = "P-PIPE", Start = new P3(0, 0, 0), End = new P3(3000, 0, 0), Shape = SizeShape.Redonda },
        new() { Id = "ACS", Layer = "P-PIPE", Start = new P3(0, 60, 0), End = new P3(3000, 60, 0), Shape = SizeShape.Redonda }
    };
    var ar = SizeAnnotations.Associate(rr, revitPipes, new SizeAnnotations.Settings { UnitToMm = 1 });
    Check(ar.Decisions.TryGetValue("AFS", out var afs) && afs.ValueMm == 32 && !ar.Decisions.ContainsKey("ACS") && afs.From[0].Prefix == "AFS",
        "Con la directriz, «FON. AFS Ø32» va al tubo que señala (no al paralelo a 60 mm) y se sabe que es AFS");

    // Redes conectadas (para colocar bajo forjado con un eje común)
    var net = NetworkBuilder.Build(new[]
    {
        new CadSegment { Layer = "AF", Start = new P3(0, 0, 0), End = new P3(1000, 0, 0) },
        new CadSegment { Layer = "AF", Start = new P3(1000, 0, 0), End = new P3(1000, 1000, 0) },
        new CadSegment { Layer = "AF", Start = new P3(5000, 0, 0), End = new P3(6000, 0, 0) }
    }, new NetworkBuilder.Settings());
    var comps = NetworkComponents.Find(net.Segments.Select(s => s.Index), net.Junctions);
    Check(comps.Count == 2 && comps.Any(c => c.Count == 2), $"Redes conectadas: el codo une 2 tramos y el tramo suelto es otra red ({comps.Count} redes)");
    Check(NetworkComponents.Samples(new P3(0, 0, 0), new P3(1200, 0, 0), 500).Count == 4, "Puntos de comprobación cada 500 mm con los extremos (1200 mm → 4)");

    // Inventario y cuadre con el plano de ejemplo (simulando que Revit crea todos los tramos previstos)
    var ex = CadReader.Read(dxf);
    var byLayer = LayerPlanning.ByLayer(ex);
    var layers = ex.SegmentLayers.Select(l => (l, MepKinds.Suggest(l))).Where(x => x.Item2 != MepKind.NoImportar)
        .Select(x => (Layer: x.l, Kind: x.Item2, DoubleLine: LayerPlanning.LooksDoubleLine(byLayer[x.l].ToList(), x.Item2, 1, 10))).ToList();
    var exNet = NetworkBuilder.Build(LayerPlanning.NetworkInput(byLayer, layers, 1, 10, null), new NetworkBuilder.Settings());
    var inv = ModelInventory.Classify(new InventoryInput
    {
        Drawing = ex, Created = exNet.Segments.Select(s => new ModeledRun(s.Layer, s.Start, s.End, (s.WidthMm ?? 0) / 2, "Modelada")).ToList(),
        Blocks = ex.Blocks.ToDictionary(b => b.EntityIndex, b => (true, "Modelada: bloque"))
    });
    Check(inv.Balanced && inv.Total == ex.Entities.Count && inv.Modeled + inv.NotModeled == inv.Total && inv.UnexplainedRuns.Count == 0,
        $"Cuadre del plano de ejemplo: {inv.Total} entidades = {inv.Modeled} modeladas + {inv.NotModeled} no modeladas, y cada tramo creado está explicado");
    var invBad = ModelInventory.Classify(new InventoryInput
    {
        Drawing = ex, Created = new List<ModeledRun> { new("M-TUBERIA-AF", new P3(90000, 90000, 0), new P3(91000, 90000, 0), 0, "Modelada") }
    });
    Check(!invBad.Balanced && invBad.UnexplainedRuns.Count == 1, "El cuadre FALLA si se crea un elemento que ninguna entidad del plano explica");
    Check(inv.Entities.Count(e => e.Reason.StartsWith("Descartada")) == 1 && inv.Entities.Any(e => e.Type == "TEXT" && !e.Modeled),
        "El tramo de 20 mm queda como descartado y el texto como anotación no modelada");
}

Console.WriteLine("2m) Botones separados: bandejas, tubos eléctricos, luminarias y PCI");
{
    Check(MepKinds.Suggest("E-POWR-CNDT") == MepKind.TuboElectrico && MepKinds.Suggest("E-CABL-TRAY") == MepKind.Bandeja &&
          MepKinds.Suggest("E-POWR-RISE") == MepKind.NoImportar && MepKinds.Suggest("E-CABL-TRAY-ANNO") == MepKind.NoImportar &&
          MepKinds.Suggest("E-LITE-EQPM-IDEN") == MepKind.NoImportar && MepKinds.Suggest("-P_PCI_RADIOS EXTINTORES") == MepKind.NoImportar,
        "Capas eléctricas de Revit: CNDT → tubo, CABL-TRAY → bandeja; RISE, -ANNO, -IDEN y radios de extintores no se modelan");
    // Clases generales vistas en planos de OTRO proyecto (Escritorio\prueba), no en los de ajuste:
    Check(MepKinds.Suggest("-INST-CLIMA-SIMB") == MepKind.NoImportar && MepKinds.Suggest("Terminales de aire") == MepKind.NoImportar &&
          MepKinds.Suggest("ESQUEMA DE PRINCIPIO$0$G656-A_ISO_ESQ-PPIO$0$AGUA-CALIENTE") == MepKind.NoImportar &&
          MepKinds.Suggest("Etiquetas de tubería") == MepKind.NoImportar && MepKinds.Suggest("Uniones de tubería") == MepKind.NoImportar &&
          MepKinds.Suggest("Accesorios de conductos") == MepKind.NoImportar && MepKinds.Suggest("-INST-PCI-ROT") == MepKind.NoImportar,
        "Símbolos, esquemas, terminales, etiquetas, uniones, accesorios y rótulos no se modelan como tramos");
    Check(MepKinds.Suggest("CONDENSADOS") == MepKind.Tuberia && MepKinds.Suggest("acc_CLI_TUB_EMP") == MepKind.Tuberia &&
          MepKinds.Suggest("acc_SAN_EMP") == MepKind.Tuberia && MepKinds.Suggest("Tuberías") == MepKind.Tuberia &&
          MepKinds.Suggest("Conductos") == MepKind.Conducto,
        "Condensados y capas con TUB/SAN se modelan como tuberías");
    Check(!MepKinds.IsUnknownLayer("A-WALL") && !MepKinds.IsUnknownLayer("EST_SEC01") && !MepKinds.IsUnknownLayer("VALVULERIA") &&
          MepKinds.IsUnknownLayer("CAPA_RARA_123") && MepKinds.IsUnknownLayer("0"),
        "Solo se pregunta por capas que el programa no sabe qué son (no por arquitectura, estructura o valvulería)");
    // Reglas de la oficina: mandan sobre las palabras clave
    var savedRules = UserRules.Current;
    UserRules.Current = new UserRules();
    UserRules.Current.Layers["CAPA_RARA_123"] = MepKind.Conducto;
    UserRules.Current.Blocks["RL300X100"] = BlockRole.Terminal;
    Check(MepKinds.Suggest("CAPA_RARA_123") == MepKind.Conducto && !MepKinds.IsUnknownLayer("capa_rara_123") &&
          BlockNames.Role("RL300X100", "0") == BlockRole.Terminal,
        "Lo que el usuario dice que es una capa o un bloque se recuerda y manda");
    string rulesFile = Path.Combine(Path.GetTempPath(), "CadMepRevit_reglas_prueba.json");
    Check(UserRules.Current.Save(rulesFile) == "" && UserRules.Load(rulesFile).LayerRule("CAPA_RARA_123") == MepKind.Conducto &&
          UserRules.Load(rulesFile).BlockRule("RL300X100") == BlockRole.Terminal, "Las reglas se guardan y se vuelven a leer");
    UserRules.Current = savedRules;
    Check(LayerPlanning.CenterlineDuplicateOf("E-POWR-CNTR", new[] { "E-POWR-CNDT", "E-POWR-CNTR", "E-POWR-CNDT-IDEN" }) == "E-POWR-CNDT",
        "E-POWR-CNTR se reconoce como ejes de E-POWR-CNDT (no se modela dos veces)");
    var roleCases = new (string Name, string Layer, BlockRole Role)[]
    {
        ("XX_LUM_DownlightEmpotrado - Downlight Tipo 3-1000101-PL01-02-ALUMBRADO-00_PLANTA BAJA", "E-LITE-EQPM", BlockRole.Luminaria),
        ("XX_LUM_TiraLED - Tira LED 1-1000102-PL01-02-ALUMBRADO-00_PLANTA BAJA", "E-LITE-EQPM", BlockRole.Luminaria),
        ("XX_LUM_LuminariaEmergencia - Luminaria de Emergencia 5 H-1000103-PL01", "E-LITE-EQPM", BlockRole.Luminaria),
        ("XX_DIL_Interruptor1 - Interruptor Simple-1000104-PL01", "E-LITE", BlockRole.MandoIluminacion),
        ("Detector de presencia1_0001 - Detector de presencia 1-1000105-PL01", "E-ELEC-EQPM", BlockRole.MandoIluminacion),
        ("XX_EQP_ExtintorySeñal - ExtintorySeñal ABC-V1-PCI_VISTA - 00 - PLANTA SÓTANO", "A-DETL-GENF", BlockRole.Pci),
        ("XX_EQP_BIE EE EC - BIE-1000106-PCI_VISTA - 00 - PLANTA SÓTANO", "A-DETL-GENF", BlockRole.Pci),
        ("XX_DAI_DispositivoVisual - Sirena-1000107-PCI_VISTA - 00 - PLANTA SÓTANO", "E-FIRE", BlockRole.Pci),
        ("MEP_DET_DetectorIncendios_LOD2 - Detector Termovelocimétrico-1000108-PCI_VISTA - 00 - PLANTA SÓTANO", "E-FIRE", BlockRole.Pci),
        ("XX_PCI_DepósitoInercia - XX_PCI_Depósito3000L-V56-PCI_VISTA - 00 - PLANTA SÓTANO", "M-EQPM", BlockRole.Equipo),
        ("CGP - 250A-V146-PL01-14 - FUERZA-PLANTA BAJA", "E-ELEC-EQPM", BlockRole.Electrico),
        ("BORNERO SALIDA - BORNERO SALIDA-1000109-PL01-14 - FUERZA-PLANTA BAJA", "E-ELEC-EQPM", BlockRole.Electrico),
        ("XX_ACC_CompuertaCortafuegosRectangular - CC1_250x200x300-V49-VEN - -1_PLANTA SOTANO", "M-HVAC-DUCT", BlockRole.Accesorio),
        ("Family - ___-1000110-PL01 - LEYENDA ELECTRICIDAD", "E-LITE-EQPM", BlockRole.Otro),
        ("SimboloDePuertasconRejilla - flecha de puerta de ext-1000111-VEN", "A-ANNO-NOTE", BlockRole.Otro),
        ("M_Codo - Soldado - Genérico1 - PCI-1000112-ROC-B1-150", "_acc_ROC", BlockRole.PiezaDeUnion),
        ("M_Transición - Soldada - Genérica - ACERO PCI-V120-PCI-B3-150", "Uniones de tubería", BlockRole.PiezaDeUnion),
        ("M_Toma de derivación rectangular", "M-HVAC-DUCT", BlockRole.PiezaDeUnion),
        ("TOMA DE CORRIENTE DOBLE DE 16A", "TEXTOS", BlockRole.Electrico),
        ("Flecha-Admision-Doble", "-INST-PCI-VENT-NAT", BlockRole.Otro),
        ("b000_PK_000_Señalización_Flecha - b000_Flecha-100113-PG_S01_", "Aparcamiento", BlockRole.Otro),
    };
    foreach (var (n, l, r) in roleCases)
        Check(BlockNames.Role(n, l) == r, $"Función de '{BlockNames.Clean(n)}' (capa {l}) = {BlockNames.Label(r)} (leída: {BlockNames.Label(BlockNames.Role(n, l))})");
    Check(BlockNames.Clean("XX_LUM_TiraLED - Tira LED 1-1000102-PL01-02-ALUMBRADO-00_PLANTA BAJA") == "XX_LUM_TiraLED - Tira LED 1",
        "Nombre de Revit con guiones en la vista: se corta en el identificador");
    Check(ModelScopes.Includes(ModelScope.Bandejas, MepKind.Bandeja) && ModelScopes.Includes(ModelScope.Bandejas, MepKind.TuboElectrico) &&
          !ModelScopes.Includes(ModelScope.Tuberias, MepKind.Conducto) && !ModelScopes.Includes(ModelScope.Luminarias, MepKind.TuboElectrico),
        "Cada botón modela solo sus capas");
    Check(ModelScopes.Includes(ModelScope.Conductos, BlockRole.Terminal, "R5_825x225") && ModelScopes.Includes(ModelScope.Conductos, BlockRole.Accesorio, "CompuertaCortafuegos") &&
          !ModelScopes.Includes(ModelScope.Tuberias, BlockRole.Accesorio, "CompuertaCortafuegos") && ModelScopes.Includes(ModelScope.Tuberias, BlockRole.Accesorio, "Valvula") &&
          ModelScopes.Includes(ModelScope.Tuberias, BlockRole.Equipo, "BombaKivu") && !ModelScopes.Includes(ModelScope.EquiposPci, BlockRole.Equipo, "BombaKivu") &&
          ModelScopes.Includes(ModelScope.Conductos, BlockRole.Equipo, "VentiladorExtractor") && !ModelScopes.Includes(ModelScope.Tuberias, BlockRole.Equipo, "VentiladorExtractor") &&
          !ModelScopes.Includes(ModelScope.Tuberias, BlockRole.Equipo, "Fancoil") && ModelScopes.Includes(ModelScope.Conductos, BlockRole.Equipo, "Fancoil") &&
          ModelScopes.Includes(ModelScope.Luminarias, BlockRole.MandoIluminacion, "Interruptor") && ModelScopes.Includes(ModelScope.EquiposPci, BlockRole.Pci, "Extintor") &&
          !ModelScopes.Includes(ModelScope.EquiposPci, BlockRole.Terminal, "R5_825x225"),
        "Bloques con su botón: aire con Conductos, válvulas y bombas con Tuberías, protección y cuadros con Equipos y PCI");
    Check(BlockElevations.For(BlockRole.Pci, "XX_EQP_ExtintorySeñal - ABC") == (1000, true) && BlockElevations.For(BlockRole.Luminaria, "Downlight") == (2700, true) &&
          BlockElevations.For(BlockRole.Terminal, "Rejilla") == (PlanDefaults.HeightAboveLevelMm, false) &&
          BlockElevations.IsCeilingMounted(BlockRole.Pci, "MEP_DET_DetectorIncendios") && !BlockElevations.IsCeilingMounted(BlockRole.Pci, "Extintor"),
        "Alturas por función: extintor 1,00 m y luminaria 2,70 m (supuestas); rejillas a la altura de las redes; detector en el techo");

    string badDwf = Path.Combine(Path.GetTempPath(), "CadMepRevit_roto.dwf");
    File.WriteAllText(badDwf, "esto no es un dwf");
    var bad1 = DwfReader.Read(badDwf);
    Check(bad1.Texts.Count == 0 && bad1.Notes.Count == 1, "DWF dañado: no se detiene, avisa y sigue con el DWG");
}

SlopeTests.Run(Check, Path.Combine(samplesDir, "pendientes"));
PipeFeatureTests.Run(Check, Path.Combine(samplesDir, "bajantes"));
LuminaireTests.Run(Check, Path.Combine(samplesDir, "luminarias"));
PlacementTests.Run(Check);
TrayTests.Run(Check);
NetworkFittingTests.Run(Check);
PipeOutlineTests.Run(Check);
CeilingTests.Run(Check);
string? slopePlans = Environment.GetEnvironmentVariable("CADMEP_PRUEBAS") ?? Environment.GetEnvironmentVariable("CADMEP_PLANOS");
if (!string.IsNullOrWhiteSpace(slopePlans) && Directory.Exists(slopePlans))
{
    SlopeTests.RunReal(Check, slopePlans, Path.Combine(samplesDir, "resultados_pendientes"));
    PlacementTests.RunReal(Check, slopePlans);
}
Console.WriteLine("3) Rendimiento: 3000 tramos");
var big = new List<CadSegment>();
for (int i = 0; i < 3000; i++)
    big.Add(new CadSegment { Layer = "AF", Start = new P3(i * 1000, 0, 0), End = new P3(i * 1000, 800, 0) });
var sw = System.Diagnostics.Stopwatch.StartNew();
var bigNet = NetworkBuilder.Build(big, new NetworkBuilder.Settings());
sw.Stop();
Check(bigNet.Segments.Count == 3000 && sw.Elapsed.TotalSeconds < 20, $"3000 tramos procesados en {sw.Elapsed.TotalSeconds:0.0} s");

Console.WriteLine("4) Errores claros");
try { CadReader.Read(Path.Combine(samplesDir, "no_existe.dwg")); Check(false, "Debe fallar con archivo inexistente"); }
catch (CadReadException ex) { Check(ex.HowToFix.Length > 0, "Archivo inexistente: '" + ex.Message + "'"); }
// El archivo dañado va a la carpeta temporal, nunca a la de ejemplos que se instala.
string bad = Path.Combine(Path.GetTempPath(), "CadMepRevit_roto.dwg");
File.WriteAllText(bad, "esto no es un dwg");
try { CadReader.Read(bad); Check(false, "Debe fallar con DWG dañado"); }
catch (CadReadException ex) { Check(ex.HowToFix.Length > 0, "DWG dañado: '" + ex.Message + "'"); }

Console.WriteLine();
Console.WriteLine(failures == 0 ? "RESULTADO: todas las pruebas superadas (fuera de Revit)." : "RESULTADO: " + failures + " pruebas fallidas.");
return failures == 0 ? 0 : 1;

/// <summary>Plano de ejemplo en milímetros con capas MEP típicas.</summary>
static class SampleDrawing
{
    public static CadDocument Create()
    {
        var doc = new CadDocument();
        doc.Header.InsUnits = ACadSharp.Types.Units.UnitsType.Millimeters;
        // Fechas fijas: así el plano de ejemplo sale idéntico en cada ejecución y no aparece como
        // modificado en el control de versiones cada vez que se pasan las pruebas.
        var fixedDate = new DateTime(2026, 1, 1, 12, 0, 0);
        doc.Header.CreateDateTime = fixedDate;
        doc.Header.UpdateDateTime = fixedDate;
        doc.Header.UniversalCreateDateTime = fixedDate;
        doc.Header.UniversalUpdateDateTime = fixedDate;
        doc.SummaryInfo.CreatedDate = fixedDate;
        doc.SummaryInfo.ModifiedDate = fixedDate;
        Layer L(string name, short color)
        {
            var layer = new Layer(name) { Color = new Color(color) };
            doc.Layers.Add(layer);
            return layer;
        }
        var af = L("M-TUBERIA-AF", 5);
        var acs = L("M-TUBERIA-ACS", 1);
        var duct = L("M-CONDUCTO-IMP", 3);
        var tray = L("E-BANDEJA", 30);
        var valves = L("M-VALVULAS", 2);
        var notes = L("COTAS", 7);

        void Line(Layer layer, double x1, double y1, double x2, double y2) =>
            doc.Entities.Add(new Line(new XYZ(x1, y1, 0), new XYZ(x2, y2, 0)) { Layer = layer });

        // AF: polilínea en L (0,0)->(6000,0)->(6000,4000) => 1 codo
        var poly = new LwPolyline { Layer = af };
        poly.Vertices.Add(new LwPolyline.Vertex(new XY(0, 0)));
        poly.Vertices.Add(new LwPolyline.Vertex(new XY(6000, 0)));
        poly.Vertices.Add(new LwPolyline.Vertex(new XY(6000, 4000)));
        doc.Entities.Add(poly);
        // AF: ramal que termina en mitad del tramo horizontal (3000,0) => te
        //     dibujado en dos líneas colineales (3000,3000)->(3000,1500)->(3000,0) => se unen
        Line(af, 3000, 3000, 3000, 1500);
        Line(af, 3000, 1500, 3000, 0);
        // AF: tramo de 20 mm (se descarta por corto)
        Line(af, 10000, 0, 10020, 0);

        // ACS: cruz en (2000, 8000)
        Line(acs, 0, 8000, 2000, 8000);
        Line(acs, 2000, 8000, 4000, 8000);
        Line(acs, 2000, 6000, 2000, 8000);
        Line(acs, 2000, 8000, 2000, 10000);

        // Conducto de impulsión con un codo
        Line(duct, 0, 14000, 8000, 14000);
        Line(duct, 8000, 14000, 8000, 18000);

        // Bandeja recta
        Line(tray, 0, 20000, 10000, 20000);

        // Conducto de retorno dibujado con su CONTORNO (doble línea), como lo exporta Revit:
        // cada tramo es un rectángulo y las piezas especiales dejan huecos.
        var ret = L("M-CONDUCTO-RET", 4);
        void Rect(double x1, double y1, double x2, double y2)
        {
            Line(ret, x1, y1, x2, y1); Line(ret, x2, y1, x2, y2);
            Line(ret, x2, y2, x1, y2); Line(ret, x1, y2, x1, y1);
        }
        Rect(-700, 23700, -350, 24300);  // rectángulo corto (350 de largo, 600 de ancho): ambiguo
        Rect(0, 23700, 2000, 24300);     // 600 de ancho
        var closed = new LwPolyline { Layer = ret, IsClosed = true };   // mismo tipo de rectángulo, como polilínea cerrada
        foreach (var (x, y) in new[] { (2600.0, 23700.0), (5000.0, 23700.0), (5000.0, 24300.0), (2600.0, 24300.0) })
            closed.Vertices.Add(new LwPolyline.Vertex(new XY(x, y)));
        doc.Entities.Add(closed);
        Rect(2100, 24500, 2500, 26000);  // ramal de 400 → te en (2300, 24000)
        Rect(5150, 24450, 5750, 26000);  // sube tras el codo en (5450, 24000)
        Rect(5250, 26300, 5650, 28000);  // 400 de ancho → transición 600/400

        // Bloque "VALVULA" insertado dos veces sobre AF
        var block = new BlockRecord("VALVULA");
        block.Entities.Add(new Line(new XYZ(-100, -100, 0), new XYZ(100, 100, 0)));
        block.Entities.Add(new Line(new XYZ(-100, 100, 0), new XYZ(100, -100, 0)));
        doc.BlockRecords.Add(block);
        doc.Entities.Add(new Insert(block) { InsertPoint = new XYZ(1500, 0, 0), Layer = valves });
        doc.Entities.Add(new Insert(block) { InsertPoint = new XYZ(6000, 2000, 0), Layer = valves, Rotation = Math.PI / 2 });

        // Texto (debe ignorarse)
        doc.Entities.Add(new TextEntity { Value = "AF DN25", InsertPoint = new XYZ(500, 200, 0), Height = 150, Layer = notes });
        return doc;
    }
}
