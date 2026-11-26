namespace RevitAutomation.Addin.Commands.SalasTecnicas;

// Contratos del módulo «Salas técnicas». Sin dependencias de Revit: se prueban en test/SalasTecnicasHarness.
// Todas las coordenadas y medidas están en milímetros, en coordenadas del proyecto (planta XY).

/// <summary>Los cuatro botones del desplegable «Salas técnicas».</summary>
public enum SalaTipo
{
    ContadoresAgua,
    Electricidad,
    Pci,
    Fontaneria
}

/// <summary>Forma 3D de un equipo. Auto = según el contorno dibujado (círculo → cilindro, resto → caja).</summary>
public enum EquipoForma
{
    Auto,
    Caja,
    Cilindro
}

/// <summary>Categoría de Revit en la que se crea el equipo (la traduce la parte de Revit).</summary>
public enum EquipoCategoria
{
    Mecanico,
    Fontaneria,
    Electrico,
    Generico
}

/// <summary>Dónde llegan las tuberías al equipo: bajan a su cara superior, a media altura o no se conectan.</summary>
public enum EquipoConexion
{
    Ninguna,
    Arriba,
    Media
}

/// <summary>Qué red lineal modela la sala con el motor CAD (CadMep).</summary>
public enum RedSala
{
    Ninguna,
    Tuberias,
    Bandejas
}

/// <summary>Un tipo de equipo reconocible en el plano de una sala.</summary>
public sealed class EquipoTipo
{
    /// <summary>Identificador estable (sin espacios), p. ej. «deposito_afs». Va en el comentario del elemento creado.</summary>
    public string Id { get; set; } = "";
    /// <summary>Nombre para el usuario, p. ej. «Depósito AFS».</summary>
    public string Nombre { get; set; } = "";
    /// <summary>
    /// Palabras clave (en mayúsculas, sin tildes). Se buscan en los textos del plano dentro o junto al contorno, en el
    /// nombre de la capa y en el nombre del bloque. Las de 3 letras o menos solo cuentan como palabra completa.
    /// </summary>
    public List<string> Claves { get; set; } = new();
    public EquipoForma Forma { get; set; } = EquipoForma.Auto;
    /// <summary>Alto del equipo (mm). Valor supuesto salvo que el rótulo diga «H=…».</summary>
    public double AltoMm { get; set; } = 1000;
    /// <summary>Altura de su base sobre el nivel (mm).</summary>
    public double ElevacionMm { get; set; }
    public EquipoCategoria Categoria { get; set; } = EquipoCategoria.Mecanico;
    public EquipoConexion Conexion { get; set; } = EquipoConexion.Ninguna;
}

/// <summary>Configuración de un botón (una sala). Se guarda en JSON y el usuario puede editarla.</summary>
public sealed class SalaPerfil
{
    public SalaTipo Tipo { get; set; }
    public string Nombre { get; set; } = "";
    public RedSala Red { get; set; } = RedSala.Tuberias;
    /// <summary>Altura del eje de las tuberías / bandejas sobre el nivel (mm).</summary>
    public double AlturaRedMm { get; set; } = 2500;
    /// <summary>En orden de prioridad: gana el primero cuyas claves aparecen (los más específicos van antes).</summary>
    public List<EquipoTipo> Equipos { get; set; } = new();
    /// <summary>
    /// Equipo para contornos cerrados sin rótulo reconocible que están en una capa de la sala (ver <see cref="CapasSala"/>).
    /// Null = esos contornos se descartan.
    /// </summary>
    public EquipoTipo? Generico { get; set; }
    /// <summary>Fragmentos de nombre de capa propios de esta instalación (mayúsculas, sin tildes).</summary>
    public List<string> CapasSala { get; set; } = new();
    /// <summary>Fragmentos de nombre de capa que nunca son equipos (muros, cotas, textos, mobiliario…).</summary>
    public List<string> CapasIgnoradas { get; set; } = new();
    /// <summary>Lado menor mínimo del contorno de un equipo (mm). Lo más pequeño es un símbolo, no un equipo.</summary>
    public double LadoMinMm { get; set; } = 200;
    /// <summary>Lado mayor máximo del contorno de un equipo (mm). Lo más grande es la sala o un local.</summary>
    public double LadoMaxMm { get; set; } = 12000;
    /// <summary>Distancia máxima (mm) de un texto fuera del contorno para considerarlo su rótulo.</summary>
    public double DistanciaRotuloMm { get; set; } = 600;
}

public readonly record struct Pt(double X, double Y);

/// <summary>Contorno cerrado leído del CAD (polilínea cerrada, círculo o líneas que cierran). Puntos sin repetir el primero.</summary>
public sealed class SalaContorno
{
    public int Id { get; init; }
    public string Capa { get; init; } = "";
    public List<Pt> Puntos { get; init; } = new();
}

public sealed class SalaTexto
{
    public Pt Pos { get; init; }
    public string Valor { get; init; } = "";
    public string Capa { get; init; } = "";
}

/// <summary>Bloque del CAD con su huella (rectángulo girado) medida.</summary>
public sealed class SalaBloque
{
    public string Nombre { get; init; } = "";
    public string Capa { get; init; } = "";
    public Pt Centro { get; init; }
    public double AnchoMm { get; init; }
    public double FondoMm { get; init; }
    public double AnguloRad { get; init; }
}

public sealed class SalaEntrada
{
    public List<SalaContorno> Contornos { get; } = new();
    public List<SalaTexto> Textos { get; } = new();
    public List<SalaBloque> Bloques { get; } = new();
}

/// <summary>Un equipo que se va a modelar.</summary>
public sealed class EquipoPlan
{
    public EquipoTipo Tipo { get; init; } = new();
    /// <summary>Rótulo leído del plano (o el nombre del tipo si no hay).</summary>
    public string Etiqueta { get; init; } = "";
    /// <summary>Caja o Cilindro (nunca Auto).</summary>
    public EquipoForma Forma { get; init; }
    /// <summary>Caja: polígono de la planta (antihorario). Cilindro: vacío.</summary>
    public List<Pt> Contorno { get; init; } = new();
    public Pt Centro { get; init; }
    /// <summary>Solo cilindros.</summary>
    public double RadioMm { get; init; }
    public double AltoMm { get; init; }
    public double ElevacionMm { get; init; }
    public bool AltoSupuesto { get; init; } = true;
    /// <summary>De dónde sale, para el informe: «contorno en capa X», «bloque Y».</summary>
    public string Origen { get; init; } = "";
}

public sealed class SalaPlan
{
    public SalaPerfil Perfil { get; init; } = new();
    public List<EquipoPlan> Equipos { get; } = new();
    /// <summary>Lo leído que no se modela y por qué.</summary>
    public List<(string Que, string Motivo)> Descartes { get; } = new();
    public List<string> Avisos { get; } = new();
    /// <summary>Contorno identificado como el perímetro de la sala (no se modela), si lo hay.</summary>
    public List<Pt>? Sala { get; set; }
}
