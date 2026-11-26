using System.Drawing;
using System.Drawing.Drawing2D;
using System.Text;
using System.Windows.Forms;
using CadMepRevit.Core;
using CadMepRevit.Revit;

namespace CadMepRevit.Ui;

/// <summary>
/// Paso 3 del asistente: muestra lo que se va a construir. No modifica el modelo.
/// 'Construir en Revit…' devuelve OK; la confirmación final se pide después.
/// </summary>
public sealed class PreviewForm : Form
{
    private readonly BuildPlan _plan;
    private readonly List<string> _readWarnings;
    private readonly List<(P3 A, P3 B)> _context = new();
    private readonly Canvas _canvas = new() { Dock = DockStyle.Fill, BackColor = System.Drawing.Color.White };
    private double _scale = 1, _panX, _panY;
    private Point _dragStart;
    private bool _dragging;
    private RectangleF _bounds;

    private static readonly Dictionary<MepKind, System.Drawing.Color> KindColors = new()
    {
        [MepKind.Tuberia] = System.Drawing.Color.RoyalBlue,
        [MepKind.Conducto] = System.Drawing.Color.SeaGreen,
        [MepKind.Bandeja] = System.Drawing.Color.DarkOrange,
        [MepKind.TuboElectrico] = System.Drawing.Color.MediumPurple,
        [MepKind.Muro] = System.Drawing.Color.DimGray,
    };

    public PreviewForm(BuildPlan plan, CadDrawing drawing, double unitToMm)
    {
        _plan = plan;
        _readWarnings = drawing.Warnings.ToList();
        Text = "CAD-REV · Paso 3 de 3: previsualización (todavía no se ha creado nada)";
        Width = 1250; Height = 820;
        MinimumSize = new Size(900, 600);
        StartPosition = FormStartPosition.CenterParent;
        Font = new System.Drawing.Font("Segoe UI", 9F);

        // Capas no importadas (y el contorno original de las capas a doble línea), en gris, como referencia
        foreach (var s in drawing.Segments.Where(s => !plan.Layers.TryGetValue(s.Layer, out var lp) || lp.DoubleLine))
            _context.Add((new P3(s.Start.X * unitToMm, s.Start.Y * unitToMm, 0) + plan.TranslationMm,
                          new P3(s.End.X * unitToMm, s.End.Y * unitToMm, 0) + plan.TranslationMm));

        var split = new SplitContainer { Dock = DockStyle.Fill, FixedPanel = FixedPanel.Panel2 };
        Controls.Add(split);
        split.Panel1.Controls.Add(_canvas);
        var hint = new Label
        {
            Dock = DockStyle.Top, Height = 22, BackColor = System.Drawing.Color.FromArgb(240, 244, 250),
            Text = "  Rueda del ratón: zoom · Arrastrar: mover · Doble clic: encuadrar todo"
        };
        split.Panel1.Controls.Add(hint);

        var right = new TableLayoutPanel { Dock = DockStyle.Fill, RowCount = 3, ColumnCount = 1, Padding = new Padding(6) };
        right.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        right.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        right.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        right.Controls.Add(BuildLegend(), 0, 0);
        var summary = new TextBox
        {
            Multiline = true, ReadOnly = true, ScrollBars = ScrollBars.Vertical, Dock = DockStyle.Fill,
            Text = BuildSummary(), BackColor = SystemColors.Window
        };
        right.Controls.Add(summary, 0, 1);
        var buttons = new FlowLayoutPanel { FlowDirection = FlowDirection.RightToLeft, AutoSize = true, Dock = DockStyle.Fill };
        var build = new Button { Text = "Construir en Revit…", AutoSize = true, DialogResult = DialogResult.OK, Font = new System.Drawing.Font(Font, FontStyle.Bold) };
        var back = new Button { Text = "Volver y cambiar", AutoSize = true, DialogResult = DialogResult.Cancel };
        buttons.Controls.AddRange(new Control[] { build, back });
        right.Controls.Add(buttons, 0, 2);
        split.Panel2.Controls.Add(right);
        Shown += (_, _) => { split.SplitterDistance = Math.Max(300, split.Width - 400); Fit(); };
        CancelButton = back;

        _canvas.Paint += (_, e) => Draw(e.Graphics);
        _canvas.Resize += (_, _) => _canvas.Invalidate();
        _canvas.MouseWheel += (_, e) => Zoom(e.Delta > 0 ? 1.25 : 0.8, e.Location);
        _canvas.MouseDown += (_, e) => { _dragging = true; _dragStart = e.Location; _canvas.Focus(); };
        _canvas.MouseUp += (_, _) => _dragging = false;
        _canvas.MouseMove += (_, e) =>
        {
            if (!_dragging) return;
            _panX += e.X - _dragStart.X; _panY += e.Y - _dragStart.Y;
            _dragStart = e.Location;
            _canvas.Invalidate();
        };
        _canvas.DoubleClick += (_, _) => Fit();
        ComputeBounds();
    }

