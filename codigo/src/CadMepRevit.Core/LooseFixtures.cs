using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace CadMepRevit.Core;

/// <summary>Trozo de dibujo (una entidad) en una capa de luminarias, en unidades del dibujo.</summary>
/// <param name="Group">«» si está en el espacio modelo; si está dentro de un bloque de planta entera, el nombre de ese bloque.</param>
/// <param name="EntityIndex">Número de la entidad del espacio modelo que lo contiene (la propia entidad o el bloque de planta).</param>
/// <param name="BlockName">Si es un bloque de luminaria que estaba dentro de un bloque de planta, su nombre.</param>
public sealed record FixtureShape(string Layer, string Group, double MinX, double MinY, double MaxX, double MaxY,
    IReadOnlyList<(double X, double Y)> Points, int EntityIndex, string? BlockName = null);

public enum FixtureKind { Lineal, Downlight, Plafon, Emergencia, Bloque }

/// <summary>Luminaria dibujada con líneas (sin bloque), reconocida agrupando el dibujo que se toca.</summary>
public sealed class LooseFixture
{
    public string Layer { get; init; } = "";
    public string Group { get; init; } = "";
    /// <summary>Centro, en unidades del dibujo.</summary>
    public double CenterX { get; init; }
    public double CenterY { get; init; }
    /// <summary>Dirección del lado largo (radianes).</summary>
    public double AngleRad { get; init; }
    public double LongMm { get; init; }
    public double ShortMm { get; init; }
    /// <summary>Rótulo junto a la luminaria (índice de tipo, referencia…), si lo hay.</summary>
    public string? Label { get; set; }
    /// <summary>Artículo leído de la tabla de luminarias del plano para ese rótulo, si la hay.</summary>
    public string? Description { get; set; }
    public FixtureKind Kind { get; set; }
    /// <summary>Cómo se ha decidido el tipo (para el informe).</summary>
    public string KindSource { get; set; } = "";
    /// <summary>Nombre con el que se coloca (como si fuera un bloque): decide la familia genérica y la altura.</summary>
    public string Name { get; set; } = "";
    public string? BlockName { get; init; }
    public List<int> EntityIndices { get; } = new();
    /// <summary>Número (negativo) del bloque equivalente añadido al plano.</summary>
    public int SyntheticIndex { get; set; }
}

/// <summary>
/// Luminarias que el plano no dibuja como bloques. Los programas de cálculo de alumbrado (DIALux, Litestar…) exportan
/// cada luminaria como un grupo de polilíneas sueltas en una capa de luminarias, con un rótulo de tipo al lado y una
/// tabla de luminarias en el propio plano; otros planos meten la planta entera en un bloque. Aquí se reconocen con
/// reglas generales (capa, forma y tamaño, rótulo y tabla), sin depender de un programa concreto, y se convierten en
/// bloques equivalentes para que el botón «Luminarias» las coloque como cualquier otra.
/// </summary>
public static class LooseFixtures
{
    /// <summary>Lo que se toca a menos de esta distancia es la misma luminaria.</summary>
    public const double JoinGapMm = 20;
    /// <summary>Una luminaria mide como mucho esto (las filas continuas más largas se dejan: el informe lo dice).</summary>
    public const double MaxFixtureMm = 3000;
    public const double MinFixtureMm = 50;
    /// <summary>Una entidad suelta más larga que esto en una capa de luminarias no es parte de una luminaria (circuito, contorno…).</summary>
    public const double MaxPieceMm = 2500;

