#nullable enable
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Drawing;
using System.Globalization;
using System.Linq;
using System.Windows.Forms;

namespace RevitAutomation.Addin.Commands.Aislamiento;

/// <summary>
/// Short dialog: where (level or selection) and, per network, insulate or not, material and thickness (RITE or fixed).
/// Systems that cannot be recognised automatically appear in a second small list. Everything is remembered.
/// </summary>
public sealed class AislamientoForm : Form
{
    private const string RiteText = "RITE (automático)";
    private const string NotInsulate = "No aislar";
    private static readonly CultureInfo Spanish = CultureInfo.GetCultureInfo("es-ES");
    private static readonly Color Amber = Color.FromArgb(255, 236, 179);

    private static readonly (AislRed Red, string Text)[] RedTexts =
    {
        (AislRed.Acs, "ACS (agua caliente sanitaria)"),
        (AislRed.AguaFria, "Agua fría sanitaria"),
        (AislRed.ClimaCaliente, "Calefacción (agua caliente de clima)"),
        (AislRed.ClimaFrio, "Agua fría de clima"),
        (AislRed.ClimaMixto, "Clima frío y calor (2 tubos)"),
        (AislRed.Frigorifica, "Líneas frigoríficas"),
    };

    private readonly ToolTip _tips = new() { AutoPopDelay = 15000 };
    private readonly AislModelOptions _options;
    private readonly ComboBox _where = new() { DropDownStyle = ComboBoxStyle.DropDownList, Width = 420 };
    private readonly CheckBox _exterior = new() { Text = "Tuberías por el exterior o cubierta (tablas de exterior del RITE)", AutoSize = true },
        _review = new() { Text = "Revisar la lista antes de aplicar", AutoSize = true };
    private readonly DataGridView _networks = Grid(), _unknown = Grid();
    private readonly List<string> _thicknessTexts;

    [Browsable(false)]
    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public AislSettings Settings { get; }

    public AislamientoForm(AislSettings settings, AislModelOptions options)
    {
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(options);
        Settings = settings.Clone();
        _options = options;
        _thicknessTexts = Settings.CommercialThicknessesMm.Where(t => t > 0).OrderBy(t => t).Select(Mm).ToList();

        Text = "Aislamiento RITE";
        Font = new Font("Segoe UI", 9F);
        AutoScaleDimensions = new SizeF(96, 96);
        AutoScaleMode = AutoScaleMode.Dpi;
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false;
        MinimizeBox = false;
        StartPosition = FormStartPosition.CenterScreen;
        AutoSize = true;
        AutoSizeMode = AutoSizeMode.GrowAndShrink;
        Padding = new Padding(14);

        var root = new TableLayoutPanel { AutoSize = true, ColumnCount = 1, Dock = DockStyle.Fill };
        Controls.Add(root);

        // Where.
        var whereRow = new FlowLayoutPanel { AutoSize = true, WrapContents = false, Margin = new Padding(0, 0, 0, 10) };
        whereRow.Controls.Add(new Label { Text = "Aislar:", AutoSize = true, Margin = new Padding(0, 6, 8, 0) });
        whereRow.Controls.Add(_where);
        root.Controls.Add(whereRow);
        FillWhere();

        // Networks: insulate, material, thickness.
        root.Controls.Add(Title("Material y espesor por red"));
        BuildNetworks();
        root.Controls.Add(_networks);
        root.Controls.Add(new Label
        {
            Text = "«RITE (automático)»: espesor de las tablas del RITE para cada diámetro, redondeado al comercial superior. "
                + "Un número fija ese espesor en toda la red.",
            AutoSize = true, MaximumSize = new Size(760, 0), ForeColor = SystemColors.GrayText, Margin = new Padding(0, 4, 0, 10)
        });

        // Systems not recognised automatically.
        if (BuildUnknown())
        {
            root.Controls.Add(Title("Sistemas que no se reconocen por su nombre: indique qué son"));
            root.Controls.Add(_unknown);
        }

        _exterior.Checked = Settings.Exterior;
        _review.Checked = Settings.ReviewBeforeApplying;
        _tips.SetToolTip(_review, "Si no se marca, aísla directamente y al final muestra un resumen (Ctrl+Z lo deshace todo).");
        root.Controls.Add(_exterior);
        root.Controls.Add(_review);
        root.Controls.Add(new Label
        {
            Text = "Procedimiento simplificado del RITE (IT 1.2.4.2.1.2), válido hasta 70 kW. Piezas y accesorios (codos, tes, válvulas) "
                + "se aíslan con su tubería. Saneamiento, PCI y gas no se aíslan. El aislamiento existente solo se aumenta, nunca se reduce.",
            AutoSize = true, MaximumSize = new Size(760, 0), ForeColor = SystemColors.GrayText, Font = new Font(Font, FontStyle.Italic),
            Margin = new Padding(0, 8, 0, 0)
        });

        var buttons = new FlowLayoutPanel { AutoSize = true, FlowDirection = FlowDirection.RightToLeft, Dock = DockStyle.Fill, Margin = new Padding(0, 12, 0, 0) };
        var ok = new Button { Text = "Aislar", AutoSize = true, MinimumSize = new Size(100, 0) };
        var cancel = new Button { Text = "Cancelar", AutoSize = true, DialogResult = DialogResult.Cancel };
        ok.Click += (_, _) => Accept();
        buttons.Controls.Add(ok);
        buttons.Controls.Add(cancel);
        root.Controls.Add(buttons);
        AcceptButton = ok;
        CancelButton = cancel;
    }