    private Control BuildLegend()
    {
        var p = new FlowLayoutPanel { AutoSize = true, FlowDirection = FlowDirection.TopDown, Dock = DockStyle.Fill };
        void Item(System.Drawing.Color c, string text)
        {
            p.Controls.Add(new Label { Text = "■ " + text, ForeColor = c, AutoSize = true, Font = new System.Drawing.Font(Font, FontStyle.Bold) });
        }
        foreach (var kv in KindColors) Item(kv.Value, MepKinds.Label(kv.Key));
        Item(System.Drawing.Color.LimeGreen, "Codo");
        Item(System.Drawing.Color.DeepSkyBlue, "Te");
        Item(System.Drawing.Color.Magenta, "Cruz");
        Item(System.Drawing.Color.Goldenrod, "Transición (cambio de tamaño)");
        Item(System.Drawing.Color.Red, "Unión que NO se podrá crear (revísela)");
        Item(System.Drawing.Color.SaddleBrown, "Familia en posición de bloque");
        Item(System.Drawing.Color.Chocolate, "Marcador 3D sin familia (huella del CAD)");
        Item(System.Drawing.Color.Silver, "Capas no importadas (solo referencia)");
        return p;
    }

    private string BuildSummary()
    {
        var n = _plan.Network;
        var sb = new StringBuilder();
        sb.AppendLine("RESUMEN DE LO QUE SE VA A CREAR");
        sb.AppendLine($"Nivel: {_plan.LevelName} · altura sobre el nivel: {_plan.HeightAboveLevelMm:0} mm");
        sb.AppendLine();
        foreach (var kind in MepKinds.All.Skip(1))
        {
            int c = _plan.Count(kind);
            if (c == 0) continue;
            double len = n.Segments.Where(s => _plan.Layers[s.Layer].Kind == kind).Sum(s => s.Length) / 1000.0;
            sb.AppendLine($"{MepKinds.Label(kind)}: {c} tramos, {len:0.0} m");
        }
        if (_plan.CreateFittings)
            sb.AppendLine($"Uniones: {n.Count(JunctionKind.Codo)} codos, {n.Count(JunctionKind.Te)} tes, {n.Count(JunctionKind.Cruz)} cruces, {n.Count(JunctionKind.Transicion)} transiciones");
        else
            sb.AppendLine("Uniones: desactivadas (los tramos quedarán sin conectar)");
        if (_plan.BlockCount > 0) sb.AppendLine($"Familias en bloques: {_plan.BlockCount}");
        if (_plan.MarkerCount > 0)
        {
            sb.AppendLine($"Marcadores 3D sin familia: {_plan.MarkerCount}");
            foreach (var g in _plan.Blocks.Where(b => _plan.BlockPlans.Any(p => BuildPlan.Same(p, b.Block) && p.Action == BlockAction.Marcador))
                         .GroupBy(b => BlockNames.Clean(b.Block.BlockName)))
            {
                var bp = _plan.BlockPlans.First(p => BuildPlan.Same(p, g.First().Block));
                var f = g.First().FootprintMm;
                sb.AppendLine($"  • {g.Key} ×{g.Count()}: {f?.SizeX:0}×{f?.SizeY:0} mm, alto {bp.HeightMm:0} mm, a {bp.ElevationMm:0} mm");
            }
            sb.AppendLine("  (Un marcador ocupa su sitio real pero no tiene conectores: sustitúyalo por la familia cuando la tenga.)");
        }
        sb.AppendLine();
        sb.AppendLine("POR CAPA");
        foreach (var lp in _plan.Layers.Values)
        {
            string size = lp.HeightMm > 0 ? $"{lp.SizeMm:0.#}×{lp.HeightMm:0.#} mm" : $"Ø {lp.SizeMm:0.#} mm";
            if (lp.DoubleLine)
            {
                var widths = n.Segments.Where(s => s.Layer == lp.Layer && s.WidthMm.HasValue).Select(s => s.WidthMm!.Value).Distinct().OrderBy(w => w).ToList();
                size = "ancho medido en el CAD: " + (widths.Count == 0 ? "—" : string.Join(", ", widths.Take(8).Select(w => w.ToString("0")))) + " mm" +
                       (lp.HeightMm > 0 ? $" × alto {lp.HeightMm:0} mm" : "");
            }
            sb.AppendLine($"• {lp.Layer} → {MepKinds.Label(lp.Kind)}, {size}, {n.Segments.Count(s => s.Layer == lp.Layer)} tramos");
        }
        sb.AppendLine();
        foreach (var (layer, o) in _plan.Outlines)
        {
            sb.AppendLine($"DOBLE LÍNEA · {layer}");
            sb.AppendLine($"• {o.InputLines} líneas → {o.Centerlines.Count} ejes; {o.Caps} testas de cierre ignoradas");
            sb.AppendLine($"• Huecos de piezas especiales cerrados: {o.GapsClosed}");
            if (o.Reoriented > 0) sb.AppendLine($"• Rectángulos ambiguos leídos según sus vecinos: {o.Reoriented}");
            if (o.UnpairedLines > 0) sb.AppendLine($"• Líneas sin pareja (no se construyen): {o.UnpairedLines}");
            if (o.DiscardedStubs > 0) sb.AppendLine($"• Piezas demasiado cortas descartadas: {o.DiscardedStubs}");
            if (o.PairedFraction < 0.6) sb.AppendLine("  ATENCIÓN: poca parte de la capa forma parejas de líneas; quizá no esté dibujada a doble línea.");
            sb.AppendLine();
        }
        sb.AppendLine("LIMPIEZA DEL DIBUJO");
        sb.AppendLine($"• Tramos cortos descartados: {n.RemovedShort}");
        sb.AppendLine($"• Duplicados eliminados: {n.RemovedDuplicates}");
        if (n.MergedOverlaps > 0) sb.AppendLine($"• Tramos que se pisaban (una línea sobre otra), unidos en uno: {n.MergedOverlaps}");
        if (n.FilletsCollapsed > 0) sb.AppendLine($"• Curvas que hacían de codo, convertidas en esquina: {n.FilletsCollapsed}");
        sb.AppendLine($"• Tramos alineados unidos: {n.MergedCollinear}");
        sb.AppendLine($"• Tramos partidos para crear tes: {n.SplitAtTee}");
        if (_plan.TranslationMm.Length > 0)
            sb.AppendLine($"• Dibujo desplazado X={_plan.TranslationMm.X / 1000:0.###} m, Y={_plan.TranslationMm.Y / 1000:0.###} m");
        foreach (var w in _plan.Warnings)
        {
            sb.AppendLine();
            sb.AppendLine("ATENCIÓN · " + w);
        }
        int bad = n.Count(JunctionKind.NoAdmitida);
        if (bad > 0)
        {
            sb.AppendLine();
            sb.AppendLine($"ATENCIÓN: {bad} uniones no se podrán crear (en rojo):");
            foreach (var j in n.Junctions.Where(j => j.Kind == JunctionKind.NoAdmitida).Take(15))
                sb.AppendLine($"  - {j.Layer} en {j.Point}: {j.Note}");
            sb.AppendLine("Puede construir igualmente y unirlas a mano después, o corregir el CAD.");
        }
        if (_readWarnings.Count > 0)
        {
            sb.AppendLine();
            sb.AppendLine("AVISOS DE LECTURA DEL CAD");
            foreach (var w in _readWarnings) sb.AppendLine("• " + w);
        }
        sb.AppendLine();
        sb.AppendLine("Al pulsar 'Construir en Revit…' se le pedirá una última confirmación. Todo se hace en una sola operación que puede deshacer con Ctrl+Z.");
        return sb.ToString().Replace("\n", Environment.NewLine).Replace("\r\r", "\r");
    }

