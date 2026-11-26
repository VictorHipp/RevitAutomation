#nullable enable
using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Globalization;
using System.Linq;
using System.Windows.Forms;

namespace RevitAutomation.Addin.Commands.Patinillo;

public sealed class PatinilloPreviewForm : Form
{
    private static readonly CultureInfo Spanish = CultureInfo.GetCultureInfo("es-ES");
    private readonly ListView _list = new() { Dock = DockStyle.Fill, View = View.Details, FullRowSelect = true,
        MultiSelect = false, HideSelection = false, GridLines = true, ShowItemToolTips = true };
    private readonly ShaftPanel? _shaft;

    public PatinilloPreviewForm(PatinilloPlan plan)
    {
        ArgumentNullException.ThrowIfNull(plan);
        Text = "Patinillo de fontanería · Vista previa";
        Font = new Font("Segoe UI", 9F);
        AutoScaleDimensions = new SizeF(96, 96);
        AutoScaleMode = AutoScaleMode.Dpi;
        ClientSize = new Size(1160, 640);
        StartPosition = FormStartPosition.CenterScreen;
        MinimizeBox = false;
        Padding = new Padding(12);
        var root = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 3 };
        root.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        Controls.Add(root);
        int floors = plan.Pipes.Select(p => p.FloorIndex).Distinct().Count();
        var summary = new TextBox { ReadOnly = true, Multiline = true, ScrollBars = ScrollBars.Vertical,
            Dock = DockStyle.Fill, Height = plan.Notes.Count == 0 ? 44 : 90, BorderStyle = BorderStyle.None,
            BackColor = SystemColors.Control,
            Text = $"{plan.Pipes.Count} tuberías · {floors} plantas · matriz {plan.Rows} filas × {plan.Columns} columnas · separación entre ejes {Mm(plan.PitchMm)} mm"
                + (plan.Notes.Count == 0 ? "" : Environment.NewLine + string.Join(Environment.NewLine, plan.Notes)) };
        root.Controls.Add(summary, 0, 0);
        var buttons = new FlowLayoutPanel { AutoSize = true, Dock = DockStyle.Fill,
            FlowDirection = FlowDirection.RightToLeft, WrapContents = false, Margin = new Padding(0, 10, 0, 0) };
        root.Controls.Add(buttons, 0, 2);
        if (!plan.Fits)
        {
            var failure = new Panel { Dock = DockStyle.Fill, BackColor = Color.MistyRose, Padding = new Padding(16) };
            failure.Controls.Add(new TextBox { Dock = DockStyle.Fill, Multiline = true, ReadOnly = true,
                BorderStyle = BorderStyle.None, BackColor = Color.MistyRose, ForeColor = Color.DarkRed,
                ScrollBars = ScrollBars.Vertical, Text = plan.Message + Environment.NewLine + Environment.NewLine
                    + $"Tamaño mínimo necesario: {Mm(plan.MinWidthMm)} × {Mm(plan.MinDepthMm)} mm" });
            root.Controls.Add(failure, 0, 1);
            var close = new Button { Text = "Cerrar", AutoSize = true, DialogResult = DialogResult.Cancel };
            buttons.Controls.Add(close);
            AcceptButton = CancelButton = close;
            return;
        }

