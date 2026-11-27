namespace CadMepRevit.Core;

/// <summary>Resultado de convertir una capa dibujada a doble línea (contorno) en ejes.</summary>
public sealed class OutlineResult
{
    /// <summary>Ejes en unidades del dibujo, con su ancho en <see cref="CadSegment.WidthMm"/>.</summary>
    public List<CadSegment> Centerlines { get; } = new();
    public int InputLines { get; internal set; }
    /// <summary>Líneas de cierre (testas) de los rectángulos, que no son tramos.</summary>
    public int Caps { get; internal set; }
    public int UnpairedLines { get; internal set; }
    /// <summary>Huecos entre tramos (ocupados en el CAD por piezas especiales) que se han cerrado.</summary>
    public int GapsClosed { get; internal set; }
    public int DiscardedStubs { get; internal set; }
    /// <summary>Cuerpos de pieza dibujados con líneas (tes, manguitos, embocaduras) descartados: no son tramos.</summary>
    public int FittingBodies { get; internal set; }
    /// <summary>Rectángulos ambiguos que se han leído en la otra dirección por encajar con sus vecinos.</summary>
    public int Reoriented { get; internal set; }
    /// <summary>Rectángulos casi cuadrados descartados como pilares (solo muros).</summary>
    public int Pillars { get; internal set; }
    /// <summary>Fracción (0..1) de la longitud de la capa que forma parejas de líneas paralelas.</summary>
    public double PairedFraction { get; internal set; }
    /// <summary>Líneas dibujadas DENTRO de un elemento (su eje con flechas, rejillas rayadas, rótulos): no son bordes.</summary>
    public int InteriorLines { get; internal set; }
    /// <summary>Ejes descartados por quedar enteros dentro del contorno de otro más largo (dibujo interior, no un tramo).</summary>
    public int ContainedAxes { get; internal set; }
    /// <summary>Rectángulos cruzados por sus diagonales (símbolo de equipo, hueco o patinillo): no son tramos.</summary>
    public int CrossedSymbols { get; internal set; }
    /// <summary>Cuerdas de curvas (codos dibujados con arcos) que no se emparejan: el codo se cierra prolongando los tramos.</summary>
    public int CurvedElbowChords { get; internal set; }
    /// <summary>Líneas descartadas por ser más cortas que el mínimo (restos de testas o de piezas): no son bordes.</summary>
    public int TinyLines { get; internal set; }
    /// <summary>
    /// Tramos cortados en el dibujo justo donde los cruza otro (el plano oculta el de debajo): se unen en línea recta y NO
    /// se unen al que pasa por encima. Puntos del cruce en mm del plano.
    /// </summary>
    public List<P3> CrossingsJoined { get; } = new();
}

/// <summary>
/// Convierte conductos/bandejas/tuberías dibujados con DOBLE LÍNEA (contorno, como los exporta Revit)
/// en ejes con ancho:
/// 1) une líneas colineales solapadas; 2) quita las testas de los rectángulos;
/// 3) empareja líneas paralelas (la 1ª con la 2ª, la 3ª con la 4ª… en cada tramo) → eje + ancho;
/// 4) cierra los huecos que dejan las piezas especiales (codos, tes, transiciones dibujadas como bloque)
///    prolongando los ejes hasta su intersección.
/// No usa Revit: se puede probar fuera.
/// </summary>
public static class OutlineDetector
{
    /// <summary>Una línea entre dos bordes es dibujo interior si mide como mucho esta fracción del borde más corto.</summary>
    public const double InteriorLengthRatio = 0.7;

    public sealed class Settings
    {
        public double UnitToMm { get; init; } = 1.0;
        public double ToleranceMm { get; init; } = 10.0;
        /// <summary>Separación mínima entre las dos líneas para considerarlas un mismo elemento.</summary>
        public double MinWidthMm { get; init; } = 80.0;
        public double MaxWidthMm { get; init; } = 2500.0;
        /// <summary>Hueco máximo que se cierra (pieza especial), como múltiplo del ancho…</summary>
        public double GapFactor { get; init; } = 1.5;
        /// <summary>…y nunca mayor que esto (mm).</summary>
        public double MaxGapMm { get; init; } = 1500.0;
        public double AngleToleranceDeg { get; init; } = 1.0;
        /// <summary>
        /// Muros: un rectángulo cerrado casi cuadrado (largo &lt; 2 × ancho) es un pilar, no un muro, y se descarta.
        /// </summary>
        public bool DiscardSquareRectangles { get; init; }
        /// <summary>
        /// Descartar los cuerpos de pieza dibujados con líneas (ver DropFittingBodies). En muros NO: un tabique corto de otro
        /// espesor alineado con un muro es real.
        /// </summary>
        public bool DropFittingBodies { get; init; } = true;
        /// <summary>
        /// Descartar el dibujo que no es un tramo: cuerdas de curvas (codos dibujados con arcos), rectángulos cruzados por sus
        /// diagonales (símbolo de subida/bajada o de equipo) y ejes que quedan enteros dentro de otro tramo más largo
        /// (rejillas, juntas de transición, rótulos dibujados con líneas); y emparejar los bordes dejando dentro el eje
        /// dibujado y el dibujo interior. Para conductos y bandejas (ajustes por defecto). En muros NO (un arco es un muro
        /// curvo) ni en tuberías (sus planos ya se modelaban bien y estas reglas se han medido solo con conductos).
        /// </summary>
        public bool CleanDrawing { get; init; } = true;

        /// <summary>Bandejas: cerrar el codo cuando un arco del contorno confirma la unión de los tramos.</summary>
        public bool CloseDrawnArcElbows { get; init; }

        /// <summary>
        /// Líneas más cortas que esto (mm) no son bordes y se descartan antes de emparejar (0 = solo la tolerancia). En los
        /// planos exportados de Revit quedan trocitos de testa o de pieza de 0,5–4 mm que rompían el emparejado: tramos
        /// fantasma entre dos tubos vecinos y tubos partidos o duplicados.
        /// </summary>
        public double MinLineMm { get; init; }

        /// <summary>
        /// Tuberías: el plano corta el tubo de debajo donde lo cruza otro (hueco del ancho del que pasa). Esos dos trozos,
        /// alineados y del mismo ancho, se unen en línea recta antes de buscar codos o tes, en vez de unirse al que pasa
        /// (daba una cruz o dos tes que no existen). Y al cerrar un hueco no se arrastra el extremo de otro tramo que tiene
        /// su propia esquina más cerca. En conductos, bandejas y muros no: un ramal enfrentado a otro sí es una cruz.
        /// </summary>
        public bool PipeCrossings { get; init; }

        /// <summary>
        /// Tuberías: dos bordes emparejados siguen siendo pareja mientras continúen los dos (ver KeptPairs en Detect).
        /// </summary>
        public bool KeepPairs { get; init; }

