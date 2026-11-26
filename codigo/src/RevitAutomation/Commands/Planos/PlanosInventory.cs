using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.ExtensibleStorage;
using View = Autodesk.Revit.DB.View;

namespace RevitAutomation.Addin.Commands.Planos;

/// <summary>
/// Marks the documentation created by PLANOS (views, sheets, schedules, legends, filters) with extensible storage, so a
/// new run recognises and replaces only its own elements. Nothing without the mark is ever deleted or changed.
/// The value is "Discipline|Kind|Key" (Discipline "*" for filters shared by several disciplines).
/// </summary>
internal static class PlanosMark
{
    private static readonly Guid Id = new Guid("5B0E7C61-3A2F-4C8E-9D41-6F2A8B17C3E5");
    private const string Field = "PlanosKey";

    internal const string Plan = "Plan", Sheet = "Sheet", Schedule = "Schedule", Legend = "Legend", Filter = "Filter";

    internal static string? Read(Element e)
    {
        var schema = Schema.Lookup(Id);
        var field = schema?.GetField(Field);
        if (schema is null || field is null) return null;
        try
        {
            var entity = e.GetEntity(schema);
            return entity.IsValid() ? entity.Get<string>(field) : null;
        }
        catch (Exception) { return null; } // unreadable mark: not ours
    }

    internal static (string Discipline, string Kind, string Key)? Parse(string? value)
    {
        if (string.IsNullOrEmpty(value)) return null;
        var parts = value!.Split(new[] { '|' }, 3);
        return parts.Length == 3 ? (parts[0], parts[1], parts[2]) : ((string, string, string)?)null;
    }

    internal static void Set(Element e, string discipline, string kind, string key)
    {
        var schema = Schema.Lookup(Id);
        if (schema is null)
        {
            var builder = new SchemaBuilder(Id);
            builder.SetSchemaName("RevitAutomationPlanos");
            builder.AddSimpleField(Field, typeof(string));
            schema = builder.Finish();
        }
        var entity = new Entity(schema);
        entity.Set(schema.GetField(Field), discipline + "|" + kind + "|" + key);
        e.SetEntity(entity);
    }
}

/// <summary>One documented model element (read only).</summary>
internal sealed class PlanosElement
{
    internal ElementId Id { get; set; } = ElementId.InvalidElementId;
    internal string Category { get; set; } = "";
    internal PlanosClassification Class { get; set; } = new PlanosClassification();
    internal ElementId SystemTypeId { get; set; } = ElementId.InvalidElementId;
    internal int Level { get; set; } = -1;
    internal PlanosRect Box { get; set; }
    /// <summary>Pipes, ducts, trays: mid point and length (feet). Families: insertion point.</summary>
    internal XYZ? Point { get; set; }
    internal double Length { get; set; }
}

internal sealed class PlanosSystemType
{
    internal ElementId Id { get; set; } = ElementId.InvalidElementId;
    internal string Name { get; set; } = "";
    internal string Classification { get; set; } = "";
    internal bool IsPipe { get; set; }
    internal int Uses { get; set; }
}

/// <summary>An element created by a previous run (found by its mark).</summary>
internal sealed class PlanosOwned
{
    internal ElementId Id { get; set; } = ElementId.InvalidElementId;
    internal string Discipline { get; set; } = "";
    internal string Kind { get; set; } = "";
    internal string Key { get; set; } = "";
    internal string Name { get; set; } = "";
}

/// <summary>Everything PLANOS needs to know about the open project, read without changing anything.</summary>
internal sealed class PlanosInventory
{
    internal List<Level> Levels { get; } = new List<Level>();
    internal List<PlanosLevel> PlanLevels { get; } = new List<PlanosLevel>();
    internal List<PlanosElement> Elements { get; } = new List<PlanosElement>();
    internal Dictionary<ElementId, PlanosSystemType> SystemTypes { get; } = new Dictionary<ElementId, PlanosSystemType>();
    internal List<FamilySymbol> TitleBlocks { get; } = new List<FamilySymbol>();
    /// <summary>Measured size (mm) of title block types already used on a sheet.</summary>
    internal Dictionary<ElementId, (double W, double H)> TitleBlockSizes { get; } = new Dictionary<ElementId, (double, double)>();
    internal List<View> PlanTemplates { get; } = new List<View>();
    /// <summary>Tag type per tag category name (e.g. "OST_PipeTags").</summary>
    internal Dictionary<string, FamilySymbol> TagTypes { get; } = new Dictionary<string, FamilySymbol>(StringComparer.Ordinal);
    internal List<string> SheetNumbers { get; } = new List<string>();
    internal int Sheets { get; set; }
    internal List<PlanosOwned> Owned { get; } = new List<PlanosOwned>();
    internal int ElectricalCircuits { get; set; }
    internal ViewFamilyType? FloorPlanType { get; set; }
    internal ViewFamilyType? CeilingPlanType { get; set; }
    internal ViewFamilyType? DraftingType { get; set; }

