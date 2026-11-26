using RevitAutomation.Addin.Commands.PipeTagging;

// Pure-logic checks for "Etiquetar tuberías seleccionadas". No Revit API: Revit behaviour still needs a manual test.
internal static class Program
{
    private static int _pass, _fail;

    private static void Check(bool condition, string name)
    {
        if (condition) { _pass++; Console.WriteLine("PASS: " + name); }
        else { _fail++; Console.WriteLine("FAIL: " + name); }
    }

    // A tag box 1.0 x 0.25 ft whose centre sits 0.2 ft above its head (like a label written above the pipe).
    private static PipeTagRequest Request(string key, double x0, double y0, double x1, double y1, double w = 1.0, double h = 0.25, double above = 0.2)
    {
        var mid = new TagPoint((x0 + x1) / 2, (y0 + y1) / 2);
        var centre = mid + new TagPoint(0, above);
        return new PipeTagRequest
        {
            Key = key,
            Start = new TagPoint(x0, y0),
            End = new TagPoint(x1, y1),
            MeasuredHead = mid,
            MeasuredBox = new TagBox(centre.X - w / 2, centre.Y - h / 2, centre.X + w / 2, centre.Y + h / 2)
        };
    }

    private static bool NoOverlap(IReadOnlyList<PipeTagPlacement> placed, IReadOnlyList<TagBox> obstacles, double gap)
    {
        for (int i = 0; i < placed.Count; i++)
        {
            foreach (var o in obstacles) if (placed[i].Box.Overlaps(o, gap * 0.999)) return false;
            for (int j = i + 1; j < placed.Count; j++)
                if (placed[i].Box.Overlaps(placed[j].Box, gap * 0.999)) return false;
        }
        return true;
    }

    // A luminaire symbol 0.6 x 0.6 ft centred at (x, y) and its tag (1.0 x 0.25) first created on top of it.
    private static FamilyTagRequest Fixture(string key, double x, double y, double w = 1.0, double h = 0.25)
    {
        var centre = new TagPoint(x, y);
        return new FamilyTagRequest
        {
            Key = key,
            Target = new TagBox(x - 0.3, y - 0.3, x + 0.3, y + 0.3),
            Anchor = centre,
            MeasuredHead = centre,
            MeasuredBox = new TagBox(x - w / 2, y - h / 2, x + w / 2, y + h / 2)
        };
    }

    private static bool SamePlacements(IReadOnlyList<PipeTagPlacement> a, IReadOnlyList<PipeTagPlacement> b)
        => a.Count == b.Count && a.Zip(b).All(pair =>
            pair.First.Key == pair.Second.Key && pair.First.Head == pair.Second.Head && pair.First.Box == pair.Second.Box
            && pair.First.Kind == pair.Second.Kind && pair.First.LeaderEnd == pair.Second.LeaderEnd
            && pair.First.PipeParameter == pair.Second.PipeParameter && pair.First.Target == pair.Second.Target
            && pair.First.AxisOffset == pair.Second.AxisOffset);

    private static void SpatialChecks(double gap)
    {
        var random = new Random(46381);
        bool pipesAgree = true, familiesAgree = true, conflictsAgree = true;
        for (int scene = 0; scene < 16; scene++)
        {
            double Coordinate() => random.NextDouble() * 60 - 30;
            var obstacles = Enumerable.Range(0, 50).Select(_ =>
            {
                double x = Coordinate(), y = Coordinate();
                return new TagBox(x, y, x + random.NextDouble() * 3, y + random.NextDouble());
            }).ToList();
            // Overflow buckets and cell boundaries, including geometry too large for the grid.
            obstacles.Add(new TagBox(-10000, -10000, 10000, -9999));
            obstacles.Add(new TagBox(-4, -4, 0, 0));
            obstacles.Add(new TagBox(double.NaN, 0, 1, 1));
            var runs = Enumerable.Range(0, 30).Select(_ => new RunSegment(
                new TagPoint(Coordinate(), Coordinate()), new TagPoint(Coordinate(), Coordinate()), 0.05)).ToList();
            runs.Add(new RunSegment(new TagPoint(-10000, -100), new TagPoint(10000, -100), 0.2));
            var requests = Enumerable.Range(0, 20).Select(i => Request(i.ToString(), Coordinate(), Coordinate(), Coordinate(), Coordinate())).ToList();
            var expected = new List<PipeTagPlacement>();
            var missing = new List<string>();
            foreach (var request in requests.OrderByDescending(r => r.ProjectedLength).ThenBy(r => r.Key, StringComparer.Ordinal))
            {
                var placement = PipeTagLayout.Place(request, obstacles, expected, gap, null, runs);
                if (placement is null) missing.Add(request.Key); else expected.Add(placement);
            }
            var actual = PipeTagLayout.PlaceAll(requests, obstacles, gap, null, runs);
            pipesAgree &= SamePlacements(expected, actual.Placed) && missing.SequenceEqual(actual.Unplaced);

            var fixtures = Enumerable.Range(0, 20).Select(i => Fixture(i.ToString(), Coordinate(), Coordinate())).ToList();
            var familyObstacles = obstacles.Concat(fixtures.Select(f => f.Target)).ToList();
            expected.Clear(); missing.Clear();
            foreach (var request in fixtures.OrderByDescending(r => Math.Round(r.Target.Center.Y, 3))
                .ThenBy(r => r.Target.Center.X).ThenBy(r => r.Key, StringComparer.Ordinal))
            {
                var placement = FamilyTagLayout.Place(request, familyObstacles, expected, gap, null);
                if (placement is null) missing.Add(request.Key); else expected.Add(placement);
            }
            var families = FamilyTagLayout.PlaceAll(fixtures, familyObstacles, gap, null);
            familiesAgree &= SamePlacements(expected, families.Placed) && missing.SequenceEqual(families.Unplaced);

            // Final checking also indexes leaders, and only accepted predecessors may block later tags.
            var measured = families.Placed.Concat(actual.Placed).ToList();
            measured.Add(new PipeTagPlacement { Key = "long-leader", Box = new TagBox(-100, 20, -99, 21), LeaderEnd = new TagPoint(100, -20) });
            var kept = new List<PipeTagPlacement>();
            missing.Clear();
            foreach (var placement in measured)
                if (PipeTagLayout.IsFree(placement, familyObstacles, kept, gap * 0.9, null, runs)) kept.Add(placement);
                else missing.Add(placement.Key);
            conflictsAgree &= missing.SequenceEqual(PipeTagLayout.Conflicts(measured, familyObstacles, gap, runs));
        }
        Check(pipesAgree, "indexed pipe layout matches linear checks exactly (seeded scenes)");
        Check(familiesAgree, "indexed family layout matches linear checks exactly (seeded scenes)");
        Check(conflictsAgree, "indexed final conflict checks match linear checks and predecessor order");
    }

