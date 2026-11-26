using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using Autodesk.Revit.DB;
using WF = System.Windows.Forms;
using Color = System.Drawing.Color;
using View = Autodesk.Revit.DB.View;

namespace RevitAutomation.Addin.Commands.Planos;

/// <summary>
/// Configuration and summary before generating: what was found, what will be created and what is missing.
/// Nothing is changed in the project until «Generar» is pressed.
/// </summary>
internal sealed class PlanosForm : WF.Form
{
    private static readonly PlanosDiscipline[] All = { PlanosDiscipline.Fontaneria, PlanosDiscipline.Climatizacion, PlanosDiscipline.Electricidad, PlanosDiscipline.Iluminacion, PlanosDiscipline.Genericas };
    private static readonly int[] Scales = { 0, 50, 75, 100, 150, 200 };

    private readonly PlanosInventory _inv;
    private readonly WF.CheckedListBox _disciplines = new() { CheckOnClick = true, Width = 330, Height = 96 };
    private readonly WF.CheckedListBox _levels = new() { CheckOnClick = true, Width = 330, Height = 96 };
    private readonly WF.ComboBox _titleBlock = new() { DropDownStyle = WF.ComboBoxStyle.DropDownList, Width = 330 };
    private readonly WF.ComboBox _paper = new() { DropDownStyle = WF.ComboBoxStyle.DropDownList, Width = 120 };
    private readonly WF.NumericUpDown _bottom = new() { Minimum = 0, Maximum = 300, Value = 50, Width = 60 };
    private readonly WF.NumericUpDown _right = new() { Minimum = 0, Maximum = 400, Value = 0, Width = 60 };
    private readonly WF.ComboBox _scale = new() { DropDownStyle = WF.ComboBoxStyle.DropDownList, Width = 120 };
    private readonly WF.ComboBox _template = new() { DropDownStyle = WF.ComboBoxStyle.DropDownList, Width = 330 };
    private readonly WF.CheckBox _context = new() { AutoSize = true, Text = "Incluir el contexto arquitectónico (muros, pilares, forjados… en gris)", Checked = true };
    private readonly WF.CheckBox _schedules = new() { AutoSize = true, Text = "Incluir cuadros y leyendas (lámina «Leyendas y cuadros»)", Checked = true };
    private readonly WF.CheckBox _tags = new() { AutoSize = true, Text = "Etiquetar con las familias de etiqueta del proyecto", Checked = true };
    private readonly WF.TextBox _separator = new() { Width = 30, MaxLength = 1 };
    private readonly WF.NumericUpDown _digits = new() { Minimum = 1, Maximum = 4, Value = 2, Width = 45 };
    private readonly WF.RadioButton _full = new() { AutoSize = true, Text = "Vistas y láminas", Checked = true };
    private readonly WF.RadioButton _viewsOnly = new() { AutoSize = true, Text = "Solo vistas" };
    private readonly WF.RadioButton _sheetsOnly = new() { AutoSize = true, Text = "Solo láminas (con las vistas generadas antes)" };
    private readonly WF.TextBox _summary = new() { Multiline = true, ReadOnly = true, ScrollBars = WF.ScrollBars.Vertical, Width = 470, Height = 470, BackColor = SystemColors.Window };
    private readonly WF.Button _ok = new() { Text = "Generar", AutoSize = true, DialogResult = WF.DialogResult.OK };
    // «Conjunto completo»: desplegable para elegir qué generar (empieza sin nada marcado).
    private readonly WF.ComboBox _what = new() { DropDownStyle = WF.ComboBoxStyle.DropDownList, Width = 330 };
    // Visualización («render») y PDF.
    private static readonly (PlanosStyle Style, string Label)[] Styles =
    {
        (PlanosStyle.LineasOcultas, "Líneas ocultas (plano técnico)"), (PlanosStyle.Sombreado, "Sombreado"),
        (PlanosStyle.ColoresCoherentes, "Colores coherentes"), (PlanosStyle.Realista, "Realista"), (PlanosStyle.Alambrico, "Alámbrico")
    };
    private static readonly (PlanosDetail Detail, string Label)[] Details =
        { (PlanosDetail.Bajo, "Bajo"), (PlanosDetail.Medio, "Medio"), (PlanosDetail.Alto, "Alto") };
    private readonly WF.ComboBox _style = new() { DropDownStyle = WF.ComboBoxStyle.DropDownList, Width = 200 };
    private readonly WF.ComboBox _detail = new() { DropDownStyle = WF.ComboBoxStyle.DropDownList, Width = 90 };
    private readonly WF.CheckBox _view3d = new() { AutoSize = true, Text = "Añadir una vista 3D por disciplina (solo sus instalaciones), con su lámina" };
    private readonly WF.CheckBox _pdf = new() { AutoSize = true, Text = "Guardar un PDF combinado en el Escritorio al terminar", Checked = true };
    private readonly bool _choose;
    private PlanosPlan? _plan;

