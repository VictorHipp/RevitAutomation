using System.Diagnostics;
using System.Reflection;
using Autodesk.Revit.Attributes;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using CadMepRevit.Ui;

namespace CadMepRevit.Commands;

/// <summary>Botón «Ayuda». No modifica el modelo.</summary>
[Transaction(TransactionMode.ReadOnly)]
public sealed class HelpCommand : IExternalCommand
{
    public Result Execute(ExternalCommandData commandData, ref string message, ElementSet elements)
    {
        string folder = Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location) ?? "";
        string guide = Path.Combine(folder, "Guia_CadMepRevit.txt");
        string samples = Path.Combine(folder, "Ejemplos");

        var td = new TaskDialog(Messages.Title)
        {
            MainIcon = TaskDialogIcon.TaskDialogIconInformation,
            MainInstruction = "Cómo usar CAD-REV",
            MainContent = HelpText.Short,
            CommonButtons = TaskDialogCommonButtons.Close
        };
        if (File.Exists(guide)) td.AddCommandLink(TaskDialogCommandLinkId.CommandLink1, "Abrir la guía completa");
        if (Directory.Exists(samples)) td.AddCommandLink(TaskDialogCommandLinkId.CommandLink2, "Abrir la carpeta con el plano de ejemplo");

        var r = td.Show();
        try
        {
            if (r == TaskDialogResult.CommandLink1) Process.Start(new ProcessStartInfo(guide) { UseShellExecute = true });
            if (r == TaskDialogResult.CommandLink2) Process.Start(new ProcessStartInfo(samples) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            Messages.Error("No se pudo abrir el archivo.", "Ábralo manualmente desde: " + folder, ex.Message);
        }
        return Result.Succeeded;
    }
}

/// <summary>Los comandos de construcción solo se activan con un proyecto abierto.</summary>
public sealed class ProjectAvailability : IExternalCommandAvailability
{
    public bool IsCommandAvailable(UIApplication applicationData, CategorySet selectedCategories) =>
        applicationData.ActiveUIDocument?.Document is { IsFamilyDocument: false };
}

/// <summary>La ayuda está siempre disponible, incluso sin documentos abiertos.</summary>
public sealed class AlwaysAvailable : IExternalCommandAvailability
{
    public bool IsCommandAvailable(UIApplication applicationData, CategorySet selectedCategories) => true;
}
