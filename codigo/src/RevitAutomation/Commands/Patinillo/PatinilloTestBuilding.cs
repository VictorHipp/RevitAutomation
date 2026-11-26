#nullable enable
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using Autodesk.Revit.DB;

namespace RevitAutomation.Addin.Commands.Patinillo;

public static class PatinilloTestBuilding
{
    public static string Create(Document doc, PatinilloSettings s)
    {
        var existing = new FilteredElementCollector(doc).OfClass(typeof(Level)).Cast<Level>().OrderBy(l => l.Elevation).ToList();
        if (new FilteredElementCollector(doc).OfClass(typeof(Floor)).Any() || existing.Count > 2)
            throw new InvalidOperationException("El edificio de prueba solo se puede crear en un proyecto sin forjados y con un máximo de dos niveles.");
        if (s.TestFloorsAboveGround < 0 || !double.IsFinite(s.TestFloorHeightMm) || s.TestFloorHeightMm <= 0 ||
            !double.IsFinite(s.SlabThicknessMm) || s.SlabThicknessMm <= 0)
            return "No se ha creado el edificio: revise el número de plantas, la altura entre plantas y el espesor del forjado.";

        var notes = new List<string>();
        var levels = new Dictionary<int, Level>();
        int createdLevels = 0, reusedLevels = 0, plans = 0, floors = 0;
        bool opening = false;
        using var group = new TransactionGroup(doc, "Patinillo · edificio de prueba");
        try
        {
            if (group.Start() != TransactionStatus.Started) return "No se pudo iniciar el edificio de prueba.";
            // Index 0 = PB; 1..n = dwellings; n+1 = roof slab.
            for (int i = 0; i <= s.TestFloorsAboveGround + 1; i++)
            {
                int index = i;
                string name = index == 0 ? "PB" : index == s.TestFloorsAboveGround + 1 ? "Cubierta" : "P" + index;
                double elevation = Ft(index * s.TestFloorHeightMm);
                Level? chosen = existing.FirstOrDefault(l => Math.Abs(l.Elevation - elevation) <= Ft(1) &&
                    !levels.Values.Any(used => used.Id == l.Id));
                // A template level at an elevation that matches no target (e.g. «Nivel 2» at 4,00 m) would become an extra
                // floor: move the first unused one to this target instead of creating a new level.
                Level? moved = chosen is null
                    ? existing.FirstOrDefault(l => !levels.Values.Any(used => used.Id == l.Id) &&
                        !Enumerable.Range(0, s.TestFloorsAboveGround + 2).Any(k => Math.Abs(l.Elevation - Ft(k * s.TestFloorHeightMm)) <= Ft(1)))
                    : null;
                chosen ??= moved;
                bool reused = chosen != null;
                try
                {
                    PatinilloTransactions.Run(doc, "Edificio de prueba · nivel «" + name + "»", () =>
                    {
                        if (moved != null) moved.Elevation = elevation;
                        chosen ??= Level.Create(doc, elevation);
                        if (!existing.Any(l => l.Id != chosen.Id && string.Equals(l.Name, name, StringComparison.OrdinalIgnoreCase)))
                            chosen.Name = name;
                    });
                    if (chosen is null) throw new InvalidOperationException("Revit no devolvió el nivel.");
                    levels[index] = chosen;
                    if (reused) reusedLevels++; else { createdLevels++; existing.Add(chosen); }
                    if (!string.Equals(chosen.Name, name, StringComparison.Ordinal))
                        notes.Add("El nivel previsto «" + name + "» conserva «" + chosen.Name + "» porque el nombre ya existe.");
                }
                catch (Autodesk.Revit.Exceptions.RegenerationFailedException) { throw; }
                catch (Exception ex) { notes.Add("Nivel «" + name + "» no creado: " + ex.Message); }
            }

            var planType = new FilteredElementCollector(doc).OfClass(typeof(ViewFamilyType)).Cast<ViewFamilyType>()
                .FirstOrDefault(t => t.ViewFamily == ViewFamily.FloorPlan);
            foreach (Level level in levels.Values)
            {
                bool hasPlan = new FilteredElementCollector(doc).OfClass(typeof(ViewPlan)).Cast<ViewPlan>()
                    .Any(v => !v.IsTemplate && v.ViewType == ViewType.FloorPlan && v.GenLevel?.Id == level.Id);
                if (hasPlan) continue;
                try
                {
                    PatinilloTransactions.Run(doc, "Edificio de prueba · planta «" + level.Name + "»", () =>
                    {
                        if (planType is null) throw new InvalidOperationException("No hay tipo de vista de planta de piso.");
                        var view = ViewPlan.Create(doc, planType.Id, level.Id);
                        view.Name = PatinilloBuilder.UniqueViewName(doc, "Prueba - " + level.Name);
                    });
                    plans++;
                }
                catch (Autodesk.Revit.Exceptions.RegenerationFailedException) { throw; }
                catch (Exception ex) { notes.Add("Planta «" + level.Name + "» no creada: " + ex.Message); }
            }

            FloorType? floorType = null;
            try
            {
                PatinilloTransactions.Run(doc, "Edificio de prueba · tipo de forjado", () =>
                {
                    var source = new FilteredElementCollector(doc).OfClass(typeof(FloorType)).Cast<FloorType>()
                        .FirstOrDefault(t => t.GetCompoundStructure() != null)
                        ?? throw new InvalidOperationException("No hay ningún tipo de forjado con estructura compuesta.");
                    string name = "Forjado prueba " + s.SlabThicknessMm.ToString("0.###", CultureInfo.InvariantCulture) + " mm";
                    var names = new HashSet<string>(new FilteredElementCollector(doc).OfClass(typeof(FloorType)).Cast<FloorType>()
                        .Select(t => t.Name), StringComparer.OrdinalIgnoreCase);
                    string unique = name;
                    for (int n = 2; names.Contains(unique); n++) unique = name + " (" + n + ")";
                    floorType = (FloorType)source.Duplicate(unique);
                    using var structure = CompoundStructure.CreateSingleLayerCompoundStructure(MaterialFunctionAssignment.Structure,
                        Ft(s.SlabThicknessMm), ElementId.InvalidElementId);
                    floorType.SetCompoundStructure(structure);
                });
            }
            catch (Autodesk.Revit.Exceptions.RegenerationFailedException) { throw; }
            catch (Exception ex) { floorType = null; notes.Add("Tipo de forjado no creado: " + ex.Message); }
            foreach (var pair in levels.Where(p => p.Key > 0).OrderBy(p => p.Key))
            {
                var level = pair.Value;
                try
                {
                    PatinilloTransactions.Run(doc, "Edificio de prueba · forjado «" + level.Name + "»", () =>
                    {
                        if (floorType is null) throw new InvalidOperationException("No se dispone del tipo de forjado de prueba.");
                        using var loop = new CurveLoop();
                        foreach (var curve in Rectangle(0, 0, 12000, 10000)) loop.Append(curve);
                        _ = Floor.Create(doc, new List<CurveLoop> { loop }, floorType.Id, level.Id);
                    });
                    floors++;
                }
                catch (Autodesk.Revit.Exceptions.RegenerationFailedException) { throw; }
                catch (Exception ex) { notes.Add("Forjado «" + level.Name + "» no creado: " + ex.Message); }
            }
            try
            {
                PatinilloTransactions.Run(doc, "Edificio de prueba · hueco del patinillo", () =>
                {
                    if (!levels.TryGetValue(0, out var bottom) || !levels.TryGetValue(s.TestFloorsAboveGround + 1, out var top))
                        throw new InvalidOperationException("Falta PB o Cubierta para limitar el hueco.");
                    using var profile = doc.Application.Create.NewCurveArray();
                    foreach (var curve in Rectangle(5700, 4800, 6300, 5200)) profile.Append(curve);
                    _ = doc.Create.NewOpening(bottom, top, profile) ?? throw new InvalidOperationException("Revit no devolvió el hueco.");
                });
                opening = true;
            }
            catch (Autodesk.Revit.Exceptions.RegenerationFailedException) { throw; }
            catch (Exception ex) { notes.Add("Hueco del patinillo no creado: " + ex.Message); }
            if (group.Assimilate() != TransactionStatus.Committed)
                throw new InvalidOperationException("Revit no aceptó el grupo del edificio de prueba.");
        }
        catch (Exception ex)
        {
            if (group.GetStatus() == TransactionStatus.Started) group.RollBack();
            return "No se pudo completar el edificio de prueba; se han deshecho sus cambios.\n" + ex.Message +
                (notes.Count > 0 ? "\n" + string.Join("\n", notes) : "");
        }
        var summary = new StringBuilder();
        summary.AppendLine($"Niveles: {createdLevels} nuevos y {reusedLevels} reutilizados. Plantas nuevas: {plans}.");
        summary.AppendLine($"Forjados de 12 × 10 m: {floors}. Espesor: {s.SlabThicknessMm:0.###} mm.");
        summary.AppendLine(opening ? "Hueco de 600 × 400 mm creado entre PB y Cubierta, en (5,70–6,30; 4,80–5,20) m."
            : "No se ha creado el hueco del patinillo.");
        summary.AppendLine("Ctrl+Z deshace el edificio de prueba. Abra una planta y coloque el patinillo en el hueco.");
        if (notes.Count > 0) summary.AppendLine().AppendLine("Incidencias:").Append(string.Join("\n", notes));
        return summary.ToString();
    }

    private static IEnumerable<Curve> Rectangle(double x1, double y1, double x2, double y2)
    {
        var points = new[] { new XYZ(Ft(x1), Ft(y1), 0), new XYZ(Ft(x2), Ft(y1), 0),
            new XYZ(Ft(x2), Ft(y2), 0), new XYZ(Ft(x1), Ft(y2), 0) };
        for (int i = 0; i < 4; i++) yield return Line.CreateBound(points[i], points[(i + 1) % 4]);
    }

    private static double Ft(double mm) => PatinilloCommand.ToFeet(mm);
}
