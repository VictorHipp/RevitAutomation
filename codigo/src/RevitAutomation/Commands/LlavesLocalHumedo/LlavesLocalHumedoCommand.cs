// Antes «#if !NET48»: ahora también se compila para Revit 2023 y 2024 (.NET Framework 4.8).
using Autodesk.Revit.Attributes;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using Autodesk.Revit.UI.Selection;
using CadMepRevit.Core;
using System.Text;
using WF = System.Windows.Forms;
using TaskDialog = Autodesk.Revit.UI.TaskDialog;

namespace RevitAutomation.Addin.Commands.LlavesLocalHumedo;

/// <summary>Coloca llaves reales en las inserciones del bloque y capa elegidos del CAD vinculado.</summary>
[Transaction(TransactionMode.Manual)]
[Regeneration(RegenerationOption.Manual)]
public sealed class LlavesLocalHumedoCommand : IExternalCommand
{
    private const string Title = "Llaves de local húmedo";

    public Result Execute(ExternalCommandData data, ref string message, ElementSet elements)
    {
        var ui = data.Application.ActiveUIDocument;
        if (ui == null || ui.Document.IsFamilyDocument || ui.Document.IsReadOnly ||
            ui.ActiveView is not ViewPlan { IsTemplate: false, GenLevel: not null } view)
        {
            TaskDialog.Show(Title, "Abra una planta de un proyecto editable con el CAD de fontanería vinculado.");
            return Result.Cancelled;
        }
        var doc = ui.Document;
        try
        {
            var links = new FilteredElementCollector(doc, view.Id).OfClass(typeof(ImportInstance))
                .Cast<ImportInstance>().Where(i => i.IsLinked).ToList();
            var link = links.FirstOrDefault(i => ui.Selection.GetElementIds().Contains(i.Id));
            if (link == null && links.Count == 1) link = links[0];
            if (links.Count == 0) throw new InvalidOperationException("Vincule el DWG/DXF de fontanería en esta planta.");
            if (link == null)
                link = (ImportInstance)doc.GetElement(ui.Selection.PickObject(ObjectType.Element,
                    new LinkFilter(links), "Seleccione el CAD de fontanería (Esc cancela)."));
            string path = ModelPathUtils.ConvertModelPathToUserVisiblePath(
                ExternalFileUtils.GetExternalFileReference(doc, link.GetTypeId()).GetAbsolutePath());
            if (!File.Exists(path)) throw new InvalidOperationException("No se encuentra el archivo del CAD vinculado: " + path);
            var drawing = CadReader.Read(path, new CadReader.Options { ReadLinesInsideBlocks = false });
            var blocks = drawing.Blocks.Where(b => !BlockNames.IsLegendOrReference(b.BlockName))
                .GroupBy(b => (b.BlockName, b.Layer)).OrderBy(g => g.Key.BlockName).ThenBy(g => g.Key.Layer).ToList();
            if (blocks.Count == 0) throw new InvalidOperationException("No se han leído bloques del CAD. Las marcas deben ser bloques INSERT; no se interpretan líneas sueltas ni referencias externas sin contenido.");
            var symbols = new FilteredElementCollector(doc).OfClass(typeof(FamilySymbol)).Cast<FamilySymbol>()
                .Where(s => s.Category?.Id == new ElementId(BuiltInCategory.OST_PipeAccessory) &&
                            s.Family.FamilyPlacementType == FamilyPlacementType.OneLevelBased)
                .OrderBy(s => s.FamilyName).ThenBy(s => s.Name).ToList();
            if (symbols.Count == 0) throw new InvalidOperationException("Cargue una familia de llave de Accesorios de tubería, basada en nivel y sin anfitrión. Esta versión no coloca familias de cara, muro o plano de trabajo.");
            using var form = new LlavesOptions(blocks.Select(g => $"{g.Key.BlockName} | {g.Key.Layer} ({g.Count()})").ToArray(),
                symbols.Select(s => s.FamilyName + " : " + s.Name).ToArray(), drawing.HeaderUnit);
            if (form.ShowDialog() != WF.DialogResult.OK) return Result.Cancelled;
            var selected = blocks[form.BlockIndex].ToList();
            var symbol = symbols[form.SymbolIndex];
            double unit = DrawingUnits.ToMillimeters(form.Unit) / 304.8;
            var transform = link.GetTotalTransform();
            if (transform.HasReflection || Math.Abs(transform.BasisZ.Normalize().DotProduct(XYZ.BasisZ) - 1) > 1e-6)
                throw new InvalidOperationException("Esta versión requiere un vínculo CAD horizontal y sin simetría.");
            VerifyAlignment(link, drawing, unit, transform);
            double elevation = view.GenLevel.ProjectElevation + form.HeightMm / 304.8;
            var plan = selected.Select(b =>
            {
                var q = transform.OfPoint(new XYZ(b.Position.X * unit, b.Position.Y * unit, b.Position.Z * unit));
                var direction = transform.OfVector(new XYZ(Math.Cos(b.RotationRadians), Math.Sin(b.RotationRadians), 0));
                return (Block: b, Point: new XYZ(q.X, q.Y, elevation), Angle: Math.Atan2(direction.Y, direction.X) + form.AngleDegrees * Math.PI / 180);
            }).ToList();
            if (plan.Any(p => !XYZ.IsWithinLengthLimits(p.Point) || !double.IsFinite(p.Angle)))
                throw new InvalidOperationException("El CAD contiene posiciones o giros no válidos para Revit.");
            if (new TaskDialog(Title)
            {
                MainInstruction = $"Colocar hasta {plan.Count} llaves en {view.GenLevel.Name}",
                MainContent = $"Bloque: {selected[0].BlockName}\nCapa: {selected[0].Layer}\nFamilia: {symbol.FamilyName} : {symbol.Name}\n" +
                    $"Altura sobre nivel: {form.HeightMm} mm. Origen de la familia en la inserción del bloque.\n" +
                    "Se procesan todas las apariciones de este bloque y capa en el archivo, incluso fuera del recorte.\n" +
                    "Se omiten posiciones ocupadas por accesorios (tolerancia 1 mm).\n" +
                    "Solo colocación: no corta tuberías, no conecta hidráulicamente y no cambia diámetros. Ctrl+Z deshace el lote.",
                CommonButtons = TaskDialogCommonButtons.Ok | TaskDialogCommonButtons.Cancel,
                DefaultButton = TaskDialogResult.Cancel
            }.Show() != TaskDialogResult.Ok) return Result.Cancelled;

            var report = new List<string> { "Entidad;Bloque;Capa;Estado;Id Revit;Detalle" };
            report.Add(Row("", "", "", "Configuración", "", $"CAD={path}; nivel={view.GenLevel.Name}; familia={symbol.FamilyName} : {symbol.Name}; unidad={form.Unit}; altura={form.HeightMm} mm; giro adicional={form.AngleDegrees} grados; sin conexión hidráulica"));
            report.AddRange(drawing.Warnings.Select(w => Row("", "", "", "Aviso CAD", "", w)));
            var occupied = new FilteredElementCollector(doc).OfClass(typeof(FamilyInstance)).Cast<FamilyInstance>()
                .Where(f => f.Category?.Id == new ElementId(BuiltInCategory.OST_PipeAccessory))
                .Select(f => (f.Location as LocationPoint)?.Point).Where(p => p != null).Cast<XYZ>().ToList();
            var created = new List<ElementId>();
            int skipped = 0, failed = 0;
            using (var group = new TransactionGroup(doc, Title))
            {
                group.Start();
                using var tx = new Transaction(doc, Title);
                tx.Start();
                if (!symbol.IsActive) { symbol.Activate(); doc.Regenerate(); }
                foreach (var p in plan)
                {
                    string entity = p.Block.EntityIndex.ToString(System.Globalization.CultureInfo.InvariantCulture);
                    if (occupied.Any(q => q.DistanceTo(p.Point) <= 1 / 304.8))
                    {
                        skipped++;
                        report.Add(Row(entity, p.Block.BlockName, p.Block.Layer, "Omitida", "", "Posición ocupada por un accesorio (1 mm)."));
                        continue;
                    }
                    using var sub = new SubTransaction(doc);
                    sub.Start();
                    try
                    {
                        var instance = doc.Create.NewFamilyInstance(p.Point, symbol, view.GenLevel,
                            Autodesk.Revit.DB.Structure.StructuralType.NonStructural);
                        doc.Regenerate();
                        if (instance.Location is not LocationPoint location)
                            throw new InvalidOperationException("La familia no expone un punto de inserción.");
                        ElementTransformUtils.MoveElement(doc, instance.Id, p.Point - location.Point);
                        ElementTransformUtils.RotateElement(doc, instance.Id, Line.CreateBound(p.Point, p.Point + XYZ.BasisZ), p.Angle - location.Rotation);
                        doc.Regenerate();
                        if (instance.Location is not LocationPoint actual || actual.Point.DistanceTo(p.Point) > 1 / 304.8)
                            throw new InvalidOperationException("La familia no ha quedado en la posición solicitada.");
                        var comments = instance.get_Parameter(BuiltInParameter.ALL_MODEL_INSTANCE_COMMENTS);
                        if (comments is { IsReadOnly: false }) comments.Set($"Llaves local húmedo | {link.UniqueId} | {p.Block.BlockName} | {entity} | Sin conexión hidráulica automática");
                        if (sub.Commit() != TransactionStatus.Committed) throw new InvalidOperationException("Revit no confirmó la colocación.");
                        created.Add(instance.Id);
                        occupied.Add(p.Point);
                        report.Add(Row(entity, p.Block.BlockName, p.Block.Layer, "Colocada", instance.Id.ToString(), "Sin conexión hidráulica automática."));
                    }
                    catch (Exception ex)
                    {
                        if (sub.GetStatus() == TransactionStatus.Started) sub.RollBack();
                        failed++;
                        report.Add(Row(entity, p.Block.BlockName, p.Block.Layer, "Desviación", "", ex.Message));
                    }
                }
                if (tx.Commit() != TransactionStatus.Committed) throw new InvalidOperationException("Revit ha revertido el lote de llaves.");
                if (group.Assimilate() != TransactionStatus.Committed) throw new InvalidOperationException("Revit no ha confirmado el grupo de colocación.");
            }
            string reportNote;
            try
            {
                string folder = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "RevitAutomation", "LlavesLocalHumedo");
                Directory.CreateDirectory(Path.Combine(folder, "input"));
                Directory.CreateDirectory(Path.Combine(folder, "output"));
                string file = Path.Combine(folder, "output", $"llaves-{DateTime.Now:yyyyMMdd-HHmmss-fff}.csv");
                File.WriteAllLines(file, report, new UTF8Encoding(true));
                reportNote = "Informe: " + file;
            }
            catch (Exception ex) { reportNote = "No se pudo guardar el informe: " + ex.Message; }
            try { ui.Selection.SetElementIds(created); } catch { /* La selección no afecta al resultado. */ }
            new TaskDialog(Title)
            {
                MainInstruction = $"{created.Count} colocadas, {skipped} omitidas y {failed} desviaciones",
                MainContent = "Las llaves están colocadas, sin conexión hidráulica automática.\n" + reportNote,
                ExpandedContent = string.Join("\n", report.Take(100))
            }.Show();
            return Result.Succeeded;
        }
        catch (Autodesk.Revit.Exceptions.OperationCanceledException) { return Result.Cancelled; }
        catch (Exception ex) { message = ex.Message; TaskDialog.Show(Title, ex.Message); return Result.Failed; }
    }

    private static string Row(params string[] cells) => string.Join(";", cells.Select(c => "\"" + c.Replace("\"", "\"\"") + "\""));

    private static void VerifyAlignment(ImportInstance link, CadDrawing drawing, double unit, Transform transform)
    {
        // Verificación del vínculo elegido, sin volver silenciosamente a coordenadas originales.
        var vertices = new List<LlavesPoint>();
        void Put(XYZ p) => vertices.Add(new LlavesPoint(p.X, p.Y, p.Z));
        void Walk(GeometryElement geometry)
        {
            foreach (var item in geometry)
                if (item is GeometryInstance instance) Walk(instance.GetInstanceGeometry());
                else if (item is PolyLine line) { foreach (var p in line.GetCoordinates()) Put(p); }
                else if (item is Curve { IsBound: true } curve) { Put(curve.GetEndPoint(0)); Put(curve.GetEndPoint(1)); }
        }
        var geometry = link.get_Geometry(new Options());
        if (geometry != null) Walk(geometry);
        var sample = drawing.Segments.Where(s => s.Length > 0).Where((_, i) => i % Math.Max(1, drawing.Segments.Count / 40) == 0)
            .Take(40).Select(s => transform.OfPoint(new XYZ(s.Start.X * unit, s.Start.Y * unit, s.Start.Z * unit))).ToList();
        if (!LlavesAlignment.Matches(vertices, sample.Select(p => new LlavesPoint(p.X, p.Y, p.Z)), 10 / 304.8))
            throw new InvalidOperationException("No se pudo verificar la coincidencia del archivo con el vínculo (mínimo 3 puntos distintos y 80 % a menos de 10 mm). Compruebe unidades, archivo y recarga del CAD. No se coloca ninguna llave.");
    }

    private sealed class LinkFilter(List<ImportInstance> links) : ISelectionFilter
    {
        public bool AllowElement(Element e) => links.Any(i => i.Id == e.Id);
        public bool AllowReference(Reference r, XYZ p) => false;
    }
}