    private static void FamilyChecks(double gap)
    {
        // --- Rules.
        Check(FamilyTagRules.ExclusionReason(FamilyTagElementKind.FamilyInstance, false, true, true) is null, "family: luminaire with a tag type is tagged");
        Check(FamilyTagRules.ExclusionReason(FamilyTagElementKind.LinearRun, false, true, true)!.Contains("botones 1, 2 o 3"), "family: pipes/ducts/trays sent to buttons 1-3");
        Check(FamilyTagRules.ExclusionReason(FamilyTagElementKind.RunFitting, false, true, true) is not null, "family: network fittings excluded");
        Check(FamilyTagRules.ExclusionReason(FamilyTagElementKind.NotLoadableFamily, false, true, true)!.Contains("muro"), "family: walls and system elements excluded");
        Check(FamilyTagRules.ExclusionReason(FamilyTagElementKind.FamilyInstance, true, true, true)!.Contains("Ya tiene"), "family: already tagged not duplicated");
        Check(FamilyTagRules.ExclusionReason(FamilyTagElementKind.FamilyInstance, false, false, true)!.Contains("No hay ninguna etiqueta"), "family: no tag for its category reported");
        Check(FamilyTagRules.ExclusionReason(FamilyTagElementKind.FamilyInstance, false, true, false) is not null, "family: no visible symbol reported");

        // --- Free space: beside the symbol, above and centred, never covering it.
        var f = Fixture("A", 0, 0);
        var obstacles = new List<TagBox> { f.Target };
        var p = FamilyTagLayout.Place(f, obstacles, new List<PipeTagPlacement>(), gap, null)!;
        Check(p.Kind == PipeTagPlacementKind.Beside && p.LeaderEnd is null, "family: free space -> beside, no leader");
        Check(p.Box.MinY > f.Target.MaxY && Math.Abs(p.Box.Center.X) < 1e-9, "family: first choice is above and centred");
        Check(!p.Box.Overlaps(f.Target, gap), "family: tag never covers its own symbol");
        Check((p.Head - f.MeasuredHead) == (p.Box.Center - f.MeasuredBox.Center), "family: head moves with the box (family offset kept)");

        // Above occupied -> right side.
        var above = new TagBox(-2, 0.35, 2, 1.5);
        p = FamilyTagLayout.Place(f, new List<TagBox> { f.Target, above }, new List<PipeTagPlacement>(), gap, null)!;
        Check(p.Kind == PipeTagPlacementKind.Beside && p.Box.MinX > f.Target.MaxX, "family: above occupied -> to the right");

        // Closed ring of texts around the element: any leader would cross it -> not placed, reported.
        var ring = new List<TagBox> { f.Target, new(-3, 0.32, 3, 3), new(-3, -3, 3, -0.32), new(0.32, -3, 3, 3), new(-3, -3, -0.32, 3) };
        Check(FamilyTagLayout.Place(f, ring, new List<PipeTagPlacement>(), gap, null) is null, "family: element enclosed by texts -> reported, nothing crossed");

        // Small texts on the 8 spots beside the symbol, with room further out -> leader to the element.
        TagBox Shrink(TagBox b, double k) { var c = b.Center; double hw = b.Width * k / 2, hh = b.Height * k / 2; return new TagBox(c.X - hw, c.Y - hh, c.X + hw, c.Y + hh); }
        var slots = new List<TagBox> { f.Target };
        slots.AddRange(FamilyTagLayout.Candidates(f, gap).Take(FamilyTagLayout.Sides.Length).Select(c => Shrink(c.Box, 0.3)));
        p = FamilyTagLayout.Place(f, slots, new List<PipeTagPlacement>(), gap, null);
        Check(p is not null && p.Kind == PipeTagPlacementKind.OffsetWithLeader && p.LeaderEnd == f.Anchor, "family: no room beside -> leader to the element");
        var l = PipeTagLayout.Leader(p!)!.Value;
        Check(slots.Skip(1).All(o => !o.Overlaps(p!.Box, gap) && !o.IntersectsSegment(l.From, l.To, 0)), "family: leader tag and leader clear every text");
        Check(PipeTagLayout.Conflicts(new[] { p! }, slots, gap).Count == 0, "family: leader may enter only its own symbol");

        // Another luminaire right next to it: a leader must not cross its symbol.
        var other = Fixture("B", 0, 1.0);
        var withOther = new List<TagBox> { f.Target, other.Target };
        p = FamilyTagLayout.Place(f, withOther, new List<PipeTagPlacement>(), gap, null)!;
        Check(!p.Box.Overlaps(other.Target, gap) && (PipeTagLayout.Leader(p) is not { } l2 || !other.Target.IntersectsSegment(l2.From, l2.To, 0)), "family: tag and leader never cover a neighbouring element");

        // --- Stress: dense ceiling grid of luminaires plus existing texts.
        var rnd = new Random(7);
        var fixtures = new List<FamilyTagRequest>();
        for (int i = 0; i < 12; i++)
            for (int j = 0; j < 12; j++)
                fixtures.Add(Fixture($"L{i}-{j}", i * 2.4 + rnd.NextDouble() * 0.2, j * 1.8 + rnd.NextDouble() * 0.2, 0.9 + rnd.NextDouble() * 0.5, 0.22));
        var all = fixtures.Select(x => x.Target).ToList();
        all.AddRange(Enumerable.Range(0, 15).Select(_ => { double x = rnd.NextDouble() * 26, y = rnd.NextDouble() * 20; return new TagBox(x, y, x + 1.1, y + 0.3); }));
        var (placed, unplaced) = FamilyTagLayout.PlaceAll(fixtures, all, gap, null);
        Check(placed.Count + unplaced.Count == fixtures.Count, "family stress: every element placed once or reported");
        Check(NoOverlap(placed, all, gap), $"family stress: no tag covers a tag, text or symbol ({placed.Count} placed, {unplaced.Count} reported)");
        bool leadersOk = placed.All(a => PipeTagLayout.Leader(a) is not { } ld
            || (all.Where(o => o != a.Target).All(o => !o.IntersectsSegment(ld.From, ld.To, 0)) && placed.All(o => ReferenceEquals(o, a) || !o.Box.IntersectsSegment(ld.From, ld.To, 0))));
        Check(leadersOk, "family stress: leaders only enter their own element");
        Check(PipeTagLayout.Conflicts(placed, all, gap).Count == 0, "family stress: final verification accepts the layout (own symbol allowed for the leader)");
        Check(placed.Count(x => x.Kind == PipeTagPlacementKind.Beside) >= placed.Count * 0.8, "family stress: most tags sit beside their symbol without leader");
    }