    private static Label Title(string text) =>
        new() { Text = text, AutoSize = true, Font = new Font("Segoe UI", 9F, FontStyle.Bold), Margin = new Padding(0, 4, 0, 4) };

    private static DataGridView Grid() => new()
    {
        Width = 760, AllowUserToAddRows = false, AllowUserToDeleteRows = false, AllowUserToResizeRows = false,
        RowHeadersVisible = false, AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.Fill, ScrollBars = ScrollBars.Vertical,
        SelectionMode = DataGridViewSelectionMode.CellSelect, MultiSelect = false, EditMode = DataGridViewEditMode.EditOnEnter,
        BackgroundColor = SystemColors.Window, BorderStyle = BorderStyle.FixedSingle, ColumnHeadersHeightSizeMode = DataGridViewColumnHeadersHeightSizeMode.AutoSize
    };

    private static string Mm(double mm) => mm.ToString("0.#", Spanish) + " mm";
    private static string RedText(AislRed red) => RedTexts.FirstOrDefault(r => r.Red == red).Text ?? NotInsulate;

    // ---- Where ----

    private void FillWhere()
    {
        // Pipes picked with the mouse: nothing to choose here. Level mode: only the levels.
        if (_options.SelectedPipeCount > 0)
        {
            _where.Items.Add($"Tuberías seleccionadas ({_options.SelectedPipeCount} elementos)");
            _where.SelectedIndex = 0;
            _where.Enabled = false;
            return;
        }
        foreach (var name in _options.LevelNames) _where.Items.Add("Planta: " + name);
        // The level of the active view first (that is where the user is looking), then the last one used.
        string level = _options.LevelNames.Contains(_options.ActiveLevelName) ? _options.ActiveLevelName : Settings.LevelName;
        if (_where.Items.Count > 0) _where.SelectedIndex = Math.Max(0, _where.Items.IndexOf("Planta: " + level));
        _tips.SetToolTip(_where, "Nivel cuyas tuberías, piezas y accesorios se aíslan.");
    }

    // ---- Networks ----

