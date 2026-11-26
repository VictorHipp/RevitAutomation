using System.Drawing;
using System.Windows.Forms;
using CadMepRevit.Core;
using Form = System.Windows.Forms.Form;
using Control = System.Windows.Forms.Control;

namespace CadMepRevit.Ui;

/// <summary>
/// Plano con capas o bloques cuyo nombre el programa no reconoce: el usuario dice qué es cada uno y se RECUERDA para los
/// siguientes planos de la oficina (UserRules). Solo aparece si hay algo desconocido. «Seguir sin decidir» modela lo
/// reconocido y lo desconocido queda en el informe; se volverá a preguntar la próxima vez.
/// </summary>
public sealed class UnknownItemsForm : Form
{
    private readonly DataGridView _grid = new()
    {
        Width = 760, Height = 360, AllowUserToAddRows = false, AllowUserToDeleteRows = false, RowHeadersVisible = false,
        AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.Fill, SelectionMode = DataGridViewSelectionMode.CellSelect,
        EditMode = DataGridViewEditMode.EditOnEnter
    };

    private const string Ask = "(preguntar la próxima vez)";

    internal static readonly (string Label, MepKind Kind)[] LayerChoices =
    {
        ("No es una instalación (no preguntar más)", MepKind.NoImportar),
        ("Tubería", MepKind.Tuberia), ("Conducto", MepKind.Conducto), ("Bandeja de cables", MepKind.Bandeja),
        ("Tubo eléctrico", MepKind.TuboElectrico),
    };

    /// <summary>Capa con luminarias dibujadas con líneas (sin bloque): las coloca «Luminarias»; los tramos no la usan.</summary>
    internal const string FixtureLayerChoice = "Luminarias dibujadas con líneas (las coloca «Luminarias»)";

    internal static readonly (string Label, BlockRole Role)[] BlockChoices =
    {
        ("No es nada que colocar (no preguntar más)", BlockRole.Otro),
        ("Luminaria", BlockRole.Luminaria), ("Interruptor / detector de presencia", BlockRole.MandoIluminacion),
        ("PCI: extintor, BIE, detector, pulsador, sirena…", BlockRole.Pci),
        ("Eléctrico: cuadro, toma, caja…", BlockRole.Electrico), ("Equipo (bomba, fancoil, ventilador…)", BlockRole.Equipo),
        ("Rejilla o difusor", BlockRole.Terminal), ("Accesorio (válvula, compuerta…)", BlockRole.Accesorio),
        ("Aparato sanitario", BlockRole.Sanitario), ("Dispositivo (termostato, sensor…)", BlockRole.Dispositivo),
    };

    private readonly List<(bool IsLayer, string Name)> _rows = new();

    /// <summary>Respuestas del usuario (solo las filas en las que eligió algo).</summary>
    public Dictionary<string, MepKind> LayerAnswers { get; } = new(StringComparer.OrdinalIgnoreCase);
    public Dictionary<string, BlockRole> BlockAnswers { get; } = new(StringComparer.OrdinalIgnoreCase);
    /// <summary>Capas que el usuario dice que son luminarias dibujadas con líneas.</summary>
    public HashSet<string> FixtureLayerAnswers { get; } = new(StringComparer.OrdinalIgnoreCase);

