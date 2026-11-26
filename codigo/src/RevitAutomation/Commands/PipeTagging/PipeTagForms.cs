using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using Autodesk.Revit.DB;
using WF = System.Windows.Forms;

namespace RevitAutomation.Addin.Commands.PipeTagging;

/// <summary>Review before modifying: what will be tagged, what is excluded and which tag type is used.</summary>
internal sealed class PipeTagSetupForm : WF.Form
{
    private readonly WF.ComboBox _types = new() { DropDownStyle = WF.ComboBoxStyle.DropDownList, Width = 520 };
    private readonly List<FamilySymbol> _symbols;

    internal FamilySymbol? SelectedType => _types.SelectedIndex >= 0 ? _symbols[_types.SelectedIndex] : null;

    internal PipeTagSetupForm(MepTagProfile profile, string viewName, int viewScale, int toTag, List<PipeTagDeviation> excluded,
        List<FamilySymbol> symbols, ElementId? defaultTypeId)
    {
        _symbols = symbols;
        Text = profile.Title + " · Revisar antes de etiquetar";
        Width = 1000;
        Height = 600;
        StartPosition = WF.FormStartPosition.CenterScreen;
        MinimizeBox = false;
        AutoScaleMode = WF.AutoScaleMode.Dpi;

        var header = new WF.Label
        {
            Dock = WF.DockStyle.Top,
            Height = 134,
            Padding = new WF.Padding(10),
            Text = $"Vista: {viewName} (escala 1:{viewScale})\n\n" +
                   $"Se etiquetarán {toTag} tramos rectos de {profile.Nouns.Singular} de más de 15 cm.\n" +
                   $"{excluded.Count} elementos de la selección no se etiquetarán (lista de abajo): codos, tes, accesorios, tramos cortos o ya etiquetados.\n\n" +
                   "Cada etiqueta se coloca 20 cm por encima del eje de su tramo; si ahí pisaría otra etiqueta, un texto o cualquier tubería, conducto o bandeja modelada, " +
                   "se sube lo justo (hasta 100 cm, con directriz) y, si arriba no cabe, se pone debajo del eje con la misma regla. " +
                   "Si aun así no cabe, no se crea y aparecerá en el reporter de desviaciones."
        };

        var typeRow = new WF.FlowLayoutPanel { Dock = WF.DockStyle.Top, Height = 40, Padding = new WF.Padding(8, 6, 8, 0) };
        typeRow.Controls.Add(new WF.Label { Text = "Etiqueta del modelo a usar:", AutoSize = true, Padding = new WF.Padding(0, 5, 6, 0) });
        int selected = 0;
        for (int i = 0; i < symbols.Count; i++)
        {
            bool isDefault = defaultTypeId is not null && symbols[i].Id.Equals(defaultTypeId);
            _types.Items.Add($"{symbols[i].FamilyName} : {symbols[i].Name}" + (isDefault ? "  (predeterminada del proyecto)" : ""));
            if (isDefault) selected = i;
        }
        _types.SelectedIndex = symbols.Count > 0 ? selected : -1;
        typeRow.Controls.Add(_types);

        var grid = PipeTagReportForm.CreateGrid();
        PipeTagReportForm.AddColumns(grid, ("Id", 80), ("Elemento", 260), ("Motivo", 420), ("Cómo seguir", 220));
        foreach (var d in excluded) grid.Rows.Add(d.Id, d.Element, d.Reason, d.Fix);

        var ok = new WF.Button { Text = "Etiquetar", AutoSize = true, Enabled = toTag > 0 };
        ok.Click += (_, _) =>
        {
            if (SelectedType is null)
            {
                WF.MessageBox.Show(this, "Elige la etiqueta del modelo que quieres usar.", Text);
                return;
            }
            DialogResult = WF.DialogResult.OK;
            Close();
        };
        var cancel = new WF.Button { Text = "Cancelar", AutoSize = true, DialogResult = WF.DialogResult.Cancel };
        var actions = new WF.FlowLayoutPanel { Dock = WF.DockStyle.Bottom, Height = 48, FlowDirection = WF.FlowDirection.RightToLeft, Padding = new WF.Padding(6) };
        actions.Controls.Add(ok);
        actions.Controls.Add(cancel);

        Controls.Add(grid);
        Controls.Add(actions);
        Controls.Add(typeRow);
        Controls.Add(header);
        AcceptButton = ok;
        CancelButton = cancel;
    }
}

