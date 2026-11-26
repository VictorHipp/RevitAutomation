using Autodesk.Revit.Attributes;
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Plumbing;
using Autodesk.Revit.UI;
using View = Autodesk.Revit.DB.View;
using TaskDialog = Autodesk.Revit.UI.TaskDialog;
using CadMepRevit.Core;
using RevitAutomation.Addin.Commands.Regiones;

namespace RevitAutomation.Addin.Commands.SalasTecnicas;

/// <summary>
/// Botones del desplegable «Salas técnicas». Automático, sin ventanas de opciones: usa el CAD vinculado de la vista en
/// planta activa (el seleccionado, el único o el que se pinche), modela los equipos de la sala como volúmenes (cajas o
/// cilindros según el contorno dibujado), sus tuberías o bandejas con el motor CAD (CadMep) a la altura de la sala, y
/// baja las tuberías que acaban sobre un depósito o un grupo hasta él. Todo en un TransactionGroup: un Ctrl+Z lo deshace.
/// Al repetirlo sustituye solo los equipos que creó este botón sobre ese CAD. Los valores (claves, alturas) están en
/// Documentos\RevitAutomation\SalasTecnicas\perfil-*.json y se pueden editar.
/// </summary>
public abstract class SalaTecnicaCommand : IExternalCommand
{
    internal const string AppId = "RevitAutomation.SalasTecnicas";
    protected abstract SalaTipo Tipo { get; }

    private string Title => "Salas técnicas · " + SalaPerfiles.Defecto(Tipo).Nombre;

