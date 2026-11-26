using ACadSharp;
using ACadSharp.Entities;
using ACadSharp.IO;
using ACadSharp.Tables;
using CadMepRevit.Core;
using CSMath;

/// <summary>
/// Luminarias dibujadas con líneas (como las exportan DIALux y Litestar), plantas enteras metidas en bloques,
/// emergencias por nombre o capa, y que no se pregunte por capas de luminarias o de contornos.
/// </summary>
static class LuminaireTests
{
    public static void Run(Action<bool, string> check, string dir)
    {
        Console.WriteLine("2l) Luminarias dibujadas con líneas, tabla de tipos, plantas en bloques y emergencias");
        Directory.CreateDirectory(dir);
        string path = Path.Combine(dir, "luminarias_sueltas.dxf");
        DxfWriter.Write(path, Build(), false);
        var d = CadReader.Read(path);
        LayerPlanning.FixImplausibleUnits(d);
        double k = DrawingUnits.ToMillimeters(d.HeaderUnit ?? DrawingUnit.Milimetros);
        var all = LooseFixtures.Find(d, k);
        var model = all.Where(f => f.Group.Length == 0).ToList();
        var lum = model.Where(f => f.Layer == "DLX_FL0_LUM").ToList();
        check(lum.Count == 4, $"DIALux: 4 luminarias en DLX_FL0_LUM (salen {lum.Count}); el circuito de ALUMBRADO y la tabla no cuentan");
        check(lum.Count(f => f.Kind == FixtureKind.Lineal && f.Label == "1" && (f.Description ?? "").Contains("DP CBO 1200")) == 2,
              "Tipo 1 por la tabla del plano: lineal 'DP CBO 1200…' (2)  → " + string.Join(" | ", lum.Select(f => f.Name)));
        check(lum.Count(f => f.Kind == FixtureKind.Downlight && f.Label == "2") == 1, "Tipo 2 por la tabla del plano: downlight");
        check(lum.Count(f => f.Kind == FixtureKind.Plafon && f.Label is null) == 1, "Sin rótulo ni tabla: por su forma (400×400 → plafón)");
        var lin = lum.First(f => f.Kind == FixtureKind.Lineal);
        check(Math.Abs(lin.LongMm - 1250) < 5 && Math.Abs(lin.ShortMm - 100) < 5, $"Medida de la lineal 1250×100 (sale {lin.LongMm:0}×{lin.ShortMm:0})");
        var vert = lum.First(f => f.Kind == FixtureKind.Lineal && Math.Abs(f.CenterX - 5050) < 5);
        check(Math.Abs(Math.Abs(vert.AngleRad) - Math.PI / 2) < 0.01 && Math.Abs(vert.CenterY - 625) < 5,
              $"Lineal vertical: girada 90° y en su centro ({vert.CenterX:0}, {vert.CenterY:0}, {vert.AngleRad * 180 / Math.PI:0}°)");
        check(!model.Any(f => Math.Abs(f.CenterX - 9000) < 200), "El dibujo bajo un bloque de luminaria no se repite como luminaria suelta");
        // Plantas en bloques (Litestar / Legrand): las luminarias de dentro, cada una con su planta
        var groups = LooseFixtures.Groups(all);
        check(groups.Count == 2 && groups.Contains("00 Planta sótano") && groups.Contains("02 Planta baja"), "Dos plantas en bloques: " + string.Join(", ", groups));
        var baja = all.Where(f => f.Group == "02 Planta baja").ToList();
        check(baja.Count == 2 && baja.All(f => f.Kind == FixtureKind.Emergencia && f.Label == "660102" && (f.Description ?? "").Contains("URA NEXT")),
              "Planta baja en bloque: 2 emergencias por la tabla (660102 → URA NEXT … NP 1h)  → " + string.Join(" | ", baja.Select(f => f.Name)));
        check(baja.All(f => f.CenterX > 100000), "Las de dentro del bloque quedan en su sitio (desplazadas con el bloque)");
        check(all.Count(f => f.Group == "00 Planta sótano") == 1, "Sótano en bloque: 1 luminaria");
        check(LooseFixtures.GroupForLevel(groups, "PLANTA BAJA") == "02 Planta baja" && LooseFixtures.GroupForLevel(groups, "AUX_00") is null &&
              LooseFixtures.GroupForLevel(groups, "Nivel Sótano -1") == "00 Planta sótano", "Planta del bloque según el nombre del nivel (si no coincide, se pregunta)");
        // Bloques equivalentes: los coloca «Luminarias» con la familia y la altura de su tipo
        int before = d.Blocks.Count;
        LooseFixtures.AddAsBlocks(d, model, k);
        var added = d.Blocks.Skip(before).ToList();
        check(added.Count == model.Count && added.All(b => b.EntityIndex < 0 && b.Role == BlockRole.Luminaria &&
              ModelScopes.Includes(ModelScope.Luminarias, b.Role, b.BlockName)), "Se añaden como bloques de luminaria (los coloca «Luminarias»)");
        var em = new LooseFixture { Layer = "X", LongMm = 250, ShortMm = 100, Description = "URA NEXT A 100 lm NP 1h", Label = "660102" };
        LooseFixtures.Classify(em);
        check(em.Kind == FixtureKind.Emergencia && !BlockElevations.IsCeilingMounted(BlockRole.Luminaria, em.Name) &&
              BlockElevations.For(BlockRole.Luminaria, em.Name).Mm == 2200, "Emergencia: en pared a 2,20 m (como los bloques de emergencia)");
        check(BlockElevations.IsCeilingMounted(BlockRole.Luminaria, lin.Name), "Lineal, downlight y plafón: a la cota del techo");

        // Emergencias por nombre o capa (bloques)
        check(BlockNames.Role("Emergencia URA 21", "0") == BlockRole.Luminaria && BlockNames.Role("Alumbrado de emergencia", "0") == BlockRole.Luminaria,
              "Bloque «Emergencia…»: luminaria");
        check(BlockNames.Role("Pulsador de emergencia", "0") != BlockRole.Luminaria && BlockNames.Role("Ducha de emergencia", "0") != BlockRole.Luminaria,
              "Pulsador / ducha de emergencia: no son luminarias");
        check(BlockNames.Role("XYZ-123", "07-EMERGENCIAS") == BlockRole.Luminaria && BlockNames.Role("XYZ-123", "EMERGENIAS") == BlockRole.Luminaria,
              "Bloque sin nombre conocido en capa de emergencias: luminaria");
        // No se pregunta por capas de luminarias ni de contornos de los programas de alumbrado
        check(!MepKinds.IsUnknownLayer("DLX_FL0_LUM") && !MepKinds.IsUnknownLayer("DLX_FL0_CONT") && !MepKinds.IsUnknownLayer("X1$0$A-AREA-BNDY") &&
              !MepKinds.IsUnknownLayer("OXY_LUM_POINT") && !MepKinds.IsUnknownLayer("OXY_ROOM"), "Capas de luminarias, contornos y recintos: no se pregunta por ellas");
        check(MepKinds.Suggest("ALUMBRADO") == MepKind.TuboElectrico && !LooseFixtures.IsFixtureLayer("ALUMBRADO") && !LooseFixtures.IsFixtureLayer("COLUMNAS"),
              "«ALUMBRADO» sigue siendo de tubos (circuitos), no de luminarias; «COLUMNAS» no es de luminarias");
        // Regla de la oficina: capa de luminarias con un nombre cualquiera
        var saved = UserRules.Current;
        try
        {
            UserRules.Current = new UserRules { FixtureLayers = { "E-PUNTOS DE LUZ" } };
            check(LooseFixtures.IsFixtureLayer("e-puntos de luz") && !MepKinds.IsUnknownLayer("E-PUNTOS DE LUZ"), "Capa marcada por la oficina como «luminarias dibujadas con líneas»");
        }
        finally { UserRules.Current = saved; }
    }

