using CadMepRevit.Core;

/// <summary>
/// Tuberías dibujadas con sus dos bordes, como las exporta Revit: cruces (el tubo de debajo cortado), restos de testa de
/// pocos milímetros, bordes cortados que desplazaban el emparejado y cuerpos de pieza dibujados con líneas.
/// Casos sintéticos (mm), sin planos de clientes.
/// </summary>
static class PipeOutlineTests
{
    public static void Run(Action<bool, string> check)
    {
        Console.WriteLine("2s) Tuberías a doble línea: cruces, restos de testa, parejas que siguen y cuerpos de pieza");
        var settings = OutlineDetector.Settings.ForPipes(1, PlanDefaults.ToleranceMm);

        // 1) Cruce: un Ø25 vertical continuo pasa por encima de un Ø40 horizontal, cortado justo lo que tapa el Ø25.
        var crossing = new List<CadSegment>();
        crossing.AddRange(PipeH(0, 0, 487.5, 40));
        crossing.AddRange(PipeH(0, 512.5, 1000, 40));
        crossing.AddRange(PipeV(500, -500, 500, 25));
        var r1 = OutlineDetector.Detect(crossing, settings);
        check(r1.Centerlines.Count == 2 && Spans(r1, 40, true, 0, 1000) && Spans(r1, 25, false, -500, 500) && r1.CrossingsJoined.Count == 1,
            "Cruce: el Ø40 cortado por el Ø25 que pasa por encima queda recto y entero; no se une al Ø25");

        // 2) Corte mutuo: el Ø125 está cortado donde pasa el Ø25, y el Ø25 también está cortado dentro del Ø125.
        var mutual = new List<CadSegment>();
        mutual.AddRange(PipeH(0, 0, 487.5, 125));
        mutual.AddRange(PipeH(0, 512.5, 1000, 125));
        mutual.AddRange(PipeV(500, -600, -40, 25));
        mutual.AddRange(PipeV(500, 5, 600, 25));
        var r2 = OutlineDetector.Detect(mutual, settings);
        check(r2.Centerlines.Count == 2 && Spans(r2, 125, true, 0, 1000) && Spans(r2, 25, false, -600, 600) && r2.CrossingsJoined.Count == 2,
            "Cruce con los dos tubos cortados: los dos quedan rectos y enteros, sin cruz");

        // 3) Restos de testa de pocos milímetros junto al extremo de un tubo: no son bordes.
        var tiny = new List<CadSegment>(PipeH(0, 0, 1000, 40))
        {
            Line(1008, -2, 1008, 1.4), Line(990, -37, 993.4, -37), Line(995, 30, 995.5, 30)
        };
        var r3 = OutlineDetector.Detect(tiny, settings);
        check(r3.Centerlines.Count == 1 && Spans(r3, 40, true, 0, 1000) && r3.TinyLines >= 2,
            "Restos de testa de 0,5–4 mm: se descartan y no crean tubos fantasma ni duplicados");

        // 4) Tres tubos paralelos; al de abajo le falta un trozo de un borde. Antes la cuenta 1ª-2ª se desplazaba y salían
        //    tubos fantasma de 172 y 127,5 mm entre bordes de tubos distintos.
        var parallel = new List<CadSegment>();
        parallel.AddRange(PipeH(0, 0, 2000, 40));
        parallel.AddRange(PipeH(-163.5, 0, 2000, 32));
        parallel.Add(Line(0, -351.5, 2000, -351.5));
        parallel.Add(Line(0, -226.5, 900, -226.5));
        parallel.Add(Line(1056, -226.5, 2000, -226.5));
        var r4 = OutlineDetector.Detect(parallel, settings);
        var widths = r4.Centerlines.Select(c => Math.Round(c.WidthMm ?? 0)).Distinct().OrderBy(w => w).ToList();
        check(widths.SequenceEqual(new double[] { 32, 40, 125 }),
            $"Borde cortado junto a otros tubos paralelos: solo salen los anchos reales (32, 40, 125); salen {string.Join(", ", widths)}");
        check(Spans(r4, 40, true, 0, 2000) && Spans(r4, 32, true, 0, 2000),
            "Borde cortado de un tubo: los tubos vecinos siguen enteros");

        // 5) Codo de Ø32 con otro tubo (Ø25) que termina un poco más cerca de la línea del Ø32: el horizontal llega a su codo
        //    y no al Ø25 (comprobación de que no cambia; el arrastre real lo reproduce el caso 8).
        var corner = new List<CadSegment>();
        corner.AddRange(PipeV(370.1, -1500, -376.8, 32));
        corner.AddRange(PipeH(-352.9, 394.1, 2000, 32));
        corner.AddRange(PipeV(290.6, -1500, -376.7, 25));
        var r5 = OutlineDetector.Detect(corner, settings);
        var horizontal = r5.Centerlines.Where(c => Math.Abs((c.WidthMm ?? 0) - 32) < 1 && Math.Abs(c.Start.Y - c.End.Y) < 1).ToList();
        check(horizontal.Count == 1 && Math.Abs(Math.Min(horizontal[0].Start.X, horizontal[0].End.X) - 370.1) <= 3,
            "Codo junto a otro tubo: el tramo horizontal llega a su codo");

        // 8) Zona real de un plano exportado de Revit (coordenadas desplazadas): Ø40 con codo que pasa por debajo de un Ø25,
        //    Ø32 con codo al lado, y restos de testa de 0,5–5 mm. Antes salían ejes duplicados y solapados del Ø40; deben
        //    salir solo los cinco ejes reales (dos codos) y el Ø25 recto.
        var zone = new List<CadSegment>
        {
            Line(386.1, -1449.4, 386.1, -376.8), Line(354.1, -376.8, 354.1, -1449.4), Line(378.1, -376.8, 374.7, -376.8),
            Line(386.1, -376.8, 385.6, -376.8), Line(394.1, -368.9, 2021.2, -368.9), Line(2021.2, -336.9, 394.1, -336.9),
            Line(394.1, -357.4, 394.1, -360.9), Line(278.1, 2990.3, 278.1, -1441.4), Line(303.1, -1441.4, 303.1, 2990.3),
            Line(203.5, -209.4, 278.1, -209.4), Line(303.1, -209.4, 1911.6, -209.4), Line(1911.6, -169.4, 303.1, -169.4),
            Line(278.1, -169.4, 203.5, -169.4), Line(195.5, -755.8, 195.5, -217.3), Line(155.5, -217.3, 155.5, -755.8),
            Line(195.5, -783.8, 194.9, -778.8), Line(189.1, -769, 186.8, -767.3), Line(164.2, -767.3, 162, -769),
            Line(195.4, -785.7, 195.5, -783.8)
        };
        var r8 = OutlineDetector.Detect(zone, settings);
        check(r8.Centerlines.Count == 5 && Spans(r8, 40, true, 175.5, 1911.6) && Spans(r8, 25, false, -1441.4, 2990.3) &&
              Spans(r8, 32, true, 370.1, 2021.2) && Spans(r8, 32, false, -1449.4, -352.9) && Spans(r8, 40, false, -755.8, -189.4),
            $"Zona exportada de Revit: solo los cinco ejes reales, con sus dos codos (salen {r8.Centerlines.Count})");

        // 6) Te dibujada con líneas (cuerpo de 28,6 × 54 mm sobre un Ø25) junto a un Ø125 que pasa al lado: el cuerpo, leído
        //    de través, no se alarga hasta el Ø125 (salían una cruz y una te inexistentes).
        var tee = new List<CadSegment>();
        tee.AddRange(PipeV(919.3, -1900, -37.9, 25));
        tee.AddRange(PipeV(919.3, 16.1, 5000, 25));
        tee.Add(Line(905, -37.9, 933.6, -37.9));
        tee.Add(Line(905, 16.1, 933.6, 16.1));
        tee.Add(Line(933.6, -37.9, 933.6, 16.1));
        tee.Add(Line(905, -37.9, 905, -25.2));
        tee.Add(Line(905, 3.4, 905, 16.1));
        tee.AddRange(PipeH(-10.9, 0, 885.2, 20));
        tee.AddRange(PipeV(1025.3, -1900, 5000, 125));
        var r6 = OutlineDetector.Detect(tee, settings);
        bool reachesBig = r6.Centerlines.Any(c => Math.Abs((c.WidthMm ?? 0) - 125) > 1 &&
            Math.Max(c.Start.X, c.End.X) > 1000 && Math.Abs(c.Start.Y - c.End.Y) < 1);
        bool branch = r6.Centerlines.Any(c => Math.Abs((c.WidthMm ?? 0) - 20) < 1 && Math.Abs(Math.Max(c.Start.X, c.End.X) - 919.3) <= 3);
        check(!reachesBig && branch, "Te dibujada con líneas: el ramal Ø20 llega al Ø25 y nada se alarga hasta el Ø125 vecino");

        // 7) Conductos: sin estas reglas (un ramal enfrentado a otro sí es una cruz de conducto).
        var ducts = OutlineDetector.Settings.ForPipes(1, PlanDefaults.ToleranceMm);
        check(!new OutlineDetector.Settings().PipeCrossings && !new OutlineDetector.Settings().KeepPairs && new OutlineDetector.Settings().MinLineMm == 0 &&
              ducts.PipeCrossings && ducts.KeepPairs && ducts.MinLineMm == 5,
            "Las reglas de cruces, parejas y restos de testa solo se aplican a tuberías");

        RisersFromCircles(check);
    }

