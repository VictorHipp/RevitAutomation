using System.Globalization;
using System.Text;

namespace RevitAutomation.Addin.Commands.SalasTecnicas;

public static class SalaPerfiles
{
    public static string Normalizar(string s)
    {
        var b = new StringBuilder();
        bool espacio = true;
        foreach (var r in (s ?? "").Normalize(NormalizationForm.FormD).ToUpperInvariant().EnumerateRunes())
        {
            var categoria = Rune.GetUnicodeCategory(r);
            if (categoria is UnicodeCategory.NonSpacingMark or UnicodeCategory.SpacingCombiningMark or UnicodeCategory.EnclosingMark) continue;
            if (Rune.IsLetterOrDigit(r)) { b.Append(r.ToString()); espacio = false; }
            else if (!espacio) { b.Append(' '); espacio = true; }
        }
        return b.ToString().Trim();
    }

    private static List<string> Lista(string s) => s.Split('|').Select(Normalizar).ToList();
    private static EquipoTipo Equipo(string id, string nombre, string claves, EquipoForma forma, double alto,
        double baseMm = 0, EquipoCategoria categoria = EquipoCategoria.Mecanico, EquipoConexion conexion = EquipoConexion.Ninguna) =>
        new() { Id = id, Nombre = nombre, Claves = claves.Length == 0 ? new() : Lista(claves), Forma = forma,
            AltoMm = alto, ElevacionMm = baseMm, Categoria = categoria, Conexion = conexion };