    // --- One tag per run: aligned pieces of the same type and size form one run (plans converted from CAD are cut in pieces).
    private static void RunChecks()
    {
        var n = MepTagNouns.Pipe;
        Check(PipeTagRuns.SameLine((0, 0, 0), (1, 0, 0), (1.02, 0.01, 0), (3, 0.01, 0)), "runs: piece in line 3 mm off the axis is the same run");
        Check(!PipeTagRuns.SameLine((0, 0, 0), (1, 0, 0), (1, 0, 0), (1, 2, 0)), "runs: a piece at 90 deg (after an elbow) is another run");
        Check(!PipeTagRuns.SameLine((0, 0, 0), (1, 0, 0), (1, 0.5, 0), (3, 0.5, 0)), "runs: a parallel pipe 15 cm away is another run");
        Check(!PipeTagRuns.SameLine((0, 0, 0), (1, 0, 0), (1, 0, 0), (3, 0, 0.3)), "runs: a piece that rises (slope) is another run");

        // Ten pieces of 20 cm in a line (a CAD run cut in pieces), all selected → one tag, on one piece.
        var pieces = Enumerable.Range(0, 10).Select(i => new RunPiece("p" + i, 0.20 + (i == 6 ? 0.01 : 0), 0.7, true, false)).ToList();
        var links = Enumerable.Range(0, 9).Select(i => ("p" + i, "p" + (i + 1))).ToList();
        var runs = PipeTagRuns.Decide(pieces, links, n);
        Check(runs.Count == 1 && runs[0].Chosen?.Key == "p6" && Math.Abs(runs[0].LengthMetres - 2.01) < 1e-9,
            "runs: 10 pieces in line → 1 run, 1 tag on its longest piece, length of the whole run");

        // Pieces of 10 cm: each is too short, the run (40 cm) is not.
        var shortPieces = Enumerable.Range(0, 4).Select(i => new RunPiece("s" + i, 0.10, 0.3, true, false)).ToList();
        var shortRuns = PipeTagRuns.Decide(shortPieces, new[] { ("s0", "s1"), ("s1", "s2"), ("s2", "s3") }, n);
        Check(shortRuns.Count == 1 && shortRuns[0].Chosen is not null, "runs: the 15 cm minimum applies to the run, not to each piece");

        // Already tagged on another (even unselected) piece of the run → not duplicated.
        var tagged = new List<RunPiece> { new("a", 1, 3, true, false), new("b", 1, 3, false, true) };
        var taggedRuns = PipeTagRuns.Decide(tagged, new[] { ("a", "b") }, n);
        Check(taggedRuns.Count == 1 && taggedRuns[0].Chosen is null && taggedRuns[0].Reason!.Contains("otra de sus piezas"),
            "runs: run already tagged on another piece → no second tag, explained");

        // Two separate runs (not linked) → two tags; a run with only unselected pieces is ignored.
        var two = new List<RunPiece> { new("x", 2, 6, true, false), new("y", 3, 9, true, false), new("z", 3, 9, false, false) };
        var twoRuns = PipeTagRuns.Decide(two, Array.Empty<(string, string)>(), n);
        Check(twoRuns.Count == 2 && twoRuns.All(r => r.Chosen is not null), "runs: separate runs get one tag each; unselected runs are not tagged");

        // Single piece: same wording as before.
        var single = PipeTagRuns.Decide(new[] { new RunPiece("o", 1, 3, true, true) }, Array.Empty<(string, string)>(), n);
        Check(single[0].Reason == "Ya tiene una etiqueta de tubería en esta vista; no se duplica.", "runs: single tagged piece keeps the original message");
    }