    /// <summary>Bajantes dibujadas como círculos sobre un tubo Ø25 dibujado a doble línea.</summary>
    private static void RisersFromCircles(Action<bool, string> check)
    {
        Console.WriteLine("2t) Bajantes dibujadas con círculos: diámetro del tubo y círculos pequeños sin rótulo");
        const string layer = "Fontanería agua fría";
        var pipe = new NetSegment { Index = 0, Layer = layer, Start = new P3(0, 0, 0), End = new P3(2000, 0, 0), WidthMm = 25 };
        var slopes = SlopeSolver.Solve(new[] { pipe }, new SlopeAssociation(), PlanDefaults.ToleranceMm);
        var d = new CadDrawing { Path = "Planta.dwg" };
        // A) Como las exporta Revit: un círculo del diámetro del tubo y otro de la mitad, tangente por dentro.
        d.Circles.Add(new(new P3(1000, 0, 0), 12.5, layer, 1));
        d.Circles.Add(new(new P3(1000, 6.25, 0), 6.25, layer, 2));
        // B) Círculo pequeño suelto, sin rótulo SUBE/BAJA: símbolo o bajante sin rotular.
        d.Circles.Add(new(new P3(1950, 0, 0), 6.25, layer, 3));
        // C) Círculo pequeño con rótulo SUBE al lado: es una bajante (montante).
        d.Circles.Add(new(new P3(100, 0, 0), 6.25, layer, 4));
        d.Texts.Add(new CadText { Value = "SUBE", Position = new P3(100, -150, 0), Height = 100 });
        var plan = PipeRisers.Plan(d, new[] { pipe }, slopes, new HashSet<string> { layer }, 1, default,
            PlanDefaults.ToleranceMm, 3000, -3000);
        var a = plan.Risers.SingleOrDefault(r => Math.Abs(r.Center.X - 1000) < 1);
        check(a is not null && Math.Abs(a.OuterMm - 25) < 0.01 && a.Entities.Count == 2,
            $"Bajante con círculo interior: toma el Ø25 de su tubo, no el Ø12,5 del símbolo (sale {a?.OuterMm:0.#})");
        check(!plan.Risers.Any(r => Math.Abs(r.Center.X - 1950) < 1) && plan.SmallUnlabeled.Count == 1 &&
              Math.Abs(plan.SmallUnlabeled[0].Center.X - 1950) < 1 && plan.Discarded.ContainsKey(3),
            "Círculo más pequeño que el tubo y sin SUBE/BAJA: no se modela y se lista para revisar");
        check(plan.Risers.Any(r => Math.Abs(r.Center.X - 100) < 1 && r.End.Z > r.Base.Z),
            "Círculo pequeño con rótulo SUBE: se modela como montante");
    }