        /// <summary>
        /// Tuberías y tubos dibujados con sus dos bordes (planos exportados de Revit): de 6 a 400 mm de diámetro.
        /// Los codos y tes dibujados como bloque dejan huecos mayores que el propio diámetro (hasta 4 veces).
        /// </summary>
        public static Settings ForPipes(double unitToMm, double toleranceMm) => new()
        {
            UnitToMm = unitToMm, ToleranceMm = Math.Min(toleranceMm, 3), MinWidthMm = 10, MaxWidthMm = 400,
            GapFactor = 4, MaxGapMm = 400, CleanDrawing = false,
            // Un borde de tubo nunca mide menos que medio diámetro mínimo.
            MinLineMm = 5, PipeCrossings = true, KeepPairs = true
        };

        /// <summary>Ajustes típicos para muros de arquitectura.</summary>
        public static Settings ForWalls(double unitToMm, double toleranceMm) => new()
        {
            UnitToMm = unitToMm, ToleranceMm = toleranceMm, MinWidthMm = 50, MaxWidthMm = 1000,
            GapFactor = 1.5, MaxGapMm = 1200, DiscardSquareRectangles = true, DropFittingBodies = false, CleanDrawing = false
        };
    }

    /// <summary>Línea 2D en coordenadas locales (mm) expresada como recta (d, n, c) e intervalo [T0, T1].</summary>
    private sealed class Ln
    {
        public int Cluster;
        public P3 D, N;          // dirección y normal unitarias
        public double C;         // desplazamiento: n·p
        public double T0, T1;    // intervalo a lo largo de d
        public double Z;
        public double Len => T1 - T0;
        public P3 At(double t) => D * t + N * C;
        public P3 A => At(T0);
        public P3 B => At(T1);
    }

    /// <summary>Eje candidato en coordenadas locales (mm).</summary>
    private sealed class Axis
    {
        public P3 S, E;
        public double W, Z;
        public bool FreeS = true, FreeE = true;
        /// <summary>Viene de un rectángulo cerrado (dos lados + dos testas).</summary>
        public bool FromRect;
        /// <summary>Rectángulo con sus diagonales dibujadas (aspa): símbolo, no tramo.</summary>
        public bool Crossed;
        public P3 Dir => (E - S).Normalized();
        public double Len => S.DistanceTo(E);
    }

