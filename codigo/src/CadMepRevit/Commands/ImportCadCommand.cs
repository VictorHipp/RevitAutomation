using System.Windows.Forms;
using Autodesk.Revit.Attributes;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using CadMepRevit.Core;
using CadMepRevit.Revit;
using CadMepRevit.Ui;

namespace CadMepRevit.Commands;

/// <summary>Botón «Importar CAD y construir MEP».</summary>
[Transaction(TransactionMode.Manual)]
[Regeneration(RegenerationOption.Manual)]
public sealed class ImportCadCommand : IExternalCommand
{
    private static string? _lastFolder;

    public Result Execute(ExternalCommandData commandData, ref string message, ElementSet elements)
    {
        var uiapp = commandData.Application;
        var uidoc = uiapp.ActiveUIDocument;

        // ---- Requisitos previos (solo lectura)
        if (uidoc?.Document is not { } doc)
        {
            Messages.Error("No hay ningún proyecto abierto.", "Abra o cree un proyecto de Revit y vuelva a pulsar el botón.");
            return Result.Cancelled;
        }
        if (doc.IsFamilyDocument)
        {
            Messages.Error("Está abierto un archivo de familia.", "Este comando trabaja en proyectos. Cambie a un proyecto (.rvt) y vuelva a intentarlo.");
            return Result.Cancelled;
        }
        if (doc.IsReadOnly)
        {
            Messages.Error("El proyecto está abierto en solo lectura.", "Guarde una copia editable o ábralo con permisos de escritura.");
            return Result.Cancelled;
        }

        RevitCatalog catalog;
        try { catalog = RevitCatalog.Load(doc); }
        catch (Exception ex)
        {
            Messages.Error("No se pudieron leer los tipos del proyecto.", "Vuelva a intentarlo. Si persiste, guarde, cierre y abra de nuevo el proyecto.", ex.ToString());
            return Result.Failed;
        }
        if (catalog.Levels.Count == 0)
        {
            Messages.Error("El proyecto no tiene niveles.", "Abra una vista de alzado y cree un nivel (Arquitectura > Nivel). Después repita el comando.");
            return Result.Cancelled;
        }
        if (catalog.Types.Values.All(t => t.Count == 0))
        {
            Messages.Error("El proyecto no tiene tipos de tubería, conducto, bandeja ni tubo eléctrico.",
                "Cree el proyecto con una plantilla MEP de Revit ('Sistemas', 'Mecánica', 'Fontanería' o 'Eléctrica') " +
                "o copie los tipos desde otro proyecto con Gestionar > Transferir normas de proyecto.");
            return Result.Cancelled;
        }
        // Un proyecto de la plantilla de Arquitectura tiene tipos de muro, pero no de tubería ni de conducto:
        // sin este aviso el asistente solo dejaba crear marcadores de los bloques y no se veía por qué.
        if (catalog.Types[MepKind.Tuberia].Count == 0 && catalog.Types[MepKind.Conducto].Count == 0 &&
            !Messages.Confirm("El proyecto no tiene tipos de tubería ni de conducto.",
                "Parece creado con la plantilla de Arquitectura. Así solo se podrán crear muros y marcadores de los bloques: " +
                "NO se crearán tuberías, conductos, codos ni tes.\n\n" +
                "Cómo corregirlo: cancele y abra un proyecto creado con la plantilla 'Sistemas' o 'Mecánica', o copie los tipos " +
                "de tubería y conducto (con sus familias de unión) desde otro proyecto con Gestionar > Transferir normas de proyecto.",
                "Continuar igualmente (solo muros y bloques)"))
            return Result.Cancelled;

        var owner = new RevitWindow(uiapp.MainWindowHandle);

        // ---- Paso 1: elegir el archivo
        string path;
        using (var dlg = new OpenFileDialog
               {
                   Title = "CAD-REV · Paso 1 de 3: elija el plano CAD (DWG o DXF)",
                   Filter = "Planos CAD (*.dwg;*.dxf)|*.dwg;*.dxf|AutoCAD DWG (*.dwg)|*.dwg|DXF (*.dxf)|*.dxf",
                   CheckFileExists = true,
                   InitialDirectory = _lastFolder ?? ""
               })
        {
            if (dlg.ShowDialog(owner) != DialogResult.OK) return Result.Cancelled;
            path = dlg.FileName;
            _lastFolder = Path.GetDirectoryName(path);
        }

        CadDrawing? Read(bool insideBlocks)
        {
            Cursor.Current = Cursors.WaitCursor;
            try { return CadReader.Read(path, new CadReader.Options { ReadLinesInsideBlocks = insideBlocks }); }
            catch (CadReadException ex) { Messages.Error(ex.Message, ex.HowToFix); return null; }
            catch (Exception ex)
            {
                Messages.Error("Error inesperado al leer el CAD.", "Pruebe a guardar el plano como DXF o DWG 2018 y vuelva a intentarlo.", ex.ToString());
                return null;
            }
            finally { Cursor.Current = Cursors.Default; }
        }

        var drawing = Read(false);
        if (drawing is null) return Result.Cancelled;
        if (drawing.Segments.Count == 0 && drawing.Blocks.Count == 0)
        {
            Messages.Error("El plano no contiene líneas ni bloques en el espacio modelo.",
                "Compruebe que las tuberías están dibujadas con LÍNEA o POLILÍNEA en el espacio modelo (no en una presentación) y que no son una referencia externa (XREF) sin unir.",
                string.Join("\n", drawing.Warnings));
            return Result.Cancelled;
        }

        // ---- Paso 2 y 3: mapeo + previsualización (no tocan el modelo)
        BuildPlan plan;
        using (var form = new ImportForm(drawing, catalog, Read))
        {
            if (form.ShowDialog(owner) != DialogResult.OK || form.Result is null) return Result.Cancelled;
            plan = form.Result;
        }

        // ---- Confirmación final antes de modificar el modelo
        var lines = MepKinds.All.Skip(1).Where(k => plan.Count(k) > 0)
            .Select(k => $"• {MepKinds.Label(k)}: {plan.Count(k)}").ToList();
        if (plan.CreateFittings)
            lines.Add($"• Uniones a intentar: {plan.Network.Count(JunctionKind.Codo)} codos, {plan.Network.Count(JunctionKind.Te)} tes, {plan.Network.Count(JunctionKind.Cruz)} cruces, {plan.Network.Count(JunctionKind.Transicion)} transiciones");
        if (plan.BlockCount > 0) lines.Add($"• Familias en bloques: {plan.BlockCount}");
        if (plan.MarkerCount > 0) lines.Add($"• Marcadores 3D sin familia (equipos, rejillas… con su huella del CAD): {plan.MarkerCount}");
        foreach (var w in plan.Warnings) lines.Add("⚠ " + w);
        if (!Messages.Confirm(
                "¿Construir ahora estos elementos en el modelo?",
                $"Nivel '{plan.LevelName}', altura {plan.HeightAboveLevelMm:0} mm.\n" + string.Join("\n", lines),
                "Sí, construir en el modelo",
                "Se hará en una única operación ('CAD-REV: construir desde CAD') que puede deshacer con Ctrl+Z."))
            return Result.Cancelled;

        BuildReport report;
        try
        {
            Cursor.Current = Cursors.WaitCursor;
            report = MepBuilder.Build(doc, plan);
        }
        catch (Exception ex)
        {
            string failFile = SaveReport(doc, plan, null, "LA CONSTRUCCIÓN SE DETUVO (no se guardó ningún cambio):\n" + ex);
            Messages.Error("La construcción se detuvo y no se ha guardado ningún cambio.",
                "Revise el mensaje técnico. Causas habituales: el tipo de tubería no tiene segmento/tamaños en sus preferencias de enrutamiento, o el nivel fue borrado." +
                (failFile.Length > 0 ? "\nInforme guardado en: " + failFile : ""),
                ex.ToString());
            return Result.Failed;
        }
        finally { Cursor.Current = Cursors.Default; }

        if (report.Status != TransactionStatus.Committed)
        {
            string cancelFile = SaveReport(doc, plan, report, "REVIT CANCELÓ LA OPERACIÓN (no se guardó ningún cambio). Estado: " + report.Status);
            Messages.Error("Revit canceló la operación y no se ha guardado ningún cambio.",
                "Puede deberse a errores graves de Revit (no avisos). Revise el detalle, corrija el CAD o los tipos y repita." +
                (cancelFile.Length > 0 ? "\nInforme guardado en: " + cancelFile : ""),
                string.Join("\n", report.Problems.Take(100)));
            return Result.Cancelled;
        }

        var existing = report.CreatedIds.Where(id => doc.GetElement(id) != null).ToList();
        try { uidoc.Selection.SetElementIds(existing); } catch { /* la selección es solo una ayuda */ }

        string reportFile = SaveReport(doc, plan, report, null);
        string extra = plan.TranslationMm.Length > 0
            ? $"\nEl dibujo se desplazó X={plan.TranslationMm.X / 1000:0.###} m, Y={plan.TranslationMm.Y / 1000:0.###} m respecto a sus coordenadas CAD."
            : "";
        Messages.Info("Construcción terminada",
            report.Summary() + extra +
            "\n\nLos elementos creados quedan seleccionados. Ctrl+Z deshace todo." +
            (reportFile.Length > 0 ? "\nInforme completo guardado en: " + reportFile : ""),
            report.Problems.Count == 0 ? null : "Detalle de incidencias:\n" + string.Join("\n", report.Problems.Take(200)) +
                (report.Problems.Count > 200 ? $"\n… y {report.Problems.Count - 200} más." : ""));
        return Result.Succeeded;
    }

