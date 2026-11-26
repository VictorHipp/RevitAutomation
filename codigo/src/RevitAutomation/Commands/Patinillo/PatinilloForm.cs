#nullable enable
using System;
using System.Collections.Generic;
using System.Drawing;
using System.Globalization;
using System.Linq;
using System.Windows.Forms;

namespace RevitAutomation.Addin.Commands.Patinillo;

public sealed class PatinilloForm : Form
{
    private static readonly CultureInfo Spanish = CultureInfo.GetCultureInfo("es-ES");
    private readonly ToolTip _tips = new() { AutoPopDelay = 15000 };
    private readonly NumericUpDown _dwellings = Number(1, 20), _width = Number(1, 20000),
        _depth = Number(1, 20000), _gap = Number(0, 1000), _below = Number(0, 10000),
        _slab = Number(1, 2000), _length = Number(1, 20000), _start = Number(0, 10000),
        _floors = Number(1, 60), _height = Number(100, 20000);
    private readonly CheckBox _ground = new() { Text = "La planta baja también tiene viviendas", AutoSize = true },
        _return = new() { Text = "Retorno de ACS", AutoSize = true };
    private readonly ComboBox _cold = Diameter(), _hot = Diameter(), _returnDiameter = Diameter(),
        _type = Choice(), _coldSystem = Choice(), _hotSystem = Choice(), _returnSystem = Choice();
    private readonly RadioButton _cap = new() { Text = "Tapón", AutoSize = true },
        _open = new() { Text = "Extremo abierto", AutoSize = true };

    public PatinilloSettings Settings { get; }
    public PatinilloAction Action { get; private set; } = PatinilloAction.Cancelar;

    public PatinilloForm(PatinilloSettings settings, PatinilloModelOptions options)
    {
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(options);
        Settings = settings.Clone();
        Text = "Patinillo de fontanería";
        Font = new Font("Segoe UI", 9F);
        AutoScaleDimensions = new SizeF(96, 96);
        AutoScaleMode = AutoScaleMode.Dpi;
        ClientSize = new Size(780, 610);
        FormBorderStyle = FormBorderStyle.FixedDialog;
        StartPosition = FormStartPosition.CenterScreen;
        MaximizeBox = MinimizeBox = false;
        Padding = new Padding(12);

        var root = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 2 };
        root.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        var scroll = new Panel { Dock = DockStyle.Fill, AutoScroll = true, Margin = new Padding(0) };
        var content = new TableLayoutPanel { AutoSize = true, Dock = DockStyle.Top, ColumnCount = 1, Padding = new Padding(0, 0, 18, 0) };
        content.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        scroll.Controls.Add(content);
        root.Controls.Add(scroll, 0, 0);
        Controls.Add(root);

        var building = Section(content, "1 · Edificio");
        Field(building, "Viviendas por planta", _dwellings, "Viviendas de cada planta; habitual: 2.");
        Field(building, "Planta baja", _ground, "Actívelo si hay viviendas en PB; habitual: desactivado.");
        var levels = new TextBox { Multiline = true, ReadOnly = true, ScrollBars = ScrollBars.Vertical,
            Height = 64, BackColor = SystemColors.Window,
            Text = string.Join(Environment.NewLine, options.Levels.Select(l => l.Name + " · " + (l.ElevationMm / 1000).ToString("0.00", Spanish) + " m")) };
        Field(building, "Niveles del modelo", levels, "La planta superior se toma como cubierta. Habitual: PB y plantas de viviendas.");

