using System.Reflection;
using Autodesk.Revit.UI;
using CadMepRevit;
using RevitAutomation.Addin.Commands;

namespace RevitAutomation.Addin;

/// <summary>
/// Crea la pestaña única «RevitAutomation» al arrancar Revit. Cada sección es un panel con su desplegable:
/// CAD-REV (del plano CAD a Revit), Instalaciones (salas técnicas, patinillo, aislamiento, llaves), Etiquetado,
/// Documentación (planos y exportaciones) y Región (sectorización).
/// </summary>
public sealed class RevitAutomationApplication : IExternalApplication
{
    public const string TabName = "RevitAutomation";

#if NETFRAMEWORK
    // Revit 2023 y 2024 (.NET Framework 4.8) no tienen redirecciones de ensamblados para los complementos: las bibliotecas
    // que acompañan al DLL (System.Text.Json, System.Memory…) se buscan aquí en la carpeta del complemento, sea cual sea
    // la versión que pida quien las usa.
    static RevitAutomationApplication() => AppDomain.CurrentDomain.AssemblyResolve += ResolverDesdeCarpeta;

    private static Assembly? ResolverDesdeCarpeta(object? sender, ResolveEventArgs args)
    {
        try
        {
            string nombre = new AssemblyName(args.Name).Name ?? "";
            if (nombre.Length == 0 || nombre.EndsWith(".resources", StringComparison.OrdinalIgnoreCase)) return null;
            string carpeta = Path.GetDirectoryName(typeof(RevitAutomationApplication).Assembly.Location) ?? "";
            string ruta = Path.Combine(carpeta, nombre + ".dll");
            if (!File.Exists(ruta)) return null;
            foreach (var cargado in AppDomain.CurrentDomain.GetAssemblies())
            {
                try
                {
                    if (!cargado.IsDynamic && string.Equals(cargado.Location, ruta, StringComparison.OrdinalIgnoreCase)) return cargado;
                }
                catch (NotSupportedException) { }
            }
            return Assembly.LoadFrom(ruta);
        }
        catch
        {
            return null;
        }
    }
#endif

