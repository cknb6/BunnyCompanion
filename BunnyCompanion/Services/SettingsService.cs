using System.IO;
using System.Text.Json;
using BunnyCompanion.Models;

namespace BunnyCompanion.Services;

public sealed class SettingsService
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true,
    };

    public string ConfigDirectory { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "BunnyCompanion");

    public string SettingsPath => Path.Combine(ConfigDirectory, "settings.json");

    public PetSettings Load()
    {
        try
        {
            Directory.CreateDirectory(ConfigDirectory);
            if (!File.Exists(SettingsPath))
                return new PetSettings();

            var json = File.ReadAllText(SettingsPath);
            var settings = JsonSerializer.Deserialize<PetSettings>(json, JsonOptions) ?? new PetSettings();
            settings.Normalize();
            return settings;
        }
        catch
        {
            return new PetSettings();
        }
    }

    public void Save(PetSettings settings)
    {
        settings.Normalize();
        Directory.CreateDirectory(ConfigDirectory);
        var temporaryPath = SettingsPath + ".tmp";
        var json = JsonSerializer.Serialize(settings, JsonOptions);
        File.WriteAllText(temporaryPath, json);
        File.Move(temporaryPath, SettingsPath, true);
    }
}
