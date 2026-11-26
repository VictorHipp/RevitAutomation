using RevitAutomation.Addin.Commands.Planos;

// Pure-logic checks of the PLANOS module. Elements are described by the same API names the Revit adapter reads
// (BuiltInCategory, MEPSystemClassification, connector Domain/PipeSystemType/DuctSystemType). The creation of views,
// sheets, schedules and tags still needs a test inside Revit.
internal static class Program
{
    private static int _pass, _fail;
    private const double M = 1 / 0.3048; // one metre in feet

    private static void Check(bool condition, string name)
    {
        if (condition) { _pass++; Console.WriteLine("PASS: " + name); }
        else { _fail++; Console.WriteLine("FAIL: " + name); }
    }

    private static PlanosClassification C(string category, string system = "", string family = "", params (string Domain, string Cls)[] connectors)
    {
        var f = new PlanosElementFacts { Category = category, SystemClassification = system, FamilyName = family };
        foreach (var (d, c) in connectors) f.Connectors.Add(new PlanosConnectorFact(d, c));
        return PlanosClassifier.Classify(f);
    }

    private static int Main()
    {
        Classification();
        Visibility();
        Levels();
        Scales();
        Numbering();
        Packing();
        Tags();
        Planning();
        AdversarialClassification();
        AdversarialScales();
        AdversarialNumbering();
        AdversarialPacking();
        AdversarialPlanning();
        BoundsIndexEquivalence();
        PlannerLookupEquivalence();
        Console.WriteLine($"PLANOS_TOTAL={_pass + _fail} PASS={_pass} FAIL={_fail}; lógica pura, no API de Revit.");
        return _fail == 0 ? 0 : 1;
    }

    private static void Classification()
    {
        foreach (var sys in new[] { "DomesticColdWater", "DomesticHotWater", "Recirculation" })
            Check(C("OST_PipeCurves", sys) is { Discipline: PlanosDiscipline.Fontaneria, Network: PlanosNetwork.AguaFriaAcs }, "Fontanería: " + sys + " → red de agua fría y ACS");
        foreach (var sys in new[] { "Sanitary", "Vent", "Storm" })
            Check(C("OST_PipeCurves", sys) is { Discipline: PlanosDiscipline.Fontaneria, Network: PlanosNetwork.Saneamiento }, "Fontanería: " + sys + " → red de saneamiento");
        var other = C("OST_PipeCurves", "OtherPipe");
        Check(other is { Discipline: PlanosDiscipline.Fontaneria, Network: PlanosNetwork.OtrasFontaneria, AssumedByCategory: true }, "Fontanería: sistema «Otro» → otras redes, marcado como supuesto");
        Check(C("OST_PipeFitting", "", "", ("DomainPiping", "Sanitary"), ("DomainPiping", "Sanitary")).Network == PlanosNetwork.Saneamiento, "Fontanería: unión sin sistema, por sus conectores");
        Check(C("OST_PipeFitting", "", "", ("DomainPiping", "Sanitary"), ("DomainPiping", "DomesticColdWater")) is { Network: PlanosNetwork.OtrasFontaneria, AssumedByCategory: true },
            "Fontanería: unión con conectores de redes distintas → otras redes, supuesta");
        Check(C("OST_PlumbingFixtures") is { Discipline: PlanosDiscipline.Fontaneria, Network: PlanosNetwork.Todo }, "Fontanería: aparatos sanitarios en todas las plantas de fontanería");
        Check(C("OST_MechanicalEquipment", "", "", ("DomainPiping", "DomesticHotWater"), ("DomainElectrical", "")).Discipline == PlanosDiscipline.Fontaneria, "Fontanería: termo por sus conectores");

        Check(C("OST_PipeCurves", "SupplyHydronic").Discipline == PlanosDiscipline.Climatizacion, "Climatización: tuberías de agua de climatización en la misma lámina que los conductos");
        Check(C("OST_DuctCurves", "ExhaustAir") is { Discipline: PlanosDiscipline.Climatizacion, ColorKey: "ExhaustAir" }, "Climatización: conducto de extracción");
        Check(C("OST_DuctTerminal", "SupplyAir").Discipline == PlanosDiscipline.Climatizacion, "Climatización: difusores");
        Check(C("OST_MechanicalEquipment", "", "", ("DomainHvac", "SupplyAir"), ("DomainPiping", "DomesticColdWater")).Discipline == PlanosDiscipline.Climatizacion, "Climatización: equipo con conductos");
        Check(C("OST_MechanicalEquipment") is { Discipline: PlanosDiscipline.Climatizacion, AssumedByCategory: true }, "Climatización: equipo sin conectores, supuesto");

        Check(C("OST_CableTray").Discipline == PlanosDiscipline.Electricidad && C("OST_ElectricalEquipment").Discipline == PlanosDiscipline.Electricidad, "Electricidad: bandejas y cuadros");
        Check(C("OST_LightingFixtures").Discipline == PlanosDiscipline.Iluminacion && C("OST_LightingDevices").Discipline == PlanosDiscipline.Iluminacion, "Iluminación: luminarias y mandos");

        // PCI goes with the generic families (decided by the user).
        Check(C("OST_PipeCurves", "FireProtectWet").Discipline == PlanosDiscipline.Genericas, "PCI: tubería contra incendios con las familias genéricas");
        Check(C("OST_Sprinklers").Discipline == PlanosDiscipline.Genericas && C("OST_FireProtection").Discipline == PlanosDiscipline.Genericas
              && C("OST_FireAlarmDevices").Discipline == PlanosDiscipline.Genericas, "PCI: rociadores, extintores/BIE y alarma con las familias genéricas");
        Check(C("OST_MechanicalEquipment", "", "", ("DomainPiping", "FireProtectWet")).Discipline == PlanosDiscipline.Genericas, "PCI: grupo de presión contra incendios");
        Check(C("OST_GenericModel", "", "Tubería AF (bloque)") is { Discipline: PlanosDiscipline.Genericas, ColorKey: "Tubería AF (bloque)" }, "Genéricas: el nombre no la convierte en tubería");
        Check(C("OST_Walls").Discipline is null && C("OST_Furniture").Discipline is null, "Muros y mobiliario no se documentan como instalación");
    }

