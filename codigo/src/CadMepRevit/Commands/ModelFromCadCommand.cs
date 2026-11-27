using System.Windows.Forms;
using Autodesk.Revit.Attributes;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using CadMepRevit.Core;
using CadMepRevit.Revit;
using CadMepRevit.Ui;

namespace CadMepRevit.Commands;

/// <summary>
/// Botón «Modelar desde CAD». Sin campos ni medidas: lee el plano, hace el inventario, pregunta SOLO la planta
/// (si el proyecto tiene varios niveles), modela con el motor de CadMepRevit2 (MepBuilder.Build: una sola
/// transacción, eje a la altura sobre el nivel) y termina con un informe que cuadra el inventario.
/// </summary>
[Transaction(TransactionMode.Manual)]
[Regeneration(RegenerationOption.Manual)]
public class ModelFromCadCommand : IExternalCommand
{
    private static string? _lastFolder;

    /// <summary>Qué modela este botón (null = todo, como el antiguo «Modelar desde CAD»).</summary>
    protected virtual ModelScope? Scope => null;

    private string Title => Scope is ModelScope s ? "Modelar " + ModelScopes.Label(s).ToLowerInvariant() : "Modelar desde CAD";

    /// <summary>¿Necesita este botón tipos de tramo (tubería, conducto, bandeja, tubo)?</summary>
    private bool NeedsCurveTypes => Scope is null or ModelScope.Tuberias or ModelScope.Conductos or ModelScope.Bandejas;