    // Capa con el DIBUJO de las luminarias: «LUM», «LUMINARIAS», «LUMINAIRE», «LIGHT FIXTURE», «LUCES», «EMERGENCIAS»…
    // Las de «ALUMBRADO» o «ILUMINACIÓN» no: en los planos de electricidad suelen llevar los circuitos.
    private static readonly Regex FixtureWord = new(
        @"(^|[^A-Z])(LUM|LUMS|LUMINARIA|LUMINARIAS|LUMINAIRE|LUMINAIRES|LUCES)([^A-Z]|$)|(^|[^A-Z])EMER|LIGHT[ _-]?FIXT|LIGHTING[ _-]?FIXT",
        RegexOptions.Compiled);
    // …salvo las de rótulos, tablas, puntos de cálculo, recintos, contornos, circuitos, rutas de evacuación…
    private static readonly Regex NotGeometry = new(
        @"KEY|TABLE|TABLA|REF|LABEL|IDX|INDEX|TXT|TEXT|POINT|PUNTO|ROOM|ARCH|CONT|CIRC|ANNO|NOTE|IDEN|TAG|COTA|DIM|LEYEND|LEGEND|SIMB|SYMB|EXIT|ROUTE|RUTA|ESCAPE|ISO|GRID|AREA|BNDY|CALC|RESULT|WORK|MAST|FURN|MOBIL",
        RegexOptions.Compiled);
    // Capa con textos de luminarias (rótulos de tipo y tablas): la palabra de luminarias, aunque sea de rótulos.
    private static readonly Regex FixtureText = new(@"LUM|LUZ|LUCES|EMERG|ILUMIN|ALUMBR|LIGHT|(^|[^A-Z])LITE([^A-Z]|$)", RegexOptions.Compiled);

    private static string U(string s) => RemoveAccents((s ?? "").ToUpperInvariant());

    /// <summary>¿Capa con el dibujo de luminarias? (por su nombre o porque la oficina lo ha dicho).</summary>
    public static bool IsFixtureLayer(string layer)
    {
        if (string.IsNullOrWhiteSpace(layer)) return false;
        if (UserRules.Current.FixtureLayers.Contains(layer, StringComparer.OrdinalIgnoreCase)) return true;
        string u = U(layer);
        return FixtureWord.IsMatch(u) && !NotGeometry.IsMatch(u);
    }

    /// <summary>¿Capa de textos de luminarias (rótulos, tablas)?</summary>
    public static bool IsFixtureTextLayer(string layer) =>
        !string.IsNullOrWhiteSpace(layer) && (FixtureText.IsMatch(U(layer)) || IsFixtureLayer(layer));

    /// <summary>Luminarias dibujadas con líneas en el plano (vacío si no hay).</summary>
    public static List<LooseFixture> Find(CadDrawing d, double unitToMm)
    {
        double k = unitToMm > 0 ? unitToMm : 1;
        var result = new List<LooseFixture>();
        // Piezas sueltas demasiado largas para ser parte de una luminaria: fuera antes de agrupar (si no, un circuito
        // dibujado en la misma capa uniría todas las luminarias en un solo grupo).
        var shapes = d.FixtureShapes.Where(s => s.BlockName != null ||
            Math.Max(s.MaxX - s.MinX, s.MaxY - s.MinY) * k <= MaxPieceMm).ToList();
        double gap = JoinGapMm / k;
        foreach (var bucket in shapes.GroupBy(s => (s.Layer, s.Group)))
        {
            var L = bucket.ToList();
            int n = L.Count;
            var parent = Enumerable.Range(0, n).ToArray();
            int Find(int i) { while (parent[i] != i) i = parent[i] = parent[parent[i]]; return i; }
            var order = Enumerable.Range(0, n).OrderBy(i => L[i].MinX).ToList();
            for (int a = 0; a < n; a++)
            {
                var A = L[order[a]];
                if (A.BlockName != null) continue; // un bloque de luminaria es una luminaria entera
                for (int b = a + 1; b < n; b++)
                {
                    var B = L[order[b]];
                    if (B.MinX > A.MaxX + gap) break;
                    if (B.BlockName != null) continue;
                    if (B.MinY <= A.MaxY + gap && A.MinY <= B.MaxY + gap) parent[Find(order[a])] = Find(order[b]);
                }
            }
            foreach (var cluster in Enumerable.Range(0, n).GroupBy(Find))
            {
                var members = cluster.Select(i => L[i]).ToList();
                var pts = members.SelectMany(m => m.Points).ToList();
                if (pts.Count == 0) continue;
                var (cx, cy, ang, len, wid) = Oriented(pts);
                double longMm = len * k, shortMm = wid * k;
                string? block = members.FirstOrDefault(m => m.BlockName != null)?.BlockName;
                // Una línea suelta no es una luminaria; tampoco lo muy grande (filas continuas, contornos) ni lo muy pequeño.
                if (block is null && (longMm > MaxFixtureMm || longMm < MinFixtureMm || shortMm < 20)) continue;
                // Un bloque «de luminaria» que es la tabla o la leyenda de luminarias, o que mide como un dibujo entero, tampoco.
                if (block != null && (IsTableLayer(block) || IsTableLayer(bucket.Key.Layer) || longMm > PlanDefaults.MaxApplianceFootprintMm)) continue;
                var f = new LooseFixture
                {
                    Layer = bucket.Key.Layer, Group = bucket.Key.Group, CenterX = cx, CenterY = cy, AngleRad = ang,
                    LongMm = longMm, ShortMm = shortMm, BlockName = block
                };
                f.EntityIndices.AddRange(members.Select(m => m.EntityIndex).Distinct());
                result.Add(f);
            }
        }
        // Lo que ya está dibujado como bloque de luminaria en el mismo sitio no se repite.
        var lightBlocks = d.Blocks.Where(b => b.EntityIndex >= 0 && b.Footprint != null &&
            BlockNames.Role(b.BlockName, b.Layer) is BlockRole.Luminaria or BlockRole.MandoIluminacion).ToList();
        result.RemoveAll(f => lightBlocks.Any(b => b.Footprint!.Contains(f.CenterX, f.CenterY, 100 / k)));
        Describe(d, result, k);
        return result;
    }

