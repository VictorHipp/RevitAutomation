using Autodesk.Revit.UI;

namespace CadMepRevit.Ui;

/// <summary>Mensajes uniformes para principiantes: qué pasó, cómo corregirlo y detalle técnico.</summary>
public static class Messages
{
    public const string Title = "CAD-REV";

    public static void Error(string what, string howToFix, string? technicalDetail = null)
    {
        var td = new TaskDialog(Title)
        {
            MainIcon = TaskDialogIcon.TaskDialogIconError,
            MainInstruction = what,
            MainContent = "Cómo corregirlo:\n" + howToFix,
            CommonButtons = TaskDialogCommonButtons.Close
        };
        if (!string.IsNullOrWhiteSpace(technicalDetail))
            td.ExpandedContent = "Detalle técnico:\n" + technicalDetail;
        td.Show();
    }

    public static void Info(string instruction, string content, string? expanded = null)
    {
        var td = new TaskDialog(Title)
        {
            MainIcon = TaskDialogIcon.TaskDialogIconInformation,
            MainInstruction = instruction,
            MainContent = content,
            CommonButtons = TaskDialogCommonButtons.Close
        };
        if (!string.IsNullOrWhiteSpace(expanded)) td.ExpandedContent = expanded;
        td.Show();
    }

    /// <summary>Pide confirmación explícita antes de modificar el modelo.</summary>
    public static bool Confirm(string instruction, string content, string yesText, string? expanded = null)
    {
        var td = new TaskDialog(Title)
        {
            MainIcon = TaskDialogIcon.TaskDialogIconWarning,
            MainInstruction = instruction,
            MainContent = content,
            CommonButtons = TaskDialogCommonButtons.Cancel,
            DefaultButton = TaskDialogResult.Cancel
        };
        td.AddCommandLink(TaskDialogCommandLinkId.CommandLink1, yesText);
        if (!string.IsNullOrWhiteSpace(expanded)) td.ExpandedContent = expanded;
        return td.Show() == TaskDialogResult.CommandLink1;
    }
}

/// <summary>Permite usar la ventana principal de Revit como propietaria de los formularios.</summary>
public sealed class RevitWindow : System.Windows.Forms.IWin32Window
{
    public RevitWindow(IntPtr handle) => Handle = handle;
    public IntPtr Handle { get; }
}
