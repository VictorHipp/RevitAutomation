using System.Text.Json;
using Autodesk.Revit.Attributes;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using WF = System.Windows.Forms;

namespace RevitAutomation.Addin.Commands;

[Transaction(TransactionMode.Manual)]
public sealed class PdfExportCommand : IExternalCommand
{
    private sealed record ViewChoice(ElementId Id, string Name, string Type, bool CanPrint, string Reason)
    {
        public override string ToString() => $"{(CanPrint ? "" : "[INCOMPATIBLE] ")}{Name} | {Type} | ID {Id.Value}";
    }

    private sealed record PrintSetChoice(string Name, IReadOnlyList<ElementId> OrderedIds)
    {
        public override string ToString() => Name;
    }

    public Result Execute(ExternalCommandData commandData, ref string message, ElementSet elements)
    {
        Document doc = commandData.Application.ActiveUIDocument?.Document!;
        if (doc is null)
        {
            Autodesk.Revit.UI.TaskDialog.Show("Exportar PDF", "Abre un documento de Revit antes de exportar.");
            return Result.Cancelled;
        }

        try
        {
            using var form = new PdfExportForm(doc);
            if (form.ShowDialog() != WF.DialogResult.OK) return Result.Cancelled;
            Export(doc, form.SelectedIds, form.OutputDirectory, form.FileName, form.OverwriteConfirmed);
            int sheetCount = form.SelectedIds.Count(id => doc.GetElement(id) is ViewSheet);
            int viewCount = form.SelectedIds.Count - sheetCount;
            Autodesk.Revit.UI.TaskDialog.Show("Exportar PDF", $"PDF combinado generado correctamente.\n\nArchivo: {form.OutputPath}\nPlanos: {sheetCount}\nVistas: {viewCount}");
            return Result.Succeeded;
        }
        catch (OperationCanceledException) { return Result.Cancelled; }
        catch (Exception ex)
        {
            Autodesk.Revit.UI.TaskDialog.Show("Error al exportar PDF", ExportPathSupport.ErrorMessage(ex));
            message = ex.Message;
            return Result.Failed;
        }
    }

    internal static void Export(Document doc, IReadOnlyList<ElementId> selectedIds, string folder, string name, bool overwriteConfirmed)
    {
        if (selectedIds.Count == 0) throw new InvalidOperationException("Selecciona al menos una vista o un plano.");
        if (!Directory.Exists(folder)) throw new DirectoryNotFoundException($"La carpeta de salida no existe: {folder}");
        if (!HasWriteAccess(folder)) throw new UnauthorizedAccessException($"No hay permisos para escribir en: {folder}");
        string safeName = ValidateFileName(name);
        string finalPath = Path.Combine(folder, safeName + ".pdf");
        if (File.Exists(finalPath) && !overwriteConfirmed) throw new IOException("El PDF ya existe y no se autorizó sobrescribirlo.");

        var ids = new List<ElementId>();
        var seen = new HashSet<long>();
        var incompatible = new List<string>();
        foreach (ElementId id in selectedIds)
        {
            Element? element = doc.GetElement(id);
            if (element is not Autodesk.Revit.DB.View view)
            {
                incompatible.Add($"Elemento | ID {id.Value} | tipo no disponible o no derivado de View");
                continue;
            }
            bool printable;
            string reason;
            try { printable = view.CanBePrinted; reason = printable ? "" : "CanBePrinted es False"; }
            catch (Exception ex) { printable = false; reason = "No se pudo consultar CanBePrinted: " + ex.Message; }
            if (!printable)
            {
                incompatible.Add($"{view.Name} | ID {id.Value} | {view.GetType().FullName} | {reason}");
                continue;
            }
            if (!seen.Add(id.Value)) incompatible.Add($"{view.Name} | ID {id.Value} | ID duplicado en la selección");
            else ids.Add(id);
        }
        if (incompatible.Count > 0) throw new InvalidOperationException("La exportación se canceló; estos elementos no son compatibles:\n\n" + string.Join("\n", incompatible));
        if (ids.Count == 0) throw new InvalidOperationException("No hay vistas imprimibles en la selección.");

        string staging = ExportPathSupport.CreateStaging("pdf");
        string stagingName = "exportacion_" + Guid.NewGuid().ToString("N");
        string stagedPdf = Path.Combine(staging, stagingName + ".pdf");
        var publication = new PdfFilePublication(staging);
        try
        {
            using var options = new PDFExportOptions { Combine = true, FileName = stagingName, StopOnError = true };
            bool ok;
            ok = doc.Export(staging, ids, options);
            if (!ok || !IsValidPdf(stagedPdf)) throw new InvalidOperationException("Revit no completó la exportación o el archivo no superó la validación PDF. No se publicó el resultado.");
            publication.Publish(stagedPdf, finalPath, overwriteConfirmed);
        }
        finally
        {
            publication.Cleanup();
        }
    }