    private static CadDocument Build()
    {
        var doc = new CadDocument();
        doc.Header.InsUnits = ACadSharp.Types.Units.UnitsType.Millimeters;
        Layer L(string name) { var l = new Layer(name); doc.Layers.Add(l); return l; }
        var lum = L("DLX_FL0_LUM"); var idx = L("DLX_FL0_LUMKEY_IDX"); var key = L("DLX_FL0_LUMKEY");
        var circuit = L("ALUMBRADO"); var body = L("OXY_LUM_BODY"); var refl = L("OXY_LUM_REF"); var table = L("OXY_LUM_TABLE");
        var eLum = L("E-LUM");
        void Rect(BlockRecord? into, Layer layer, double x1, double y1, double x2, double y2)
        {
            var p = new LwPolyline { Layer = layer, IsClosed = true };
            foreach (var (x, y) in new[] { (x1, y1), (x2, y1), (x2, y2), (x1, y2) }) p.Vertices.Add(new LwPolyline.Vertex(new XY(x, y)));
            if (into is null) doc.Entities.Add(p); else into.Entities.Add(p);
        }
        void Line(BlockRecord? into, Layer layer, double x1, double y1, double x2, double y2)
        {
            var l = new ACadSharp.Entities.Line(new XYZ(x1, y1, 0), new XYZ(x2, y2, 0)) { Layer = layer };
            if (into is null) doc.Entities.Add(l); else into.Entities.Add(l);
        }
        void Text(BlockRecord? into, Layer layer, string v, double x, double y, double h = 100)
        {
            var t = new TextEntity { Value = v, InsertPoint = new XYZ(x, y, 0), Height = h, Layer = layer };
            if (into is null) doc.Entities.Add(t); else into.Entities.Add(t);
        }
        // DIALux: dos lineales 1250×100 (una horizontal, otra vertical) con su cuerpo interior, un downlight (cuadrado con
        // aspa) y una de 400×400 sin rótulo. Rótulo de tipo al lado de cada una y tabla de luminarias del plano.
        Rect(null, lum, 0, 0, 1250, 100); Rect(null, lum, 20, 20, 1230, 80); Text(null, idx, "1", 600, 200);
        Rect(null, lum, 5000, 0, 5100, 1250); Rect(null, lum, 5020, 20, 5080, 1230); Text(null, idx, "1", 5200, 600);
        Rect(null, lum, 3000, 3000, 3100, 3100); Line(null, lum, 3000, 3000, 3100, 3100); Line(null, lum, 3000, 3100, 3100, 3000); Text(null, idx, "2", 3150, 3150);
        Rect(null, lum, 7000, 3000, 7400, 3400);
        double ty = 20000;
        foreach (var (i, maker, name, qty) in new[] { ("1", "LEDVANCE", "DP CBO 1200 V 32W MS 840 IP66 GY", "2"), ("2", "LEDVANCE", "DL CMFT D100 P 13W MS 940 OP WT", "1") })
        {
            Text(null, key, i, 0, ty); Text(null, key, maker, 1000, ty); Text(null, key, name, 3000, ty); Text(null, key, "32.0 W", 9000, ty); Text(null, key, qty, 11000, ty);
            ty -= 300;
        }
        // Circuito en la capa ALUMBRADO (no es una luminaria) y un bloque de luminaria con dibujo suelto debajo
        var poly = new LwPolyline { Layer = circuit };
        foreach (var (x, y) in new[] { (0.0, 500.0), (5000.0, 500.0), (5000.0, 3000.0) }) poly.Vertices.Add(new LwPolyline.Vertex(new XY(x, y)));
        doc.Entities.Add(poly);
        var dl = new BlockRecord("Downlight empotrado");
        dl.Entities.Add(new Circle { Center = new XYZ(0, 0, 0), Radius = 100 });
        doc.BlockRecords.Add(dl);
        doc.Entities.Add(new Insert(dl) { InsertPoint = new XYZ(9000, 0, 0), Layer = eLum });
        Rect(null, lum, 8950, -50, 9050, 50);
        // Litestar: dos plantas metidas en bloques, con las emergencias dentro, su referencia y la tabla
        var bajaBlock = new BlockRecord("02 Planta baja");
        Rect(bajaBlock, body, 0, 0, 250, 100); Text(bajaBlock, refl, "660102", 300, 150);
        Rect(bajaBlock, body, 4000, 0, 4250, 100); Text(bajaBlock, refl, "660102", 4300, 150);
        Text(bajaBlock, table, "A", 0, -5000); Text(bajaBlock, table, "660102", 1000, -5000); Text(bajaBlock, table, "URA NEXT A 100 lm NP 1h", 2500, -5000);
        Text(bajaBlock, table, "2", 9000, -5000);
        for (int i = 0; i < 4; i++) Line(bajaBlock, L("MUROS" + i), i * 3000, 3000, i * 3000 + 2500, 3000); // arquitectura
        doc.BlockRecords.Add(bajaBlock);
        doc.Entities.Add(new Insert(bajaBlock) { InsertPoint = new XYZ(100000, 0, 0) });
        var sotBlock = new BlockRecord("00 Planta sótano");
        Rect(sotBlock, body, 0, 0, 250, 100); Text(sotBlock, refl, "660104", 300, 150);
        doc.BlockRecords.Add(sotBlock);
        doc.Entities.Add(new Insert(sotBlock) { InsertPoint = new XYZ(200000, 0, 0) });
        return doc;
    }
}