    // ------------------------------------------------------------ Dibujo

    private void ComputeBounds()
    {
        var pts = _plan.Network.Segments.SelectMany(s => new[] { s.Start, s.End })
            .Concat(_plan.Blocks.Where(b => b.FootprintMm != null).SelectMany(b => b.FootprintMm!.Corners().Select(c => new P3(c.X, c.Y, 0))))
            .Concat(_context.SelectMany(c => new[] { c.A, c.B })).ToList();
        if (pts.Count == 0) { _bounds = new RectangleF(0, 0, 1000, 1000); return; }
        double minX = pts.Min(p => p.X), maxX = pts.Max(p => p.X), minY = pts.Min(p => p.Y), maxY = pts.Max(p => p.Y);
        _bounds = new RectangleF((float)minX, (float)minY, (float)Math.Max(1, maxX - minX), (float)Math.Max(1, maxY - minY));
    }

    private void Fit()
    {
        double w = Math.Max(50, _canvas.Width - 40), h = Math.Max(50, _canvas.Height - 40);
        _scale = Math.Min(w / _bounds.Width, h / _bounds.Height);
        _panX = 20 + (w - _bounds.Width * _scale) / 2;
        _panY = 20 + (h - _bounds.Height * _scale) / 2;
        _canvas.Invalidate();
    }

