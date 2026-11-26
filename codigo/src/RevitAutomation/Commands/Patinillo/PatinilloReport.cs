#nullable enable
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;

namespace RevitAutomation.Addin.Commands.Patinillo;

public static class PatinilloReport
{
    private static readonly CultureInfo Spanish = CultureInfo.GetCultureInfo("es-ES");
    private static string Root => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "RevitAutomation", "Patinillo");
    public static string OutputFolder => Path.Combine(Root, "output");
    public static string InputFolder => Path.Combine(Root, "input");

    public static void EnsureFolders()
    {
        Directory.CreateDirectory(OutputFolder);
        Directory.CreateDirectory(InputFolder);
    }

    private static string Number(double value) => value.ToString("0.################", Spanish);
    private static string Escape(string value) => value.IndexOfAny(new[] { ';', '"', '\r', '\n' }) >= 0
        ? "\"" + value.Replace("\"", "\"\"") + "\"" : value;
    private static void Row(StringBuilder text, params string[] values) =>
        text.AppendLine(string.Join(";", values.Select(Escape)));

    private static string Service(PatinilloService service) => service switch
    {
        PatinilloService.AguaFria => "Agua fría",
        PatinilloService.Acs => "Agua caliente (ACS)",
        PatinilloService.RetornoAcs => "Retorno ACS",
        _ => service.ToString()
    };

    public static string BuildReportCsv(PatinilloPlan plan)
    {
        var text = new StringBuilder();
        // Fila 1 = la de la boca; columna 1 = la de menor X (como en la vista previa).
        text.AppendLine("Tubería;Sistema;Diámetro nominal (mm);Diámetro exterior (mm);Planta de salida;Vivienda;Fila;Columna;X (mm);Y (mm);Cota de salida (mm);Escalón");
        foreach (var p in plan.Placements)
            Row(text, p.Pipe.Id, Service(p.Pipe.Service), Number(p.Pipe.NominalMm), Number(p.Pipe.OuterMm),
                p.Pipe.FloorName, p.Pipe.Dwelling.ToString(Spanish), (p.Row + 1).ToString(Spanish), (p.Column + 1).ToString(Spanish),
                Number(p.XMm), Number(p.YMm), Number(p.ExitElevationMm), p.Tier.ToString(Spanish));
        return text.ToString();
    }

    public static string BuildDeviationsCsv(IReadOnlyList<PatinilloDeviation> d)
    {
        var text = new StringBuilder();
        text.AppendLine("Tubería;Qué;Motivo;Cómo corregirlo");
        foreach (var item in d) Row(text, item.PipeId, item.What, item.Reason, item.HowToFix);
        return text.ToString();
    }

    public static (string ReportPath, string DeviationsPath) Write(PatinilloPlan plan,
        IReadOnlyList<PatinilloDeviation> d, string folder, DateTime when)
    {
        Directory.CreateDirectory(folder);
        string stamp = when.ToString("yyyyMMdd_HHmmss", CultureInfo.InvariantCulture);
        string report = Path.Combine(folder, $"informe_patinillo_{stamp}.csv");
        string deviations = Path.Combine(folder, $"desviaciones_patinillo_{stamp}.csv");
        var encoding = new UTF8Encoding(true);
        File.WriteAllText(report, BuildReportCsv(plan), encoding);
        File.WriteAllText(deviations, BuildDeviationsCsv(d), encoding);
        return (report, deviations);
    }

    public static string Ascii(PatinilloPlan plan)
    {
        int cellWidth = Math.Max(1, plan.Placements.Select(p => p.Pipe.Id.Length).DefaultIfEmpty(1).Max());
        var cells = plan.Placements.ToDictionary(p => (p.Row, p.Column), p => p.Pipe.Id);
        var text = new StringBuilder();
        for (int row = plan.Rows - 1; row >= 0; row--)
        {
            text.Append('|');
            for (int column = 0; column < plan.Columns; column++)
            {
                string id = cells.TryGetValue((row, column), out var value) ? value : "·";
                text.Append(' ').Append(id.PadRight(cellWidth)).Append(" |");
            }
            text.AppendLine();
        }
        text.AppendLine("BOCA ↓");
        return text.ToString();
    }
}