    private static void Visibility()
    {
        var hiddenF = PlanosClassifier.HiddenIn(PlanosDiscipline.Fontaneria);
        Check(hiddenF.Contains("OST_DuctCurves") && hiddenF.Contains("OST_LightingFixtures") && !hiddenF.Contains("OST_PipeCurves") && !hiddenF.Contains("OST_Walls"),
            "Visibilidad: la planta de fontanería oculta conductos y luminarias, no tuberías ni muros");
        var hiddenG = PlanosClassifier.HiddenIn(PlanosDiscipline.Genericas);
        Check(!hiddenG.Contains("OST_Sprinklers") && !hiddenG.Contains("OST_PipeCurves") && hiddenG.Contains("OST_CableTray"), "Visibilidad: genéricas y PCI ven rociadores y tuberías (filtradas por sistema)");
        Check(PlanosClassifier.IsContext("OST_Walls") && PlanosClassifier.IsContext("OST_StructuralColumns") && !PlanosClassifier.IsContext("OST_PipeCurves"), "Contexto: muros y pilares");
        var hvacUnit = C("OST_MechanicalEquipment", "", "", ("DomainHvac", "SupplyAir"));
        var heater = C("OST_MechanicalEquipment", "", "", ("DomainPiping", "DomesticHotWater"));
        var firePump = C("OST_MechanicalEquipment", "", "", ("DomainPiping", "FireProtectWet"));
        Check(!PlanosClassifier.ShownIn(hvacUnit, PlanosDiscipline.Fontaneria, PlanosNetwork.AguaFriaAcs) && PlanosClassifier.ShownIn(hvacUnit, PlanosDiscipline.Climatizacion, PlanosNetwork.Todo),
            "Visibilidad: un climatizador no sale en la planta de fontanería, sí en la de climatización");
        Check(PlanosClassifier.ShownIn(heater, PlanosDiscipline.Fontaneria, PlanosNetwork.Saneamiento) && !PlanosClassifier.ShownIn(heater, PlanosDiscipline.Climatizacion, PlanosNetwork.Todo),
            "Visibilidad: un termo sale en todas las plantas de fontanería y no en climatización");
        Check(PlanosClassifier.ShownIn(firePump, PlanosDiscipline.Genericas, PlanosNetwork.Todo) && !PlanosClassifier.ShownIn(firePump, PlanosDiscipline.Climatizacion, PlanosNetwork.Todo),
            "Visibilidad: el grupo contra incendios sale en genéricas y PCI, no en climatización");
        var loose = C("OST_PipeFitting", "", "", ("DomainPiping", "UndefinedSystemType"));
        Check(PlanosClassifier.ShownIn(loose, PlanosDiscipline.Fontaneria, PlanosNetwork.OtrasFontaneria) && !PlanosClassifier.ShownIn(loose, PlanosDiscipline.Fontaneria, PlanosNetwork.AguaFriaAcs)
              && !PlanosClassifier.ShownIn(loose, PlanosDiscipline.Genericas, PlanosNetwork.Todo), "Visibilidad: una unión sin sistema solo sale en «otras redes» de fontanería");
    }

    private static void Levels()
    {
        var elevations = new[] { 0.0, 3 * M, 6 * M };
        Check(PlanosLevels.Assign(2.8 * M, elevations) == 0, "Niveles: tubería a 2,80 m en la planta baja");
        Check(PlanosLevels.Assign(-0.6 * M, elevations) == 0, "Niveles: saneamiento bajo forjado en el nivel más bajo");
        Check(PlanosLevels.Assign(2.95 * M, elevations) == 1, "Niveles: a 5 cm del nivel superior (tolerancia 0,10 m) → nivel superior");
        Check(PlanosLevels.Assign(9 * M, elevations) == 2 && PlanosLevels.Assign(1, Array.Empty<double>()) == -1, "Niveles: cubierta y proyecto sin niveles");
    }