    public UnknownItemsForm(string cadFile, List<(string Layer, int Lines)> layers, List<(string Block, string Layer, int Count, double SizeMm)> blocks)
    {
        Text = "CAD-REV · ¿Qué es esto?";
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = MinimizeBox = false;
        StartPosition = FormStartPosition.CenterParent;
        AutoSize = true;
        AutoSizeMode = AutoSizeMode.GrowAndShrink;
        Font = new System.Drawing.Font("Segoe UI", 9F);
        Padding = new Padding(12);

        var layout = new FlowLayoutPanel { FlowDirection = FlowDirection.TopDown, AutoSize = true, WrapContents = false };
        layout.Controls.Add(new Label
        {
            AutoSize = true, MaximumSize = new Size(760, 0), Font = new System.Drawing.Font(Font, FontStyle.Bold),
            Text = "El programa no reconoce estos nombres del plano. Diga qué es cada uno."
        });
        layout.Controls.Add(new Label
        {
            AutoSize = true, MaximumSize = new Size(760, 0), Margin = new Padding(3, 6, 3, 9),
            Text = $"Plano: {cadFile}\n\nLo que elija se RECUERDA para los siguientes planos (archivo {UserRules.DefaultPath}). " +
                   "Cada botón modela solo lo suyo: lo que marque como tubería lo modela «Tuberías», como luminaria «Luminarias»… " +
                   $"Deje «{Ask}» en lo que no sepa."
        });

        _grid.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = "Capa o bloque del plano", ReadOnly = true, FillWeight = 160 });
        _grid.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = "Qué es en el CAD", ReadOnly = true, FillWeight = 90 });
        var what = new DataGridViewComboBoxColumn { HeaderText = "Qué es en Revit", FillWeight = 150, FlatStyle = FlatStyle.Flat };
        what.Items.Add(Ask);
        foreach (var c in LayerChoices) what.Items.Add(c.Label);
        what.Items.Add(FixtureLayerChoice);
        foreach (var c in BlockChoices) what.Items.Add(c.Label);
        _grid.Columns.Add(what);

        foreach (var (layer, lines) in layers)
        {
            _rows.Add((true, layer));
            int r = _grid.Rows.Add(layer, $"capa · {lines} líneas", Ask);
            var cell = (DataGridViewComboBoxCell)_grid.Rows[r].Cells[2];
            cell.Items.Clear();
            cell.Items.Add(Ask);
            foreach (var c in LayerChoices) cell.Items.Add(c.Label);
            cell.Items.Add(FixtureLayerChoice);
        }
        foreach (var (block, blockLayer, count, size) in blocks)
        {
            _rows.Add((false, block));
            int r = _grid.Rows.Add(block, $"bloque · {count} uds. · {size / 1000:0.##} m · capa {blockLayer}", Ask);
            var cell = (DataGridViewComboBoxCell)_grid.Rows[r].Cells[2];
            cell.Items.Clear();
            cell.Items.Add(Ask);
            foreach (var c in BlockChoices) cell.Items.Add(c.Label);
        }
        layout.Controls.Add(_grid);

        var buttons = new FlowLayoutPanel { AutoSize = true, FlowDirection = FlowDirection.RightToLeft, Dock = DockStyle.Fill, Margin = new Padding(0, 9, 0, 0) };
        var ok = new Button { Text = "Guardar y seguir", AutoSize = true };
        var skip = new Button { Text = "Seguir sin decidir", AutoSize = true };
        var cancel = new Button { Text = "Cancelar", AutoSize = true, DialogResult = DialogResult.Cancel };
        ok.Click += (_, _) => { Collect(); DialogResult = DialogResult.OK; Close(); };
        skip.Click += (_, _) => { DialogResult = DialogResult.Ignore; Close(); };
        buttons.Controls.AddRange(new Control[] { ok, skip, cancel });
        layout.Controls.Add(buttons);
        Controls.Add(layout);
        CancelButton = cancel;
    }

    private void Collect()
    {
        _grid.EndEdit();
        for (int i = 0; i < _rows.Count; i++)
        {
            string? v = _grid.Rows[i].Cells[2].Value as string;
            if (v is null || v == Ask) continue;
            var (isLayer, name) = _rows[i];
            if (isLayer && v == FixtureLayerChoice) { FixtureLayerAnswers.Add(name); LayerAnswers[name] = MepKind.NoImportar; }
            else if (isLayer) { var c = LayerChoices.FirstOrDefault(x => x.Label == v); if (c.Label != null) LayerAnswers[name] = c.Kind; }
            else { var c = BlockChoices.FirstOrDefault(x => x.Label == v); if (c.Label != null) BlockAnswers[name] = c.Role; }
        }
    }
}