    /// <summary>Centro, dirección del lado largo y medidas (largo ≥ ancho) del conjunto de puntos, en sus unidades.</summary>
    private static (double Cx, double Cy, double Angle, double Long, double Short) Oriented(List<(double X, double Y)> pts)
    {
        double mx = pts.Average(p => p.X), my = pts.Average(p => p.Y);
        double sxx = 0, syy = 0, sxy = 0;
        foreach (var (x, y) in pts) { sxx += (x - mx) * (x - mx); syy += (y - my) * (y - my); sxy += (x - mx) * (y - my); }
        double ang = 0.5 * Math.Atan2(2 * sxy, sxx - syy);
        // Ángulos casi ortogonales: se redondean a 0/90° (el ruido del dibujo no debe girar la luminaria).
        double deg = ang * 180 / Math.PI;
        double snapped = Math.Round(deg / 90) * 90;
        if (Math.Abs(deg - snapped) < 2) ang = snapped * Math.PI / 180;
        double c = Math.Cos(ang), s = Math.Sin(ang);
        double minU = double.MaxValue, maxU = double.MinValue, minV = double.MaxValue, maxV = double.MinValue;
        foreach (var (x, y) in pts)
        {
            double u = x * c + y * s, v = -x * s + y * c;
            minU = Math.Min(minU, u); maxU = Math.Max(maxU, u); minV = Math.Min(minV, v); maxV = Math.Max(maxV, v);
        }
        double cu = (minU + maxU) / 2, cv = (minV + maxV) / 2;
        double len = maxU - minU, wid = maxV - minV;
        if (wid > len) { (len, wid) = (wid, len); ang += Math.PI / 2; }
        while (ang > Math.PI / 2 + 1e-9) ang -= Math.PI;
        while (ang <= -Math.PI / 2 + 1e-9) ang += Math.PI;
        return (cu * c - cv * s, cu * s + cv * c, ang, len, wid);
    }

