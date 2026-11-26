#nullable enable
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;

namespace RevitAutomation.Addin.Commands.Aislamiento;

public static class AislReport
{
    private static readonly CultureInfo Spanish = CultureInfo.GetCultureInfo("es-ES");
    private static string Root => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "RevitAutomation", "Aislamiento");
    public static string OutputFolder => Path.Combine(Root, "output");

    public static void EnsureFolders() => Directory.CreateDirectory(OutputFolder);

    private static string Number(double value) => value.ToString("0.################", Spanish);
    private static string Escape(string value) => value.IndexOfAny(new[] { ';', '"', '\r', '\n' }) >= 0
        ? "\"" + value.Replace("\"", "\"\"") + "\"" : value;
    private static void Row(StringBuilder text, params string[] values) =>
        text.AppendLine(string.Join(";", values.Select(Escape)));

    public static string RedText(AislRed red) => red switch
    {
        AislRed.SinClasificar => "Sin clasificar",
        AislRed.Acs => "ACS",
        AislRed.AguaFria => "Agua fría",
        AislRed.ClimaCaliente => "Clima calor",
        AislRed.ClimaFrio => "Clima frío",
        AislRed.ClimaMixto => "Clima mixto",
        AislRed.Frigorifica => "Frigorífica",
        AislRed.Excluida => "Excluida",
        _ => red.ToString()
    };

    public static string ActionText(AislAction action) => action switch
    {
        AislAction.Crear => "Crear",
        AislAction.Aumentar => "Aumentar",
        AislAction.MantenerSuficiente => "Ya cumple",
        AislAction.NoExigido => "No exigido",
        AislAction.SinClasificar => "Sin clasificar",
        AislAction.Excluida => "Excluida",
        _ => action.ToString()
    };

    public static string KindText(AislElementKind kind) => kind switch
    {
        AislElementKind.Tuberia => "Tubería",
        AislElementKind.Pieza => "Pieza",
        AislElementKind.Accesorio => "Accesorio",
        _ => kind.ToString()
    };

    // Las redes sin temperatura de tabla (frigorífica, agua fría) dejan la columna vacía.
    private static string Temperature(AislRequirement? r) =>
        r is null || !r.Required || r.Red is AislRed.Frigorifica or AislRed.AguaFria ? "" : Number(r.TemperatureC);

    public static string BuildReportCsv(IReadOnlyList<AislDecision> decisions)
    {
        var text = new StringBuilder();
        text.AppendLine("Elemento (Id);Tipo;Sistema;Red;Planta;Diámetro exterior (mm);Temperatura (°C);Espesor existente (mm);Espesor RITE (mm);Acción;Justificación");
        foreach (var d in decisions)
        {
            var r = d.Requirement;
            Row(text,
                d.Item.ElementId.ToString(CultureInfo.InvariantCulture),
                KindText(d.Item.Kind),
                d.Item.SystemName ?? "",
                RedText(d.Classification.Red),
                d.Item.LevelName ?? "",
                Number(Math.Round(d.Item.OuterMm, 2)),
                Temperature(r),
                d.Item.ExistingThicknessMm is double e ? Number(Math.Round(e, 2)) : "",
                r is { Required: true } ? Number(r.ThicknessMm) : "",
                ActionText(d.Action),
                r?.Rule ?? d.Classification.Reason);
        }
        return text.ToString();
    }

    public static string BuildDeviationsCsv(IReadOnlyList<AislDeviation> d)
    {
        var text = new StringBuilder();
        text.AppendLine("Elemento;Qué;Motivo;Cómo corregirlo");
        foreach (var item in d) Row(text, item.Element, item.What, item.Reason, item.HowToFix);
        return text.ToString();
    }

    public static (string ReportPath, string DeviationsPath) Write(IReadOnlyList<AislDecision> decisions,
        IReadOnlyList<AislDeviation> deviations, string folder, DateTime when)
    {
        Directory.CreateDirectory(folder);
        string stamp = when.ToString("yyyyMMdd_HHmmss", CultureInfo.InvariantCulture);
        string report = Path.Combine(folder, $"informe_aislamiento_{stamp}.csv");
        string devPath = Path.Combine(folder, $"desviaciones_aislamiento_{stamp}.csv");
        var encoding = new UTF8Encoding(true);
        File.WriteAllText(report, BuildReportCsv(decisions), encoding);
        File.WriteAllText(devPath, BuildDeviationsCsv(deviations), encoding);
        return (report, devPath);
    }
}