    public static OutlineResult Detect(IEnumerable<CadSegment> input, Settings s)
    {
        var result = new OutlineResult();
        var segs = input.ToList();
        result.InputLines = segs.Count;
        if (segs.Count == 0) return result;
        string layer = segs[0].Layer;
        double k = s.UnitToMm;
        double tol = Math.Max(0.001, s.ToleranceMm);
        double angTol = s.AngleToleranceDeg * Math.PI / 180.0;

        // Origen local: evita perder precisión con coordenadas UTM.
        var origin = new P3(segs[0].Start.X * k, segs[0].Start.Y * k, 0);
        P3 Local(P3 p) => new P3(p.X * k, p.Y * k, 0) - origin;

        // 1) Rectas y agrupación por dirección
        var raw = new List<(P3 A, P3 B, double Z, double Theta)>();
        foreach (var c in segs)
        {
            // Cuerdas de una curva (codo dibujado con arcos): no son bordes rectos. Emparejadas daban un racimo de tramos
            // cortos en cada codo; sin ellas, los dos tramos rectos se prolongan hasta cortarse (paso de cerrar huecos).
            if (s.CleanDrawing && c.ArcId != 0) { result.CurvedElbowChords++; continue; }
            P3 a = Local(c.Start), b = Local(c.End);
            if (a.DistanceTo(b) <= tol) continue;
            if (a.DistanceTo(b) < s.MinLineMm) { result.TinyLines++; continue; }
            double th = Math.Atan2(b.Y - a.Y, b.X - a.X);
            if (th < 0) th += Math.PI;
            if (th >= Math.PI) th -= Math.PI;
            raw.Add((a, b, (c.Start.Z + c.End.Z) * 0.5 * k, th));
        }
        var order = Enumerable.Range(0, raw.Count).OrderBy(i => raw[i].Theta).ToList();
        var clusterOf = new int[raw.Count];
        int clusters = 0;
        for (int i = 0; i < order.Count; i++)
        {
            if (i > 0 && raw[order[i]].Theta - raw[order[i - 1]].Theta > angTol) clusters++;
            clusterOf[order[i]] = clusters;
        }
        clusters++;
        // Las direcciones casi 0 y casi 180° son la misma
        if (clusters > 1 && raw.Count > 0 &&
            raw[order[0]].Theta + Math.PI - raw[order[^1]].Theta <= angTol)
        {
            int last = clusterOf[order[^1]];
            for (int i = 0; i < raw.Count; i++) if (clusterOf[i] == last) clusterOf[i] = 0;
        }

        // 2) Unir líneas colineales solapadas o que se tocan
        var lines = new List<Ln>();
        foreach (var grp in Enumerable.Range(0, raw.Count).GroupBy(i => clusterOf[i]))
        {
            var idx = grp.ToList();
            int longest = idx.OrderByDescending(i => raw[i].A.DistanceTo(raw[i].B)).First();
            var d = (raw[longest].B - raw[longest].A).Normalized();
            var n = new P3(-d.Y, d.X, 0);
            var items = idx.Select(i =>
            {
                double t0 = raw[i].A.Dot(d), t1 = raw[i].B.Dot(d);
                var mid = (raw[i].A + raw[i].B) * 0.5;
                return (C: mid.Dot(n), T0: Math.Min(t0, t1), T1: Math.Max(t0, t1), raw[i].Z);
            }).OrderBy(x => x.C).ToList();

            int start = 0;
            for (int i = 1; i <= items.Count; i++)
            {
                if (i < items.Count && items[i].C - items[i - 1].C <= tol) continue;
                var same = items.GetRange(start, i - start).OrderBy(x => x.T0).ToList();
                double c = same.Average(x => x.C), z = same.Average(x => x.Z);
                double cur0 = same[0].T0, cur1 = same[0].T1;
                foreach (var x in same.Skip(1))
                {
                    if (x.T0 <= cur1 + tol) { cur1 = Math.Max(cur1, x.T1); continue; }
                    lines.Add(new Ln { Cluster = grp.Key, D = d, N = n, C = c, T0 = cur0, T1 = cur1, Z = z });
                    cur0 = x.T0; cur1 = x.T1;
                }
                lines.Add(new Ln { Cluster = grp.Key, D = d, N = n, C = c, T0 = cur0, T1 = cur1, Z = z });
                start = i;
            }
        }
        double totalLen = lines.Sum(l => l.Len);

        // 3) Testas: línea con una perpendicular en CADA extremo, hacia el mismo lado y de igual longitud
        //    (forma de U o rectángulo). La testa es el lado corto, salvo que el lado corto sea más
        //    estrecho que el ancho mínimo (entonces es un tramo corto y ancho).
        var endGrid = new NetworkBuilder.PointGrid(tol);
        var endOwner = new List<int>();
        for (int i = 0; i < lines.Count; i++)
        {
            endGrid.Add(lines[i].A); endOwner.Add(i);
            endGrid.Add(lines[i].B); endOwner.Add(i);
        }
        const double perpCos = 0.035; // ~2°
        (double Depth, int Other)? Leg(int i, P3 p)
        {
            var l = lines[i];
            foreach (int id in endGrid.Near(p, tol))
            {
                int m = endOwner[id];
                if (m == i || Math.Abs(lines[m].D.Dot(l.D)) > perpCos) continue;
                var other = lines[m].A.DistanceTo(p) <= tol ? lines[m].B : lines[m].A;
                double side = (other - p).Dot(l.N);
                return (side, m);
            }
            return null;
        }
        var isCap = new bool[lines.Count];
        for (int i = 0; i < lines.Count; i++)
        {
            var la = Leg(i, lines[i].A);
            var lb = Leg(i, lines[i].B);
            if (la is null || lb is null) continue;
            double da = la.Value.Depth, db = lb.Value.Depth;
            if (Math.Sign(da) != Math.Sign(db) || Math.Abs(Math.Abs(da) - Math.Abs(db)) > tol * 2) continue;
            double depth = Math.Abs(da), len = lines[i].Len;
            // Una testa mide lo que el ancho del elemento: nunca más que el ancho máximo (p. ej. el lado de una habitación).
            if (Math.Min(len, depth) > s.MaxWidthMm) continue;
            bool cap;
            if (Math.Abs(len - depth) <= tol)
                cap = lines[i].Cluster > lines[la.Value.Other].Cluster; // cuadrado: criterio fijo
            else if (len < depth) cap = len >= s.MinWidthMm && len <= s.MaxWidthMm;
            else cap = depth < s.MinWidthMm && len <= s.MaxWidthMm;
            isCap[i] = cap;
        }
        result.Caps = isCap.Count(c => c);

        // 4) Emparejar paralelas por tramos: en cada intervalo, ordenadas por desplazamiento,
        //    se emparejan 1ª-2ª, 3ª-4ª… si su separación es un ancho válido.
        var axes = new List<Axis>();
        double pairedLen = 0, interiorLen = 0;
        var paired = new bool[lines.Count];
        var interior = new HashSet<int>();

        // Mejor emparejamiento de las líneas activas en un intervalo (ordenadas por desplazamiento). Antes se emparejaban
        // 1ª-2ª, 3ª-4ª…; si el conducto lleva dibujo DENTRO (su eje con flechas de sentido, una rejilla rayada, un rótulo),
        // esas líneas rompían la paridad y salían conductos de medio ancho o varios donde hay uno. Ahora una pareja puede
        // dejar dentro líneas que NO son bordes: las claramente más cortas que sus dos bordes, o la que está justo en medio
        // (el eje). Se elige el emparejamiento de más peso (bordes más largos), y a igualdad el que deja dentro un eje.
        // Dos conductos paralelos siguen saliendo como dos: sus bordes interiores son tan largos como los exteriores.
        // Emparejamiento clásico (tuberías): 1ª-2ª, 3ª-4ª… si su separación es un ancho válido.
        List<(int P, int Q, List<int> Skipped)> ConsecutivePairs(List<int> act)
        {
            var res = new List<(int, int, List<int>)>();
            for (int q = 0; q + 1 < act.Count;)
            {
                double w = lines[act[q + 1]].C - lines[act[q]].C;
                if (w >= s.MinWidthMm && w <= s.MaxWidthMm) { res.Add((act[q], act[q + 1], new List<int>())); q += 2; }
                else q++;
            }
            return res;
        }

        // Tuberías: las parejas siguen. Dos bordes que ya formaban un tubo en el intervalo anterior siguen siendo pareja
        // mientras los dos continúen, aunque aparezca o desaparezca otra línea entre medias o al lado. Antes, el
        // emparejamiento 1ª-2ª, 3ª-4ª se rehacía en cada intervalo con TODAS las líneas paralelas del plano: un borde
        // cortado (por un cruce o una pieza) desplazaba la cuenta y salían tubos fantasma entre bordes de tubos distintos.
        // Las líneas sueltas se emparejan entre sí como siempre, pero nunca por encima de un tubo ya formado.
        List<(int P, int Q, List<int> Skipped)> KeptPairs(List<int> act, ICollection<(int A, int B)> previous)
        {
            var pos = new Dictionary<int, int>();
            for (int i = 0; i < act.Count; i++) pos[act[i]] = i;
            var res = new List<(int, int, List<int>)>();
            var used = new HashSet<int>();
            foreach (var (a, b) in previous)
            {
                if (!pos.ContainsKey(a) || !pos.ContainsKey(b) || used.Contains(a) || used.Contains(b)) continue;
                res.Add((a, b, new List<int>()));
                used.Add(a); used.Add(b);
            }
            var rest = act.Where(i => !used.Contains(i)).ToList();
            for (int q = 0; q + 1 < rest.Count;)
            {
                int a = rest[q], b = rest[q + 1];
                double w = lines[b].C - lines[a].C;
                bool over = pos[b] - pos[a] > 1; // entre las dos queda un borde de otro tubo ya emparejado
                if (w >= s.MinWidthMm && w <= s.MaxWidthMm && !over) { res.Add((a, b, new List<int>())); q += 2; }
                else q++;
            }
            return res;
        }

        List<(int P, int Q, List<int> Skipped)> BestPairs(List<int> act, double u0, double v0)
        {
            int m = act.Count;
            // Eje dibujado: línea justo en medio de dos bordes (A debajo, B encima, a un ancho válido) entre los que no hay más
            // que ella y dibujo claramente más corto que esos bordes. No es borde de nada: no se empareja y siempre queda
            // dentro. (El eje suele ser más largo que cada tramo: sigue de un tramo al siguiente a través de las transiciones.)
            var isAxis = new bool[m];
            var borders = new (int A, int B)[m];
            for (int i = 0; i < m; i++)
            {
                double c = lines[act[i]].C;
                for (int ia = i - 1; ia >= 0 && !isAxis[i]; ia--)
                {
                    double half = c - lines[act[ia]].C;
                    if (2 * half > s.MaxWidthMm) break;
                    if (2 * half < s.MinWidthMm) continue;
                    for (int ib = i + 1; ib < m; ib++)
                    {
                        double d = lines[act[ib]].C - c;
                        if (d > half + 2 * tol) break;
                        if (d < half - 2 * tol) continue;
                        double edge = Math.Min(lines[act[ia]].Len, lines[act[ib]].Len);
                        bool clean = true;
                        for (int k2 = ia + 1; k2 < ib && clean; k2++)
                            if (k2 != i && lines[act[k2]].Len > InteriorLengthRatio * edge) clean = false;
                        if (clean) { isAxis[i] = true; borders[i] = (ia, ib); break; }
                    }
                }
            }
            // Si uno de sus bordes es a su vez «eje» de otra pareja, son conductos paralelos separados lo mismo que su
            // ancho (bordes 0-300 y 600-900), no un conducto con eje: ninguno de los dos es eje.
            var candidate = (bool[])isAxis.Clone();
            for (int i = 0; i < m; i++)
                if (candidate[i] && (candidate[borders[i].A] || candidate[borders[i].B])) isAxis[i] = false;
            var best = new double[m + 1];
            var choice = new (int J, List<int> Skipped)?[m + 1];
            for (int i = m - 1; i >= 0; i--)
            {
                best[i] = best[i + 1]; choice[i] = null;
                if (isAxis[i]) continue;
                var li = lines[act[i]];
                for (int j = i + 1; j < m; j++)
                {
                    if (isAxis[j]) continue;
                    var lj = lines[act[j]];
                    double w = lj.C - li.C;
                    if (w > s.MaxWidthMm) break;
                    if (w < s.MinWidthMm) continue;
                    double edge = Math.Min(li.Len, lj.Len);
                    var skipped = new List<int>();
                    bool ok = true;
                    for (int k2 = i + 1; k2 < j && ok; k2++)
                    {
                        if (isAxis[k2] || lines[act[k2]].Len <= InteriorLengthRatio * edge) skipped.Add(act[k2]);
                        else ok = false;
                    }
                    if (!ok) continue;
                    double score = edge * edge + best[j + 1];
                    if (score > best[i] + 1e-9) { best[i] = score; choice[i] = (j, skipped); }
                }
            }
            var result = new List<(int, int, List<int>)>();
            for (int i = 0; i < m;)
            {
                if (choice[i] is { } c) { result.Add((act[i], act[c.J], c.Skipped)); i = c.J + 1; }
                else i++;
            }
            return result;
        }
        foreach (var grp in Enumerable.Range(0, lines.Count).Where(i => !isCap[i]).GroupBy(i => lines[i].Cluster))
        {
            var idx = grp.ToList();
            var cuts = idx.SelectMany(i => new[] { lines[i].T0, lines[i].T1 }).OrderBy(t => t).ToList();
            var bps = new List<double>();
            foreach (var t in cuts) if (bps.Count == 0 || t - bps[^1] > tol) bps.Add(t);
            var open = new Dictionary<(int, int), (double U, double V)>();
            for (int b = 0; b + 1 < bps.Count; b++)
            {
                double u = bps[b], v = bps[b + 1];
                var active = idx.Where(i => lines[i].T0 <= u + tol && lines[i].T1 >= v - tol)
                                .OrderBy(i => lines[i].C).ToList();
                var now = new HashSet<(int, int)>();
                var pairsHere = s.CleanDrawing ? BestPairs(active, u, v)
                    : s.KeepPairs ? KeptPairs(active, open.Keys.ToList()) : ConsecutivePairs(active);
                foreach (var (p, q, skipped) in pairsHere)
                {
                    var key = (p, q);
                    now.Add(key);
                    open[key] = open.TryGetValue(key, out var iv) && Math.Abs(iv.V - u) <= tol ? (iv.U, v) : (u, v);
                    // Las líneas interiores (eje dibujado, rejilla, rótulo dentro del conducto) quedan explicadas por la pareja.
                    foreach (int k2 in skipped) { paired[k2] = true; interiorLen += v - u; interior.Add(k2); }
                }
                // Cerrar las parejas que ya no continúan
                foreach (var key in open.Keys.Where(kk => !now.Contains(kk)).ToList())
                {
                    AddAxis(key, open[key]);
                    open.Remove(key);
                }
            }
            foreach (var kv in open) AddAxis(kv.Key, kv.Value);
        }

        void AddAxis((int A, int B) key, (double U, double V) iv)
        {
            var la = lines[key.A]; var lb = lines[key.B];
            double c = (la.C + lb.C) * 0.5;
            var mid = new Ln { D = la.D, N = la.N, C = c };
            bool Corner(P3 p) => endGrid.Near(p, tol).Any(id => isCap[endOwner[id]]);
            bool rect = Corner(la.At(iv.U)) && Corner(la.At(iv.V)) && Corner(lb.At(iv.U)) && Corner(lb.At(iv.V));
            // Aspa: una línea (de cualquier dirección) une dos esquinas opuestas del contorno de la pareja.
            bool Diagonal(P3 p, P3 q) => endGrid.Near(p, 2 * tol).Any(id =>
            {
                var l = lines[endOwner[id]];
                return l.A.DistanceTo(p) <= 2 * tol && l.B.DistanceTo(q) <= 2 * tol || l.B.DistanceTo(p) <= 2 * tol && l.A.DistanceTo(q) <= 2 * tol;
            });
            bool crossed = Diagonal(la.At(iv.U), lb.At(iv.V)) || Diagonal(la.At(iv.V), lb.At(iv.U));
            axes.Add(new Axis { S = mid.At(iv.U), E = mid.At(iv.V), W = lb.C - la.C, Z = (la.Z + lb.Z) * 0.5, FromRect = rect, Crossed = crossed });
            pairedLen += 2 * (iv.V - iv.U);
            paired[key.A] = paired[key.B] = true;
        }
        result.UnpairedLines = Enumerable.Range(0, lines.Count).Count(i => !isCap[i] && !paired[i]);
        double capLen = Enumerable.Range(0, lines.Count).Where(i => isCap[i]).Sum(i => lines[i].Len);
        result.PairedFraction = totalLen - capLen > 0 ? Math.Min(1, (pairedLen + interiorLen) / (totalLen - capLen)) : 0;
        result.InteriorLines = interior.Count;

        // 4a) Muros: los rectángulos cerrados casi cuadrados son pilares
        if (s.DiscardSquareRectangles)
            result.Pillars = axes.RemoveAll(a => a.FromRect && a.Len < 2 * a.W);

        // 4b) Rectángulo que admite las dos lecturas (largo×ancho o ancho×largo): se elige la que
        //     enlaza mejor con los tramos vecinos (alineada con otro tramo > corta a otro tramo).
        for (int i = 0; i < axes.Count; i++)
        {
            var a = axes[i];
            if (!a.FromRect || a.Len < s.MinWidthMm || a.Len > s.MaxWidthMm) continue;
            var dir = a.Dir;
            var nrm = new P3(-dir.Y, dir.X, 0);
            var center = (a.S + a.E) * 0.5;
            var alt = new Axis { S = center - nrm * (a.W / 2), E = center + nrm * (a.W / 2), W = a.Len, Z = a.Z, FromRect = true };
            var others = axes.Where((_, j) => j != i).ToList();
            if (Score(alt, others, s, tol) > Score(a, others, s, tol)) { axes[i] = alt; result.Reoriented++; }
        }

        // 4c) Cuerpos de pieza dibujados con líneas (tes, manguitos, embocaduras): no son tramos
        if (s.DropFittingBodies) result.FittingBodies = DropFittingBodies(axes, s, tol);

        // 4d) Dibujo que no es un tramo: rectángulos con aspa y ejes que quedan enteros dentro de otro tramo más largo.
        if (s.CleanDrawing)
        {
            result.CrossedSymbols = axes.RemoveAll(a => a.Crossed);
            result.ContainedAxes = DropContained(axes, tol);
            result.CurvedElbowChords += DropCurvePieces(axes, tol);
        }

        // 5) Extremos que ya tocan a otro eje no son libres
        for (int i = 0; i < axes.Count; i++)
        for (int j = 0; j < axes.Count; j++)
        {
            if (i == j) continue;
            foreach (bool atS in new[] { true, false })
            {
                var p = atS ? axes[i].S : axes[i].E;
                var (dist, _) = NetworkBuilder.ProjectToSegment(p, axes[j].S, axes[j].E);
                if (dist <= tol) { if (atS) axes[i].FreeS = false; else axes[i].FreeE = false; }
            }
        }

        if (s.CloseDrawnArcElbows) CloseArcElbows(axes, segs, Local, s, tol, result);
        CloseGaps(axes, s, tol, result);
        for (int i = 0; i < result.CrossingsJoined.Count; i++)
            result.CrossingsJoined[i] = result.CrossingsJoined[i] + origin; // a mm del plano

        // 6) Restos sin sentido: tramos más cortos que la cuarta parte de su ancho
        foreach (var a in axes)
        {
            if (a.Len < Math.Max(tol, 0.25 * a.W)) { result.DiscardedStubs++; continue; }
            P3 Back(P3 p) => new P3((p.X + origin.X) / k, (p.Y + origin.Y) / k, a.Z / k);
            result.Centerlines.Add(new CadSegment
            {
                Layer = layer, Start = Back(a.S), End = Back(a.E), Source = "DOBLE LÍNEA", WidthMm = Math.Round(a.W, 1)
            });
        }
        return result;
    }