    /// <summary>Rótulo de cada luminaria, artículo de la tabla del plano y tipo (lineal, downlight, plafón, emergencia).</summary>
    private static void Describe(CadDrawing d, List<LooseFixture> fixtures, double k)
    {
        // Textos de luminarias: los del plano y los que había dentro de bloques de planta (sin repetir).
        var texts = d.Texts.Concat(d.FixtureTexts).Where(t => IsFixtureTextLayer(t.Layer) && t.Value.Trim().Length > 0)
            .GroupBy(t => (t.Value.Trim(), Math.Round(t.Position.X * k / 10), Math.Round(t.Position.Y * k / 10)))
            .Select(g => g.First()).ToList();
        var labelTexts = new HashSet<CadText>(ReferenceEqualityComparer.Instance);
        // 1) Rótulo: el texto corto más cercano, a menos de 1,5 m o de vez y media su largo. Si hay una capa de rótulos
        //    que «va con» la de las luminarias (empiezan igual: DLX_FL0_LUM ↔ DLX_FL0_LUMKEY_IDX, OXY_LUM ↔ OXY_LUM_REF),
        //    solo esa: así una luminaria no se queda con el rótulo de otra de otra capa que esté al lado.
        var shortTexts = texts.Where(t => { string v = t.Value.Trim(); return v.Length <= 16 && !(v.Contains(' ') && v.Length > 8); }).ToList();
        double Dist(CadText t, LooseFixture f) => Math.Sqrt(Math.Pow(t.Position.X - f.CenterX, 2) + Math.Pow(t.Position.Y - f.CenterY, 2));
        double Reach(LooseFixture f) => Math.Max(1500, 1.5 * f.LongMm) / k;
        foreach (var byLayer in fixtures.GroupBy(f => f.Layer))
        {
            var near = shortTexts.Where(t => byLayer.Any(f => Dist(t, f) < Reach(f))).ToList();
            int bestPrefix = near.Count == 0 ? 0 : near.Max(t => CommonPrefix(t.Layer, byLayer.Key));
            var candidates = bestPrefix >= 3 ? near.Where(t => CommonPrefix(t.Layer, byLayer.Key) == bestPrefix).ToList() : near;
            foreach (var f in byLayer)
            {
                CadText? best = null; double bestD = double.MaxValue;
                foreach (var t in candidates)
                {
                    double dd = Dist(t, f);
                    if (dd < Reach(f) && dd < bestD) { best = t; bestD = dd; }
                }
                if (best != null) { f.Label = best.Value.Trim(); labelTexts.Add(best); }
            }
        }
        // 2) Tabla de luminarias del plano: la fila (textos de la misma capa a la misma altura) donde aparece el rótulo.
        var cells = texts.Where(t => !labelTexts.Contains(t)).ToList();
        var cache = new Dictionary<(string, string), string?>();
        foreach (var f in fixtures.Where(x => x.Label != null))
        {
            if (!cache.TryGetValue((f.Label!, f.Layer), out var desc))
                cache[(f.Label!, f.Layer)] = desc = TableRow(cells, f.Label!, f.Layer, k);
            f.Description = desc;
        }
        // 3) Sin rótulo: si la tabla de luminarias más cercana (la de su planta) tiene un solo artículo, es ese.
        var tableCells = cells.Where(c => IsTableLayer(c.Layer)).ToList();
        var single = new Dictionary<CadText, string?>(ReferenceEqualityComparer.Instance);
        var fromSingle = new HashSet<LooseFixture>(ReferenceEqualityComparer.Instance);
        foreach (var f in fixtures.Where(x => x.Description is null && x.BlockName is null))
        {
            CadText? nearest = null; double nd = double.MaxValue;
            foreach (var c in tableCells)
            {
                double dd = Math.Pow(c.Position.X - f.CenterX, 2) + Math.Pow(c.Position.Y - f.CenterY, 2);
                if (dd < nd) { nd = dd; nearest = c; }
            }
            if (nearest is null) continue;
            if (!single.TryGetValue(nearest, out var article)) single[nearest] = article = OnlyArticle(tableCells, nearest, k);
            if (article != null) { f.Description = article; fromSingle.Add(f); }
        }
        // 4) Tipo: por el artículo, la capa o el nombre del bloque; si no dicen nada, por la forma y el tamaño dibujados.
        foreach (var f in fixtures) Classify(f);
        foreach (var f in fromSingle.Where(x => x.KindSource.StartsWith("artículo")))
            f.KindSource = "única luminaria de la tabla de su planta";
        // Mismo rótulo de tipo en la misma capa = mismo tipo: si el tipo salió de la medida dibujada, manda la mayoría
        // (una luminaria dibujada un poco más ancha no cambia de tipo).
        foreach (var g in fixtures.Where(f => f.Label != null && f.Description is null && f.Kind != FixtureKind.Bloque).GroupBy(f => (f.Layer, f.Label)))
        {
            var major = g.GroupBy(f => f.Kind).OrderByDescending(x => x.Count()).First().Key;
            foreach (var f in g.Where(f => f.Kind != major))
            {
                f.Kind = major;
                f.KindSource = "el de las demás con el mismo rótulo";
                f.Name = NameFor(f);
            }
        }
    }

