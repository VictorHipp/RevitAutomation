using System.Drawing;
using System.Windows.Forms;
using Autodesk.Revit.DB;
using CadMepRevit.Core;
using CadMepRevit.Revit;
using Form = System.Windows.Forms.Form;
using Control = System.Windows.Forms.Control;

namespace CadMepRevit.Ui;

/// <summary>
/// Paso 2 del asistente: el usuario decide qué es cada capa, el nivel, las unidades
/// y los tamaños. No modifica el modelo: solo prepara un <see cref="BuildPlan"/>.
/// </summary>
public sealed class ImportForm : Form
{
    private const string NoPlace = "(no colocar)";
    private const string NoSystem = "(no necesita)";
    private const string FromCad = "del CAD";
    private const string HeightEdited = "alto escrito por el usuario";
    private const string AutoWall = "(automático por espesor)";
    // Sección de cada capa: decide si se pide el diámetro o el ancho × alto.
    private const string ShapeRound = "Redonda · diámetro";
    private const string ShapeRect = "Rectangular · ancho × alto";
    private const string ShapeOval = "Oval · ancho × alto";
    private const string ShapeWall = "Muro · espesor del CAD";
    // Qué tipo de plano es (elección general que cambia todas las capas de tubería/conducto a la vez).
    private const string PlanAuto = "Automático (según el nombre de cada capa)";
    private const string PlanPipes = "Tuberías (se miden por DIÁMETRO)";
    private const string PlanDucts = "Conductos (se miden por ANCHO × ALTO)";

    private CadDrawing _drawing;
    private readonly RevitCatalog _catalog;
    private readonly Func<bool, CadDrawing?> _reread;

    private readonly ComboBox _units = new() { DropDownStyle = ComboBoxStyle.DropDownList, Width = 170 };
    private readonly ComboBox _level = new() { DropDownStyle = ComboBoxStyle.DropDownList, Width = 230 };
    // Valores iniciales: los de PlanDefaults (los mismos que usa «Modelar desde CAD»).
    private readonly NumericUpDown _height = Num(-20000, 50000, (decimal)PlanDefaults.HeightAboveLevelMm, 0);
    private readonly NumericUpDown _defaultDiameter = Num(1, 3000, (decimal)PlanDefaults.PipeDiameterMm, 1);
    private readonly NumericUpDown _defaultDuctWidth = Num(10, 10000, (decimal)PlanDefaults.DuctWidthMm, 0);
    private readonly NumericUpDown _defaultDuctHeight = Num(10, 10000, (decimal)PlanDefaults.DuctHeightMm, 0);
    private readonly ComboBox _planKind = new() { DropDownStyle = ComboBoxStyle.DropDownList, Width = 300 };
    private readonly NumericUpDown _tolerance = Num(0.1M, 1000, (decimal)PlanDefaults.ToleranceMm, 1);
    private readonly NumericUpDown _minLength = Num(1, 10000, (decimal)PlanDefaults.MinLengthMm, 0);
    private readonly CheckBox _fittings = new() { Text = "Crear uniones (codos, tes y cruces)", Checked = PlanDefaults.CreateFittings, AutoSize = true };
    private readonly CheckBox _autoConnect = new() { Text = "Conectar automáticamente con equipos y terminales", Checked = PlanDefaults.AutoConnect, AutoSize = true };
    private readonly CheckBox _useZ = new() { Text = "Usar la Z del CAD (solo dibujos 3D)", Checked = PlanDefaults.UseCadZ, AutoSize = true };
    private readonly CheckBox _translate = new() { Text = "Acercar el dibujo al origen del proyecto", AutoSize = true };
    private readonly CheckBox _insideBlocks = new() { Text = "Leer también las líneas dentro de bloques", Checked = PlanDefaults.ReadLinesInsideBlocks, AutoSize = true };
    private readonly DataGridView _layers = NewGrid();
    private readonly DataGridView _blocks = NewGrid();
    private readonly Label _status = new() { Dock = DockStyle.Fill, ForeColor = System.Drawing.Color.DarkRed, AutoEllipsis = true };
    private readonly Label _fileInfo = new() { AutoSize = true, MaximumSize = new Size(1100, 0) };
    private readonly ToolTip _tips = new() { AutoPopDelay = 20000 };

    private bool _layerEventsHooked, _blockEventsHooked;

    public BuildPlan? Result { get; private set; }

    public ImportForm(CadDrawing drawing, RevitCatalog catalog, Func<bool, CadDrawing?> reread)
    {
        _drawing = drawing;
        _catalog = catalog;
        _reread = reread;

        Text = "CAD-REV · Paso 2 de 3: indique qué es cada capa";
        Width = 1250; Height = 820;
        MinimumSize = new Size(1000, 650);
        StartPosition = FormStartPosition.CenterParent;
        Font = new System.Drawing.Font("Segoe UI", 9F);

        BuildLayout();
        FillSettings();
        FillLayerGrid();
        FillBlockGrid();
        UpdateFileInfo();
    }

    // ---------------------------------------------------------------- Diseño

