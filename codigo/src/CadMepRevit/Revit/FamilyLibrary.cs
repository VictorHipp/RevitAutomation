using System.Text;
using Autodesk.Revit.DB;

namespace CadMepRevit.Revit;

/// <summary>
/// Biblioteca de familias (.rfa) de la que «Modelar desde CAD» carga lo que el proyecto no tiene: las piezas de
/// unión (codos, tes, cruces, transiciones) y las familias de los bloques del plano (rejillas, equipos…).
/// Las carpetas se leen de %APPDATA%\CadMepRevit\bibliotecas.txt (una por línea; se crea con las de por defecto).
/// El índice de archivos se guarda en %LOCALAPPDATA%\CadMepRevit\indice_familias.txt y se rehace cada 7 días.
/// </summary>
public sealed class FamilyLibrary
{
    /// <summary>
    /// Carpeta por defecto: la biblioteca de Revit del equipo. Las bibliotecas propias de cada oficina
    /// (por ejemplo, en una unidad de red) se añaden en bibliotecas.txt, una por línea.
    /// </summary>
    public static IEnumerable<string> DefaultRoots(string revitYear) => new[]
    {
        $@"C:\ProgramData\Autodesk\RVT {revitYear}\Libraries\Spanish"
    };

    private readonly Dictionary<string, List<string>> _byName = new(StringComparer.OrdinalIgnoreCase);
    public List<string> Roots { get; } = new();
    public List<string> Notes { get; } = new();
    public int FileCount { get; private set; }

