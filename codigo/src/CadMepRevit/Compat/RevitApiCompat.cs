// Compatibilidad de la API de Revit entre versiones (2023 a 2027).
// Revit 2023 identifica los elementos con un entero de 32 bits (ElementId.IntegerValue); desde 2024 con uno de 64 bits
// (ElementId.Value y new ElementId(long)). El código usa el de 64 bits; para 2023 se traduce aquí.
using Autodesk.Revit.DB;

namespace CadMepRevit.Compat;

public static class RevitIds
{
    /// <summary>Crea el identificador de un elemento a partir de su número (en Revit 2023, de 32 bits).</summary>
    public static ElementId FromValue(long value) =>
#if REVIT2023
        new ElementId(checked((int)value));
#else
        new ElementId(value);
#endif
}

#if REVIT2023
public static class RevitApiCompat2023
{
    extension(ElementId id)
    {
        /// <summary>Número del elemento (ElementId.Value de Revit 2024 y posteriores).</summary>
        public long Value => id.IntegerValue;
    }
}
#endif