    /// <summary>
    /// Un eje CORTO para su ancho (largo ≤ 3 anchos), alineado y pegado a (o encima de) un tramo MÁS LARGO de OTRO ancho,
    /// es el cuerpo de una pieza dibujado con líneas (una te o un manguito más gruesos que el tubo, la embocadura de un
    /// injerto de PVC) o una pareja falsa de líneas lejanas, no un tramo. Se descarta ANTES de cerrar huecos: si no, ese
    /// eje «ocupa» el extremo del tubo, el hueco de la pieza no se cierra y luego aparece como un tubo duplicado de otro
    /// diámetro (nudos de 5 a 7 tramos que Revit no puede unir, y transiciones donde no hay ninguna).
    /// Los cuerpos encadenados (embocadura + cuello) se descartan uno tras otro. No se toca un eje del mismo ancho que su
    /// vecino (es el mismo tubo), ni uno largo (un tubo corto de otro diámetro mide más de 3 diámetros).
    /// </summary>
    private static int DropFittingBodies(List<Axis> axes, Settings s, double tol)
    {
        // «Corto para su ancho»: hasta 3 anchos, pero nunca más que el ancho + 400 mm (un conducto de 900 mm y 2,6 m de
        // largo es un tramo real; el cuerpo de una pieza no pasa de ahí). Mismo límite que NetworkBuilder.StubLimit.
        static double Limit(double w) => NetworkBuilder.StubLimit(w);
        double parallelCos = Math.Cos(3 * Math.PI / 180);
        int n = axes.Count;
        var dropped = new bool[n];
        // Extensión «virtual» de cada tramo real a lo largo de su eje: crece al absorber un cuerpo, para alcanzar al siguiente.
        var lo = new double[n];
        var hi = new double[n];
        for (int i = 0; i < n; i++) { lo[i] = 0; hi[i] = axes[i].Len; }
        bool Real(int j) => !dropped[j] && axes[j].Len > Limit(axes[j].W);
        int count = 0;
        bool changed = true;
        while (changed)
        {
            changed = false;
            for (int i = 0; i < n; i++)
            {
                if (dropped[i]) continue;
                var x = axes[i];
                if (x.Len > Limit(x.W)) continue;
                for (int j = 0; j < n; j++)
                {
                    if (j == i || !Real(j)) continue;
                    var m = axes[j];
                    if (Math.Abs(m.W - x.W) <= 0.5 || m.Len <= x.Len) continue;
                    var d = m.Dir;
                    if (Math.Abs(d.Dot(x.Dir)) < parallelCos) continue;
                    // Posición del cuerpo en el eje del tramo: a lo largo (t) y de lado (lateral)
                    var vs = x.S - m.S; var ve = x.E - m.S;
                    double t0 = vs.Dot(d), t1 = ve.Dot(d);
                    if (t0 > t1) (t0, t1) = (t1, t0);
                    var mid = (x.S + x.E) * 0.5 - m.S;
                    double lateral = Math.Abs(mid.X * d.Y - mid.Y * d.X);
                    // El cuerpo de una te es excéntrico (tiene la boca del ramal a un lado): basta con que el contorno del
                    // tubo quede DENTRO del contorno del cuerpo. Si el cuerpo es más estrecho, casi centrado.
                    double inside = x.W > m.W ? (x.W - m.W) / 2 + tol : 0;
                    if (lateral > Math.Max(Math.Max(tol, 0.25 * Math.Min(m.W, x.W)), inside)) continue;
                    // Pegado al tramo (o encima de él): el hueco entre ambos no pasa de un cuarto del ancho del cuerpo
                    double gap = Math.Max(t0 - hi[j], lo[j] - t1);
                    if (gap > Math.Max(2 * tol, 0.25 * x.W)) continue;
                    dropped[i] = true;
                    lo[j] = Math.Min(lo[j], t0);
                    hi[j] = Math.Max(hi[j], t1);
                    count++;
                    changed = true;
                    break;
                }
            }
        }
        if (count > 0)
        {
            var keep = axes.Where((_, i) => !dropped[i]).ToList();
            axes.Clear();
            axes.AddRange(keep);
        }
        return count;
    }