internal sealed class LlavesOptions : WF.Form
{
    private readonly WF.ComboBox blocks = new() { DropDownStyle = WF.ComboBoxStyle.DropDownList, Width = 610 };
    private readonly WF.ComboBox symbols = new() { DropDownStyle = WF.ComboBoxStyle.DropDownList, Width = 610 };
    private readonly WF.ComboBox units = new() { DropDownStyle = WF.ComboBoxStyle.DropDownList, Width = 250 };
    private readonly WF.NumericUpDown height = new() { Minimum = -10000, Maximum = 100000, Value = 1000, DecimalPlaces = 1, Width = 160 };
    private readonly WF.NumericUpDown angle = new() { Minimum = -360, Maximum = 360, DecimalPlaces = 1, Width = 160 };
    internal int BlockIndex => blocks.SelectedIndex;
    internal int SymbolIndex => symbols.SelectedIndex;
    internal DrawingUnit Unit => (DrawingUnit)units.SelectedItem!;
    internal double HeightMm => (double)height.Value;
    internal double AngleDegrees => (double)angle.Value;

    internal LlavesOptions(string[] blockNames, string[] familyNames, DrawingUnit? unit)
    {
        Text = "Llaves de local húmedo · Colocación desde CAD";
        AutoScaleMode = WF.AutoScaleMode.Dpi;
        AutoSize = true; AutoSizeMode = WF.AutoSizeMode.GrowAndShrink;
        StartPosition = WF.FormStartPosition.CenterScreen;
        FormBorderStyle = WF.FormBorderStyle.FixedDialog; MaximizeBox = false; MinimizeBox = false;
        var layout = new WF.FlowLayoutPanel { FlowDirection = WF.FlowDirection.TopDown, WrapContents = false, AutoSize = true, Padding = new WF.Padding(16) };
        Controls.Add(layout);
        void Add(string label, WF.Control control)
        {
            layout.Controls.Add(new WF.Label { Text = label, AutoSize = true, MaximumSize = new System.Drawing.Size(610, 0), Margin = new WF.Padding(3, 10, 3, 3) });
            layout.Controls.Add(control);
        }
        blocks.Items.AddRange(blockNames); symbols.Items.AddRange(familyNames);
        units.Items.AddRange(Enum.GetValues<DrawingUnit>().Cast<object>().ToArray());
        if (unit.HasValue) units.SelectedItem = unit.Value;
        // Se eligen expresamente bloque y familia; el primer bloque no tiene por qué ser una llave.
        Add("Bloque y capa de las marcas de llave (todas sus apariciones):", blocks);
        Add("Familia y tipo de llave cargados en el modelo:", symbols);
        Add("Unidades del archivo CAD (compruebe las unidades del vínculo):", units);
        Add("Altura del origen de la familia sobre el nivel, en mm (propuesta inicial: 1000):", height);
        Add("Giro adicional respecto al bloque, en grados:", angle);
        layout.Controls.Add(new WF.Label { AutoSize = true, MaximumSize = new System.Drawing.Size(610, 0), Text = "Origen de la familia en la inserción del bloque. Solo se leen bloques del archivo vinculado. No se conectan las llaves a las tuberías." });
        var ok = new WF.Button { Text = "Revisar colocación", AutoSize = true, Enabled = false, DialogResult = WF.DialogResult.OK };
        var cancel = new WF.Button { Text = "Cancelar", AutoSize = true, DialogResult = WF.DialogResult.Cancel };
        void ValidateChoice(object? sender, EventArgs e) => ok.Enabled = blocks.SelectedIndex >= 0 && symbols.SelectedIndex >= 0 && units.SelectedIndex >= 0;
        blocks.SelectedIndexChanged += ValidateChoice; symbols.SelectedIndexChanged += ValidateChoice; units.SelectedIndexChanged += ValidateChoice;
        var buttons = new WF.FlowLayoutPanel { AutoSize = true }; buttons.Controls.Add(ok); buttons.Controls.Add(cancel); layout.Controls.Add(buttons);
        AcceptButton = ok; CancelButton = cancel;
    }
}