    private static bool HasWriteAccess(string folder)=>ExportPathSupport.HasWriteAccess(folder);

    private static string ValidateFileName(string input)
    {
        string name = (input ?? "").Trim();
        if (name.EndsWith(".pdf", StringComparison.OrdinalIgnoreCase)) name = name[..^4];
        if (string.IsNullOrWhiteSpace(name) || name is "." or ".." || name.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 || name.EndsWith(' ') || name.EndsWith('.'))
            throw new ArgumentException("El nombre del PDF está vacío o contiene caracteres no válidos.");
        string stem = name.Split('.')[0].ToUpperInvariant();
        if (new[] { "CON", "PRN", "AUX", "NUL" }.Contains(stem) || System.Text.RegularExpressions.Regex.IsMatch(stem, @"^(COM|LPT)[1-9]$"))
            throw new ArgumentException("El nombre del PDF está reservado por Windows.");
        return name;
    }

    private static bool IsValidPdf(string path)
    {
        try
        {
            var info = new FileInfo(path);
            if (info.Length < 10) return false;
            using var stream = File.OpenRead(path);
            byte[] header = new byte[5];
            if (stream.Read(header, 0, header.Length) != header.Length || System.Text.Encoding.ASCII.GetString(header) != "%PDF-") return false;
            stream.Seek(Math.Max(0, stream.Length - 4096), SeekOrigin.Begin);
            using var reader = new StreamReader(stream);
            return reader.ReadToEnd().Contains("%%EOF", StringComparison.Ordinal);
        }
        catch { return false; }
    }

    private sealed class PdfExportForm : WF.Form
    {
        private readonly Document _doc;
        private readonly WF.ComboBox _sets = new() { DropDownStyle = WF.ComboBoxStyle.DropDownList };
        private readonly WF.CheckedListBox _views = new() { CheckOnClick = true, IntegralHeight = false };
        private readonly WF.TextBox _folder = new();
        private readonly WF.TextBox _fileName = new();
        private readonly WF.Label _status = new() { AutoSize = true };
        private bool _overwriteConfirmed;
        private bool _applyingPrintSet;
        private bool _restoringChecks;
        private bool _statusPending;
        public IReadOnlyList<ElementId> SelectedIds { get; private set; } = Array.Empty<ElementId>();
        public string OutputDirectory => _folder.Text.Trim();
        public string FileName => _fileName.Text.Trim();
        public string OutputPath => Path.Combine(OutputDirectory, ValidateFileName(FileName) + ".pdf");
        public bool OverwriteConfirmed => _overwriteConfirmed;