    public Result OnStartup(UIControlledApplication application)
    {
        try
        {
            try { application.CreateRibbonTab(TabName); }
            catch (Autodesk.Revit.Exceptions.ArgumentException) { /* la pestaña ya existe */ }
            string asm = Assembly.GetExecutingAssembly().Location;

            // 1 · CAD-REV: del plano CAD (DWG/DXF) a Revit, un botón por instalación.
            var cadPanel = application.CreateRibbonPanel(TabName, CadRevRibbon.Name);
            var cad = Menu(cadPanel, "RevitAutomation_CadRev", CadRevRibbon.Name,
                "Del plano CAD (DWG/DXF) a Revit: un botón por instalación (tuberías, conductos, bandejas, luminarias, equipos y PCI). " +
                "Las redes colgadas siguen el techo; al final, informe de lo modelado, lo no modelado y por qué.");
            cad.Image = CadRevRibbon.Icon("CAD", System.Windows.Media.Color.FromRgb(0x24, 0x70, 0xB8), 16);
            cad.LargeImage = CadRevRibbon.Icon("CAD", System.Windows.Media.Color.FromRgb(0x24, 0x70, 0xB8), 32);
            var cadRevCommands = new[] { typeof(Commands.CadRev.CadRevPipesCommand), typeof(Commands.CadRev.CadRevDuctsCommand),
                typeof(Commands.CadRev.CadRevTraysCommand), typeof(Commands.CadRev.CadRevLightingCommand), typeof(Commands.CadRev.CadRevEquipmentCommand) };
            foreach (var b in CadRevRibbon.Buttons(asm, cadRevCommands, typeof(Commands.CadRev.CadRevAvailability).FullName!)) cad.AddPushButton(b);

            // 2 · Instalaciones: salas técnicas (desplegable propio) y herramientas de fontanería.
            var installPanel = application.CreateRibbonPanel(TabName, "Instalaciones");
            var salas = Menu(installPanel, "RevitAutomation_SalasTecnicas", "Salas técnicas",
                "Modela una sala técnica desde su CAD vinculado en la vista en planta activa: equipos (depósitos, grupos, cuadros, " +
                "contadores…) con su volumen, y sus tuberías o bandejas. Automático; al final, informe. Ctrl+Z lo deshace.", RibbonIcon.TechRoom);
            const string salaTip = " Abra la planta con el DWG de la sala vinculado (si hay varios, pinche el de la sala) y pulse: cada contorno cerrado " +
                "con un rótulo dentro o al lado (o en una capa de la instalación) se convierte en el equipo que dice el rótulo, con su forma " +
                "(círculo → cilindro, resto → caja) y alto habitual (o el del rótulo «H=…»). Al repetirlo sustituye solo lo que creó este botón. " +
                "Claves y alturas editables en Documentos\\RevitAutomation\\SalasTecnicas. Informe en …\\SalasTecnicas\\output. Ctrl+Z lo deshace.";
            Add(salas, "RevitAutomation_SalaContadores", "1 · Contadores de agua", asm, typeof(Commands.SalasTecnicas.SalaContadoresAguaCommand),
                "Cuarto de contadores de agua: batería de contadores, contadores, armarios y grupo, con sus tuberías (eje a 1,00 m)." + salaTip, RibbonIcon.TechRoomWaterMeters);
            Add(salas, "RevitAutomation_SalaElectricidad", "2 · Electricidad", asm, typeof(Commands.SalasTecnicas.SalaElectricidadCommand),
                "Cuarto eléctrico: transformador, celdas, CGBT y cuadros, centralización de contadores, SAI, baterías, grupo electrógeno y racks, con sus bandejas (a 2,70 m)." + salaTip, RibbonIcon.TechRoomElectrical);
            Add(salas, "RevitAutomation_SalaPci", "3 · PCI", asm, typeof(Commands.SalasTecnicas.SalaPciCommand),
                "Sala de bombas de PCI: aljibe, grupo de bombeo (principal, jockey, diésel), calderín y cuadro, con sus tuberías (a 2,70 m) bajando a los equipos." + salaTip, RibbonIcon.TechRoomFire);
            Add(salas, "RevitAutomation_SalaFontaneria", "4 · Fontanería", asm, typeof(Commands.SalasTecnicas.SalaFontaneriaCommand),
                "Sala de fontanería: depósitos AFS, acumuladores ACS, grupo de presión, vaso de expansión, tratamiento y cuadro, con sus tuberías (a 2,50 m) bajando a los equipos." + salaTip, RibbonIcon.TechRoomPlumbing);

            var plumbing = Menu(installPanel, "RevitAutomation_Fontaneria", "Fontanería",
                "Patinillo de fontanería, aislamiento de tuberías según el RITE y llaves de local húmedo.", RibbonIcon.Shaft);
            Add(plumbing, "RevitAutomation_Patinillo", "Patinillo de fontanería", asm, typeof(Commands.Patinillo.PatinilloCommand),
                "Modela el patinillo vertical de fontanería: las tuberías de agua fría y ACS suben desde los contadores de planta baja " +
                "y en cada planta salen bajo el forjado, en peine, las de sus viviendas. Seleccione el patinillo (habitación o contorno de " +
                "líneas) o dibújelo; todos los valores se editan en el diálogo y se guardan. Muestra la matriz antes de modelar, crea una " +
                "vista 3D y un alzado, y deja el informe y las desviaciones en Documentos\\RevitAutomation\\Patinillo\\output. Ctrl+Z lo deshace entero.",
                RibbonIcon.Shaft);
            Add(plumbing, "RevitAutomation_Aislamiento", "Aislamiento RITE", asm, typeof(Commands.Aislamiento.AislamientoCommand),
                "Aísla automáticamente las tuberías seleccionadas (o las que elija con el ratón), con sus piezas y accesorios, según el RITE " +
                "(IT 1.2.4.2.1.2, procedimiento simplificado): identifica por el sistema de Revit el ACS, el agua fría, la calefacción, el " +
                "agua fría de clima y las líneas frigoríficas, calcula el espesor según diámetro, temperatura y conductividad del aislante y lo " +
                "redondea al espesor comercial. Solo pregunta si van por el interior o el exterior del edificio; nunca reduce un aislamiento " +
                "existente y deja informe y desviaciones en Documentos\\RevitAutomation\\Aislamiento\\output. Ctrl+Z lo deshace entero.",
                RibbonIcon.Insulation);
            Add(plumbing, "RevitAutomation_LlavesLocalHumedo", "Llaves de local húmedo", asm, typeof(Commands.LlavesLocalHumedo.LlavesLocalHumedoCommand),
                "Coloca las llaves en las marcas del CAD vinculado: elija bloque y capa, familia del modelo, unidades, altura y giro. " +
                "Comprueba la posición del vínculo, omite posiciones ocupadas y genera un informe. Solo colocación, sin conexión hidráulica. Ctrl+Z deshace el lote.",
                RibbonIcon.Pipe);

            // 3 · Etiquetado de instalaciones con las etiquetas del propio modelo, sin solapes.
            var tagPanel = application.CreateRibbonPanel(TabName, "Etiquetado");
            var tags = Menu(tagPanel, "RevitAutomation_Tagging", "Etiquetado",
                "Etiquetado automático de instalaciones con las etiquetas cargadas en el modelo, sin solapes y con informe de resultados.", RibbonIcon.Tag);
            const string runTip = ": solo tramos rectos de más de 15 cm, con una etiqueta del propio modelo, con su cabeza 20 cm por encima del eje; " +
                "si ahí taparía otra etiqueta o cualquier tubería, conducto o bandeja, se sube lo justo (hasta 100 cm, con directriz) o se pone debajo del eje. " +
                "Si no cabe en ningún sitio, se informa. Al terminar muestra el listado de etiquetados y de desviaciones.";
            Add(tags, "RevitAutomation_TagSelectedPipes", "1 · Tuberías seleccionadas", asm, typeof(Commands.PipeTagging.PipeTagSelectedCommand),
                "Etiqueta las tuberías seleccionadas en la vista activa (no codos, tes ni accesorios)" + runTip, RibbonIcon.Pipe);
            Add(tags, "RevitAutomation_TagSelectedDucts", "2 · Conductos seleccionados", asm, typeof(Commands.PipeTagging.DuctTagSelectedCommand),
                "Etiqueta los conductos seleccionados en la vista activa (no codos, tes, transiciones ni accesorios)" + runTip, RibbonIcon.Duct);
            Add(tags, "RevitAutomation_TagSelectedCableTrays", "3 · Bandejas seleccionadas", asm, typeof(Commands.PipeTagging.CableTrayTagSelectedCommand),
                "Etiqueta las bandejas de cables seleccionadas en la vista activa (no curvas, tes, cruces ni reducciones)" + runTip, RibbonIcon.CableTray);
            Add(tags, "RevitAutomation_TagSelectedFamilies", "4 · Familias seleccionadas (luminarias, extintores…)", asm, typeof(Commands.PipeTagging.FamilyTagSelectedCommand),
                "Etiqueta las familias seleccionadas en la vista activa (luminarias, extintores, equipos, mecanismos…) con las etiquetas del propio modelo, " +
                "eligiendo una por categoría. Cada etiqueta va junto a su símbolo sin taparlo ni pisar otra; si no cabe, se aparta con directriz o se informa.",
                RibbonIcon.Extinguisher);
            Add(tags, "RevitAutomation_TagSelectedLuminaires", "5 · Luminarias seleccionadas", asm, typeof(Commands.PipeTagging.LuminaireTagSelectedCommand),
                "Etiqueta solo las luminarias seleccionadas en la vista activa, una etiqueta por luminaria (las que ya tienen etiqueta se saltan), sin tapar " +
                "ninguna luminaria, etiqueta o texto; si no cabe, se aparta con directriz o se informa. Lo que no es luminaria se descarta solo.",
                RibbonIcon.Luminaire);

            // 4 · Documentación: planos (láminas) y exportaciones a PDF y CAD.
            var docPanel = application.CreateRibbonPanel(TabName, "Documentación");
            var planos = Menu(docPanel, "RevitAutomation_Planos", "Planos",
                "Genera los planos técnicos del modelo: vistas en planta por nivel y disciplina, colores por sistema, etiquetas, cuadros, " +
                "leyendas y láminas con cajetín. Muestra un resumen antes de crear nada y no modifica los elementos del modelo.", RibbonIcon.Sheets);
            const string planosTip = " Muestra antes un resumen (vistas, láminas, niveles, etiquetas disponibles y datos que faltan). Crea vistas y láminas " +
                "nativas de Revit; al repetirlo sustituye solo lo que generó antes. Al terminar guarda en el Escritorio un PDF combinado con las láminas. " +
                "No modifica los elementos del modelo.";
            var planosActions = new (string Id, string Label, Type Command, RibbonIcon Icon, string Tip)[]
            {
                ("RevitAutomation_PlanosPlumbing", "Fontanería (agua y saneamiento)", typeof(Commands.Planos.PlanosPlumbingCommand), RibbonIcon.Pipe,
                    "Planos de fontanería por nivel: agua fría y ACS, saneamiento y evacuación, con aparatos sanitarios y equipos."),
                ("RevitAutomation_PlanosHvac", "Climatización", typeof(Commands.Planos.PlanosHvacCommand), RibbonIcon.Duct,
                    "Planos de climatización por nivel: conductos de impulsión, retorno y extracción, difusores, tuberías de clima y equipos."),
                ("RevitAutomation_PlanosElectrical", "Electricidad", typeof(Commands.Planos.PlanosElectricalCommand), RibbonIcon.CableTray,
                    "Planos eléctricos por nivel: bandejas, tubos, cuadros, tomas y dispositivos, con cuadros de equipos y circuitos."),
                ("RevitAutomation_PlanosLighting", "Iluminación", typeof(Commands.Planos.PlanosLightingCommand), RibbonIcon.Luminaire,
                    "Planos de iluminación por nivel: luminarias y mandos, con cuadro de tipos de luminaria."),
                ("RevitAutomation_PlanosGeneric", "Familias genéricas y PCI", typeof(Commands.Planos.PlanosGenericCommand), RibbonIcon.GenericModel,
                    "Planos de modelos genéricos y de protección contra incendios (rociadores, extintores, BIE, alarma y sus tuberías)."),
                ("RevitAutomation_PlanosAll", "Conjunto completo", typeof(Commands.Planos.PlanosAllCommand), RibbonIcon.Sheets,
                    "Primero elija qué generar (todas las disciplinas o una) y cómo se ven las vistas (estilo visual, detalle, 3D)."),
            };
            for (int i = 0; i < planosActions.Length; i++)
            {
                if (i == planosActions.Length - 1) planos.AddSeparator();
                var a = planosActions[i];
                Add(planos, a.Id, a.Label, asm, a.Command, a.Tip + planosTip, a.Icon);
            }
            var export = Menu(docPanel, "RevitAutomation_Exportar", "Exportar",
                "Exporta láminas y vistas a PDF combinado o a CAD (DWG, DXF o DGN).", RibbonIcon.Pdf);
            Add(export, "RevitAutomation_ExportPdf", "Exportar PDF", asm, typeof(PdfExportCommand),
                "Exporta conjuntos o vistas seleccionadas a un PDF combinado.", RibbonIcon.Pdf);
            Add(export, "RevitAutomation_ExportCad", "Exportar CAD", asm, typeof(CadExportCommand),
                "Exporta planos y vistas a DWG, DXF o DGN con selección de formato y configuración.", RibbonIcon.Cad);

            // 5 · Región: sectorización calcada del CAD vinculado.
            var regionPanel = application.CreateRibbonPanel(TabName, "Región");
            var region = Menu(regionPanel, "RevitAutomation_RegionMenu", "Región",
                "Regiones rellenadas calcadas de las zonas de colores del CAD vinculado (sectorización).", RibbonIcon.Region);
            Add(region, "RevitAutomation_Region", "Región de sectorización", asm, typeof(Commands.Regiones.RegionCommand),
                "Calca en la vista en planta activa las zonas del CAD vinculado: cada polilínea cerrada se convierte en una región rellenada " +
                "con el color de su capa, y cada capa en un tipo «REG - <capa>». Automático: usa el CAD seleccionado o el único de la vista; si hay varios, " +
                "pide que lo pinche. Al terminar muestra un informe por capa. Al repetirlo sustituye solo las regiones que creó. Ctrl+Z lo deshace.",
                RibbonIcon.Region);
            return Result.Succeeded;
        }
        catch (Exception ex)
        {
            Autodesk.Revit.UI.TaskDialog.Show(TabName, $"No se pudo crear la pestaña «{TabName}» en la cinta.\n\n" +
                "Cómo corregirlo: cierre Revit, vuelva a ejecutar Instalar_RevitAutomation.exe y abra Revit de nuevo.\n\nDetalle: " + ex.Message);
            return Result.Failed;
        }
    }

    public Result OnShutdown(UIControlledApplication application) => Result.Succeeded;

    private static PulldownButton Menu(RibbonPanel panel, string id, string label, string tip, RibbonIcon? icon = null)
    {
        var menu = (PulldownButton)panel.AddItem(new PulldownButtonData(id, label));
        menu.ToolTip = tip;
        if (icon is { } i)
        {
            menu.Image = RibbonIcons.Create(i, 16);
            menu.LargeImage = RibbonIcons.Create(i, 32);
        }
        return menu;
    }

    private static void Add(PulldownButton menu, string id, string label, string assemblyPath, Type command, string tip, RibbonIcon icon)
    {
        var button = menu.AddPushButton(new PushButtonData(id, label, assemblyPath, command.FullName!));
        button.ToolTip = tip;
        button.Image = RibbonIcons.Create(icon, 16);
        button.LargeImage = RibbonIcons.Create(icon, 32);
    }
}