    private static void Scales()
    {
        var small = new PlanosRect(0, 0, 20 * M, 10 * M);
        Check(PlanosScale.Choose(small, 0, 801, 484).Scale == 50, "Escala: planta de 20 × 10 m en A1 → 1:50");
        var large = new PlanosRect(0, 0, 70 * M, 30 * M);
        Check(PlanosScale.Choose(large, 0, 801, 484).Scale == 100, "Escala: planta de 70 × 30 m en A1 → 1:100 (1:75 no cabe)");
        var huge = new PlanosRect(0, 0, 400 * M, 40 * M);
        var (s, tiles) = PlanosScale.Choose(huge, 0, 801, 484);
        Check(s == 100 && tiles.Count == 5 && tiles.All(t => PlanosScale.Fits(t, 100, 801, 484)), "Escala: planta de 400 m que no cabe ni a 1:200 → 5 partes a 1:100, cada una cabe");
        Check(Math.Abs(tiles.Sum(t => t.Width * t.Height) - huge.Width * huge.Height) < 1e-6, "Escala: las partes cubren toda la planta sin huecos");
        Check(PlanosScale.Choose(small, 100, 801, 484) is { Scale: 100 } r && r.Tiles.Count == 1, "Escala: la escala elegida por el usuario se respeta");
        Check(PlanosScale.Choose(large, 50, 380, 200).Tiles.Count > 1, "Escala: escala elegida que no cabe → se divide, no se cambia la escala");
    }

    private static void Numbering()
    {
        var n = SheetNumbering.Detect(new[] { "A-001", "A-002", "E-010", "Portada" });
        Check(n.Separator == "-" && n.Digits == 3 && n.Detected, "Numeración: detecta «A-001» (guion, 3 cifras)");
        var dot = SheetNumbering.Detect(new[] { "IF.01", "IF.02" });
        Check(dot.Format("F", 4) == "F.04", "Numeración: detecta «IF.01» → F.04");
        var none = SheetNumbering.Detect(new[] { "Portada", "101" });
        Check(!none.Detected && none.Format("FG", 1) == "FG-01", "Numeración: sin convención → FG-01");
        Check(none.Next("F", 3, new[] { "F-01", "F-03" }).SequenceEqual(new[] { "F-02", "F-04", "F-05" }), "Numeración: salta los números ya usados");
    }

    private static void Packing()
    {
        var boxes = new List<(double, double)> { (100, 50), (100, 50), (100, 50), (250, 120), (500, 20) };
        var p = PlanosPacker.Pack(boxes, 300, 150, 10);
        bool noOverlap = true;
        for (int i = 0; i < boxes.Count; i++)
            for (int j = i + 1; j < boxes.Count; j++)
            {
                if (p[i].Page != p[j].Page || p[i].Oversize || p[j].Oversize) continue;
                var a = new PlanosRect(p[i].X, p[i].Y, p[i].X + boxes[i].Item1, p[i].Y + boxes[i].Item2);
                var b = new PlanosRect(p[j].X, p[j].Y, p[j].X + boxes[j].Item1, p[j].Y + boxes[j].Item2);
                if (a.Overlaps(b)) noOverlap = false;
            }
        Check(noOverlap, "Láminas: cuadros y leyendas sin solapes");
        Check(p.Where((x, i) => !x.Oversize).All(x => true) && p.Select((x, i) => x.Oversize || (x.X + boxes[i].Item1 <= 300 && x.Y + boxes[i].Item2 <= 150)).All(b => b),
            "Láminas: todo queda dentro de la zona de dibujo");
        Check(p[4].Oversize && PlanosPacker.Pages(p) == 3, "Láminas: lo que no cabe pasa a otra lámina; lo que no cabe en ninguna va solo y se avisa");
    }

    private static void Tags()
    {
        var c = new[]
        {
            new PlanosTagPicker.Candidate(0, 0, 0, 5), new PlanosTagPicker.Candidate(1, 0.5, 0, 10),
            new PlanosTagPicker.Candidate(2, 10, 0, 1), new PlanosTagPicker.Candidate(3, 20, 0, 1)
        };
        var pick = PlanosTagPicker.Pick(c, 2, 10);
        Check(pick.Contains(1) && !pick.Contains(0) && pick.Count == 3, "Etiquetas: una por tramo, la del tramo más largo, sin amontonarse");
        Check(PlanosTagPicker.Pick(c, 0.1, 2).Count == 2, "Etiquetas: límite por vista");
    }