    private static string? TableRow(List<CadText> cells, string label, string fixtureLayer, double k)
    {
        string? best = null; double bestScore = double.MinValue;
        foreach (var hit in cells.Where(c => string.Equals(c.Value.Trim(), label, StringComparison.OrdinalIgnoreCase)))
        {
            double h = hit.Height > 0 ? hit.Height : 200 / k;
            var row = cells.Where(c => c.Layer == hit.Layer && Math.Abs(c.Position.Y - hit.Position.Y) <= 0.75 * h &&
                                       Math.Abs(c.Position.X - hit.Position.X) <= 80 * h).OrderBy(c => c.Position.X).ToList();
            if (row.Count < 3) continue;
            var words = row.Where(c => !ReferenceEquals(c, hit) && Regex.IsMatch(c.Value, @"[A-Za-zÁÉÍÓÚÑáéíóúñ]{2}") &&
                                       !Regex.IsMatch(c.Value.Trim(), @"^[\d.,]+\s*(W|lm|K|%|lm/W)?$", RegexOptions.IgnoreCase))
                .Select(c => c.Value.Trim()).ToList();
            if (words.Count == 0) continue;
            // Mejor la fila en la que el rótulo es la primera columna y la tabla de la misma planta (capa parecida).
            double score = (ReferenceEquals(row[0], hit) ? 10 : 0) + CommonPrefix(hit.Layer, fixtureLayer) + Math.Min(words.Count, 4);
            if (score <= bestScore) continue;
            bestScore = score;
            // Nombre del artículo: el texto más largo con letras; delante, el fabricante si cabe.
            string name = words.OrderByDescending(w => w.Length).First();
            string? maker = words.FirstOrDefault(w => w != name && w.Length <= 20 && !w.Any(char.IsDigit));
            best = (maker != null ? maker + " " : "") + name;
        }
        return best is null ? null : best.Length > 80 ? best[..80] : best;
    }

    private static readonly Regex TableWord = new(@"KEY|TABLE|TABLA|LEYEND|LEGEND", RegexOptions.Compiled);

    /// <summary>¿Capa (o nombre de bloque) de tabla o leyenda de luminarias?</summary>
    public static bool IsTableLayer(string layer) => TableWord.IsMatch(U(layer));