    public Result Execute(ExternalCommandData commandData, ref string message, ElementSet elements)
    {
        var uiapp = commandData.Application;
        var uidoc = uiapp.ActiveUIDocument;

        // ---- Requisitos previos (solo lectura; los mismos que el asistente)
        if (uidoc?.Document is not { } doc)
        {
            Messages.Error("No hay ningún proyecto abierto.", "Abra o cree un proyecto de Revit y vuelva a pulsar el botón.");
            return Result.Cancelled;
        }
        if (doc.IsFamilyDocument)
        {
            Messages.Error("Está abierto un archivo de familia.", "Este botón trabaja en proyectos. Cambie a un proyecto (.rvt) y vuelva a intentarlo.");
            return Result.Cancelled;
        }
        if (doc.IsReadOnly)
        {
            Messages.Error("El proyecto está abierto en solo lectura.", "Guarde una copia editable o ábralo con permisos de escritura.");
            return Result.Cancelled;
        }
        RevitCatalog catalog;
        try { catalog = RevitCatalog.Load(doc); }
        catch (Exception ex)
        {
            Messages.Error("No se pudieron leer los tipos del proyecto.", "Vuelva a intentarlo. Si persiste, guarde, cierre y abra de nuevo el proyecto.", ex.ToString());
            return Result.Failed;
        }
        if (catalog.Levels.Count == 0)
        {
            Messages.Error("El proyecto no tiene niveles.", "Abra una vista de alzado y cree un nivel (Arquitectura > Nivel). Después repita el comando.");
            return Result.Cancelled;
        }
        var neededKinds = Scope is ModelScope sk ? MepKinds.All.Where(k => ModelScopes.Includes(sk, k)).ToList() : MepKinds.All.ToList();
        if (Scope is ModelScope && NeedsCurveTypes && neededKinds.All(k => catalog.Types.GetValueOrDefault(k)?.Count is null or 0))
        {
            Messages.Error($"El proyecto no tiene tipos de {string.Join(" ni de ", neededKinds.Select(k => MepKinds.Label(k).ToLowerInvariant()))}.",
                RevitCatalog.HowToGetTypes(neededKinds[0]));
            return Result.Cancelled;
        }
        if (Scope is null && catalog.Types.Values.All(t => t.Count == 0))
        {
            Messages.Error("El proyecto no tiene tipos de tubería, conducto, bandeja ni tubo eléctrico.",
                "Cree el proyecto con una plantilla MEP de Revit ('Sistemas', 'Mecánica', 'Fontanería' o 'Eléctrica') " +
                "o copie los tipos desde otro proyecto con Gestionar > Transferir normas de proyecto.");
            return Result.Cancelled;
        }
        var extraDecisions = new List<string>();
        if (Scope is null && catalog.Types[MepKind.Tuberia].Count == 0 && catalog.Types[MepKind.Conducto].Count == 0)
            extraDecisions.Add("El proyecto no tiene tipos de tubería ni de conducto (¿plantilla de Arquitectura?): se continúa sin preguntar " +
                               "y solo se crean muros y bloques. Para tuberías y conductos use la plantilla 'Sistemas' o transfiera los tipos.");

        var owner = new RevitWindow(uiapp.MainWindowHandle);

        // ---- Plano
        string path;
        using (var dlg = new OpenFileDialog
               {
                   Title = $"CAD-REV · {Title}: elija el plano (DWG o DXF)",
                   Filter = "Planos CAD (*.dwg;*.dxf)|*.dwg;*.dxf|AutoCAD DWG (*.dwg)|*.dwg|DXF (*.dxf)|*.dxf",
                   CheckFileExists = true,
                   InitialDirectory = _lastFolder ?? ""
               })
        {
            if (dlg.ShowDialog(owner) != DialogResult.OK) return Result.Cancelled;
            path = dlg.FileName;
            _lastFolder = Path.GetDirectoryName(path);
        }

        // ---- Inventario del plano (capas, bloques, textos, cotas)
        CadDrawing drawing;
        // Reglas de la oficina antes de leer: dicen también qué capas tienen luminarias dibujadas con líneas.
        UserRules.Current = UserRules.Load();
        Cursor.Current = Cursors.WaitCursor;
        var readClock = System.Diagnostics.Stopwatch.StartNew();
        try { drawing = CadReader.Read(path, new CadReader.Options { ReadLinesInsideBlocks = PlanDefaults.ReadLinesInsideBlocks }); readClock.Stop(); }
        catch (CadReadException ex) { Messages.Error(ex.Message, ex.HowToFix); return Result.Cancelled; }
        catch (Exception ex)
        {
            Messages.Error("Error inesperado al leer el CAD.", "Pruebe a guardar el plano como DXF o DWG 2018 y vuelva a intentarlo.", ex.ToString());
            return Result.Cancelled;
        }
        finally { Cursor.Current = Cursors.Default; }
        if (drawing.Segments.Count == 0 && drawing.Blocks.Count == 0 && drawing.FixtureShapes.Count == 0 &&
            !(Scope == ModelScope.Tuberias && drawing.Circles.Count > 0))
        {
            Messages.Error("El plano no contiene líneas ni bloques en el espacio modelo.",
                "Compruebe que los tramos están dibujados con LÍNEA o POLILÍNEA y las bajantes con CÍRCULO en el espacio modelo (no en una presentación) y que no son una referencia externa (XREF) sin unir.",
                string.Join("\n", drawing.Warnings));
            return Result.Cancelled;
        }

        // ---- Unidades absurdas en la cabecera (p. ej. dice mm y está dibujado en metros): se corrigen antes de nada.
        LayerPlanning.FixImplausibleUnits(drawing);

        // ---- Lo que el programa no reconoce por su nombre: se pregunta UNA vez y se recuerda para los siguientes planos.
        {
            double uk = DrawingUnits.ToMillimeters(drawing.HeaderUnit ?? DrawingUnit.Milimetros);
            var unknownLayers = Unrecognized.Layers(drawing);
            if (Scope == ModelScope.Tuberias) unknownLayers.RemoveAll(l => PipeNetworks.LayerKind(l.Layer) == MepKind.Tuberia);
            // Las capas (líneas) solo importan a los botones de tramos: «Luminarias» y «Equipos y PCI» colocan bloques y
            // reconocen solas las capas de luminarias; preguntar por capas ahí solo confunde.
            if (Scope is ModelScope.Luminarias or ModelScope.EquiposPci) unknownLayers.Clear();
            var unknownBlocks = Unrecognized.Blocks(drawing, uk);
            if (unknownLayers.Count + unknownBlocks.Count > 0)
            {
                using var ask = new UnknownItemsForm(Path.GetFileName(path), unknownLayers, unknownBlocks);
                var answer = ask.ShowDialog(owner);
                if (answer == DialogResult.Cancel) return Result.Cancelled;
                if (answer == DialogResult.OK && ask.LayerAnswers.Count + ask.BlockAnswers.Count > 0)
                {
                    foreach (var (l, kind) in ask.LayerAnswers) UserRules.Current.Layers[l] = kind;
                    foreach (var (b, role) in ask.BlockAnswers) UserRules.Current.Blocks[b] = role;
                    foreach (var l in ask.FixtureLayerAnswers)
                        if (!UserRules.Current.FixtureLayers.Contains(l, StringComparer.OrdinalIgnoreCase)) UserRules.Current.FixtureLayers.Add(l);
                    string err = UserRules.Current.Save();
                    extraDecisions.Add($"Reconocimiento: el usuario ha dicho qué son {ask.LayerAnswers.Count} capas y {ask.BlockAnswers.Count} bloques " +
                                       (err.Length == 0 ? $"(guardado en {UserRules.DefaultPath} para los siguientes planos)." : $"(no se pudo guardar: {err})."));
                    // Capas que el usuario dice que son luminarias dibujadas con líneas: se vuelve a leer el plano para recogerlas.
                    if (ask.FixtureLayerAnswers.Count > 0)
                    {
                        try
                        {
                            drawing = CadReader.Read(path, new CadReader.Options { ReadLinesInsideBlocks = PlanDefaults.ReadLinesInsideBlocks });
                            LayerPlanning.FixImplausibleUnits(drawing);
                        }
                        catch (Exception ex) { extraDecisions.Add("No se pudo volver a leer el plano para las capas de luminarias: " + ex.Message); }
                    }
                }
                int pending = unknownLayers.Count(x => UserRules.Current.LayerRule(x.Layer) is null) +
                              unknownBlocks.Count(x => UserRules.Current.BlockRule(x.Block) is null);
                if (pending > 0)
                    extraDecisions.Add($"Sin reconocer (no se modelan; se preguntará la próxima vez): " +
                        string.Join(", ", unknownLayers.Where(x => UserRules.Current.LayerRule(x.Layer) is null).Select(x => $"capa '{x.Layer}'")
                            .Concat(unknownBlocks.Where(x => UserRules.Current.BlockRule(x.Block) is null).Select(x => $"bloque '{x.Block}'")).Take(30)));
            }
        }

        // ---- DWF del mismo plano (OPCIONAL, solo donde se usan los rótulos de medida): sus textos se suman a los del
        //      DWG sin duplicar, situados con la matriz que trae el propio DWF. Cancelar = seguir solo con el DWG.
        string? dwfPath = null;
        if (NeedsCurveTypes)
        {
            string guess = Path.ChangeExtension(path, ".dwf");
            using var dlgDwf = new OpenFileDialog
            {
                Title = "CAD-REV · (opcional) DWF del mismo plano para leer sus textos de medida — pulse Cancelar si no lo tiene",
                Filter = "DWF (*.dwf)|*.dwf",
                CheckFileExists = true,
                InitialDirectory = Path.GetDirectoryName(path) ?? "",
                FileName = File.Exists(guess) ? Path.GetFileName(guess) : ""
            };
            if (dlgDwf.ShowDialog(owner) == DialogResult.OK) dwfPath = dlgDwf.FileName;
        }
        extraDecisions.AddRange(MergeDwf(drawing, dwfPath));
        (string Name, ElementId Id, double ElevationFt) level;
        string levelChoice;
        if (catalog.Levels.Count == 1)
        {
            level = catalog.Levels[0];
            levelChoice = $"Planta: el proyecto tiene un único nivel ('{level.Name}'); no se pregunta.";
        }
        else
        {
            using var picker = new LevelPickerForm(catalog.Levels, Path.GetFileName(path));
            if (picker.ShowDialog(owner) != DialogResult.OK || picker.Selected is null) return Result.Cancelled;
            level = picker.Selected.Value;
            levelChoice = $"Planta: lista = niveles de Revit ({catalog.Levels.Count}); elegida por el usuario: '{level.Name}'.";
        }

        // ---- Luminarias dibujadas con líneas (sin bloque), como las exportan DIALux, Litestar y similares: se reconocen
        //      y se añaden como bloques equivalentes. Si el archivo trae varias plantas en bloques, solo la del nivel.
        if (Scope is ModelScope.Luminarias)
        {
            double fk = DrawingUnits.ToMillimeters(drawing.HeaderUnit ?? DrawingUnit.Milimetros);
            var fixtures = LooseFixtures.Find(drawing, fk);
            var groups = LooseFixtures.Groups(fixtures);
            if (groups.Count > 1)
            {
                string? chosen = LooseFixtures.GroupForLevel(groups, level.Name);
                string how = $"es la que se llama como el nivel '{level.Name}'";
                if (chosen is null)
                {
                    using var pick = new ChoiceForm("CAD-REV · ¿Qué planta del plano va en este nivel?",
                        $"El plano '{Path.GetFileName(path)}' tiene {groups.Count} plantas metidas en bloques, cada una con sus luminarias. " +
                        $"Elija la que corresponde al nivel '{level.Name}' de Revit.",
                        groups.Select(g => $"{g}  ({fixtures.Count(f => f.Group == g)} luminarias)").ToList());
                    if (pick.ShowDialog(owner) != DialogResult.OK || pick.SelectedIndex < 0) return Result.Cancelled;
                    chosen = groups[pick.SelectedIndex];
                    how = "elegida por el usuario";
                }
                fixtures = fixtures.Where(f => f.Group.Length == 0 || f.Group == chosen).ToList();
                extraDecisions.Add($"Plantas en bloques: el plano trae {groups.Count} ({string.Join(", ", groups.Select(g => "'" + g + "'"))}); " +
                                   $"se colocan solo las luminarias de '{chosen}' ({how}).");
            }
            if (fixtures.Count > 0)
            {
                LooseFixtures.AddAsBlocks(drawing, fixtures, fk);
                extraDecisions.Add(LooseFixtures.Summary(fixtures));
            }
        }

        // Todo (preparar familias y uniones + modelar) en un grupo: un solo Ctrl+Z lo deshace.
        using var group = new TransactionGroup(doc, "CAD-REV: " + Title.ToLowerInvariant());
        group.Start();
        UnderSlabPlacer? placer = null;
        BuildReport report;
        AutoPlanResult auto;
        BuildPlan plan;
        var preparation = new List<string>();
        if (Scope is ModelScope sc0)
            preparation.Add($"Botón «{ModelScopes.Label(sc0)}»: solo se modela lo suyo; el resto del plano lo modelan los otros botones (el informe dice cuál).");
        using (var progress = new ProgressWindow(Title))
        {
            progress.Show(owner);
            Cursor.Current = Cursors.WaitCursor;
            try
            {
                // ---- Preparar el proyecto: piezas de unión de los tipos y familias de los bloques (de la biblioteca).
                progress.Report("Buscando en la biblioteca las piezas de unión y las familias de los bloques…");
                var stepClock = System.Diagnostics.Stopwatch.StartNew();
                var library = PrepareProject(commandData.Application.Application.VersionNumber, doc, drawing, catalog, preparation);
                catalog = RevitCatalog.Load(doc);
                double prepareS = stepClock.Elapsed.TotalSeconds; stepClock.Restart();
                foreach (var (block, family) in _fallback) catalog.FamilyFallback[block] = family;

                // ---- Plan con la configuración existente (lo que el asistente proponía), sin ventanas
                auto = AutoPlanner.Create(drawing, catalog, level, Scope);
                if (auto.Error != null || auto.Plan is null)
                {
                    group.RollBack();
                    progress.Close();
                    Messages.Error(auto.Error ?? "No hay nada que modelar.",
                        Scope is ModelScope sc1
                            ? $"Compruebe que eligió el plano de {ModelScopes.Label(sc1).ToLowerInvariant()} y que sus capas o bloques tienen nombres reconocibles. " +
                              "Lo que hay en el plano y qué botón lo modela está en el detalle."
                            : "Compruebe que las capas del plano tienen nombres de instalaciones (por ejemplo 'FONTANERIA', 'CLIMA', 'SANEAMIENTO') " +
                              "y que el proyecto tiene tipos de tubería y conducto.",
                        string.Join("\n", auto.SkippedLayers.Select(k => $"Capa '{k.Key}': {k.Value}")
                            .Concat(auto.SkippedBlocks.Take(40).Select(k => $"Bloque '{BlockNames.Clean(k.Key.Block)}': {k.Value}"))));
                    return Result.Cancelled;
                }
                plan = auto.Plan;
                if (Scope is ModelScope.Tuberias or ModelScope.Conductos && plan.Network.Segments.Count > 0)
                {
                    var typeIds = plan.Network.Segments.Select(s => plan.SegmentPipeTypes.GetValueOrDefault(s.Index)
                        ?? plan.SegmentDuctTypes.GetValueOrDefault(s.Index) ?? plan.Layers[s.Layer].TypeId)
                        .OfType<ElementId>();
                    using var fittings = new Transaction(doc, "CAD-REV: preparar las uniones de los tipos seleccionados");
                    var handling = fittings.GetFailureHandlingOptions();
                    handling.SetFailuresPreprocessor(new WarningDismisser(new BuildReport()));
                    fittings.SetFailureHandlingOptions(handling);
                    fittings.Start();
                    preparation.AddRange(FittingSetup.Complete(doc, typeIds, library, Scope));
                    if (fittings.Commit() != TransactionStatus.Committed)
                        preparation.Add("Revit anuló la preparación de preferencias de enrutamiento de los tipos seleccionados.");
                }
                double planS = stepClock.Elapsed.TotalSeconds; stepClock.Restart();
                // Encima del CAD vinculado de este mismo archivo, si está en el proyecto y encaja con el dibujo.
                progress.Report("Situando el plano sobre el CAD del proyecto…");
                var (cadT, cadNote, cadPresent) = CadPlacement.Find(doc, path, drawing, auto.UnitToMm);
                plan.CadToProject = cadT;
                // Sin el plano en el proyecto: se vincula (en su sitio; o encima del CAD con el que coincide; o centro a
                // centro si está a más de 10 km del origen) y se vuelve a situar encima de él: lo modelado y el CAD coinciden.
                if (!cadPresent)
                {
                    progress.Report("Vinculando el plano…");
                    bool far = plan.TranslationMm.Length > 0 && cadT is null;
                    var (linkNote, linked) = CadPlacement.LinkInPlace(doc, path, auto.Unit, level.Id, centered: far, place: cadT);
                    preparation.Add(linkNote);
                    if (linked && cadT is null)
                    {
                        var (t2, note2, _) = CadPlacement.Find(doc, path, drawing, auto.UnitToMm);
                        cadNote = note2;
                        if (t2 != null) plan.CadToProject = t2;
                        else if (far)
                            preparation.Add("AVISO: el plano vinculado centro a centro no se ha podido situar; lo modelado está acercado al origen " +
                                            $"({plan.TranslationMm.X / 1000:0.###} m, {plan.TranslationMm.Y / 1000:0.###} m) y puede no coincidir con él.");
                    }
                }
                preparation.Add(cadNote);
                preparation.Add($"Tiempos antes de modelar: leer el plano {readClock.Elapsed.TotalSeconds:0.0} s; preparar el proyecto (biblioteca de familias y piezas de unión) " +
                                $"{prepareS:0.0} s; calcular tramos y uniones {planS:0.0} s; situar sobre el CAD vinculado {stepClock.Elapsed.TotalSeconds:0.0} s.");

                // ---- Modelado con el motor de CadMepRevit2 (el que modela bien): eje a altura fija, tramos → uniones → bloques.
                // La regla «bajo_forjado» y el modelado por sistemas de la versión anterior NO se usan (ver Config/PlacementConfig).
                progress.Report($"Plano leído: {drawing.Entities.Count} entidades. Modelando {plan.Network.Segments.Count} tramos…");
                report = MepBuilder.Build(doc, plan);
            }
            catch (Exception ex)
            {
                if (group.HasStarted() && !group.HasEnded()) group.RollBack();
                progress.Close();
                Messages.Error("El modelado se detuvo y no se ha guardado ningún cambio.",
                    "Revise el mensaje técnico. Causas habituales: el tipo de tubería no tiene segmento o tamaños en sus preferencias de enrutamiento, o el nivel fue borrado.",
                    ex.ToString());
                return Result.Failed;
            }
            finally { Cursor.Current = Cursors.Default; }
        }
        if (report.Status != TransactionStatus.Committed)
        {
            group.RollBack();
            string cancelFile = ImportCadCommand.SaveReport(doc, plan, report,
                "REVIT CANCELÓ EL MODELADO (no se guardó ningún cambio). Estado: " + report.Status + "\n" + string.Join("\n", preparation));
            Messages.Error("Revit canceló el modelado y no se ha guardado ningún cambio.",
                "Puede deberse a errores graves de Revit (no avisos). Revise el detalle, corrija el CAD o los tipos y repita." +
                (cancelFile.Length > 0 ? "\nInforme guardado en: " + cancelFile : ""),
                string.Join("\n", report.Problems.Take(100)));
            return Result.Cancelled;
        }
        group.Assimilate();
        extraDecisions.AddRange(preparation);

        var existing = report.CreatedIds.Where(id => doc.GetElement(id) != null).ToList();
        try { uidoc.Selection.SetElementIds(existing); } catch { /* la selección es solo una ayuda */ }

        // ---- Inventario cruzado con lo modelado e informe.
        // El modelo ya está creado: un fallo aquí no lo deshace, pero se explica.
        InventoryResult inventory;
        string text, reportFile;
        try
        {
            extraDecisions.Add("Alto de los muros: la distancia del nivel ELEGIDO al nivel superior (el asistente anterior proponía la del nivel " +
                               "más bajo aunque se eligiera otro; con el nivel más bajo coinciden).");
            inventory = ModelInventory.Classify(ModelReport.InventoryInput(drawing, auto, plan, report));
            text = ModelReport.ForModel(doc, drawing, auto, plan, report, inventory, placer, levelChoice, extraDecisions);
            reportFile = ImportCadCommand.SaveReport(doc, plan, report, null, text);
        }
        catch (Exception ex)
        {
            string file = ImportCadCommand.SaveReport(doc, plan, report,
                "EL MODELO SE CREÓ, PERO FALLÓ EL INFORME:\n" + ex + "\n" + string.Join("\n", preparation));
            Messages.Error("El modelo se ha creado, pero no se pudo completar el informe.",
                "Los elementos están en el modelo (Ctrl+Z los deshace)." +
                (file.Length > 0 ? "\nDetalle técnico guardado en: " + file : ""), ex.ToString());
            return Result.Succeeded;
        }

        Messages.Info(Title + ": terminado",
            report.Summary() +
            $"\nInventario del plano: {inventory.Total} entidades = {inventory.Modeled} modeladas + {inventory.NotModeled} no modeladas " +
            (inventory.Balanced ? "(cuadra)." : "(NO cuadra: revise el informe).") +
            (report.SlabWarnings.Count > 0 ? $"\n⚠ {report.SlabWarnings.Count} redes sin forjado encima (quedan a la altura anterior)." : "") +
            (report.HoleWarnings.Count > 0 ? $"\n⚠ {report.HoleWarnings.Count} tramos con huecos del forjado encima." : "") +
            (report.CeilingElbowFailures > 0 ? $"\n⚠ {report.CeilingElbowFailures} subidas o bajadas bajo el techo sin codos (vea el informe)." : "") +
            (NeedsCurveTypes
                ? (Scope == ModelScope.Tuberias
                    ? "\n\nRevise PENDIENTES, UNIONES y las cotas SUPUESTAS en el informe. Los encuentros de cuatro tubos quedan para el técnico. Ctrl+Z deshace todo."
                    : "\n\nLas cotas y los rótulos de tamaño asociados se aplican al modelar. Ctrl+Z deshace todo el modelado.")
                : "\n\nDónde están: quedan SELECCIONADOS. Las luminarias y detectores de techo están a la cota del falso techo o del forjado " +
                  "de encima, por encima del plano de corte de la planta: véalos en una vista 3D o en el plano de techo. El resto de alturas " +
                  "son valores habituales SUPUESTOS (vea el informe). Ctrl+Z deshace todo.") +
            (reportFile.Length > 0 ? "\nInforme completo: " + reportFile : ""),
            text.Length > 12000 ? text[..12000] + "\n… (continúa en el archivo de informe)" : text);
        return Result.Succeeded;
    }