    public Result Execute(ExternalCommandData data, ref string message, ElementSet elements)
    {
        UIDocument? ui = data.Application.ActiveUIDocument;
        if (ui is null || ui.Document.IsFamilyDocument)
        {
            Show("Abra un proyecto de Revit.", "Este botón trabaja en proyectos (.rvt), no en familias.");
            return Result.Cancelled;
        }
        Document doc = ui.Document;
        if (doc.IsReadOnly)
        {
            Show("El proyecto está abierto en solo lectura.", "Guarde una copia editable o ábralo con permisos de escritura.");
            return Result.Cancelled;
        }
        if (ui.ActiveView is not ViewPlan view || view.IsTemplate || view.GenLevel is not Level level)
        {
            Show("Abra la vista en planta de la sala técnica.",
                "El botón usa el CAD vinculado en la vista en planta activa y modela en su nivel. Vista activa: «" + ui.ActiveView?.Name + "».");
            return Result.Cancelled;
        }

        // 1 · El CAD de la sala: el seleccionado, el único de la vista o el que se pinche.
        var links = new FilteredElementCollector(doc, view.Id).OfClass(typeof(ImportInstance)).Cast<ImportInstance>()
            .OrderByDescending(i => i.IsLinked).ToList();
        if (links.Count == 0)
        {
            Show("No se ve ningún CAD vinculado en la vista «" + view.Name + "».",
                "Vincule el DWG de la sala con Insertar > Vincular CAD en esta planta (posición: automático o el que corresponda) " +
                "y vuelva a pulsar el botón. No se ha modificado nada.");
            return Result.Cancelled;
        }
        ImportInstance? link = ui.Selection.GetElementIds().Select(doc.GetElement).OfType<ImportInstance>()
            .FirstOrDefault(i => links.Any(l => l.Id == i.Id));
        if (link is null && links.Count == 1) link = links[0];
        if (link is null)
        {
            try
            {
                var picked = ui.Selection.PickObject(Autodesk.Revit.UI.Selection.ObjectType.Element, new CadLinkFilter(),
                    $"{Title}: hay {links.Count} CAD en la vista. Pinche el plano de la sala (Esc para cancelar).");
                link = doc.GetElement(picked) as ImportInstance;
            }
            catch (Autodesk.Revit.Exceptions.OperationCanceledException) { return Result.Cancelled; }
            if (link is null) return Result.Cancelled;
        }

        var notas = new List<string>();
        var (perfil, notaPerfil) = SalaSettingsStore.Load(Tipo);
        notas.Add(notaPerfil);

        // 2 · Lectura (no cambia nada): contornos desde la geometría del vínculo; textos y bloques desde el archivo.
        RegionSource src;
        try { src = RegionReader.Read(doc, view, link); }
        catch (Exception ex)
        {
            Show("No se pudo leer la geometría del CAD. No se ha modificado nada.",
                "Recargue el vínculo (Insertar > Gestionar vínculos > Volver a cargar) y repita.", ex.ToString());
            return Result.Failed;
        }
        var entrada = new SalaEntrada();
        foreach (var loop in src.Scan.Loops)
            entrada.Contornos.Add(new SalaContorno
            {
                Id = entrada.Contornos.Count, Capa = loop.Layer,
                Puntos = loop.Outline.Select(p => new Pt(p.X * 304.8, p.Y * 304.8)).ToList()
            });

        string? path = LinkPath(doc, link);
        CadDrawing? drawing = null;
        Transform linkT = link.GetTotalTransform();
        if (path is null)
            notas.Add("El CAD está importado (no vinculado) o su archivo no se encuentra: se usan solo sus capas (sin rótulos) y no se modelan " +
                      (perfil.Red == RedSala.Bandejas ? "bandejas." : "tuberías."));
        else
        {
            try
            {
                drawing = CadReader.Read(path, new CadReader.Options { ReadLinesInsideBlocks = false });
                double k = DrawingUnits.ToMillimeters(drawing.HeaderUnit ?? DrawingUnit.Milimetros);
                Pt ToProject(double x, double y)
                {
                    var q = linkT.OfPoint(new XYZ(x * k / 304.8, y * k / 304.8, 0));
                    return new Pt(q.X * 304.8, q.Y * 304.8);
                }
                foreach (var t in drawing.Texts)
                    entrada.Textos.Add(new SalaTexto { Pos = ToProject(t.Center.X, t.Center.Y), Valor = t.Value, Capa = t.Layer });
                double angle = Math.Atan2(linkT.BasisX.Y, linkT.BasisX.X);
                foreach (var b in drawing.Blocks)
                {
                    if (b.Footprint is not { } f) continue;
                    entrada.Bloques.Add(new SalaBloque
                    {
                        Nombre = b.BlockName, Capa = b.Layer, Centro = ToProject(f.CenterX, f.CenterY),
                        AnchoMm = f.SizeX * k, FondoMm = f.SizeY * k, AnguloRad = f.AngleRad + angle
                    });
                }
            }
            catch (Exception ex)
            {
                drawing = null;
                notas.Add($"No se pudo leer el archivo del CAD ({ex.Message}): se usan solo sus capas y no se modela la red.");
            }
        }

        // 3 · Qué es cada cosa.
        SalaPlan plan = SalaClasificador.Clasificar(perfil, entrada);
        bool hayRed = drawing != null && perfil.Red != RedSala.Ninguna;
        if (plan.Equipos.Count == 0 && !hayRed)
        {
            Show("No se ha reconocido nada que modelar en el CAD. No se ha modificado nada.",
                string.Join("\n", plan.Avisos), SalaReport.Texto(plan, RegionReader.LinkName(doc, link), level.Name, notas));
            return Result.Cancelled;
        }

        // 4 · Modelar (todo en un grupo: un Ctrl+Z).
        var creados = new List<(EquipoPlan Equipo, ElementId Id)>();
        int bajantes = 0, borrados = 0;
        using var group = new TransactionGroup(doc, Title);
        group.Start();
        try
        {
            using (var t = new Transaction(doc, Title + ": equipos"))
            {
                t.Start();
                borrados = DeletePrevious(doc, link, view);
                foreach (var e in plan.Equipos)
                {
                    try { creados.Add((e, CreateEquipment(doc, level, e))); }
                    catch (Exception ex) { notas.Add($"No se pudo crear «{e.Etiqueta}»: {ex.Message}"); }
                }
                t.Commit();
            }
            if (borrados > 0) notas.Add($"Se han sustituido {borrados} equipos creados antes por este botón sobre este CAD.");

            if (hayRed && path != null)
            {
                var scope = perfil.Red == RedSala.Bandejas ? ModelScope.Bandejas : ModelScope.Tuberias;
                var (report, redNotas) = CadMepRevit.Commands.ModelFromCadCommand.BuildWithoutUi(
                    data.Application.Application.VersionNumber, doc, path, drawing!, level,
                    scope, perfil.AlturaRedMm, linkT);
                notas.AddRange(redNotas);
                if (report != null && perfil.Red == RedSala.Tuberias)
                {
                    var pipes = report.CurvesCreated.Where(c => c.Kind == MepKind.Tuberia).Select(c => c.Id).ToList();
                    bajantes = DropsToEquipment(doc, level, pipes, plan.Equipos, notas);
                }
            }
            group.Assimilate();
        }
        catch (Exception ex)
        {
            if (group.HasStarted() && !group.HasEnded()) group.RollBack();
            message = ex.ToString();
            Show("El modelado se detuvo y no se ha guardado ningún cambio.", ex.Message, ex.ToString());
            return Result.Failed;
        }

        // 5 · Informe.
        if (bajantes > 0) notas.Add($"{bajantes} tuberías bajadas hasta su equipo (depósito, acumulador, grupo…).");
        string texto = SalaReport.Texto(plan, RegionReader.LinkName(doc, link), level.Name, notas);
        string archivo = SaveReport(doc, texto, SalaReport.Csv(plan));
        try { ui.Selection.SetElementIds(creados.Select(c => c.Id).ToList()); } catch { /* solo ayuda */ }
        Show($"{Title}: {creados.Count} equipos modelados" + (bajantes > 0 ? $", {bajantes} bajadas a equipos" : "") + ".",
            (plan.Equipos.Any(e => e.AltoSupuesto) ? "Las alturas no indicadas en el plano son valores habituales SUPUESTOS: revíselos en 3D. " : "") +
            "Los equipos quedan seleccionados. Ctrl+Z deshace todo." + (archivo.Length > 0 ? "\nInforme: " + archivo : ""),
            texto.Length > 12000 ? texto[..12000] + "\n…" : texto);
        return Result.Succeeded;
    }