    /// <summary>
    /// Artículo de la tabla a la que pertenece la celda, si la tabla tiene UN solo artículo (filas con algún número;
    /// la cabecera «Ref / Código / Descripción…» no cuenta). Null si tiene varios o ninguno.
    /// </summary>
    private static string? OnlyArticle(List<CadText> tableCells, CadText cell, double k)
    {
        double h = cell.Height > 0 ? cell.Height : 200 / k;
        var table = tableCells.Where(c => c.Layer == cell.Layer &&
            Math.Abs(c.Position.X - cell.Position.X) <= 100 * h && Math.Abs(c.Position.Y - cell.Position.Y) <= 100 * h).ToList();
        var articles = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var row in table.GroupBy(c => Math.Round(c.Position.Y / h)))
        {
            var r = row.ToList();
            if (r.Count < 3 || !r.Any(c => c.Value.Any(char.IsDigit))) continue;
            var words = r.Where(c => Regex.IsMatch(c.Value, @"[A-Za-zÁÉÍÓÚÑáéíóúñ]{2}") &&
                                     !Regex.IsMatch(c.Value.Trim(), @"^[\d.,]+\s*(W|lm|K|%|lm/W)?$", RegexOptions.IgnoreCase))
                .Select(c => c.Value.Trim()).ToList();
            if (words.Count > 0) articles.Add(words.OrderByDescending(w => w.Length).First());
        }
        return articles.Count == 1 ? articles.First() : null;
    }

    private static int CommonPrefix(string a, string b)
    {
        int i = 0;
        while (i < a.Length && i < b.Length && char.ToUpperInvariant(a[i]) == char.ToUpperInvariant(b[i])) i++;
        return i;
    }

    internal static void Classify(LooseFixture f)
    {
        string text = U((f.Description ?? "") + " " + (f.BlockName ?? ""));
        string layer = U(f.Layer);
        bool T(string pattern) => Regex.IsMatch(text, pattern);
        if (f.BlockName != null && BlockNames.Role(f.BlockName) is BlockRole.Luminaria or BlockRole.MandoIluminacion)
        { f.Kind = FixtureKind.Bloque; f.KindSource = "bloque de luminaria"; }
        else if (T(@"EMERG|AUTONOM|SENALIZ|(^|[^A-Z0-9])(NP|NM|P)?\s?\d{1,2}\s?H([^A-Z0-9]|$)") || Regex.IsMatch(layer, "EMERG|(^|[^A-Z])EMER([^A-Z]|$)"))
        { f.Kind = FixtureKind.Emergencia; f.KindSource = f.Description != null ? "artículo de la tabla del plano" : "capa de emergencias"; }
        else if (T(@"LINEAL|ESTANCA|BATTEN|REGLETA|(^|[^A-Z])TUBO|(^|\D)(600|1200|1500)(\D|$)"))
        { f.Kind = FixtureKind.Lineal; f.KindSource = "artículo de la tabla del plano"; }
        else if (T(@"DOWNLIGHT|EMPOTR|(^|[^A-Z])DL([^A-Z]|$)|FOCO|SPOT"))
        { f.Kind = FixtureKind.Downlight; f.KindSource = "artículo de la tabla del plano"; }
        else if (T(@"PLAFON|DISC|SUPERFICIE|SURFACE|(^|[^A-Z])SF([^A-Z]|$)"))
        { f.Kind = FixtureKind.Plafon; f.KindSource = "artículo de la tabla del plano"; }
        else
        {
            f.Kind = f.LongMm >= 900 && f.LongMm >= 3 * f.ShortMm ? FixtureKind.Lineal
                   : f.LongMm <= 300 ? FixtureKind.Downlight : FixtureKind.Plafon;
            f.KindSource = "forma y tamaño dibujados (el plano no dice el artículo)";
        }
        f.Name = NameFor(f);
    }

    public static string KindLabel(FixtureKind k) => k switch
    {
        FixtureKind.Lineal => "Luminaria lineal",
        FixtureKind.Downlight => "Downlight",
        FixtureKind.Plafon => "Plafón",
        FixtureKind.Emergencia => "Luminaria de emergencia",
        _ => "Luminaria"
    };

    /// <summary>
    /// Nombre del bloque equivalente. Lleva la palabra que decide la familia genérica y la altura («Luminaria lineal»,
    /// «Downlight», «Plafón», «Luminaria de emergencia») y lo que dice el plano (rótulo y artículo) o, si no dice nada,
    /// la medida redondeada; sin « - » para que no se tome por un nombre «Familia - Tipo» de Revit.
    /// </summary>
    private static string NameFor(LooseFixture f)
    {
        if (f.Kind == FixtureKind.Bloque && f.BlockName != null) return f.BlockName;
        string size = $"{Math.Round(f.LongMm / 50) * 50:0}x{Math.Max(50, Math.Round(f.ShortMm / 50) * 50):0}";
        string what = f.Description != null ? (f.Label != null ? $"tipo {f.Label} · " : "") + f.Description
                    : f.Label != null ? $"tipo {f.Label} · {size} mm" : $"{size} mm";
        return (KindLabel(f.Kind) + " · " + what).Replace(" - ", " · ").Replace("|", "/");
    }

    /// <summary>Bloques de planta entera de los que salen luminarias (vacío si todas están en el espacio modelo).</summary>
    public static List<string> Groups(IEnumerable<LooseFixture> fixtures) =>
        fixtures.Select(f => f.Group).Where(g => g.Length > 0).Distinct().OrderBy(g => g, StringComparer.OrdinalIgnoreCase).ToList();

    private static readonly string[] FloorWords =
        { "SOTANO", "SEMISOTANO", "BAJA", "ENTREPLANTA", "PRIMERA", "SEGUNDA", "TERCERA", "CUARTA", "QUINTA", "SEXTA", "ATICO", "CUBIERTA", "BASEMENT", "GROUND" };

    /// <summary>
    /// Bloque de planta que corresponde al nivel elegido, por la palabra de planta que comparten («Planta baja» ↔
    /// nivel «PLANTA BAJA»). Null si ninguno o varios coinciden (entonces se pregunta).
    /// </summary>
    public static string? GroupForLevel(IReadOnlyList<string> groups, string levelName)
    {
        string lv = U(levelName);
        var words = FloorWords.Where(w => Regex.IsMatch(lv, $@"(^|[^A-Z]){w}([^A-Z]|$)")).ToList();
        // «SEMISOTANO» contiene «SOTANO»: si están las dos, vale la larga.
        if (words.Contains("SEMISOTANO")) words.Remove("SOTANO");
        if (words.Count != 1) return null;
        var hits = groups.Where(g => Regex.IsMatch(U(g), $@"(^|[^A-Z]){words[0]}([^A-Z]|$)")).ToList();
        return hits.Count == 1 ? hits[0] : null;
    }

    /// <summary>
    /// Añade las luminarias al plano como bloques equivalentes (en su sitio, girados y con su huella), para que el
    /// botón «Luminarias» las coloque como cualquier bloque. Devuelve cuántas se han añadido.
    /// </summary>
    public static int AddAsBlocks(CadDrawing d, IEnumerable<LooseFixture> fixtures, double unitToMm)
    {
        double k = unitToMm > 0 ? unitToMm : 1;
        int next = -1 - d.Fixtures.Count, added = 0;
        foreach (var f in fixtures)
        {
            f.SyntheticIndex = next--;
            d.Fixtures.Add(f);
            d.Blocks.Add(new CadBlockRef
            {
                Layer = f.Layer, BlockName = f.Name, Position = new P3(f.CenterX, f.CenterY, 0), RotationRadians = f.AngleRad,
                Footprint = new Footprint(f.CenterX, f.CenterY, f.AngleRad, f.LongMm / k, Math.Max(f.ShortMm, 20) / k),
                EntityIndex = f.SyntheticIndex
            });
            added++;
        }
        return added;
    }

    /// <summary>Resumen para el informe.</summary>
    public static string Summary(IReadOnlyCollection<LooseFixture> fixtures)
    {
        var sb = new StringBuilder();
        sb.Append($"Luminarias dibujadas con líneas (sin bloque): {fixtures.Count} reconocidas agrupando el dibujo que se toca en las capas de luminarias (");
        sb.Append(string.Join(", ", fixtures.GroupBy(f => f.Layer).Select(g => $"'{g.Key}' {g.Count()}")));
        sb.Append("). Tipos: ");
        sb.Append(string.Join("; ", fixtures.GroupBy(f => (f.Name, f.KindSource)).OrderByDescending(g => g.Count())
            .Select(g => $"{g.Count()} × {g.Key.Name} (según {g.Key.KindSource})")));
        sb.Append(". Se colocan en el centro de su dibujo, giradas según su lado largo.");
        return sb.ToString();
    }

    private static string RemoveAccents(string s)
    {
        var n = s.Normalize(NormalizationForm.FormD);
        var sb = new StringBuilder(n.Length);
        foreach (char ch in n)
            if (CharUnicodeInfo.GetUnicodeCategory(ch) != UnicodeCategory.NonSpacingMark) sb.Append(ch);
        return sb.ToString().Normalize(NormalizationForm.FormC);
    }
}
