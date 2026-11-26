using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace RevitAutomation.Addin.Commands.SalasTecnicas;

public static class SalaSettingsStore
{
    public static string DefaultFolder => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "RevitAutomation", "SalasTecnicas");
    public static string FilePath(SalaTipo tipo, string? folder = null) => Path.Combine(folder ?? DefaultFolder, $"perfil-{tipo.ToString().ToLowerInvariant()}.json");
    private static JsonSerializerOptions Options() => new()
    {
        WriteIndented=true, PropertyNameCaseInsensitive=true,
        NumberHandling=JsonNumberHandling.AllowNamedFloatingPointLiterals,
        Converters={new JsonStringEnumConverter()}
    };
    private static double Numero(double valor,double defecto) => double.IsFinite(valor) && valor>=0 ? valor : defecto;
    private static List<string> Claves(List<string>? lista) => (lista ?? new()).Where(s=>s is not null).Select(SalaPerfiles.Normalizar).Where(s=>s.Length>0).ToList();
    private static void SanearEquipo(EquipoTipo e, EquipoTipo defecto)
    {
        e.Id ??= defecto.Id; e.Nombre ??= defecto.Nombre; e.Claves=Claves(e.Claves);
        e.AltoMm=Numero(e.AltoMm,defecto.AltoMm); e.ElevacionMm=Numero(e.ElevacionMm,defecto.ElevacionMm);
        if(!Enum.IsDefined(e.Forma)) e.Forma=defecto.Forma;
        if(!Enum.IsDefined(e.Categoria)) e.Categoria=defecto.Categoria;
        if(!Enum.IsDefined(e.Conexion)) e.Conexion=defecto.Conexion;
    }
    private static SalaPerfil Sanear(SalaPerfil p,SalaTipo tipo)
    {
        var d=SalaPerfiles.Defecto(tipo); p.Tipo=tipo; p.Nombre ??= d.Nombre;
        p.AlturaRedMm=Numero(p.AlturaRedMm,d.AlturaRedMm);
        p.LadoMinMm=Numero(p.LadoMinMm,d.LadoMinMm);p.LadoMaxMm=Numero(p.LadoMaxMm,d.LadoMaxMm);
        p.DistanciaRotuloMm=Numero(p.DistanciaRotuloMm,d.DistanciaRotuloMm);
        if(!Enum.IsDefined(p.Red)) p.Red=d.Red;
        p.CapasSala=Claves(p.CapasSala);p.CapasIgnoradas=Claves(p.CapasIgnoradas);
        p.Equipos=(p.Equipos ?? new()).Where(e=>e is not null).ToList();
        if(p.Equipos.Count==0) p.Equipos=d.Equipos;
        foreach(var e in p.Equipos) SanearEquipo(e,d.Equipos.FirstOrDefault(t=>t.Id==e.Id) ?? d.Generico ?? new EquipoTipo());
        if(p.Generico is not null) SanearEquipo(p.Generico,d.Generico ?? new EquipoTipo());
        return p;
    }
    public static (SalaPerfil Perfil,string Nota) Load(SalaTipo tipo,string? folder=null)
    {
        string archivo;
        try { archivo=FilePath(tipo,folder); }
        catch(Exception ex) {return(SalaPerfiles.Defecto(tipo),$"Se ha ignorado la ruta del perfil: {ex.Message}");}
        try
        {
            // Leer primero distingue un archivo ausente de uno inaccesible.
            string json=File.ReadAllText(archivo,Encoding.UTF8);
            var perfil=JsonSerializer.Deserialize<SalaPerfil>(json,Options()) ?? throw new JsonException("El perfil está vacío.");
            return(Sanear(perfil,tipo),"");
        }
        catch(Exception ex) when(ex is FileNotFoundException or DirectoryNotFoundException)
        {
            var perfil=SalaPerfiles.Defecto(tipo); string error=Save(perfil,folder);
            return(perfil,error.Length==0 ? $"Perfil por defecto creado en {archivo} (editable)" : $"No se pudo crear el perfil por defecto en {archivo}: {error}");
        }
        catch(Exception ex) {return(SalaPerfiles.Defecto(tipo),$"Se ha ignorado el perfil de {archivo}; se usan los valores por defecto: {ex.Message}");}
    }
    public static string Save(SalaPerfil perfil,string? folder=null)
    {
        try
        {
            string archivo=Path.GetFullPath(FilePath(perfil.Tipo,folder));
            string json=JsonSerializer.Serialize(perfil,Options());
            Directory.CreateDirectory(Path.GetDirectoryName(archivo)!);
            File.WriteAllText(archivo,json,new UTF8Encoding(false));
            return "";
        }
        catch(Exception ex) {return $"No se pudo guardar el perfil: {ex.Message}";}
    }
}