    private static void Planning()
    {
        var levels = new[] { new PlanosLevel("1", "Nivel 0", 0), new PlanosLevel("2", "Nivel 1", 3 * M), new PlanosLevel("3", "Cubierta", 6 * M) };
        PlanosItem I(PlanosDiscipline d, PlanosNetwork n, int l, double x) => new PlanosItem { Discipline = d, Network = n, Level = l, Box = new PlanosRect(x, 0, x + 1, 1) };
        var items = new List<PlanosItem>
        {
            I(PlanosDiscipline.Fontaneria, PlanosNetwork.AguaFriaAcs, 0, 0), I(PlanosDiscipline.Fontaneria, PlanosNetwork.Saneamiento, 0, 10),
            I(PlanosDiscipline.Fontaneria, PlanosNetwork.Todo, 0, 30), I(PlanosDiscipline.Fontaneria, PlanosNetwork.AguaFriaAcs, 1, 0),
            I(PlanosDiscipline.Fontaneria, PlanosNetwork.Todo, 2, 0),
            I(PlanosDiscipline.Climatizacion, PlanosNetwork.Todo, 1, 0), I(PlanosDiscipline.Iluminacion, PlanosNetwork.Todo, -1, 0)
        };
        var o = new PlanosOptions { PaperWMm = 841, PaperHMm = 594 };
        o.Disciplines.Add(PlanosDiscipline.Fontaneria);
        foreach (var l in levels) o.LevelKeys.Add(l.Key);
        var plan = PlanosPlanner.Plan(levels, items, o);
        Check(plan.Views.Count == 4, "Plan: fontanería → N0 agua y N0 saneamiento, N1 agua, cubierta «planta de fontanería» (4 plantas)");
        Check(plan.Views.Any(v => v.SheetName == "Planta de saneamiento y evacuación - Nivel 0") && plan.Views.Any(v => v.SheetName == "Planta de fontanería - Cubierta"),
            "Plan: nombres de lámina por red y nivel");
        var san = plan.Views.First(v => v.Network == PlanosNetwork.Saneamiento);
        Check(san.Crop.MaxX >= 31 && san.Elements == 2, "Plan: los aparatos sanitarios salen en la planta de saneamiento y amplían su recorte");
        Check(plan.LegendSheets.SequenceEqual(new[] { PlanosDiscipline.Fontaneria }), "Plan: una lámina de leyendas y cuadros solo para la disciplina generada");

        o.LevelKeys.Remove("2");
        Check(PlanosPlanner.Plan(levels, items, o).Views.All(v => v.Level != 1), "Plan: solo los niveles elegidos");
        var o2 = new PlanosOptions { PaperWMm = 841, PaperHMm = 594 };
        o2.Disciplines.Add(PlanosDiscipline.Electricidad);
        foreach (var l in levels) o2.LevelKeys.Add(l.Key);
        Check(PlanosPlanner.Plan(levels, items, o2) is { Views.Count: 0, LegendSheets.Count: 0 }, "Plan: sin elementos de la disciplina no se crean láminas vacías");
        var o3 = new PlanosOptions { PaperWMm = 841, PaperHMm = 594 };
        o3.Disciplines.Add(PlanosDiscipline.Iluminacion);
        foreach (var l in levels) o3.LevelKeys.Add(l.Key);
        Check(PlanosPlanner.Plan(levels, items, o3).Notes.Any(n => n.Contains("sin nivel")), "Plan: avisa de los elementos sin nivel");
        var tiny = new PlanosOptions { PaperWMm = 120, PaperHMm = 100 };
        tiny.Disciplines.Add(PlanosDiscipline.Fontaneria);
        Check(PlanosPlanner.Plan(levels, items, tiny) is { Views.Count: 0 } t && t.Notes.Count == 1, "Plan: zona de dibujo demasiado pequeña → no planifica y lo explica");
    }
    private static void AdversarialClassification()
    {
        // Reverse each mixed set: connector enumeration order must not change ownership or assumptions.
        var mixes = new[]
        {
            (new[] { ("DomainPiping", "DomesticColdWater"), ("DomainPiping", "SupplyHydronic") }, PlanosDiscipline.Climatizacion),
            (new[] { ("DomainPiping", "FireProtectWet"), ("DomainPiping", "Sanitary") }, PlanosDiscipline.Fontaneria),
            (new[] { ("DomainPiping", "FireProtectDry"), ("DomainHvac", "UndefinedSystemType") }, PlanosDiscipline.Climatizacion),
            (new[] { ("DomainElectrical", ""), ("DomainPiping", "FireProtectPreaction") }, PlanosDiscipline.Genericas)
        };
        foreach (var (connectors, expected) in mixes)
        {
            var a = C("OST_MechanicalEquipment", "", "", connectors);
            var b = C("OST_MechanicalEquipment", "", "", connectors.Reverse().ToArray());
            Check(a.Discipline == expected && b.Discipline == expected && !a.AssumedByCategory && !b.AssumedByCategory,
                "Adversarial: equipo con conectores mixtos, prioridad independiente del orden → " + expected);
        }
        foreach (var connectors in new[]
        {
            new[] { ("DomainPiping", "Sanitary"), ("DomainPiping", "SupplyHydronic") },
            new[] { ("DomainPiping", "FireProtectWet"), ("DomainPiping", "DomesticHotWater") }
        })
        {
            foreach (var order in new[] { connectors, connectors.Reverse().ToArray() })
                Check(C("OST_PipeFitting", "", "", order) is { Discipline: PlanosDiscipline.Fontaneria, Network: PlanosNetwork.OtrasFontaneria, AssumedByCategory: true },
                    "Adversarial: unión con disciplinas mezcladas → otras redes, supuesta");
        }
        Check(C("OST_PipeAccessory", "", "", ("DomainPiping", "DomesticColdWater"), ("DomainPiping", "DomesticHotWater"), ("DomainElectrical", ""))
              is { Discipline: PlanosDiscipline.Fontaneria, Network: PlanosNetwork.AguaFriaAcs, AssumedByCategory: false },
            "Adversarial: AF y ACS comparten red; el conector eléctrico no la cambia");
        Check(C("OST_PipeFitting", "", "", ("DomainPiping", "FireProtectWet"), ("DomainPiping", "FireProtectDry"))
              is { Discipline: PlanosDiscipline.Genericas, AssumedByCategory: false }, "Adversarial: unión con dos tipos PCI → PCI");
        Check(C("OST_PipeFitting", "Sanitary", "", ("DomainPiping", "SupplyHydronic"), ("DomainPiping", "FireProtectWet"))
              is { Discipline: PlanosDiscipline.Fontaneria, Network: PlanosNetwork.Saneamiento, AssumedByCategory: false },
            "Adversarial: el sistema explícito prevalece sobre los conectores mixtos");
        Check(C("OST_GenericModel", "", "Bloque", ("DomainPiping", "Sanitary"), ("DomainHvac", "SupplyAir")).Discipline == PlanosDiscipline.Genericas,
            "Adversarial: los conectores de un bloque genérico no sustituyen su categoría");
    }

