using System.Globalization;
using System.Text;
using System.Text.Json;
using RevitAutomation.Addin.Commands.Patinillo;

namespace PatinilloHarness;

internal static class Program
{
    private static int _failures;
    private static double Outer(double nominal) => nominal switch { 20 => 25, 25 => 32, 32 => 40, _ => nominal * 1.25 };
    private static PatinilloLevel[] Levels() => new[]
    {
        new PatinilloLevel("PB", 0), new PatinilloLevel("P1", 3000), new PatinilloLevel("P2", 6000),
        new PatinilloLevel("P3", 9000), new PatinilloLevel("P4", 12000), new PatinilloLevel("Cubierta", 15000)
    };
    private static PatinilloPlan Plan(PatinilloSettings? s = null, double width = 600, double depth = 400,
        IReadOnlyList<PatinilloLevel>? levels = null) =>
        PatinilloPlanner.Plan(s ?? new PatinilloSettings(), width, depth, levels ?? Levels(), Outer);

    private static void Check(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
    private static void Near(double actual, double expected, string message) =>
        Check(Math.Abs(actual - expected) < 1e-6, $"{message}: {actual} != {expected}");

    private static void Test(string name, Action action)
    {
        try { action(); Console.WriteLine($"PASS {name}"); }
        catch (Exception ex) { _failures++; Console.WriteLine($"FAIL {name}: {ex.Message}"); }
    }

    private static void Reject(PatinilloPlan p, string message) =>
        Check(!p.Fits && p.Placements.Count == 0 && !string.IsNullOrWhiteSpace(p.Message), message);

    private static void Order(PatinilloPlan p)
    {
        foreach (var column in p.Placements.GroupBy(x => x.Column))
        {
            var frontToBack = column.OrderBy(x => x.Row).ToArray();
            for (int i = 1; i < frontToBack.Length; i++)
                Check(frontToBack[i - 1].Pipe.FloorIndex <= frontToBack[i].Pipe.FloorIndex, "Orden de plantas en columna");
        }
        var floors = p.Placements.GroupBy(x => x.Pipe.FloorIndex).OrderBy(g => g.Key).ToArray();
        for (int i = 1; i < floors.Length; i++)
            Check(floors[i - 1].Max(x => x.Row) <= floors[i].Min(x => x.Row), "Orden de filas entre plantas");
    }

    public static int Main()
    {
        Test("Edificio predeterminado y posiciones", () =>
        {
            var p = Plan();
            Check(p.Fits, p.Message);
            Check(p.Pipes.Count == 16 && p.Placements.Count == 16, "16 tuberías");
            Near(p.WidthMm, 600, "Ancho"); Near(p.DepthMm, 400, "Fondo"); Near(p.PitchMm, 82, "Paso");
            Check(p.Columns == 7 && p.Rows == 4, "Matriz 7 × 4");
            Check(p.Pipes[0].Id == "P1-V1-AF" && p.Pipes[1].Id == "P1-V1-ACS" &&
                p.Pipes[2].Id == "P1-V2-AF", "Orden vivienda y servicio");
            Check(p.Pipes.All(x => x.FloorName != "PB" && x.FloorName != "Cubierta"), "PB y cubierta sin viviendas");
            Check(p.Notes.Count == 2 && p.Levels.Count == 6, "Niveles y notas");
            foreach (var placement in p.Placements.Where(x => x.Pipe.FloorName == "P1"))
                Near(placement.ExitElevationMm, 5400, "Salida P1");
            Near(p.Placements[0].XMm, 54, "X centrada");
            Near(p.Placements[0].YMm, 77, "Y centrada");
            Near(p.Placements[0].StartElevationMm, 500, "Arranque");
            Near(p.Placements[0].EndYMm, -500, "Fin de salida");
            Near(p.MinWidthMm, 600, "Ancho mínimo");
            Near(p.MinDepthMm, 250, "Fondo mínimo");
            Check(Plan(width: p.MinWidthMm, depth: p.MinDepthMm).Fits, "Mínimos del plan válido");
            Order(p);
        });

        Test("Escalones y choque con forjado", () =>
        {
            var p = Plan(width: 246, depth: 500);
            Check(p.Fits, p.Message);
            var behind = p.Placements.Single(x => x.Pipe.Id == "P1-V2-ACS");
            Check(behind.Tier == 1 && behind.Row == 1 && behind.Column == 0, "Escalón de la misma planta");
            Near(behind.ExitElevationMm, 5482, "Salida escalonada");
            var front = p.Placements.Single(x => x.Row == 0 && x.Column == 0);
            Near(behind.ExitElevationMm - front.ExitElevationMm, p.PitchMm, "Diferencia de cota");
            Order(p);
            var s = new PatinilloSettings { OutletBelowSlabMm = 90 };
            var blocked = Plan(s, 246, 500);
            Reject(blocked, "Escalón bajo el forjado");
            Check(blocked.Message.Contains("forjado") && blocked.Message.Contains("1"), "Causa y escalón");
            Reject(Plan(new PatinilloSettings { OutletBelowSlabMm = 5200 }), "Salida base bajo arranque");
        });

        Test("Capacidad y dimensiones mínimas", () =>
        {
            var small = Plan(width: 300, depth: 200);
            Reject(small, "Patinillo pequeño");
            Near(small.MinWidthMm, 300, "Ancho mínimo"); Near(small.MinDepthMm, 500, "Fondo mínimo");
            Check(small.Pipes.Count == 16 && small.Columns == 3 && small.Rows == 2, "Datos del plan rechazado");
            Check(Plan(width: small.MinWidthMm, depth: small.MinDepthMm).Fits, "Replanificar con mínimos");
            var s = new PatinilloSettings { DwellingsPerFloor = 6, HotReturn = true };
            var crowded = Plan(s);
            Reject(crowded, "Seis viviendas con retorno");
            Near(crowded.MinWidthMm, 600, "Ancho mínimo retorno"); Near(crowded.MinDepthMm, 910, "Fondo mínimo retorno");
            Check(Plan(s, crowded.MinWidthMm, crowded.MinDepthMm).Fits, "Mínimos con retorno");
            var zeroColumns = Plan(width: 40, depth: 2000);
            Reject(zeroColumns, "Cero columnas");
            Near(zeroColumns.MinWidthMm, 90, "Redondeo de ancho");
            Near(zeroColumns.MinDepthMm, 1320, "Redondeo de fondo");
            Check(Plan(width: zeroColumns.MinWidthMm, depth: zeroColumns.MinDepthMm).Fits, "Mínimos de cero columnas");
            Reject(Plan(width: 600, depth: 40), "Cero filas");
            var exact = Plan(width: 574 - 0.0000005, depth: 328 - 0.0000005);
            Check(exact.Fits && exact.Columns == 7 && exact.Rows == 4, "Tolerancia de 1e-6 mm");
        });

        Test("Servicios, PB, nombres y niveles desordenados", () =>
        {
            var s = new PatinilloSettings { HotReturn = true };
            var p = Plan(s);
            Check(p.Fits && p.Pipes.Count == 24, p.Message);
            Check(p.Pipes.Take(3).Select(x => x.Service).SequenceEqual(new[]
                { PatinilloService.AguaFria, PatinilloService.Acs, PatinilloService.RetornoAcs }), "Tres servicios en orden");
            Check(p.Pipes[2].Id == "P1-V1-RET", "Id de retorno");
            s.GroundFloorHasDwellings = true;
            var ground = Plan(s, depth: 500);
            Check(ground.Fits && ground.Pipes.Count == 30 && ground.Pipes[0].Id == "PB-V1-AF", "Viviendas en PB");
            var reversed = Levels().Reverse().ToArray();
            var copy = reversed.ToArray();
            var sorted = Plan(levels: reversed);
            Check(sorted.Fits && sorted.Pipes.SequenceEqual(Plan().Pipes), "Entrada desordenada");
            Check(reversed.SequenceEqual(copy), "No modificar niveles originales");
            Check(PatinilloPlanner.BuildPipes(new PatinilloSettings(), reversed, Outer).SequenceEqual(Plan().Pipes), "BuildPipes ordena");
            var named = PatinilloPlanner.BuildPipes(new PatinilloSettings(), new[]
                { new PatinilloLevel("PB", 0), new PatinilloLevel("Planta 1 / á", 3000), new PatinilloLevel("Cubierta", 6000) }, Outer);
            Check(named[0].Id == "Planta1á-V1-AF", "Nombre saneado con letras Unicode");
            Reject(Plan(levels: new[] { new PatinilloLevel("PB", 0) }), "Un nivel");
            Reject(Plan(levels: Array.Empty<PatinilloLevel>()), "Sin niveles");
            Reject(Plan(levels: Levels().Take(2).ToArray()), "Sin plantas servidas");
        });

        Test("Validación de cada límite", () =>
        {
            Check(PatinilloPlanner.Validate(new PatinilloSettings()).Count == 0, "Valores predeterminados válidos");
            Action<PatinilloSettings>[] invalid =
            {
                s => s.DwellingsPerFloor = 0, s => s.ColdDiameterMm = 0, s => s.HotDiameterMm = -1,
                s => s.ReturnDiameterMm = 0, s => s.ShaftWidthMm = 0, s => s.ShaftDepthMm = -1,
                s => s.GapMm = -1, s => s.OutletLengthMm = 0, s => s.SlabThicknessMm = -1,
                s => s.OutletBelowSlabMm = -1, s => s.StartAboveFloorMm = -1,
                s => s.TestFloorsAboveGround = 0, s => s.TestFloorsAboveGround = 61,
                s => s.TestFloorHeightMm = s.SlabThicknessMm, s => s.GapMm = double.NaN,
                s => s.ColdDiameterMm = double.PositiveInfinity
            };
            foreach (var mutate in invalid)
            {
                var s = new PatinilloSettings(); mutate(s);
                Check(PatinilloPlanner.Validate(s).Count > 0, "Detectar valor incorrecto");
                Reject(Plan(s), "Validación bloquea planificación");
            }
            var boundary = new PatinilloSettings
            {
                GapMm = 0, SlabThicknessMm = 0, OutletBelowSlabMm = 0,
                StartAboveFloorMm = 0, TestFloorsAboveGround = 60
            };
            Check(PatinilloPlanner.Validate(boundary).Count == 0, "Límites inclusivos válidos");
            Reject(Plan(width: -1), "Ancho real incorrecto");
            Reject(Plan(depth: double.NaN), "Fondo real no finito");
            Reject(PatinilloPlanner.Plan(new PatinilloSettings(), 600, 400, Levels(), _ => 0), "Diámetro exterior incorrecto");
        });

        Test("Persistencia JSON y archivos CSV con BOM", () =>
        {
            string folder = Path.Combine(Path.GetTempPath(), "PatinilloHarness_" + Guid.NewGuid().ToString("N"));
            try
            {
                string config = Path.Combine(folder, "nested", "settings.json");
                var s = new PatinilloSettings
                {
                    DwellingsPerFloor = 3, HotReturn = true, GroundFloorHasDwellings = true,
                    End = PatinilloEnd.Abierto, PipeTypeName = "Tubería de prueba", GapMm = 62.5
                };
                Check(PatinilloSettingsStore.Save(s, config), "Guardar ajustes y crear carpeta");
                var loaded = PatinilloSettingsStore.Load(config);
                Check(JsonSerializer.Serialize(s) == JsonSerializer.Serialize(loaded), "Round-trip de todos los ajustes");
                Check(File.ReadAllText(config).Contains("\"Abierto\""), "Enum como texto");
                File.WriteAllText(config, "{corrupto");
                Check(JsonSerializer.Serialize(PatinilloSettingsStore.Load(config)) ==
                    JsonSerializer.Serialize(new PatinilloSettings()), "Archivo corrupto devuelve valores predeterminados");
                Check(PatinilloSettingsStore.Load(Path.Combine(folder, "missing.json")).GapMm == 50, "Archivo inexistente");
                Check(!PatinilloSettingsStore.Save(s, folder), "Error de escritura devuelve false");
                var paths = PatinilloReport.Write(Plan(), Array.Empty<PatinilloDeviation>(), folder,
                    new DateTime(2026, 11, 18, 9, 8, 7));
                Check(Path.GetFileName(paths.ReportPath) == "informe_patinillo_20261118_090807.csv", "Nombre informe");
                Check(Path.GetFileName(paths.DeviationsPath) == "desviaciones_patinillo_20261118_090807.csv", "Nombre desviaciones");
                foreach (string file in new[] { paths.ReportPath, paths.DeviationsPath })
                    Check(File.ReadAllBytes(file).Take(3).SequenceEqual(new byte[] { 0xEF, 0xBB, 0xBF }), "BOM UTF-8");
            }
            finally { if (Directory.Exists(folder)) Directory.Delete(folder, true); }
        });

        Test("Geometría girada, boca y esquinas", () =>
        {
            double angle = 0.47, uX = Math.Cos(angle), uY = Math.Sin(angle);
            (double X, double Y) World(double x, double y) => (2 + uX * x - uY * y, 3 + uY * x + uX * y);
            var points = new[] { World(0, 0), World(0.6, 0), World(0.6, 0.4), World(0, 0.4), World(0, 0) };
            var r = PatinilloGeometry.FromOutline(points);
            Near(r.Width, 0.6, "Ancho girado"); Near(r.Depth, 0.4, "Fondo girado");
            void Corners(PatinilloRect rect)
            {
                foreach (var corner in new[] { rect.ToWorld(0, 0), rect.ToWorld(rect.Width, 0),
                    rect.ToWorld(rect.Width, rect.Depth), rect.ToWorld(0, rect.Depth) })
                    Check(points.Any(p => Math.Abs(p.X - corner.X) < 1e-6 && Math.Abs(p.Y - corner.Y) < 1e-6), "Esquinas del contorno");
                Near(rect.UX * rect.VY - rect.UY * rect.VX, 1, "Orientación");
            }
            Corners(r);
            foreach (double y in new[] { -0.2, r.Depth + 0.2 })
            {
                var query = r.ToWorld(r.Width / 2, y);
                var mouth = PatinilloGeometry.WithMouthNear(r, query.X, query.Y);
                var expected = r.ToWorld(r.Width / 2, y < 0 ? 0 : r.Depth);
                var actual = mouth.ToWorld(mouth.Width / 2, 0);
                Near(actual.X, expected.X, "Boca X"); Near(actual.Y, expected.Y, "Boca Y");
                Corners(mouth);
            }
            var reversed = PatinilloGeometry.FromOutline(points.Reverse().ToArray());
            Corners(reversed);
            var center = PatinilloGeometry.FromCenter(10, 20, 6, 4);
            Near(center.Width, 6, "Centro ancho"); Near(center.Depth, 4, "Centro fondo");
            Near(center.OriginX, 7, "Centro origen X"); Near(center.OriginY, 18, "Centro origen Y");
            var corners = PatinilloGeometry.FromCorners(6, 4, 0, 0);
            Near(corners.Width, 6, "Esquinas ancho"); Near(corners.Depth, 4, "Esquinas fondo");
            var tall = PatinilloGeometry.FromCorners(0, 0, 4, 6);
            Near(tall.Width, 6, "Vertical ancho"); Near(tall.Depth, 4, "Vertical fondo");
            Check(new[] { tall.ToWorld(0, 0), tall.ToWorld(6, 0), tall.ToWorld(6, 4), tall.ToWorld(0, 4) }
                .All(p => (p.X == 0 || p.X == 4) && (p.Y == 0 || p.Y == 6)), "Conservar rectángulo vertical");
            foreach (var bad in new[] { new[] { (0.0, 0.0), (1.0, 1.0) },
                new[] { (0.0, 0.0), (1.0, 1.0), (2.0, 2.0) } })
            {
                bool thrown = false;
                try { PatinilloGeometry.FromOutline(bad); } catch (ArgumentException) { thrown = true; }
                Check(thrown, "Rechazar contorno degenerado");
            }
        });

        Test("CSV, cultura, escape y esquema ASCII", () =>
        {
            var previous = CultureInfo.CurrentCulture;
            try
            {
                CultureInfo.CurrentCulture = CultureInfo.InvariantCulture;
                string csv = PatinilloReport.BuildReportCsv(Plan());
                var lines = csv.Split(new[] { "\r\n", "\n" }, StringSplitOptions.RemoveEmptyEntries);
                Check(lines.Length == 17, "Cabecera y 16 datos");
                Check(lines[0] == "Tubería;Sistema;Diámetro nominal (mm);Diámetro exterior (mm);Planta de salida;Vivienda;Fila;Columna;X (mm);Y (mm);Cota de salida (mm);Escalón", "Cabecera exacta");
                Check(csv.Contains("Agua fría") && csv.Contains("Agua caliente (ACS)"), "Etiquetas de servicio");
                var pipe = new PatinilloPipe("ID;\"x\"", 1, "Planta;\n\"Uno\"", 1, PatinilloService.RetornoAcs, 20.5, 25.5);
                var special = new PatinilloPlan
                {
                    Placements = new[] { new PatinilloPlacement(pipe, 0, 0, 12.5, 13.5, 0, 500, 5400.5, -500) }
                };
                string escaped = PatinilloReport.BuildReportCsv(special);
                Check(escaped.Contains("\"ID;\"\"x\"\"\"") && escaped.Contains("\"Planta;\n\"\"Uno\"\"\""), "Escape de CSV");
                Check(escaped.Contains(";20,5;25,5;") && escaped.Contains("Retorno ACS"), "Cultura española sin miles");
                string deviations = PatinilloReport.BuildDeviationsCsv(new[]
                    { new PatinilloDeviation("A;B", "Qué \"ocurrió\"", "Línea\nnueva", "Corregir") });
                Check(deviations.StartsWith("Tubería;Qué;Motivo;Cómo corregirlo"), "Cabecera desviaciones");
                Check(deviations.Contains("\"A;B\";\"Qué \"\"ocurrió\"\"\";\"Línea\nnueva\";Corregir"), "Escape de desviaciones");
                string ascii = PatinilloReport.Ascii(Plan());
                Check(ascii.TrimEnd().EndsWith("BOCA ↓") && ascii.Contains("·"), "Boca y celdas vacías");
                Check(ascii.IndexOf("P4-V2-ACS", StringComparison.Ordinal) < ascii.IndexOf("P1-V1-AF", StringComparison.Ordinal), "Filas de atrás hacia delante");
                Console.WriteLine(ascii);
            }
            finally { CultureInfo.CurrentCulture = previous; }
        });

        Console.WriteLine(_failures == 0 ? "PASS: todas las comprobaciones." : $"FAIL: {_failures} grupos.");
        return _failures == 0 ? 0 : 1;
    }
}
