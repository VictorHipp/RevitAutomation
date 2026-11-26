#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace RevitAutomation.Addin.Commands.Aislamiento;

public static class AislSettingsStore
{
    public static string DefaultPath => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "RevitAutomation", "aislamiento.json");

    private static JsonSerializerOptions Options() => new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() }
    };

    public static AislSettings Load(string? path = null)
    {
        try
        {
            var s = JsonSerializer.Deserialize<AislSettings>(File.ReadAllText(path ?? DefaultPath), Options()) ?? new AislSettings();
            // System.Text.Json crea el diccionario con el comparador por defecto: se restaura el insensible a mayúsculas.
            s.SystemOverrides = new Dictionary<string, AislRed>(s.SystemOverrides ?? new Dictionary<string, AislRed>(),
                StringComparer.OrdinalIgnoreCase);
            s.CommercialThicknessesMm ??= new AislSettings().CommercialThicknessesMm;
            s.LevelName ??= "";
            var networks = s.Networks;
            s.Networks = AislSettings.DefaultNetworks();
            if (networks is not null)
                foreach (var kv in networks) if (kv.Value is not null) { kv.Value.InsulationType ??= ""; s.Networks[kv.Key] = kv.Value; }
            s.HotInsulationType ??= "";
            s.ColdInsulationType ??= "";
            s.RefrigerantInsulationType ??= "";
            return s;
        }
        catch { return new AislSettings(); }
    }

    public static bool Save(AislSettings s, string? path = null)
    {
        try
        {
            string fullPath = Path.GetFullPath(path ?? DefaultPath);
            string json = JsonSerializer.Serialize(s, Options());
            Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
            File.WriteAllText(fullPath, json);
            return true;
        }
        catch { return false; }
    }
}