    private static void AdversarialScales()
    {
        var crop = new PlanosRect(-10, -5, 90, 45);
        double w = PlanosScale.PaperMm(crop.Width, 50), h = PlanosScale.PaperMm(crop.Height, 50) + PlanosScale.TitleMm;
        foreach (int preferred in new[] { 0, 50 })
        {
            var exact = PlanosScale.Choose(crop, preferred, w, h);
            Check(exact.Scale == 50 && exact.Tiles.Count == 1 && exact.Tiles[0].Contains(crop) && crop.Contains(exact.Tiles[0]),
                "Adversarial: recorte exactamente del área, incluido título, escala " + preferred);
        }
        Check(PlanosScale.Choose(crop, 0, w - 1e-6, h).Scale == 75, "Adversarial: un exceso mínimo de anchura descarta 1:50");
        Check(PlanosScale.Choose(crop, 0, w, h - 1e-6).Scale == 75, "Adversarial: un exceso mínimo de altura descarta 1:50");
        Check(PlanosScale.Choose(crop, 50, w - 1e-6, h).Tiles.Count == 2, "Adversarial: borde superado con escala fija → dos partes");
        var line = new PlanosRect(7, -100, 7, 100);
        var vertical = PlanosScale.Choose(line, 50, 300, 100);
        Check(vertical.Scale == 50 && vertical.Tiles.Count > 1 && vertical.Tiles.All(t => t.Width == 0 && t.IsValid && line.Contains(t))
              && Math.Abs(vertical.Tiles.Sum(t => t.Height) - line.Height) < 1e-9,
            "Adversarial: anchura cero, división vertical sin perder longitud");
        var horizontal = PlanosScale.Choose(new PlanosRect(-100, 7, 100, 7), 50, 300, 100);
        Check(horizontal.Tiles.Count > 1 && horizontal.Tiles.All(t => t.Height == 0 && t.IsValid)
              && Math.Abs(horizontal.Tiles.Sum(t => t.Width) - 200) < 1e-9, "Adversarial: altura cero, división horizontal");
        var point = PlanosScale.Choose(new PlanosRect(7, 8, 7, 8), 0, 300, 100);
        Check(point.Scale == 50 && point.Tiles.Count == 1 && point.Tiles[0].IsValid, "Adversarial: recorte puntual no produce divisiones vacías");
        var enormous = PlanosScale.Choose(crop, int.MaxValue, 300, 100);
        Check(enormous.Scale == int.MaxValue && enormous.Tiles.Count == 1 && PlanosScale.Fits(enormous.Tiles[0], enormous.Scale, 300, 100),
            "Adversarial: escala elegida int.MaxValue sin desbordamiento ni sustitución");
        foreach (var (aw, ah) in new[] { (10.0, 100.0), (300.0, PlanosScale.TitleMm + 10) })
        {
            bool rejected = false;
            try { PlanosScale.Choose(crop, 0, aw, ah); } catch (ArgumentException) { rejected = true; }
            Check(rejected, "Adversarial: límite exacto de área insuficiente se rechaza");
        }
    }

    private static void AdversarialNumbering()
    {
        foreach (string prefix in new[] { "A", "IF", "PCI", "ABCD" })
            foreach (string separator in new[] { "", "-", ".", "_", " " })
            {
                var detected = SheetNumbering.Detect(new[] { prefix.ToLowerInvariant() + separator + "0001", prefix + separator + "0003" });
                Check(detected.Detected && detected.Digits == 4 && detected.Separator == separator
                      && detected.Next(prefix, 2, new[] { prefix.ToLowerInvariant() + separator + "0001", prefix + separator + "0003" })
                          .SequenceEqual(new[] { prefix + separator + "0002", prefix + separator + "0004" }),
                    "Adversarial: prefijo " + prefix + ", separador «" + separator + "», huecos y colisión sin distinguir mayúsculas");
            }
        var numbering = new SheetNumbering("-", 2);
        Check(numbering.Next("FG", 3, new[] { "fg-01", "Fg-03", "FG-04", "fg-01", "F-02" }).SequenceEqual(new[] { "FG-02", "FG-05", "FG-06" }),
            "Adversarial: huecos, duplicados, mayúsculas y otro prefijo");
        Check(numbering.Next("F", 0, Array.Empty<string>()).Count == 0, "Adversarial: solicitar cero números no reserva ninguno");
        Check(numbering.Format("F", 100) == "F-100", "Adversarial: las cifras mínimas no truncan números mayores");
        Check(!SheetNumbering.Detect(new[] { "ABCDE-01", "A-00001", "A--01", "01" }).Detected, "Adversarial: rechaza convenciones fuera de los límites");
        Check(SheetNumbering.Detect(new[] { "A.01", "b.02", "C-001" }).Format("F", 4) == "F.04", "Adversarial: prevalece la convención mayoritaria");
        Check(numbering.Next("F", 1, Enumerable.Range(1, 9998).Select(n => numbering.Format("F", n)).ToArray()).SequenceEqual(new[] { "F-9999" }),
            "Adversarial: último número libre de cuatro cifras");
    }