    private void BuildNetworks()
    {
        var types = _options.InsulationTypeNames.ToList();
        _networks.Columns.Add(new DataGridViewCheckBoxColumn { Name = "On", HeaderText = "Aislar", FillWeight = 12 });
        _networks.Columns.Add(new DataGridViewTextBoxColumn { Name = "Red", HeaderText = "Red", ReadOnly = true, FillWeight = 60 });
        var material = new DataGridViewComboBoxColumn { Name = "Material", HeaderText = "Material (tipo de aislamiento)", FillWeight = 70,
            FlatStyle = FlatStyle.Flat, DisplayStyle = DataGridViewComboBoxDisplayStyle.DropDownButton };
        material.Items.AddRange(types.Cast<object>().ToArray());
        _networks.Columns.Add(material);
        _networks.Columns.Add(new DataGridViewComboBoxColumn { Name = "Espesor", HeaderText = "Espesor", FillWeight = 42,
            FlatStyle = FlatStyle.Flat, DisplayStyle = DataGridViewComboBoxDisplayStyle.DropDownButton });
        _networks.Columns.Add(new DataGridViewTextBoxColumn { Name = "T", HeaderText = "T diseño (°C)", FillWeight = 26 });

        foreach (var (red, text) in RedTexts)
        {
            var n = Settings.Network(red);
            int row = _networks.Rows.Add();
            var r = _networks.Rows[row];
            r.Tag = red;
            r.Cells["On"].Value = n.Enabled;
            r.Cells["Red"].Value = text;
            string type = types.FirstOrDefault(t => string.Equals(t, n.InsulationType, StringComparison.OrdinalIgnoreCase))
                ?? GuessType(red, types) ?? "";
            if (types.Count > 0) r.Cells["Material"].Value = type;
            var thickness = (DataGridViewComboBoxCell)r.Cells["Espesor"];
            if (red != AislRed.AguaFria) thickness.Items.Add(RiteText);
            foreach (var t in _thicknessTexts) thickness.Items.Add(t);
            string current = n.ThicknessMm is double mm && mm > 0 ? Mm(mm) : red == AislRed.AguaFria ? Mm(9) : RiteText;
            if (!thickness.Items.Contains(current)) thickness.Items.Add(current);
            thickness.Value = current;
            var tCell = r.Cells["T"];
            switch (red)
            {
                case AislRed.Acs: tCell.Value = Settings.AcsTemperatureC.ToString("0.#", Spanish); break;
                case AislRed.ClimaCaliente: tCell.Value = Settings.HeatingTemperatureC.ToString("0.#", Spanish); break;
                case AislRed.ClimaFrio: tCell.Value = Settings.ChilledTemperatureC.ToString("0.#", Spanish); break;
                default:
                    tCell.Value = red == AislRed.ClimaMixto ? "calor / frío" : "—";
                    tCell.ReadOnly = true;
                    tCell.Style.ForeColor = SystemColors.GrayText;
                    break;
            }
        }
        _networks.Height = _networks.ColumnHeadersHeight + _networks.Rows.Cast<DataGridViewRow>().Sum(r => r.Height) + 3;
        _networks.CurrentCellDirtyStateChanged += (_, _) => { if (_networks.IsCurrentCellDirty) _networks.CommitEdit(DataGridViewDataErrorContexts.Commit); };
        _networks.DataError += (_, e) => e.ThrowException = false;
        if (types.Count == 0) _tips.SetToolTip(_networks, "El proyecto no tiene tipos de aislamiento de tubería.");
    }

    /// <summary>Default material by the insulation type name: wool/fibre for hot, elastomer for cold and refrigerant.</summary>
    internal static string? GuessType(AislRed red, IReadOnlyList<string> types)
    {
        if (types.Count == 0) return null;
        string[] hot = { "lana", "fibra", "mineral", "vidrio", "caliente", "calor", "acs", "calef" };
        string[] cold = { "elast", "armaflex", "caucho", "espuma", "k-flex", "kflex", "frio", "frigor", "polietileno" };
        var keys = AislRite.MaterialOf(red) == AislMaterialGroup.Caliente ? hot.Concat(cold) : cold.Concat(hot);
        foreach (var key in keys)
        {
            var match = types.FirstOrDefault(t => AislClassifier.Normalize(t).Contains(key, StringComparison.Ordinal));
            if (match != null) return match;
        }
        return types[0];
    }

    // ---- Unknown systems ----

