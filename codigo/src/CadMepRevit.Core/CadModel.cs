namespace CadMepRevit.Core;

/// <summary>Punto 3D en milímetros (coordenadas del dibujo ya convertidas).</summary>
public readonly record struct P3(double X, double Y, double Z)
{
    public static P3 operator +(P3 a, P3 b) => new(a.X + b.X, a.Y + b.Y, a.Z + b.Z);
    public static P3 operator -(P3 a, P3 b) => new(a.X - b.X, a.Y - b.Y, a.Z - b.Z);
    public static P3 operator *(P3 a, double k) => new(a.X * k, a.Y * k, a.Z * k);
    public double Length => Math.Sqrt(X * X + Y * Y + Z * Z);
    public double DistanceTo(P3 other) => (this - other).Length;
    public double Dot(P3 o) => X * o.X + Y * o.Y + Z * o.Z;
    public P3 Normalized() { double l = Length; return l <= 0 ? this : this * (1.0 / l); }
    public override string ToString() => $"({X:0.#}, {Y:0.#}, {Z:0.#})";
}

/// <summary>Unidades de dibujo admitidas. El valor indica cuántos milímetros mide una unidad.</summary>
public enum DrawingUnit
{
    Milimetros,
    Centimetros,
    Metros,
    Pulgadas,
    Pies
}

public static class DrawingUnits
{
    public static double ToMillimeters(DrawingUnit unit) => unit switch
    {
        DrawingUnit.Milimetros => 1.0,
        DrawingUnit.Centimetros => 10.0,
        DrawingUnit.Metros => 1000.0,
        DrawingUnit.Pulgadas => 25.4,
        DrawingUnit.Pies => 304.8,
        _ => 1.0
    };

    public static string Label(DrawingUnit unit) => unit switch
    {
        DrawingUnit.Milimetros => "Milímetros (mm)",
        DrawingUnit.Centimetros => "Centímetros (cm)",
        DrawingUnit.Metros => "Metros (m)",
        DrawingUnit.Pulgadas => "Pulgadas (in)",
        DrawingUnit.Pies => "Pies (ft)",
        _ => unit.ToString()
    };
}

/// <summary>Segmento recto leído del CAD, en unidades del dibujo.</summary>
public sealed class CadSegment
{
    public string Layer { get; init; } = "";
    public P3 Start { get; init; }
    public P3 End { get; init; }
    /// <summary>Tipo de entidad de origen (LINE, LWPOLYLINE...).</summary>
    public string Source { get; init; } = "";
    /// <summary>Ancho (mm) medido en el CAD cuando el tramo viene de un dibujo a doble línea; null si no.</summary>
    public double? WidthMm { get; init; }
    /// <summary>
    /// Número de la curva (arco o tramo curvo de polilínea) de la que esta cuerda forma parte;
    /// 0 si es un tramo recto dibujado como tal.
    /// </summary>
    public int ArcId { get; init; }
    /// <summary>
    /// Solo en cuerdas de una curva: punto donde se cortan las tangentes de los dos extremos de la curva,
    /// es decir, la esquina que habría si no estuviera redondeada. Se calcula con la geometría del CAD.
    /// Null si la curva gira demasiado para ser un codo.
    /// </summary>
    public P3? ArcCorner { get; init; }
    /// <summary>Número de la entidad del CAD de la que sale este tramo (ver <see cref="CadDrawing.Entities"/>); 0 si se ha calculado.</summary>
    public int EntityIndex { get; init; }
    public double Length => Start.DistanceTo(End);
}

/// <summary>Texto (TEXT o MTEXT) leído del CAD, en unidades del dibujo. Solo se usa para el inventario y como respaldo de las cotas.</summary>
public sealed class CadText
{
    public int EntityIndex { get; init; }
    public string Layer { get; init; } = "";
    /// <summary>TEXT o MTEXT.</summary>
    public string Source { get; init; } = "";
    /// <summary>Texto sin códigos de formato (%%C se convierte en Ø).</summary>
    public string Value { get; init; } = "";
    public P3 Position { get; init; }
    public double Height { get; init; }
    /// <summary>
    /// Centro aproximado del texto (según su justificación, giro y longitud). Null = no se calculó (se usa la posición).
    /// La posición de inserción de un TEXT es la esquina inferior izquierda: medir desde ella aleja el rótulo de su tubo.
    /// </summary>
    public P3? CenterPoint { get; init; }
    public P3 Center => CenterPoint ?? Position;
    /// <summary>Giro del texto (radianes, en planta).</summary>
    public double RotationRad { get; init; }
    /// <summary>Punto al que señala la directriz (LEADER o MULTILEADER) del rótulo, si la tiene: está sobre su tubo.</summary>
    public P3? Anchor { get; set; }
    /// <summary>Identificador interno de la entidad en el CAD (para unir una directriz con su texto).</summary>
    internal ulong Handle { get; init; }
    /// <summary>Ancho y alto aproximados del texto (unidades del dibujo), para encontrar su directriz.</summary>
    internal double WidthApprox { get; init; }
    internal double HeightApprox { get; init; }
}

