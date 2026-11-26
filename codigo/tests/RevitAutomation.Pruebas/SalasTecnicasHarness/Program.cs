using System.Globalization;
using System.Text.Json.Nodes;
using RevitAutomation.Addin.Commands.SalasTecnicas;

internal static class Program
{
    private static int fallos;
    private static void Check(bool ok,string mensaje) {if(!ok) throw new InvalidOperationException(mensaje);}
    private static void Near(double a,double b,string mensaje) => Check(Math.Abs(a-b)<1e-5,$"{mensaje}: {a} ≠ {b}");
    private static void Test(string nombre,Action prueba)
    {
        try {prueba();Console.WriteLine("PASS "+nombre);}
        catch(Exception ex) {fallos++;Console.WriteLine($"FAIL {nombre}: {ex.Message}");}
    }
    private static List<Pt> Rect(double x,double y,double a,double b,double giro=0) =>
        SalaGeometria.Huella(new SalaBloque {Centro=new(x+a/2,y+b/2),AnchoMm=a,FondoMm=b,AnguloRad=giro});
    private static void Contorno(SalaEntrada e,int id,List<Pt> p,string capa="0") => e.Contornos.Add(new() {Id=id,Puntos=p,Capa=capa});
    private static void Texto(SalaEntrada e,double x,double y,string texto) => e.Textos.Add(new() {Pos=new(x,y),Valor=texto});
    private static SalaPlan Uno(string texto,SalaTipo tipo=SalaTipo.Fontaneria,string capa="0",List<Pt>? pol=null)
    {
        var e=new SalaEntrada();Contorno(e,1,pol ?? Rect(0,0,2400,600),capa);Texto(e,100,100,texto);
        return SalaClasificador.Clasificar(SalaPerfiles.Defecto(tipo),e);
    }
    private static SalaPlan Fontaneria()
    {
        var e=new SalaEntrada();
        Contorno(e,1,Rect(0,0,10000,6000));
        Contorno(e,2,Rect(500,500,3000,1500));Texto(e,1500,1000,"DEPÓSITO AFS");
        Contorno(e,3,Rect(5000,500,3000,1500));Texto(e,6000,1000,"DEPÓSITO AFS");
        Contorno(e,4,Enumerable.Range(0,24).Select(i=>new Pt(1500+500*Math.Cos(i*Math.PI/12),4000+500*Math.Sin(i*Math.PI/12))).ToList());
        Texto(e,2300,4000,"ACUMULADOR ACS");
        Contorno(e,5,Rect(5000,3500,1200,800),"FON-EQUIPOS");
        Contorno(e,6,Rect(7500,3500,1600,1000));Texto(e,8000,3900,"BOMBA");
        Contorno(e,7,Rect(7600,3600,1400,800));
        Contorno(e,8,Rect(500,5500,50,50));
        Contorno(e,9,Rect(3000,5200,1000,500),"A-WALL");
        return SalaClasificador.Clasificar(SalaPerfiles.Defecto(SalaTipo.Fontaneria),e);
    }
    public static int Main()
    {
        Test("Rótulos exteriores compartidos conservan vacíos y ocupación",()=>
        {
            var tipo=new EquipoTipo {Id="equipo",Nombre="Equipo",Claves=new(){"EQUIPO"}};
            var perfil=new SalaPerfil {Equipos=new(){tipo},Generico=tipo,CapasSala=new(){"EQUIPOS"}};
            var entrada=new SalaEntrada();
            Contorno(entrada,1,Rect(0,0,1000,1000),"EQUIPOS");
            Contorno(entrada,2,Rect(1200,0,1000,1000),"EQUIPOS");
            Contorno(entrada,3,Rect(2300,0,1000,1000),"EQUIPOS");
            Texto(entrada,500,500,"");
            Texto(entrada,500,-100,"EQUIPO NO PRESTADO");
            Texto(entrada,2190,500,"EQUIPO INTERIOR");
            Texto(entrada,2800,-400,"EQUIPO EXTERIOR");
            var plan=SalaClasificador.Clasificar(perfil,entrada);
            Check(plan.Equipos.Select(e=>e.Etiqueta).SequenceEqual(new[]{"Equipo","EQUIPO INTERIOR","EQUIPO EXTERIOR"}),
                "Un vacío ocupa el contorno y un texto de otro contorno no se toma como exterior");
        });
        Test("Bancada con dos bombas no es la sala",()=>
        {
            var e=new SalaEntrada();
            Contorno(e,1,Rect(0,0,3000,1500));Texto(e,200,1300,"GRUPO DE PRESIÓN");
            Contorno(e,2,Rect(300,300,800,600));Contorno(e,3,Rect(1800,300,800,600));
            Contorno(e,4,Rect(5000,0,3000,1500));Texto(e,6000,700,"DEPÓSITO AFS");
            Contorno(e,5,Rect(5000,3000,3000,1500));Texto(e,6000,3700,"DEPÓSITO AFS");
            Contorno(e,6,Rect(9000,3000,3000,1500));Texto(e,10000,3700,"DEPÓSITO AFS");
            var p=SalaClasificador.Clasificar(SalaPerfiles.Defecto(SalaTipo.Fontaneria),e);
            Check(p.Sala is null,"sin sala");
            Check(p.Equipos.Count(x=>x.Tipo.Id=="grupo_presion")==1,"un grupo");
            Check(p.Equipos.Count(x=>x.Tipo.Id=="deposito_afs")==3,"tres depósitos");
            Check(p.Descartes.Count(d=>d.Motivo.StartsWith("dibujo interior"))==2,"bombas como dibujo interior");
        });
        Test("Normalización y coincidencias",()=>
        {
            Check(SalaPerfiles.Normalizar("Depósito_A.C.S.")=="DEPOSITO A C S","Normalizar puntuación");
            Check(SalaPerfiles.Normalizar("  cañón-/\\ á\tβ 12  ")=="CANON A Β 12","Unicode y espacios");
            Check(SalaClasificador.Coincide("GRUPO GP 2","GP"),"GP completo");
            Check(!SalaClasificador.Coincide("GPS AGP","GP"),"GP no parcial");
            Check(SalaClasificador.Coincide("DEPOSITOS AFS","DEPOSITO"),"Clave larga parcial");
            Check(SalaClasificador.Coincide("M-EQPM","M EQPM"),"Secuencia de palabras");
            Check(!SalaClasificador.Coincide("XAB CDY","AB C"),"Secuencia corta delimitada");
            Check(!SalaClasificador.Coincide("ABC",""),"Clave vacía");
        });
        Test("Perfiles completos e independientes",()=>
        {
            int[] cantidades={5,9,4,6};
            foreach(var tipo in Enum.GetValues<SalaTipo>())
            {
                var p=SalaPerfiles.Defecto(tipo);Check(p.Equipos.Count==cantidades[(int)tipo],"Tipos del perfil");
                Check(p.Generico is not null && p.CapasIgnoradas.Contains("A WALL"),"Genérico y exclusiones");
                Check(p.Equipos.SelectMany(t=>t.Claves).All(k=>k==SalaPerfiles.Normalizar(k)),"Claves normalizadas");
                p.Equipos.Clear();Check(SalaPerfiles.Defecto(tipo).Equipos.Count>0,"Sin estado compartido");
            }
        });
        Test("Escena de fontanería",()=>
        {
            var p=Fontaneria();Check(p.Sala is not null,"Perímetro detectado");Check(p.Equipos.Count==5,"Cinco equipos");
            Check(p.Equipos.Count(e=>e.Tipo.Id=="deposito_afs")==2,"Dos depósitos");
            var acs=p.Equipos.Single(e=>e.Tipo.Id=="acumulador_acs");Check(acs.Forma==EquipoForma.Cilindro,"Círculo ACS");Near(acs.RadioMm,500,"Radio");
            Check(p.Equipos.Count(e=>e.Tipo.Id=="generico")==1,"Equipo genérico sin rótulo");
            Check(p.Equipos.Single(e=>e.Tipo.Id=="grupo_presion").Forma==EquipoForma.Caja,"Bomba caja");
            foreach(string motivo in new[]{"perímetro de la sala","dibujo interior de","demasiado pequeño","capa ignorada"})
                Check(p.Descartes.Any(d=>d.Motivo.Contains(motivo)),motivo);
            Check(p.Avisos.Single().StartsWith("5 equipos"),"Altos supuestos agrupados");
            Check(p.Equipos.Select(e=>e.Tipo.Id).SequenceEqual(new[]{"deposito_afs","deposito_afs","grupo_presion","generico","acumulador_acs"}),"Orden estable por área");
        });
        Test("Alturas y unidades",()=>
        {
            foreach(var caso in new[]{("H=2,5 m",2500d),("h=1800",1800d),("ALTURA 1,9 M",1900d),("H = 2.00m",2000d),("ALTO 1800",1800d),("H=2 mm",2000d),("H=2",2000d)})
            {
                var e=Uno("DEPOSITO AFS "+caso.Item1).Equipos.Single();Near(e.AltoMm,caso.Item2,caso.Item1);Check(!e.AltoSupuesto,"Altura explícita");
            }
            Check(Uno("DEPÓSITO AFS").Equipos.Single().AltoSupuesto,"Altura por defecto");
        });
        Test("Bloques PCI y huellas giradas",()=>
        {
            var e=new SalaEntrada();
            e.Bloques.Add(new() {Nombre="BOMBA JOCKEY",Centro=new(0,0),AnchoMm=1500,FondoMm=700,AnguloRad=0.43});
            e.Bloques.Add(new() {Nombre="VASO",Centro=new(3000,0),AnchoMm=600,FondoMm=600});
            var p=SalaClasificador.Clasificar(SalaPerfiles.Defecto(SalaTipo.Pci),e);
            Check(p.Equipos.Count==2 && p.Equipos[0].Tipo.Id=="grupo_pci" && p.Equipos[0].Forma==EquipoForma.Caja,"Bomba PCI");
            Near(SalaGeometria.Rectangulo(p.Equipos[0].Contorno).Menor,700,"Fondo girado");
            Check(p.Equipos[1].Forma==EquipoForma.Cilindro,"Vaso cilindro");Near(p.Equipos[1].RadioMm,300,"Radio vaso");
        });
        Test("Electricidad y claves cortas",()=>
        {
            Check(Uno("CGBT",SalaTipo.Electricidad).Equipos.Single().Tipo.Id=="cgbt","CGBT");
            Check(Uno("GE",SalaTipo.Electricidad).Equipos.Single().Tipo.Id=="grupo_electrogeno","GE");
            Check(Uno("GENERICO",SalaTipo.Electricidad).Equipos.Count==0,"GE no parcial");
            Check(Uno("",SalaTipo.Electricidad,"E-SAI").Equipos.Single().Tipo.Id=="sai","SAI en capa");
        });
        Test("Contadores y bloques desconocidos",()=>
        {
            Check(Uno("BATERÍA DE CONTADORES",SalaTipo.ContadoresAgua).Equipos.Single().Tipo.Id=="bateria","Prioridad batería");
            var e=new SalaEntrada();for(int i=0;i<3;i++) e.Bloques.Add(new() {Nombre="DESCONOCIDO",AnchoMm=500,FondoMm=500});
            var p=SalaClasificador.Clasificar(SalaPerfiles.Defecto(SalaTipo.ContadoresAgua),e);
            Check(p.Descartes.Count==1 && p.Descartes[0].Que=="bloque 'DESCONOCIDO' ×3","Desconocidos agrupados");
            Check(p.Avisos.Single().Contains("contornos cerrados"),"Aviso de entrada vacía");
        });
        Test("Limpieza MTEXT y nombre anónimo",()=>
        {
            Check(Uno(@"{\fArial|b1;DEPÓSITO\PAFS}").Equipos.Single().Etiqueta=="DEPÓSITO AFS","MTEXT");
            var e=new SalaEntrada();e.Bloques.Add(new() {Nombre="*U12 BOMBA",AnchoMm=1500,FondoMm=700});
            var p=SalaClasificador.Clasificar(SalaPerfiles.Defecto(SalaTipo.Pci),e);
            Check(p.Equipos.Single().Etiqueta=="BOMBA","Prefijo anónimo");
        });
        Test("Rectángulos orientados y limpieza del polígono",()=>
        {
            var pol=Rect(1000000,2000000,3000,250,0.71);var r=SalaGeometria.Rectangulo(pol);
            Near(r.Menor,250,"Menor orientado");Near(r.Mayor,3000,"Mayor orientado");
            pol.Reverse();pol.Insert(1,new((pol[0].X+pol[1].X)/2,(pol[0].Y+pol[1].Y)/2));pol.Add(pol[^1]);pol.Add(pol[0]);
            var limpio=SalaGeometria.Limpiar(pol);Check(limpio.Count==4 && SalaGeometria.Area(limpio)>0,"Sin duplicados ni colineales; antihorario");
            Near(SalaGeometria.Centroide(limpio).X,r.Centro.X,"Centroide estable");
            var e=new SalaEntrada();Contorno(e,1,pol,"FON");Check(SalaClasificador.Clasificar(SalaPerfiles.Defecto(SalaTipo.Fontaneria),e).Equipos.Count==1,"No descartar por caja global");
            Check(SalaGeometria.Dentro(limpio[0],limpio),"Borde incluido");
            var concavo=new List<Pt>{new(0,0),new(1000,0),new(1000,300),new(300,300),new(300,1000),new(0,1000)};
            Near(SalaGeometria.Area(concavo),510000,"Área cóncava");Check(!SalaGeometria.Dentro(new(700,700),concavo),"Hueco cóncavo");
        });
        Test("Forma cilíndrica y tolerancia",()=>
        {
            var cuadrado=Uno("VASO EXPANSION",pol:Rect(0,0,600,600)).Equipos.Single();
            Check(cuadrado.Forma==EquipoForma.Cilindro,"Cilindro inscrito");Near(cuadrado.RadioMm,300,"Radio inscrito");
            Check(Uno("VASO EXPANSION").Equipos.Single().Forma==EquipoForma.Caja,"Rectángulo alargado conserva caja");
            var elipse=Enumerable.Range(0,24).Select(i=>new Pt(600*Math.Cos(i*Math.PI/12),400*Math.Sin(i*Math.PI/12))).ToList();
            Check(!SalaGeometria.Circulo(elipse,new(0,0),out _),"Elipse no circular");
        });
        Test("Distancia del rótulo y candidatos ajenos",()=>
        {
            var e=new SalaEntrada();Contorno(e,1,Rect(0,0,1000,500));Texto(e,1300,250,"DEPÓSITO AFS");
            Check(SalaClasificador.Clasificar(SalaPerfiles.Defecto(SalaTipo.Fontaneria),e).Equipos.Count==1,"Texto a 300 mm");
            Contorno(e,2,Rect(1200,0,1000,500));
            var p=SalaClasificador.Clasificar(SalaPerfiles.Defecto(SalaTipo.Fontaneria),e);
            Check(p.Equipos.Count==1 && p.Equipos[0].Centro.X>1200,"No robar texto de otro candidato");
            var lejano=new SalaEntrada();Contorno(lejano,1,Rect(0,0,1000,500));Texto(lejano,1601,250,"BOMBA");
            Check(SalaClasificador.Clasificar(SalaPerfiles.Defecto(SalaTipo.Fontaneria),lejano).Equipos.Count==0,"Texto fuera de alcance");
        });
        Test("Descartes y bloques duplicados",()=>
        {
            var e=new SalaEntrada();Contorno(e,1,Rect(0,0,1500,700),"BOMBA");
            Contorno(e,2,new(){new(0,0),new(1,1)});Contorno(e,3,Rect(3000,0,13000,500));
            e.Bloques.Add(new(){Nombre="BOMBA",Centro=new(500,300),AnchoMm=1500,FondoMm=700});
            e.Bloques.Add(new(){Nombre="BOMBA",Centro=new(20000,0),AnchoMm=50,FondoMm=50});
            var p=SalaClasificador.Clasificar(SalaPerfiles.Defecto(SalaTipo.Fontaneria),e);
            Check(p.Equipos.Count==1,"Sin duplicado");
            foreach(string motivo in new[]{"contorno degenerado","demasiado grande","ya dibujado como contorno","símbolo"}) Check(p.Descartes.Any(d=>d.Motivo==motivo),motivo);
            Check(Uno("BOMBA",capa:"A-WALL").Equipos.Count==0,"Ignorar antes de clasificar");
        });
        Test("Persistencia y recuperación JSON",()=>
        {
            string folder=Path.Combine(Path.GetTempPath(),"SalasHarness_"+Guid.NewGuid().ToString("N"));
            try
            {
                var (p,nota)=SalaSettingsStore.Load(SalaTipo.Fontaneria,folder);string archivo=SalaSettingsStore.FilePath(p.Tipo,folder);
                Check(File.Exists(archivo) && nota.Contains("Perfil por defecto creado"),"Crear valores por defecto");
                Check(Path.GetFileName(archivo)=="perfil-fontaneria.json","Nombre de archivo");
                var json=JsonNode.Parse(File.ReadAllText(archivo))!.AsObject();json["AlturaRedMm"]=3210;File.WriteAllText(archivo,json.ToJsonString());
                Near(SalaSettingsStore.Load(p.Tipo,folder).Perfil.AlturaRedMm,3210,"Altura editada");
                p.Equipos[0].Claves.Add("CLAVE PERSONAL");Check(SalaSettingsStore.Save(p,folder)=="","Guardar");
                Check(SalaSettingsStore.Load(p.Tipo,folder).Perfil.Equipos[0].Claves.Contains("CLAVE PERSONAL"),"Clave preservada");
                Check(File.ReadAllText(archivo).Contains("Cilindro"),"Enum como texto");
                File.WriteAllText(archivo,"{corrupto");var corrupto=SalaSettingsStore.Load(p.Tipo,folder);
                Near(corrupto.Perfil.AlturaRedMm,2500,"Recuperar predeterminado");Check(corrupto.Nota.Contains("ignorado") && File.ReadAllText(archivo)=="{corrupto","No sobrescribir archivo corrupto");
                File.WriteAllText(archivo,"""
                    {"Tipo":"Pci","AlturaRedMm":-1,"LadoMinMm":"NaN","LadoMaxMm":"Infinity","DistanciaRotuloMm":-5,"CapasSala":null,"CapasIgnoradas":null,"Equipos":[]}
                    """);
                var saneado=SalaSettingsStore.Load(p.Tipo,folder).Perfil;
                Check(saneado.Tipo==p.Tipo && saneado.Equipos.Count==6 && saneado.CapasSala.Count==0 && saneado.CapasIgnoradas.Count==0,"Tipo y listas saneados");
                Near(saneado.LadoMinMm,200,"NaN saneado");Near(saneado.LadoMaxMm,12000,"Infinito saneado");Near(saneado.DistanciaRotuloMm,600,"Negativo saneado");
                File.WriteAllText(archivo,"""
                    {"Equipos":[{"Id":"deposito_afs","AltoMm":-1,"ElevacionMm":-2,"Claves":null}],"Generico":null}
                    """);
                var equipo=SalaSettingsStore.Load(p.Tipo,folder).Perfil.Equipos.Single();Near(equipo.AltoMm,2000,"Alto de tipo saneado");Near(equipo.ElevacionMm,0,"Base saneada");Check(equipo.Claves.Count==0,"Claves nulas");
                string bloqueo=Path.Combine(folder,"archivo");File.WriteAllText(bloqueo,"ocupado");
                Check(SalaSettingsStore.Save(p,bloqueo).Length>0,"Error de escritura informado");
            }
            finally {if(Directory.Exists(folder)) Directory.Delete(folder,true);}
        });
        Test("Informe y CSV",()=>
        {
            var p=Fontaneria();string texto=SalaReport.Texto(p,"plano.dwg","PB",new[]{"Detalle de integración"});
            Check(texto.Contains("Equipos modelados (5)") && texto.Contains("Caja 3000×1500") && texto.Contains("Cilindro Ø 1000") && texto.Contains("Detalle de integración"),"Contenido del informe");
            string csv=SalaReport.Csv(p);Check(csv.Split('\n',StringSplitOptions.RemoveEmptyEntries).Length==6,"Una fila por equipo");
            Check(csv.StartsWith("Tipo;Etiqueta;Forma;Ancho_mm;Fondo_mm;Diametro_mm;Alto_mm;Alto_supuesto;Base_mm;Centro_X_mm;Centro_Y_mm;Origen"),"Cabecera exacta");
            for(int i=0;i<65;i++) p.Descartes.Add(($"elemento {i}","omitido"));
            texto=SalaReport.Texto(p,"CAD","Nivel",Array.Empty<string>());Check(texto.Contains($"… y {p.Descartes.Count-60} más"),"Límite de descartes");
            var especial=new SalaPlan();especial.Equipos.Add(new(){Tipo=new(){Nombre="Tipo;A"},Etiqueta="A;\"B\"\nC",Forma=EquipoForma.Cilindro,RadioMm=500,Centro=new(12.4,18.6),Origen="origen"});
            var anterior=CultureInfo.CurrentCulture;
            try {CultureInfo.CurrentCulture=CultureInfo.GetCultureInfo("es-ES");csv=SalaReport.Csv(especial);Check(csv.Contains("\"Tipo;A\";\"A;\"\"B\"\"\nC\"") && csv.Contains(";12;19;"),"Escape y números invariantes");}
            finally {CultureInfo.CurrentCulture=anterior;}
        });
        Console.WriteLine(fallos==0?"PASS: todas las comprobaciones.":$"FAIL: {fallos} grupos.");
        return fallos==0?0:1;
    }
}