    /// <summary>
    /// Suma al plano los textos del DWF que el DWG no tiene. Devuelve lo que hay que contar en el informe.
    /// </summary>
    private static List<string> MergeDwf(CadDrawing drawing, string? dwfPath)
    {
        var notes = new List<string>();
        if (string.IsNullOrEmpty(dwfPath)) { notes.Add("DWF: no se ha dado; se usan los textos del DWG."); return notes; }
        if (!File.Exists(dwfPath)) { notes.Add($"DWF: no se encuentra '{dwfPath}'; se usan solo los textos del DWG."); return notes; }
        var dwf = DwfReader.Read(dwfPath);
        double k = DrawingUnits.ToMillimeters(drawing.HeaderUnit ?? DrawingUnit.Milimetros);
        var (added, _, _, check) = DwfReader.Merge(drawing, dwf, k);
        notes.Add($"DWF '{Path.GetFileName(dwfPath)}': {dwf.Texts.Count} textos leídos; {check}; {added} no estaban en el DWG y se han añadido " +
                  "(los demás ya estaban: no se duplican).");
        notes.AddRange(dwf.Notes);
        return notes;
    }

    /// <summary>Bloques que usan una familia de sustitución (rejilla o difusor genérico), por nombre limpio.</summary>
    private Dictionary<string, string> _fallback = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Antes de modelar: a los tipos de tubería y conducto que se van a usar se les asignan codos, tes, cruces y
    /// transiciones si no tienen (las plantillas «Construcción» o «Arquitectura» no traen ninguna) y se cargan las
    /// familias de los bloques del plano. Todo de la biblioteca de familias (FamilyLibrary). Lo hecho va al informe.
    /// </summary>
    private FamilyLibrary? PrepareProject(string revitYear, Document doc, CadDrawing drawing, RevitCatalog catalog, List<string> log)
    {
        FamilyLibrary? library = null;
        try
        {
            library = FamilyLibrary.Open(revitYear, Array.Empty<string>());
            log.Add($"Biblioteca de familias: {library.FileCount} archivos .rfa en {string.Join("; ", library.Roots)} " +
                    "(lista en %APPDATA%\\CadMepRevit\\bibliotecas.txt).");
            log.AddRange(library.Notes);
        }
        catch (Exception ex) { log.Add("Biblioteca de familias no disponible: " + ex.Message); }

        using var t = new Transaction(doc, "CAD-REV: preparar uniones y familias");
        var options = t.GetFailureHandlingOptions();
        options.SetFailuresPreprocessor(new WarningDismisser(new BuildReport()));
        t.SetFailureHandlingOptions(options);
        t.Start();
        try
        {
            var kinds = drawing.SegmentLayers.Select(MepKinds.Suggest).ToHashSet();
            var ids = new List<ElementId>();
            if (Scope is null && kinds.Contains(MepKind.Tuberia)) ids.AddRange(TypesToComplete(catalog, catalog.Types[MepKind.Tuberia]));
            if (Scope is null && kinds.Contains(MepKind.Conducto))
                foreach (var shape in catalog.DuctShapesAvailable())
                    ids.AddRange(TypesToComplete(catalog, catalog.DuctTypesOf(shape)));
            if (kinds.Contains(MepKind.Bandeja)) ids.AddRange(TypesToComplete(catalog, catalog.Types[MepKind.Bandeja]));
            if (kinds.Contains(MepKind.TuboElectrico) && ModelScopes.AllowsAnyFitting(Scope, MepKind.TuboElectrico))
                ids.AddRange(TypesToComplete(catalog, catalog.Types[MepKind.TuboElectrico]));
            log.AddRange(FittingSetup.Complete(doc, ids, library, Scope));
            if (library != null && drawing.Blocks.Count > 0)
                _fallback = BlockFamilies.Prepare(doc, drawing, catalog, library, log, Scope);
            t.Commit();
        }
        catch
        {
            if (t.GetStatus() == TransactionStatus.Started) t.RollBack();
            throw;
        }
        return library;
    }

