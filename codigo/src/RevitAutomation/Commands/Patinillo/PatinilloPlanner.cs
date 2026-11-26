#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;

namespace RevitAutomation.Addin.Commands.Patinillo;

public static class PatinilloPlanner
{
    public static IReadOnlyList<string> Validate(PatinilloSettings s)
    {
        var errors = new List<string>();
        void Positive(double v, string name) { if (!double.IsFinite(v) || v <= 0) errors.Add($"{name} debe ser mayor que 0 (valor: {v})."); }
        void Nonnegative(double v, string name) { if (!double.IsFinite(v) || v < 0) errors.Add($"{name} no puede ser negativo (valor: {v})."); }
        if (s.DwellingsPerFloor < 1) errors.Add($"Debe haber al menos 1 vivienda por planta (valor: {s.DwellingsPerFloor}).");
        Positive(s.ColdDiameterMm, "El diámetro de agua fría");
        Positive(s.HotDiameterMm, "El diámetro de ACS");
        Positive(s.ReturnDiameterMm, "El diámetro de retorno");
        Positive(s.ShaftWidthMm, "El ancho del patinillo");
        Positive(s.ShaftDepthMm, "El fondo del patinillo");
        Nonnegative(s.GapMm, "La separación");
        Positive(s.OutletLengthMm, "La longitud de salida");
        Nonnegative(s.SlabThicknessMm, "El espesor del forjado");
        Nonnegative(s.OutletBelowSlabMm, "La distancia bajo el forjado");
        Nonnegative(s.StartAboveFloorMm, "La altura de arranque");
        if (s.TestFloorsAboveGround < 1 || s.TestFloorsAboveGround > 60)
            errors.Add($"El edificio de prueba debe tener entre 1 y 60 plantas (valor: {s.TestFloorsAboveGround}).");
        if (!double.IsFinite(s.TestFloorHeightMm) || s.TestFloorHeightMm <= s.SlabThicknessMm)
            errors.Add($"La altura de planta ({s.TestFloorHeightMm} mm) debe superar el espesor del forjado ({s.SlabThicknessMm} mm).");
        return errors;
    }

    public static IReadOnlyList<PatinilloPipe> BuildPipes(PatinilloSettings s, IReadOnlyList<PatinilloLevel> levels, Func<double, double> outerMm)
    {
        var sorted = levels.OrderBy(l => l.ElevationMm).ToArray();
        var pipes = new List<PatinilloPipe>();
        for (int i = s.GroundFloorHasDwellings ? 0 : 1; i < sorted.Length - 1; i++)
        {
            string prefix = new string(sorted[i].Name.Where(char.IsLetterOrDigit).ToArray());
            for (int d = 1; d <= s.DwellingsPerFloor; d++)
            {
                void Add(PatinilloService service, double nominal, string code) =>
                    pipes.Add(new PatinilloPipe($"{prefix}-V{d}-{code}", i, sorted[i].Name, d, service, nominal, outerMm(nominal)));
                Add(PatinilloService.AguaFria, s.ColdDiameterMm, "AF");
                Add(PatinilloService.Acs, s.HotDiameterMm, "ACS");
                if (s.HotReturn) Add(PatinilloService.RetornoAcs, s.ReturnDiameterMm, "RET");
            }
        }
        return pipes;
    }

    private static int Slots(double length, double pitch) =>
        double.IsFinite(length) && double.IsFinite(pitch) && length > 0 && pitch > 0
            ? (int)Math.Min(int.MaxValue, Math.Floor((length + 1e-6) / pitch)) : 0;

