using Autodesk.Revit.DB;

namespace RevitAutomation.Addin.Commands;

/// <summary>Acepta los avisos de Revit sin mostrarlos, para que cientos de elementos no paren la interfaz.</summary>
internal sealed class CadWarningSwallower : IFailuresPreprocessor
{
    internal int Warnings;

    public FailureProcessingResult PreprocessFailures(FailuresAccessor accessor)
    {
        foreach (var message in accessor.GetFailureMessages())
        {
            if (message.GetSeverity() != FailureSeverity.Warning) continue;
            accessor.DeleteWarning(message);
            Warnings++;
        }
        return FailureProcessingResult.Continue;
    }
}