    internal IEnumerable<PlanosElement> Of(PlanosDiscipline d) => Elements.Where(e => e.Class.Discipline == d);

    internal List<PlanosItem> Items() => Elements.Where(e => e.Class.Discipline.HasValue)
        .Select(e => new PlanosItem { Discipline = e.Class.Discipline!.Value, Network = e.Class.Network, Level = e.Level, Box = e.Box }).ToList();

    /// <summary>Tag category of each tagged model category.</summary>
    internal static readonly Dictionary<string, string> TagCategoryOf = new Dictionary<string, string>(StringComparer.Ordinal)
    {
        ["OST_PipeCurves"] = "OST_PipeTags",
        ["OST_DuctCurves"] = "OST_DuctTags",
        ["OST_CableTray"] = "OST_CableTrayTags",
        ["OST_MechanicalEquipment"] = "OST_MechanicalEquipmentTags",
        ["OST_DuctTerminal"] = "OST_DuctTerminalTags",
        ["OST_PlumbingFixtures"] = "OST_PlumbingFixtureTags",
        ["OST_ElectricalEquipment"] = "OST_ElectricalEquipmentTags",
        ["OST_LightingFixtures"] = "OST_LightingFixtureTags",
        ["OST_GenericModel"] = "OST_GenericModelTags",
        ["OST_Sprinklers"] = "OST_SprinklerTags",
        ["OST_FireAlarmDevices"] = "OST_FireAlarmDeviceTags",
    };

    internal static string TagLabel(string tagCategory) => tagCategory switch
    {
        "OST_PipeTags" => "etiqueta de tubería",
        "OST_DuctTags" => "etiqueta de conducto",
        "OST_CableTrayTags" => "etiqueta de bandeja",
        "OST_MechanicalEquipmentTags" => "etiqueta de equipo mecánico",
        "OST_DuctTerminalTags" => "etiqueta de terminal de aire",
        "OST_PlumbingFixtureTags" => "etiqueta de aparato sanitario",
        "OST_ElectricalEquipmentTags" => "etiqueta de equipo eléctrico",
        "OST_LightingFixtureTags" => "etiqueta de luminaria",
        "OST_GenericModelTags" => "etiqueta de modelo genérico",
        "OST_SprinklerTags" => "etiqueta de rociador",
        "OST_FireAlarmDeviceTags" => "etiqueta de dispositivo de alarma",
        _ => tagCategory
    };