    public static PatinilloPlan Plan(PatinilloSettings s, double widthMm, double depthMm, IReadOnlyList<PatinilloLevel> levels, Func<double, double> outerMm)
    {
        var sorted = levels.OrderBy(l => l.ElevationMm).ToArray();
        var pipes = BuildPipes(s, sorted, outerMm);
        double pitch = pipes.Count == 0 ? 0 : pipes.Max(p => p.OuterMm) + s.GapMm;
        int columns = Slots(widthMm, pitch), rows = Slots(depthMm, pitch);
        double minWidth = 0, minDepth = 0;
        if (double.IsFinite(widthMm) && widthMm > 0 && double.IsFinite(pitch) && pitch > 0)
        {
            double rawWidth = Math.Max(widthMm, pitch);
            int minColumns = Math.Max(1, Slots(rawWidth, pitch));
            minWidth = Math.Ceiling(rawWidth / 10) * 10;
            minDepth = Math.Ceiling(Math.Ceiling((double)pipes.Count / minColumns) * pitch / 10) * 10;
        }
        var notes = new List<string>();
        if (!s.GroundFloorHasDwellings) notes.Add("La planta baja no tiene viviendas (contadores).");
        if (sorted.Length > 0) notes.Add($"El nivel superior \"{sorted[^1].Name}\" se toma como cubierta.");
        PatinilloPlan Result(bool fits, string message, IReadOnlyList<PatinilloPlacement>? placements = null) => new()
        {
            Fits = fits, Message = message, WidthMm = widthMm, DepthMm = depthMm,
            PitchMm = pitch, Rows = rows, Columns = columns, MinWidthMm = minWidth, MinDepthMm = minDepth,
            Levels = sorted, Pipes = pipes, Notes = notes, Placements = placements ?? Array.Empty<PatinilloPlacement>()
        };
        var errors = Validate(s).ToList();
        if (!double.IsFinite(widthMm) || widthMm <= 0) errors.Add($"El ancho real debe ser mayor que 0 mm (valor: {widthMm}).");
        if (!double.IsFinite(depthMm) || depthMm <= 0) errors.Add($"El fondo real debe ser mayor que 0 mm (valor: {depthMm}).");
        if (errors.Count > 0) return Result(false, string.Join(Environment.NewLine, errors));
        if (sorted.Length < 2) return Result(false, $"Se necesitan al menos 2 niveles; se recibieron {sorted.Length}.");
        if (pipes.Count == 0) return Result(false, $"No hay plantas con viviendas entre los {sorted.Length} niveles y la cubierta.");
        if (sorted.Any(l => !double.IsFinite(l.ElevationMm)))
            return Result(false, "Las cotas de los niveles deben ser números finitos.");
        if (pipes.Any(p => !double.IsFinite(p.OuterMm) || p.OuterMm <= 0) || !double.IsFinite(pitch))
            return Result(false, "Los diámetros exteriores y el paso deben ser números finitos mayores que 0 mm.");
        if (columns == 0 || rows == 0 || (long)columns * rows < pipes.Count)
            return Result(false, $"No caben {pipes.Count} tuberías en {widthMm:0.###} × {depthMm:0.###} mm: {columns} columnas × {rows} filas, paso {pitch:0.###} mm. Mínimo: {minWidth:0.###} × {minDepth:0.###} mm.");

        double start = sorted[0].ElevationMm + s.StartAboveFloorMm;
        var placements = new List<PatinilloPlacement>();
        var tiers = new Dictionary<(int Floor, int Column), int>();
        for (int k = 0; k < pipes.Count; k++)
        {
            var pipe = pipes[k];
            int row = k / columns, column = k % columns;
            var key = (pipe.FloorIndex, column);
            tiers.TryGetValue(key, out int tier);
            tiers[key] = tier + 1;
            double ceiling = sorted[pipe.FloorIndex + 1].ElevationMm - s.SlabThicknessMm;
            double baseExit = ceiling - s.OutletBelowSlabMm;
            if (baseExit <= start)
                return Result(false, $"Planta \"{pipe.FloorName}\": salida base {baseExit:0.###} mm ≤ arranque {start:0.###} mm.");
            double exit = baseExit + tier * pitch;
            if (exit + pipe.OuterMm / 2 > ceiling)
                return Result(false, $"Tubería {pipe.Id}: escalón {tier}, cota superior {exit + pipe.OuterMm / 2:0.###} mm > cara inferior del forjado {ceiling:0.###} mm (paso {pitch:0.###} mm).");
            placements.Add(new PatinilloPlacement(pipe, row, column,
                (widthMm - (columns - 1) * pitch) / 2 + column * pitch,
                (depthMm - (rows - 1) * pitch) / 2 + row * pitch,
                tier, start, exit, -s.OutletLengthMm));
        }
        return Result(true, $"Caben {pipes.Count} tuberías en {columns} columnas × {rows} filas (paso {pitch:0.###} mm).", placements);
    }
}
