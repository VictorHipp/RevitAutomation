#nullable enable
using System;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace RevitAutomation.Addin.Commands.Patinillo;

public static class PatinilloSettingsStore
{
    public static string DefaultPath => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "RevitAutomation", "patinillo.json");

    private static JsonSerializerOptions Options() => new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() }
    };

    public static PatinilloSettings Load(string? path = null)
    {
        try
        {
            return JsonSerializer.Deserialize<PatinilloSettings>(File.ReadAllText(path ?? DefaultPath), Options())
                ?? new PatinilloSettings();
        }
        catch { return new PatinilloSettings(); }
    }

    public static bool Save(PatinilloSettings s, string? path = null)
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
