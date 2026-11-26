using CadMepRevit.Core;

namespace CadMepRevit.Config;

/// <summary>
/// Regla de colocación vertical de tuberías y conductos. Módulo de CONFIGURACIÓN aislado: aquí solo hay
/// valores y textos; el cálculo está en Revit/UnderSlabPlacer.cs y la construcción en Revit/MepBuilder.cs.
/// </summary>
public static class PlacementConfig
{
    /// <summary>
    /// Regla activa (25-11-2026, decisión del usuario): los conductos, las tuberías horizontales sin cota en el plano, las
    /// bandejas y los tubos eléctricos SIGUEN EL TECHO: su parte de arriba queda a <see cref="CeilingClearanceMm"/> bajo la
    /// cara inferior del forjado de encima (proyecto y vínculos). Donde el forjado sube o baja, el tramo sube o baja con
    /// dos codos. La planta es siempre la del CAD. Lo que el plano fija (pendientes, cotas) no se cambia.
    /// Sin forjado encima, el eje queda a la «Altura sobre el nivel» (2500 mm), como antes, y el informe lo dice.
    /// La regla «bajo_forjado» anterior se conserva en el código, pero no se aplica.
    /// </summary>
    public static readonly string PLACEMENT_RULE = RuleFollowCeiling;

    /// <summary>Identificador de la regla del motor de CadMepRevit2: eje a altura fija sobre el nivel.</summary>
    public const string RuleFixedHeight = "altura_fija";

    /// <summary>Identificador de la regla «sigue el techo».</summary>
    public const string RuleFollowCeiling = "sigue_techo";

    public static bool FollowsCeiling => PLACEMENT_RULE == RuleFollowCeiling;

    /// <summary>Separación entre la parte de arriba del tramo y la cara inferior del forjado (decisión del usuario).</summary>
    public const double CeilingClearanceMm = 100;

    /// <summary>Distancia entre puntos de comprobación del forjado a lo largo de cada tramo (mm).</summary>
    public const double CeilingSampleStepMm = 200;

    /// <summary>Descripción de la regla «sigue el techo» para los informes.</summary>
    public const string FollowCeilingText =
        "la parte de arriba de conductos, tuberías horizontales sin cota en el plano, bandejas y tubos eléctricos queda a 100 mm " +
        "bajo la cara inferior del forjado de encima (del proyecto o de los modelos vinculados); donde el forjado sube o baja, " +
        "el tramo sube o baja con dos codos; la planta es la del CAD; en cada unión todos los tramos van a la cota del más bajo; " +
        "sin forjado encima, eje a la «Altura sobre el nivel» (2500 mm); lo que el plano fija (pendientes y cotas) no cambia";

    /// <summary>Identificador de la regla «bajo forjado».</summary>
    public const string RuleUnderSlab = "bajo_forjado";

    /// <summary>Descripción de la regla de altura fija para los informes.</summary>
    public const string FixedHeightText =
        "eje de tuberías, conductos, bandejas y tubos a la «Altura sobre el nivel» (2500 mm), como en CadMepRevit2";

    /// <summary>La regla se aplica SOLO a estos elementos. Bandejas, tubos eléctricos, muros y bloques siguen la regla anterior.</summary>
    public static readonly MepKind[] AppliesTo = { MepKind.Tuberia, MepKind.Conducto };

    public static bool Applies(MepKind kind) => PLACEMENT_RULE == RuleUnderSlab && Array.IndexOf(AppliesTo, kind) >= 0;

    /// <summary>Referencia: cara inferior del forjado que cubre la planta.</summary>
    public const string Reference =
        "cara inferior del forjado que cubre la planta: suelos asociados al nivel inmediatamente superior al nivel elegido";

    /// <summary>Criterio. Es una SUPOSICIÓN y así se indica en el informe.</summary>
    public const string Criterion =
        "SUPOSICIÓN: generatriz superior (parte de arriba) del tubo o conducto tangente a la cara inferior del forjado";

    /// <summary>
    /// Una red conectada (tramos unidos por codos, tes…) comparte un mismo eje para que las uniones encajen:
    /// queda tangente el elemento más alto de la red y los demás quedan colgados por debajo.
    /// </summary>
    public const string SharedAxis =
        "eje común por red conectada (para que codos y tes encajen): el elemento más alto queda tangente; los menores, por debajo";

    /// <summary>Si la cara inferior varía bajo una red, se usa la más baja (el elemento no atraviesa ningún forjado).</summary>
    public const string LowestUnderside = "si la cara inferior del forjado varía a lo largo de una red, se toma la más baja";

    /// <summary>Distancia entre puntos de comprobación a lo largo de cada tramo, para detectar huecos (mm).</summary>
    public const double SampleStepMm = 500;

    /// <summary>Diferencias de altura de la cara inferior menores que esto (mm) no se avisan.</summary>
    public const double UndersideVariationMm = 1;

    /// <summary>Qué se hace si no se encuentra forjado: no se inventa; se usa la regla anterior y se señala.</summary>
    public const string NotFound =
        "forjado no encontrado: el elemento se deja con la regla anterior (eje a la «Altura sobre el nivel», 2500 mm) y se señala en el informe";

    /// <summary>Medida usada para la generatriz superior.</summary>
    public const string OuterSize =
        "medida exterior sin aislamiento: diámetro exterior de la tubería, diámetro del conducto redondo o alto del conducto rectangular/oval";
}