    /// <summary>
    /// Salas técnicas (RevitAutomation): modela SIN VENTANAS la red de <paramref name="scope"/> (tuberías o bandejas) del
    /// plano ya leído, con su eje a <paramref name="heightMm"/> sobre el nivel (la altura propia de la sala: aquí NO se
    /// aplica la regla «sigue el techo»), con uniones y conexiones, igual que el botón correspondiente. Los bloques de
    /// equipos (bombas, depósitos, cuadros…) NO se colocan: los modela la sala con su volumen. Hay que llamarlo dentro de
    /// un TransactionGroup abierto (lo confirma o deshace quien llama). Devuelve el informe de construcción (null si no
    /// había nada que modelar) y las notas para el informe de la sala.
    /// </summary>
    public static (BuildReport? Report, List<string> Notes) BuildWithoutUi(string revitYear, Document doc, string path,
        CadDrawing drawing, Level level, ModelScope scope, double heightMm, Transform? fallbackPlacement)
    {
        var notes = new List<string>();
        var runner = new ScopedRun(scope);
        var catalog = RevitCatalog.Load(doc);
        UserRules.Current = UserRules.Load();
        var library = runner.PrepareProject(revitYear, doc, drawing, catalog, notes);
        catalog = RevitCatalog.Load(doc);
        foreach (var (block, family) in runner._fallback) catalog.FamilyFallback[block] = family;
        var auto = AutoPlanner.Create(drawing, catalog, (level.Name, level.Id, level.ProjectElevation), scope);
        if (auto.Error != null || auto.Plan is null)
        {
            notes.Add($"{ModelScopes.Label(scope)}: nada que modelar ({auto.Error ?? "sin capas reconocidas"}).");
            notes.AddRange(auto.SkippedLayers.Take(20).Select(k => $"  Capa '{k.Key}': {k.Value}"));
            return (null, notes);
        }
        var plan = auto.Plan;
        int equipment = plan.Blocks.RemoveAll(b => b.Block.Role == BlockRole.Equipo);
        plan.BlockPlans.RemoveAll(b => BlockNames.Role(b.BlockName, b.Layer) == BlockRole.Equipo);
        if (equipment > 0) notes.Add($"{equipment} bloques de equipo no se colocan como familia: la sala los modela con su volumen.");
        // Altura propia de la sala: eje a heightMm. Las redes de tubería con altura SUPUESTA (sin cota en el plano) se
        // suben o bajan enteras a esa altura; las que tienen cota o pendiente en el plano la conservan.
        plan.HeightAboveLevelMm = heightMm;
        plan.FollowCeiling = false;
        if (plan.Slopes is { } slopes)
            foreach (var net in slopes.Networks.Where(n => n.AssumedSuspended))
            {
                double shift = heightMm - net.HighestMm;
                foreach (int i in net.Segments.Where(slopes.Pipes.ContainsKey))
                {
                    var sp = slopes.Pipes[i];
                    slopes.Pipes[i] = sp with
                    {
                        Start = new P3(sp.Start.X, sp.Start.Y, sp.Start.Z + shift),
                        End = new P3(sp.End.X, sp.End.Y, sp.End.Z + shift)
                    };
                }
            }
        // Uniones de los tipos que va a usar el plan (como hace el botón).
        var typeIds = plan.Network.Segments.Select(s => plan.SegmentPipeTypes.GetValueOrDefault(s.Index)
            ?? plan.SegmentDuctTypes.GetValueOrDefault(s.Index) ?? plan.Layers[s.Layer].TypeId).OfType<ElementId>().ToList();
        if (typeIds.Count > 0)
        {
            using var fittings = new Transaction(doc, "CAD-REV: preparar las uniones de los tipos seleccionados");
            var handling = fittings.GetFailureHandlingOptions();
            handling.SetFailuresPreprocessor(new WarningDismisser(new BuildReport()));
            fittings.SetFailureHandlingOptions(handling);
            fittings.Start();
            notes.AddRange(FittingSetup.Complete(doc, typeIds, library, scope));
            fittings.Commit();
        }
        var (cadT, cadNote, _) = CadPlacement.Find(doc, path, drawing, auto.UnitToMm);
        if (cadT is null && fallbackPlacement is not null)
        {
            cadT = fallbackPlacement;
            cadNote += " Salas técnicas: se usa la posición del CAD vinculado en la vista, igual que los equipos.";
        }
        plan.CadToProject = cadT;
        notes.Add(cadNote);
        var report = MepBuilder.Build(doc, plan);
        notes.Add($"{ModelScopes.Label(scope)}: {report.Summary()}");
        notes.AddRange(report.Problems.Take(30).Select(p => "  " + p));
        return (report, notes);
    }