/// <summary>Reporter: tagged elements with their tag, and deviations (excluded or not tagged) with how to fix them.</summary>
internal sealed class PipeTagReportForm : WF.Form
{
    private readonly WF.TabControl _tabs = new() { Dock = WF.DockStyle.Fill };
    private readonly WF.DataGridView _taggedGrid = CreateGrid();
    private readonly WF.DataGridView _deviationGrid = CreateGrid();
    private readonly List<PipeTagReportRow> _tagged;
    private readonly List<PipeTagDeviation> _deviations;
    private readonly string _viewName, _tagName, _status;
    private readonly MepTagProfile _profile;

    private string[] TaggedHeaders()
    {
        if (_profile.ReportHeaders is { Length: 8 } custom) return custom;
        string s = _profile.Nouns.Singular;
        return new[] { "Id " + s, _profile.SystemHeader, "Tipo de " + s, _profile.SizeHeader, "Longitud (m)", "Etiqueta", "Texto de la etiqueta", "Colocación" };
    }

    internal PipeTagReportForm(MepTagProfile profile, string viewName, string tagName, string status, List<PipeTagReportRow> tagged,
        List<PipeTagDeviation> deviations, Action<ICollection<ElementId>> select)
    {
        _profile = profile;
        _tagged = tagged;
        _deviations = deviations;
        _viewName = viewName;
        _tagName = tagName;
        _status = status;
        Text = profile.Title + " · Reporter";
        Width = 1250;
        Height = 700;
        StartPosition = WF.FormStartPosition.CenterScreen;
        MinimizeBox = false;
        AutoScaleMode = WF.AutoScaleMode.Dpi;

        int failed = deviations.Count(d => d.Kind == "No se pudo etiquetar");
        var header = new WF.Label
        {
            Dock = WF.DockStyle.Top,
            Height = 76,
            Padding = new WF.Padding(10),
            Text = $"Vista: {viewName}" + (tagName.Length > 0 ? $"   ·   Etiqueta: {tagName}" : "") + $"\n{status}\n" +
                   $"Etiquetados: {tagged.Count}   ·   Desviaciones: {deviations.Count} ({failed} no se pudieron etiquetar, {deviations.Count - failed} excluidos por la regla)."
        };

        var headers = TaggedHeaders();
        AddColumns(_taggedGrid, (headers[0], 80), (headers[1], 170), (headers[2], 170), (headers[3], 80), (headers[4], 85),
            ("Etiqueta", 230), ("Texto de la etiqueta", 170), ("Colocación", 230));
        foreach (var r in tagged)
        {
            int i = _taggedGrid.Rows.Add(r.Id, r.System, r.PipeType, r.Diameter, r.Length, r.Tag, r.TagText, r.Placement);
            _taggedGrid.Rows[i].Tag = r;
        }
        AddColumns(_deviationGrid, ("Id", 80), ("Tipo de desviación", 150), ("Elemento", 250), ("Qué ocurre", 420), ("Cómo corregirlo", 300));
        foreach (var d in deviations.OrderBy(d => d.Kind == "No se pudo etiquetar" ? 0 : 1))
        {
            int i = _deviationGrid.Rows.Add(d.Id, d.Kind, d.Element, d.Reason, d.Fix);
            _deviationGrid.Rows[i].Tag = d;
            if (d.Kind == "No se pudo etiquetar") _deviationGrid.Rows[i].DefaultCellStyle.BackColor = System.Drawing.Color.MistyRose;
        }

        var taggedPage = new WF.TabPage($"Etiquetados ({tagged.Count})");
        taggedPage.Controls.Add(_taggedGrid);
        var deviationPage = new WF.TabPage($"Desviaciones ({deviations.Count})");
        deviationPage.Controls.Add(_deviationGrid);
        _tabs.TabPages.Add(taggedPage);
        _tabs.TabPages.Add(deviationPage);
        if (tagged.Count == 0 && deviations.Count > 0) _tabs.SelectedTab = deviationPage;

        var close = new WF.Button { Text = "Cerrar", AutoSize = true, DialogResult = WF.DialogResult.Cancel };
        var export = new WF.Button { Text = "Exportar CSV…", AutoSize = true };
        export.Click += (_, _) => ExportCsv();
        var show = new WF.Button { Text = "Seleccionar en Revit", AutoSize = true };
        show.Click += (_, _) =>
        {
            var ids = SelectedIds();
            if (ids.Count == 0)
            {
                WF.MessageBox.Show(this, "Marca una o varias filas de la pestaña activa.", Text);
                return;
            }
            select(ids);
        };
        var actions = new WF.FlowLayoutPanel { Dock = WF.DockStyle.Bottom, Height = 48, FlowDirection = WF.FlowDirection.RightToLeft, Padding = new WF.Padding(6) };
        actions.Controls.Add(close);
        actions.Controls.Add(export);
        actions.Controls.Add(show);

        Controls.Add(_tabs);
        Controls.Add(actions);
        Controls.Add(header);
        CancelButton = close;
    }