        public PdfExportForm(Document doc)
        {
            _doc = doc;
            Text = "RevitAutomation | Exportar PDF";
            AutoScaleMode = WF.AutoScaleMode.Dpi; MaximizeBox = true;
            StartPosition = WF.FormStartPosition.CenterParent;
            var work = WF.Screen.FromPoint(WF.Cursor.Position).WorkingArea;
            MinimumSize = new System.Drawing.Size(Math.Min(620, Math.Max(480, work.Width - 24)), Math.Min(470, Math.Max(360, work.Height - 24)));
            Size = new System.Drawing.Size(Math.Min(920, work.Width - 24), Math.Min(720, work.Height - 24));
            BuildUi();
            LoadData();
        }

        private void BuildUi()
        {
            var root = new WF.TableLayoutPanel { Dock = WF.DockStyle.Fill, Padding = new WF.Padding(12), ColumnCount = 1, RowCount = 9 };
            root.ColumnStyles.Add(new WF.ColumnStyle(WF.SizeType.Percent, 100));
            root.RowStyles.Add(new WF.RowStyle(WF.SizeType.Absolute, 24));
            root.RowStyles.Add(new WF.RowStyle(WF.SizeType.Absolute, 34));
            root.RowStyles.Add(new WF.RowStyle(WF.SizeType.Absolute, 38));
            root.RowStyles.Add(new WF.RowStyle(WF.SizeType.Percent, 100));
            root.RowStyles.Add(new WF.RowStyle(WF.SizeType.Absolute, 24));
            root.RowStyles.Add(new WF.RowStyle(WF.SizeType.Absolute, 34));
            root.RowStyles.Add(new WF.RowStyle(WF.SizeType.Absolute, 24));
            root.RowStyles.Add(new WF.RowStyle(WF.SizeType.Absolute, 34));
            root.RowStyles.Add(new WF.RowStyle(WF.SizeType.Absolute, 48));
            root.Controls.Add(new WF.Label { Text = "Conjunto guardado (o selección manual)", AutoSize = true }, 0, 0);
            root.Controls.Add(_sets, 0, 1); _sets.Dock = WF.DockStyle.Fill;
            var selectionTools = new WF.FlowLayoutPanel { Dock = WF.DockStyle.Fill, WrapContents = false, AutoScroll = true };
            var selectAll = new WF.Button { Text = "Seleccionar todos los imprimibles", AutoSize = true };
            var up = new WF.Button { Text = "Subir", Width = 70 };
            var down = new WF.Button { Text = "Bajar", Width = 70 };
            selectionTools.Controls.Add(selectAll); selectionTools.Controls.Add(up); selectionTools.Controls.Add(down);
            _status.AutoSize = true; _status.Padding = new WF.Padding(8, 7, 0, 0); selectionTools.Controls.Add(_status);
            root.Controls.Add(selectionTools, 0, 2);
            root.Controls.Add(_views, 0, 3); _views.Dock = WF.DockStyle.Fill;
            root.Controls.Add(new WF.Label { Text = "Carpeta de salida", AutoSize = true }, 0, 4);
            var folderRow = new WF.TableLayoutPanel { Dock = WF.DockStyle.Fill, ColumnCount = 2, RowCount = 1 };
            folderRow.ColumnStyles.Add(new WF.ColumnStyle(WF.SizeType.Percent, 100)); folderRow.ColumnStyles.Add(new WF.ColumnStyle(WF.SizeType.Absolute, 100));
            folderRow.Controls.Add(_folder, 0, 0); _folder.Dock = WF.DockStyle.Fill;
            var browse = new WF.Button { Text = "Examinar…", Dock = WF.DockStyle.Fill }; folderRow.Controls.Add(browse, 1, 0);
            root.Controls.Add(folderRow, 0, 5);
            root.Controls.Add(new WF.Label { Text = "Nombre del PDF", AutoSize = true }, 0, 6);
            root.Controls.Add(_fileName, 0, 7); _fileName.Dock = WF.DockStyle.Fill;
            var actionPanel = new WF.FlowLayoutPanel { FlowDirection = WF.FlowDirection.RightToLeft, Dock = WF.DockStyle.Fill, WrapContents = false };
            var cancel = new WF.Button { Text = "Cancelar", DialogResult = WF.DialogResult.Cancel, Width = 96, Height = 32 };
            var export = new WF.Button { Text = "Exportar PDF", Width = 120, Height = 32 };
            actionPanel.Controls.Add(cancel); actionPanel.Controls.Add(export); root.Controls.Add(actionPanel, 0, 8);
            AcceptButton = export; CancelButton = cancel; Controls.Add(root);
            selectAll.Click += (_, _) =>
            {
                SwitchSetToManual();
                CheckedListSelection.SelectAllCompatible(_views, item => item is ViewChoice view && view.CanPrint);
                UpdateStatus();
            };
            up.Click += (_, _) => { SwitchSetToManual(); MoveChecked(-1); }; down.Click += (_, _) => { SwitchSetToManual(); MoveChecked(1); };
            browse.Click += (_, _) => { using var dialog = new WF.FolderBrowserDialog { SelectedPath = _folder.Text }; if (dialog.ShowDialog(this) == WF.DialogResult.OK) _folder.Text = dialog.SelectedPath; };
            _sets.SelectedIndexChanged += (_, _) => ApplyPrintSet();
            export.Click += (_, _) => TryAccept();
            _views.ItemCheck += OnViewItemCheck;
        }