    /// <summary>
    /// Un eje cuyo contorno (sus cuatro esquinas) queda entero dentro de los contornos de otros tramos MÁS LARGOS no es un
    /// tramo: es dibujo interior (una rejilla o un rótulo con líneas, la junta de una transición dibujada como dos líneas
    /// atravesando el conducto). Un ramal real sale fuera del contorno del tramo principal y no se toca.
    /// </summary>
    private static int DropContained(List<Axis> axes, double tol)
    {
        bool Inside(P3 p, Axis m)
        {
            var d = m.Dir; var v = p - m.S;
            double along = v.Dot(d), lateral = Math.Abs(v.X * d.Y - v.Y * d.X);
            return along >= -tol && along <= m.Len + tol && lateral <= m.W / 2 + tol;
        }
        var drop = new bool[axes.Count];
        for (int i = 0; i < axes.Count; i++)
        {
            var x = axes[i];
            var dir = x.Dir; var n = new P3(-dir.Y, dir.X, 0) * (x.W / 2);
            var corners = new[] { x.S + n, x.S - n, x.E + n, x.E - n, (x.S + x.E) * 0.5 };
            var bigger = axes.Where((m, j) => j != i && !drop[j] && m.Len > x.Len + tol).ToList();
            if (bigger.Count > 0 && corners.All(c => bigger.Any(m => Inside(c, m)))) drop[i] = true;
        }
        int count = drop.Count(d => d);
        if (count > 0)
        {
            var keep = axes.Where((_, i) => !drop[i]).ToList();
            axes.Clear(); axes.AddRange(keep);
        }
        return count;
    }