    /// <summary>Instancia de un botón concreto para <see cref="BuildWithoutUi"/>.</summary>
    private sealed class ScopedRun : ModelFromCadCommand
    {
        private readonly ModelScope _scope;
        public ScopedRun(ModelScope scope) { _scope = scope; }
        protected override ModelScope? Scope => _scope;
    }

    /// <summary>Si ningún tipo de la lista tiene todas sus uniones, el primero (el que elegirá el plan) se completa.</summary>
    private static IEnumerable<ElementId> TypesToComplete(RevitCatalog catalog, List<NamedId> types)
    {
        if (types.Count == 0 || types.Any(x => catalog.FittingWarning(x.Name, x.Id) is null)) yield break;
        yield return types[0].Id;
    }

}

// ---- Un botón por instalación. Revit crea cada comando por su clase: cada una solo cambia lo que modela.

/// <summary>Botón «Tuberías»: cotas, pendientes, codos, tes, reducciones y accesorios.</summary>
[Transaction(TransactionMode.Manual)]
[Regeneration(RegenerationOption.Manual)]
public sealed class ModelPipesCommand : ModelFromCadCommand { protected override ModelScope? Scope => ModelScope.Tuberias; }

/// <summary>Botón «Conductos»: conductos, uniones, terminales y equipos de aire.</summary>
[Transaction(TransactionMode.Manual)]
[Regeneration(RegenerationOption.Manual)]
public sealed class ModelDuctsCommand : ModelFromCadCommand { protected override ModelScope? Scope => ModelScope.Conductos; }

/// <summary>Botón «Bandejas»: bandejas de cables y tubos eléctricos, los dos con codos, tes, cruces y reducciones.</summary>
[Transaction(TransactionMode.Manual)]
[Regeneration(RegenerationOption.Manual)]
public sealed class ModelTraysCommand : ModelFromCadCommand { protected override ModelScope? Scope => ModelScope.Bandejas; }

/// <summary>Botón «Luminarias»: luminarias, interruptores y detectores de presencia.</summary>
[Transaction(TransactionMode.Manual)]
[Regeneration(RegenerationOption.Manual)]
public sealed class ModelLightingCommand : ModelFromCadCommand { protected override ModelScope? Scope => ModelScope.Luminarias; }

/// <summary>Botón «Equipos y PCI»: extintores, BIE, detectores, pulsadores, sirenas, señales, cuadros, tomas y dispositivos.</summary>
[Transaction(TransactionMode.Manual)]
[Regeneration(RegenerationOption.Manual)]
public sealed class ModelEquipmentCommand : ModelFromCadCommand { protected override ModelScope? Scope => ModelScope.EquiposPci; }