    public static SalaPerfil Defecto(SalaTipo tipo)
    {
        var p = new SalaPerfil { Tipo = tipo, LadoMinMm = 200, LadoMaxMm = 12000, DistanciaRotuloMm = 600,
            CapasIgnoradas = Lista("MURO|MUROS|WALL|TABIQUE|ARQ|ARQUITECTURA|PUERTA|PUERTAS|DOOR|VENTANA|WINDOW|COTA|COTAS|DIM|TEXTO|TEXTOS|TEXT|HATCH|SOMBREADO|RAYADO|MOBILIARIO|MUEBLE|PILAR|PILARES|COLUMN|ESTRUCTURA|EJE|EJES|GRID|CAJETIN|MARCO|VIEWPORT|DEFPOINTS|LEYENDA|ROTULO|ANOTACION|SIMBOLO|A WALL|A DOOR|A ANNO") };
        var caja = EquipoForma.Caja; var auto = EquipoForma.Auto; var cilindro = EquipoForma.Cilindro;
        var mecanico = EquipoCategoria.Mecanico; var electrico = EquipoCategoria.Electrico;
        var media = EquipoConexion.Media; var arriba = EquipoConexion.Arriba;
        switch (tipo)
        {
            case SalaTipo.Fontaneria:
                p.Nombre = "Fontanería"; p.AlturaRedMm = 2500;
                p.CapasSala = Lista("FON|FONTANERIA|AFS|ACS|AGUA|DEPOSITO|GRUPO|BOMBA|EQUIPO|EQUIPOS|M EQPM|P EQPM");
                p.Equipos = new()
                {
                    Equipo("vaso_expansion", "Vaso de expansión / calderín", "VASO EXPANSION|VASO DE EXPANSION|CALDERIN|HIDROFORO|ACUMULADOR HIDRONEUMATICO", cilindro, 900, 0, mecanico, media),
                    Equipo("acumulador_acs", "Acumulador ACS", "ACUMULADOR|INTERACUMULADOR|DEPOSITO ACS|ACS|TERMO|CALENTADOR", auto, 1900, 0, mecanico, arriba),
                    Equipo("deposito_afs", "Depósito AFS", "DEPOSITO|ALJIBE|AFS|TANQUE|AGUA FRIA|RESERVA", auto, 2000, 0, mecanico, arriba),
                    Equipo("grupo_presion", "Grupo de presión", "GRUPO DE PRESION|GRUPO PRESION|GRUPO|BOMBA|BOMBEO|GP", caja, 1200, 0, mecanico, media),
                    Equipo("tratamiento", "Tratamiento de agua", "DESCALCIFICADOR|OSMOSIS|FILTRO|DOSIFICADOR|CLORADOR", auto, 1500, 0, mecanico, media),
                    Equipo("cuadro", "Cuadro eléctrico", "CUADRO|CE|CCM|CONTROL", caja, 800, 1000, electrico)
                };
                p.Generico = Equipo("generico", "Equipo de fontanería", "", auto, 1000); break;
            case SalaTipo.Pci:
                p.Nombre = "Protección contra incendios"; p.AlturaRedMm = 2700;
                p.CapasSala = Lista("PCI|INCENDIO|INCENDIOS|EXTINCION|BIE|ROCIADOR|SPK|SPRINKLER|F PROT|FP|EQUIPO|EQUIPOS");
                p.Equipos = new()
                {
                    Equipo("vaso_pci", "Calderín / vaso", "CALDERIN|VASO|ACUMULADOR HIDRONEUMATICO", cilindro, 900, 0, mecanico, media),
                    Equipo("aljibe_pci", "Aljibe / depósito PCI", "ALJIBE|DEPOSITO|RESERVA|TANQUE", auto, 2500, 0, mecanico, arriba),
                    Equipo("grupo_pci", "Grupo de bombeo PCI", "GRUPO|BOMBA|JOCKEY|ELECTROBOMBA|MOTOBOMBA|DIESEL|BOMBEO", caja, 1300, 0, mecanico, media),
                    Equipo("cuadro_pci", "Cuadro / central", "CUADRO|CENTRAL|CONTROL", caja, 800, 1000, electrico)
                };
                p.Generico = Equipo("generico", "Equipo PCI", "", auto, 1000); break;
            case SalaTipo.ContadoresAgua:
                p.Nombre = "Contadores de agua"; p.AlturaRedMm = 1000;
                p.CapasSala = Lista("CONT|CONTADOR|CONTADORES|BATERIA|ACOMETIDA|AGUA|FON|FONTANERIA");
                p.Equipos = new()
                {
                    Equipo("bateria", "Batería de contadores", "BATERIA|CENTRALIZACION|COLECTOR", caja, 1800, 200, EquipoCategoria.Fontaneria),
                    Equipo("armario", "Armario / hornacina", "ARMARIO|HORNACINA|NICHO", caja, 2000, 0, EquipoCategoria.Generico),
                    Equipo("contador", "Contador", "CONTADOR|CONTADORES|CONT|MEDIDOR|WATERMETER", caja, 250, 900, EquipoCategoria.Fontaneria),
                    Equipo("grupo_presion", "Grupo de presión", "GRUPO DE PRESION|GRUPO PRESION|BOMBA|GP", caja, 1200, 0, mecanico, media),
                    Equipo("tratamiento", "Filtro / tratamiento", "FILTRO|DESCALCIFICADOR|DOSIFICADOR", auto, 1200, 0, mecanico, media)
                };
                p.Generico = Equipo("generico", "Equipo de contadores", "", auto, 1000, 0, EquipoCategoria.Fontaneria); break;
            case SalaTipo.Electricidad:
                p.Nombre = "Electricidad"; p.Red = RedSala.Bandejas; p.AlturaRedMm = 2700;
                p.CapasSala = Lista("ELEC|ELECTRICIDAD|ELECTRICO|BT|MT|CUADRO|CUADROS|CGBT|TRAFO|E EQPM|EQUIPO|EQUIPOS");
                p.Equipos = new()
                {
                    Equipo("trafo", "Transformador", "TRAFO|TRANSFORMADOR", caja, 2000, 0, electrico),
                    Equipo("celdas", "Celdas de media tensión", "CELDA|CELDAS|MEDIA TENSION|MT", caja, 2000, 0, electrico),
                    Equipo("cgbt", "Cuadro general (CGBT)", "CGBT|CUADRO GENERAL|CGD|CGMP", caja, 2000, 0, electrico),
                    Equipo("centralizacion", "Centralización de contadores", "CENTRALIZACION|CONTADORES|CONTADOR|MODULO|CPM|CGP", caja, 1800, 300, electrico),
                    Equipo("sai", "SAI", "SAI|UPS", caja, 1900, 0, electrico),
                    Equipo("baterias", "Baterías / condensadores", "BATERIA|BATERIAS|CONDENSADOR|CONDENSADORES", caja, 1800, 0, electrico),
                    Equipo("grupo_electrogeno", "Grupo electrógeno", "GRUPO ELECTROGENO|ELECTROGENO|GENERADOR|GE", caja, 1800, 0, electrico),
                    Equipo("rack", "Rack / telecomunicaciones", "RACK|RITI|RITS|RITU|TELECOM", caja, 2000, 0, electrico),
                    Equipo("cuadro", "Cuadro eléctrico", "CUADRO|CS|CSA|SUBCUADRO", caja, 2000, 0, electrico)
                };
                p.Generico = Equipo("generico", "Equipo eléctrico", "", caja, 1500, 0, electrico); break;
            default: throw new ArgumentOutOfRangeException(nameof(tipo), "Tipo de sala desconocido.");
        }
        return p;
    }
}