    private static bool ValidPacking(IReadOnlyList<(double W, double H)> boxes, IReadOnlyList<PlanosPacker.Placement> positions, double width, double height)
    {
        if (positions.Count != boxes.Count) return false;
        for (int i = 0; i < boxes.Count; i++)
        {
            var a = positions[i];
            var box = new PlanosRect(a.X, a.Y, a.X + boxes[i].W, a.Y + boxes[i].H);
            if (a.Page < 0 || a.Oversize != (boxes[i].W > width || boxes[i].H > height)) return false;
            if (!a.Oversize && !new PlanosRect(0, 0, width, height).Contains(box)) return false;
            for (int j = i + 1; j < boxes.Count; j++)
            {
                var b = positions[j];
                if (a.Page != b.Page) continue;
                if (a.Oversize || b.Oversize || box.Overlaps(new PlanosRect(b.X, b.Y, b.X + boxes[j].W, b.Y + boxes[j].H))) return false;
            }
        }
        return positions.Select(p => p.Page).Distinct().OrderBy(p => p).SequenceEqual(Enumerable.Range(0, PlanosPacker.Pages(positions)));
    }

    private static void AdversarialPacking()
    {
        var empty = PlanosPacker.Pack(Array.Empty<(double, double)>(), 300, 150, 10);
        Check(empty.Count == 0 && PlanosPacker.Pages(empty) == 0, "Adversarial: ninguna caja → ninguna lámina");
        var exact = Enumerable.Repeat((300.0, 150.0), 3).ToArray();
        var full = PlanosPacker.Pack(exact, 300, 150, 10);
        Check(ValidPacking(exact, full, 300, 150) && full.Select(p => p.Page).SequenceEqual(new[] { 0, 1, 2 })
              && full.All(p => p.X == 0 && p.Y == 0 && !p.Oversize), "Adversarial: cajas exactamente del área ocupan una lámina cada una");
        var touching = Enumerable.Repeat((150.0, 75.0), 4).ToArray();
        var tiled = PlanosPacker.Pack(touching, 300, 150, 0);
        Check(ValidPacking(touching, tiled, 300, 150) && PlanosPacker.Pages(tiled) == 1, "Adversarial: cuatro cajas tocan bordes sin solaparse");
        var many = Enumerable.Range(0, 1000).Select(i => (W: 10.0 + i % 7 * 11, H: 5.0 + i % 5 * 9)).ToArray();
        var packed = PlanosPacker.Pack(many, 300, 150, 8);
        Check(ValidPacking(many, packed, 300, 150) && PlanosPacker.Pages(packed) > 1, "Adversarial: mil cajas distintas, sin pérdidas, solapes ni láminas vacías");
        var mixed = new[] { (301.0, 10.0), (10.0, 151.0), (20.0, 20.0), (301.0, 151.0), (300.0, 150.0) };
        var isolated = PlanosPacker.Pack(mixed, 300, 150, 8);
        Check(ValidPacking(mixed, isolated, 300, 150) && PlanosPacker.Pages(isolated) == 5, "Adversarial: cajas sobredimensionadas consecutivas quedan solas, sin huecos de numeración");
    }