    private static CadSegment Line(double x1, double y1, double x2, double y2) =>
        new() { Layer = "P-PIPE", Start = new P3(x1, y1, 0), End = new P3(x2, y2, 0) };

    private static IEnumerable<CadSegment> PipeH(double y, double x0, double x1, double w) =>
        new[] { Line(x0, y - w / 2, x1, y - w / 2), Line(x1, y + w / 2, x0, y + w / 2) };

    private static IEnumerable<CadSegment> PipeV(double x, double y0, double y1, double w) =>
        new[] { Line(x - w / 2, y0, x - w / 2, y1), Line(x + w / 2, y1, x + w / 2, y0) };

    /// <summary>¿Hay un eje de ese ancho, horizontal o vertical, que va de un extremo al otro (±3 mm)?</summary>
    private static bool Spans(OutlineResult r, double width, bool horizontal, double from, double to) =>
        r.Centerlines.Any(c =>
        {
            if (Math.Abs((c.WidthMm ?? 0) - width) > 1) return false;
            double a = horizontal ? c.Start.X : c.Start.Y, b = horizontal ? c.End.X : c.End.Y;
            double side = horizontal ? Math.Abs(c.Start.Y - c.End.Y) : Math.Abs(c.Start.X - c.End.X);
            return side < 1 && Math.Abs(Math.Min(a, b) - from) <= 3 && Math.Abs(Math.Max(a, b) - to) <= 3;
        });
}