    internal PlanosChoices Choices { get; } = new PlanosChoices();
    internal PlanosPlan Plan => _plan ?? new PlanosPlan();

    internal PlanosForm(string title, PlanosInventory inv, IReadOnlyCollection<PlanosDiscipline> initial, bool chooseDisciplines = false)
    {
        _inv = inv;
        _choose = chooseDisciplines;
        Text = "RevitAutomation · " + title;
        Font = new Font("Segoe UI", 9F);
        FormBorderStyle = WF.FormBorderStyle.FixedDialog;
        MaximizeBox = MinimizeBox = false;
        StartPosition = WF.FormStartPosition.CenterParent;
        AutoSize = true;
        AutoSizeMode = WF.AutoSizeMode.GrowAndShrink;
        Padding = new WF.Padding(12);

        var left = new WF.FlowLayoutPanel { FlowDirection = WF.FlowDirection.TopDown, AutoSize = true, WrapContents = false };
        WF.Label Head(string t) => new WF.Label { Text = t, AutoSize = true, Font = new Font(Font, FontStyle.Bold), Margin = new WF.Padding(0, 8, 0, 2) };
        WF.Label Hint(string t) => new WF.Label { Text = t, AutoSize = true, MaximumSize = new Size(340, 0), ForeColor = SystemColors.GrayText };
        WF.FlowLayoutPanel Row(params WF.Control[] cs)
        {
            var r = new WF.FlowLayoutPanel { AutoSize = true, WrapContents = false, Margin = new WF.Padding(0) };
            foreach (var c in cs) { if (c is WF.Label) c.Margin = new WF.Padding(0, 6, 4, 0); r.Controls.Add(c); }
            return r;
        }
        WF.Label L(string t) => new WF.Label { Text = t, AutoSize = true };

        if (_choose)
        {
            left.Controls.Add(Head("0 · ¿Qué quiere generar?"));
            _what.Items.Add("— Elija qué generar —");
            _what.Items.Add("Todas las disciplinas con elementos");
            foreach (var d in All) _what.Items.Add($"Solo {PlanosNaming.DisciplineLabel(d)} ({inv.Of(d).Count()} elementos)");
            _what.SelectedIndex = 0;
            left.Controls.Add(_what);
            left.Controls.Add(Hint("Después puede ajustar la selección en la lista de disciplinas."));
        }
        left.Controls.Add(Head("1 · Disciplinas"));
        foreach (var d in All)
        {
            int n = inv.Of(d).Count();
            _disciplines.Items.Add($"{PlanosNaming.DisciplineLabel(d)} ({n} elementos)", !_choose && initial.Contains(d) && n > 0);
        }
        left.Controls.Add(_disciplines);

        left.Controls.Add(Head("2 · Niveles"));
        for (int i = 0; i < inv.PlanLevels.Count; i++)
        {
            int n = inv.Elements.Count(e => e.Level == i);
            _levels.Items.Add($"{inv.PlanLevels[i].Name} ({inv.PlanLevels[i].Elevation * 0.3048:+0.00;-0.00} m) · {n} elementos", n > 0);
        }
        left.Controls.Add(_levels);

        left.Controls.Add(Head("3 · Lámina"));
        _titleBlock.Items.Add("(sin cajetín)");
        foreach (var tb in inv.TitleBlocks)
            _titleBlock.Items.Add(tb.FamilyName + " : " + tb.Name + (inv.TitleBlockSizes.TryGetValue(tb.Id, out var sz) ? $" ({sz.W:0} × {sz.H:0} mm)" : ""));
        left.Controls.Add(Row(L("Cajetín:"), _titleBlock));
        foreach (var f in PaperFormats.All) _paper.Items.Add(f.Name + $" ({f.W:0} × {f.H:0})");
        left.Controls.Add(Row(L("Formato:"), _paper, L("Escala:"), _scale));
        left.Controls.Add(Row(L("Reservar para el cajetín: abajo"), _bottom, L("mm  derecha"), _right, L("mm")));
        left.Controls.Add(Hint("Las vistas se colocan dentro del cajetín, con 10 mm de margen, sin invadir estas franjas."));
        foreach (int s in Scales) _scale.Items.Add(s == 0 ? "Automática" : "1:" + s);
        _scale.SelectedIndex = 0;
        _titleBlock.SelectedIndex = inv.TitleBlocks.Count > 0 ? 1 : 0;

        left.Controls.Add(Head("4 · Vistas"));
        _template.Items.Add("(ninguna: ajustes de RevitAutomation)");
        foreach (var t in inv.PlanTemplates) _template.Items.Add(t.Name);
        _template.SelectedIndex = 0;
        left.Controls.Add(Row(L("Plantilla:"), _template));
        left.Controls.Add(Hint("Se copian sus ajustes a las vistas nuevas; la plantilla no se modifica ni se asigna."));
        left.Controls.Add(_context);
        left.Controls.Add(_schedules);
        left.Controls.Add(_tags);

        left.Controls.Add(Head("5 · Numeración y modo"));
        var numbering = SheetNumbering.Detect(inv.SheetNumbers);
        _separator.Text = numbering.Separator;
        _digits.Value = numbering.Digits;
        left.Controls.Add(Row(L("Separador:"), _separator, L("Cifras:"), _digits));
        left.Controls.Add(Hint(numbering.Detected ? "Convención detectada en las láminas del proyecto." : "No se ha detectado convención: se propone F-01, C-01, E-01, I-01, FG-01."));
        left.Controls.Add(_full);
        left.Controls.Add(_viewsOnly);
        left.Controls.Add(_sheetsOnly);

        left.Controls.Add(Head("6 · Visualización (render) y PDF"));
        foreach (var st in Styles) _style.Items.Add(st.Label);
        foreach (var dt in Details) _detail.Items.Add(dt.Label);
        _style.SelectedIndex = 0;
        _detail.SelectedIndex = 1;
        left.Controls.Add(Row(L("Estilo visual:"), _style, L("Detalle:"), _detail));
        left.Controls.Add(_view3d);
        left.Controls.Add(_pdf);
        left.Controls.Add(Hint("El PDF lleva las láminas generadas (en «Solo vistas», las vistas), en orden de número."));

        var right = new WF.FlowLayoutPanel { FlowDirection = WF.FlowDirection.TopDown, AutoSize = true, WrapContents = false, Margin = new WF.Padding(16, 0, 0, 0) };
        right.Controls.Add(Head("Resumen (no se crea nada hasta pulsar «Generar»)"));
        right.Controls.Add(_summary);
        var buttons = new WF.FlowLayoutPanel { FlowDirection = WF.FlowDirection.RightToLeft, AutoSize = true, Width = 470, Margin = new WF.Padding(0, 10, 0, 0) };
        var cancel = new WF.Button { Text = "Cancelar", AutoSize = true, DialogResult = WF.DialogResult.Cancel };
        buttons.Controls.Add(cancel);
        buttons.Controls.Add(_ok);
        right.Controls.Add(buttons);

        var root = new WF.FlowLayoutPanel { AutoSize = true, WrapContents = false };
        root.Controls.Add(left);
        root.Controls.Add(right);
        Controls.Add(root);
        AcceptButton = _ok;
        CancelButton = cancel;

        _titleBlock.SelectedIndexChanged += (_, _) => { PaperFromTitleBlock(); Refresh2(); };
        PaperFromTitleBlock();
        foreach (var c in new WF.Control[] { _paper, _scale, _template, _separator })
        {
            if (c is WF.ComboBox cb) cb.SelectedIndexChanged += (_, _) => Refresh2();
            else c.TextChanged += (_, _) => Refresh2();
        }
        foreach (var n in new[] { _bottom, _right, _digits }) n.ValueChanged += (_, _) => Refresh2();
        foreach (var c in new[] { _context, _schedules, _tags, _view3d, _pdf }) c.CheckedChanged += (_, _) => Refresh2();
        _style.SelectedIndexChanged += (_, _) => Refresh2();
        _detail.SelectedIndexChanged += (_, _) => Refresh2();
        _what.SelectedIndexChanged += (_, _) => ApplyWhat();
        foreach (var r in new[] { _full, _viewsOnly, _sheetsOnly }) r.CheckedChanged += (_, _) => Refresh2();
        _disciplines.ItemCheck += (_, _) => BeginInvoke(new Action(Refresh2));
        _levels.ItemCheck += (_, _) => BeginInvoke(new Action(Refresh2));
        _ok.Click += (_, _) => { if (!Accept()) DialogResult = WF.DialogResult.None; };
        Refresh2();
    }