    /// <summary>Systems the classifier does not recognise (or that the user assigned before). False if there are none.</summary>
    private bool BuildUnknown()
    {
        var plain = Settings.Clone();
        plain.SystemOverrides.Clear();
        var rows = _options.Systems
            .Where(s => Settings.SystemOverrides.ContainsKey(s.Name) || AislClassifier.Classify(s, plain).Red == AislRed.SinClasificar)
            .OrderBy(s => s.Name, StringComparer.CurrentCultureIgnoreCase).ToList();
        if (rows.Count == 0) return false;

        _unknown.Columns.Add(new DataGridViewTextBoxColumn { Name = "Sistema", HeaderText = "Sistema de Revit", ReadOnly = true, FillWeight = 60 });
        var what = new DataGridViewComboBoxColumn { Name = "Es", HeaderText = "Es…", FillWeight = 60, FlatStyle = FlatStyle.Flat,
            DisplayStyle = DataGridViewComboBoxDisplayStyle.DropDownButton };
        what.Items.AddRange(RedTexts.Select(r => (object)r.Text).Append(NotInsulate).ToArray());
        _unknown.Columns.Add(what);
        foreach (var s in rows)
        {
            int i = _unknown.Rows.Add();
            var r = _unknown.Rows[i];
            r.Tag = s.Name;
            r.Cells["Sistema"].Value = s.Name;
            bool assigned = Settings.SystemOverrides.TryGetValue(s.Name, out var red);
            r.Cells["Es"].Value = assigned && red != AislRed.SinClasificar ? RedText(red) : NotInsulate;
            if (!assigned) r.DefaultCellStyle.BackColor = Amber;
        }
        int visible = Math.Min(rows.Count, 6);
        _unknown.Height = _unknown.ColumnHeadersHeight + visible * _unknown.RowTemplate.Height + 3;
        _unknown.CurrentCellDirtyStateChanged += (_, _) => { if (_unknown.IsCurrentCellDirty) _unknown.CommitEdit(DataGridViewDataErrorContexts.Commit); };
        _unknown.CellValueChanged += (_, e) => { if (e.RowIndex >= 0) _unknown.Rows[e.RowIndex].DefaultCellStyle.BackColor = SystemColors.Window; };
        _unknown.DataError += (_, e) => e.ThrowException = false;
        _tips.SetToolTip(_unknown, "Se recuerda para la próxima vez.");
        return true;
    }

    // ---- Accept ----

    private void Accept()
    {
        _networks.EndEdit();
        _unknown.EndEdit();
        string where = _where.SelectedItem as string ?? "";
        Settings.OnlySelection = _options.SelectedPipeCount > 0 && _where.SelectedIndex == 0;
        if (where.StartsWith("Planta: ", StringComparison.Ordinal)) Settings.LevelName = where.Substring("Planta: ".Length);

        foreach (DataGridViewRow r in _networks.Rows)
        {
            if (r.Tag is not AislRed red) continue;
            var n = Settings.Network(red);
            n.Enabled = r.Cells["On"].Value is true;
            n.InsulationType = r.Cells["Material"].Value as string ?? "";
            string thickness = r.Cells["Espesor"].Value as string ?? RiteText;
            n.ThicknessMm = thickness == RiteText ? null : ParseMm(thickness);
            if (red is AislRed.Acs or AislRed.ClimaCaliente or AislRed.ClimaFrio)
            {
                if (!double.TryParse((r.Cells["T"].Value as string ?? "").Replace(" ", ""), NumberStyles.Float, Spanish, out double t)
                    || t < -40 || t > 200)
                {
                    MessageBox.Show(this, $"Temperatura no válida en «{r.Cells["Red"].Value}».", Text, MessageBoxButtons.OK, MessageBoxIcon.Warning);
                    _networks.CurrentCell = r.Cells["T"];
                    return;
                }
                if (red == AislRed.Acs) Settings.AcsTemperatureC = t;
                else if (red == AislRed.ClimaCaliente) Settings.HeatingTemperatureC = t;
                else Settings.ChilledTemperatureC = t;
            }
            if (red == AislRed.AguaFria)
            {
                Settings.InsulateColdWater = n.Enabled;
                if (n.ThicknessMm is double cw) Settings.ColdWaterThicknessMm = cw;
            }
        }
        foreach (DataGridViewRow r in _unknown.Rows)
        {
            if (r.Tag is not string system) continue;
            string text = r.Cells["Es"].Value as string ?? NotInsulate;
            var match = RedTexts.FirstOrDefault(x => x.Text == text);
            Settings.SystemOverrides[system] = match.Text is null ? AislRed.Excluida : match.Red;
        }
        Settings.Exterior = _exterior.Checked;
        Settings.ReviewBeforeApplying = _review.Checked;
        Settings.IncludeHydronic = true;
        Settings.IncludeFittingsAndAccessories = true;
        DialogResult = DialogResult.OK;
        Close();
    }

    private static double? ParseMm(string text)
    {
        string number = text.Replace("mm", "").Trim();
        return double.TryParse(number, NumberStyles.Float, Spanish, out double mm) && mm > 0 ? mm : null;
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) _tips.Dispose();
        base.Dispose(disposing);
    }
}
