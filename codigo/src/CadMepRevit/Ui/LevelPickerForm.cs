using System.Drawing;
using System.Windows.Forms;
using Autodesk.Revit.DB;
using Form = System.Windows.Forms.Form;
using Control = System.Windows.Forms.Control;

namespace CadMepRevit.Ui;

/// <summary>
/// La ÚNICA pregunta de «Modelar desde CAD»: en qué planta (nivel de Revit) se modela el plano.
/// Solo se muestra si el proyecto tiene más de un nivel. Propone el más bajo, como hacía el asistente.
/// </summary>
public sealed class LevelPickerForm : Form
{
    private readonly ListBox _list = new() { Width = 420, Height = 220, IntegralHeight = false };
    private readonly IReadOnlyList<(string Name, ElementId Id, double ElevationFt)> _levels;

    public (string Name, ElementId Id, double ElevationFt)? Selected { get; private set; }

    public LevelPickerForm(IReadOnlyList<(string Name, ElementId Id, double ElevationFt)> levels, string cadFile)
    {
        _levels = levels;
        Text = "CAD-REV · Elegir planta";
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
            AutoSize = true, MaximumSize = new Size(440, 0), Font = new System.Drawing.Font(Font, FontStyle.Bold),
            Text = "¿En qué planta se modela este plano?"
        });
        layout.Controls.Add(new Label
        {
            AutoSize = true, MaximumSize = new Size(440, 0), Margin = new Padding(3, 6, 3, 9),
            Text = $"Plano: {cadFile}\n\nEl proyecto tiene {levels.Count} niveles. Elija el de la planta que dibuja el plano. " +
                   "Las tuberías y los conductos se colocan con su eje a 2,50 m sobre ese nivel. " +
                   "Es la única pregunta: el resto se hace solo y al final verá un informe. Ctrl+Z deshace todo."
        });
        foreach (var l in levels)
            _list.Items.Add($"{l.Name}   (cota {l.ElevationFt * 304.8 / 1000:0.00} m)");
        _list.SelectedIndex = 0;
        _list.DoubleClick += (_, _) => Accept();
        layout.Controls.Add(_list);

        var buttons = new FlowLayoutPanel { AutoSize = true, FlowDirection = FlowDirection.RightToLeft, Dock = DockStyle.Fill, Margin = new Padding(0, 9, 0, 0) };
        var ok = new Button { Text = "Modelar en esta planta", AutoSize = true };
        var cancel = new Button { Text = "Cancelar", AutoSize = true, DialogResult = DialogResult.Cancel };
        ok.Click += (_, _) => Accept();
        buttons.Controls.AddRange(new Control[] { ok, cancel });
        layout.Controls.Add(buttons);
        Controls.Add(layout);
        AcceptButton = ok;
        CancelButton = cancel;
    }

    private void Accept()
    {
        if (_list.SelectedIndex < 0) return;
        Selected = _levels[_list.SelectedIndex];
        DialogResult = DialogResult.OK;
        Close();
    }
}

/// <summary>Ventana de progreso: dice qué se está haciendo. Se actualiza sin procesar clics (no admite interrupciones).</summary>
public sealed class ProgressWindow : Form
{
    private readonly Label _label = new() { AutoSize = false, Width = 460, Height = 40 };

    public ProgressWindow(string title)
    {
        Text = "CAD-REV · " + title;
        FormBorderStyle = FormBorderStyle.FixedToolWindow;
        ControlBox = false;
        ShowInTaskbar = false;
        StartPosition = FormStartPosition.CenterScreen;
        TopMost = true;
        Font = new System.Drawing.Font("Segoe UI", 9F);
        ClientSize = new Size(490, 64);
        _label.Location = new System.Drawing.Point(14, 12);
        Controls.Add(_label);
    }

    public void Report(string text)
    {
        if (IsDisposed) return;
        _label.Text = text;
        Refresh();
    }
}
