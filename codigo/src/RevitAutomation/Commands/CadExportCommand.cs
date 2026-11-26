using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using Autodesk.Revit.Attributes;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using WF = System.Windows.Forms;

namespace RevitAutomation.Addin.Commands;

/// <summary>
/// Exports selected Revit views/sheets to an Autodesk CAD format.
/// Exported files are staged in a temporary folder and only published after
/// validation and, when needed, explicit overwrite confirmation.
/// </summary>
[Transaction(TransactionMode.Manual)]
public sealed class CadExportCommand : IExternalCommand
{
    private enum CadFormat { DWG, DXF, DGN }

    private sealed record FormatChoice(string Label, CadFormat Format, string Extension, ACADVersion? AcadVersion, string Key)
    {
        public override string ToString() => Label;
    }

    private sealed record ViewChoice(ElementId Id, string Name, string Type, bool CanExport, string Reason)
    {
        public override string ToString() => CanExport
            ? $"{Name} | {Type} | ID {Id.Value}"
            : $"[INCOMPATIBLE] {Name} | {Type} | ID {Id.Value} | {Reason}";
    }

    private sealed record PrintSetChoice(string Name, IReadOnlyList<ElementId> OrderedIds)
    {
        public override string ToString() => Name;
    }

    private sealed record SetupChoice(string Label, string? Name)
    {
        public override string ToString() => Label;
    }

    private sealed record ExportPreferences
    {
        [JsonPropertyName("carpeta_salida")]
        public string? OutputFolder { get; init; }

        [JsonPropertyName("formato")]
        public string? Format { get; init; }

        [JsonPropertyName("configuracion_exportacion")]
        public string? SetupName { get; init; }
    }

    private sealed record ExportSummary(
        string FormatLabel,
        string SetupLabel,
        int SelectedViewCount,
        IReadOnlyList<string> PublishedFiles,
        int PrimaryFileCount,
        int AuxiliaryFileCount,
        IReadOnlyList<string> ExcludedViews);

    // The entries mirror Revit's CAD file-type selector. Revit 2027 supports the
    // AutoCAD 2007, 2010, 2013 and 2018 file versions for DWG/DXF.
    // The 2013 DWG option is first to match the user's existing Revit workflow.
    private static readonly FormatChoice[] Formats =
    {
        new("Archivos AutoCAD 2013 DWG (*.dwg)", CadFormat.DWG, ".dwg", ACADVersion.R2013, "DWG_R2013"),
        new("Archivos AutoCAD 2018 DWG (*.dwg)", CadFormat.DWG, ".dwg", ACADVersion.R2018, "DWG_R2018"),
        new("Archivos AutoCAD 2010 DWG (*.dwg)", CadFormat.DWG, ".dwg", ACADVersion.R2010, "DWG_R2010"),
        new("Archivos AutoCAD 2007 DWG (*.dwg)", CadFormat.DWG, ".dwg", ACADVersion.R2007, "DWG_R2007"),
        new("Archivos AutoCAD 2013 DXF (*.dxf)", CadFormat.DXF, ".dxf", ACADVersion.R2013, "DXF_R2013"),
        new("Archivos AutoCAD 2018 DXF (*.dxf)", CadFormat.DXF, ".dxf", ACADVersion.R2018, "DXF_R2018"),
        new("Archivos AutoCAD 2010 DXF (*.dxf)", CadFormat.DXF, ".dxf", ACADVersion.R2010, "DXF_R2010"),
        new("Archivos AutoCAD 2007 DXF (*.dxf)", CadFormat.DXF, ".dxf", ACADVersion.R2007, "DXF_R2007"),
        new("MicroStation DGN (*.dgn)", CadFormat.DGN, ".dgn", null, "DGN")
    };

    public Result Execute(ExternalCommandData commandData, ref string message, ElementSet elements)
    {
        Document? doc = commandData.Application.ActiveUIDocument?.Document;
        if (doc is null)
        {
            Autodesk.Revit.UI.TaskDialog.Show("Exportar CAD", "Abre un proyecto de Revit antes de exportar a CAD.");
            return Result.Cancelled;
        }

        if (doc.IsFamilyDocument)
        {
            Autodesk.Revit.UI.TaskDialog.Show("Exportar CAD", "La exportación CAD de RevitAutomation requiere un documento de proyecto (.rvt), no un documento de familia (.rfa).");
            return Result.Cancelled;
        }

        try
        {
            using var form = new CadExportForm(doc);
            if (form.ShowDialog() != WF.DialogResult.OK)
                return Result.Cancelled;

            ExportSummary? summary = ExportAndPublish(
                doc,
                form.SelectedIds,
                form.OutputDirectory,
                form.FilePrefix,
                form.SelectedFormat,
                form.SelectedSetupName,
                form.SelectedSetupLabel,
                form.ExcludedViews);

            if (summary is null)
                return Result.Cancelled;

            string outputList = string.Join(Environment.NewLine, summary.PublishedFiles.Take(12));
            if (summary.PublishedFiles.Count > 12)
                outputList += $"{Environment.NewLine}… y {summary.PublishedFiles.Count - 12} archivos más.";

            string excluded = summary.ExcludedViews.Count == 0
                ? ""
                : $"{Environment.NewLine}{Environment.NewLine}Vistas excluidas del conjunto: {summary.ExcludedViews.Count}{Environment.NewLine}" +
                  string.Join(Environment.NewLine, summary.ExcludedViews.Take(8)) +
                  (summary.ExcludedViews.Count > 8 ? $"{Environment.NewLine}… y {summary.ExcludedViews.Count - 8} más." : "");

            Autodesk.Revit.UI.TaskDialog.Show("Exportar CAD - Resultado",
                $"Exportación completada.{Environment.NewLine}{Environment.NewLine}" +
                $"Formato: {summary.FormatLabel}{Environment.NewLine}" +
                $"Configuración: {summary.SetupLabel}{Environment.NewLine}" +
                $"Vistas/planos solicitados: {summary.SelectedViewCount}{Environment.NewLine}" +
                $"Archivos principales {summary.FormatLabel}: {summary.PrimaryFileCount}{Environment.NewLine}" +
                $"Archivos auxiliares: {summary.AuxiliaryFileCount}{Environment.NewLine}" +
                $"Carpeta: {form.OutputDirectory}{Environment.NewLine}{Environment.NewLine}" +
                outputList + excluded);
            return Result.Succeeded;
        }
        catch (OperationCanceledException)
        {
            return Result.Cancelled;
        }
        catch (Exception ex)
        {
            Autodesk.Revit.UI.TaskDialog.Show("Error al exportar CAD", ExportPathSupport.ErrorMessage(ex));
            message = ex.Message;
            return Result.Failed;
        }
    }