        var pipes = Section(content, "2 · Tuberías por vivienda");
        Field(pipes, "Agua fría Ø (mm)", _cold, "Diámetro nominal de agua fría por vivienda; habitual: 25 mm.");
        Field(pipes, "ACS Ø (mm)", _hot, "Diámetro nominal de agua caliente sanitaria; habitual: 20 mm.");
        Field(pipes, "Recirculación", _return, "Mantiene caliente el circuito de ACS; habitual: desactivado.");
        Field(pipes, "Retorno Ø (mm)", _returnDiameter, "Diámetro nominal del retorno de ACS; habitual: 20 mm.");
        Populate(_type, options.PipeTypeNames, settings.PipeTypeName);
        Populate(_coldSystem, options.ColdSystemNames, settings.ColdSystemName);
        Populate(_hotSystem, options.HotSystemNames, settings.HotSystemName);
        Populate(_returnSystem, options.ReturnSystemNames, settings.ReturnSystemName);
        Field(pipes, "Tipo de tubería", _type, "Tipo cargado en el proyecto; habitual: el tipo de fontanería disponible.");
        Field(pipes, "Sistema agua fría", _coldSystem, "Sistema para el agua de red; habitual: Agua fría sanitaria.");
        Field(pipes, "Sistema agua caliente", _hotSystem, "Sistema para el agua caliente; habitual: Agua caliente sanitaria.");
        Field(pipes, "Sistema retorno", _returnSystem, "Sistema de recirculación de ACS; habitual: Retorno ACS.");
        _return.CheckedChanged += (_, _) => UpdateReturn();

        var shaft = Section(content, "3 · Patinillo");
        Field(shaft, "Ancho interior (lado de la boca) · mm", _width, "Ancho libre del lado de salida del peine; habitual: 600 mm.");
        Field(shaft, "Fondo interior (mm)", _depth, "Distancia libre desde la boca hasta el fondo; habitual: 400 mm.");
        Field(shaft, "Separación libre entre tuberías (mm)", _gap, "Hueco libre entre caras exteriores de tubos; habitual: 50 mm.");
        var source = new Label { Text = options.ShaftSourceText, AutoSize = true, Dock = DockStyle.Top,
            Font = new Font(Font, FontStyle.Italic), Margin = new Padding(0, 4, 0, 8) };
        shaft.Controls.Add(source);
        _tips.SetToolTip(source, options.ShaftSourceText);

        var comb = Section(content, "4 · Peine de salida");
        Field(comb, "Salida bajo la cara inferior del forjado (mm)", _below, "Distancia bajo el forjado hasta la salida; habitual: 300 mm.");
        Field(comb, "Espesor del forjado (mm)", _slab, "Espesor para localizar la cara inferior; habitual: 300 mm.");
        Field(comb, "Longitud fuera del patinillo (mm)", _length, "Tramo que sobresale de la boca; habitual: 500 mm.");
        var ends = new FlowLayoutPanel { AutoSize = true, WrapContents = true };
        ends.Controls.AddRange(new Control[] { _cap, _open });
        Field(comb, "Final", ends, "Tapón cierra el tubo; extremo abierto permite continuarlo. Habitual: tapón.");
        var start = Section(content, "5 · Arranque");
        Field(start, "Altura sobre el suelo de planta baja (mm)", _start, "Cota inicial de los montantes sobre PB; habitual: 500 mm.");

        var test = Section(content, "6 · Edificio de prueba");
        var expand = new CheckBox { Text = "Mostrar opciones de edificio de prueba", AutoSize = true };
        test.Controls.Add(expand);
        _tips.SetToolTip(expand, "Despliega la creación de un edificio sencillo; habitual: cerrado.");
        var testFields = new TableLayoutPanel { AutoSize = true, Dock = DockStyle.Top, ColumnCount = 1, Visible = false };
        testFields.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        test.Controls.Add(testFields);
        expand.CheckedChanged += (_, _) => testFields.Visible = expand.Checked;
        Field(testFields, "Plantas sobre rasante", _floors, "Número de plantas por encima de PB; habitual: 4.");
        Field(testFields, "Altura entre plantas (mm)", _height, "Distancia entre niveles consecutivos; habitual: 3000 mm.");
        var create = new Button { Text = "Crear edificio de prueba", AutoSize = true, Enabled = options.CanCreateTestBuilding };
        Field(testFields, "Crear modelo de prueba", create, options.CanCreateTestBuilding
            ? "Crea niveles y forjados de prueba con estos valores. Habitual: solo para ensayos."
            : "Solo en un proyecto sin suelos y con dos niveles como máximo");
        create.Click += (_, _) => TryAccept(PatinilloAction.CrearEdificioPrueba);