    /// <summary>«Conjunto completo»: marca en la lista lo elegido en el desplegable.</summary>
    private void ApplyWhat()
    {
        if (_what.SelectedIndex <= 0) return;
        for (int i = 0; i < All.Length; i++)
        {
            bool has = _inv.Of(All[i]).Any();
            _disciplines.SetItemChecked(i, has && (_what.SelectedIndex == 1 || _what.SelectedIndex - 2 == i));
        }
        Refresh2();
    }

    private void PaperFromTitleBlock()
    {
        int index = 1; // A1 by default
        if (_titleBlock.SelectedIndex > 0)
        {
            var tb = _inv.TitleBlocks[_titleBlock.SelectedIndex - 1];
            string? name = _inv.TitleBlockSizes.TryGetValue(tb.Id, out var sz) ? PaperFormats.Closest(sz.W, sz.H) : null;
            // Without a sheet to measure, the type or family name may say the format ("A3", "A1"…): only a suggestion.
            name ??= PaperFormats.All.Select(f => f.Name).FirstOrDefault(f => (tb.Name + " " + tb.FamilyName).ToUpperInvariant().Contains(f));
            if (name is not null) index = Array.FindIndex(PaperFormats.All, f => f.Name == name);
        }
        _paper.SelectedIndex = Math.Max(0, index);
    }