    private static string AppDataDir => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "CadMepRevit");
    private static string CacheDir => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "CadMepRevit");

    /// <summary>Lee la configuración y el índice (lo rehace si falta, si cambian las carpetas o si tiene más de 7 días).</summary>
    public static FamilyLibrary Open(string revitYear, IEnumerable<string> extraFolders)
    {
        var lib = new FamilyLibrary();
        try
        {
            Directory.CreateDirectory(AppDataDir);
            string cfg = Path.Combine(AppDataDir, "bibliotecas.txt");
            if (!File.Exists(cfg))
                File.WriteAllLines(cfg, new[]
                {
                    "# Carpetas con familias de Revit (.rfa) que «Modelar desde CAD» puede cargar (una por línea).",
                    "# Se buscan por el nombre de la familia. Las líneas que empiezan por # no se leen.",
                    "# Añada aquí las carpetas de familias de su oficina, por ejemplo: X:\\Biblioteca Revit\\Familias",
                }.Concat(DefaultRoots("{AÑO}")), new UTF8Encoding(true));
            foreach (var line in File.ReadAllLines(cfg))
            {
                string l = line.Trim();
                if (l.Length == 0 || l.StartsWith("#")) continue;
                lib.Roots.Add(l.Replace("{AÑO}", revitYear));
            }
        }
        catch (Exception ex)
        {
            lib.Notes.Add("No se pudo leer la lista de bibliotecas (" + ex.Message + "); se usan las de por defecto.");
            lib.Roots.AddRange(DefaultRoots(revitYear));
        }
        lib.Roots.AddRange(extraFolders.Where(f => !string.IsNullOrWhiteSpace(f)));
        // Reserva: las familias de uniones que se instalan con el complemento (la última: mandan las de la oficina).
        if (BundledFolder is { } bundled) lib.Roots.Add(bundled);
        lib.LoadIndex();
        return lib;
    }

    /// <summary>Carpeta «Familias» junto al complemento (codos, tes, cruces y transiciones de reserva), o null si no está.</summary>
    public static string? BundledFolder
    {
        get
        {
            try
            {
                string? dir = Path.GetDirectoryName(typeof(FamilyLibrary).Assembly.Location);
                return dir is null ? null : Path.Combine(dir, "Familias");
            }
            catch { return null; }
        }
    }

    /// <summary>
    /// Archivos cuyo nombre de familia cumple la condición, el de la biblioteca listada primero. Sirve para encontrar piezas
    /// de unión con los nombres de otras oficinas («M_Codo rectangular - Radio 1», «Codo rect.»…).
    /// </summary>
    public IEnumerable<string> FindLike(Func<string, bool> match) =>
        _byName.Where(kv => match(kv.Key)).SelectMany(kv => kv.Value).OrderBy(RootRank).ThenBy(f => Path.GetFileName(f).Length);

    /// <summary>Carga la familia de ese archivo (o la ya cargada con su nombre). Necesita una transacción abierta.</summary>
    public Family? LoadFile(Document doc, string file, List<string> log)
    {
        string name = Path.GetFileNameWithoutExtension(file);
        var loaded = new FilteredElementCollector(doc).OfClass(typeof(Family)).Cast<Family>()
            .FirstOrDefault(f => f.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
        if (loaded != null) return loaded;
        try
        {
            if (doc.LoadFamily(file, new KeepExisting(), out Family fam) && fam != null)
            {
                log.Add($"Familia '{name}' cargada desde {file}.");
                return fam;
            }
        }
        catch (Exception ex) { log.Add($"No se pudo cargar la familia '{name}' ({file}): {ex.Message}"); }
        return null;
    }

    private void LoadIndex()
    {
        var roots = Roots.Where(Directory.Exists).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        foreach (var r in Roots.Where(r => !Directory.Exists(r)).Distinct())
            Notes.Add($"Biblioteca no disponible en este equipo: {r}");
        string key = string.Join("|", roots);
        string cache = Path.Combine(CacheDir, "indice_familias.txt");
        List<string>? files = null;
        try
        {
            if (File.Exists(cache) && DateTime.Now - File.GetLastWriteTime(cache) < TimeSpan.FromDays(7))
            {
                var lines = File.ReadAllLines(cache, Encoding.UTF8);
                if (lines.Length > 0 && lines[0] == "#" + key) files = lines.Skip(1).ToList();
            }
        }
        catch { files = null; }
        if (files is null)
        {
            files = new List<string>();
            foreach (var r in roots)
            {
                try { files.AddRange(Directory.EnumerateFiles(r, "*.rfa", new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true })); }
                catch (Exception ex) { Notes.Add($"No se pudo recorrer la biblioteca {r}: {ex.Message}"); }
            }
            try
            {
                Directory.CreateDirectory(CacheDir);
                File.WriteAllLines(cache, new[] { "#" + key }.Concat(files), new UTF8Encoding(true));
            }
            catch { /* el índice en disco es solo para ir más rápido */ }
        }
        foreach (var f in files)
        {
            string name = Path.GetFileNameWithoutExtension(f);
            // Las copias de seguridad de Revit («Familia.0001.rfa») no se usan.
            if (System.Text.RegularExpressions.Regex.IsMatch(name, @"\.\d{4}$")) continue;
            if (!_byName.TryGetValue(name, out var list)) _byName[name] = list = new List<string>();
            list.Add(f);
        }
        FileCount = files.Count;
    }

    /// <summary>Archivo de la familia con ese nombre exacto (el de la biblioteca listada primero).</summary>
    public string? Find(string familyName) =>
        _byName.TryGetValue(familyName.Trim(), out var list) ? list.OrderBy(RootRank).ThenBy(f => f.Length).First() : null;

    /// <summary>Primer nombre de la lista que esté en la biblioteca.</summary>
    public string? FindAny(IEnumerable<string> familyNames) => familyNames.Select(Find).FirstOrDefault(f => f != null);

    private int RootRank(string file)
    {
        for (int i = 0; i < Roots.Count; i++)
            if (file.StartsWith(Roots[i], StringComparison.OrdinalIgnoreCase)) return i;
        return Roots.Count;
    }

    /// <summary>
    /// Familia cargada con ese nombre o, si no, la carga desde la biblioteca. Necesita una transacción abierta.
    /// Devuelve null si no está en ningún sitio.
    /// </summary>
    public Family? Ensure(Document doc, string familyName, List<string> log)
    {
        var loaded = new FilteredElementCollector(doc).OfClass(typeof(Family)).Cast<Family>()
            .FirstOrDefault(f => f.Name.Equals(familyName, StringComparison.OrdinalIgnoreCase));
        if (loaded != null) return loaded;
        string? file = Find(familyName);
        if (file is null) return null;
        try
        {
            if (doc.LoadFamily(file, new KeepExisting(), out Family fam) && fam != null)
            {
                log.Add($"Familia '{familyName}' cargada desde {file}.");
                return fam;
            }
        }
        catch (Exception ex) { log.Add($"No se pudo cargar la familia '{familyName}' ({file}): {ex.Message}"); }
        return null;
    }

    private sealed class KeepExisting : IFamilyLoadOptions
    {
        public bool OnFamilyFound(bool familyInUse, out bool overwriteParameterValues) { overwriteParameterValues = false; return true; }
        public bool OnSharedFamilyFound(Family sharedFamily, bool familyInUse, out FamilySource source, out bool overwriteParameterValues)
        {
            source = FamilySource.Family; overwriteParameterValues = false; return true;
        }
    }
}