    /// <summary>Ruta del archivo de un CAD vinculado (null si está importado o no existe).</summary>
    private static string? LinkPath(Document doc, ImportInstance link)
    {
        try
        {
            if (!link.IsLinked) return null;
            var typeId = link.GetTypeId();
            if (!ExternalFileUtils.IsExternalFileReference(doc, typeId)) return null;
            var model = ExternalFileUtils.GetExternalFileReference(doc, typeId).GetAbsolutePath();
            string p = ModelPathUtils.ConvertModelPathToUserVisiblePath(model);
            return File.Exists(p) ? p : null;
        }
        catch { return null; }
    }

    private static double Ft(double mm) => mm / 304.8;

    /// <summary>Borra los equipos creados antes por este botón cuyo centro cae dentro del CAD elegido.</summary>
    private int DeletePrevious(Document doc, ImportInstance link, View view)
    {
        var box = link.get_BoundingBox(null) ?? link.get_BoundingBox(view);
        if (box is null) return 0;
        string prefix = Tipo + "|";
        var ids = new FilteredElementCollector(doc).OfClass(typeof(DirectShape)).Cast<DirectShape>()
            .Where(d => d.ApplicationId == AppId && (d.ApplicationDataId ?? "").StartsWith(prefix, StringComparison.Ordinal))
            .Where(d => d.get_BoundingBox(null) is { } b &&
                        (b.Min.X + b.Max.X) / 2 is var x && (b.Min.Y + b.Max.Y) / 2 is var y &&
                        x >= box.Min.X && x <= box.Max.X && y >= box.Min.Y && y <= box.Max.Y)
            .Select(d => d.Id).ToList();
        if (ids.Count > 0) doc.Delete(ids);
        return ids.Count;
    }

