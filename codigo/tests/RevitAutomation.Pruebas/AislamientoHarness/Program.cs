using System.Globalization;
using System.Text;
using RevitAutomation.Addin.Commands.Aislamiento;

namespace AislamientoHarness;

internal static class Program
{
    private static int _failures;
    private static int _checks;

    private static void Check(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
    private static void Near(double actual, double expected, string message, double tol = 1e-6) =>
        Check(Math.Abs(actual - expected) < tol, $"{message}: {actual} != {expected}");

    private static void Test(string name, Action action)
    {
        _checks++;
        try { action(); Console.WriteLine($"PASS {name}"); }
        catch (Exception ex) { _failures++; Console.WriteLine($"FAIL {name}: {ex.Message}"); }
    }

    // Tablas transcritas de nuevo aquí (independientes del código) desde el texto consolidado del BOE.
    // Filas: D ≤ 35 | 35 < D ≤ 60 | 60 < D ≤ 90 | 90 < D ≤ 140 | 140 < D.
    private static readonly double[][] T1 = { new double[] { 25, 25, 30 }, new double[] { 30, 30, 40 }, new double[] { 30, 30, 40 }, new double[] { 30, 40, 50 }, new double[] { 35, 40, 50 } };
    private static readonly double[][] T2 = { new double[] { 35, 35, 40 }, new double[] { 40, 40, 50 }, new double[] { 40, 40, 50 }, new double[] { 40, 50, 60 }, new double[] { 45, 50, 60 } };
    private static readonly double[][] T3 = { new double[] { 30, 25, 20 }, new double[] { 40, 30, 20 }, new double[] { 40, 30, 30 }, new double[] { 50, 40, 30 }, new double[] { 50, 40, 30 } };
    private static readonly double[][] T4 = { new double[] { 50, 45, 40 }, new double[] { 60, 50, 40 }, new double[] { 60, 50, 50 }, new double[] { 70, 60, 50 }, new double[] { 70, 60, 50 } };
    // Tabla 1.2.4.2 (ACS) interior / exterior, por fila de diámetro, para 40…60 °C.
    private static readonly double[] AcsInt = { 30, 35, 35, 45, 45 };
    private static readonly double[] AcsExt = { 40, 45, 45, 55, 55 };

    // Diámetros de prueba → fila esperada (incluye los límites).
    private static readonly (double D, int Row)[] Diameters =
    {
        (15, 0), (35, 0), (35.01, 1), (50, 1), (60, 1), (60.01, 2), (90, 2), (90.01, 3), (140, 3), (140.01, 4), (200, 4)
    };
    private static readonly (double T, int Col)[] HotTemps =
        { (40, 0), (50, 0), (60, 0), (60.01, 1), (80, 1), (100, 1), (100.01, 2), (180, 2) };
    private static readonly (double T, int Col)[] ColdTemps =
        { (-9.99, 0), (-5, 0), (0, 0), (0.01, 1), (7, 1), (10, 1), (10.01, 2), (20, 2) };

    private static AislSystemInfo Sys(string name, string cls, string fluid = "") => new(name, cls, fluid, null);

    public static int Main()
    {
        CultureInfo.CurrentCulture = CultureInfo.InvariantCulture;

        // ---------- Tablas 1.2.4.2.1 a 1.2.4.2.4: todas las celdas, con límites de diámetro y de temperatura ----------
        foreach (bool ext in new[] { false, true })
        {
            var hot = ext ? T2 : T1;
            var cold = ext ? T4 : T3;
            string place = ext ? "exterior" : "interior";
            foreach (var (d, row) in Diameters)
            {
                foreach (var (t, col) in HotTemps)
                    Test($"Caliente {place} D {d} T {t}", () =>
                    {
                        double v = AislRite.TableMm(AislRed.ClimaCaliente, d, t, ext, out string name, out bool oor);
                        Near(v, hot[row][col], "Espesor");
                        Check(!oor, "No debe estar fuera de tabla");
                        Check(name == (ext ? "Tabla 1.2.4.2.2 (fluidos calientes, exterior)" : "Tabla 1.2.4.2.1 (fluidos calientes, interior)"), name);
                    });
                foreach (var (t, col) in ColdTemps)
                    Test($"Frío {place} D {d} T {t}", () =>
                    {
                        double v = AislRite.TableMm(AislRed.ClimaFrio, d, t, ext, out string name, out bool oor);
                        Near(v, cold[row][col], "Espesor");
                        Check(!oor, "No debe estar fuera de tabla");
                        Check(name == (ext ? "Tabla 1.2.4.2.4 (fluidos fríos, exterior)" : "Tabla 1.2.4.2.3 (fluidos fríos, interior)"), name);
                    });
                // ACS = Tabla 1.2.4.2 a 60 °C, y en general caliente + 5 mm.
                Test($"ACS {place} D {d} 60 °C = Tabla 1.2.4.2", () =>
                {
                    double v = AislRite.TableMm(AislRed.Acs, d, 60, ext, out string name, out _);
                    Near(v, (ext ? AcsExt : AcsInt)[row], "ACS");
                    Check(v >= hot[row][0] + 5, "ACS ≥ caliente + 5");
                    Check(name == $"Tabla 1.2.4.2 (ACS, {place})", name);
                });
                Test($"ACS {place} D {d} 70 °C = caliente + 5", () =>
                    Near(AislRite.TableMm(AislRed.Acs, d, 70, ext, out _, out _),
                    Math.Max(hot[row][1] + 5, (ext ? AcsExt : AcsInt)[row]), "ACS 70 °C"));
                Test($"Fuera de tabla {place} D {d}", () =>
                {
                    Near(AislRite.TableMm(AislRed.ClimaCaliente, d, 39.9, ext, out _, out bool o1), 0, "39,9 °C no exigido");
                    Check(!o1, "39,9 no es fuera de tabla");
                    Near(AislRite.TableMm(AislRed.ClimaCaliente, d, 200, ext, out _, out bool o2), hot[row][2], "200 °C → última columna");
                    Check(o2, "200 °C fuera de tabla");
                    Near(AislRite.TableMm(AislRed.ClimaFrio, d, -10, ext, out _, out bool o3), cold[row][0], "−10 °C → primera columna");
                    Check(o3, "−10 °C fuera de tabla");
                    Near(AislRite.TableMm(AislRed.ClimaFrio, d, -25, ext, out _, out bool o4), cold[row][0], "−25 °C");
                    Check(o4, "−25 °C fuera de tabla");
                });
            }
        }

        // ---------- Tabla 1.2.4.2.5 (frigoríficos), límites 26/35/90 a la fila superior ----------
        var refr = new (double D, double Int, double Ext)[]
        {
            (6.35, 10, 15), (13, 10, 15), (13.01, 15, 20), (25.99, 15, 20), (26, 20, 25), (28, 20, 25), (34.99, 20, 25),
            (35, 30, 40), (54, 30, 40), (89.99, 30, 40), (90, 40, 50), (120, 40, 50)
        };
        foreach (var (d, i, e) in refr)
            Test($"Frigorífica D {d}", () =>
            {
                Near(AislRite.TableMm(AislRed.Frigorifica, d, 0, false, out string n1, out bool o1), i, "Interior");
                Near(AislRite.TableMm(AislRed.Frigorifica, d, -30, true, out string n2, out bool o2), e, "Exterior");
                Check(!o1 && !o2, "Sin fuera de tabla");
                Check(n1 == "Tabla 1.2.4.2.5 (circuitos frigoríficos, interior)" && n2.EndsWith("exterior)"), n1 + " / " + n2);
            });
        Test("Otras redes sin tabla", () =>
        {
            foreach (var red in new[] { AislRed.AguaFria, AislRed.SinClasificar, AislRed.Excluida, AislRed.ClimaMixto })
                Near(AislRite.TableMm(red, 28, 60, false, out _, out _), 0, red.ToString());
        });

        // ---------- Conversión de λ ----------
        Test("λ referencia = identidad", () =>
        {
            foreach (double d in new[] { 10.0, 28, 54, 160 })
                foreach (double t in new[] { 20.0, 25, 40, 60 })
                    Near(AislRite.ConvertLambda(t, d, 0.040), t, $"D {d} e {t}", 1e-9);
        });
        Test("λ menor → más fino; mayor → más grueso", () =>
        {
            Check(AislRite.ConvertLambda(25, 28, 0.036) < 25, "0,036 < 25");
            Check(AislRite.ConvertLambda(25, 28, 0.045) > 25, "0,045 > 25");
            Near(AislRite.ConvertLambda(0, 28, 0.036), 0, "0 → 0");
        });
        // Caso a mano: D = 28, dref = 25, λ = 0,036.
        //   (D + 2·dref)/D = 78/28 = 2,785714;  ln = 1,024504;  × 0,036/0,040 = 0,922054;  exp = 2,514430;
        //   − 1 = 1,514430;  × D/2 = 14 → d = 21,202 mm.
        Test("λ caso a mano D 28, 25 mm, λ 0,036 → 21,20 mm", () =>
            Near(AislRite.ConvertLambda(25, 28, 0.036), 21.202, "d", 0.005));
        // Caso exacto: λ = 2·λref → d = (D/2)·(((D+2dref)/D)² − 1) = 14·((78/28)² − 1) = 5300/56 = 94,642857 mm.
        Test("λ 0,080 caso exacto D 28, 25 mm → 94,6429 mm", () =>
            Near(AislRite.ConvertLambda(25, 28, 0.080), 5300.0 / 56, "d", 1e-9));
        // Caso a mano 2: D = 54, dref = 30, λ = 0,030 → ln(114/54) = 0,747214; × 0,75 = 0,560411; exp = 1,751397;
        //   − 1 = 0,751397; × 27 = 20,288 mm.
        Test("λ caso a mano D 54, 30 mm, λ 0,030 → 20,29 mm", () =>
            Near(AislRite.ConvertLambda(30, 54, 0.030), 20.288, "d", 0.005));

        // ---------- Redondeo comercial ----------
        var com = new AislSettings().CommercialThicknessesMm;
        Test("Redondeo comercial", () =>
        {
            Near(AislRite.RoundCommercial(21.2, com), 25, "21,2 → 25");
            Near(AislRite.RoundCommercial(25, com), 25, "25 → 25");
            Near(AislRite.RoundCommercial(25.04, com), 25, "25,04 → 25 (tolerancia)");
            Near(AislRite.RoundCommercial(25.06, com), 30, "25,06 → 30");
            Near(AislRite.RoundCommercial(5, com), 6, "5 → 6");
            Near(AislRite.RoundCommercial(9.5, com), 10, "9,5 → 10");
            Near(AislRite.RoundCommercial(31, com), 32, "31 → 32");
            Near(AislRite.RoundCommercial(100, com), 100, "100 → 100");
            Near(AislRite.RoundCommercial(101, com), 110, "101 → 110");
            Near(AislRite.RoundCommercial(127, com), 130, "127 → 130");
            Near(AislRite.RoundCommercial(0, com), 0, "0 → 0");
            Near(AislRite.RoundCommercial(23, new List<double> { 30, 20, 25 }), 25, "lista desordenada");
            Near(AislRite.RoundCommercial(23, new List<double>()), 30, "lista vacía → múltiplo de 10");
        });

        // ---------- Requirement ----------
        Test("Requirement ACS 60 °C D 28 λ 0,040", () =>
        {
            var r = AislRite.Requirement(AislRed.Acs, 28, new AislSettings());
            Check(r.Required && r.Material == AislMaterialGroup.Caliente, "Exigido, caliente");
            Near(r.TableMm, 30, "Tabla"); Near(r.ConvertedMm, 30, "Convertido"); Near(r.ThicknessMm, 30, "Final");
            Near(r.TemperatureC, 60, "T"); Near(r.LambdaUsed, 0.040, "λ");
            Check(r.Rule == "RITE Tabla 1.2.4.2 (ACS, interior), 60 °C, D 28 mm: 30 mm; λ 0,040 → 30 mm; comercial 30 mm", r.Rule);
            var hot = AislRite.Requirement(AislRed.Acs, 28, new AislSettings { AcsTemperatureC = 70 });
            Near(hot.TableMm, 30, "70 °C: 25 + 5");
            Check(hot.Rule.Contains("; ACS funcionamiento continuo +5 mm"), hot.Rule);
            Near(AislRite.Requirement(AislRed.Acs, 160, new AislSettings()).TableMm, 45, "D 160 a 60 °C = Tabla 1.2.4.2");
        });
        Test("Requirement clima calor 80 °C D 28 λ 0,036", () =>
        {
            var s = new AislSettings { HotLambda = 0.036 };
            var r = AislRite.Requirement(AislRed.ClimaCaliente, 28, s);
            Near(r.TableMm, 25, "Tabla"); Near(r.ConvertedMm, 21.202, "Conv", 0.005); Near(r.ThicknessMm, 25, "Final");
            Check(r.Rule == "RITE Tabla 1.2.4.2.1 (fluidos calientes, interior), 80 °C, D 28 mm: 25 mm; λ 0,036 → 21,2 mm; comercial 25 mm", r.Rule);
        });
        Test("Requirement clima calor < 40 °C no exigido", () =>
        {
            var r = AislRite.Requirement(AislRed.ClimaCaliente, 28, new AislSettings { HeatingTemperatureC = 35 });
            Check(!r.Required && r.ThicknessMm == 0 && r.Rule == "No exigido: fluido por debajo de 40 °C", r.Rule);
        });
        Test("Requirement fuera de tabla", () =>
        {
            var r = AislRite.Requirement(AislRed.ClimaCaliente, 28, new AislSettings { HeatingTemperatureC = 190 });
            Check(r.Required && r.Rule.Contains("; fuera de tabla: use el procedimiento alternativo"), r.Rule);
            Near(r.TableMm, 30, "Última columna");
        });
        Test("Requirement clima frío 7 °C D 28 λ 0,036 exterior", () =>
        {
            var r = AislRite.Requirement(AislRed.ClimaFrio, 28, new AislSettings { Exterior = true });
            Check(r.Required && r.Material == AislMaterialGroup.Frio && r.Exterior, "Frío exterior");
            Near(r.TableMm, 45, "Tabla"); Near(r.ConvertedMm, AislRite.ConvertLambda(45, 28, 0.036), "Conv");
            Near(r.ThicknessMm, AislRite.RoundCommercial(r.ConvertedMm, new AislSettings().CommercialThicknessesMm), "Final");
            Check(r.Rule.StartsWith("RITE Tabla 1.2.4.2.4 (fluidos fríos, exterior), 7 °C, D 28 mm: 45 mm; λ 0,036 → "), r.Rule);
        });
        Test("Requirement clima mixto = máximo (calor gobierna)", () =>
        {
            var s = new AislSettings { HeatingTemperatureC = 80, ChilledTemperatureC = 12 };
            var r = AislRite.Requirement(AislRed.ClimaMixto, 160, s);
            Check(r.Material == AislMaterialGroup.Frio, "Material frío");
            Near(r.TableMm, 40, "Calor 40 > frío 30");
            Near(r.ConvertedMm, AislRite.ConvertLambda(40, 160, s.ColdLambda), "Convertido con λ frío");
            Near(r.TemperatureC, 80, "T de calor");
            Check(r.Rule.StartsWith("Clima mixto") && r.Rule.Contains("fluidos calientes"), r.Rule);
        });
        Test("Requirement clima mixto = máximo (frío gobierna)", () =>
        {
            var s = new AislSettings { HeatingTemperatureC = 80, ChilledTemperatureC = -5 };
            var r = AislRite.Requirement(AislRed.ClimaMixto, 160, s);
            Near(r.TableMm, 50, "Frío 50 > calor 40");
            Near(r.TemperatureC, -5, "T de frío");
            var hot = AislRite.Requirement(AislRed.ClimaCaliente, 160, new AislSettings { HotLambda = s.ColdLambda });
            var cold = AislRite.Requirement(AislRed.ClimaFrio, 160, s);
            Near(r.ThicknessMm, Math.Max(hot.ThicknessMm, cold.ThicknessMm), "max");
        });
        Test("Requirement frigorífica", () =>
        {
            var r = AislRite.Requirement(AislRed.Frigorifica, 12.7, new AislSettings());
            Check(r.Required && r.Material == AislMaterialGroup.Frigorifico, "Frigorífico");
            Near(r.TableMm, 10, "Tabla"); Near(r.LambdaUsed, 0.036, "λ");
            Near(r.ThicknessMm, AislRite.RoundCommercial(AislRite.ConvertLambda(10, 12.7, 0.036), new AislSettings().CommercialThicknessesMm), "Final");
            Check(r.Rule.StartsWith("RITE Tabla 1.2.4.2.5 (circuitos frigoríficos, interior), D 12,7 mm: 10 mm"), r.Rule);
        });
        Test("Requirement agua fría", () =>
        {
            var r = AislRite.Requirement(AislRed.AguaFria, 28, new AislSettings { ColdWaterThicknessMm = 9 });
            Check(r.Required && r.Material == AislMaterialGroup.Frio, "Exigido");
            Near(r.TableMm, 0, "Sin tabla"); Near(r.ThicknessMm, 9, "9 mm sin redondeo"); Near(r.ConvertedMm, 9, "Conv");
            Check(r.Rule == "Agua fría sanitaria: el RITE no fija espesor; aislamiento anticondensación (IT 1.2.4.2.1.2, punto 9)", r.Rule);
            var off = AislRite.Requirement(AislRed.AguaFria, 28, new AislSettings { InsulateColdWater = false });
            Check(!off.Required, "Desactivado");
            var odd = AislRite.Requirement(AislRed.AguaFria, 28, new AislSettings { ColdWaterThicknessMm = 7.5 });
            Near(odd.ThicknessMm, 7.5, "Sin redondeo comercial");
        });
        Test("Requirement sin clasificar / excluida", () =>
        {
            Check(!AislRite.Requirement(AislRed.SinClasificar, 28, new AislSettings()).Required, "Sin clasificar");
            Check(!AislRite.Requirement(AislRed.Excluida, 28, new AislSettings()).Required, "Excluida");
        });

        // ---------- Clasificador ----------
        var cs = new AislSettings();
        void Cls(string name, string cls, AislRed expected, string fluid = "") =>
            Test($"Clasificar «{name}» ({cls}) → {expected}", () =>
            {
                var c = AislClassifier.Classify(Sys(name, cls, fluid), cs);
                Check(c.Red == expected && !c.ByUser && !string.IsNullOrWhiteSpace(c.Reason), $"{c.Red}: {c.Reason}");
            });
        Cls("Línea frigorífica", "OtherPipe", AislRed.Frigorifica);
        Cls("Refrigerante R-410A", "SupplyHydronic", AislRed.Frigorifica);
        Cls("Tubería", "OtherPipe", AislRed.Frigorifica, "R32");
        Cls("VRV líquido", "OtherPipe", AislRed.Frigorifica);
        Cls("Split salón", "ReturnHydronic", AislRed.Frigorifica);
        Cls("Expansión directa", "OtherPipe", AislRed.Frigorifica);
        Cls("Línea de gas", "OtherPipe", AislRed.Frigorifica);
        Cls("Succión", "OtherPipe", AislRed.Frigorifica);
        Cls("ACS refrigerante", "DomesticHotWater", AislRed.Acs);
        Cls("AFS split", "DomesticColdWater", AislRed.AguaFria);
        Cls("Agua caliente sanitaria", "DomesticHotWater", AislRed.Acs);
        Cls("Agua fría sanitaria", "DomesticColdWater", AislRed.AguaFria);
        Cls("Impulsión calefacción", "SupplyHydronic", AislRed.ClimaCaliente);
        Cls("Retorno radiadores", "ReturnHydronic", AislRed.ClimaCaliente);
        Cls("Suelo radiante", "SupplyHydronic", AislRed.ClimaCaliente);
        Cls("Agua fría enfriadora", "SupplyHydronic", AislRed.ClimaFrio);
        Cls("Chilled water supply", "SupplyHydronic", AislRed.ClimaFrio);
        Cls("Agua de refrigeración", "SupplyHydronic", AislRed.ClimaFrio);
        Cls("Frío / calor", "SupplyHydronic", AislRed.ClimaMixto);
        Cls("Impulsión 2 tubos", "SupplyHydronic", AislRed.ClimaMixto);
        Cls("Changeover", "ReturnHydronic", AislRed.ClimaMixto);
        Cls("Impulsión fancoils", "SupplyHydronic", AislRed.ClimaMixto);  // clima sin pista: lo más exigente
        Cls("XX_SIT_Calefaccion-Retorno", "ReturnHydronic", AislRed.ClimaCaliente);
        Cls("XX_SIT_Calefaccion-Impulsion", "SupplyHydronic", AislRed.ClimaCaliente);
        Cls("XX_SIT_Calefaccion-Retorno", "OtherPipe", AislRed.ClimaCaliente);
        Cls("Impulsión calefacción (nombre antiguo)", "HydronicSupply", AislRed.ClimaCaliente);
        Cls("XX_SIT_Fontaneria-AFS", "DomesticColdWater", AislRed.AguaFria);
        Cls("Saneamiento", "Sanitary", AislRed.Excluida);
        Cls("Ventilación", "Vent", AislRed.Excluida);
        Cls("Rociadores", "FireProtectWet", AislRed.Excluida);
        Cls("Red BIE", "OtherPipe", AislRed.Excluida);
        Cls("Pluviales", "OtherPipe", AislRed.Excluida);
        Cls("Gas natural", "OtherPipe", AislRed.Excluida);
        Cls("Retorno ACS", "OtherPipe", AislRed.Acs);
        Cls("Recirculación", "OtherPipe", AislRed.Acs);
        Cls("ACS", "OtherPipe", AislRed.Acs);
        Cls("Agua fria", "OtherPipe", AislRed.AguaFria);
        Cls("AFS", "OtherPipe", AislRed.AguaFria);
        Cls("Aire ambiente", "OtherPipe", AislRed.SinClasificar);  // «bie» de «ambiente» no excluye
        Cls("Otros", "OtherPipe", AislRed.SinClasificar);
        Test("Override del usuario tiene prioridad (sin distinguir mayúsculas)", () =>
        {
            var s = new AislSettings();
            s.SystemOverrides["línea frigorífica"] = AislRed.Excluida;
            s.SystemOverrides["IMPULSIÓN FANCOILS"] = AislRed.ClimaFrio;
            var a = AislClassifier.Classify(Sys("Línea frigorífica", "OtherPipe"), s);
            var b = AislClassifier.Classify(Sys("Impulsión fancoils", "SupplyHydronic"), s);
            Check(a.Red == AislRed.Excluida && a.ByUser && b.Red == AislRed.ClimaFrio && b.ByUser, $"{a} {b}");
        });

        Test("Red desactivada en el diálogo: no se aísla", () =>
        {
            var s = new AislSettings();
            s.Network(AislRed.AguaFria).Enabled = false;
            var sys = new Dictionary<string, AislSystemInfo>(StringComparer.OrdinalIgnoreCase) { ["AFS"] = Sys("AFS", "DomesticColdWater") };
            var d = AislPlanner.Decide(new[] { new AislItem(1, AislElementKind.Tuberia, "AFS", "P1", 25, null) }, sys, s);
            Check(d[0].Action == AislAction.NoExigido && d[0].Requirement is { Required: false }, d[0].Action.ToString());
        });
        Test("Agua fría por defecto: 9 mm fijos", () =>
        {
            var sys = new Dictionary<string, AislSystemInfo>(StringComparer.OrdinalIgnoreCase) { ["AFS"] = Sys("AFS", "DomesticColdWater") };
            var d = AislPlanner.Decide(new[] { new AislItem(1, AislElementKind.Tuberia, "AFS", "P1", 25, null) }, sys, new AislSettings());
            Check(d[0].Action == AislAction.Crear && d[0].Requirement!.ThicknessMm == 9 && d[0].Requirement!.Required, d[0].Requirement!.Rule);
        });
        Test("Espesor fijo menor que el RITE: se aplica y avisa", () =>
        {
            var s = new AislSettings();
            s.Network(AislRed.ClimaCaliente).ThicknessMm = 20;
            var sys = new Dictionary<string, AislSystemInfo>(StringComparer.OrdinalIgnoreCase) { ["Cal"] = Sys("Calefacción", "SupplyHydronic") };
            var d = AislPlanner.Decide(new[] { new AislItem(1, AislElementKind.Tuberia, "Cal", "P1", 54, null) }, sys, s);
            var r = d[0].Requirement!;
            Check(r.Required && r.ThicknessMm == 20 && r.Rule.Contains("ATENCIÓN") && r.Material == AislMaterialGroup.Caliente, r.Rule);
        });
        Test("Espesor fijo mayor que el RITE: sin aviso, redondeado al comercial", () =>
        {
            var s = new AislSettings();
            s.Network(AislRed.Acs).ThicknessMm = 48;
            var r = AislRite.Fixed(AislRed.Acs, 22, 48, s);
            Check(r.Required && r.ThicknessMm == 50 && !r.Rule.Contains("ATENCIÓN"), r.Rule);
        });
        Test("Redes del diálogo se guardan y se clonan", () =>
        {
            var s = new AislSettings();
            s.Network(AislRed.Frigorifica).InsulationType = "Elastomérico 19";
            s.Network(AislRed.ClimaFrio).ThicknessMm = 25;
            s.Network(AislRed.Acs).Enabled = false;
            string path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "aisl-networks-" + Guid.NewGuid().ToString("N") + ".json");
            try
            {
                Check(AislSettingsStore.Save(s, path), "guardado");
                var l = AislSettingsStore.Load(path);
                Check(l.Network(AislRed.Frigorifica).InsulationType == "Elastomérico 19" && l.Network(AislRed.ClimaFrio).ThicknessMm == 25
                    && !l.Network(AislRed.Acs).Enabled && l.Network(AislRed.AguaFria).ThicknessMm == 9, "leído");
                var c = l.Clone();
                c.Network(AislRed.ClimaFrio).ThicknessMm = 40;
                Check(l.Network(AislRed.ClimaFrio).ThicknessMm == 25, "el clon no comparte las redes");
            }
            finally { System.IO.File.Delete(path); }
        });

        // ---------- Planner ----------
        var systems = new Dictionary<string, AislSystemInfo>(StringComparer.OrdinalIgnoreCase)
        {
            ["ACS"] = Sys("ACS", "DomesticHotWater"),
            ["Calefacción"] = Sys("Calefacción", "SupplyHydronic"),
            ["Fancoils"] = Sys("Fancoils", "OtherPipe"),
            ["Saneamiento"] = Sys("Saneamiento", "Sanitary")
        };
        var items = new List<AislItem>
        {
            new(1, AislElementKind.Tuberia, "ACS", "P1", 28, null),        // crear 30
            new(2, AislElementKind.Tuberia, "ACS", "P1", 28, 20),          // aumentar
            new(3, AislElementKind.Tuberia, "ACS", "P1", 28, 30),          // ya cumple
            new(4, AislElementKind.Tuberia, "ACS", "P1", 28, 40),          // nunca reducir
            new(5, AislElementKind.Pieza, "ACS", "P1", 28, 29.96),         // ya cumple (tolerancia)
            new(6, AislElementKind.Tuberia, "Calefacción", "P1", 54, null),
            new(7, AislElementKind.Tuberia, "Fancoils", "P1", 54, null),
            new(8, AislElementKind.Accesorio, "Fancoils", "P1", 54, null),
            new(9, AislElementKind.Tuberia, "Saneamiento", "P1", 110, null),
            new(10, AislElementKind.Tuberia, "", "P1", 20, null),
            new(11, AislElementKind.Tuberia, "Desconocido", "P1", 20, null),
            new(12, AislElementKind.Tuberia, "ACS", "P1", 28.04, null)     // misma fila de resumen que 1 (28,0)
        };
        var decisions = AislPlanner.Decide(items, systems, new AislSettings());
        AislAction A(long id) => decisions.Single(d => d.Item.ElementId == id).Action;
        Test("Planner acciones", () =>
        {
            Check(A(1) == AislAction.Crear, "1 crear");
            Check(A(2) == AislAction.Aumentar, "2 aumentar");
            Check(A(3) == AislAction.MantenerSuficiente, "3 cumple");
            Check(A(4) == AislAction.MantenerSuficiente, "4 nunca reducir");
            Check(A(5) == AislAction.MantenerSuficiente, "5 tolerancia");
            Check(A(6) == AislAction.Crear, "6 calefacción");
            Check(A(7) == AislAction.SinClasificar && A(8) == AislAction.SinClasificar, "7/8 sin clasificar");
            Check(A(9) == AislAction.Excluida, "9 excluida");
            Check(A(10) == AislAction.SinClasificar && A(11) == AislAction.SinClasificar, "10/11 sin sistema");
            Near(decisions.Single(d => d.Item.ElementId == 1).Requirement!.ThicknessMm, 30, "ACS 30");
            Check(decisions.Single(d => d.Item.ElementId == 11).Classification.Reason == "Sin sistema asignado", "Motivo");
        });
        Test("Planner no exigido (calefacción 35 °C)", () =>
        {
            var d = AislPlanner.Decide(new[] { items[5] }, systems, new AislSettings { HeatingTemperatureC = 35 });
            Check(d[0].Action == AislAction.NoExigido && d[0].Requirement is { Required: false }, d[0].Action.ToString());
        });
        Test("Planner sin clima por agua / sin piezas", () =>
        {
            var d = AislPlanner.Decide(items, systems, new AislSettings { IncludeHydronic = false, IncludeFittingsAndAccessories = false });
            AislDecision D(long id) => d.Single(x => x.Item.ElementId == id);
            Check(D(6).Action == AislAction.Excluida && D(6).Classification.Red == AislRed.Excluida, "Clima excluido");
            Check(D(5).Action == AislAction.Excluida, "Pieza excluida");
            Check(D(1).Action == AislAction.Crear, "Tubería ACS sigue");
            Check(D(7).Action == AislAction.SinClasificar, "Sin clasificar no es clima");
        });
        Test("Planner desviaciones", () =>
        {
            var dev = AislPlanner.Deviations(decisions);
            Check(dev.Count == 3, $"3 desviaciones, hay {dev.Count}");
            Check(dev[0].Element == "Sistema «Fancoils» (2 elementos)", dev[0].Element);
            Check(dev.Count(x => x.Reason == "Sin sistema asignado") == 2, "2 sin sistema");
            Check(dev.Any(x => x.Element == "10") && dev.Any(x => x.Element == "11"), "Ids");
        });
        Test("Planner resumen", () =>
        {
            var sum = AislPlanner.Summarize(decisions);
            var acsCreate = sum.Single(r => r.SystemName == "ACS" && r.Action == AislAction.Crear);
            Check(acsCreate.Count == 2 && acsCreate.OuterMm == 28 && acsCreate.ThicknessMm == 30, $"{acsCreate}");
            Check(sum.Single(r => r.SystemName == "ACS" && r.Action == AislAction.MantenerSuficiente).Count == 3, "3 cumplen");
            Check(sum.Count(r => r.SystemName == "Fancoils") == 1 && sum.Single(r => r.SystemName == "Fancoils").Count == 2, "Fancoils");
            Check(sum.Sum(r => r.Count) == items.Count, "Total");
            Check(sum[0].SystemName == "" && sum.Select(r => r.SystemName).SequenceEqual(sum.Select(r => r.SystemName).OrderBy(x => x, StringComparer.CurrentCultureIgnoreCase)), "Orden por sistema");
            var withDiam = AislPlanner.Summarize(AislPlanner.Decide(new[]
            {
                new AislItem(1, AislElementKind.Tuberia, "ACS", "P1", 54, null), new AislItem(2, AislElementKind.Tuberia, "ACS", "P1", 20, null)
            }, systems, new AislSettings()));
            Check(withDiam[0].OuterMm == 20 && withDiam[1].OuterMm == 54, "Orden por diámetro");
        });

        // ---------- Persistencia ----------
        Test("Ajustes ida y vuelta", () =>
        {
            string path = Path.Combine(Path.GetTempPath(), $"aisl_{Guid.NewGuid():N}", "aislamiento.json");
            var s = new AislSettings { Exterior = true, AcsTemperatureC = 65, HotLambda = 0.034, HotInsulationType = "Lana" };
            s.CommercialThicknessesMm = new List<double> { 10, 20, 30 };
            s.SystemOverrides["Fancoils"] = AislRed.ClimaMixto;
            Check(AislSettingsStore.Save(s, path), "Guardar");
            string json = File.ReadAllText(path);
            Check(json.Contains("\"ClimaMixto\""), "Enum como texto");
            var l = AislSettingsStore.Load(path);
            Check(l.Exterior && l.AcsTemperatureC == 65 && l.HotLambda == 0.034 && l.HotInsulationType == "Lana", "Valores");
            Check(l.CommercialThicknessesMm.SequenceEqual(new double[] { 10, 20, 30 }), "Espesores comerciales sin duplicar");
            Check(l.SystemOverrides.TryGetValue("FANCOILS", out var red) && red == AislRed.ClimaMixto, "Diccionario insensible a mayúsculas");
            Check(AislSettingsStore.Load(path + ".no").AcsTemperatureC == 60, "Ausente → predeterminado");
            File.WriteAllText(path, "{ roto");
            Check(AislSettingsStore.Load(path).HotLambda == 0.040, "Corrupto → predeterminado");
            Directory.Delete(Path.GetDirectoryName(path)!, true);
        });

        // ---------- Informe CSV ----------
        Test("CSV cabecera, BOM y contenido", () =>
        {
            string csv = AislReport.BuildReportCsv(decisions);
            string[] lines = csv.Split(Environment.NewLine);
            Check(lines[0] == "Elemento (Id);Tipo;Sistema;Red;Planta;Diámetro exterior (mm);Temperatura (°C);Espesor existente (mm);Espesor RITE (mm);Acción;Justificación", lines[0]);
            // La justificación contiene «;» → va entre comillas.
            Check(lines[1].StartsWith("1;Tubería;ACS;ACS;P1;28;60;;30;Crear;\"RITE Tabla 1.2.4.2 (ACS, interior)"), lines[1]);
            Check(lines[2].StartsWith("2;Tubería;ACS;ACS;P1;28;60;20;30;Aumentar;"), lines[2]);
            Check(lines[3].Contains(";Ya cumple;"), lines[3]);
            Check(lines[7].Contains(";Sin clasificar;") && lines[7].Contains("No se reconoce la red"), lines[7]);
            Check(lines[9].Contains(";Excluida;"), lines[9]);
            Check(AislReport.BuildDeviationsCsv(AislPlanner.Deviations(decisions)).StartsWith("Elemento;Qué;Motivo;Cómo corregirlo"), "Cabecera desviaciones");
            string folder = Path.Combine(Path.GetTempPath(), $"aisl_{Guid.NewGuid():N}");
            var (rep, dev) = AislReport.Write(decisions, AislPlanner.Deviations(decisions), folder, new DateTime(2026, 11, 18, 9, 5, 7));
            Check(Path.GetFileName(rep) == "informe_aislamiento_20261118_090507.csv", rep);
            Check(Path.GetFileName(dev) == "desviaciones_aislamiento_20261118_090507.csv", dev);
            byte[] bytes = File.ReadAllBytes(rep);
            Check(bytes.Length > 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF, "BOM informe");
            Check(File.ReadAllBytes(dev)[0] == 0xEF, "BOM desviaciones");
            Check(Encoding.UTF8.GetString(bytes, 3, bytes.Length - 3) == AislReport.BuildReportCsv(decisions), "Contenido");
            Directory.Delete(folder, true);
        });
        Test("Textos de red y acción", () =>
        {
            Check(AislReport.RedText(AislRed.Acs) == "ACS" && AislReport.RedText(AislRed.AguaFria) == "Agua fría" &&
                AislReport.RedText(AislRed.ClimaCaliente) == "Clima calor" && AislReport.RedText(AislRed.ClimaFrio) == "Clima frío" &&
                AislReport.RedText(AislRed.ClimaMixto) == "Clima mixto" && AislReport.RedText(AislRed.Frigorifica) == "Frigorífica" &&
                AislReport.RedText(AislRed.SinClasificar) == "Sin clasificar" && AislReport.RedText(AislRed.Excluida) == "Excluida", "Red");
            Check(AislReport.ActionText(AislAction.Crear) == "Crear" && AislReport.ActionText(AislAction.Aumentar) == "Aumentar" &&
                AislReport.ActionText(AislAction.MantenerSuficiente) == "Ya cumple" && AislReport.ActionText(AislAction.NoExigido) == "No exigido", "Acción");
        });

        Console.WriteLine(_failures == 0 ? $"OK: {_checks} comprobaciones" : $"{_failures} de {_checks} comprobaciones fallidas");
        return _failures == 0 ? 0 : 1;
    }
}
