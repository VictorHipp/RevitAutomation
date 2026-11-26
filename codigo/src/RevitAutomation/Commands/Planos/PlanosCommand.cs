using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.Attributes;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using RevitTaskDialog = Autodesk.Revit.UI.TaskDialog;
using View = Autodesk.Revit.DB.View;

namespace RevitAutomation.Addin.Commands.Planos;

// PLANOS: turns the existing BIM model into technical drawings. It creates native plan views, schedules, a colour legend,
// filters, tags and sheets with title block. It never edits model elements (geometry, position, parameters, connections),
// never deletes or changes views/sheets created by the user, and replaces only its own previous output (extensible
// storage mark). Everything runs in one transaction group: on any error the project is left as it was.

[Transaction(TransactionMode.Manual)] public sealed class PlanosPlumbingCommand : PlanosCommandBase { public PlanosPlumbingCommand() : base(PlanosDiscipline.Fontaneria) { } }
[Transaction(TransactionMode.Manual)] public sealed class PlanosHvacCommand : PlanosCommandBase { public PlanosHvacCommand() : base(PlanosDiscipline.Climatizacion) { } }
[Transaction(TransactionMode.Manual)] public sealed class PlanosElectricalCommand : PlanosCommandBase { public PlanosElectricalCommand() : base(PlanosDiscipline.Electricidad) { } }
[Transaction(TransactionMode.Manual)] public sealed class PlanosLightingCommand : PlanosCommandBase { public PlanosLightingCommand() : base(PlanosDiscipline.Iluminacion) { } }
[Transaction(TransactionMode.Manual)] public sealed class PlanosGenericCommand : PlanosCommandBase { public PlanosGenericCommand() : base(PlanosDiscipline.Genericas) { } }
[Transaction(TransactionMode.Manual)] public sealed class PlanosAllCommand : PlanosCommandBase { public PlanosAllCommand() : base(null) { } }

public abstract class PlanosCommandBase : IExternalCommand
{
    private readonly PlanosDiscipline? _discipline;

    private protected PlanosCommandBase(PlanosDiscipline? discipline) => _discipline = discipline;

    private string Title => "Planos · " + (_discipline is { } d ? PlanosNaming.DisciplineLabel(d) : "Conjunto completo");

    public Result Execute(ExternalCommandData data, ref string message, ElementSet elements)
    {
        UIDocument? ui = data.Application.ActiveUIDocument;
        if (ui is null || ui.Document.IsFamilyDocument)
        {
            RevitTaskDialog.Show(Title, "Abra un proyecto de Revit y vuelva a pulsar el botón. No se ha modificado nada.");
            return Result.Cancelled;
        }
        Document doc = ui.Document;
        if (doc.IsReadOnly)
        {
            RevitTaskDialog.Show(Title, "El proyecto está abierto en solo lectura: no se pueden crear vistas ni láminas.\n\n" +
                "Cómo corregirlo: guarde una copia editable o ábralo con permisos de escritura. No se ha modificado nada.");
            return Result.Cancelled;
        }

        // 1 · Read the project (nothing is changed).
        PlanosInventory inv;
        try { inv = PlanosInventory.Read(doc); }
        catch (Exception ex)
        {
            message = ex.ToString();
            RevitTaskDialog.Show(Title, "No se pudo analizar el proyecto: " + ex.Message + "\n\nNo se ha modificado nada.");
            return Result.Failed;
        }
        if (inv.Levels.Count == 0)
        {
            RevitTaskDialog.Show(Title, "El proyecto no tiene niveles: no se pueden crear plantas.\n\nCómo corregirlo: cree los niveles en un alzado o sección. No se ha modificado nada.");
            return Result.Cancelled;
        }
        var initial = _discipline is { } one ? new List<PlanosDiscipline> { one } : Enum.GetValues(typeof(PlanosDiscipline)).Cast<PlanosDiscipline>().ToList();
        if (!initial.Any(d => inv.Of(d).Any()))
        {
            new RevitTaskDialog(Title)
            {
                MainInstruction = _discipline is { } d ? "No hay elementos de " + PlanosNaming.DisciplineLabel(d).ToLowerInvariant() + " en el modelo." : "No hay instalaciones que documentar en el modelo.",
                MainContent = "Se buscan por su categoría y su sistema de Revit (no por el nombre): tuberías, conductos, bandejas, cuadros, luminarias, " +
                              "modelos genéricos y elementos de protección contra incendios. No se ha creado nada."
            }.Show();
            return Result.Cancelled;
        }

        // 2 · Configuration and summary (nothing is changed before «Generar»).
        PlanosChoices choices;
        PlanosPlan plan;
        using (var form = new PlanosForm(Title, inv, initial, chooseDisciplines: _discipline is null))
        {
            if (form.ShowDialog(new PlanosOwner(data.Application.MainWindowHandle)) != System.Windows.Forms.DialogResult.OK) return Result.Cancelled;
            choices = form.Choices;
            plan = form.Plan;
        }

        // 3 · A view about to be replaced cannot be deleted while it is the active one: switch to a view of the user first.
        var replaced = new HashSet<ElementId>(choices.Options.Disciplines.SelectMany(d => inv.Owned.Where(o => o.Discipline == d.ToString())).Select(o => o.Id));
        if (replaced.Contains(ui.ActiveView.Id))
        {
            var other = new FilteredElementCollector(doc).OfClass(typeof(View)).Cast<View>()
                .FirstOrDefault(v => !v.IsTemplate && v.CanBePrinted && !replaced.Contains(v.Id) && v.ViewType is ViewType.FloorPlan or ViewType.DrawingSheet or ViewType.ThreeD);
            if (other is null)
            {
                RevitTaskDialog.Show(Title, "La vista activa es de una generación anterior y no hay otra vista a la que cambiar. Abra otra vista y vuelva a pulsar el botón. No se ha modificado nada.");
                return Result.Cancelled;
            }
            try { ui.ActiveView = other; }
            catch (Exception ex)
            {
                RevitTaskDialog.Show(Title, "Cierre la vista activa (es de una generación anterior que se va a sustituir) y vuelva a pulsar el botón (" + ex.Message + "). No se ha modificado nada.");
                return Result.Cancelled;
            }
        }

        // 4 · Generate (one transaction group: all or nothing).
        PlanosResult result;
        try { result = new PlanosBuilder(doc, inv, choices).Run(plan); }
        catch (Exception ex)
        {
            message = ex.ToString();
            string step = ex is PlanosStepException s ? s.Step : "generación";
            new RevitTaskDialog(Title)
            {
                MainInstruction = "No se han generado los planos",
                MainContent = "Falló el paso: " + step + ".\nCausa: " + (ex.InnerException ?? ex).Message +
                              "\n\nSe ha deshecho todo: el proyecto queda como estaba (no hay generación a medias).",
                ExpandedContent = ex.ToString()
            }.Show();
            return Result.Failed;
        }

        // 5 · PDF combinado en el Escritorio (las láminas generadas; en «Solo vistas», las vistas). Un fallo aquí no deshace los planos.
        string pdfLine = "";
        if (choices.Options.Pdf)
        {
            var ids = choices.Options.Mode == PlanosMode.SoloVistas
                ? result.ViewIds.Where(id => doc.GetElement(id) is View).ToList()
                : result.SheetIds.Select(id => doc.GetElement(id)).OfType<ViewSheet>()
                    .OrderBy(s => s.SheetNumber, StringComparer.OrdinalIgnoreCase).Select(s => s.Id).ToList();
            if (ids.Count == 0) pdfLine = "PDF: no hay láminas ni vistas nuevas que exportar.";
            else
            {
                string desktop = Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);
                string name = PdfName(Title, doc.Title);
                try
                {
                    PdfExportCommand.Export(doc, ids, desktop, name, false);
                    pdfLine = $"PDF ({ids.Count} {(choices.Options.Mode == PlanosMode.SoloVistas ? "vistas" : "láminas")}): {System.IO.Path.Combine(desktop, name + ".pdf")}";
                }
                catch (Exception ex) { pdfLine = "PDF: no se pudo generar (" + ex.Message + "). Los planos sí están creados; puede exportarlos con «Exportar PDF»."; }
            }
        }