    private ElementId CreateEquipment(Document doc, Level level, EquipoPlan e)
    {
        double z0 = level.ProjectElevation + Ft(e.ElevacionMm);
        CurveLoop loop;
        if (e.Forma == EquipoForma.Cilindro)
        {
            var c = new XYZ(Ft(e.Centro.X), Ft(e.Centro.Y), z0);
            double r = Ft(e.RadioMm);
            loop = new CurveLoop();
            loop.Append(Arc.Create(c, r, 0, Math.PI, XYZ.BasisX, XYZ.BasisY));
            loop.Append(Arc.Create(c, r, Math.PI, 2 * Math.PI, XYZ.BasisX, XYZ.BasisY));
        }
        else
        {
            double tol = doc.Application.ShortCurveTolerance;
            var pts = new List<XYZ>();
            foreach (var p in e.Contorno)
            {
                var q = new XYZ(Ft(p.X), Ft(p.Y), z0);
                if (pts.Count == 0 || pts[^1].DistanceTo(q) > tol) pts.Add(q);
            }
            while (pts.Count > 3 && pts[^1].DistanceTo(pts[0]) <= tol) pts.RemoveAt(pts.Count - 1);
            if (pts.Count < 3) throw new InvalidOperationException("contorno sin puntos suficientes");
            loop = new CurveLoop();
            for (int i = 0; i < pts.Count; i++) loop.Append(Line.CreateBound(pts[i], pts[(i + 1) % pts.Count]));
        }
        Solid solid = GeometryCreationUtilities.CreateExtrusionGeometry(new[] { loop }, XYZ.BasisZ, Math.Max(Ft(e.AltoMm), Ft(10)));

        var category = new ElementId(e.Tipo.Categoria switch
        {
            EquipoCategoria.Fontaneria => BuiltInCategory.OST_PlumbingFixtures,
            EquipoCategoria.Electrico => BuiltInCategory.OST_ElectricalEquipment,
            EquipoCategoria.Generico => BuiltInCategory.OST_GenericModel,
            _ => BuiltInCategory.OST_MechanicalEquipment
        });
        if (!DirectShape.IsValidCategoryId(category, doc)) category = new ElementId(BuiltInCategory.OST_GenericModel);
        var ds = DirectShape.CreateElement(doc, category);
        ds.ApplicationId = AppId;
        ds.ApplicationDataId = Tipo + "|" + e.Tipo.Id;
        ds.SetShape(new GeometryObject[] { solid });
        ds.get_Parameter(BuiltInParameter.ALL_MODEL_INSTANCE_COMMENTS)?.Set(
            $"Sala técnica · {SalaPerfiles.Defecto(Tipo).Nombre} · {e.Tipo.Nombre} · {e.Etiqueta}" + (e.AltoSupuesto ? " · alto supuesto" : ""));
        return ds.Id;
    }

    /// <summary>
    /// Tuberías cuyo extremo libre cae sobre un equipo con conexión (arriba o a media altura): se prolongan en vertical hasta
    /// él con un codo. Lo que Revit no admite se deja como está y se anota.
    /// </summary>
    private static int DropsToEquipment(Document doc, Level level, List<ElementId> pipeIds, List<EquipoPlan> equipos, List<string> notas)
    {
        var targets = equipos.Where(e => e.Tipo.Conexion != EquipoConexion.Ninguna).ToList();
        if (targets.Count == 0 || pipeIds.Count == 0) return 0;
        int done = 0, failed = 0;
        using var t = new Transaction(doc, "Salas técnicas: bajadas a equipos");
        t.Start();
        foreach (var id in pipeIds)
        {
            if (doc.GetElement(id) is not Pipe pipe) continue;
            foreach (Connector c in pipe.ConnectorManager.Connectors.Cast<Connector>().ToList())
            {
                if (c.ConnectorType != ConnectorType.End || c.IsConnected) continue;
                var o = c.Origin;
                var target = targets.FirstOrDefault(e => Inside(e, o.X * 304.8, o.Y * 304.8, 150));
                if (target is null) continue;
                double topMm = target.ElevacionMm + (target.Tipo.Conexion == EquipoConexion.Arriba ? target.AltoMm : target.AltoMm / 2);
                double zEnd = level.ProjectElevation + Ft(topMm);
                if (o.Z - zEnd < Ft(100)) continue;
                try
                {
                    var systemId = pipe.get_Parameter(BuiltInParameter.RBS_PIPING_SYSTEM_TYPE_PARAM)?.AsElementId() ?? ElementId.InvalidElementId;
                    var drop = Pipe.Create(doc, systemId, pipe.GetTypeId(), level.Id, o, new XYZ(o.X, o.Y, zEnd));
                    drop.get_Parameter(BuiltInParameter.RBS_PIPE_DIAMETER_PARAM)?.Set(pipe.Diameter);
                    var top = drop.ConnectorManager.Connectors.Cast<Connector>().OrderBy(x => x.Origin.DistanceTo(o)).First();
                    try { doc.Create.NewElbowFitting(c, top); }
                    catch { c.ConnectTo(top); }
                    done++;
                }
                catch (Exception) { failed++; }
            }
        }
        t.Commit();
        if (failed > 0) notas.Add($"{failed} tuberías sobre un equipo no se pudieron bajar hasta él (quedan a la altura de la red).");
        return done;
    }

