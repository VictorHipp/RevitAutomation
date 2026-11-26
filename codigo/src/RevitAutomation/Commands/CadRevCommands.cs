using Autodesk.Revit.Attributes;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using CadMepRevit.Commands;
using CadMepRevit.Core;

namespace RevitAutomation.Addin.Commands.CadRev;

// Botones de CAD-REV. Están en RevitAutomation.dll (heredan del motor, CadMepRevit.dll) para que Revit 2026 y 2027 los
// carguen en el mismo contexto aislado que el resto del complemento. Cada uno solo dice qué instalación modela.

/// <summary>CAD-REV · Tuberías: cotas, pendientes, codos, tes, reducciones y accesorios.</summary>
[Transaction(TransactionMode.Manual)]
[Regeneration(RegenerationOption.Manual)]
public sealed class CadRevPipesCommand : ModelFromCadCommand { protected override ModelScope? Scope => ModelScope.Tuberias; }

/// <summary>CAD-REV · Conductos con sus uniones, rejillas, difusores, compuertas y equipos de clima.</summary>
[Transaction(TransactionMode.Manual)]
[Regeneration(RegenerationOption.Manual)]
public sealed class CadRevDuctsCommand : ModelFromCadCommand { protected override ModelScope? Scope => ModelScope.Conductos; }

/// <summary>CAD-REV · Bandejas de cables y tubos eléctricos.</summary>
[Transaction(TransactionMode.Manual)]
[Regeneration(RegenerationOption.Manual)]
public sealed class CadRevTraysCommand : ModelFromCadCommand { protected override ModelScope? Scope => ModelScope.Bandejas; }

/// <summary>CAD-REV · Luminarias, interruptores y detectores de presencia.</summary>
[Transaction(TransactionMode.Manual)]
[Regeneration(RegenerationOption.Manual)]
public sealed class CadRevLightingCommand : ModelFromCadCommand { protected override ModelScope? Scope => ModelScope.Luminarias; }

/// <summary>CAD-REV · Equipos, PCI, cuadros, tomas y dispositivos.</summary>
[Transaction(TransactionMode.Manual)]
[Regeneration(RegenerationOption.Manual)]
public sealed class CadRevEquipmentCommand : ModelFromCadCommand { protected override ModelScope? Scope => ModelScope.EquiposPci; }

/// <summary>Los botones de CAD-REV solo se activan con un proyecto abierto (no en el editor de familias).</summary>
public sealed class CadRevAvailability : IExternalCommandAvailability
{
    public bool IsCommandAvailable(UIApplication applicationData, CategorySet selectedCategories) =>
        applicationData.ActiveUIDocument?.Document is { IsFamilyDocument: false };
}
