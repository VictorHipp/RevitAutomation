using System.Drawing;
using System.Windows.Forms;
using Form = System.Windows.Forms.Form;
using Control = System.Windows.Forms.Control;

namespace CadMepRevit.Ui;

/// <summary>Ventana sencilla para elegir una opción de una lista (p. ej. qué planta del plano va en el nivel).</summary>
public sealed class ChoiceForm : Form
{
    private readonly ListBox _list = new() { Width = 520, Height = 200, IntegralHeight = false };

    /// <summary>Opción elegida (-1 si ninguna).</summary>
    public int SelectedIndex => _list.SelectedIndex;

    public ChoiceForm(string title, string explanation, IReadOnlyList<string> options)
    {
        Text = title;
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = MinimizeBox = false;
        StartPosition = FormStartPosition.CenterParent;
        AutoSize = true;
        AutoSizeMode = AutoSizeMode.GrowAndShrink;
        Font = new Font("Segoe UI", 9F);
        Padding = new Padding(12);

        var layout = new FlowLayoutPanel { FlowDirection = FlowDirection.TopDown, AutoSize = true, WrapContents = false };
        layout.Controls.Add(new Label { AutoSize = true, MaximumSize = new Size(520, 0), Text = explanation, Margin = new Padding(3, 3, 3, 9) });
        foreach (var o in options) _list.Items.Add(o);
        if (_list.Items.Count > 0) _list.SelectedIndex = 0;
        layout.Controls.Add(_list);

        var buttons = new FlowLayoutPanel { AutoSize = true, FlowDirection = FlowDirection.RightToLeft, Dock = DockStyle.Fill, Margin = new Padding(0, 9, 0, 0) };
        var ok = new Button { Text = "Aceptar", AutoSize = true, DialogResult = DialogResult.OK };
        var cancel = new Button { Text = "Cancelar", AutoSize = true, DialogResult = DialogResult.Cancel };
        buttons.Controls.AddRange(new Control[] { ok, cancel });
        layout.Controls.Add(buttons);
        Controls.Add(layout);
        AcceptButton = ok;
        CancelButton = cancel;
        _list.DoubleClick += (_, _) => { if (_list.SelectedIndex >= 0) { DialogResult = DialogResult.OK; Close(); } };
    }
}