    internal static PlanosInventory Read(Document doc)
    {
        var inv = new PlanosInventory();

        inv.Levels.AddRange(new FilteredElementCollector(doc).OfClass(typeof(Level)).Cast<Level>().OrderBy(l => l.Elevation).ThenBy(l => l.Name));
        foreach (var l in inv.Levels) inv.PlanLevels.Add(new PlanosLevel(l.UniqueId, l.Name, l.Elevation));
        var levelIndex = new Dictionary<ElementId, int>();
        for (int i = 0; i < inv.Levels.Count; i++) levelIndex[inv.Levels[i].Id] = i;
        var elevations = inv.Levels.Select(l => l.Elevation).ToList();

        foreach (var t in new FilteredElementCollector(doc).OfClass(typeof(MEPSystemType)).Cast<MEPSystemType>())
            inv.SystemTypes[t.Id] = new PlanosSystemType { Id = t.Id, Name = t.Name, Classification = t.SystemClassification.ToString(), IsPipe = t is Autodesk.Revit.DB.Plumbing.PipingSystemType };

        var wanted = new HashSet<string>(PlanosClassifier.AllCategories);
        var wantedCategories = new List<BuiltInCategory>();
        foreach (string name in wanted)
            if (Enum.TryParse(name, out BuiltInCategory bic)) wantedCategories.Add(bic);
        // Native quick filter avoids expanding every architectural/model element into managed code.
        using var categoryFilter = new ElementMulticategoryFilter(wantedCategories);
        foreach (Element e in new FilteredElementCollector(doc).WhereElementIsNotElementType().WhereElementIsViewIndependent().WherePasses(categoryFilter))
        {
            Category? category = e.Category;
            if (category is null || category.CategoryType != CategoryType.Model) continue;
            string cat;
            try { cat = category.BuiltInCategory.ToString(); } catch { continue; }
            if (!wanted.Contains(cat)) continue;
            BoundingBoxXYZ? bb;
            try { bb = e.get_BoundingBox(null); } catch { bb = null; }
            if (bb is null) continue; // nothing to draw
            var a = bb.Transform.OfPoint(bb.Min); var b = bb.Transform.OfPoint(bb.Max);

            var facts = new PlanosElementFacts { Category = cat };
            ElementId systemType = SystemTypeOf(e);
            if (systemType != ElementId.InvalidElementId && inv.SystemTypes.TryGetValue(systemType, out var st)) { facts.SystemClassification = st.Classification; st.Uses++; }
            if (e is FamilyInstance fi)
            {
                try { facts.FamilyName = fi.Symbol?.FamilyName ?? ""; } catch { }
                ReadConnectors(fi, facts);
            }
            var c = PlanosClassifier.Classify(facts);
            if (c.Discipline is null) continue;

            var item = new PlanosElement
            {
                Id = e.Id, Category = cat, Class = c, SystemTypeId = systemType,
                Box = new PlanosRect(Math.Min(a.X, b.X), Math.Min(a.Y, b.Y), Math.Max(a.X, b.X), Math.Max(a.Y, b.Y))
            };
            ElementId lid = LevelOf(e);
            item.Level = lid != ElementId.InvalidElementId && levelIndex.TryGetValue(lid, out int li) ? li : PlanosLevels.Assign((a.Z + b.Z) / 2, elevations);
            if (e.Location is LocationCurve lc && lc.Curve is { } curve && curve.IsBound)
            {
                item.Point = curve.Evaluate(0.5, true);
                item.Length = curve.Length;
            }
            else if (e.Location is LocationPoint lp) item.Point = lp.Point;
            inv.Elements.Add(item);
        }

        foreach (FamilySymbol s in new FilteredElementCollector(doc).OfCategory(BuiltInCategory.OST_TitleBlocks).WhereElementIsElementType().Cast<FamilySymbol>()
                     .OrderBy(s => s.FamilyName).ThenBy(s => s.Name))
            inv.TitleBlocks.Add(s);
        var sheets = new FilteredElementCollector(doc).OfClass(typeof(ViewSheet)).Cast<ViewSheet>().Where(v => !v.IsTemplate).ToList();
        inv.Sheets = sheets.Count;
        inv.SheetNumbers.AddRange(sheets.Select(s => s.SheetNumber));
        var sheetById = sheets.ToDictionary(s => s.Id);
        foreach (FamilyInstance tb in new FilteredElementCollector(doc).OfCategory(BuiltInCategory.OST_TitleBlocks).WhereElementIsNotElementType().Cast<FamilyInstance>())
        {
            if (inv.TitleBlockSizes.ContainsKey(tb.Symbol.Id) || !sheetById.TryGetValue(tb.OwnerViewId, out var sheet)) continue;
            try
            {
                var bb = tb.get_BoundingBox(sheet);
                if (bb is not null) inv.TitleBlockSizes[tb.Symbol.Id] = ((bb.Max.X - bb.Min.X) * PlanosScale.MmPerFoot, (bb.Max.Y - bb.Min.Y) * PlanosScale.MmPerFoot);
            }
            catch { /* size unknown: the user picks the paper format */ }
        }

        var views = new FilteredElementCollector(doc).OfClass(typeof(View)).Cast<View>().ToList();
        foreach (View v in views)
        {
            if (v.IsTemplate)
            {
                if (v.ViewType is ViewType.FloorPlan or ViewType.CeilingPlan) inv.PlanTemplates.Add(v);
                continue;
            }
        }
        inv.PlanTemplates.Sort((x, y) => string.Compare(x.Name, y.Name, StringComparison.CurrentCultureIgnoreCase));

        // Everything a previous run created (views, sheets, schedules, filters), recognised only by its mark.
        foreach (Element e in views)
            AddOwned(inv, e);
        foreach (Element e in new FilteredElementCollector(doc).OfClass(typeof(ParameterFilterElement)))
            AddOwned(inv, e);

        foreach (var tagCat in TagCategoryOf.Values.Distinct())
        {
            if (!Enum.TryParse(tagCat, out BuiltInCategory bic)) continue;
            var symbol = new FilteredElementCollector(doc).OfCategory(bic).WhereElementIsElementType().OfType<FamilySymbol>()
                .OrderBy(s => s.FamilyName, StringComparer.CurrentCultureIgnoreCase).ThenBy(s => s.Name, StringComparer.CurrentCultureIgnoreCase).FirstOrDefault();
            if (symbol is not null) inv.TagTypes[tagCat] = symbol;
        }
        try { inv.ElectricalCircuits = new FilteredElementCollector(doc).OfCategory(BuiltInCategory.OST_ElectricalCircuit).WhereElementIsNotElementType().GetElementCount(); }
        catch { inv.ElectricalCircuits = 0; }

        var vfts = new FilteredElementCollector(doc).OfClass(typeof(ViewFamilyType)).Cast<ViewFamilyType>().ToList();
        // Prefer a type without a default template (it would be applied to every new view).
        ViewFamilyType? Pick(ViewFamily f) => vfts.Where(t => t.ViewFamily == f).OrderBy(t => t.DefaultTemplateId == ElementId.InvalidElementId ? 0 : 1).FirstOrDefault();
        inv.FloorPlanType = Pick(ViewFamily.FloorPlan);
        inv.CeilingPlanType = Pick(ViewFamily.CeilingPlan);
        inv.DraftingType = Pick(ViewFamily.Drafting);
        return inv;
    }