    /// <summary>
    /// Codo curvo dibujado con polilíneas de tramos rectos (sin arcos): sale una cadena de ejes CORTOS (más cortos que su
    /// ancho), del mismo ancho, cada uno girado unos grados respecto al siguiente. No son tramos: se quitan y los dos
    /// tramos rectos se prolongan hasta cortarse, como en un codo dibujado con arcos.
    /// </summary>
    private static int DropCurvePieces(List<Axis> axes, double tol)
    {
        double minTurn = Math.Cos(3 * Math.PI / 180), maxTurn = Math.Cos(50 * Math.PI / 180);
        bool Bent(Axis a, P3 end, out bool found)
        {
            found = false;
            double near = Math.Max(tol, 0.1 * a.W);
            foreach (var o in axes)
            {
                if (ReferenceEquals(o, a) || Math.Abs(o.W - a.W) > Math.Max(tol, 0.05 * a.W)) continue;
                if (o.S.DistanceTo(end) > near && o.E.DistanceTo(end) > near) continue;
                found = true;
                double cos = Math.Abs(o.Dir.Dot(a.Dir));
                if (cos < minTurn && cos > maxTurn) return true;
            }
            return false;
        }
        // Trocito OBLICUO junto a otros tramos (las caras inclinadas de una transición o reducción emparejadas entre sí):
        // corto para su ancho y torcido entre 5° y 60° respecto a todos los tramos que tiene a menos de su ancho.
        double oblLo = Math.Cos(5 * Math.PI / 180), oblHi = Math.Cos(60 * Math.PI / 180);
        bool Oblique(Axis a)
        {
            var mid = (a.S + a.E) * 0.5;
            var near = axes.Where(o => !ReferenceEquals(o, a) && o.Len > a.Len &&
                                       NetworkBuilder.ProjectToSegment(mid, o.S, o.E).Dist <= a.W + o.W / 2).ToList();
            return near.Count > 0 && near.All(o => { double c = Math.Abs(o.Dir.Dot(a.Dir)); return c < oblLo && c > oblHi; });
        }
        var drop = axes.Where(a => !a.FromRect && a.Len < a.W && (Bent(a, a.S, out _) && Bent(a, a.E, out _) || Oblique(a))).ToList();
        foreach (var a in drop) axes.Remove(a);
        return drop.Count;
    }

    private static double Gap(Axis a, Settings s) => Math.Min(s.MaxGapMm, s.GapFactor * a.W);

    /// <summary>Un arco tangente a un borde de cada tramo permite hallar la esquina de sus ejes.</summary>
    private static void CloseArcElbows(List<Axis> axes, List<CadSegment> segments, Func<P3, P3> local,
        Settings s, double tol, OutlineResult result)
    {
        static double Cross(P3 a, P3 b) => a.X * b.Y - a.Y * b.X;
        double parallel = Math.Cos(s.AngleToleranceDeg * Math.PI / 180);
        IEnumerable<(Axis Axis, bool AtStart, P3 End, P3 Dir, double Side)> TangentEnds(P3 edge, P3 tangent)
        {
            foreach (var a in axes)
            foreach (bool atStart in new[] { true, false })
            {
                if (atStart ? !a.FreeS : !a.FreeE) continue;
                var end = atStart ? a.S : a.E;
                var dir = atStart ? a.Dir * -1 : a.Dir;
                if (dir.Dot(tangent) < parallel) continue;
                var delta = edge - end;
                double lateral = Cross(dir, delta);
                if (Math.Abs(delta.Dot(dir)) <= tol && Math.Abs(Math.Abs(lateral) - a.W / 2) <= tol)
                    yield return (a, atStart, end, dir, lateral);
            }
        }

        foreach (var arc in segments.Where(c => c.ArcId != 0 && c.ArcCorner != null).GroupBy(c => c.ArcId))
        {
            var start = local(arc.First().Start);
            var end = local(arc.Last().End);
            var corner = local(arc.First().ArcCorner!.Value);
            var first = TangentEnds(start, (corner - start).Normalized()).ToList();
            var second = TangentEnds(end, (corner - end).Normalized()).ToList();
            if (first.Count != 1 || second.Count != 1) continue;
            var a = first[0];
            var b = second[0];
            double cross = Cross(a.Dir, b.Dir);
            if (a.Axis == b.Axis || a.Side * b.Side >= 0 || Math.Abs(cross) < 1e-9 ||
                Math.Abs(a.Axis.W - b.Axis.W) > tol) continue;
            var delta = b.End - a.End;
            double alongA = Cross(delta, b.Dir) / cross;
            double alongB = Cross(delta, a.Dir) / cross;
            if (alongA < -tol || alongB < -tol || alongA > s.MaxGapMm || alongB > s.MaxGapMm) continue;
            var intersection = a.End + a.Dir * alongA;
            if (a.AtStart) { a.Axis.S = intersection; a.Axis.FreeS = false; } else { a.Axis.E = intersection; a.Axis.FreeE = false; }
            if (b.AtStart) { b.Axis.S = intersection; b.Axis.FreeS = false; } else { b.Axis.E = intersection; b.Axis.FreeE = false; }
            result.GapsClosed += 2;
        }
    }