    private void Read(PlanosOptions o)
    {
        o.Disciplines.Clear();
        for (int i = 0; i < All.Length; i++) if (_disciplines.GetItemChecked(i)) o.Disciplines.Add(All[i]);
        o.LevelKeys.Clear();
        for (int i = 0; i < _inv.PlanLevels.Count; i++) if (_levels.GetItemChecked(i)) o.LevelKeys.Add(_inv.PlanLevels[i].Key);
        var f = PaperFormats.All[Math.Max(0, _paper.SelectedIndex)];
        o.PaperWMm = f.W; o.PaperHMm = f.H;
        o.ReserveBottomMm = (double)_bottom.Value;
        o.ReserveRightMm = (double)_right.Value;
        o.Scale = Scales[Math.Max(0, _scale.SelectedIndex)];
        o.Context = _context.Checked;
        o.Schedules = _schedules.Checked;
        o.Tags = _tags.Checked;
        o.Mode = _viewsOnly.Checked ? PlanosMode.SoloVistas : _sheetsOnly.Checked ? PlanosMode.SoloLaminas : PlanosMode.Completo;
        o.Numbering = new SheetNumbering(_separator.Text, (int)_digits.Value);
        o.Style = Styles[Math.Max(0, _style.SelectedIndex)].Style;
        o.Detail = Details[Math.Max(0, _detail.SelectedIndex)].Detail;
        o.View3D = _view3d.Checked;
        o.Pdf = _pdf.Checked;
    }