        // 6 · Open the first sheet (or view) and report.
        try { if (((View?)result.FirstSheet ?? result.FirstView) is { } open) ui.ActiveView = open; } catch { /* the user opens it from the Project Browser */ }
        var main = new List<string>
        {
            $"Vistas en planta: {result.Views}" + (result.ReplacedViews > 0 ? $" (sustituyen a {result.ReplacedViews} de la generación anterior)" : ""),
            $"Láminas: {result.Sheets}" + (result.ReplacedSheets > 0 ? $" (sustituyen a {result.ReplacedSheets})" : ""),
            $"Cuadros: {result.Schedules} · Leyendas: {result.Legends} · Etiquetas: {result.Tags} · Filtros nuevos: {result.Filters}",
            pdfLine,
            "",
            "Navegador de proyectos: las láminas en «Planos (todo)», las vistas en «Vistas» por disciplina (Fontanería, Mecánica, Eléctrica, Coordinación).",
            "El modelo no se ha modificado. Puede deshacerlo todo con Ctrl+Z."
        };
        new RevitTaskDialog(Title)
        {
            MainInstruction = "Planos generados",
            MainContent = string.Join("\n", main),
            ExpandedContent = result.Notes.Count == 0 ? "Sin avisos." : string.Join("\n", result.Notes.Select(n => "• " + n))
        }.Show();
        return Result.Succeeded;
    }

    /// <summary>«Planos Fontanería - Proyecto - 2026-11-19 1030» sin caracteres no válidos (no sobrescribe: lleva hora y segundos si hace falta).</summary>
    internal static string PdfName(string title, string project)
    {
        string raw = title.Replace("Planos · ", "Planos ") + " - " + project + " - " + DateTime.Now.ToString("yyyy-MM-dd HHmm");
        var invalid = System.IO.Path.GetInvalidFileNameChars();
        string name = new string(raw.Select(c => invalid.Contains(c) ? '_' : c).ToArray()).Trim().TrimEnd('.');
        string desktop = Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);
        if (System.IO.File.Exists(System.IO.Path.Combine(desktop, name + ".pdf"))) name += DateTime.Now.ToString(" ss");
        return name;
    }
}

/// <summary>Lets Revit's main window own the WinForms dialog.</summary>
internal sealed class PlanosOwner : System.Windows.Forms.IWin32Window
{
    internal PlanosOwner(IntPtr handle) => Handle = handle;
    public IntPtr Handle { get; }
}