    /// <summary>Cuánto enlaza un eje con los demás: +2 por extremo alineado con otro tramo, +1 si corta a otro.</summary>
    private static int Score(Axis x, List<Axis> others, Settings s, double tol)
    {
        double parallelCos = Math.Cos(5 * Math.PI / 180);
        int score = 0;
        foreach (bool atS in new[] { true, false })
        {
            var p = atS ? x.S : x.E;
            var dir = atS ? x.Dir * -1 : x.Dir;
            double gap = Gap(x, s);
            int best = 0;
            foreach (var o in others)
            {
                var od = o.Dir;
                if (Math.Abs(dir.Dot(od)) > parallelCos)
                {
                    foreach (var q in new[] { o.S, o.E })
                    {
                        var v = q - p;
                        double along = v.Dot(dir), lateral = Math.Abs(v.X * dir.Y - v.Y * dir.X);
                        if (along > -tol && along <= gap && lateral <= Math.Max(tol, 0.05 * Math.Min(x.W, o.W))) best = 2;
                    }
                    continue;
                }
                double cross = dir.X * od.Y - dir.Y * od.X;
                var w = o.S - p;
                double sa = (w.X * od.Y - w.Y * od.X) / cross;
                double sb = (w.X * dir.Y - w.Y * dir.X) / cross;
                // El extremo debe llegar hasta el costado del otro tramo (la mitad de su ancho), dejando como
                // mucho el hueco de una pieza especial; ese hueco lo limita el más estrecho de los dos.
                // Sin este límite, un tramo leído "girado" (ancho = su largo, y por tanto un hueco admitido
                // muy grande) alcanzaba a un conducto paralelo lejano y se quedaba con la lectura equivocada.
                double reach = o.W / 2 + Math.Min(gap, Gap(o, s));
                if (sa >= -tol && sa <= reach && sb >= -Gap(o, s) && sb <= o.Len + Gap(o, s)) best = Math.Max(best, 1);
            }
            score += best;
        }
        return score;
    }

    /// <summary>
    /// Cruce de tuberías a distinta cota: el plano corta el tubo de debajo justo lo que tapa el de encima. Dos extremos
    /// libres alineados, enfrentados y del mismo ancho, con un hueco del ancho de otro eje que pasa de largo por en medio
    /// (sigue a los dos lados), son el MISMO tubo: se unen en un eje recto y no se unen al que pasa.
    /// </summary>
    private static void JoinAcrossCrossings(List<Axis> axes, double tol, OutlineResult result)
    {
        double parallelCos = Math.Cos(5 * Math.PI / 180);
        // Parejas de extremos libres alineados, enfrentados y del mismo ancho: el tramo cortado a ambos lados de un hueco.
        var aligned = new List<(double Gap, int A, bool AS, int B, bool BS, P3 Pa, P3 Pb, P3 Dir, double W)>();
        for (int i = 0; i < axes.Count; i++)
        for (int j = i + 1; j < axes.Count; j++)
        foreach (bool aS in new[] { true, false })
        foreach (bool bS in new[] { true, false })
        {
            Axis a = axes[i], b = axes[j];
            if ((aS ? !a.FreeS : !a.FreeE) || (bS ? !b.FreeS : !b.FreeE)) continue;
            if (Math.Abs(a.W - b.W) > Math.Max(tol, 0.1 * Math.Min(a.W, b.W))) continue;
            var pa = aS ? a.S : a.E; var pb = bS ? b.S : b.E;
            var da = aS ? a.Dir * -1 : a.Dir; var dbo = bS ? b.Dir * -1 : b.Dir;
            if (da.Dot(dbo) > -parallelCos) continue;
            var v = pb - pa;
            double gap = v.Dot(da);
            double lateral = Math.Abs(v.X * da.Y - v.Y * da.X);
            if (gap <= tol || gap > 400 || lateral > Math.Max(tol, 0.05 * Math.Min(a.W, b.W))) continue;
            aligned.Add((gap, i, aS, j, bS, pa, pb, da, (a.W + b.W) / 2));
        }
        double Room(double w) => w + Math.Max(4 * tol, 0.5 * w); // lo que puede tapar un tubo de ese ancho (con su junta)
        var pairs = new List<(double Gap, int A, bool AS, int B, bool BS, P3 X)>();
        for (int n = 0; n < aligned.Count; n++)
        {
            var (gap, i, aS, j, bS, pa, _, da, wAB) = aligned[n];
            bool found = false;
            // a) Otro eje pasa de largo por el hueco (sigue a los dos lados) y su ancho es lo que mide el hueco.
            for (int c = 0; c < axes.Count && !found; c++)
            {
                if (c == i || c == j) continue;
                var x = axes[c];
                var dx = x.Dir;
                double cross = da.X * dx.Y - da.Y * dx.X;
                if (Math.Abs(da.Dot(dx)) > parallelCos || Math.Abs(cross) < 1e-9) continue;
                var w = x.S - pa;
                double ta = (w.X * dx.Y - w.Y * dx.X) / cross;   // dentro del hueco, desde el extremo a
                double tx = (w.X * da.Y - w.Y * da.X) / cross;   // a lo largo del que pasa, desde su inicio
                if (ta < -tol || ta > gap + tol) continue;
                // Pasa de largo: no termina en el hueco (si terminara sería un ramal, no un cruce).
                double margin = x.W / 2 + tol;
                if (tx < margin || tx > x.Len - margin) continue;
                if (gap < x.W - 2 * tol || gap > Room(x.W)) continue;
                pairs.Add((gap, i, aS, j, bS, pa + da * ta));
                found = true;
            }
            // b) Corte mutuo: el otro tubo también está cortado justo ahí (sus dos huecos se cruzan). Se unen los dos.
            for (int m = 0; m < aligned.Count && !found; m++)
            {
                if (m == n) continue;
                var o = aligned[m];
                if (o.A == i || o.A == j || o.B == i || o.B == j) continue;
                double cross = da.X * o.Dir.Y - da.Y * o.Dir.X;
                if (Math.Abs(da.Dot(o.Dir)) > parallelCos || Math.Abs(cross) < 1e-9) continue;
                var w = o.Pa - pa;
                double ta = (w.X * o.Dir.Y - w.Y * o.Dir.X) / cross;
                double to = (w.X * da.Y - w.Y * da.X) / cross;
                if (ta < -tol || ta > gap + tol || to < -tol || to > o.Gap + tol) continue;
                if (gap > Room(o.W) || o.Gap > Room(wAB)) continue;
                pairs.Add((gap, i, aS, j, bS, pa + da * ta));
                found = true;
            }
        }
        var dead = new HashSet<int>();
        foreach (var (_, i, aS, j, bS, point) in pairs.OrderBy(p => p.Gap))
        {
            if (dead.Contains(i) || dead.Contains(j)) continue;
            Axis a = axes[i], b = axes[j];
            if ((aS ? !a.FreeS : !a.FreeE) || (bS ? !b.FreeS : !b.FreeE)) continue;
            var farA = aS ? a.E : a.S; bool farAFree = aS ? a.FreeE : a.FreeS;
            var farB = bS ? b.E : b.S; bool farBFree = bS ? b.FreeE : b.FreeS;
            a.S = farA; a.FreeS = farAFree;
            a.E = farB; a.FreeE = farBFree;
            a.W = (a.W + b.W) / 2; a.Z = (a.Z + b.Z) / 2;
            dead.Add(j);
            result.CrossingsJoined.Add(point);
        }
        foreach (int d in dead.OrderByDescending(d => d)) axes.RemoveAt(d);
    }