    private void BuildLayout()
    {
        var root = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 6, Padding = new Padding(10) };
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        root.RowStyles.Add(new RowStyle(SizeType.Percent, 65));
        root.RowStyles.Add(new RowStyle(SizeType.Percent, 35));
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 28));
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        Controls.Add(root);

        var help = new Label
        {
            AutoSize = true,
            MaximumSize = new Size(1180, 0),
            Text = "1) Compruebe las UNIDADES del dibujo y elija el NIVEL donde se construirá.\n" +
                   "2) En 'Este plano es de' elija si dibuja TUBERÍAS (se miden por diámetro) o CONDUCTOS (se miden por ancho × alto).\n" +
                   "   En la tabla puede cambiarlo capa a capa: 'Construir como', 'Sección' (redonda = diámetro; rectangular = ancho × alto), tipo y medidas.\n" +
                   "   Si los conductos están dibujados con su contorno (dos líneas), marque 'Doble línea': el ancho se mide en el CAD.\n" +
                   "3) Pulse 'Previsualizar…' para ver el resultado. Nada se crea en Revit hasta que lo confirme en la previsualización.\n" +
                   "Pase el ratón por encima de cada opción para ver una ayuda."
        };
        var top = new FlowLayoutPanel { AutoSize = true, FlowDirection = FlowDirection.TopDown, Dock = DockStyle.Fill, WrapContents = false };
        top.Controls.Add(help);
        top.Controls.Add(_fileInfo);
        root.Controls.Add(top, 0, 0);

        var settings = new TableLayoutPanel { AutoSize = true, ColumnCount = 6, Dock = DockStyle.Fill };
        void Pair(string label, Control c, string tip, int col, int row)
        {
            var l = new Label { Text = label, AutoSize = true, Anchor = AnchorStyles.Left, Margin = new Padding(3, 7, 3, 3) };
            settings.Controls.Add(l, col, row);
            settings.Controls.Add(c, col + 1, row);
            _tips.SetToolTip(l, tip); _tips.SetToolTip(c, tip);
        }
        Pair("Unidades del dibujo:", _units, "Unidad en la que está dibujado el CAD. Se propone la que indica el archivo; si las longitudes de la tabla no son razonables, cámbiela.", 0, 0);
        Pair("Nivel:", _level, "Nivel de Revit al que se asocian los elementos creados.", 2, 0);
        Pair("Altura sobre el nivel (mm):", _height, "Altura del eje de tuberías y conductos medida desde el nivel elegido.", 4, 0);
        var applyDiameter = new Button { Text = "Aplicar a todas las tuberías", AutoSize = true };
        applyDiameter.Click += (_, _) => ApplyDefaultDiameter();
        var diaPanel = new FlowLayoutPanel { AutoSize = true, WrapContents = false, Margin = Padding.Empty };
        diaPanel.Controls.Add(_defaultDiameter);
        diaPanel.Controls.Add(applyDiameter);
        Pair("Diámetro por defecto (mm):", diaPanel, "Diámetro que se propone a las capas de tubería y tubo eléctrico. Puede cambiarlo fila a fila en la tabla.", 0, 1);
        Pair("Tolerancia de unión (mm):", _tolerance, "Dos extremos más cercanos que esta distancia se consideran unidos (se crea codo, te o cruz). Súbala si el CAD está dibujado sin precisión.", 2, 1);
        Pair("Longitud mínima (mm):", _minLength, "Los tramos más cortos se descartan (Revit no admite tuberías demasiado cortas).", 4, 1);

        // Fila 3: qué se está dibujando y medida por defecto de los conductos rectangulares
        _planKind.Items.AddRange(new object[] { PlanAuto, PlanPipes, PlanDucts });
        _planKind.SelectedIndex = 0;
        _planKind.SelectedIndexChanged += (_, _) => ApplyPlanKind();
        Pair("Este plano es de:", _planKind,
            "Elija TUBERÍAS si las líneas son tuberías (se piden por diámetro) o CONDUCTOS si son conductos de aire " +
            "(se piden por ancho × alto). Cambia a la vez todas las capas que el programa propone como tubería o conducto. " +
            "'Automático' vuelve a la propuesta por el nombre de cada capa. Después puede ajustar cada capa en la tabla.", 0, 2);
        var applyDuct = new Button { Text = "Aplicar a todos los conductos", AutoSize = true };
        applyDuct.Click += (_, _) => ApplyDefaultDuctSize();
        var ductPanel = new FlowLayoutPanel { AutoSize = true, WrapContents = false, Margin = Padding.Empty };
        ductPanel.Controls.Add(_defaultDuctWidth);
        ductPanel.Controls.Add(new Label { Text = "×", AutoSize = true, Margin = new Padding(3, 7, 3, 3) });
        ductPanel.Controls.Add(_defaultDuctHeight);
        ductPanel.Controls.Add(applyDuct);
        Pair("Conducto por defecto, ancho × alto (mm):", ductPanel,
            "Ancho y alto que se proponen a los conductos rectangulares (y ovales). 'Aplicar a todos los conductos' los escribe " +
            "en todas las capas de conducto rectangular; con 'Doble línea' el ancho se sigue midiendo en el CAD y solo se aplica el alto.", 2, 2);
        settings.SetColumnSpan(ductPanel, 3);

        var checks = new FlowLayoutPanel { AutoSize = true, WrapContents = true, Dock = DockStyle.Fill };
        checks.Controls.AddRange(new Control[] { _fittings, _autoConnect, _useZ, _translate, _insideBlocks });
        _tips.SetToolTip(_autoConnect, "Al terminar, une los extremos libres de tuberías y conductos con los conectores de las familias colocadas " +
                                       "(equipos, aparatos, rejillas) que estén a menos de 500 mm, y las rejillas apoyadas en un conducto. " +
                                       "Los marcadores 3D no tienen conectores y no se conectan.");
        settings.Controls.Add(checks, 0, 3);
        settings.SetColumnSpan(checks, 6);
        _tips.SetToolTip(_fittings, "Crea las piezas de unión con las familias definidas en las preferencias de enrutamiento de cada tipo.");
        _tips.SetToolTip(_useZ, "Desmarcado: todo se construye a la 'Altura sobre el nivel'. Marcado: se suma la Z de cada línea del CAD.");
        _tips.SetToolTip(_translate, "Si el dibujo está lejos del origen (coordenadas UTM, etc.) lo desplaza para que la esquina inferior izquierda quede en (0,0). El desplazamiento usado se indica en el informe final.");
        _tips.SetToolTip(_insideBlocks, "Actívelo si las tuberías están dibujadas dentro de bloques. Se vuelve a leer el archivo.");
        root.Controls.Add(settings, 0, 1);

        var layerBox = new GroupBox { Text = "Capas con líneas → elementos MEP", Dock = DockStyle.Fill };
        layerBox.Controls.Add(_layers);
        root.Controls.Add(layerBox, 0, 2);
        var blockBox = new GroupBox { Text = "Bloques (equipos, rejillas, aparatos…) → familia de Revit o marcador 3D con su huella real", Dock = DockStyle.Fill };
        blockBox.Controls.Add(_blocks);
        root.Controls.Add(blockBox, 0, 3);
        root.Controls.Add(_status, 0, 4);

        var buttons = new FlowLayoutPanel { FlowDirection = FlowDirection.RightToLeft, AutoSize = true, Dock = DockStyle.Fill };
        var preview = new Button { Text = "Previsualizar…", AutoSize = true, Font = new System.Drawing.Font(Font, FontStyle.Bold) };
        var cancel = new Button { Text = "Cancelar", AutoSize = true, DialogResult = DialogResult.Cancel };
        var help2 = new Button { Text = "Ayuda", AutoSize = true };
        preview.Click += (_, _) => OnPreview();
        help2.Click += (_, _) => HelpText.ShowDialog(this);
        buttons.Controls.AddRange(new Control[] { preview, cancel, help2 });
        root.Controls.Add(buttons, 0, 5);
        AcceptButton = preview;
        CancelButton = cancel;

        _units.SelectedIndexChanged += (_, _) => { RefreshLengths(); RefreshUnitDependentCells(); UpdateFileInfo(); };
        _insideBlocks.CheckedChanged += (_, _) => Reread();
    }

    private static NumericUpDown Num(decimal min, decimal max, decimal value, int decimals) => new()
    {
        Minimum = min, Maximum = max, Value = value, DecimalPlaces = decimals, Width = 90, ThousandsSeparator = false
    };

    private static DataGridView NewGrid() => new()
    {
        Dock = DockStyle.Fill,
        AllowUserToAddRows = false,
        AllowUserToDeleteRows = false,
        RowHeadersVisible = false,
        AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.Fill,
        SelectionMode = DataGridViewSelectionMode.CellSelect,
        EditMode = DataGridViewEditMode.EditOnEnter,
        BackgroundColor = SystemColors.Window
    };

    // ---------------------------------------------------------------- Datos

    private void FillSettings()
    {
        foreach (DrawingUnit u in Enum.GetValues(typeof(DrawingUnit)))
            _units.Items.Add(new UnitItem(u));
        var unit = _drawing.HeaderUnit ?? DrawingUnit.Milimetros;
        _units.SelectedIndex = (int)unit;

        foreach (var l in _catalog.Levels) _level.Items.Add(new NamedId(l.Name, l.Id));
        if (_level.Items.Count > 0) _level.SelectedIndex = 0;
    }

    private sealed record UnitItem(DrawingUnit Unit)
    {
        public override string ToString() => DrawingUnits.Label(Unit);
    }

    private DrawingUnit Unit => (_units.SelectedItem as UnitItem)?.Unit ?? DrawingUnit.Milimetros;

    private void UpdateFileInfo()
    {
        string unitText = _drawing.HeaderUnit is { } u
            ? "El archivo indica unidades: " + DrawingUnits.Label(u) + "."
            : "El archivo no indica unidades (" + _drawing.HeaderUnitText + "): se proponen milímetros, compruébelo.";
        _fileInfo.Text = $"Archivo: {Path.GetFileName(_drawing.Path)} · {_drawing.Segments.Count} tramos rectos en " +
                         $"{_drawing.SegmentLayers.Count()} capas · {_drawing.Blocks.Count} bloques. {unitText}";
        _fileInfo.ForeColor = _drawing.HeaderUnit is null ? System.Drawing.Color.DarkOrange : SystemColors.ControlText;
        if (UnitsLookWrong() is string unitWarn)
        {
            _fileInfo.Text += "  ⚠ " + unitWarn;
            _fileInfo.ForeColor = System.Drawing.Color.DarkRed;
        }
        UpdateTranslateSuggestion();
    }

    /// <summary>
    /// Avisa si, con las unidades elegidas, el dibujo tiene un tamaño absurdo (p. ej. un edificio de 5 metros
    /// en milímetros: suele estar dibujado en metros). Solo avisa; el usuario decide.
    /// </summary>
    private string? UnitsLookWrong() => LayerPlanning.UnitsLookWrong(_drawing, Unit);

    private void UpdateTranslateSuggestion()
    {
        double k = DrawingUnits.ToMillimeters(Unit);
        double max = _drawing.Segments.Count == 0 ? 0 :
            _drawing.Segments.Max(s => Math.Max(Math.Max(Math.Abs(s.Start.X), Math.Abs(s.Start.Y)),
                                                Math.Max(Math.Abs(s.End.X), Math.Abs(s.End.Y)))) * k;
        if (max > PlanDefaults.TranslateBeyondMm) // más de 10 km del origen
        {
            _translate.Checked = true;
            _translate.ForeColor = System.Drawing.Color.DarkOrange;
            _translate.Text = "Acercar el dibujo al origen (RECOMENDADO: el dibujo está a más de 10 km del origen)";
        }
    }

    private const int ColLayer = 0, ColCount = 1, ColLength = 2, ColKind = 3, ColSystem = 4, ColShape = 5, ColType = 6,
        ColDiameter = 7, ColWidth = 8, ColHeight = 9, ColDouble = 10;

    private void FillLayerGrid()
    {
        _layers.Columns.Clear();
        _layers.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = "Capa CAD", ReadOnly = true, FillWeight = 150 });
        _layers.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = "Tramos", ReadOnly = true, FillWeight = 45 });
        _layers.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = "Longitud (m)", ReadOnly = true, FillWeight = 60 });
        var kind = new DataGridViewComboBoxColumn { HeaderText = "Construir como", FillWeight = 110, FlatStyle = FlatStyle.Flat };
        foreach (var k in MepKinds.All) kind.Items.Add(MepKinds.Label(k));
        _layers.Columns.Add(kind);
        _layers.Columns.Add(new DataGridViewComboBoxColumn { HeaderText = "Tipo de sistema", FillWeight = 130, FlatStyle = FlatStyle.Flat });
        _layers.Columns.Add(new DataGridViewComboBoxColumn { HeaderText = "Sección", FillWeight = 105, FlatStyle = FlatStyle.Flat });
        _layers.Columns.Add(new DataGridViewComboBoxColumn { HeaderText = "Tipo", FillWeight = 130, FlatStyle = FlatStyle.Flat });
        _layers.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = "Diámetro (mm)", FillWeight = 55 });
        _layers.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = "Ancho (mm)", FillWeight = 50 });
        _layers.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = "Alto (mm)", FillWeight = 50 });
        _layers.Columns.Add(new DataGridViewCheckBoxColumn { HeaderText = "Doble línea", FillWeight = 45 });
        _layers.Columns[ColDouble].ToolTipText =
            "Marcado: los elementos están dibujados con su CONTORNO (dos líneas paralelas, como exporta Revit). " +
            "Se crea un eje entre cada pareja de líneas, con el ancho medido en el CAD, y se cierran los huecos " +
            "de codos, tes y transiciones dibujados como bloque. Se propone solo si el dibujo lo parece.";
        _layers.Columns[ColKind].ToolTipText = "Qué elemento de Revit se crea con las líneas de esta capa.";
        _layers.Columns[ColSystem].ToolTipText = "Tipo de sistema (agua fría, impulsión…). Solo tuberías y conductos.";
        _layers.Columns[ColShape].ToolTipText =
            "Redonda: se pide el DIÁMETRO (tuberías, tubos eléctricos y conductos circulares). " +
            "Rectangular u oval: se pide el ANCHO × ALTO (conductos rectangulares y bandejas). " +
            "En los conductos puede elegirla; la lista de 'Tipo' muestra solo los tipos de esa sección.";
        _layers.Columns[ColDiameter].ToolTipText = "Diámetro en mm. Solo tuberías, tubos eléctricos y conductos de sección redonda.";
        _layers.Columns[ColWidth].ToolTipText = "Ancho en mm. Solo conductos rectangulares/ovales y bandejas.";
        _layers.Columns[ColHeight].ToolTipText = "Alto en mm. Solo conductos rectangulares/ovales, bandejas y muros.";

        _layers.Rows.Clear();
        foreach (var layer in _drawing.SegmentLayers)
        {
            int r = _layers.Rows.Add();
            var row = _layers.Rows[r];
            row.Cells[ColLayer].Value = layer;
            row.Cells[ColCount].Value = _drawing.Segments.Count(s => Eq(s.Layer, layer));
            var suggested = MepKinds.Suggest(layer);
            row.Cells[ColKind].Value = MepKinds.Label(suggested);
            row.Cells[ColDouble].Value = LooksDoubleLine(layer, suggested);
            RefreshRow(row, resetSize: true);
        }
        RefreshLengths();
        if (_layerEventsHooked) return;
        _layerEventsHooked = true;

        _layers.CurrentCellDirtyStateChanged += (_, _) =>
        {
            if (_layers.IsCurrentCellDirty && _layers.CurrentCell is DataGridViewComboBoxCell or DataGridViewCheckBoxCell)
                _layers.CommitEdit(DataGridViewDataErrorContexts.Commit);
        };
        _layers.CellValueChanged += (_, e) =>
        {
            if (e.RowIndex < 0) return;
            if (e.ColumnIndex == ColKind)
            {
                var row = _layers.Rows[e.RowIndex];
                row.Cells[ColDouble].Value = LooksDoubleLine(CellText(row, ColLayer), KindOf(row));
                RefreshRow(row, resetSize: true);
            }
            else if (e.ColumnIndex == ColShape)
            {
                // Otra sección: otros tipos de Revit y otras medidas (diámetro o ancho × alto).
                var row = _layers.Rows[e.RowIndex];
                FillTypes(row, KindOf(row));
                RefreshRow(row, resetSize: false);
            }
            else if (e.ColumnIndex is ColType or ColDouble) RefreshRow(_layers.Rows[e.RowIndex], resetSize: false);
            else if (e.ColumnIndex == ColHeight && _layers.CurrentCell?.RowIndex == e.RowIndex && _layers.IsCurrentCellInEditMode)
            {
                _layers.Rows[e.RowIndex].Tag = HeightEdited;
                RefreshRow(_layers.Rows[e.RowIndex], resetSize: false);
            }
        };
        _layers.DataError += (_, e) => { e.ThrowException = false; };
    }

    private void RefreshLengths()
    {
        double k = DrawingUnits.ToMillimeters(Unit) / 1000.0;
        foreach (DataGridViewRow row in _layers.Rows)
        {
            string layer = CellText(row, ColLayer);
            double len = _drawing.Segments.Where(s => Eq(s.Layer, layer)).Sum(s => s.Length) * k;
            row.Cells[ColLength].Value = len.ToString("0.0");
        }
        UpdateTranslateSuggestion();
    }

    /// <summary>
    /// Al cambiar las unidades cambian todas las medidas en mm: se vuelve a proponer 'Doble línea' en las
    /// capas de conducto y bandeja (la propuesta depende de la separación entre líneas) y se recalcula la
    /// huella mostrada de cada bloque. Sin esto, un plano cuya cabecera indica unas unidades equivocadas
    /// se quedaba con la propuesta calculada con ellas.
    /// </summary>
    private void RefreshUnitDependentCells()
    {
        foreach (DataGridViewRow row in _layers.Rows)
        {
            var kind = KindOf(row);
            if (kind is MepKind.Conducto or MepKind.Bandeja)
                row.Cells[ColDouble].Value = LooksDoubleLine(CellText(row, ColLayer), kind);
        }
        double k = DrawingUnits.ToMillimeters(Unit);
        foreach (DataGridViewRow row in _blocks.Rows)
        {
            string layer = row.Cells[BColLayer].Value as string ?? "", block = row.Tag as string ?? "";
            var f = _drawing.Blocks.Where(b => Eq(b.Layer, layer) && Eq(b.BlockName, block))
                .Select(b => b.Footprint).FirstOrDefault(x => x != null);
            row.Cells[BColSize].Value = f is null ? "—" : $"{f.SizeX * k:0}×{f.SizeY * k:0}";
        }
    }

    private OutlineDetector.Settings OutlineSettings(MepKind kind) =>
        LayerPlanning.OutlineSettings(kind, DrawingUnits.ToMillimeters(Unit), (double)_tolerance.Value);

    /// <summary>
    /// Propone 'Doble línea' si la mayor parte de la capa son parejas de líneas paralelas.
    /// Los muros siempre son a doble línea; tuberías y tubos eléctricos casi nunca (se dibujan con su eje).
    /// La regla está en <see cref="LayerPlanning.LooksDoubleLine"/> (la misma que usa «Modelar desde CAD»).
    /// </summary>
    private bool LooksDoubleLine(string layer, MepKind kind)
    {
        if (kind is not (MepKind.Muro or MepKind.Conducto or MepKind.Bandeja)) return false;
        var segs = _drawing.Segments.Where(s => Eq(s.Layer, layer)).ToList();
        return LayerPlanning.LooksDoubleLine(segs, kind, DrawingUnits.ToMillimeters(Unit), (double)_tolerance.Value);
    }

    private static bool IsDouble(DataGridViewRow row) => row.Cells[ColDouble].Value is true;

    /// <summary>Distancia (mm) del nivel elegido al siguiente nivel superior, o null si no hay.</summary>
    private double? NextLevelHeightMm()
    {
        if (_level.SelectedItem is not NamedId sel) return null;
        var cur = _catalog.Levels.FirstOrDefault(l => l.Id == sel.Id);
        var next = _catalog.Levels.Where(l => l.ElevationFt > cur.ElevationFt + 1e-6).OrderBy(l => l.ElevationFt).FirstOrDefault();
        if (next.Id is null) return null;
        double mm = (next.ElevationFt - cur.ElevationFt) * 304.8;
        return mm is > 500 and < 20000 ? mm : null;
    }

    private MepKind KindOf(DataGridViewRow row)
    {
        string label = row.Cells[ColKind].Value as string ?? "";
        return MepKinds.All.FirstOrDefault(k => MepKinds.Label(k) == label);
    }

    // ---- Sección (forma) de cada capa: decide si se mide por diámetro o por ancho × alto

    private static ConnectorProfileType ShapeOf(string? label) => label switch
    {
        ShapeRound => ConnectorProfileType.Round,
        ShapeOval => ConnectorProfileType.Oval,
        _ => ConnectorProfileType.Rectangular
    };

    private static string ShapeLabel(ConnectorProfileType shape) => shape switch
    {
        ConnectorProfileType.Round => ShapeRound,
        ConnectorProfileType.Oval => ShapeOval,
        _ => ShapeRect
    };

    /// <summary>Secciones que se pueden elegir para cada tipo de elemento.</summary>
    private List<string> ShapesFor(MepKind kind)
    {
        switch (kind)
        {
            case MepKind.Tuberia:
            case MepKind.TuboElectrico: return new() { ShapeRound };
            case MepKind.Bandeja: return new() { ShapeRect };
            case MepKind.Muro: return new() { ShapeWall };
            case MepKind.Conducto:
                var available = _catalog.DuctShapesAvailable().Select(ShapeLabel).ToList();
                // Sin tipos de conducto en el proyecto se ofrecen igualmente las dos habituales:
                // la validación explicará cómo conseguir los tipos.
                return available.Count > 0 ? available : new() { ShapeRect, ShapeRound };
            default: return new();
        }
    }

    /// <summary>Tipos de Revit que se ofrecen en la fila: en conductos, solo los de la sección elegida.</summary>
    private List<NamedId> TypesFor(DataGridViewRow row, MepKind kind) =>
        kind == MepKind.Conducto
            ? _catalog.DuctTypesOf(ShapeOf(row.Cells[ColShape].Value as string))
            : _catalog.Types.GetValueOrDefault(kind, new List<NamedId>());

    /// <summary>Rellena la lista de tipos de la fila y propone uno (el primero con todas sus uniones definidas).</summary>
    private void FillTypes(DataGridViewRow row, MepKind kind)
    {
        var typeCell = (DataGridViewComboBoxCell)row.Cells[ColType];
        typeCell.Items.Clear();
        if (kind == MepKind.NoImportar) { typeCell.Value = null; return; }
        var types = TypesFor(row, kind);
        foreach (var t in types) typeCell.Items.Add(t.Name);
        if (kind == MepKind.Muro)
        {
            typeCell.Items.Insert(0, AutoWall);
            typeCell.Value = AutoWall;
            return;
        }
        typeCell.Value = (types.FirstOrDefault(t => _catalog.FittingWarning(t.Name, t.Id) is null) ?? types.FirstOrDefault())?.Name;
    }

    /// <summary>Medidas propuestas para cada tipo de elemento.</summary>
    private double DefaultDiameterMm(MepKind kind) =>
        kind is MepKind.Tuberia or MepKind.TuboElectrico ? (double)_defaultDiameter.Value : MepKinds.DefaultSizeMm(kind);

    private double DefaultWidthMm(MepKind kind) =>
        kind == MepKind.Conducto ? (double)_defaultDuctWidth.Value : MepKinds.DefaultSizeMm(kind);

    private double DefaultHeightMm(MepKind kind) =>
        kind == MepKind.Conducto ? (double)_defaultDuctHeight.Value : MepKinds.DefaultHeightMm(kind);

    /// <summary>Activa una celda de medida (y le pone un valor si está vacía) o la desactiva y la vacía.</summary>
    private static void SizeCell(DataGridViewRow row, int col, bool used, bool fromCad, double defaultMm)
    {
        var cell = row.Cells[col];
        if (!used)
        {
            cell.Value = "";
            cell.ReadOnly = true;
            cell.Style.BackColor = SystemColors.Control;
            return;
        }
        if (fromCad)
        {
            // Con doble línea el ancho (o el diámetro) de cada tramo se mide en el CAD.
            cell.Value = FromCad;
            cell.ReadOnly = true;
            cell.Style.BackColor = SystemColors.Control;
            return;
        }
        string text = cell.Value as string ?? cell.Value?.ToString() ?? "";
        if (text.Length == 0 || text == FromCad) cell.Value = defaultMm > 0 ? defaultMm.ToString("0.#") : "";
        cell.ReadOnly = false;
        cell.Style.BackColor = SystemColors.Window;
    }

    /// <summary>Rellena las listas de sistema, sección y tipo, y activa diámetro o ancho × alto según lo que se va a construir.</summary>
    private void RefreshRow(DataGridViewRow row, bool resetSize)
    {
        var kind = KindOf(row);
        string layer = CellText(row, ColLayer);
        var sysCell = (DataGridViewComboBoxCell)row.Cells[ColSystem];
        var shapeCell = (DataGridViewComboBoxCell)row.Cells[ColShape];
        var typeCell = (DataGridViewComboBoxCell)row.Cells[ColType];

        if (resetSize)
        {
            row.Tag = null;
            sysCell.Items.Clear();
            shapeCell.Items.Clear();
            foreach (int c in new[] { ColDiameter, ColWidth, ColHeight }) row.Cells[c].Value = "";
            if (kind == MepKind.NoImportar)
            {
                sysCell.Value = null; shapeCell.Value = null;
                FillTypes(row, kind);
            }
            else
            {
                var systems = _catalog.Systems[kind];
                if (RevitCatalog.NeedsSystem(kind))
                {
                    foreach (var s in systems) sysCell.Items.Add(s.Name);
                    sysCell.Value = SuggestSystem(layer, systems.Select(s => s.Name).ToList());
                }
                else
                {
                    sysCell.Items.Add(NoSystem);
                    sysCell.Value = NoSystem;
                }
                // Los conductos se proponen rectangulares (ancho × alto) si el proyecto tiene tipos rectangulares.
                var shapes = ShapesFor(kind);
                foreach (var s in shapes) shapeCell.Items.Add(s);
                shapeCell.Value = shapes.FirstOrDefault();
                FillTypes(row, kind);
                // Muros: altura hasta el nivel superior si lo hay (dato del modelo, no supuesto).
                if (kind == MepKind.Muro && NextLevelHeightMm() is double h)
                {
                    row.Cells[ColHeight].Value = h.ToString("0");
                    row.Tag = HeightEdited;
                }
            }
        }
        if (kind == MepKind.Muro)
        {
            row.Cells[ColDouble].Value = true;
            row.Cells[ColDouble].ReadOnly = true;
        }
        else row.Cells[ColDouble].ReadOnly = false;

        bool off = kind == MepKind.NoImportar;
        bool wh = !off && UsesWidthHeight(row);
        bool dbl = IsDouble(row);
        foreach (int c in new[] { ColSystem, ColType })
        {
            row.Cells[c].ReadOnly = off;
            row.Cells[c].Style.BackColor = off ? SystemColors.Control : SystemColors.Window;
        }
        // La sección solo se elige en conductos; en el resto es fija (tubería = redonda, bandeja = rectangular).
        bool shapeEditable = !off && kind == MepKind.Conducto && shapeCell.Items.Count > 1;
        shapeCell.ReadOnly = !shapeEditable;
        shapeCell.Style.BackColor = shapeEditable ? SystemColors.Window : SystemColors.Control;

        // Diámetro: tuberías, tubos eléctricos y conductos redondos. Ancho × alto: rectangulares, ovales y bandejas.
        SizeCell(row, ColDiameter, used: !off && !wh && kind != MepKind.Muro, fromCad: dbl, DefaultDiameterMm(kind));
        SizeCell(row, ColWidth, used: !off && wh, fromCad: dbl, DefaultWidthMm(kind));
        SizeCell(row, ColHeight, used: !off && wh, fromCad: false, DefaultHeightMm(kind));

        // El plano en planta solo da el ancho: el alto es un valor supuesto hasta que el usuario lo escriba.
        bool heightAssumed = !off && wh && !Equals(row.Tag, HeightEdited);
        if (heightAssumed) row.Cells[ColHeight].Style.BackColor = System.Drawing.Color.FromArgb(255, 245, 190);
        row.Cells[ColHeight].ToolTipText = heightAssumed
            ? "Valor SUPUESTO: un plano en planta no indica el alto del conducto o bandeja. Escriba el alto real."
            : "Solo para conductos rectangulares/ovales, bandejas y muros.";
        row.DefaultCellStyle.ForeColor = off ? SystemColors.GrayText : SystemColors.ControlText;

        // Aviso si al tipo elegido le faltan familias de unión (las uniones fallarían).
        string? typeName = typeCell.Value as string;
        string? warn = off ? null : _catalog.FittingWarning(typeName ?? "", FindId(TypesFor(row, kind), typeName));
        typeCell.ToolTipText = warn ?? "";
        if (warn != null) typeCell.Style.BackColor = System.Drawing.Color.FromArgb(255, 235, 180);
    }

    private bool UsesWidthHeight(DataGridViewRow row)
    {
        var kind = KindOf(row);
        if (kind == MepKind.Conducto)
        {
            // Si ya hay un tipo elegido manda su forma; si no (proyecto sin tipos), la sección elegida.
            var typeId = FindId(TypesFor(row, kind), row.Cells[ColType].Value as string);
            return typeId != null
                ? _catalog.UsesWidthHeight(kind, typeId)
                : ShapeOf(row.Cells[ColShape].Value as string) != ConnectorProfileType.Round;
        }
        return _catalog.UsesWidthHeight(kind, FindId(TypesFor(row, kind), row.Cells[ColType].Value as string));
    }

    /// <summary>
    /// Elección general "Este plano es de": cambia a la vez todas las capas que el programa propone (o que ya
    /// están) como tubería o conducto. Las demás capas (bandejas, muros, textos…) no se tocan.
    /// </summary>
    private void ApplyPlanKind()
    {
        string choice = _planKind.SelectedItem as string ?? PlanAuto;
        foreach (DataGridViewRow row in _layers.Rows)
        {
            var suggested = MepKinds.Suggest(CellText(row, ColLayer));
            var current = KindOf(row);
            bool pipeOrDuct = suggested is MepKind.Tuberia or MepKind.Conducto || current is MepKind.Tuberia or MepKind.Conducto;
            if (!pipeOrDuct) continue;
            var target = choice == PlanPipes ? MepKind.Tuberia : choice == PlanDucts ? MepKind.Conducto : suggested;
            if (target == current) continue;
            // Al cambiar el valor salta CellValueChanged, que vuelve a proponer sección, tipo y medidas.
            row.Cells[ColKind].Value = MepKinds.Label(target);
            if (!_layerEventsHooked)
            {
                row.Cells[ColDouble].Value = LooksDoubleLine(CellText(row, ColLayer), target);
                RefreshRow(row, resetSize: true);
            }
        }
    }

    /// <summary>Escribe el ancho × alto por defecto en todas las capas de conducto rectangular u oval.</summary>
    private void ApplyDefaultDuctSize()
    {
        _layers.EndEdit();
        foreach (DataGridViewRow row in _layers.Rows)
        {
            if (KindOf(row) != MepKind.Conducto || !UsesWidthHeight(row)) continue;
            if (!IsDouble(row)) row.Cells[ColWidth].Value = _defaultDuctWidth.Value.ToString("0");
            row.Cells[ColHeight].Value = _defaultDuctHeight.Value.ToString("0");
            row.Tag = HeightEdited;          // el usuario ha elegido el alto: ya no es un valor supuesto
            RefreshRow(row, resetSize: false);
        }
    }

    /// <summary>Propone un tipo de sistema cuyo nombre se parezca a la capa (AF → agua fría…).</summary>
    internal static string? SuggestSystem(string layer, List<string> names)
    {
        if (names.Count == 0) return null;
        string l = layer.ToUpperInvariant();
        (string[] layerKeys, string[] systemKeys)[] rules =
        {
            (new[] { "RACS", "RECIRC", "RETORNO_CALIENTE", "RETORNO CALIENTE" }, new[] { "RETORNO", "RECIRC", "RETURN" }),
            (new[] { "ACS", "CALIENTE", "HOT" }, new[] { "CALIENTE", "HOT" }),
            (new[] { "AF", "FRIA", "FRÍA", "COLD" }, new[] { "FRÍA", "FRIA", "COLD" }),
            (new[] { "SANEAM", "FECAL", "RESIDUAL", "SANIT" }, new[] { "SANIT", "RESIDUAL" }),
            (new[] { "PLUVIAL", "STORM" }, new[] { "PLUVIAL", "STORM", "SANIT" }),
            (new[] { "VENTILACI" }, new[] { "VENTIL", "VENT" }),
            (new[] { "PCI", "BIE", "INCEND", "FIRE", "ROCIA", "SPRINK" }, new[] { "INCEND", "FIRE", "SPRINK", "ROCIA" }),
            (new[] { "IMPUL", "SUPPLY", "IMP" }, new[] { "IMPUL", "SUPPLY", "SUMINISTRO" }),
            (new[] { "RETORNO", "RETURN", "RET" }, new[] { "RETORNO", "RETURN" }),
            (new[] { "EXTRAC", "EXHAUST" }, new[] { "EXTRAC", "EXHAUST", "ESCAPE" }),
            (new[] { "CALEF", "IDA", "HYDRONIC" }, new[] { "IDA", "SUPPLY", "SUMINISTRO HIDR", "HYDRONIC SUPPLY" }),
        };
        foreach (var (lk, sk) in rules)
        {
            if (!lk.Any(k => l.Contains(k))) continue;
            var hit = names.FirstOrDefault(n => sk.Any(k => n.ToUpperInvariant().Contains(k)));
            if (hit != null) return hit;
        }
        return names[0];
    }

    private void ApplyDefaultDiameter()
    {
        foreach (DataGridViewRow row in _layers.Rows)
            if (KindOf(row) is MepKind.Tuberia or MepKind.TuboElectrico && !IsDouble(row))
                row.Cells[ColDiameter].Value = _defaultDiameter.Value.ToString("0.#");
    }

    private const int BColLayer = 0, BColBlock = 1, BColRole = 2, BColCount = 3, BColSize = 4, BColAction = 5,
        BColCategory = 6, BColHeight = 7, BColElevation = 8;
    private const string FittingByRevit = "(pieza de unión: la crea Revit)";
    private const string Marker = "Marcador 3D (sin familia)";

    internal static readonly (string Label, BuiltInCategory Cat)[] MarkerCategories =
    {
        ("Equipo mecánico", BuiltInCategory.OST_MechanicalEquipment),
        ("Terminal de aire", BuiltInCategory.OST_DuctTerminal),
        ("Accesorio de conducto", BuiltInCategory.OST_DuctAccessory),
        ("Accesorio de tubería", BuiltInCategory.OST_PipeAccessory),
        ("Aparato eléctrico", BuiltInCategory.OST_ElectricalFixtures),
        ("Luminaria", BuiltInCategory.OST_LightingFixtures),
        ("Dispositivo de iluminación", BuiltInCategory.OST_LightingDevices),
        ("Equipo eléctrico", BuiltInCategory.OST_ElectricalEquipment),
        ("Protección contra incendios", BuiltInCategory.OST_FireProtection),
        ("Dispositivo de alarma de incendios", BuiltInCategory.OST_FireAlarmDevices),
        ("Aparato sanitario", BuiltInCategory.OST_PlumbingFixtures),
        ("Modelo genérico", BuiltInCategory.OST_GenericModel),
    };

    internal static string CategoryFor(BlockRole role) => CategoryFor(role, "");

    /// <summary>Categoría del marcador 3D según la función del bloque (y su nombre, para distinguir cuadros, detectores…).</summary>
    internal static string CategoryFor(BlockRole role, string blockName)
    {
        string u = BlockNames.Clean(blockName ?? "").ToUpperInvariant();
        bool Any(params string[] k) => k.Any(u.Contains);
        return role switch
        {
            BlockRole.Equipo => "Equipo mecánico",
            BlockRole.Terminal => "Terminal de aire",
            BlockRole.Electrico => Any("CUADRO", "CGP", "EMBARRADO", "BORNERO", "_EQE_", "PANEL") ? "Equipo eléctrico" : "Aparato eléctrico",
            BlockRole.Sanitario => "Aparato sanitario",
            BlockRole.Accesorio => BlockNames.IsDuctAccessory(blockName ?? "") ? "Accesorio de conducto" : "Accesorio de tubería",
            BlockRole.Luminaria => "Luminaria",
            BlockRole.MandoIluminacion => "Dispositivo de iluminación",
            BlockRole.Pci => Any("DETECTOR", "_DET_", "PULSADOR", "SIRENA", "AVISADOR", "CENTRAL", "_DAI_", "TERMOVELOCIM")
                ? "Dispositivo de alarma de incendios" : "Protección contra incendios",
            _ => "Modelo genérico"
        };
    }

    /// <summary>Alto del marcador: el escrito en el nombre (600x100 → 100) o un valor supuesto que se marca para revisar.</summary>
    internal static (double Mm, bool Assumed) DefaultMarkerHeight(string blockName, BlockRole role)
    {
        if (BlockNames.SizeInName(blockName) is { } s) return (s.H, false);
        string u = BlockNames.Clean(blockName).ToUpperInvariant();
        return (role switch
        {
            BlockRole.Equipo => 300,
            BlockRole.Conector => 50,
            BlockRole.Pci when u.Contains("EXTINTOR") => 550,
            BlockRole.Pci when u.Contains("BIE") => 800,
            BlockRole.Electrico when u.Contains("CUADRO") || u.Contains("CGP") => 500,
            _ => 100
        }, true);
    }

    private void FillBlockGrid()
    {
        _blocks.Columns.Clear();
        _blocks.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = "Capa CAD", ReadOnly = true, FillWeight = 70 });
        _blocks.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = "Bloque", ReadOnly = true, FillWeight = 150 });
        _blocks.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = "Función", ReadOnly = true, FillWeight = 55 });
        _blocks.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = "Cant.", ReadOnly = true, FillWeight = 30 });
        _blocks.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = "Huella CAD (mm)", ReadOnly = true, FillWeight = 60 });
        var action = new DataGridViewComboBoxColumn { HeaderText = "Qué crear en Revit", FillWeight = 200, FlatStyle = FlatStyle.Flat };
        action.Items.AddRange(NoPlace, FittingByRevit, Marker);
        foreach (var s in _catalog.FamilySymbols) action.Items.Add(s.Name);
        action.ToolTipText = "Familia cargada en el proyecto (se propone sola si su nombre coincide con el del bloque), " +
                             "un marcador 3D con la huella del CAD si no tiene la familia, o nada.";
        _blocks.Columns.Add(action);
        var cat = new DataGridViewComboBoxColumn { HeaderText = "Categoría marcador", FillWeight = 85, FlatStyle = FlatStyle.Flat };
        foreach (var c in MarkerCategories) cat.Items.Add(c.Label);
        _blocks.Columns.Add(cat);
        _blocks.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = "Alto marcador (mm)", FillWeight = 50 });
        _blocks.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = "Altura s/ nivel (mm)", FillWeight = 55 });
        _blocks.Columns[BColSize].ToolTipText = "Tamaño en planta medido en el CAD (rectángulo que envuelve el bloque).";
        _blocks.Columns[BColHeight].ToolTipText = "Alto del marcador 3D. El plano en planta no lo indica: se toma del nombre (600x100 → 100) o es un valor SUPUESTO (en amarillo) que debe revisar.";
        _blocks.Columns[BColElevation].ToolTipText = "Altura sobre el nivel: centro del marcador o punto de inserción de la familia. Se propone la altura de los conductos.";

        _blocks.Rows.Clear();
        double k = DrawingUnits.ToMillimeters(Unit);
        foreach (var (layer, block) in _drawing.BlockGroups)
        {
            var refs = _drawing.Blocks.Where(b => Eq(b.Layer, layer) && Eq(b.BlockName, block)).ToList();
            var role = BlockNames.Role(block, layer);
            int r = _blocks.Rows.Add();
            var row = _blocks.Rows[r];
            row.Tag = block;
            row.Cells[BColLayer].Value = layer;
            row.Cells[BColBlock].Value = BlockNames.Clean(block);
            row.Cells[BColBlock].ToolTipText = block;
            row.Cells[BColRole].Value = BlockNames.Label(role);
            row.Cells[BColCount].Value = refs.Count;
            var f = refs.Select(b => b.Footprint).FirstOrDefault(x => x != null);
            row.Cells[BColSize].Value = f is null ? "—" : $"{f.SizeX * k:0}×{f.SizeY * k:0}";
            string? match = _catalog.MatchBlock(block);
            row.Cells[BColAction].Value = role == BlockRole.PiezaDeUnion ? FittingByRevit
                                        : match ?? (f is null || role == BlockRole.Otro ? NoPlace : Marker);
            row.Cells[BColCategory].Value = CategoryFor(role);
            var (h, assumed) = DefaultMarkerHeight(block, role);
            row.Cells[BColHeight].Value = h.ToString("0");
            if (assumed)
            {
                row.Cells[BColHeight].Style.BackColor = System.Drawing.Color.FromArgb(255, 245, 190);
                row.Cells[BColHeight].ToolTipText = "Valor SUPUESTO: el DWG en planta no indica la altura de este elemento. Revíselo.";
            }
            row.Cells[BColElevation].Value = _height.Value.ToString("0");
            RefreshBlockRow(row);
        }
        if (_blockEventsHooked) return;
        _blockEventsHooked = true;
        _blocks.DataError += (_, e) => { e.ThrowException = false; };
        _blocks.CurrentCellDirtyStateChanged += (_, _) =>
        {
            if (_blocks.IsCurrentCellDirty && _blocks.CurrentCell is DataGridViewComboBoxCell)
                _blocks.CommitEdit(DataGridViewDataErrorContexts.Commit);
        };
        _blocks.CellValueChanged += (_, e) =>
        {
            if (e.RowIndex >= 0 && e.ColumnIndex == BColAction) RefreshBlockRow(_blocks.Rows[e.RowIndex]);
        };
        // Si cambia la altura general, se actualizan las filas que seguían con la anterior.
        decimal last = _height.Value;
        _height.ValueChanged += (_, _) =>
        {
            foreach (DataGridViewRow row in _blocks.Rows)
                if (row.Cells[BColElevation].Value as string == last.ToString("0")) row.Cells[BColElevation].Value = _height.Value.ToString("0");
            last = _height.Value;
        };
    }

    /// <summary>Activa o desactiva las columnas del marcador según lo que se vaya a crear.</summary>
    private void RefreshBlockRow(DataGridViewRow row)
    {
        string act = row.Cells[BColAction].Value as string ?? NoPlace;
        bool marker = act == Marker;
        bool place = act != NoPlace && act != FittingByRevit;
        foreach (int c in new[] { BColCategory, BColHeight })
        {
            row.Cells[c].ReadOnly = !marker;
            if (!marker) row.Cells[c].Style.ForeColor = SystemColors.GrayText;
            else row.Cells[c].Style.ForeColor = SystemColors.ControlText;
        }
        row.Cells[BColElevation].ReadOnly = !place;
        row.Cells[BColElevation].Style.ForeColor = place ? SystemColors.ControlText : SystemColors.GrayText;
        row.DefaultCellStyle.ForeColor = place ? SystemColors.ControlText : SystemColors.GrayText;
    }
    private void Reread()
    {
        var d = _reread(_insideBlocks.Checked);
        if (d is null) { _insideBlocks.Checked = !_insideBlocks.Checked; return; }
        _drawing = d;
        FillLayerGrid();
        if (_planKind.SelectedItem as string != PlanAuto) ApplyPlanKind();   // se mantiene "tuberías" o "conductos"
        FillBlockGrid();
        UpdateFileInfo();
    }

    // ---------------------------------------------------------------- Validación

    private static bool Eq(string a, string b) => string.Equals(a, b, StringComparison.OrdinalIgnoreCase);

    /// <summary>Texto de una celda ("" si está vacía).</summary>
    private static string CellText(DataGridViewRow row, int column) => row.Cells[column].Value as string ?? "";

    private static ElementId? FindId(List<NamedId>? list, string? name) =>
        list?.FirstOrDefault(n => n.Name == name)?.Id;

    private static bool TryNumber(object? value, out double v) =>
        double.TryParse((value as string ?? value?.ToString() ?? "").Replace(',', '.'),
            System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out v);

    private static bool TryMm(object? value, out double mm) =>
        double.TryParse((value as string ?? value?.ToString() ?? "").Replace(',', '.'),
            System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out mm) && mm > 0;

    /// <summary>Comprueba todo lo necesario y crea el plan. Devuelve null y señala el problema si falta algo.</summary>
    private BuildPlan? CreatePlanOrReport()
    {
        var problems = new List<(string Text, DataGridView? Grid, int Row, int Col)>();
        if (_level.SelectedItem is not NamedId level)
        {
            problems.Add(("No hay ningún nivel en el proyecto. Cree un nivel en una vista de alzado (Arquitectura > Nivel) y vuelva a intentarlo.", null, -1, -1));
            Report(problems);
            return null;
        }

        var plan = new BuildPlan
        {
            LevelId = level.Id,
            LevelName = level.Name,
            LevelElevationFt = _catalog.Levels.First(l => l.Id == level.Id).ElevationFt,
            HeightAboveLevelMm = (double)_height.Value,
            CreateFittings = _fittings.Checked,
            AutoConnect = _autoConnect.Checked,
            TranslationMm = ComputeTranslation(),
            SourcePath = _drawing.Path,
            UnitsLabel = DrawingUnits.Label(Unit),
            ToleranceMm = (double)_tolerance.Value,
            MinLengthMm = (double)_minLength.Value
        };

        foreach (DataGridViewRow row in _layers.Rows)
        {
            var kind = KindOf(row);
            if (kind == MepKind.NoImportar) continue;
            string layer = CellText(row, ColLayer);
            if (_catalog.Types[kind].Count == 0)
            {
                problems.Add(($"Capa '{layer}': {RevitCatalog.HowToGetTypes(kind)} O bien elija 'No importar'.", _layers, row.Index, ColKind));
                continue;
            }
            var types = TypesFor(row, kind);
            if (types.Count == 0)
            {
                problems.Add(($"Capa '{layer}': el proyecto no tiene tipos de conducto de sección '{CellText(row, ColShape)}'. Elija otra 'Sección'.", _layers, row.Index, ColShape));
                continue;
            }
            var typeId = kind == MepKind.Muro && row.Cells[ColType].Value as string == AutoWall
                ? ElementId.InvalidElementId
                : FindId(types, row.Cells[ColType].Value as string);
            if (typeId is null) problems.Add(($"Capa '{layer}': elija un 'Tipo'.", _layers, row.Index, ColType));
            ElementId? sysId = null;
            if (RevitCatalog.NeedsSystem(kind))
            {
                if (_catalog.Systems[kind].Count == 0)
                    problems.Add(($"Capa '{layer}': {RevitCatalog.HowToGetTypes(kind)}", _layers, row.Index, ColSystem));
                sysId = FindId(_catalog.Systems[kind], row.Cells[ColSystem].Value as string);
                if (sysId is null && _catalog.Systems[kind].Count > 0)
                    problems.Add(($"Capa '{layer}': elija un 'Tipo de sistema'.", _layers, row.Index, ColSystem));
            }
            bool dbl = IsDouble(row);
            bool wh = _catalog.UsesWidthHeight(kind, typeId);
            // Tuberías y conductos redondos: diámetro. Conductos rectangulares/ovales y bandejas: ancho × alto.
            // (Para el resto del programa el "tamaño" es el diámetro o el ancho, y el alto va aparte.)
            double size = 0;
            if (!dbl && kind != MepKind.Muro)
            {
                int sizeCol = wh ? ColWidth : ColDiameter;
                if (!TryMm(row.Cells[sizeCol].Value, out size))
                    problems.Add((wh ? $"Capa '{layer}': escriba el ANCHO en mm, mayor que 0 (por ejemplo 300)."
                                     : $"Capa '{layer}': escriba el DIÁMETRO en mm, mayor que 0 (por ejemplo 25).", _layers, row.Index, sizeCol));
            }
            double height = 0;
            if (wh && !TryMm(row.Cells[ColHeight].Value, out height))
                problems.Add(($"Capa '{layer}': escriba el ALTO en mm (conducto rectangular, bandeja o muro).", _layers, row.Index, ColHeight));
            if (typeId is null) continue;
            if (_catalog.UsesWidthHeight(kind, typeId) && !Equals(row.Tag, HeightEdited) && height > 0)
                plan.Warnings.Add($"Capa '{layer}': alto {height:0} mm SUPUESTO (el plano en planta no lo indica). Si no es correcto, escríbalo en la columna 'Alto'.");
            plan.Layers[layer] = new LayerPlan
            {
                Layer = layer, Kind = kind, SystemTypeId = sysId, TypeId = typeId, SizeMm = size, HeightMm = height, DoubleLine = dbl
            };
        }

        foreach (DataGridViewRow row in _blocks.Rows)
        {
            string act = row.Cells[BColAction].Value as string ?? NoPlace;
            if (act == NoPlace || act == FittingByRevit) continue;
            string blockName = row.Tag as string ?? "";
            string shown = CellText(row, BColBlock);
            if (!TryNumber(row.Cells[BColElevation].Value, out double elev))
                problems.Add(($"Bloque '{shown}': escriba la altura sobre el nivel en mm (por ejemplo 2500).", _blocks, row.Index, BColElevation));
            if (act == Marker)
            {
                if (!TryMm(row.Cells[BColHeight].Value, out double h))
                    problems.Add(($"Bloque '{shown}': escriba el alto del marcador en mm, mayor que 0.", _blocks, row.Index, BColHeight));
                var cat = MarkerCategories.FirstOrDefault(c => c.Label == row.Cells[BColCategory].Value as string);
                plan.BlockPlans.Add(new BlockPlan
                {
                    Layer = row.Cells[BColLayer].Value as string ?? "", BlockName = blockName, Action = BlockAction.Marcador,
                    MarkerCategory = cat.Label is null ? BuiltInCategory.OST_GenericModel : cat.Cat, HeightMm = h, ElevationMm = elev
                });
                continue;
            }
            var id = FindId(_catalog.FamilySymbols, act);
            if (id is null) { problems.Add(($"Bloque '{shown}': la familia elegida no está en el proyecto.", _blocks, row.Index, BColAction)); continue; }
            // Si Revit no puede colocar la familia (por ejemplo, porque necesita anfitrión) se crea un marcador
            // en su lugar: se le da el alto y la categoría propuestos en la fila, no un marcador plano genérico.
            string blockLayer = row.Cells[BColLayer].Value as string ?? "";
            TryMm(row.Cells[BColHeight].Value, out double fallbackHeight);
            var fallbackCat = MarkerCategories.FirstOrDefault(c => c.Label == row.Cells[BColCategory].Value as string);
            plan.BlockPlans.Add(new BlockPlan
            {
                Layer = blockLayer, BlockName = blockName, Action = BlockAction.Familia,
                SymbolId = id, ElevationMm = elev,
                HeightMm = fallbackHeight > 0 ? fallbackHeight : DefaultMarkerHeight(blockName, BlockNames.Role(blockName, blockLayer)).Mm,
                MarkerCategory = fallbackCat.Label is null ? BuiltInCategory.OST_GenericModel : fallbackCat.Cat
            });
        }

        if (plan.Layers.Count == 0 && plan.BlockPlans.Count == 0 && problems.Count == 0)
            problems.Add(("No ha elegido nada que construir: en la columna 'Construir como' elija Tubería (u otro) en al menos una capa.", _layers, 0, ColKind));

        if (problems.Count > 0) { Report(problems); return null; }

        // Red de tramos solo con las capas elegidas
        var settings = new NetworkBuilder.Settings
        {
            UnitToMm = DrawingUnits.ToMillimeters(Unit),
            ToleranceMm = (double)_tolerance.Value,
            MinLengthMm = (double)_minLength.Value,
            FlattenZ = !_useZ.Checked,
            OffsetMm = plan.TranslationMm,
            TransitionHints = LayerPlanning.TransitionHints(_drawing),
            ThicknessLayers = plan.Layers.Values.Where(lp => lp.Kind == MepKind.Muro).Select(lp => lp.Layer).ToList()
        };
        // Capas a doble línea: se sustituyen sus líneas por los ejes detectados (con su ancho).
        var input = new List<CadSegment>();
        foreach (var lp in plan.Layers.Values)
        {
            var layerSegs = _drawing.Segments.Where(s => Eq(s.Layer, lp.Layer)).ToList();
            if (!lp.DoubleLine) { input.AddRange(layerSegs); continue; }
            var outline = OutlineDetector.Detect(layerSegs, OutlineSettings(lp.Kind));
            plan.Outlines[lp.Layer] = outline;
            input.AddRange(outline.Centerlines);
        }
        plan.Network = NetworkBuilder.Build(input, settings);
        // Los muros no llevan piezas de unión: Revit los une solo en sus extremos.
        plan.Network.Junctions.RemoveAll(j => plan.Layers.TryGetValue(j.Layer, out var jl) && jl.Kind == MepKind.Muro);
        double k = settings.UnitToMm;
        foreach (var b in _drawing.Blocks.Where(b => plan.BlockPlans.Any(p => BuildPlan.Same(p, b))))
        {
            var pos = new P3(b.Position.X * k, b.Position.Y * k, _useZ.Checked ? b.Position.Z * k : 0) + plan.TranslationMm;
            var f = b.Footprint;
            var foot = f is null ? null
                : new Footprint(f.CenterX * k + plan.TranslationMm.X, f.CenterY * k + plan.TranslationMm.Y, f.AngleRad, f.SizeX * k, f.SizeY * k);
            plan.Blocks.Add((b, pos, foot));
        }
        if (plan.CreateFittings)
        {
            foreach (var lp in plan.Layers.Values)
            {
                int joints = plan.Network.Junctions.Count(j => Eq(j.Layer, lp.Layer) && j.Kind != JunctionKind.NoAdmitida);
                var typeName = _catalog.Types[lp.Kind].FirstOrDefault(t => t.Id == lp.TypeId)?.Name ?? "";
                var warn = _catalog.FittingWarning(typeName, lp.TypeId);
                if (joints > 0 && warn != null) plan.Warnings.Add($"Capa '{lp.Layer}' ({joints} uniones): {warn}");
            }
        }
        if (plan.Network.Segments.Count == 0 && plan.Blocks.Count == 0)
        {
            Report(new() { ("Después de limpiar el dibujo no queda ningún tramo: puede que las unidades sean incorrectas (tramos demasiado cortos) o que la longitud mínima sea muy alta.", null, -1, -1) });
            return null;
        }
        _status.Text = "";
        return plan;
    }

    private P3 ComputeTranslation()
    {
        if (!_translate.Checked || _drawing.Segments.Count == 0) return default;
        double k = DrawingUnits.ToMillimeters(Unit);
        double minX = _drawing.Segments.Min(s => Math.Min(s.Start.X, s.End.X)) * k;
        double minY = _drawing.Segments.Min(s => Math.Min(s.Start.Y, s.End.Y)) * k;
        // Redondeado a metros completos para que el desplazamiento sea fácil de reproducir.
        return new P3(-Math.Floor(minX / 1000) * 1000, -Math.Floor(minY / 1000) * 1000, 0);
    }

    private void Report(List<(string Text, DataGridView? Grid, int Row, int Col)> problems)
    {
        _status.Text = "⚠ " + problems[0].Text + (problems.Count > 1 ? $"  (y {problems.Count - 1} problemas más)" : "");
        var first = problems[0];
        if (first.Grid != null && first.Row >= 0 && first.Row < first.Grid.Rows.Count)
        {
            first.Grid.CurrentCell = first.Grid.Rows[first.Row].Cells[first.Col];
            first.Grid.Focus();
        }
        MessageBox.Show(this,
            "Antes de continuar corrija lo siguiente (la primera celda afectada queda seleccionada):\n\n• " +
            string.Join("\n• ", problems.Take(12).Select(p => p.Text)) +
            (problems.Count > 12 ? $"\n… y {problems.Count - 12} más." : ""),
            "CAD-REV · Faltan datos", MessageBoxButtons.OK, MessageBoxIcon.Warning);
    }

    private void OnPreview()
    {
        BuildPlan? plan;
        try
        {
            Cursor = Cursors.WaitCursor;
            plan = CreatePlanOrReport();
        }
        finally { Cursor = Cursors.Default; }
        if (plan is null) return;

        using var preview = new PreviewForm(plan, _drawing, DrawingUnits.ToMillimeters(Unit));
        if (preview.ShowDialog(this) == DialogResult.OK)
        {
            Result = plan;
            DialogResult = DialogResult.OK;
            Close();
        }
    }
}