    private static void AdversarialPlanning()
    {
        var levels = new[] { new PlanosLevel("N0", "Baja", 0), new PlanosLevel("N1", "Alta", 10) };
        var options = new PlanosOptions();
        options.Disciplines.Add(PlanosDiscipline.Fontaneria);
        var items = new[]
        {
            new PlanosItem { Discipline = PlanosDiscipline.Fontaneria, Network = PlanosNetwork.Todo, Level = 0, Box = new PlanosRect(0, 0, 1, 1) },
            new PlanosItem { Discipline = PlanosDiscipline.Fontaneria, Network = PlanosNetwork.Todo, Level = 0, Box = new PlanosRect(3, 2, 4, 3) },
            new PlanosItem { Discipline = PlanosDiscipline.Fontaneria, Network = PlanosNetwork.Saneamiento, Level = 1, Box = new PlanosRect(10000, 10000, 20000, 20000) }
        };
        Check(PlanosPlanner.Plan(levels, items, options) is { Views.Count: 0, LegendSheets.Count: 0 }, "Adversarial: ningún nivel elegido → no genera documentación");
        options.LevelKeys.Add("N0");
        var todo = PlanosPlanner.Plan(levels, items, options);
        var expected = new PlanosRect(0, 0, 4, 3).Grow(PlanosScale.CropMarginFeet);
        Check(todo.Views.Count == 1 && todo.Views[0] is { Network: PlanosNetwork.Todo, Elements: 2, Level: 0, Parts: 1 }
              && todo.Views[0].Crop.Contains(expected) && expected.Contains(todo.Views[0].Crop),
            "Adversarial: red Todo sola → una planta; el nivel excluido no amplía su recorte");
        Check(todo.LegendSheets.SequenceEqual(new[] { PlanosDiscipline.Fontaneria }), "Adversarial: Todo sola conserva leyendas y cuadros");
        options.LevelKeys.Clear(); options.LevelKeys.Add("inexistente");
        Check(PlanosPlanner.Plan(levels, items, options) is { Views.Count: 0, LegendSheets.Count: 0 }, "Adversarial: clave de nivel desconocida → ninguna vista");
        options.LevelKeys.Clear(); options.LevelKeys.Add("N0");
        options.Scale = 50; options.PaperWMm = 320; options.PaperHMm = 270; options.Schedules = false;
        var wide = new PlanosRect(-100, -50, 100, 50);
        var divided = PlanosPlanner.Plan(levels, new[] { new PlanosItem { Discipline = PlanosDiscipline.Fontaneria, Network = PlanosNetwork.AguaFriaAcs, Level = 0, Box = wide } }, options);
        var grown = wide.Grow(PlanosScale.CropMarginFeet);
        int nx = (int)Math.Ceiling(PlanosScale.PaperMm(grown.Width, 50) / options.AreaWMm);
        int ny = (int)Math.Ceiling(PlanosScale.PaperMm(grown.Height, 50) / (options.AreaHMm - PlanosScale.TitleMm));
        Check(nx > 1 && ny > 1 && divided.Views.Count == nx * ny && divided.Views.All(v => v.Scale == 50 && v.Parts == nx * ny
              && v.Level == 0 && v.Network == PlanosNetwork.AguaFriaAcs && grown.Contains(v.Crop)
              && PlanosScale.Fits(v.Crop, 50, options.AreaWMm, options.AreaHMm)), "Adversarial: división en ambos ejes, escala fija y todas las partes dentro del área");
        Check(Math.Abs(divided.Views.Sum(v => v.Crop.Width * v.Crop.Height) - grown.Width * grown.Height) < 1e-6
              && divided.Views.Select((v, i) => divided.Views.Skip(i + 1).All(o => !v.Crop.Overlaps(o.Crop))).All(ok => ok),
            "Adversarial: las divisiones cubren el recorte sin huecos ni solapes");
        Check(divided.Views.Select(v => v.Part).SequenceEqual(Enumerable.Range(0, nx * ny))
              && divided.Views.Select(v => v.Name).Distinct().Count() == nx * ny
              && divided.Views.All(v => v.Name.Contains(PlanosNaming.Part(v.Part, v.Parts)) && v.SheetName.Contains(PlanosNaming.Part(v.Part, v.Parts))),
            "Adversarial: partes consecutivas con nombres únicos y numeración visible");
        Check(divided.Views[0].Crop.MinX == grown.MinX && Math.Abs(divided.Views[0].Crop.MaxY - grown.MaxY) < 1e-9
              && divided.Notes.Count == 1 && divided.LegendSheets.Count == 0, "Adversarial: empieza arriba a la izquierda, explica la división y respeta cuadros desactivados");
        Check(PlanosPlanner.Plan(Array.Empty<PlanosLevel>(), items, options) is { Views.Count: 0, LegendSheets.Count: 0 }, "Adversarial: sin niveles disponibles → ninguna documentación");
    }


    private static void BoundsIndexEquivalence()
    {
        var rng = new Random(7319);
        var boxes = new List<PlanosRect>
        {
            new PlanosRect(0, 0, 0, 0), new PlanosRect(-10, -10, 0, 0),
            new PlanosRect(0, 0, 0, 0), new PlanosRect(-1e100, -1e100, 1e100, 1e100),
            new PlanosRect(double.NaN, 0, 1, 1), new PlanosRect(5, 5, -5, -5),
            new PlanosRect(double.NegativeInfinity, 0, double.PositiveInfinity, 1)
        };
        for (int i = 0; i < 2000; i++)
        {
            double x = rng.NextDouble() * 10000 - 5000, y = rng.NextDouble() * 10000 - 5000;
            boxes.Add(new PlanosRect(x, y, x + rng.NextDouble() * 200, y + rng.NextDouble() * 200));
        }
        var index = new PlanosBoundsIndex(boxes);
        var crops = new List<PlanosRect>
        {
            new PlanosRect(0, 0, 0, 0), new PlanosRect(-20, -20, 0, 0),
            new PlanosRect(-1e101, -1e101, 1e101, 1e101), new PlanosRect(-3000, -3000, 3000, 3000),
            new PlanosRect(double.NaN, 0, 0, 0), new PlanosRect(1, 1, -1, -1),
            new PlanosRect(double.NegativeInfinity, double.NegativeInfinity, double.PositiveInfinity, double.PositiveInfinity)
        };
        for (int i = 0; i < 200; i++)
        {
            double x = rng.NextDouble() * 12000 - 6000, y = rng.NextDouble() * 12000 - 6000;
            crops.Add(new PlanosRect(x, y, x + rng.NextDouble() * 1000, y + rng.NextDouble() * 1000));
        }
        bool same = crops.All(crop => index.Query(crop).SequenceEqual(Enumerable.Range(0, boxes.Count).Where(i =>
            !(boxes[i].MaxX < crop.MinX || boxes[i].MinX > crop.MaxX || boxes[i].MaxY < crop.MinY || boxes[i].MinY > crop.MaxY))));
        Check(same, "Bounds index: exact inclusive crop predicate and inventory order, randomized and malformed bounds");
        Check(new PlanosBoundsIndex(Array.Empty<PlanosRect>()).Query(crops[0]).Count == 0, "Bounds index: empty inventory");
    }

