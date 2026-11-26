using System.Globalization;
using System.Text.RegularExpressions;

namespace RevitAutomation.Addin.Commands.SalasTecnicas;

public static class SalaClasificador
{
    internal static bool Coincide(string texto, string clave)
    {
        texto=SalaPerfiles.Normalizar(texto); clave=SalaPerfiles.Normalizar(clave);
        if(clave.Length==0) return false;
        return clave.Count(c=>c!=' ')<=3 ? (" "+texto+" ").Contains(" "+clave+" ",StringComparison.Ordinal)
            : texto.Contains(clave,StringComparison.Ordinal);
    }
    internal static string LimpiarTexto(string texto)
    {
        texto=Regex.Replace(texto ?? "", @"\\P", " ", RegexOptions.IgnoreCase);
        texto=Regex.Replace(texto, @"\\[a-zA-Z][^;{}]*;", "");
        texto=texto.Replace("{", "").Replace("}", "");
        return Regex.Replace(texto,@"\s+"," ").Trim();
    }
    private static string BuscarRotulo(IReadOnlyList<Pt> pol, SalaEntrada entrada,
        Func<IReadOnlyList<SalaTexto>> exteriores, double distancia)
    {
        var interiores=entrada.Textos.Where(t=>SalaGeometria.Dentro(t.Pos,pol)).ToList();
        // Un texto vacío también ocupa su posición, sin tomar otro rótulo exterior.
        if(interiores.Count>0) return LimpiarTexto(string.Join(" ",interiores.Select(t=>t.Valor)));
        // With no text in this outline, excluding 'actual' from the occupied-text test
        // cannot affect the result. Occupancy of the remaining candidates is fixed for the run.
        var cercano=exteriores()
            .Select(t=>(Texto:t,Distancia:SalaGeometria.DistanciaBorde(t.Pos,pol)))
            .Where(t=>t.Distancia<=distancia).OrderBy(t=>t.Distancia).FirstOrDefault();
        return cercano.Texto is null ? "" : LimpiarTexto(cercano.Texto.Valor);
    }
    private static EquipoTipo? Tipo(SalaPerfil perfil,string texto) => perfil.Equipos.FirstOrDefault(t=>t.Claves.Any(c=>Coincide(texto,c)));
    private static bool Capa(string capa,IEnumerable<string> claves) => claves.Any(c=>Coincide(capa,c));
    private static (double Alto, bool Supuesto) Altura(string texto, EquipoTipo tipo)
    {
        var m=Regex.Match(texto,@"(?<![\p{L}\p{N}])(?:H\s*=\s*|ALTURA\s*=?\s*|ALTO\s*=?\s*)(\d+(?:[.,]\d+)?)\s*(MM|M)?(?![\p{L}\p{N}])",RegexOptions.IgnoreCase|RegexOptions.CultureInvariant);
        if(m.Success && double.TryParse(m.Groups[1].Value.Replace(',','.'),NumberStyles.AllowDecimalPoint,CultureInfo.InvariantCulture,out double h))
        {
            string unidad=m.Groups[2].Value.ToUpperInvariant();
            if(unidad=="M" || h<10) h*=1000;
            if(double.IsFinite(h) && h>0) return(h,false);
        }
        return(tipo.AltoMm,true);
    }
    private static EquipoPlan Crear(EquipoTipo tipo, string etiqueta, string origen, List<Pt> pol, bool bloque)
    {
        var centro=SalaGeometria.Centroide(pol); var rect=SalaGeometria.Rectangulo(pol);
        double radio=0; bool cilindro=!bloque && SalaGeometria.Circulo(pol,centro,out radio);
        if(!cilindro && tipo.Forma==EquipoForma.Cilindro && (bloque || rect.Mayor/rect.Menor<=1.15))
        { cilindro=true; radio=rect.Menor/2; centro=rect.Centro; }
        var alto=Altura(etiqueta,tipo);
        return new() { Tipo=tipo,Etiqueta=etiqueta.Length>0?etiqueta:tipo.Nombre,Origen=origen,
            Forma=cilindro?EquipoForma.Cilindro:EquipoForma.Caja,Contorno=cilindro?new():SalaGeometria.Limpiar(pol),
            Centro=centro,RadioMm=cilindro?radio:0,AltoMm=alto.Alto,AltoSupuesto=alto.Supuesto,ElevacionMm=tipo.ElevacionMm };
    }
    public static SalaPlan Clasificar(SalaPerfil perfil, SalaEntrada entrada)
    {
        var plan=new SalaPlan { Perfil=perfil }; var candidatos=new List<SalaContorno>();
        string Que(SalaContorno c)=>$"contorno {c.Id} en capa '{c.Capa}'";
        foreach(var c in entrada.Contornos)
        {
            if(c.Puntos.Count<3 || c.Puntos.Any(p=>!double.IsFinite(p.X)||!double.IsFinite(p.Y)) || Math.Abs(SalaGeometria.Area(c.Puntos))<=SalaGeometria.Epsilon)
            { plan.Descartes.Add((Que(c),"contorno degenerado")); continue; }
            if(Capa(c.Capa,perfil.CapasIgnoradas)) {plan.Descartes.Add((Que(c),"capa ignorada"));continue;}
            var r=SalaGeometria.Rectangulo(c.Puntos);
            if(r.Menor<perfil.LadoMinMm) {plan.Descartes.Add((Que(c),"demasiado pequeño (símbolo)"));continue;}
            if(r.Mayor>perfil.LadoMaxMm) {plan.Descartes.Add((Que(c),"demasiado grande"));continue;}
            candidatos.Add(c);
        }
        candidatos=candidatos.OrderByDescending(c=>Math.Abs(SalaGeometria.Area(c.Puntos))).ToList();
        // Perímetro de la sala: contiene al menos 2 contornos y la mitad de los demás, y mide 4 m² o más (una bancada con dos
        // bombas dentro no es la sala).
        int Contiene(SalaContorno c)=>candidatos.Count(o=>!ReferenceEquals(c,o) && SalaGeometria.Dentro(SalaGeometria.Centroide(o.Puntos),c.Puntos));
        var sala=candidatos.FirstOrDefault(c=>Math.Abs(SalaGeometria.Area(c.Puntos))>=4e6 && Contiene(c) is int n && n>=2 && 2*n>=candidatos.Count-1);
        if(sala is not null) {plan.Sala=SalaGeometria.Limpiar(sala.Puntos);plan.Descartes.Add((Que(sala),"perímetro de la sala"));candidatos.Remove(sala);}
        var textosExteriores=new Lazy<IReadOnlyList<SalaTexto>>(()=>entrada.Textos
            .Where(t=>!candidatos.Any(c=>SalaGeometria.Dentro(t.Pos,c.Puntos))).ToList());
        foreach(var c in candidatos)
        {
            var dentro=plan.Equipos.FirstOrDefault(e=>SalaGeometria.Dentro(SalaGeometria.Centroide(c.Puntos),e));
            if(dentro is not null) {plan.Descartes.Add((Que(c),$"dibujo interior de {dentro.Etiqueta}"));continue;}
            string etiqueta=BuscarRotulo(c.Puntos,entrada,()=>textosExteriores.Value,perfil.DistanciaRotuloMm);
            var tipo=Tipo(perfil,etiqueta+" "+c.Capa);
            if(tipo is null && Capa(c.Capa,perfil.CapasSala)) tipo=perfil.Generico;
            if(tipo is null) {plan.Descartes.Add((Que(c),$"sin rótulo reconocible (capa {c.Capa})"));continue;}
            plan.Equipos.Add(Crear(tipo,etiqueta,$"contorno en capa '{c.Capa}'",c.Puntos,false));
        }
        var desconocidos=new Dictionary<string,int>(StringComparer.Ordinal);
        foreach(var b in entrada.Bloques)
        {
            var tipo=Tipo(perfil,b.Nombre+" "+b.Capa);
            if(tipo is null) {desconocidos[b.Nombre]=desconocidos.GetValueOrDefault(b.Nombre)+1;continue;}
            string origen=$"bloque '{b.Nombre}'";
            if(plan.Equipos.Any(e=>SalaGeometria.Dentro(b.Centro,e))) {plan.Descartes.Add((origen,"ya dibujado como contorno"));continue;}
            if(!double.IsFinite(b.AnchoMm)||!double.IsFinite(b.FondoMm)||!double.IsFinite(b.AnguloRad)||!double.IsFinite(b.Centro.X)||!double.IsFinite(b.Centro.Y))
            {plan.Descartes.Add((origen,"huella no válida"));continue;}
            if(Math.Min(b.AnchoMm,b.FondoMm)<perfil.LadoMinMm) {plan.Descartes.Add((origen,"símbolo"));continue;}
            var pol=SalaGeometria.Huella(b);
            string nombre=Regex.Replace(b.Nombre,@"^(?:(?:\*U\d+|A\$C[0-9A-F]+)[\s_$-]*)+","",RegexOptions.IgnoreCase|RegexOptions.CultureInvariant);
            string etiqueta=LimpiarTexto(nombre+" "+BuscarRotulo(pol,entrada,()=>textosExteriores.Value,perfil.DistanciaRotuloMm));
            plan.Equipos.Add(Crear(tipo,etiqueta,origen,pol,true));
        }
        foreach(var par in desconocidos) plan.Descartes.Add(($"bloque '{par.Key}' ×{par.Value}","no es un equipo de esta sala"));
        if(plan.Equipos.Count==0) plan.Avisos.Add("No se ha reconocido ningún equipo. Se necesitan contornos cerrados con un texto dentro o junto a ellos (por ejemplo, «DEPÓSITO AFS»), o en una capa de la instalación. Algunas palabras clave: "+string.Join(", ",perfil.Equipos.SelectMany(t=>t.Claves.Take(2)).Take(8))+".");
        int supuestos=plan.Equipos.Count(e=>e.AltoSupuesto);
        if(supuestos>0) plan.Avisos.Add($"{supuestos} equipos con alto supuesto (el plano en planta no lo indica): revíselos.");
        return plan;
    }
}
