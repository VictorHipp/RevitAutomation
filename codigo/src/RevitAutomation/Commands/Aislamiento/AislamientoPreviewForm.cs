#nullable enable
using System;
using System.Collections.Generic;
using System.Drawing;
using System.Globalization;
using System.Linq;
using System.Windows.Forms;

namespace RevitAutomation.Addin.Commands.Aislamiento;

public sealed class AislamientoPreviewForm : Form
{
    private const string Note70kW = "Procedimiento simplificado del RITE (IT 1.2.4.2.1.2): válido para instalaciones de hasta 70 kW. "
        + "Por encima, el RITE exige el procedimiento alternativo.";
    private static readonly CultureInfo Spanish = CultureInfo.GetCultureInfo("es-ES");
    private static readonly Color Amber = Color.FromArgb(255, 236, 179);
    private static readonly Color LightRed = Color.FromArgb(255, 222, 222);
    private readonly ToolTip _tips = new() { AutoPopDelay = 20000 };
    private readonly ListView _list = new() { Dock = DockStyle.Fill, View = View.Details, FullRowSelect = true,
        MultiSelect = false, HideSelection = false, GridLines = true, ShowItemToolTips = true, ShowGroups = true };
    private readonly ListView _deviations = new() { Dock = DockStyle.Top, View = View.Details, FullRowSelect = true,
        MultiSelect = false, GridLines = true, ShowItemToolTips = true, Height = 150, Visible = false };

    public AislamientoPreviewForm(string scopeText, IReadOnlyList<AislSummaryRow> rows, IReadOnlyList<AislDeviation> deviations)
    {
        ArgumentNullException.ThrowIfNull(rows);
        ArgumentNullException.ThrowIfNull(deviations);
        Text = "Aislamiento RITE · Vista previa";
        Font = new Font("Segoe UI", 9F);
        AutoScaleDimensions = new SizeF(96, 96);
        AutoScaleMode = AutoScaleMode.Dpi;
        ClientSize = new Size(1160, 660);
        StartPosition = FormStartPosition.CenterScreen;
        MinimizeBox = false;
        Padding = new Padding(12);

        var root = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 5 };
        root.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        Controls.Add(root);

        // Top: scope and counters per action.
        int Count(AislAction action) => rows.Where(r => r.Action == action).Sum(r => r.Count);
        int create = Count(AislAction.Crear), increase = Count(AislAction.Aumentar);
        string counters = $"Crear: {create} · Aumentar: {increase} · Ya cumple: {Count(AislAction.MantenerSuficiente)}"
            + $" · No exigido: {Count(AislAction.NoExigido)} · Sin clasificar: {Count(AislAction.SinClasificar)}"
            + $" · Excluidas: {Count(AislAction.Excluida)}";
        var header = new TableLayoutPanel { AutoSize = true, Dock = DockStyle.Fill, ColumnCount = 1, Margin = new Padding(0, 0, 0, 8) };
        header.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        header.Controls.Add(new Label { Text = string.IsNullOrWhiteSpace(scopeText) ? "Alcance sin especificar" : scopeText,
            AutoSize = true, Dock = DockStyle.Top, Margin = new Padding(0, 0, 0, 4) });
        var countersLabel = new Label { Text = counters, AutoSize = true, Dock = DockStyle.Top, Font = new Font(Font, FontStyle.Bold) };
        header.Controls.Add(countersLabel);
        _tips.SetToolTip(countersLabel, "Número de elementos (tuberías, piezas y accesorios) por acción.");
        root.Controls.Add(header, 0, 0);

        // Middle: summary rows grouped by network.
        foreach (var col in new[] { ("Sistema", 200), ("Ø ext (mm)", 80), ("T (°C)", 60), ("Espesor (mm)", 90),
            ("Acción", 120), ("Nº", 50), ("Justificación", 520) })
            _list.Columns.Add(col.Item1, col.Item2, col.Item1 is "Sistema" or "Acción" or "Justificación"
                ? HorizontalAlignment.Left : HorizontalAlignment.Right);
        _list.BeginUpdate();
        foreach (var group in rows.GroupBy(r => r.Red).OrderBy(g => (int)g.Key))
        {
            int elements = group.Sum(r => r.Count);
            var lvGroup = new ListViewGroup($"{AislReport.RedText(group.Key)} ({elements} {(elements == 1 ? "elemento" : "elementos")})")
                { CollapsedState = ListViewGroupCollapsedState.Expanded };
            _list.Groups.Add(lvGroup);
            foreach (var r in group.OrderBy(r => r.SystemName, StringComparer.CurrentCultureIgnoreCase)
                         .ThenBy(r => r.OuterMm).ThenBy(r => (int)r.Action))
            {
                bool hasThickness = double.IsFinite(r.ThicknessMm) && r.ThicknessMm > 0;
                var item = new ListViewItem(new[]
                {
                    r.SystemName, Mm(r.OuterMm), Mm(r.TemperatureC), hasThickness ? Mm(r.ThicknessMm) : "—",
                    AislReport.ActionText(r.Action), r.Count.ToString(Spanish), r.Rule ?? ""
                }, lvGroup);
                item.ToolTipText = string.IsNullOrWhiteSpace(r.Rule) ? AislReport.ActionText(r.Action) : r.Rule;
                switch (r.Action)
                {
                    case AislAction.Aumentar: item.BackColor = Amber; break;
                    case AislAction.SinClasificar: item.BackColor = LightRed; break;
                    case AislAction.Excluida:
                    case AislAction.NoExigido: item.ForeColor = SystemColors.GrayText; break;
                }
                _list.Items.Add(item);
            }
        }
        _list.EndUpdate();
        root.Controls.Add(_list, 0, 1);