    private static ExportSummary? ExportAndPublish(
        Document doc,
        IReadOnlyList<ElementId> selectedIds,
        string outputFolder,
        string prefix,
        FormatChoice format,
        string? setupName,
        string setupLabel,
        IReadOnlyList<string> excludedViews)
    {
        if (selectedIds.Count == 0)
            throw new InvalidOperationException("Selecciona al menos una vista o un plano compatible.");

        string folder = Path.GetFullPath(outputFolder);
        if (!Directory.Exists(folder))
            throw new DirectoryNotFoundException($"La carpeta de salida no existe: {folder}");
        if (!HasWriteAccess(folder))
            throw new UnauthorizedAccessException($"No hay permisos de escritura en la carpeta: {folder}");

        string safePrefix = ValidateFilePrefix(prefix);
        var ids = new List<ElementId>();
        var seen = new HashSet<long>();
        var incompatible = new List<string>();
        foreach (ElementId id in selectedIds)
        {
            if (!seen.Add(id.Value))
            {
                incompatible.Add($"ID {id.Value}: aparece más de una vez en la selección.");
                continue;
            }

            if (doc.GetElement(id) is not Autodesk.Revit.DB.View view)
            {
                incompatible.Add($"ID {id.Value}: el elemento no existe o no es una vista.");
                continue;
            }

            try
            {
                if (!view.CanBePrinted)
                    incompatible.Add($"{view.Name} | ID {id.Value}: Revit indica que no se puede exportar.");
                else
                    ids.Add(id);
            }
            catch (Exception ex)
            {
                incompatible.Add($"{view.Name} | ID {id.Value}: no se pudo comprobar la compatibilidad ({ex.Message}).");
            }
        }

        if (incompatible.Count > 0)
            throw new InvalidOperationException("La exportación se ha detenido porque la selección contiene elementos no exportables o identificadores inválidos. No se publicó ningún archivo." +
                Environment.NewLine + Environment.NewLine + string.Join(Environment.NewLine, incompatible.Take(20)) +
                (incompatible.Count > 20 ? $"{Environment.NewLine}… y {incompatible.Count - 20} incidencias más." : ""));
        if (ids.Count == 0)
            throw new InvalidOperationException("No hay vistas o planos compatibles en la selección.");

        string staging = ExportPathSupport.CreateStaging("cad");
        bool preserveStagingForRecovery = false;
        try
        {
            bool exported = RunExport(doc, staging, safePrefix, ids, format, setupName);
            if (!exported)
                throw new InvalidOperationException("Revit ha indicado que la exportación no se ha completado. No se ha publicado ningún archivo.");

            List<string> stagedFiles = Directory.EnumerateFiles(staging, "*", SearchOption.AllDirectories)
                .Where(path => !string.Equals(Path.GetFileName(Path.GetDirectoryName(path)), "__revitautomation_backup", StringComparison.OrdinalIgnoreCase))
                .ToList();
            if (stagedFiles.Count == 0)
                throw new InvalidOperationException("Revit no ha generado archivos en la carpeta temporal. No se ha publicado ningún resultado.");

            foreach (string file in stagedFiles)
            {
                var info = new FileInfo(file);
                if (info.Length <= 0)
                    throw new InvalidOperationException($"El exportador ha generado un archivo vacío: {Path.GetFileName(file)}. No se publicó ningún resultado.");
            }

            List<string> primaryFiles = stagedFiles
                .Where(path => string.Equals(Path.GetExtension(path), format.Extension, StringComparison.OrdinalIgnoreCase))
                .ToList();
            if (primaryFiles.Count == 0)
                throw new InvalidOperationException($"La exportación terminó sin generar ningún archivo {format.Extension}. Se han descartado los archivos temporales.");

            if (format.Format == CadFormat.DWG && primaryFiles.Any(path => !HasDwgSignature(path)))
                throw new InvalidOperationException("Al menos un archivo DWG no tiene una cabecera DWG reconocible. No se publicó ningún resultado.");
            if (format.Format == CadFormat.DXF && primaryFiles.Any(path => !HasDxfSignature(path)))
                throw new InvalidOperationException("Al menos un archivo DXF no tiene una cabecera DXF reconocible. No se publicó ningún resultado.");

            var publications = stagedFiles.Select(source => new
            {
                Source = source,
                Relative = Path.GetRelativePath(staging, source)
            }).ToList();

            foreach (var item in publications)
            {
                if (Path.IsPathRooted(item.Relative) || item.Relative == ".." || item.Relative.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal))
                    throw new InvalidOperationException("Revit ha generado una ruta de salida no válida. Se canceló la publicación por seguridad.");
            }

            var destinations = publications
                .Select(item => Path.GetFullPath(Path.Combine(folder, item.Relative)))
                .ToList();
            var directoryCollisions = destinations.Where(Directory.Exists).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            if (directoryCollisions.Count > 0)
                throw new IOException("No se pueden publicar archivos CAD porque ya existe una carpeta con uno de los nombres de salida: " +
                    string.Join(", ", directoryCollisions.Select(Path.GetFileName).Take(8)));

            var collisions = destinations
                .Where(File.Exists)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();

            if (collisions.Count > 0)
            {
                string display = string.Join(Environment.NewLine, collisions.Take(12).Select(Path.GetFileName));
                if (collisions.Count > 12)
                    display += $"{Environment.NewLine}… y {collisions.Count - 12} archivos más.";

                WF.DialogResult answer = WF.MessageBox.Show(
                    $"Ya existen {collisions.Count} archivo(s) que se generarían con esta exportación:{Environment.NewLine}{Environment.NewLine}" +
                    display + Environment.NewLine + Environment.NewLine +
                    "¿Quieres sustituir esos archivos? Si respondes No, no se modificará ningún archivo existente.",
                    "Confirmar sustitución de archivos CAD",
                    WF.MessageBoxButtons.YesNo,
                    WF.MessageBoxIcon.Warning,
                    WF.MessageBoxDefaultButton.Button2);
                if (answer != WF.DialogResult.Yes)
                    return null;
            }

            string backupRoot = Path.Combine(staging, "__revitautomation_backup");
            Directory.CreateDirectory(backupRoot);
            var backups = new List<(string Original, string Backup)>();
            var published = new List<string>();
            try
            {
                foreach (string existing in collisions)
                {
                    string relative = Path.GetRelativePath(folder, existing);
                    string backup = Path.Combine(backupRoot, relative);
                    Directory.CreateDirectory(Path.GetDirectoryName(backup)!);
                    File.Move(existing, backup);
                    backups.Add((existing, backup));
                }

                foreach (var item in publications)
                {
                    string destination = Path.GetFullPath(Path.Combine(folder, item.Relative));
                    Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
                    File.Move(item.Source, destination);
                    published.Add(destination);
                }
            }
            catch (Exception publishError)
            {
                foreach (string created in published)
                {
                    try { if (File.Exists(created)) File.Delete(created); } catch { }
                }

                var restoreFailures = new List<string>();
                foreach (var backup in backups.AsEnumerable().Reverse())
                {
                    try
                    {
                        Directory.CreateDirectory(Path.GetDirectoryName(backup.Original)!);
                        if (File.Exists(backup.Backup)) File.Move(backup.Backup, backup.Original, true);
                    }
                    catch (Exception restoreError)
                    {
                        restoreFailures.Add($"{backup.Original}: {restoreError.Message}");
                    }
                }

                string restoreText = restoreFailures.Count == 0
                    ? "Se restauraron los archivos preexistentes."
                    : "No se pudo restaurar automáticamente algún archivo: " + string.Join("; ", restoreFailures);
                if (restoreFailures.Count > 0)
                {
                    preserveStagingForRecovery = true;
                    throw new IOException($"Falló la publicación de los archivos CAD. {restoreText} Los archivos de recuperación se conservaron en '{staging}'. Error original: {publishError.Message}", publishError);
                }
                throw new IOException($"Falló la publicación de los archivos CAD. {restoreText} Error original: {publishError.Message}", publishError);
            }

            var publishedPrimary = publications
                .Where(item => string.Equals(Path.GetExtension(item.Relative), format.Extension, StringComparison.OrdinalIgnoreCase))
                .Select(item => Path.Combine(folder, item.Relative))
                .ToList();

            return new ExportSummary(
                format.Label,
                setupLabel,
                ids.Count,
                published.Select(path => path).ToList(),
                publishedPrimary.Count,
                published.Count - publishedPrimary.Count,
                excludedViews);
        }
        finally
        {
            if (!preserveStagingForRecovery)
            {
                try { Directory.Delete(staging, true); } catch { }
            }
        }
    }

    private static bool RunExport(Document doc, string stagingFolder, string prefix, ICollection<ElementId> ids,
        FormatChoice format, string? setupName)
    {

        switch (format.Format)
        {
            case CadFormat.DWG:
            {
                using DWGExportOptions options = setupName is null
                    ? new DWGExportOptions()
                    : DWGExportOptions.GetPredefinedOptions(doc, setupName)
                      ?? throw new InvalidOperationException($"No se encontró la configuración DWG '{setupName}'.");
                if (format.AcadVersion.HasValue) options.FileVersion = format.AcadVersion.Value;
                return doc.Export(stagingFolder, prefix, ids, options);
            }
            case CadFormat.DXF:
            {
                using DXFExportOptions options = setupName is null
                    ? new DXFExportOptions()
                    : DXFExportOptions.GetPredefinedOptions(doc, setupName)
                      ?? throw new InvalidOperationException($"No se encontró la configuración DXF '{setupName}'.");
                if (format.AcadVersion.HasValue) options.FileVersion = format.AcadVersion.Value;
                return doc.Export(stagingFolder, prefix, ids, options);
            }
            case CadFormat.DGN:
            {
                using DGNExportOptions options = setupName is null
                    ? new DGNExportOptions()
                    : DGNExportOptions.GetPredefinedOptions(doc, setupName)
                      ?? throw new InvalidOperationException($"No se encontró la configuración DGN '{setupName}'.");
                return doc.Export(stagingFolder, prefix, ids, options);
            }
            default:
                throw new NotSupportedException("El formato CAD seleccionado no está soportado.");
        }
    }

    private static bool HasDwgSignature(string path)
    {
        try
        {
            using var stream = File.OpenRead(path);
            if (stream.Length < 6) return false;
            byte[] bytes = new byte[6];
            return stream.Read(bytes, 0, bytes.Length) == bytes.Length &&
                   System.Text.Encoding.ASCII.GetString(bytes).StartsWith("AC10", StringComparison.Ordinal);
        }
        catch { return false; }
    }

    private static bool HasDxfSignature(string path)
    {
        try
        {
            using var stream = File.OpenRead(path);
            byte[] bytes = new byte[Math.Min(8192, (int)Math.Min(stream.Length, 8192))];
            int read = stream.Read(bytes, 0, bytes.Length);
            if (read == 0) return false;
            string header = System.Text.Encoding.ASCII.GetString(bytes, 0, read);
            return header.Contains("SECTION", StringComparison.OrdinalIgnoreCase) ||
                   header.Contains("$ACADVER", StringComparison.OrdinalIgnoreCase) ||
                   header.StartsWith("AutoCAD Binary DXF", StringComparison.OrdinalIgnoreCase);
        }
        catch { return false; }
    }

    private static bool HasWriteAccess(string folder)=>ExportPathSupport.HasWriteAccess(folder);

    private static string ValidateFilePrefix(string input)
    {
        string name = (input ?? string.Empty).Trim();
        string extension = Path.GetExtension(name);
        if (new[] { ".dwg", ".dxf", ".dgn", ".pdf" }.Contains(extension, StringComparer.OrdinalIgnoreCase))
            name = name[..^extension.Length];

        if (string.IsNullOrWhiteSpace(name) || name is "." or ".." ||
            name.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 ||
            name.EndsWith(' ') || name.EndsWith('.'))
            throw new ArgumentException("El nombre o prefijo está vacío o contiene caracteres no válidos.");

        string stem = name.Split('.')[0].ToUpperInvariant();
        if (new[] { "CON", "PRN", "AUX", "NUL" }.Contains(stem) ||
            Regex.IsMatch(stem, @"^(COM|LPT)[1-9]$"))
            throw new ArgumentException("El nombre o prefijo está reservado por Windows.");
        return name;
    }

    private static string GetPreferencesPath() => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "RevitAutomation", "configuracion_cad.json");

    private sealed class CadExportForm : WF.Form
    {
        private readonly Document _doc;
        private readonly WF.ComboBox _sets = new() { DropDownStyle = WF.ComboBoxStyle.DropDownList };
        private readonly WF.ComboBox _format = new() { DropDownStyle = WF.ComboBoxStyle.DropDownList };
        private readonly WF.ComboBox _setup = new() { DropDownStyle = WF.ComboBoxStyle.DropDownList };
        private readonly WF.CheckedListBox _views = new() { CheckOnClick = true, IntegralHeight = false };
        private readonly WF.TextBox _folder = new();
        private readonly WF.TextBox _prefix = new();
        private readonly WF.Label _status = new() { AutoSize = false, TextAlign = System.Drawing.ContentAlignment.MiddleLeft };
        private bool _applyingPrintSet;
        private bool _restoringChecks;
        private string _setupMessage = "";
        private readonly WF.TextBox _setupInfo=new(){Dock=WF.DockStyle.Fill,Multiline=true,ReadOnly=true,ScrollBars=WF.ScrollBars.Vertical,BorderStyle=WF.BorderStyle.None};
        private IReadOnlyList<string> _excludedFromSet = Array.Empty<string>();

        public IReadOnlyList<ElementId> SelectedIds { get; private set; } = Array.Empty<ElementId>();
        public string OutputDirectory => _folder.Text.Trim();
        public string FilePrefix { get; private set; } = string.Empty;
        public FormatChoice SelectedFormat { get; private set; } = Formats[0];
        public string? SelectedSetupName { get; private set; }
        public string SelectedSetupLabel { get; private set; } = "Configuración de la sesión";
        public IReadOnlyList<string> ExcludedViews => _excludedFromSet;

        public CadExportForm(Document doc)
        {
            _doc = doc;
            Text = "RevitAutomation | Exportar CAD";
            AutoScaleMode = WF.AutoScaleMode.Dpi;
            MaximizeBox = true;
            MinimizeBox = true;
            StartPosition = WF.FormStartPosition.CenterScreen;

            var work = WF.Screen.FromPoint(WF.Cursor.Position).WorkingArea;
            int availableWidth = Math.Max(320, work.Width - 24);
            int availableHeight = Math.Max(300, work.Height - 24);
            MinimumSize = new System.Drawing.Size(Math.Min(700, availableWidth), Math.Min(600, availableHeight));
            Size = new System.Drawing.Size(Math.Min(1000, availableWidth), Math.Min(780, availableHeight));

            BuildUi();
            LoadData();
        }

        private static System.Drawing.Bitmap CreateHeaderBanner()
        {
            var bitmap = new System.Drawing.Bitmap(1200, 100);
            using var g = System.Drawing.Graphics.FromImage(bitmap);
            g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
            g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.ClearTypeGridFit;
            g.Clear(System.Drawing.Color.FromArgb(28, 43, 60));

            using var accent = new System.Drawing.SolidBrush(System.Drawing.Color.FromArgb(44, 166, 190));
            using var white = new System.Drawing.SolidBrush(System.Drawing.Color.White);
            using var muted = new System.Drawing.SolidBrush(System.Drawing.Color.FromArgb(205, 219, 230));
            using var line = new System.Drawing.Pen(System.Drawing.Color.FromArgb(91, 196, 214), 2.4f);
            using var titleFont = new System.Drawing.Font("Segoe UI", 25, System.Drawing.FontStyle.Bold, System.Drawing.GraphicsUnit.Pixel);
            using var subtitleFont = new System.Drawing.Font("Segoe UI", 15, System.Drawing.FontStyle.Regular, System.Drawing.GraphicsUnit.Pixel);
            using var formatFont = new System.Drawing.Font("Segoe UI", 14, System.Drawing.FontStyle.Bold, System.Drawing.GraphicsUnit.Pixel);

            g.FillRectangle(accent, 0, 0, 9, 100);

            // Icono vectorial de planos: no depende de ficheros externos ni de rutas locales.
            g.DrawRectangle(line, 33, 20, 31, 42);
            g.DrawLine(line, 40, 31, 57, 31);
            g.DrawLine(line, 40, 39, 57, 39);
            g.DrawLine(line, 40, 47, 53, 47);
            g.DrawLine(line, 51, 20, 64, 33);
            g.DrawLine(line, 51, 20, 51, 33);
            g.DrawLine(line, 51, 33, 64, 33);

            g.DrawString("RevitAutomation", titleFont, white, 84, 17);
            g.DrawString("Exportación profesional de planos y vistas", subtitleFont, muted, 87, 56);

            // Indicador de formatos soportados en el extremo derecho.
            g.DrawString("DWG   /   DXF   /   DGN", formatFont, accent, 890, 37);
            return bitmap;
        }

        private static System.Drawing.Bitmap CreateMiniIcon(string kind)
        {
            var bitmap = new System.Drawing.Bitmap(22, 22);
            using var g = System.Drawing.Graphics.FromImage(bitmap);
            g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
            g.Clear(System.Drawing.Color.Transparent);
            using var bg = new System.Drawing.SolidBrush(System.Drawing.Color.FromArgb(228, 240, 248));
            using var pen = new System.Drawing.Pen(System.Drawing.Color.FromArgb(42, 102, 132), 1.5f);
            using var accent = new System.Drawing.Pen(System.Drawing.Color.FromArgb(36, 151, 178), 1.5f);
            g.FillEllipse(bg, 1, 1, 20, 20);

            switch (kind)
            {
                case "views":
                    g.DrawRectangle(pen, 5, 6, 10, 12);
                    g.DrawRectangle(accent, 8, 4, 9, 12);
                    g.DrawLine(accent, 10, 8, 15, 8);
                    g.DrawLine(accent, 10, 11, 15, 11);
                    break;
                case "format":
                    g.DrawRectangle(pen, 5, 4, 12, 14);
                    g.DrawLine(accent, 7, 8, 15, 8);
                    g.DrawLine(accent, 7, 11, 15, 11);
                    g.DrawLine(accent, 7, 14, 12, 14);
                    break;
                case "settings":
                    g.DrawLine(pen, 5, 7, 17, 7);
                    g.DrawLine(pen, 5, 11, 17, 11);
                    g.DrawLine(pen, 5, 15, 17, 15);
                    g.DrawEllipse(accent, 8, 5, 4, 4);
                    g.DrawEllipse(accent, 13, 9, 4, 4);
                    g.DrawEllipse(accent, 7, 13, 4, 4);
                    break;
                case "folder":
                    g.DrawLine(pen, 4, 8, 9, 8);
                    g.DrawLine(pen, 9, 8, 11, 10);
                    g.DrawLine(pen, 11, 10, 18, 10);
                    g.DrawRectangle(pen, 4, 8, 14, 9);
                    g.DrawLine(accent, 5, 11, 17, 11);
                    break;
                default:
                    g.DrawRectangle(pen, 6, 4, 10, 14);
                    g.DrawLine(accent, 8, 9, 14, 9);
                    g.DrawLine(accent, 8, 12, 14, 12);
                    g.DrawLine(accent, 8, 15, 12, 15);
                    break;
            }
            return bitmap;
        }

        private static WF.Control CreateIconLabel(string text, string iconKind)
{
    var row = new WF.FlowLayoutPanel
    {
        Dock = WF.DockStyle.Fill,
        FlowDirection = WF.FlowDirection.LeftToRight,
        WrapContents = false,
        AutoScroll = false,
        Margin = new WF.Padding(0),
        Padding = new WF.Padding(0),
        BackColor = System.Drawing.Color.Transparent
    };

    var icon = new WF.PictureBox
    {
        Width = 22,
        Height = 22,
        Image = CreateMiniIcon(iconKind),
        SizeMode = WF.PictureBoxSizeMode.Zoom,
        Margin = new WF.Padding(0, 1, 5, 0),
        BackColor = System.Drawing.Color.Transparent
    };

    var caption = new WF.Label
    {
        Text = text,
        AutoSize = true,
        TextAlign = System.Drawing.ContentAlignment.MiddleLeft,
        Margin = new WF.Padding(0, 4, 0, 0),
        UseMnemonic = false
    };

    row.Controls.Add(icon);
    row.Controls.Add(caption);

    return row;
}

        private void BuildUi()
        {
            var root = new WF.TableLayoutPanel
            {
                Dock = WF.DockStyle.Fill,
                Padding = new WF.Padding(12),
                ColumnCount = 1,
                RowCount = 14
            };
            root.ColumnStyles.Add(new WF.ColumnStyle(WF.SizeType.Percent, 100));
            root.RowStyles.Add(new WF.RowStyle(WF.SizeType.Absolute, 78));
            root.RowStyles.Add(new WF.RowStyle(WF.SizeType.Absolute, 24));
            root.RowStyles.Add(new WF.RowStyle(WF.SizeType.Absolute, 34));
            root.RowStyles.Add(new WF.RowStyle(WF.SizeType.Absolute, 78));
            root.RowStyles.Add(new WF.RowStyle(WF.SizeType.Percent, 100));
            root.RowStyles.Add(new WF.RowStyle(WF.SizeType.Absolute, 24));
            root.RowStyles.Add(new WF.RowStyle(WF.SizeType.Absolute, 32));
            root.RowStyles.Add(new WF.RowStyle(WF.SizeType.Absolute, 24));
            root.RowStyles.Add(new WF.RowStyle(WF.SizeType.Absolute, 92));
            root.RowStyles.Add(new WF.RowStyle(WF.SizeType.Absolute, 24));
            root.RowStyles.Add(new WF.RowStyle(WF.SizeType.Absolute, 34));
            root.RowStyles.Add(new WF.RowStyle(WF.SizeType.Absolute, 24));
            root.RowStyles.Add(new WF.RowStyle(WF.SizeType.Absolute, 34));
            root.RowStyles.Add(new WF.RowStyle(WF.SizeType.Absolute, 48));

            var banner = new WF.PictureBox
            {
                Dock = WF.DockStyle.Fill,
                Image = CreateHeaderBanner(),
                SizeMode = WF.PictureBoxSizeMode.StretchImage,
                Margin = new WF.Padding(0, 0, 0, 6)
            };
            root.Controls.Add(banner, 0, 0);

            root.Controls.Add(CreateIconLabel("Conjunto guardado (o selección manual)", "views"), 0, 1);
            root.Controls.Add(_sets, 0, 2);
            _sets.Dock = WF.DockStyle.Fill;

            var selectionTools = new WF.FlowLayoutPanel { Dock = WF.DockStyle.Fill, WrapContents = false, AutoScroll = true };
            var selectAll = new WF.Button { Text = "Seleccionar todos los compatibles", AutoSize = true };
            var up = new WF.Button { Text = "Subir", Width = 70 };
            var down = new WF.Button { Text = "Bajar", Width = 70 };
            selectionTools.Controls.Add(selectAll);
            selectionTools.Controls.Add(up);
            selectionTools.Controls.Add(down);
            var selectionPanel=new WF.TableLayoutPanel{Dock=WF.DockStyle.Fill,ColumnCount=1,RowCount=2};
            selectionPanel.RowStyles.Add(new(WF.SizeType.Absolute,30));selectionPanel.RowStyles.Add(new(WF.SizeType.Percent,100));
            _status.Dock=WF.DockStyle.Fill;_status.AutoSize=false;_status.AutoEllipsis=true;_status.Padding=new WF.Padding(0,2,0,0);
            selectionPanel.Controls.Add(selectionTools,0,0);selectionPanel.Controls.Add(_status,0,1);
            root.Controls.Add(selectionPanel,0,3);

            root.Controls.Add(_views, 0, 4);
            _views.Dock = WF.DockStyle.Fill;
            root.Controls.Add(CreateIconLabel("Tipo de archivo", "format"), 0, 5);
            root.Controls.Add(_format, 0, 6);
            _format.Dock = WF.DockStyle.Fill;
            root.Controls.Add(CreateIconLabel("Configuración de exportación CAD", "settings"), 0, 7);
            var setupPanel=new WF.TableLayoutPanel{Dock=WF.DockStyle.Fill,ColumnCount=1,RowCount=2};
            setupPanel.RowStyles.Add(new(WF.SizeType.Absolute,32));setupPanel.RowStyles.Add(new(WF.SizeType.Percent,100));
            setupPanel.Controls.Add(_setup,0,0);setupPanel.Controls.Add(_setupInfo,0,1);
            root.Controls.Add(setupPanel,0,8);_setup.Dock=WF.DockStyle.Fill;
            root.Controls.Add(CreateIconLabel("Carpeta de destino", "folder"), 0, 9);

            var folderRow = new WF.TableLayoutPanel { Dock = WF.DockStyle.Fill, ColumnCount = 2, RowCount = 1 };
            folderRow.ColumnStyles.Add(new WF.ColumnStyle(WF.SizeType.Percent, 100));
            folderRow.ColumnStyles.Add(new WF.ColumnStyle(WF.SizeType.Absolute, 100));
            folderRow.Controls.Add(_folder, 0, 0);
            _folder.Dock = WF.DockStyle.Fill;
            var browse = new WF.Button { Text = "Examinar…", Dock = WF.DockStyle.Fill };
            folderRow.Controls.Add(browse, 1, 0);
            root.Controls.Add(folderRow, 0, 10);

            root.Controls.Add(CreateIconLabel("Nombre o prefijo de los archivos", "file"), 0, 11);
            root.Controls.Add(_prefix, 0, 12);
            _prefix.Dock = WF.DockStyle.Fill;

            var actionPanel = new WF.FlowLayoutPanel
            {
                FlowDirection = WF.FlowDirection.RightToLeft,
                Dock = WF.DockStyle.Fill,
                WrapContents = false
            };
            var cancel = new WF.Button { Text = "Cancelar", DialogResult = WF.DialogResult.Cancel, Width = 100, Height = 32 };
            var export = new WF.Button { Text = "Exportar CAD", Width = 125, Height = 32 };
            actionPanel.Controls.Add(cancel);
            actionPanel.Controls.Add(export);
            root.Controls.Add(actionPanel, 0, 13);

            AcceptButton = export;
            CancelButton = cancel;
            Controls.Add(root);

            foreach (FormatChoice item in Formats) _format.Items.Add(item);
            _format.SelectedIndex = 0; // DWG por defecto; el usuario conserva el selector visible.

            selectAll.Click += (_, _) =>
            {
                if (_sets.SelectedItem is PrintSetChoice) _sets.SelectedIndex = 0;
                CheckedListSelection.SelectAllCompatible(_views,item=>item is ViewChoice view&&view.CanExport);
                UpdateStatus();
            };
            up.Click += (_, _) => { SwitchSetToManual(); MoveChecked(-1); };
            down.Click += (_, _) => { SwitchSetToManual(); MoveChecked(1); };
            browse.Click += (_, _) =>
            {
                using var dialog = new WF.FolderBrowserDialog { SelectedPath = _folder.Text };
                if (dialog.ShowDialog(this) == WF.DialogResult.OK) _folder.Text = dialog.SelectedPath;
            };
            _sets.SelectedIndexChanged += (_, _) => ApplyPrintSet();
            _views.ItemCheck += OnViewItemCheck;
            _views.SelectedIndexChanged+=(_,_)=>UpdateStatus();
            _format.SelectedIndexChanged += (_, _) => { LoadSetups(); UpdateStatus(); };
            export.Click += (_, _) => TryAccept();
        }

        private void LoadData()
        {
            string defaultFolder = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "RevitAutomation", "CAD");
            try { Directory.CreateDirectory(defaultFolder); } catch { }

            ExportPreferences? preferences = null;
            string configPath = GetPreferencesPath();
            try
            {
                if (File.Exists(configPath))
                    preferences = JsonSerializer.Deserialize<ExportPreferences>(File.ReadAllText(configPath));
            }
            catch { /* La preferencia dañada se ignora; no afecta al documento ni a la exportación. */ }

            if (!string.IsNullOrWhiteSpace(preferences?.OutputFolder))
            {
                string savedFolder = Environment.ExpandEnvironmentVariables(preferences.OutputFolder);
                if (Directory.Exists(savedFolder)) defaultFolder = savedFolder;
            }
            _folder.Text = defaultFolder;
            _prefix.Text = Path.GetFileNameWithoutExtension(_doc.Title);

            _sets.Items.Add("-- Selección manual --");
            foreach (ViewSheetSet set in new FilteredElementCollector(_doc).OfClass(typeof(ViewSheetSet)).Cast<ViewSheetSet>()
                         .OrderBy(x => x.Name, StringComparer.CurrentCultureIgnoreCase))
            {
                var ids = new List<ElementId>();
                foreach (Autodesk.Revit.DB.View view in set.OrderedViewList) ids.Add(view.Id);
                _sets.Items.Add(new PrintSetChoice(set.Name, ids));
            }
            _sets.SelectedIndex = 0;

            if (!string.IsNullOrWhiteSpace(preferences?.Format))
            {
                for (int i = 0; i < _format.Items.Count; i++)
                    if (_format.Items[i] is FormatChoice value &&
                        (value.Key.Equals(preferences.Format, StringComparison.OrdinalIgnoreCase) ||
                         // Migrate preferences saved by the earlier format-only selector.
                         value.Format.ToString().Equals(preferences.Format, StringComparison.OrdinalIgnoreCase) &&
                         (value.Format == CadFormat.DWG && value.AcadVersion == ACADVersion.R2013 ||
                          value.Format == CadFormat.DXF && value.AcadVersion == ACADVersion.R2013 ||
                          value.Format == CadFormat.DGN)))
                    { _format.SelectedIndex = i; break; }
            }

            LoadSetups(preferences?.SetupName);

            var views = new FilteredElementCollector(_doc).OfClass(typeof(Autodesk.Revit.DB.View))
                .WhereElementIsNotElementType().Cast<Autodesk.Revit.DB.View>()
                .OrderBy(v => v.Name, StringComparer.CurrentCultureIgnoreCase).ThenBy(v => v.Id.Value);
            foreach(var choice in views.Select(CreateChoice).OrderByDescending(x=>x.CanExport).ThenBy(x=>x.Name,StringComparer.CurrentCultureIgnoreCase).ThenBy(x=>x.Id.Value))
                _views.Items.Add(choice,false);
            UpdateStatus();
        }

        private void LoadSetups(string? preferredName=null)
        {
            string? previous=preferredName??(_setup.SelectedItem as SetupChoice)?.Name;
            _setup.Items.Clear();
            _setup.Items.Add(new SetupChoice("Valores predeterminados de la API (no configuración de sesión)",null));
            _setupMessage="";
            try
            {
                var format=(_format.SelectedItem as FormatChoice)?.Format??CadFormat.DWG;
                var names=format==CadFormat.DGN?DGNExportOptions.GetPredefinedSetupNames(_doc):BaseExportOptions.GetPredefinedSetupNames(_doc);
                foreach(string name in names.OrderBy(x=>x,StringComparer.CurrentCultureIgnoreCase))
                {
                    using BaseExportOptions? options=format switch
                    {
                        CadFormat.DWG=>DWGExportOptions.GetPredefinedOptions(_doc,name),
                        CadFormat.DXF=>DXFExportOptions.GetPredefinedOptions(_doc,name),
                        CadFormat.DGN=>DGNExportOptions.GetPredefinedOptions(_doc,name),
                        _=>null
                    };
                    if(options is not null)_setup.Items.Add(new SetupChoice(name,name));
                }
                _setupMessage=_setup.Items.Count==1?"No hay configuraciones guardadas para este formato. Créala en Revit: Archivo > Exportar > Opciones > Configuraciones de exportación DWG/DXF o DGN; después vuelve a abrir esta ventana.":$"Configuraciones guardadas disponibles: {_setup.Items.Count-1}. La versión de archivo elegida prevalece sobre la del ajuste.";
            }
            catch(Exception ex)
            {
                _setup.Items.Clear();_setup.Items.Add(new SetupChoice("Valores predeterminados de la API",null));
                _setupMessage="Error al leer configuraciones: "+ex.Message+". Solo se ofrecen los valores predeterminados de la API.";
            }
            _setupInfo.Text=_setupMessage;
            _setup.SelectedIndex=0;
            for(int i=1;i<_setup.Items.Count;i++)if(_setup.Items[i] is SetupChoice setup&&setup.Name==previous){_setup.SelectedIndex=i;break;}
            _setup.Enabled=_setup.Items.Count>1;
        }

        private static ViewChoice CreateChoice(Autodesk.Revit.DB.View view)
        {
            try
            {
                bool canExport = view.CanBePrinted;
                return new ViewChoice(view.Id, view.Name, view.GetType().Name, canExport, canExport ? "" : "CanBePrinted es False");
            }
            catch (Exception ex)
            {
                return new ViewChoice(view.Id, view.Name, view.GetType().Name, false, "No se pudo consultar CanBePrinted: " + ex.Message);
            }
        }

        private void ApplyPrintSet()
        {
            _applyingPrintSet = true;
            try
            {
                for (int i = 0; i < _views.Items.Count; i++) _views.SetItemChecked(i, false);
                _excludedFromSet = Array.Empty<string>();
                if (_sets.SelectedItem is PrintSetChoice set)
                {
                    var excluded = new List<string>();
                    foreach (ElementId id in set.OrderedIds)
                    {
                        int index = FindViewIndex(id);
                        if (index < 0) { excluded.Add($"ID {id.Value}: no está disponible en la lista de vistas del documento."); continue; }
                        if (_views.Items[index] is not ViewChoice choice)
                        {
                            excluded.Add($"ID {id.Value}: no se pudo identificar la vista.");
                            continue;
                        }
                        if (!choice.CanExport)
                        {
                            excluded.Add($"{choice.Name} | ID {id.Value}: incompatible");
                            continue;
                        }
                        _views.SetItemChecked(index, true);
                    }
                    _excludedFromSet = excluded;
                }
            }
            finally { _applyingPrintSet = false; }
            UpdateStatus();
        }

        private void OnViewItemCheck(object? sender, WF.ItemCheckEventArgs e)
        {
            if (e.Index < 0 || e.Index >= _views.Items.Count) return;
            if (e.NewValue == WF.CheckState.Checked && _views.Items[e.Index] is ViewChoice choice && !choice.CanExport)
                e.NewValue = WF.CheckState.Unchecked;

            bool changeToManual = !_applyingPrintSet && !_restoringChecks && _sets.SelectedItem is PrintSetChoice;
            if (IsHandleCreated)
            {
                try
                {
                    BeginInvoke((Action)(() =>
                    {
                        if (changeToManual && _sets.SelectedItem is PrintSetChoice)
                            SwitchSetToManual();
                        UpdateStatus();
                    }));
                }
                catch (InvalidOperationException) { }
            }
        }

        private int FindViewIndex(ElementId id)
        {
            for (int i = 0; i < _views.Items.Count; i++)
                if (_views.Items[i] is ViewChoice choice && choice.Id == id) return i;
            return -1;
        }

        private void SwitchSetToManual()
        {
            if (_sets.SelectedItem is not PrintSetChoice) return;
            var currentlyChecked = _views.CheckedItems.Cast<ViewChoice>().Select(x => x.Id.Value).ToHashSet();
            _restoringChecks = true;
            try
            {
                _sets.SelectedIndex = 0;
                for (int i = 0; i < _views.Items.Count; i++)
                {
                    if (_views.Items[i] is ViewChoice choice)
                        _views.SetItemChecked(i, currentlyChecked.Contains(choice.Id.Value) && choice.CanExport);
                }
            }
            finally { _restoringChecks = false; }
            UpdateStatus();
        }

        private void MoveChecked(int delta)
        {
            int index = _views.SelectedIndex;
            int target = index + delta;
            if (index < 0 || target < 0 || target >= _views.Items.Count) return;
            object item = _views.Items[index]!;
            bool isChecked = _views.GetItemChecked(index);
            _views.Items.RemoveAt(index);
            _views.Items.Insert(target, item);
            _views.SetItemChecked(target, isChecked);
            _views.SelectedIndex = target;
        }

        private void UpdateStatus()
        {
            int checkedCount = _views.CheckedItems.Count;
            int incompatibleCount = _views.Items.Cast<object>().OfType<ViewChoice>().Count(x => !x.CanExport);
            string format = _format.SelectedItem is FormatChoice choice ? choice.Label : "AutoCAD 2013 DWG";
            string setWarning = _excludedFromSet.Count == 0 ? "" : $" | {_excludedFromSet.Count} incompatibles en el conjunto";
            string reason=_views.SelectedItem is ViewChoice active&&!active.CanExport?$"\r\nLa fila activa no se puede marcar: {active.Type}. {active.Reason}.":"\r\nLas vistas compatibles aparecen primero. Las incompatibles se muestran al final y no se exportan.";
            _status.Text = $"{checkedCount} casillas marcadas | {format} | {incompatibleCount} incompatibles{setWarning}"+reason;
        }

        private void TryAccept()
        {
            try
            {
                string folder = Path.GetFullPath(_folder.Text.Trim());
                if (!Directory.Exists(folder))
                    throw new DirectoryNotFoundException("La carpeta indicada no existe. Selecciona una carpeta existente.");
                if (!HasWriteAccess(folder))
                    throw new UnauthorizedAccessException("No hay permisos de escritura en la carpeta seleccionada.");

                if (_format.SelectedItem is not FormatChoice format)
                    throw new InvalidOperationException("Selecciona el tipo de archivo que quieres exportar.");

                var checkedIds = _views.CheckedItems.Cast<ViewChoice>().Select(x => x.Id.Value).ToHashSet();
                List<ElementId> ids;
                if (_sets.SelectedItem is PrintSetChoice set)
                {
                    // Keep the saved order, excluding only entries that the UI showed as incompatible.
                    ids = set.OrderedIds.Where(id => checkedIds.Contains(id.Value)).ToList();
                }
                else
                {
                    // In manual mode, order in the checked list is the export order.
                    ids = _views.CheckedItems.Cast<ViewChoice>().Where(x => x.CanExport).Select(x => x.Id).ToList();
                }

                if (ids.Count == 0)
                    throw new InvalidOperationException("No hay vistas o planos compatibles seleccionados.");

                if (_sets.SelectedItem is PrintSetChoice && _excludedFromSet.Count > 0)
                {
                    string exclusions = string.Join(Environment.NewLine, _excludedFromSet.Take(8));
                    if (_excludedFromSet.Count > 8)
                        exclusions += $"{Environment.NewLine}… y {_excludedFromSet.Count - 8} vistas más.";
                    WF.DialogResult continueWithout = WF.MessageBox.Show(
                        this,
                        $"El conjunto guardado contiene {_excludedFromSet.Count} vista(s) que Revit no permite exportar. No se incluirán en esta exportación:{Environment.NewLine}{Environment.NewLine}" +
                        exclusions + Environment.NewLine + Environment.NewLine + "¿Quieres continuar con el resto del conjunto?",
                        "Conjunto CAD con vistas incompatibles",
                        WF.MessageBoxButtons.YesNo,
                        WF.MessageBoxIcon.Warning,
                        WF.MessageBoxDefaultButton.Button2);
                    if (continueWithout != WF.DialogResult.Yes) return;
                }

                string prefix = ValidateFilePrefix(_prefix.Text);
                SelectedIds = ids;
                FilePrefix = prefix;
                SelectedFormat = format;
                SetupChoice setup = _setup.SelectedItem as SetupChoice ?? new SetupChoice("Configuración de la sesión (valores predeterminados)", null);
                SelectedSetupName = setup.Name;
                SelectedSetupLabel = setup.Label;
                SavePreferences(folder, format.Key, setup.Name);
                DialogResult = WF.DialogResult.OK;
                Close();
            }
            catch (Exception ex)
            {
                WF.MessageBox.Show(this, ex.Message, "No se puede preparar la exportación CAD", WF.MessageBoxButtons.OK, WF.MessageBoxIcon.Error);
            }
        }

        private static void SavePreferences(string folder, string format, string? setupName)
        {
            string path = GetPreferencesPath();
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            string temp = path + ".tmp";
            var preferences = new ExportPreferences { OutputFolder = folder, Format = format, SetupName = setupName };
            File.WriteAllText(temp, JsonSerializer.Serialize(preferences, new JsonSerializerOptions { WriteIndented = true }));
            if (File.Exists(path)) File.Replace(temp, path, null); else File.Move(temp, path);
        }
    }
}