        private void LoadData()
        {
            string config = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "RevitAutomation", "configuracion_pdf.json");
            string defaultFolder = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "RevitAutomation", "PDF");
            try { Directory.CreateDirectory(defaultFolder); } catch { }
            try { if (File.Exists(config)) { using var stream = File.OpenRead(config); using var json = JsonDocument.Parse(stream); if (json.RootElement.TryGetProperty("carpeta_salida", out var value)) defaultFolder = Environment.ExpandEnvironmentVariables(value.GetString() ?? defaultFolder); } } catch { }
            _folder.Text = defaultFolder;
            _fileName.Text = Path.GetFileNameWithoutExtension(_doc.Title);
            _sets.Items.Add("-- Selección manual --");
            foreach (ViewSheetSet set in new FilteredElementCollector(_doc).OfClass(typeof(ViewSheetSet)).Cast<ViewSheetSet>().OrderBy(x => x.Name, StringComparer.CurrentCultureIgnoreCase))
            {
                var ids = new List<ElementId>(); foreach (Autodesk.Revit.DB.View view in set.OrderedViewList) ids.Add(view.Id);
                _sets.Items.Add(new PrintSetChoice(set.Name, ids));
            }
            _sets.SelectedIndex = 0;
            var views = new FilteredElementCollector(_doc).OfClass(typeof(Autodesk.Revit.DB.View)).WhereElementIsNotElementType().Cast<Autodesk.Revit.DB.View>()
                .OrderBy(v => v.Name, StringComparer.CurrentCultureIgnoreCase).ThenBy(v => v.Id.Value);
            foreach (Autodesk.Revit.DB.View view in views) _views.Items.Add(CreateChoice(view), false);
            UpdateStatus();
        }

        private static ViewChoice CreateChoice(Autodesk.Revit.DB.View view)
        {
            try { bool can = view.CanBePrinted; return new ViewChoice(view.Id, view.Name, view.GetType().Name, can, can ? "" : "CanBePrinted es False"); }
            catch (Exception ex) { return new ViewChoice(view.Id, view.Name, view.GetType().Name, false, ex.Message); }
        }

        private void ApplyPrintSet()
        {
            if(_restoringChecks)return;
            _applyingPrintSet=true;
            try
            {
                var selected = _sets.SelectedItem is PrintSetChoice set
                    ? set.OrderedIds.Select(id => id.Value).ToHashSet()
                    : new HashSet<long>();
                _views.BeginUpdate();
                try
                {
                    for (int i = 0; i < _views.Items.Count; i++)
                        _views.SetItemChecked(i, selected.Contains(((ViewChoice)_views.Items[i]!).Id.Value));
                }
                finally { _views.EndUpdate(); }
            }
            finally{_applyingPrintSet=false;}
            UpdateStatus();
        }
        private void OnViewItemCheck(object? sender,WF.ItemCheckEventArgs e)
        {
            // Change scope immediately: an export must not use the saved set after a manual edit.
            // ApplyPrintSet is guarded, so this does not replace the pending checkbox value.
            if (!_applyingPrintSet && !_restoringChecks) SwitchSetToManual();
            // ItemCheck runs before CheckedItems reflects the new value.
            if (!_applyingPrintSet && !_restoringChecks && !_statusPending && IsHandleCreated && !IsDisposed && !Disposing)
            {
                _statusPending = true;
                BeginInvoke((Action)(() =>
                {
                    _statusPending = false;
                    if (!IsDisposed && !Disposing) UpdateStatus();
                }));
            }
        }
        private void SwitchSetToManual()
        {
            if(_sets.SelectedItem is not PrintSetChoice)return;
            // Changing the mode must retain exactly the current checks, including removals.
            _restoringChecks=true;
            try{_sets.SelectedIndex=0;}
            finally{_restoringChecks=false;}
        }

        private void MoveChecked(int delta)
        {
            int index = _views.SelectedIndex; int target = index + delta;
            if (index < 0 || target < 0 || target >= _views.Items.Count) return;
            object item = _views.Items[index]!; bool check = _views.GetItemChecked(index);
            _views.Items.RemoveAt(index); _views.Items.Insert(target, item); _views.SetItemChecked(target, check); _views.SelectedIndex = target;
        }

        private void UpdateStatus() => _status.Text = $"{_views.CheckedItems.Count} seleccionados. Los elementos incompatibles detienen la exportación.";

        private void TryAccept()
        {
            try
            {
                string folder = Path.GetFullPath(_folder.Text.Trim());
                if (!Directory.Exists(folder)) throw new DirectoryNotFoundException("La carpeta indicada no existe. Créala desde el Explorador y vuelve a intentarlo.");
                string name = ValidateFileName(_fileName.Text);
                var ids = new List<ElementId>();
                if (_sets.SelectedItem is PrintSetChoice set)
                {
                    var checkedIds=_views.CheckedItems.Cast<ViewChoice>().Select(x=>x.Id.Value).ToHashSet();
                    ids.AddRange(set.OrderedIds.Where(id=>checkedIds.Contains(id.Value)));
                }
                else foreach (ViewChoice choice in _views.CheckedItems) ids.Add(choice.Id);
                if (ids.Count == 0) throw new InvalidOperationException("No hay vistas seleccionadas.");
                SelectedIds = ids;
                string output = Path.Combine(folder, name + ".pdf");
                _overwriteConfirmed = false;
                if (File.Exists(output))
                {
                    if (WF.MessageBox.Show(this, $"El archivo ya existe:\n{output}\n\n¿Confirmas reemplazarlo?", "Confirmar reemplazo", WF.MessageBoxButtons.YesNo, WF.MessageBoxIcon.Warning) != WF.DialogResult.Yes) return;
                    _overwriteConfirmed = true;
                }
                SaveFolder(folder);
                DialogResult = WF.DialogResult.OK;
                Close();
            }
            catch (Exception ex){ WF.MessageBox.Show(this, ExportPathSupport.ErrorMessage(ex,_folder.Text), "No se puede exportar", WF.MessageBoxButtons.OK, WF.MessageBoxIcon.Error); }
        }

        private static void SaveFolder(string folder)
        {
            string path = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "RevitAutomation", "configuracion_pdf.json");
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            string temp = path + ".tmp";
            File.WriteAllText(temp, JsonSerializer.Serialize(new { carpeta_salida = folder }, new JsonSerializerOptions { WriteIndented = true }));
            if (File.Exists(path)) File.Replace(temp, path, null); else File.Move(temp, path);
        }
    }
}