        // Bottom: collapsible deviations list.
        var deviationsPanel = new TableLayoutPanel { AutoSize = true, Dock = DockStyle.Fill, ColumnCount = 1, Margin = new Padding(0, 8, 0, 0) };
        deviationsPanel.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        if (deviations.Count == 0)
        {
            deviationsPanel.Controls.Add(new Label { Text = "Sin desviaciones: todo lo previsto se puede aislar según el RITE.",
                AutoSize = true, ForeColor = SystemColors.GrayText });
        }
        else
        {
            var toggle = new CheckBox { Text = $"Mostrar desviaciones ({deviations.Count})", AutoSize = true };
            _tips.SetToolTip(toggle, "Elementos que no se aislarán (o no como se pidió), el motivo y cómo corregirlo.");
            toggle.CheckedChanged += (_, _) => _deviations.Visible = toggle.Checked;
            deviationsPanel.Controls.Add(toggle);
            foreach (var col in new[] { ("Elemento", 170), ("Qué", 230), ("Motivo", 360), ("Cómo corregir", 340) })
                _deviations.Columns.Add(col.Item1, col.Item2);
            _deviations.BeginUpdate();
            foreach (var d in deviations)
            {
                var item = new ListViewItem(new[] { d.Element ?? "", d.What ?? "", d.Reason ?? "", d.HowToFix ?? "" });
                item.ToolTipText = string.Join(Environment.NewLine,
                    new[] { d.What, d.Reason, string.IsNullOrWhiteSpace(d.HowToFix) ? null : "Cómo corregir: " + d.HowToFix }
                        .Where(s => !string.IsNullOrWhiteSpace(s)));
                _deviations.Items.Add(item);
            }
            _deviations.EndUpdate();
            deviationsPanel.Controls.Add(_deviations);
        }
        root.Controls.Add(deviationsPanel, 0, 2);

        var note = new Label { Text = Note70kW, AutoSize = true, Dock = DockStyle.Fill, ForeColor = SystemColors.GrayText,
            Font = new Font(Font, FontStyle.Italic), Margin = new Padding(0, 8, 0, 0) };
        root.Controls.Add(note, 0, 3);

        // Buttons.
        bool anything = create + increase > 0;
        var buttons = new TableLayoutPanel { AutoSize = true, Dock = DockStyle.Fill, ColumnCount = 2, Margin = new Padding(0, 10, 0, 0) };
        buttons.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        buttons.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        const string nothingHint = "No hay aislamiento que crear ni aumentar con estos ajustes. Vuelva al diálogo para cambiarlos.";
        if (!anything)
        {
            var hint = new Label { Text = nothingHint, AutoSize = true, ForeColor = Color.FromArgb(150, 80, 0),
                Anchor = AnchorStyles.Left, Margin = new Padding(0, 6, 8, 0) };
            buttons.Controls.Add(hint, 0, 0);
        }
        var right = new FlowLayoutPanel { AutoSize = true, WrapContents = false, FlowDirection = FlowDirection.RightToLeft };
        var confirm = new Button { Text = "Confirmar y aislar", AutoSize = true, DialogResult = DialogResult.OK, Enabled = anything };
        var cancel = new Button { Text = "Cancelar", AutoSize = true, DialogResult = DialogResult.Cancel };
        right.Controls.AddRange(new Control[] { confirm, cancel });
        buttons.Controls.Add(right, 1, 0);
        root.Controls.Add(buttons, 0, 4);
        // A disabled button shows no tooltip itself; the surrounding panel does.
        _tips.SetToolTip(right, anything ? "Crea o aumenta el aislamiento de los elementos indicados." : nothingHint);
        _tips.SetToolTip(confirm, "Crea o aumenta el aislamiento de los elementos indicados.");
        AcceptButton = anything ? confirm : cancel;
        CancelButton = cancel;
    }

    protected override void OnLoad(EventArgs e)
    {
        base.OnLoad(e);
        var area = Screen.FromControl(this).WorkingArea;
        Size = new Size(Math.Min(Width, area.Width - 24), Math.Min(Height, area.Height - 24));
        Location = new Point(area.Left + (area.Width - Width) / 2, area.Top + (area.Height - Height) / 2);
    }
    private static string Mm(double value) => double.IsFinite(value) ? value.ToString("0.##", Spanish) : "—";
    protected override void Dispose(bool disposing)
    {
        if (disposing) _tips.Dispose();
        base.Dispose(disposing);
    }
}
