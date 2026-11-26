using System.Text;
using System.Text.Json;

namespace CadMepRevit.Core;

/// <summary>
/// Reglas de reconocimiento de la oficina: lo que el usuario ha dicho que es cada capa y cada bloque que el programa no
/// reconocía por su nombre. Mandan sobre las palabras clave, así el programa se adapta a cualquier forma de nombrar.
/// Se guardan en %APPDATA%\CadMepRevit\reglas_reconocimiento.json (se puede editar o borrar).
/// </summary>
public sealed class UserRules
{
    public Dictionary<string, MepKind> Layers { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    /// <summary>Bloque (nombre limpio, sin el sufijo de Revit) → función.</summary>
    public Dictionary<string, BlockRole> Blocks { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    /// <summary>Capas en las que las luminarias están dibujadas con líneas (sin bloque).</summary>
    public List<string> FixtureLayers { get; set; } = new();

    /// <summary>Reglas en uso. Vacías hasta que se cargan (las pruebas no las cargan).</summary>
    public static UserRules Current { get; set; } = new();

    public static string DefaultPath =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "CadMepRevit", "reglas_reconocimiento.json");

    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true, Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter() } };

    /// <summary>Carga las reglas (vacías si no hay archivo o no se puede leer; nunca lanza).</summary>
    public static UserRules Load(string? path = null)
    {
        try
        {
            path ??= DefaultPath;
            if (!File.Exists(path)) return new UserRules();
            var r = JsonSerializer.Deserialize<UserRules>(File.ReadAllText(path, Encoding.UTF8), Json) ?? new UserRules();
            // El diccionario leído no distingue mayúsculas: se copia a uno que no las distinga.
            return new UserRules
            {
                Layers = new Dictionary<string, MepKind>(r.Layers, StringComparer.OrdinalIgnoreCase),
                Blocks = new Dictionary<string, BlockRole>(r.Blocks, StringComparer.OrdinalIgnoreCase),
                FixtureLayers = (r.FixtureLayers ?? new()).Distinct(StringComparer.OrdinalIgnoreCase).ToList()
            };
        }
        catch { return new UserRules(); }
    }

    /// <summary>Guarda las reglas. Devuelve "" si todo fue bien o el motivo del fallo.</summary>
    public string Save(string? path = null)
    {
        try
        {
            path ??= DefaultPath;
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, JsonSerializer.Serialize(this, Json), new UTF8Encoding(true));
            return "";
        }
        catch (Exception ex) { return ex.Message; }
    }

    public MepKind? LayerRule(string layer) => Layers.TryGetValue(layer ?? "", out var k) ? k : null;
    public BlockRole? BlockRule(string blockName) => Blocks.TryGetValue(BlockNames.Clean(blockName ?? ""), out var r) ? r : null;
}

/// <summary>Lo que el programa no sabe qué es en un plano: lo que se pregunta al usuario (una vez por oficina).</summary>
public static class Unrecognized
{
    /// <summary>
    /// Capas con líneas cuyo nombre no dice nada al programa (ni instalación, ni anotación, ni arquitectura conocida) y
    /// sin regla de la oficina. Con al menos 4 líneas (las capas de 1–3 líneas suelen ser restos).
    /// </summary>
    public static List<(string Layer, int Lines)> Layers(CadDrawing d) =>
        d.Segments.GroupBy(s => s.Layer, StringComparer.OrdinalIgnoreCase)
            .Where(g => g.Count() >= 4 && MepKinds.IsUnknownLayer(g.Key))
            .Select(g => (g.Key, g.Count())).OrderByDescending(x => x.Item2).ToList();

    /// <summary>
    /// Bloques sin función reconocida y sin regla: no son leyendas, referencias externas, anotaciones, flechas, piezas
    /// de unión ni bloques anónimos, y miden menos que un dibujo entero.
    /// </summary>
    public static List<(string Block, string Layer, int Count, double SizeMm)> Blocks(CadDrawing d, double unitToMm) =>
        d.Blocks.Where(b => b.Role == BlockRole.Otro && UserRules.Current.BlockRule(b.BlockName) is null &&
                            !BlockNames.IsLegendOrReference(b.BlockName) && !BlockNames.IsAnnotationLayer(b.Layer) &&
                            !BlockNames.IsArrowSymbol(b.BlockName) && !BlockNames.IsAnonymous(b.BlockName) &&
                            b.Footprint is { } f && Math.Max(f.SizeX, f.SizeY) * unitToMm is > 20 and <= PlanDefaults.MaxApplianceFootprintMm)
            .GroupBy(b => BlockNames.Clean(b.BlockName), StringComparer.OrdinalIgnoreCase)
            .Select(g => (g.Key, g.First().Layer, g.Count(), g.Max(b => Math.Max(b.Footprint!.SizeX, b.Footprint.SizeY)) * unitToMm))
            .OrderByDescending(x => x.Item3).ToList();
}