    private void Refresh2()
    {
        var o = Choices.Options;
        Read(o);
        var lines = new List<string>();
        _plan = PlanosPlanner.Plan(_inv.PlanLevels, _inv.Items(), o);

        lines.Add("ELEMENTOS IDENTIFICADOS");
        foreach (var d in All)
        {
            var mine = _inv.Of(d).ToList();
            if (mine.Count == 0) { lines.Add($"  {PlanosNaming.DisciplineLabel(d)}: ninguno"); continue; }
            int assumed = mine.Count(e => e.Class.AssumedByCategory);
            string detail = d == PlanosDiscipline.Fontaneria
                ? " (" + string.Join(", ", PlanosPlanner.PlumbingNetworks.Select(n => (n, c: mine.Count(e => e.Class.Network == n))).Where(x => x.c > 0).Select(x => PlanosNaming.NetworkLabel(x.n).ToLowerInvariant() + " " + x.c)) + ")"
                : "";
            lines.Add($"  {PlanosNaming.DisciplineLabel(d)}: {mine.Count}{detail}" + (assumed > 0 ? $"; {assumed} sin sistema o con sistema «Otro» (revíselos)" : ""));
        }
        lines.Add($"  Niveles del proyecto: {_inv.PlanLevels.Count}. Láminas existentes: {_inv.Sheets}.");
        lines.Add("");

        if (o.Mode == PlanosMode.SoloLaminas)
        {
            lines.Add("SE CREARÁN (con las vistas generadas antes)");
            foreach (var d in o.Disciplines)
            {
                int views = _inv.OwnedOf(d, PlanosMark.Plan).Count();
                int extras = _inv.OwnedOf(d, PlanosMark.Schedule).Count() + _inv.OwnedOf(d, PlanosMark.Legend).Count();
                lines.Add(views == 0 ? $"  {PlanosNaming.DisciplineLabel(d)}: no hay vistas generadas antes (use «Vistas y láminas»)."
                    : $"  {PlanosNaming.DisciplineLabel(d)}: {views} láminas de planta" + (o.Schedules && extras > 0 ? " + leyendas y cuadros" : ""));
            }
        }
        else
        {
            lines.Add("SE CREARÁN");
            lines.Add($"  Vistas en planta: {_plan.Views.Count}" + (o.Mode == PlanosMode.SoloVistas ? " (sin láminas)" : ""));
            if (o.Mode == PlanosMode.Completo)
                lines.Add($"  Láminas: {_plan.PlanSheets} de planta + {_plan.LegendSheets.Count} de leyendas y cuadros (más si no caben)");
            var numbering = o.Numbering;
            foreach (var g in _plan.Views.GroupBy(v => v.Discipline))
            {
                lines.Add($"  {PlanosNaming.DisciplineLabel(g.Key).ToUpperInvariant()}");
                int n = 1;
                foreach (var v in g)
                    lines.Add($"    {(o.Mode == PlanosMode.Completo ? numbering.Format(PlanosNaming.Prefix(g.Key), n++) + " " : "")}{v.SheetName} · 1:{v.Scale}");
                if (o.Mode == PlanosMode.Completo && _plan.LegendSheets.Contains(g.Key))
                    lines.Add($"    {numbering.Format(PlanosNaming.Prefix(g.Key), n)} {PlanosNaming.LegendSheetName(g.Key)}");
            }
            lines.Add("  (Números orientativos: se saltan los que ya existen en el proyecto.)");
        }
        lines.Add("");

        lines.Add("VISUALIZACIÓN Y PDF");
        lines.Add($"  Estilo visual: {Styles[Math.Max(0, _style.SelectedIndex)].Label} · detalle {Details[Math.Max(0, _detail.SelectedIndex)].Label.ToLowerInvariant()}" +
                  (o.Mode == PlanosMode.SoloLaminas ? " (no se aplica: se reutilizan las vistas generadas antes)" : ""));
        if (o.View3D && o.Mode != PlanosMode.SoloLaminas) lines.Add($"  Vistas 3D: una por disciplina ({o.Disciplines.Count})" + (o.Mode == PlanosMode.Completo ? ", cada una en su lámina" : ""));
        lines.Add(o.Pdf ? "  PDF: se guardará en el Escritorio un PDF combinado con " + (o.Mode == PlanosMode.SoloVistas ? "las vistas." : "las láminas.") : "  PDF: no.");
        lines.Add("");

        lines.Add("SE SUSTITUIRÁ (solo lo generado antes por este módulo)");
        int oldViews = o.Mode == PlanosMode.SoloLaminas ? 0 : o.Disciplines.Sum(d => _inv.OwnedOf(d, PlanosMark.Plan).Count());
        int oldSheets = o.Disciplines.Sum(d => _inv.OwnedOf(d, PlanosMark.Sheet).Count());
        lines.Add(oldViews + oldSheets == 0 ? "  Nada: es la primera generación de estas disciplinas." : $"  {oldViews} vistas y {oldSheets} láminas de una generación anterior.");
        lines.Add("  Las vistas, láminas y plantillas creadas por usted no se tocan.");
        lines.Add("");

        lines.Add("ANOTACIÓN");
        var needed = o.Disciplines.SelectMany(d => _inv.Of(d)).Select(e => PlanosInventory.TagCategoryOf.TryGetValue(e.Category, out var t) ? t : null)
            .Where(t => t is not null).Select(t => t!).Distinct().OrderBy(t => t).ToList();
        foreach (var t in needed)
            lines.Add(_inv.TagTypes.TryGetValue(t, out var s) ? $"  ✓ {PlanosInventory.TagLabel(t)}: {s.FamilyName}" : $"  ✗ falta {PlanosInventory.TagLabel(t)} (esos elementos no se etiquetan)");
        if (needed.Count == 0) lines.Add("  Sin elementos que etiquetar.");
        lines.Add("  No se generan cotas: no hay referencias geométricas fiables para acotar instalaciones automáticamente.");
        lines.Add("");

        var missing = new List<string>();
        if (o.Disciplines.Count == 0) missing.Add(_choose ? "Elija en «¿Qué quiere generar?» (o en la lista) al menos una disciplina con elementos." : "Elija al menos una disciplina con elementos.");
        if (o.LevelKeys.Count == 0) missing.Add("Elija al menos un nivel.");
        if (_titleBlock.SelectedIndex == 0 && o.Mode != PlanosMode.SoloVistas) missing.Add(_inv.TitleBlocks.Count == 0 ? "El proyecto no tiene cajetines: las láminas se crearán sin cajetín (cargue uno en Insertar > Cargar familia > Cajetines)." : "Ha elegido láminas sin cajetín.");
        if (_titleBlock.SelectedIndex > 0 && !_inv.TitleBlockSizes.ContainsKey(_inv.TitleBlocks[_titleBlock.SelectedIndex - 1].Id))
            missing.Add("No se ha podido medir el cajetín elegido (no está en ninguna lámina): compruebe que el formato coincide; al colocar se usa su tamaño real.");
        if (o.Disciplines.Contains(PlanosDiscipline.Iluminacion) && _inv.CeilingPlanType is null) missing.Add("No hay tipo de vista de techo reflejado: la iluminación irá en planta.");
        if (_inv.FloorPlanType is null) missing.Add("El proyecto no tiene ningún tipo de vista de planta: no se pueden crear vistas.");
        missing.AddRange(_plan.Notes);
        lines.Add("DATOS QUE FALTAN O REQUIEREN SU ATENCIÓN");
        lines.AddRange(missing.Count == 0 ? new[] { "  Ninguno." } : missing.Select(m => "  • " + m));
        lines.Add("");
        lines.Add("El modelo no se modifica: solo se crean vistas, láminas, cuadros, leyendas, filtros y etiquetas. Todo se puede deshacer con Ctrl+Z.");

        _summary.Text = string.Join(Environment.NewLine, lines);
        bool nothing = o.Mode == PlanosMode.SoloLaminas ? !o.Disciplines.Any(d => _inv.OwnedOf(d, PlanosMark.Plan).Any()) : _plan.Views.Count == 0;
        _ok.Enabled = o.Disciplines.Count > 0 && !nothing && _inv.FloorPlanType is not null;
    }

    private bool Accept()
    {
        Read(Choices.Options);
        Choices.TitleBlockId = _titleBlock.SelectedIndex > 0 ? _inv.TitleBlocks[_titleBlock.SelectedIndex - 1].Id : ElementId.InvalidElementId;
        Choices.Template = _template.SelectedIndex > 0 ? _inv.PlanTemplates[_template.SelectedIndex - 1] : null;
        _plan = PlanosPlanner.Plan(_inv.PlanLevels, _inv.Items(), Choices.Options);
        return true;
    }
}