    private static void PlannerLookupEquivalence()
    {
        var rng = new Random(941);
        var levels = Enumerable.Range(0, 24).Select(i => new PlanosLevel("L" + i, "Level " + i, i * 10)).ToList();
        var items = new List<PlanosItem>();
        for (int i = 0; i < 4000; i++)
        {
            double x = rng.NextDouble() * 500, y = rng.NextDouble() * 300;
            items.Add(new PlanosItem { Discipline = (PlanosDiscipline)rng.Next(5), Network = (PlanosNetwork)rng.Next(4),
                Level = rng.Next(-2, 27), Box = new PlanosRect(x, y, x + 10, y + 5) });
        }
        string Describe(PlannedView v) => string.Join("|", v.Discipline, v.Network, v.Level, v.LevelName, v.Name, v.SheetName,
            v.Scale, v.Crop.MinX.ToString("R"), v.Crop.MinY.ToString("R"), v.Crop.MaxX.ToString("R"), v.Crop.MaxY.ToString("R"), v.Part, v.Parts, v.Elements);
        bool same = true;
        for (int run = 0; run < 12; run++)
        {
            var options = new PlanosOptions { Scale = run % 2 == 0 ? 0 : 50, Schedules = run % 3 != 0 };
            options.Disciplines.AddRange(new[] { PlanosDiscipline.Genericas, PlanosDiscipline.Fontaneria, PlanosDiscipline.Iluminacion,
                PlanosDiscipline.Climatizacion, PlanosDiscipline.Electricidad, PlanosDiscipline.Fontaneria });
            foreach (var l in levels.Where((_, i) => (i + run) % 3 != 0)) options.LevelKeys.Add(l.Key);
            var expected = ReferencePlan(levels, items, options);
            var actual = PlanosPlanner.Plan(levels, items, options);
            same &= actual.Views.Select(Describe).SequenceEqual(expected.Views.Select(Describe)) &&
                actual.Notes.SequenceEqual(expected.Notes) && actual.LegendSheets.SequenceEqual(expected.LegendSheets);
        }
        Check(same, "Planner lookup: every view field, bounds, count, note and order match the previous scan, including duplicate disciplines");
    }

    // Pre-optimization scan kept as a differential oracle for grouping, ordering and reports.
    private static PlanosPlan ReferencePlan(IReadOnlyList<PlanosLevel> levels, IReadOnlyList<PlanosItem> items, PlanosOptions options)
    {
        var plan = new PlanosPlan();
        if (options.AreaWMm < 60 || options.AreaHMm < 60)
        {
            plan.Notes.Add($"La zona de dibujo de la lámina ({options.AreaWMm:0} × {options.AreaHMm:0} mm) es demasiado pequeña: reduzca los márgenes o la reserva del cajetín, o elija un formato mayor.");
            return plan;
        }
        int unlevelled = items.Count(i => i.Level < 0 && options.Disciplines.Contains(i.Discipline));
        if (unlevelled > 0) plan.Notes.Add($"{unlevelled} elementos sin nivel ni altura medible no se pueden situar en ninguna planta.");

        foreach (var d in options.Disciplines)
        {
            var mine = items.Where(i => i.Discipline == d && i.Level >= 0).ToList();
            for (int l = 0; l < levels.Count; l++)
            {
                if (!options.LevelKeys.Contains(levels[l].Key)) continue;
                var onLevel = mine.Where(i => i.Level == l).ToList();
                if (onLevel.Count == 0) continue;
                var networks = d == PlanosDiscipline.Fontaneria
                    ? PlanosPlanner.PlumbingNetworks.Where(n => onLevel.Any(i => i.Network == n)).ToList()
                    : new List<PlanosNetwork> { PlanosNetwork.Todo };
                if (networks.Count == 0) networks.Add(PlanosNetwork.Todo);
                foreach (var n in networks)
                {
                    var shown = onLevel.Where(i => n == PlanosNetwork.Todo || i.Network == n || i.Network == PlanosNetwork.Todo).ToList();
                    if (PlanosRect.Union(shown.Select(i => i.Box)) is not { } box) continue;
                    var crop = box.Grow(PlanosScale.CropMarginFeet);
                    var (scale, tiles) = PlanosScale.Choose(crop, options.Scale, options.AreaWMm, options.AreaHMm);
                    for (int t = 0; t < tiles.Count; t++)
                        plan.Views.Add(new PlannedView
                        {
                            Discipline = d, Network = n, Level = l, LevelName = levels[l].Name, Scale = scale, Crop = tiles[t], Part = t, Parts = tiles.Count,
                            Elements = shown.Count,
                            Name = PlanosNaming.ViewName(d, n, levels[l].Name, t, tiles.Count),
                            SheetName = PlanosNaming.SheetName(d, n, levels[l].Name, t, tiles.Count)
                        });
                    if (tiles.Count > 1)
                        plan.Notes.Add($"{PlanosNaming.PlanTitle(d, n)} - {levels[l].Name}: no cabe en una lámina a 1:{scale}; se divide en {tiles.Count} partes.");
                }
            }
            if (options.Schedules && plan.Views.Any(v => v.Discipline == d)) plan.LegendSheets.Add(d);
        }
        return plan;
    }
}