        var buttons = new TableLayoutPanel { AutoSize = true, Dock = DockStyle.Fill, ColumnCount = 2, Margin = new Padding(0, 10, 0, 0) };
        buttons.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        buttons.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        var reset = new Button { Text = "Restablecer valores por defecto", AutoSize = true };
        reset.Click += (_, _) => LoadValues(new PatinilloSettings());
        buttons.Controls.Add(reset, 0, 0);
        var right = new FlowLayoutPanel { AutoSize = true, WrapContents = false, FlowDirection = FlowDirection.RightToLeft };
        var preview = new Button { Text = "Vista previa…", AutoSize = true };
        var cancel = new Button { Text = "Cancelar", AutoSize = true, DialogResult = DialogResult.Cancel };
        preview.Click += (_, _) => TryAccept(PatinilloAction.Modelar);
        right.Controls.AddRange(new Control[] { preview, cancel });
        buttons.Controls.Add(right, 1, 0);
        root.Controls.Add(buttons, 0, 1);
        AcceptButton = preview;
        CancelButton = cancel;
        LoadValues(Settings);
    }

    protected override void OnLoad(EventArgs e)
    {
        base.OnLoad(e);
        // Apply the work-area limit after WinForms has scaled all controls for this monitor.
        var area = Screen.FromControl(this).WorkingArea;
        Size = new Size(Math.Min(Width, area.Width - 24), Math.Min(Height, area.Height - 24));
        Location = new Point(area.Left + (area.Width - Width) / 2, area.Top + (area.Height - Height) / 2);
    }

    private static NumericUpDown Number(decimal min, decimal max) => new()
        { Minimum = min, Maximum = max, DecimalPlaces = 0, ThousandsSeparator = true, Width = 140 };
    private static ComboBox Diameter()
    {
        var c = new ComboBox { DropDownStyle = ComboBoxStyle.DropDown, Width = 140 };
        c.Items.AddRange(new object[] { "16", "20", "25", "32", "40", "50" });
        return c;
    }
    private static ComboBox Choice() => new() { DropDownStyle = ComboBoxStyle.DropDownList };
    private static void Populate(ComboBox c, IReadOnlyList<string> names, string saved)
    {
        foreach (string name in names) c.Items.Add(name);
        if (names.Count == 0) { c.Items.Add("(no hay en el modelo)"); c.Enabled = false; }
        c.SelectedIndex = Math.Max(0, c.Items.IndexOf(saved));
    }
    private TableLayoutPanel Section(TableLayoutPanel parent, string title)
    {
        var head = new Label { Text = title, AutoSize = true, Font = new Font(Font, FontStyle.Bold), Margin = new Padding(0, 8, 0, 2) };
        parent.Controls.Add(head);
        var body = new TableLayoutPanel { AutoSize = true, Dock = DockStyle.Top, ColumnCount = 1, Margin = new Padding(0) };
        body.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        parent.Controls.Add(body);
        return body;
    }
    private void Field(TableLayoutPanel parent, string title, Control control, string hint)
    {
        var row = new TableLayoutPanel { AutoSize = true, Dock = DockStyle.Top, ColumnCount = 2, Margin = new Padding(0, 2, 0, 4) };
        row.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 49));
        row.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 51));
        var label = new Label { Text = title, AutoSize = true, Dock = DockStyle.Fill, Margin = new Padding(0, 6, 4, 0) };
        row.Controls.Add(label, 0, 0);
        control.Dock = DockStyle.Top;
        row.Controls.Add(control, 1, 0);
        var caption = new Label { Text = hint, AutoSize = true, Dock = DockStyle.Top, ForeColor = SystemColors.GrayText,
            Margin = new Padding(0, 2, 0, 2) };
        row.Controls.Add(caption, 0, 1);
        row.SetColumnSpan(caption, 2);
        parent.Controls.Add(row);
        _tips.SetToolTip(row, hint); // Also provides hover help around a disabled control.
        _tips.SetToolTip(label, hint);
        _tips.SetToolTip(control, hint);
        _tips.SetToolTip(caption, hint);
        foreach (Control child in control.Controls) _tips.SetToolTip(child, hint);
    }
    private void UpdateReturn()
    {
        _returnDiameter.Enabled = _return.Checked;
        _returnSystem.Enabled = _return.Checked && !(_returnSystem.Items.Count == 1 && (string?)_returnSystem.Items[0] == "(no hay en el modelo)");
    }
    private static void Set(NumericUpDown n, double value) => n.Value = double.IsFinite(value)
        ? (decimal)Math.Clamp(value, (double)n.Minimum, (double)n.Maximum) : n.Minimum;
    private void LoadValues(PatinilloSettings s)
    {
        Set(_dwellings, s.DwellingsPerFloor); _ground.Checked = s.GroundFloorHasDwellings;
        Set(_floors, s.TestFloorsAboveGround); Set(_height, s.TestFloorHeightMm);
        Set(_slab, s.SlabThicknessMm); Set(_width, s.ShaftWidthMm); Set(_depth, s.ShaftDepthMm);
        Set(_gap, s.GapMm); Set(_below, s.OutletBelowSlabMm); Set(_length, s.OutletLengthMm); Set(_start, s.StartAboveFloorMm);
        _cold.Text = s.ColdDiameterMm.ToString("0.##", Spanish);
        _hot.Text = s.HotDiameterMm.ToString("0.##", Spanish);
        _returnDiameter.Text = s.ReturnDiameterMm.ToString("0.##", Spanish);
        _return.Checked = s.HotReturn;
        _cap.Checked = s.End == PatinilloEnd.Tapon; _open.Checked = !_cap.Checked;
        UpdateReturn();
    }
    private static string SelectedName(ComboBox c) => c.SelectedItem is string s && s != "(no hay en el modelo)" ? s : "";
    private static bool ParseDiameter(ComboBox c, out double value) =>
        (double.TryParse(c.Text.Trim(), NumberStyles.AllowDecimalPoint, Spanish, out value)
        || double.TryParse(c.Text.Trim(), NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out value))
        && double.IsFinite(value) && value > 0;
    private void TryAccept(PatinilloAction action)
    {
        var errors = new List<string>();
        if (!ParseDiameter(_cold, out double cold)) errors.Add("Indique un diámetro de agua fría válido y mayor que cero.");
        if (!ParseDiameter(_hot, out double hot)) errors.Add("Indique un diámetro de ACS válido y mayor que cero.");
        bool validReturn = ParseDiameter(_returnDiameter, out double ret);
        if (_return.Checked && !validReturn) errors.Add("Indique un diámetro de retorno válido y mayor que cero.");
        if (errors.Count == 0)
        {
            Settings.DwellingsPerFloor = (int)_dwellings.Value; Settings.GroundFloorHasDwellings = _ground.Checked;
            Settings.TestFloorsAboveGround = (int)_floors.Value; Settings.TestFloorHeightMm = (double)_height.Value;
            Settings.SlabThicknessMm = (double)_slab.Value; Settings.ColdDiameterMm = cold; Settings.HotDiameterMm = hot;
            Settings.HotReturn = _return.Checked; Settings.ReturnDiameterMm = validReturn ? ret : new PatinilloSettings().ReturnDiameterMm;
            Settings.PipeTypeName = SelectedName(_type); Settings.ColdSystemName = SelectedName(_coldSystem);
            Settings.HotSystemName = SelectedName(_hotSystem); Settings.ReturnSystemName = SelectedName(_returnSystem);
            Settings.ShaftWidthMm = (double)_width.Value; Settings.ShaftDepthMm = (double)_depth.Value; Settings.GapMm = (double)_gap.Value;
            Settings.OutletBelowSlabMm = (double)_below.Value; Settings.OutletLengthMm = (double)_length.Value;
            Settings.StartAboveFloorMm = (double)_start.Value; Settings.End = _cap.Checked ? PatinilloEnd.Tapon : PatinilloEnd.Abierto;
            errors.AddRange(PatinilloPlanner.Validate(Settings));
        }
        if (errors.Count != 0)
        {
            MessageBox.Show(this, string.Join(Environment.NewLine, errors.Select(s => "• " + s)), Text, MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }
        Action = action;
        DialogResult = DialogResult.OK;
        Close();
    }
    protected override void Dispose(bool disposing)
    {
        if (disposing) _tips.Dispose();
        base.Dispose(disposing);
    }
}
