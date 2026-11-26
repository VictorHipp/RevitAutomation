#if NETFRAMEWORK
// Compatibilidad de ventanas para Revit 2023 y 2024 (.NET Framework 4.8). Véase src\CadMepRevit.Core\Compat\NetFrameworkCompat.cs.
using System.Windows.Forms;

namespace CadMepRevit.Compat;

/// <summary>Estado plegado de un grupo de lista (Windows Forms de .NET).</summary>
public enum ListViewGroupCollapsedState
{
    Default = 0,
    Expanded = 1,
    Collapsed = 2,
}

public static class NetFrameworkCompatUi
{
    extension(ListViewGroup group)
    {
        /// <summary>En .NET Framework los grupos no se pueden plegar: se muestran siempre desplegados.</summary>
        public ListViewGroupCollapsedState CollapsedState
        {
            get => ListViewGroupCollapsedState.Expanded;
            set { }
        }
    }
}
#endif