    private void Zoom(double factor, Point at)
    {
        // Mantiene fijo el punto bajo el ratón
        _panX = at.X - (at.X - _panX) * factor;
        _panY = at.Y - (at.Y - _panY) * factor;
        _scale *= factor;
        _canvas.Invalidate();
    }

    private PointF Screen(P3 p) => new(
        (float)(_panX + (p.X - _bounds.Left) * _scale),
        (float)(_panY + (_bounds.Bottom - p.Y) * _scale));

    private void Draw(Graphics g)
    {
        g.SmoothingMode = SmoothingMode.AntiAlias;
        using (var pen = new Pen(System.Drawing.Color.Gainsboro, 1))
            foreach (var (a, b) in _context) g.DrawLine(pen, Screen(a), Screen(b));

        foreach (var s in _plan.Network.Segments)
        {
            var kind = _plan.Layers[s.Layer].Kind;
            var color = KindColors.GetValueOrDefault(kind, System.Drawing.Color.Black);
            if (s.WidthMm is double w && w * _scale >= 3)
            {
                // Banda con el ancho real para compararla con el contorno gris del CAD
                using var band = new Pen(System.Drawing.Color.FromArgb(60, color), (float)(w * _scale)) { StartCap = LineCap.Flat, EndCap = LineCap.Flat };
                g.DrawLine(band, Screen(s.Start), Screen(s.End));
            }
            using var pen = new Pen(color, 2.5f);
            g.DrawLine(pen, Screen(s.Start), Screen(s.End));
        }

        if (_plan.CreateFittings)
        {
            foreach (var j in _plan.Network.Junctions)
            {
                var c = j.Kind switch
                {
                    JunctionKind.Codo => System.Drawing.Color.LimeGreen,
                    JunctionKind.Te => System.Drawing.Color.DeepSkyBlue,
                    JunctionKind.Cruz => System.Drawing.Color.Magenta,
                    JunctionKind.Transicion => System.Drawing.Color.Goldenrod,
                    _ => System.Drawing.Color.Red
                };
                var p = Screen(j.Point);
                float r = j.Kind == JunctionKind.NoAdmitida ? 7 : 4;
                using var b = new SolidBrush(c);
                g.FillEllipse(b, p.X - r, p.Y - r, 2 * r, 2 * r);
            }
        }

        foreach (var (block, pos, foot) in _plan.Blocks)
        {
            var bp = _plan.BlockPlans.FirstOrDefault(p => BuildPlan.Same(p, block));
            bool marker = bp?.Action == BlockAction.Marcador;
            var color = marker ? System.Drawing.Color.Chocolate : System.Drawing.Color.SaddleBrown;
            if (foot != null)
            {
                var poly = foot.Corners().Select(c => Screen(new P3(c.X, c.Y, 0))).ToArray();
                using var fill = new SolidBrush(System.Drawing.Color.FromArgb(70, color));
                using var edge = new Pen(color, 1.5f) { DashStyle = marker ? DashStyle.Dash : DashStyle.Solid };
                g.FillPolygon(fill, poly);
                g.DrawPolygon(edge, poly);
            }
            else
            {
                var p = Screen(pos);
                using var brush = new SolidBrush(color);
                g.FillRectangle(brush, p.X - 4, p.Y - 4, 8, 8);
            }
        }
    }

    /// <summary>Panel con doble búfer para que el dibujo no parpadee.</summary>
    private sealed class Canvas : Panel
    {
        public Canvas()
        {
            DoubleBuffered = true;
            SetStyle(ControlStyles.Selectable, true);
            TabStop = true;
        }
    }
}