/// <summary>Cota (DIMENSION) leída del CAD, en unidades del dibujo.</summary>
public sealed class CadDimension
{
    public int EntityIndex { get; init; }
    public string Layer { get; init; } = "";
    /// <summary>Clase de cota: lineal, alineada, angular, radio, diámetro, ordenada, de arco u otra.</summary>
    public string Kind { get; init; } = "";
    /// <summary>Valor medido por el CAD (unidades del dibujo). NaN si no se pudo obtener.</summary>
    public double Measurement { get; init; } = double.NaN;
    /// <summary>Texto escrito en la cota. Vacío o con «&lt;&gt;» = muestra el valor medido.</summary>
    public string Text { get; init; } = "";
    /// <summary>Puntos medidos (solo cotas lineales y alineadas).</summary>
    public P3? FirstPoint { get; init; }
    public P3? SecondPoint { get; init; }
    /// <summary>Dirección en la que se mide (unitaria; solo lineales y alineadas).</summary>
    public P3? Direction { get; init; }
    /// <summary>False si la cota no está dibujada en planta (su normal no es Z).</summary>
    public bool IsPlanar { get; init; } = true;
}

/// <summary>Entidad del CAD para el inventario: número, tipo (LINE, INSERT, TEXT…), capa y profundidad (0 = espacio modelo).</summary>
public readonly record struct CadEntityInfo(int Index, string Type, string Layer, int Depth);

/// <summary>Referencia de bloque (INSERT) leída del CAD, en unidades del dibujo.</summary>
public sealed class CadBlockRef
{
    public string Layer { get; init; } = "";
    public string BlockName { get; init; } = "";
    public P3 Position { get; init; }
    public double RotationRadians { get; init; }
    /// <summary>Rectángulo que envuelve la geometría real del bloque (null si no tiene geometría medible).</summary>
    public Footprint? Footprint { get; init; }
    /// <summary>Número de la entidad INSERT en el CAD (ver <see cref="CadDrawing.Entities"/>).</summary>
    public int EntityIndex { get; init; }
    public BlockRole Role => BlockNames.Role(BlockName, Layer);
}

public sealed class CadDrawing
{
    /// <summary>Geometría auxiliar de anotaciones; nunca entra en el inventario ni en los tramos modelados.</summary>
    public List<CadSegment> AnnotationGeometry { get; } = new();
    public List<CadArrow> Arrows { get; } = new();
    public string Path { get; init; } = "";
    /// <summary>Unidad indicada en la cabecera del archivo (INSUNITS), si se reconoce.</summary>
    public DrawingUnit? HeaderUnit { get; set; }
    public string HeaderUnitText { get; set; } = "";
    /// <summary>Explicación si las unidades de la cabecera se han corregido por absurdas (ver LayerPlanning.FixImplausibleUnits).</summary>
    public string UnitCorrection { get; set; } = "";
    public List<CadSegment> Segments { get; } = new();
    public List<CadCircle> Circles { get; } = new();
    public List<CadBlockRef> Blocks { get; } = new();
    /// <summary>Dibujo suelto en capas de luminarias (también el de dentro de bloques de planta entera). Ver LooseFixtures.</summary>
    public List<FixtureShape> FixtureShapes { get; } = new();
    /// <summary>Textos de luminarias que estaban dentro de bloques de planta entera (rótulos y tablas).</summary>
    public List<CadText> FixtureTexts { get; } = new();
    /// <summary>Luminarias dibujadas con líneas que se han añadido a <see cref="Blocks"/> como bloques equivalentes.</summary>
    public List<LooseFixture> Fixtures { get; } = new();
    /// <summary>Caché de lectura: ¿el bloque tiene dentro dibujo de luminarias?</summary>
    internal Dictionary<object, bool> FixtureBlockCache { get; } = new(ReferenceEqualityComparer.Instance);
    /// <summary>Textos (TEXT y MTEXT). No se construyen: sirven para el inventario y como respaldo de las cotas.</summary>
    public List<CadText> Texts { get; } = new();
    /// <summary>Directrices (LEADER) leídas: texto asociado (0 si ninguno), punta de flecha y final. Solo para unirlas con su rótulo.</summary>
    internal List<(ulong Annotation, P3 Arrow, P3 Tail)> Leaders { get; } = new();
    /// <summary>Cotas (DIMENSION) con su valor medido.</summary>
    public List<CadDimension> Dimensions { get; } = new();
    /// <summary>Todas las entidades leídas, en el orden del archivo (inventario del plano).</summary>
    public List<CadEntityInfo> Entities { get; } = new();
    /// <summary>Números de las referencias externas (XREF) sin contenido: no se leen.</summary>
    public HashSet<int> UnloadedXrefs { get; } = new();
    public List<string> AllLayers { get; } = new();
    public Dictionary<string, int> IgnoredEntityCounts { get; } = new(StringComparer.OrdinalIgnoreCase);
    public List<string> Warnings { get; } = new();
    /// <summary>Cuerdas rectas que sustituyen a curvas (arcos y tramos curvos de polilínea).</summary>
    public int ArcSegmentsApproximated { get; set; }
    /// <summary>Curvas leídas (arcos y tramos curvos de polilínea). Sirve para numerarlas.</summary>
    public int CurveCount { get; set; }

    public IEnumerable<string> SegmentLayers =>
        Segments.Select(s => s.Layer).Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(s => s, StringComparer.OrdinalIgnoreCase);

    public IEnumerable<(string Layer, string Block)> BlockGroups =>
        Blocks.Select(b => (b.Layer, b.BlockName)).Distinct().OrderBy(t => t.Layer).ThenBy(t => t.BlockName);
}

public sealed record CadCircle(P3 Center, double Radius, string Layer, int EntityIndex, bool IsPlanar = true);

/// <summary>Flecha en unidades CAD, desde la cola hasta la punta (aguas abajo).</summary>
public sealed record CadArrow(string Layer, P3 Tail, P3 Tip, string Source);