    private static int Main(string[] args)
    {
        RunChecks();
        // --- Rule: straight runs longer than 15 cm only.
        Check(!PipeTagRules.IsLongEnough(0.15), "exactly 15 cm is not tagged (must be longer)");
        Check(!PipeTagRules.IsLongEnough(0.15 + 1e-9), "15 cm plus conversion noise is not tagged");
        Check(PipeTagRules.IsLongEnough(0.151), "15.1 cm is tagged");
        Check(!PipeTagRules.IsLongEnough(0.10), "10 cm is not tagged");
        Check(!PipeTagRules.IsLongEnough(double.NaN), "unreadable length is not tagged");
        Check(PipeTagRules.ExclusionReason(PipeTagElementKind.Pipe, 1.0, false, 1.0) is null, "1 m pipe visible in length: tagged");
        Check(PipeTagRules.ExclusionReason(PipeTagElementKind.PipeFitting, 1.0, false, 1.0)!.Contains("codo"), "elbow/tee/fitting excluded");
        Check(PipeTagRules.ExclusionReason(PipeTagElementKind.PipeAccessory, 1.0, false, 1.0) is not null, "accessory excluded");
        Check(PipeTagRules.ExclusionReason(PipeTagElementKind.FlexPipe, 1.0, false, 1.0) is not null, "flex pipe excluded");
        Check(PipeTagRules.ExclusionReason(PipeTagElementKind.PipePlaceholder, 1.0, false, 1.0) is not null, "placeholder excluded");
        Check(PipeTagRules.ExclusionReason(PipeTagElementKind.Other, 1.0, false, 1.0) is not null, "non-pipe excluded");
        Check(PipeTagRules.ExclusionReason(PipeTagElementKind.Pipe, 0.12, false, 1.0)!.Contains("12 cm"), "short run excluded with its length");
        Check(PipeTagRules.ExclusionReason(PipeTagElementKind.Pipe, 1.0, true, 1.0)!.Contains("Ya tiene"), "already tagged: not duplicated");
        Check(PipeTagRules.ExclusionReason(PipeTagElementKind.Pipe, 1.0, false, 0.0)!.Contains("de punta"), "pipe seen end-on: excluded");
        Check(Math.Abs(PipeTagRules.PaperGapFeet(100) - 100.0 / 304.8) < 1e-12, "1 mm paper gap at 1:100 = 100 mm model");

        // --- Ducts: same rule, duct wording; pipe wording unchanged by default.
        var duct = MepTagNouns.Duct;
        Check(PipeTagRules.ExclusionReason(PipeTagElementKind.Pipe, 2.0, false, 1.0, duct) is null, "2 m duct visible in length: tagged");
        Check(PipeTagRules.ExclusionReason(PipeTagElementKind.Pipe, 0.15, false, 1.0, duct) is not null, "duct of exactly 15 cm is not tagged");
        Check(PipeTagRules.ExclusionReason(PipeTagElementKind.PipeFitting, 2.0, false, 1.0, duct)!.Contains("codo"), "duct fitting (elbow, tee, transition) excluded");
        Check(PipeTagRules.ExclusionReason(PipeTagElementKind.PipeAccessory, 2.0, false, 1.0, duct)!.Contains("accesorio de conducto (compuerta"), "duct accessory excluded with duct wording");
        Check(PipeTagRules.ExclusionReason(PipeTagElementKind.FlexPipe, 2.0, false, 1.0, duct)!.Contains("conducto flexible"), "flex duct excluded with duct wording");
        Check(PipeTagRules.ExclusionReason(PipeTagElementKind.PipePlaceholder, 2.0, false, 1.0, duct)!.Contains("no un conducto real"), "duct placeholder excluded");
        Check(PipeTagRules.ExclusionReason(PipeTagElementKind.Other, 2.0, false, 1.0, duct)!.Contains("No es un conducto"), "non-duct (e.g. air terminal) excluded");
        Check(PipeTagRules.ExclusionReason(PipeTagElementKind.Pipe, 2.0, true, 1.0, duct)!.Contains("etiqueta de conducto"), "duct already tagged: not duplicated");
        Check(!PipeTagRules.ExclusionReason(PipeTagElementKind.Other, 2.0, false, 1.0, duct)!.Contains("tubería"), "duct messages never mention pipes");
        Check(PipeTagRules.ExclusionReason(PipeTagElementKind.FlexPipe, 1.0, false, 1.0) == "Es una tubería flexible: solo se etiquetan tramos rectos.", "pipe wording unchanged by default");
        Check(PipeTagRules.ExclusionReason(PipeTagElementKind.Pipe, 1.0, true, 1.0) == "Ya tiene una etiqueta de tubería en esta vista; no se duplica.", "pipe 'already tagged' wording unchanged");

        // --- Cable trays: same rule, tray wording.
        var tray = MepTagNouns.CableTray;
        Check(PipeTagRules.ExclusionReason(PipeTagElementKind.Pipe, 3.0, false, 1.0, tray) is null, "3 m cable tray visible in length: tagged");
        Check(PipeTagRules.ExclusionReason(PipeTagElementKind.Pipe, 0.15, false, 1.0, tray) is not null, "cable tray of exactly 15 cm is not tagged");
        Check(PipeTagRules.ExclusionReason(PipeTagElementKind.PipeFitting, 3.0, false, 1.0, tray)!.Contains("codo"), "cable tray fitting (bend, tee, cross) excluded");
        Check(PipeTagRules.ExclusionReason(PipeTagElementKind.Other, 3.0, false, 1.0, tray) == "No es una bandeja de cables: solo se etiquetan tramos rectos de bandeja de cables.", "non-tray excluded with tray wording");
        Check(PipeTagRules.ExclusionReason(PipeTagElementKind.Pipe, 3.0, true, 1.0, tray) == "Ya tiene una etiqueta de bandeja de cables en esta vista; no se duplica.", "tray already tagged: not duplicated");
        Check(PipeTagRules.ExclusionReason(PipeTagElementKind.Pipe, 3.0, false, 0.0, tray)!.Contains("de punta"), "vertical tray seen end-on in plan: excluded");
        Check(new[] { MepTagNouns.Pipe, MepTagNouns.Duct, MepTagNouns.CableTray }.Select(x => x.DefinitePlural).SequenceEqual(new[] { "las tuberías", "los conductos", "las bandejas de cables" }), "articles agree in gender for each discipline");

        // --- Orientation.
        Check(PipeTagRules.Orthogonality(new TagPoint(1, 0)) == 0, "horizontal pipe -> horizontal tag");
        Check(PipeTagRules.Orthogonality(new TagPoint(-3, 0.01)) == 0, "almost horizontal (0.2 deg), reversed -> horizontal");
        Check(PipeTagRules.Orthogonality(new TagPoint(0, -2)) == 1, "vertical pipe -> vertical tag");
        Check(PipeTagRules.Orthogonality(new TagPoint(1, 1)) == -1, "45 deg pipe -> model direction");
        Check(PipeTagRules.Orthogonality(new TagPoint(1, 0.05)) == -1, "2.9 deg pipe -> model direction");
        var n1 = PipeTagLayout.UpperNormal(new TagPoint(0, 0), new TagPoint(5, 0));
        var n2 = PipeTagLayout.UpperNormal(new TagPoint(5, 0), new TagPoint(0, 0));
        Check(n1 == new TagPoint(0, 1) && n2 == new TagPoint(0, 1), "upper side of a horizontal pipe is +Y in both directions");
        var nv = PipeTagLayout.UpperNormal(new TagPoint(0, 0), new TagPoint(0, 5));
        Check(Math.Abs(nv.X + 1) < 1e-12 && Math.Abs(nv.Y) < 1e-12, "upper side of a vertical pipe is -X (left)");

        // --- Geometry primitives.
        var b = new TagBox(0, 0, 1, 1);
        Check(b.Overlaps(new TagBox(0.5, 0.5, 2, 2), 0), "overlapping boxes");
        Check(!b.Overlaps(new TagBox(1.2, 0, 2, 1), 0.1), "boxes 0.2 apart are clear with 0.1 gap");
        Check(b.Overlaps(new TagBox(1.05, 0, 2, 1), 0.1), "boxes 0.05 apart collide with 0.1 gap");
        Check(b.IntersectsSegment(new TagPoint(-1, 0.5), new TagPoint(2, 0.5), 0), "segment crossing the box");
        Check(!b.IntersectsSegment(new TagPoint(-1, 2), new TagPoint(2, 2), 0), "segment passing above the box");
        Check(!b.IntersectsSegment(new TagPoint(2, 0), new TagPoint(3, 1), 0), "segment beside the box");

        double gap = PipeTagRules.PaperGapFeet(50);

        // --- Rule: every run tag has its head 20 cm above the axis.
        double up = PipeTagRules.AboveAxisFeet;
        Check(Math.Abs(up * 0.3048 - 0.20) < 1e-12, "20 cm above the axis = 0.656 ft in model units");

        // --- Free drawing: 20 cm above the middle of the pipe, keeping the family's own offset.
        var r = Request("A", 0, 0, 10, 0);
        var p = PipeTagLayout.Place(r, new List<TagBox>(), new List<PipeTagPlacement>(), gap, null)!;
        Check(p.Kind == PipeTagPlacementKind.OnPipe && Math.Abs(p.Head.X - 5) < 1e-12 && Math.Abs(p.Head.Y - up) < 1e-12 && p.LeaderEnd is null,
            "free space: head 20 cm above the axis, centred, no leader");
        Check(Math.Abs(p.Box.Center.Y - (up + 0.2)) < 1e-12, "the family offset between head and label is kept");

        // Vertical run: 20 cm to its upper (left) side; diagonal run: 20 cm along its upper normal.
        var pv = PipeTagLayout.Place(Request("V", 0, 0, 0, 10, 0.25, 1.0, 0), new List<TagBox>(), new List<PipeTagPlacement>(), gap, null)!;
        Check(Math.Abs(pv.Head.X + up) < 1e-12 && Math.Abs(pv.Head.Y - 5) < 1e-12, "vertical run: head 20 cm to the upper (left) side");
        var pd = PipeTagLayout.Place(Request("D", 0, 0, 7, 7), new List<TagBox>(), new List<PipeTagPlacement>(), gap, null)!;
        Check(PipeTagLayout.IsAtAboveAxis(pd.Head, new TagPoint(0, 0), new TagPoint(7, 7)) && pd.Head.Y > 3.5, "diagonal run: head 20 cm above the axis");
        Check(PipeTagLayout.IsAtAboveAxis(new TagPoint(5, up + 0.0004), new TagPoint(0, 0), new TagPoint(10, 0))
              && !PipeTagLayout.IsAtAboveAxis(new TagPoint(5, up + 0.03), new TagPoint(0, 0), new TagPoint(10, 0))
              && !PipeTagLayout.IsAtAboveAxis(new TagPoint(5, -up), new TagPoint(0, 0), new TagPoint(10, 0)),
            "Revit check: ±5 mm accepted, 1 cm off or below the axis flagged");

        // --- Middle occupied: slides along the pipe, still 20 cm above it.
        var middleText = new List<TagBox> { new(4.2, 0.6, 5.8, 1.2) };
        p = PipeTagLayout.Place(r, middleText, new List<PipeTagPlacement>(), gap, null)!;
        Check(p.Kind == PipeTagPlacementKind.AlongPipe && !p.Box.Overlaps(middleText[0], gap) && Math.Abs(p.Head.Y - up) < 1e-12,
            "middle occupied: slides along the pipe, 20 cm above, without overlapping");

        // --- Row at 20 cm occupied with a gap in the middle: further up with a leader through the gap.
        var row = new List<TagBox> { new(-1, 0.6, 4.7, 0.9), new(5.3, 0.6, 11, 0.9) };
        p = PipeTagLayout.Place(r, row, new List<PipeTagPlacement>(), gap, null);
        Check(p is not null && p.Kind == PipeTagPlacementKind.OffsetWithLeader && p.LeaderEnd is not null, "row at 20 cm full: further up with a leader");
        Check(p is not null && p.Head.Y > up && p.Box.MinY > 0.9, "the leader tag stays above the axis");
        Check(p is not null && Math.Abs(p.AxisOffset * 0.3048 - 0.35) < 1e-9, "just enough: 35 cm, the first 5 cm step that clears the text");
        var leader = PipeTagLayout.Leader(p!)!.Value;
        Check(Math.Abs(p!.LeaderEnd!.Value.Y) < 1e-12, "leader ends on the pipe");
        Check(row.All(o => !o.IntersectsSegment(leader.From, leader.To, 0) && !o.Overlaps(p.Box, gap)), "leader and tag clear every text");

        // Whole band above the pipe occupied: goes below the axis, 20 cm, no leader.
        var band = new List<TagBox> { new(-1, 0.03, 11, 1.2) };
        p = PipeTagLayout.Place(r, band, new List<PipeTagPlacement>(), gap, null);
        Check(p is not null && p.LeaderEnd is null && Math.Abs(p.Head.Y + up) < 1e-12 && Math.Abs(p.AxisOffset + up) < 1e-12 && !p.Box.Overlaps(band[0], gap),
            "band above occupied: 20 cm below the axis");

        // Text covering the pipe line and the space above: below the axis too.
        var onLine = new List<TagBox> { new(-1, -0.02, 11, 0.6) };
        p = PipeTagLayout.Place(r, onLine, new List<PipeTagPlacement>(), gap, null);
        Check(p is not null && p.AxisOffset < 0 && p.LeaderEnd is null, "text over the pipe line: tag below the axis, no leader crossing the text");

        // Text all around up to 1 m above and below: beyond the 100 cm limit, so it is reported.
        var around = new List<TagBox> { new(-1, -3.6, 11, 3.6) };
        Check(PipeTagLayout.Place(r, around, new List<PipeTagPlacement>(), gap, null) is null, "nothing free within 100 cm above or below: reported");
        Check(PipeTagRules.AxisOffsetsFeet().Select(f => Math.Round(f * 30.48)).SequenceEqual(Enumerable.Range(0, 17).Select(i => 20.0 + 5 * i)),
            "distances tried: 20, 25 … 100 cm");
        Check(PipeTagRules.AxisOffsetText(-up) == "20 cm bajo el eje" && PipeTagRules.AxisOffsetText(up * 2) == "40 cm sobre el eje", "report text of the distance");

        // --- Modelled runs: a tag never covers a pipe (its own or another one).
        double thin = 0.05;
        var lone = new List<RunSegment> { new(new TagPoint(0, 0), new TagPoint(10, 0), thin) };
        p = PipeTagLayout.Place(r, new List<TagBox>(), new List<PipeTagPlacement>(), gap, null, lone);
        Check(p is not null && p.Kind == PipeTagPlacementKind.OnPipe && Math.Abs(p.AxisOffset - up) < 1e-12, "lone pipe: 20 cm above, centred");
        var withNeighbour = new List<RunSegment>(lone) { new(new TagPoint(0, 0.9), new TagPoint(10, 0.9), thin) };
        p = PipeTagLayout.Place(r, new List<TagBox>(), new List<PipeTagPlacement>(), gap, null, withNeighbour);
        Check(p is not null && Math.Abs(p.AxisOffset * 0.3048 - 0.35) < 1e-9 && p.LeaderEnd is not null && !PipeTagLayout.CoversRun(p.Box, withNeighbour, gap),
            "pipe 27 cm above: tag goes up just enough (35 cm) with a leader, without covering it");
        var wide = new List<RunSegment> { new(new TagPoint(0, 0), new TagPoint(10, 0), 1.0) };
        p = PipeTagLayout.Place(r, new List<TagBox>(), new List<PipeTagPlacement>(), gap, null, wide);
        Check(p is not null && p.AxisOffset > up && !PipeTagLayout.CoversRun(p.Box, wide, gap), "wide duct: the tag clears its own drawn width");

        // Bundle of 8 parallel pipes 13.7 cm apart: no tag covers any pipe, bottom one goes below, top one stays at +20 cm.
        var bundleRuns = new List<RunSegment>();
        var bundle = new List<PipeTagRequest>();
        for (int i = 0; i < 8; i++)
        {
            double y = i * 0.45;
            bundleRuns.Add(new RunSegment(new TagPoint(0, y), new TagPoint(10, y), thin));
            bundle.Add(Request("P" + i, 0, y, 10, y));
        }
        var (bPlaced, bUnplaced) = PipeTagLayout.PlaceAll(bundle, new List<TagBox>(), gap, null, bundleRuns);
        Check(bPlaced.Count + bUnplaced.Count == 8, $"bundle: every pipe placed or reported ({bPlaced.Count} placed, {bUnplaced.Count} reported)");
        Check(bPlaced.All(x => !PipeTagLayout.CoversRun(x.Box, bundleRuns, gap)), "bundle: no tag covers any modelled pipe");
        Check(NoOverlap(bPlaced, new List<TagBox>(), gap), "bundle: no tag overlaps another");
        Check(bPlaced.All(x => Math.Abs(x.AxisOffset) >= up - 1e-9 && Math.Abs(x.AxisOffset) <= 1.0 / 0.3048 + 1e-9), "bundle: every tag between 20 and 100 cm from its axis");
        Check(bPlaced.All(a => PipeTagLayout.Leader(a) is not { } bl || bPlaced.All(o => ReferenceEquals(o, a) || !o.Box.IntersectsSegment(bl.From, bl.To, 0))), "bundle: leaders cross no other tag");
        Check(bPlaced.Single(x => x.Key == "P7") is { LeaderEnd: null } top && Math.Abs(top.AxisOffset - up) < 1e-12, "bundle: top pipe 20 cm above");
        Check(bPlaced.Single(x => x.Key == "P0") is { LeaderEnd: null } bottom && Math.Abs(bottom.AxisOffset + up) < 1e-12, "bundle: bottom pipe 20 cm below (pipes cover all the space above)");
        Check(bPlaced.Where(x => x.LeaderEnd is null).All(x =>
            {
                var q = bundle.Single(b => b.Key == x.Key);
                var anchor = q.Start.Lerp(q.End, x.PipeParameter);
                return bundleRuns.All(run => PipeTagLayout.DistanceToSegment(anchor, run.A, run.B) <= run.HalfWidth + 1e-6
                                             || PipeTagLayout.DistanceBetweenSegments(anchor, x.Box.Center, run.A, run.B) > run.HalfWidth);
            }), "bundle: a tag without leader never has another pipe between it and its own pipe");
        Check(bPlaced.Any(x => x.LeaderEnd is not null), "bundle: tags pushed past other pipes carry a leader to their own pipe");
        Check(bPlaced.All(x => PipeTagLayout.IsAtAxisOffset(x.Head, bundle.Single(q => q.Key == x.Key).Start, bundle.Single(q => q.Key == x.Key).End, x.AxisOffset)),
            "bundle: recorded distance matches the head position");

        // --- No room anywhere: not created, reported.
        var everywhere = new List<TagBox> { new(-100, -100, 100, 100) };
        Check(PipeTagLayout.Place(r, everywhere, new List<PipeTagPlacement>(), gap, null) is null, "no free position: tag is not created");

        // --- Crop region: tags must stay inside it.
        var crop = new TagBox(-1, -1, 11, 1.2);
        p = PipeTagLayout.Place(r, new List<TagBox>(), new List<PipeTagPlacement>(), gap, crop);
        Check(p is not null && crop.Contains(p.Box) && Math.Abs(p.Head.Y - up) < 1e-12, "tag 20 cm above stays inside the crop region");
        Check(PipeTagLayout.Place(r, new List<TagBox>(), new List<PipeTagPlacement>(), gap, new TagBox(-1, -0.1, 11, 0.5)) is null, "crop too tight for 20 cm: reported, not placed outside");

        // --- Two close parallel pipes: both tagged 20 cm above their own axis, no overlap.
        var (placed, unplaced) = PipeTagLayout.PlaceAll(new[] { Request("low", 0, 0, 10, 0), Request("high", 0, 0.3, 10, 0.3) }, new List<TagBox>(), gap, null);
        Check(placed.Count == 2 && unplaced.Count == 0 && NoOverlap(placed, new List<TagBox>(), gap), "close parallel pipes: both tagged, no overlap");
        Check(placed.Single(x => x.Key == "low").Head.Y is var lowY && Math.Abs(lowY - up) < 1e-12
              && Math.Abs(placed.Single(x => x.Key == "high").Head.Y - (0.3 + up)) < 1e-12, "close parallel pipes: each tag 20 cm above its own axis");

        // --- Stress: dense grid of horizontal, vertical and diagonal pipes with existing annotations.
        var rnd = new Random(12345);
        var requests = new List<PipeTagRequest>();
        for (int i = 0; i < 400; i++)
        {
            double x = rnd.NextDouble() * 60, y = rnd.NextDouble() * 60, len = 0.6 + rnd.NextDouble() * 8;
            int dir = rnd.Next(3);
            var (dx, dy) = dir == 0 ? (len, 0.0) : dir == 1 ? (0.0, len) : (len * 0.7, len * 0.7);
            double w = 0.8 + rnd.NextDouble() * 0.8, h = 0.2 + rnd.NextDouble() * 0.1;
            requests.Add(dir == 1
                ? Request("P" + i, x, y, x + dx, y + dy, h, w, 0)   // vertical tag: box rotated
                : Request("P" + i, x, y, x + dx, y + dy, w, h, 0.15));
        }
        var existing = Enumerable.Range(0, 60).Select(_ => { double x = rnd.NextDouble() * 60, y = rnd.NextDouble() * 60; return new TagBox(x, y, x + 1.2, y + 0.3); }).ToList();
        (placed, unplaced) = PipeTagLayout.PlaceAll(requests, existing, gap, null);
        Check(placed.Count + unplaced.Count == requests.Count && placed.Select(x => x.Key).Distinct().Count() == placed.Count, "stress: every pipe placed once or reported");
        Check(NoOverlap(placed, existing, gap), $"stress: no tag overlaps another tag or annotation ({placed.Count} placed, {unplaced.Count} reported)");
        bool leadersClear = placed.All(a =>
        {
            if (PipeTagLayout.Leader(a) is not { } l) return true;
            return existing.All(o => !o.IntersectsSegment(l.From, l.To, 0)) && placed.All(o => ReferenceEquals(o, a) || !o.Box.IntersectsSegment(l.From, l.To, 0));
        });
        Check(leadersClear, "stress: no leader crosses a tag or annotation");
        var byKey = requests.ToDictionary(x => x.Key);
        Check(placed.Where(x => x.LeaderEnd is null).All(x => Math.Abs(Math.Abs(x.AxisOffset) - up) < 1e-12 && PipeTagLayout.IsAtAxisOffset(x.Head, byKey[x.Key].Start, byKey[x.Key].End, x.AxisOffset)),
            "stress: every tag without leader has its head exactly 20 cm above or below its axis (horizontal, vertical and diagonal)");
        Check(placed.Where(x => x.LeaderEnd is not null).All(x => Math.Abs(PipeTagLayout.DistanceAboveAxis(x.Head, byKey[x.Key].Start, byKey[x.Key].End)) is var d && d > up && d <= 1.0 / 0.3048 + 1e-9),
            "stress: tags with leader are between 20 and 100 cm from the axis");
        Check(placed.Count(x => x.LeaderEnd is null && x.AxisOffset > 0) >= placed.Count * 0.8, $"stress: most tags sit 20 cm above without leader ({placed.Count(x => x.LeaderEnd is null && x.AxisOffset > 0)} of {placed.Count})");
        Check(PipeTagLayout.Conflicts(placed, existing, gap).Count == 0, "stress: final verification finds no conflict in a clean layout");

        // Same dense drawing, now with the 400 pipes as modelled runs that no tag may cover.
        var stressRuns = requests.Select(q => new RunSegment(q.Start, q.End, 0.05)).ToList();
        var sw = System.Diagnostics.Stopwatch.StartNew();
        var (rPlaced, rUnplaced) = PipeTagLayout.PlaceAll(requests, existing, gap, null, stressRuns);
        sw.Stop();
        Check(rPlaced.Count + rUnplaced.Count == requests.Count, $"stress with runs: every pipe placed or reported ({rPlaced.Count} placed, {rUnplaced.Count} reported, {sw.ElapsedMilliseconds} ms)");
        Check(rPlaced.All(x => !PipeTagLayout.CoversRun(x.Box, stressRuns, gap)) && NoOverlap(rPlaced, existing, gap), "stress with runs: no tag covers a pipe, a tag or a text");
        Check(PipeTagLayout.Conflicts(rPlaced, existing, gap, stressRuns).Count == 0, "stress with runs: final verification agrees");
        Check(rPlaced.All(x => Math.Abs(x.AxisOffset) >= up - 1e-9 && Math.Abs(x.AxisOffset) <= 1.0 / 0.3048 + 1e-9), "stress with runs: every tag 20-100 cm from its axis");

        // --- Final verification: if Revit moved a tag onto another, the later one is flagged, the first kept.
        var a1 = new PipeTagPlacement { Key = "first", Box = new TagBox(0, 0, 1, 0.3) };
        var a2 = new PipeTagPlacement { Key = "second", Box = new TagBox(0.5, 0.1, 1.5, 0.4) };
        var a3 = new PipeTagPlacement { Key = "third", Box = new TagBox(5, 5, 6, 5.3) };
        var conflicts = PipeTagLayout.Conflicts(new[] { a1, a2, a3 }, new List<TagBox>(), gap);
        Check(conflicts.SequenceEqual(new[] { "second" }), "verification removes only the later overlapping tag");
        Check(PipeTagLayout.Conflicts(new[] { a3 }, new List<TagBox> { new(5.5, 5.1, 7, 6) }, gap).SequenceEqual(new[] { "third" }), "verification catches overlap with an existing annotation");

        SpatialChecks(gap);
        FamilyChecks(gap);

        Console.WriteLine($"PIPE_TAG_TOTAL={_pass + _fail} PASS={_pass} FAIL={_fail}; pure logic, not Revit API.");
        return _fail == 0 ? 0 : 1;
    }
}