    /// <summary>
    /// Guarda el informe en un archivo de texto para poder revisarlo o enviarlo: siempre en
    /// ultimo_informe.txt y, además, una copia con fecha en la carpeta "informes" (así una prueba no borra
    /// el informe de la anterior). Se guarda también cuando la construcción falla. Devuelve la ruta o "".
    /// </summary>
    internal static string SaveReport(Document doc, BuildPlan plan, BuildReport? report, string? failure, string? header = null)
    {
        try
        {
            string dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "CadMepRevit");
            Directory.CreateDirectory(dir);
            string file = Path.Combine(dir, "ultimo_informe.txt");
            var now = DateTime.Now;
            var sb = new System.Text.StringBuilder();
            if (header != null)
            {
                // Informe de «Modelar desde CAD» (resumen para el técnico) seguido del detalle técnico de siempre.
                sb.AppendLine(header);
                sb.AppendLine();
                sb.AppendLine("==================== DETALLE TÉCNICO ====================");
            }
            sb.AppendLine("CAD-REV · " + now.ToString("yyyy-MM-dd HH:mm:ss") + " · " + doc.Title);
            try
            {
                var app = doc.Application;
                var asm = System.Reflection.Assembly.GetExecutingAssembly();
                sb.AppendLine($"Revit: {app.VersionName} (compilación {app.VersionBuild}), idioma {app.Language}");
                sb.AppendLine($"Complemento: versión {asm.GetName().Version}, compilado el {File.GetLastWriteTime(asm.Location):yyyy-MM-dd HH:mm}, en {asm.Location}");
            }
            catch { /* datos informativos */ }
            sb.AppendLine($"Plano: {plan.SourcePath}");
            sb.AppendLine($"Unidades {plan.UnitsLabel}, tolerancia de unión {plan.ToleranceMm:0.#} mm, longitud mínima {plan.MinLengthMm:0.#} mm" +
                          (plan.TranslationMm.Length > 0 ? $", dibujo desplazado X={plan.TranslationMm.X / 1000:0.###} m Y={plan.TranslationMm.Y / 1000:0.###} m" : ""));
            sb.AppendLine($"Nivel {plan.LevelName}, altura {plan.HeightAboveLevelMm:0} mm, uniones {(plan.CreateFittings ? "sí" : "no")}, conexión automática {(plan.AutoConnect ? "sí" : "no")}");
            if (failure != null) { sb.AppendLine(); sb.AppendLine(failure); }

            sb.AppendLine();
            sb.AppendLine("CAPAS");
            foreach (var lp in plan.Layers.Values)
            {
                var type = lp.TypeId is null ? null : doc.GetElement(lp.TypeId);
                sb.AppendLine($"Capa {lp.Layer}: {lp.Kind}, tipo {type?.Name ?? "(automático)"}, sistema {(lp.SystemTypeId is null ? "-" : doc.GetElement(lp.SystemTypeId)?.Name)}, " +
                              $"tamaño {lp.SizeMm} x {lp.HeightMm}, doble línea {lp.DoubleLine}, {plan.Network.Segments.Count(s => string.Equals(s.Layer, lp.Layer, StringComparison.OrdinalIgnoreCase))} tramos previstos");
                if (type is MEPCurveType mt && !(plan.Scope == ModelScope.Bandejas && lp.Kind == MepKind.TuboElectrico))
                    sb.AppendLine($"   Tipo '{mt.Name}': {RevitCatalog.DescribeRouting(doc, mt)}");
            }
            if (plan.BlockPlans.Count > 0)
            {
                sb.AppendLine();
                sb.AppendLine("BLOQUES");
                foreach (var bp in plan.BlockPlans)
                    sb.AppendLine($"Bloque {BlockNames.Clean(bp.BlockName)} (capa {bp.Layer}): {bp.Action}" +
                                  (bp.Action == BlockAction.Familia ? $" '{doc.GetElement(bp.SymbolId)?.Name}'" : "") +
                                  $", altura {bp.ElevationMm:0} mm, alto de marcador {bp.HeightMm:0} mm, {plan.Blocks.Count(b => BuildPlan.Same(bp, b.Block))} veces");
            }
            sb.AppendLine();
            sb.AppendLine("UNIONES PREVISTAS");
            foreach (var j in plan.Network.Junctions.Where(j => plan.AllowsFitting(plan.Layers[j.Layer].Kind, j.Kind) ||
                j.Kind == JunctionKind.NoAdmitida && ModelScopes.AllowsAnyFitting(plan.Scope, plan.Layers[j.Layer].Kind)))
                sb.AppendLine($"Unión prevista: {j.Kind} en {j.Point} ({j.Layer}) {j.Note}");
            foreach (var w in plan.Warnings) sb.AppendLine("Aviso: " + w);
            if (report != null)
            {
                sb.AppendLine();
                sb.AppendLine("RESULTADO");
                sb.AppendLine(report.Summary());
                foreach (var p in report.Problems) sb.AppendLine("- " + p);
                sb.AppendLine();
                sb.AppendLine("DIAGNÓSTICO (lo que Revit ha creado realmente)");
                foreach (var d in report.Diagnostics) sb.AppendLine("· " + d);
            }
            var encoding = new System.Text.UTF8Encoding(true);
            File.WriteAllText(file, sb.ToString(), encoding);
            try
            {
                string history = Path.Combine(dir, "informes");
                Directory.CreateDirectory(history);
                File.WriteAllText(Path.Combine(history, "informe_" + now.ToString("yyyyMMdd_HHmmss") + ".txt"), sb.ToString(), encoding);
            }
            catch { /* la copia con fecha es una ayuda */ }
            return file;
        }
        catch { return ""; }
    }
}