    /// <summary>
    /// Prolonga los extremos libres hasta donde se cortan con otro eje (codo, te, cruz)
    /// o con el extremo alineado de enfrente (transición, accesorio en línea).
    /// </summary>
    private static void CloseGaps(List<Axis> axes, Settings s, double tol, OutlineResult result)
    {
        double parallelCos = Math.Cos(5 * Math.PI / 180);
        if (s.PipeCrossings) JoinAcrossCrossings(axes, tol, result);
        var candidates = new List<(double Dist, int Axis, bool AtS, P3 P)>();
        for (int i = 0; i < axes.Count; i++)
        {
            foreach (bool atS in new[] { true, false })
            {
                if (atS ? !axes[i].FreeS : !axes[i].FreeE) continue;
                var a = axes[i];
                // Tuberías: un trozo más corto que 3/4 de su ancho es el cuerpo de una pieza dibujada con líneas (una te, una
                // válvula) leído de través, no un tubo: no sale a buscar uniones por sí mismo (se alargaba hasta otro tubo
                // que pasa al lado y salían una cruz y una te inexistentes). Los demás sí pueden llegar hasta él.
                if (s.PipeCrossings && a.Len < 0.75 * a.W) continue;
                var p = atS ? a.S : a.E;
                var dir = atS ? a.Dir * -1 : a.Dir;
                double gap = Gap(a, s);
                for (int j = 0; j < axes.Count; j++)
                {
                    if (j == i) continue;
                    var b = axes[j];
                    var db = b.Dir;
                    double cross = dir.X * db.Y - dir.Y * db.X;
                    if (Math.Abs(dir.Dot(db)) > parallelCos || Math.Abs(cross) < 1e-9) continue;
                    // p + dir·sa = b.S + db·sb
                    var w = b.S - p;
                    double sa = (w.X * db.Y - w.Y * db.X) / cross;
                    double sb = (w.X * dir.Y - w.Y * dir.X) / cross;
                    // Hueco admitido: el propio (pieza especial) o, si el otro es más ancho, hasta su eje
                    // (ramal que termina en la cara de un conducto o muro más grueso).
                    if (sa < -tol || sa > Math.Max(gap, b.W / 2 + tol)) continue;
                    double before = b.FreeS ? Gap(b, s) : tol, after = b.FreeE ? Gap(b, s) : tol;
                    if (sb < -before || sb > b.Len + after) continue;
                    candidates.Add((sa, i, atS, p + dir * sa));
                }
            }
        }

        // Distancia a la esquina más cercana de cada extremo libre: no se le arrastra hasta otra más lejana.
        var nearest = new Dictionary<(int, bool), double>();
        foreach (var (dist, i, atS, _) in candidates)
            if (!nearest.TryGetValue((i, atS), out var d0) || dist < d0) nearest[(i, atS)] = dist;

        foreach (var (_, i, atS, pt) in candidates.OrderBy(c => c.Dist))
        {
            if (atS ? !axes[i].FreeS : !axes[i].FreeE) continue;
            // El extremo que originó el candidato se prolonga siempre (su hueco ya se comprobó)
            {
                var own = axes[i];
                var p0 = atS ? own.S : own.E;
                var d0 = atS ? own.Dir * -1 : own.Dir;
                var target0 = p0 + d0 * (pt - p0).Dot(d0);
                if (atS) { own.S = target0; own.FreeS = false; } else { own.E = target0; own.FreeE = false; }
                result.GapsClosed++;
            }
            // Todos los demás extremos libres cuya recta pasa por el punto se prolongan hasta él
            for (int j = 0; j < axes.Count; j++)
            {
                var c = axes[j];
                foreach (bool cs in new[] { true, false })
                {
                    if (cs ? !c.FreeS : !c.FreeE) continue;
                    var p = cs ? c.S : c.E;
                    var dir = cs ? c.Dir * -1 : c.Dir;
                    var v = pt - p;
                    double along = v.Dot(dir);
                    double lateral = Math.Abs(v.X * dir.Y - v.Y * dir.X);
                    if (along < -tol || along > Gap(c, s) || lateral > Math.Max(tol, 0.05 * c.W)) continue;
                    if (s.PipeCrossings && nearest.TryGetValue((j, cs), out var own) && own < along - tol) continue;
                    var target = p + dir * along; // se mantiene sobre su propio eje
                    if (cs) { c.S = target; c.FreeS = false; } else { c.E = target; c.FreeE = false; }
                    result.GapsClosed++;
                }
            }
        }

        // Extremos alineados enfrentados (transición o accesorio en línea): se unen en el punto medio.
        var pairs = new List<(double Dist, int A, bool AS, int B, bool BS)>();
        for (int i = 0; i < axes.Count; i++)
        for (int j = i + 1; j < axes.Count; j++)
        foreach (bool aS in new[] { true, false })
        foreach (bool bS in new[] { true, false })
        {
            Axis a = axes[i], b = axes[j];
            if ((aS ? !a.FreeS : !a.FreeE) || (bS ? !b.FreeS : !b.FreeE)) continue;
            var pa = aS ? a.S : a.E; var pb = bS ? b.S : b.E;
            var da = aS ? a.Dir * -1 : a.Dir; var dbo = bS ? b.Dir * -1 : b.Dir;
            if (da.Dot(dbo) > -parallelCos) continue;
            var v = pb - pa;
            double along = v.Dot(da);
            double lateral = Math.Abs(v.X * da.Y - v.Y * da.X);
            // Desvío lateral admitido: casi centrados o, si los anchos son distintos, hasta la mitad de la diferencia
            // (reducción EXCÉNTRICA, con una cara enrasada: el eje se desplaza justo eso).
            double eccentric = s.DropFittingBodies && Math.Abs(a.W - b.W) > 0.5 ? Math.Abs(a.W - b.W) / 2 + tol : 0; // en muros no
            if (along <= tol || along > Math.Min(Gap(a, s), Gap(b, s)) ||
                lateral > Math.Max(Math.Max(tol, 0.05 * Math.Min(a.W, b.W)), eccentric)) continue;
            pairs.Add((along, i, aS, j, bS));
        }
        foreach (var (_, i, aS0, j, bS0) in pairs.OrderBy(p => p.Dist))
        {
            Axis a = axes[i], b = axes[j];
            bool aS = aS0, bS = bS0;
            if ((aS ? !a.FreeS : !a.FreeE) || (bS ? !b.FreeS : !b.FreeE)) continue;
            // El punto de unión se toma sobre el eje del tramo MÁS LARGO (se queda recto); el otro se desvía lo mínimo.
            if (b.Len > a.Len) { (a, b) = (b, a); (aS, bS) = (bS, aS); }
            var pa = aS ? a.S : a.E; var pb = bS ? b.S : b.E;
            var da = aS ? a.Dir * -1 : a.Dir;
            var mid = pa + da * ((pb - pa).Dot(da) * 0.5);
            if (aS) { a.S = mid; a.FreeS = false; } else { a.E = mid; a.FreeE = false; }
            if (bS) { b.S = mid; b.FreeS = false; } else { b.E = mid; b.FreeE = false; }
            result.GapsClosed += 2;
        }
    }
}