    private static bool Inside(EquipoPlan e, double x, double y, double margin)
    {
        if (e.Forma == EquipoForma.Cilindro)
            return Math.Sqrt((x - e.Centro.X) * (x - e.Centro.X) + (y - e.Centro.Y) * (y - e.Centro.Y)) <= e.RadioMm + margin;
        var p = e.Contorno;
        bool inside = false;
        for (int i = 0, j = p.Count - 1; i < p.Count; j = i++)
            if ((p[i].Y > y) != (p[j].Y > y) && x < (p[j].X - p[i].X) * (y - p[i].Y) / (p[j].Y - p[i].Y) + p[i].X) inside = !inside;
        if (inside) return true;
        for (int i = 0, j = p.Count - 1; i < p.Count; j = i++)
        {
            double dx = p[i].X - p[j].X, dy = p[i].Y - p[j].Y, len2 = dx * dx + dy * dy;
            double u = len2 <= 0 ? 0 : Math.Clamp(((x - p[j].X) * dx + (y - p[j].Y) * dy) / len2, 0, 1);
            double ex = p[j].X + u * dx - x, ey = p[j].Y + u * dy - y;
            if (ex * ex + ey * ey <= margin * margin) return true;
        }
        return false;
    }

    private string SaveReport(Document doc, string texto, string csv)
    {
        try
        {
            string folder = Path.Combine(SalaSettingsStore.DefaultFolder, "output");
            Directory.CreateDirectory(folder);
            string name = $"{Tipo}-{DateTime.Now:yyyyMMdd-HHmmss}";
            string file = Path.Combine(folder, name + ".txt");
            File.WriteAllText(file, texto, System.Text.Encoding.UTF8);
            File.WriteAllText(Path.Combine(folder, name + ".csv"), csv, new System.Text.UTF8Encoding(true));
            return file;
        }
        catch { return ""; }
    }

    private void Show(string instruction, string content, string? details = null)
    {
        var td = new TaskDialog(Title) { MainInstruction = instruction, MainContent = content };
        if (!string.IsNullOrEmpty(details)) td.ExpandedContent = details.Length > 12000 ? details[..12000] + "\n…" : details;
        td.Show();
    }
}

[Transaction(TransactionMode.Manual)]
[Regeneration(RegenerationOption.Manual)]
public sealed class SalaContadoresAguaCommand : SalaTecnicaCommand { protected override SalaTipo Tipo => SalaTipo.ContadoresAgua; }

[Transaction(TransactionMode.Manual)]
[Regeneration(RegenerationOption.Manual)]
public sealed class SalaElectricidadCommand : SalaTecnicaCommand { protected override SalaTipo Tipo => SalaTipo.Electricidad; }

[Transaction(TransactionMode.Manual)]
[Regeneration(RegenerationOption.Manual)]
public sealed class SalaPciCommand : SalaTecnicaCommand { protected override SalaTipo Tipo => SalaTipo.Pci; }

[Transaction(TransactionMode.Manual)]
[Regeneration(RegenerationOption.Manual)]
public sealed class SalaFontaneriaCommand : SalaTecnicaCommand { protected override SalaTipo Tipo => SalaTipo.Fontaneria; }