    private List<ElementId> SelectedIds()
    {
        var grid = _tabs.SelectedIndex == 0 ? _taggedGrid : _deviationGrid;
        var ids = new List<ElementId>();
        foreach (WF.DataGridViewRow row in grid.SelectedRows)
        {
            if (row.Tag is PipeTagReportRow t) { ids.Add(t.PipeId); if (t.TagId != ElementId.InvalidElementId) ids.Add(t.TagId); }
            else if (row.Tag is PipeTagDeviation d && d.ElementId != ElementId.InvalidElementId) ids.Add(d.ElementId);
        }
        return ids;
    }

    private void ExportCsv()
    {
        using var dialog = new WF.SaveFileDialog
        {
            Filter = "CSV (*.csv)|*.csv",
            FileName = "Etiquetado " + _profile.Nouns.Plural + " - " + string.Concat(_viewName.Split(Path.GetInvalidFileNameChars())) + ".csv"
        };
        if (dialog.ShowDialog(this) != WF.DialogResult.OK) return;
        string Q(string s) => "\"" + (s ?? "").Replace("\"", "\"\"") + "\"";
        var sb = new StringBuilder();
        sb.AppendLine(string.Join(";", Q("Vista"), Q(_viewName), Q("Etiqueta"), Q(_tagName), Q(_status)));
        sb.AppendLine();
        sb.AppendLine("ETIQUETADOS");
        sb.AppendLine(string.Join(";", TaggedHeaders().Select(Q)));
        foreach (var r in _tagged)
            sb.AppendLine(string.Join(";", new[] { r.Id, r.System, r.PipeType, r.Diameter, r.Length, r.Tag, r.TagText, r.Placement }.Select(Q)));
        sb.AppendLine();
        sb.AppendLine("DESVIACIONES");
        sb.AppendLine(string.Join(";", new[] { "Id", "Tipo de desviación", "Elemento", "Qué ocurre", "Cómo corregirlo" }.Select(Q)));
        foreach (var d in _deviations)
            sb.AppendLine(string.Join(";", new[] { d.Id, d.Kind, d.Element, d.Reason, d.Fix }.Select(Q)));
        try
        {
            File.WriteAllText(dialog.FileName, sb.ToString(), new UTF8Encoding(true));
            WF.MessageBox.Show(this, "Reporter guardado en:\n" + dialog.FileName, Text);
        }
        catch (Exception ex)
        {
            WF.MessageBox.Show(this, "No se pudo guardar el archivo: " + ex.Message + "\nSi está abierto en Excel, ciérralo y vuelve a intentarlo.", Text);
        }
    }

    internal static WF.DataGridView CreateGrid() => new()
    {
        Dock = WF.DockStyle.Fill,
        AllowUserToAddRows = false,
        AllowUserToDeleteRows = false,
        ReadOnly = true,
        RowHeadersVisible = false,
        SelectionMode = WF.DataGridViewSelectionMode.FullRowSelect,
        MultiSelect = true
    };

    internal static void AddColumns(WF.DataGridView grid, params (string Title, int Width)[] columns)
    {
        foreach (var (title, width) in columns)
            grid.Columns.Add(new WF.DataGridViewTextBoxColumn { HeaderText = title, Width = width, ReadOnly = true });
    }
}