        var columns = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, RowCount = 1 };
        columns.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 43));
        columns.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 57));
        columns.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        root.Controls.Add(columns, 0, 1);
        var floorColours = plan.Placements.Select(p => p.Pipe.FloorIndex).Distinct().OrderBy(i => i)
            .Select((floor, index) => (floor, colour: FloorColour(index)))
            .ToDictionary(p => p.floor, p => p.colour);
        _shaft = new ShaftPanel(plan, floorColours) { Dock = DockStyle.Fill, Margin = new Padding(0, 0, 10, 0) };
        columns.Controls.Add(_shaft, 0, 0);
        foreach (var col in new[] { ("Tubería", 100), ("Sistema", 145), ("Ø", 60), ("Planta de salida", 110),
            ("Fila", 45), ("Columna", 65), ("Cota salida (m)", 100), ("Escalón", 60) })
            _list.Columns.Add(col.Item1, col.Item2);
        for (int i = 0; i < plan.Placements.Count; i++)
        {
            var p = plan.Placements[i];
            var item = new ListViewItem(new[] { p.Pipe.Id, Service(p.Pipe.Service), Mm(p.Pipe.NominalMm) + " mm",
                p.Pipe.FloorName, (p.Row + 1).ToString(Spanish), (p.Column + 1).ToString(Spanish),
                (p.ExitElevationMm / 1000).ToString("0.00", Spanish), p.Tier.ToString(Spanish) });
            item.Tag = i;
            item.ToolTipText = string.Join(" · ", item.SubItems.Cast<ListViewItem.ListViewSubItem>().Select(s => s.Text));
            _list.Items.Add(item);
        }
        columns.Controls.Add(_list, 1, 0);
        _list.SelectedIndexChanged += (_, _) => _shaft.SelectedIndex = _list.SelectedIndices.Count == 0 ? -1 : _list.SelectedIndices[0];
        _shaft.PipeSelected += index =>
        {
            if (_list.SelectedIndices.Count > 0) _list.Items[_list.SelectedIndices[0]].Selected = false;
            if (index < 0) return;
            _list.Items[index].Selected = true;
            _list.Items[index].Focused = true;
            _list.EnsureVisible(index);
        };
        var confirm = new Button { Text = "Confirmar y modelar", AutoSize = true, DialogResult = DialogResult.OK };
        var cancel = new Button { Text = "Cancelar", AutoSize = true, DialogResult = DialogResult.Cancel };
        buttons.Controls.AddRange(new Control[] { confirm, cancel });
        AcceptButton = confirm;
        CancelButton = cancel;
    }

    protected override void OnLoad(EventArgs e)
    {
        base.OnLoad(e);
        var area = Screen.FromControl(this).WorkingArea;
        Size = new Size(Math.Min(Width, area.Width - 24), Math.Min(Height, area.Height - 24));
        Location = new Point(area.Left + (area.Width - Width) / 2, area.Top + (area.Height - Height) / 2);
    }
    private static string Mm(double value) => value.ToString("0.##", Spanish);
    private static string Service(PatinilloService service) => service switch
    {
        PatinilloService.AguaFria => "Agua fría",
        PatinilloService.Acs => "Agua caliente (ACS)",
        PatinilloService.RetornoAcs => "Retorno ACS",
        _ => service.ToString()
    };
    private static string Code(PatinilloService service) => service switch
    {
        PatinilloService.AguaFria => "AF", PatinilloService.Acs => "ACS", PatinilloService.RetornoAcs => "RET", _ => "?"
    };
    private static Color FloorColour(int index)
    {
        // Golden-angle hues avoid repeating a small fixed palette in taller buildings.
        double hue = (index * 137.508 + 20) % 360 / 60;
        double x = 1 - Math.Abs(hue % 2 - 1);
        (double r, double g, double b) = hue switch
        {
            < 1 => (1.0, x, 0.0), < 2 => (x, 1.0, 0.0), < 3 => (0.0, 1.0, x),
            < 4 => (0.0, x, 1.0), < 5 => (x, 0.0, 1.0), _ => (1.0, 0.0, x)
        };
        return Color.FromArgb((int)(255 * (0.48 + 0.40 * r)), (int)(255 * (0.48 + 0.40 * g)), (int)(255 * (0.48 + 0.40 * b)));
    }

    private sealed class ShaftPanel : Panel
    {
        private readonly PatinilloPlan _plan;
        private readonly IReadOnlyDictionary<int, Color> _colours;
        private readonly ToolTip _tips = new() { AutoPopDelay = 20000 };
        private readonly List<(RectangleF Bounds, int Index)> _circles = new();
        private readonly FlowLayoutPanel _legend;
        private int _selected = -1, _hover = -1;
        public event Action<int>? PipeSelected;
        [System.ComponentModel.Browsable(false)]
        [System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)]
        public int SelectedIndex
        {
            get => _selected;
            set { if (_selected != value) { _selected = value; Invalidate(); } }
        }
        public ShaftPanel(PatinilloPlan plan, IReadOnlyDictionary<int, Color> colours)
        {
            _plan = plan; _colours = colours;
            DoubleBuffered = true;
            ResizeRedraw = true;
            BackColor = SystemColors.Window;
            AccessibleName = "Distribución en planta del patinillo";
            _legend = new FlowLayoutPanel { Dock = DockStyle.Bottom, AutoScroll = true, Height = 82,
                BackColor = SystemColors.Window, Padding = new Padding(4) };
            foreach (var floor in plan.Placements.GroupBy(p => p.Pipe.FloorIndex).OrderBy(g => g.Key))
            {
                var label = new Label { AutoSize = true, Padding = new Padding(4), Margin = new Padding(2),
                    Text = floor.First().Pipe.FloorName, BackColor = colours[floor.Key], ForeColor = Color.Black };
                _legend.Controls.Add(label);
                _tips.SetToolTip(label, "Planta de salida: " + label.Text);
            }
            Controls.Add(_legend);
        }
        protected override void OnPaint(PaintEventArgs e)
        {
            base.OnPaint(e);
            _circles.Clear();
            float unit = DeviceDpi / 96f;
            float availableW = ClientSize.Width - 100 * unit;
            float availableH = ClientSize.Height - _legend.Height - 115 * unit;
            if (availableW <= 0 || availableH <= 0 || !double.IsFinite(_plan.WidthMm) || !double.IsFinite(_plan.DepthMm)
                || _plan.WidthMm <= 0 || _plan.DepthMm <= 0) return;
            var g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            float scale = (float)Math.Min(availableW / _plan.WidthMm, availableH / _plan.DepthMm);
            float w = (float)_plan.WidthMm * scale, h = (float)_plan.DepthMm * scale;
            float left = (ClientSize.Width - w) / 2, top = 44 * unit + (availableH - h) / 2;
            var shaft = new RectangleF(left, top, w, h);
            using var outline = new Pen(Color.DimGray, 1.5f * unit);
            using var mouth = new Pen(Color.Teal, 5 * unit);
            using var small = new Font(Font.FontFamily, 8F);
            using var center = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center };
            g.DrawRectangle(outline, shaft.X, shaft.Y, shaft.Width, shaft.Height);
            g.DrawLine(mouth, shaft.Left, shaft.Bottom, shaft.Right, shaft.Bottom);
            g.DrawString(Mm(_plan.WidthMm) + " mm", Font, Brushes.DimGray,
                new RectangleF(left, top - 27 * unit, w, 22 * unit), center);
            var state = g.Save();
            g.TranslateTransform(left - 24 * unit, top + h / 2);
            g.RotateTransform(-90);
            g.DrawString(Mm(_plan.DepthMm) + " mm", Font, Brushes.DimGray,
                new RectangleF(-h / 2, -12 * unit, h, 24 * unit), center);
            g.Restore(state);
            g.DrawString("BOCA (salida del peine)", Font, Brushes.Teal,
                new RectangleF(0, shaft.Bottom + 8 * unit, ClientSize.Width, 24 * unit), center);
            g.DrawString("Separación entre ejes: " + Mm(_plan.PitchMm) + " mm", small, Brushes.DimGray,
                new RectangleF(0, shaft.Bottom + 33 * unit, ClientSize.Width, 24 * unit), center);
            for (int i = 0; i < _plan.Placements.Count; i++)
            {
                var p = _plan.Placements[i];
                float diameter = (float)p.Pipe.OuterMm * scale;
                float x = left + (float)p.XMm * scale, y = shaft.Bottom - (float)p.YMm * scale;
                var bounds = new RectangleF(x - diameter / 2, y - diameter / 2, diameter, diameter);
                _circles.Add((bounds, i));
                using var fill = new SolidBrush(_colours[p.Pipe.FloorIndex]);
                g.FillEllipse(fill, bounds);
                using var border = new Pen(i == _selected ? Color.Black : Color.DimGray, (i == _selected ? 3 : 1) * unit);
                g.DrawEllipse(border, bounds);
                if (i == _selected)
                {
                    var halo = bounds; halo.Inflate(4 * unit, 4 * unit);
                    g.DrawEllipse(border, halo);
                }
                string label = "V" + p.Pipe.Dwelling + " " + Code(p.Pipe.Service);
                // Keep a readable minimum font; the table and tooltip retain full details on dense plans.
                var size = g.MeasureString(label, small);
                var labelBounds = new RectangleF(x - size.Width / 2, y - size.Height / 2, size.Width, size.Height);
                using var backing = new SolidBrush(Color.FromArgb(210, Color.White));
                g.FillRectangle(backing, labelBounds);
                g.DrawString(label, small, Brushes.Black, labelBounds, center);
            }
        }
        private int Hit(Point point)
        {
            foreach (var circle in _circles)
            {
                var r = circle.Bounds;
                float radius = r.Width / 2;
                float dx = point.X - (r.X + radius), dy = point.Y - (r.Y + radius);
                if (dx * dx + dy * dy <= radius * radius) return circle.Index;
            }
            return -1;
        }
        protected override void OnMouseClick(MouseEventArgs e)
        {
            base.OnMouseClick(e);
            if (e.Button == MouseButtons.Left) PipeSelected?.Invoke(Hit(e.Location));
        }
        protected override void OnMouseMove(MouseEventArgs e)
        {
            base.OnMouseMove(e);
            int index = Hit(e.Location);
            if (index == _hover) return;
            _hover = index;
            Cursor = index >= 0 ? Cursors.Hand : Cursors.Default;
            if (index < 0) { _tips.SetToolTip(this, null); return; }
            var p = _plan.Placements[index];
            _tips.SetToolTip(this, $"{p.Pipe.Id} · Vivienda {p.Pipe.Dwelling} · {Service(p.Pipe.Service)}"
                + $"\nØ nominal {Mm(p.Pipe.NominalMm)} mm · Ø exterior {Mm(p.Pipe.OuterMm)} mm"
                + $"\nPlanta de salida: {p.Pipe.FloorName} · Fila {p.Row + 1} · Columna {p.Column + 1}"
                + $"\nCota salida: {(p.ExitElevationMm / 1000).ToString("0.00", Spanish)} m · Escalón {p.Tier}");
        }
        protected override void OnMouseLeave(EventArgs e)
        {
            base.OnMouseLeave(e);
            _hover = -1;
            _tips.SetToolTip(this, null);
        }
        protected override void Dispose(bool disposing)
        {
            if (disposing) _tips.Dispose();
            base.Dispose(disposing);
        }
    }
}