    private static void AddOwned(PlanosInventory inv, Element e)
    {
        if (PlanosMark.Parse(PlanosMark.Read(e)) is not { } m) return;
        inv.Owned.Add(new PlanosOwned { Id = e.Id, Discipline = m.Discipline, Kind = m.Kind, Key = m.Key, Name = e.Name });
    }

    internal IEnumerable<PlanosOwned> OwnedOf(PlanosDiscipline d, string kind) =>
        Owned.Where(o => o.Discipline == d.ToString() && o.Kind == kind);

    private static ElementId SystemTypeOf(Element e)
    {
        try
        {
            Parameter? p = e.get_Parameter(BuiltInParameter.RBS_PIPING_SYSTEM_TYPE_PARAM) ?? e.get_Parameter(BuiltInParameter.RBS_DUCT_SYSTEM_TYPE_PARAM);
            ElementId? id = p?.AsElementId();
            if (id is not null && id != ElementId.InvalidElementId) return id;
            if (e is MEPCurve curve && curve.MEPSystem is { } sys) return sys.GetTypeId();
        }
        catch { /* no system information */ }
        return ElementId.InvalidElementId;
    }

    private static ElementId LevelOf(Element e)
    {
        try
        {
            if (e is MEPCurve mc && mc.ReferenceLevel is { } rl) return rl.Id;
            if (e.LevelId != ElementId.InvalidElementId) return e.LevelId;
            if (e is FamilyInstance fi && fi.Host is Level hl) return hl.Id;
            foreach (var bip in new[] { BuiltInParameter.FAMILY_LEVEL_PARAM, BuiltInParameter.INSTANCE_SCHEDULE_ONLY_LEVEL_PARAM, BuiltInParameter.RBS_START_LEVEL_PARAM })
            {
                var id = e.get_Parameter(bip)?.AsElementId();
                if (id is not null && id != ElementId.InvalidElementId) return id;
            }
        }
        catch { }
        return ElementId.InvalidElementId;
    }

    private static void ReadConnectors(FamilyInstance fi, PlanosElementFacts facts)
    {
        ConnectorSet? set;
        try { set = fi.MEPModel?.ConnectorManager?.Connectors; } catch { return; }
        if (set is null) return;
        foreach (Connector c in set)
        {
            try
            {
                string cls = c.Domain == Domain.DomainPiping ? c.PipeSystemType.ToString()
                    : c.Domain == Domain.DomainHvac ? c.DuctSystemType.ToString() : "";
                facts.Connectors.Add(new PlanosConnectorFact(c.Domain.ToString(), cls));
            }
            catch { /* unreadable connector ignored */ }
        }
    }
}
