using DesktopPet.Abstractions;
using System.Text.Json;

namespace DesktopPet.Configuration;

public sealed class DesktopPetSettingsStore
{
    private const string SettingsFileName = "settings.json";

    private readonly string _settingsPath;

    public DesktopPetSettingsStore(string? baseDirectory = null)
    {
        var directory = new DesktopPetAppPaths(baseDirectory).ConfigurationDirectory;

        Directory.CreateDirectory(directory);
        _settingsPath = Path.Combine(directory, SettingsFileName);
    }

    public string SettingsPath => _settingsPath;

    public DesktopPetSettings Load()
    {
        if (!File.Exists(_settingsPath))
        {
            return new DesktopPetSettings();
        }

        using var stream = File.OpenRead(_settingsPath);
        return JsonSerializer.Deserialize(stream, DesktopPetJsonContext.Default.DesktopPetSettings)
            ?? new DesktopPetSettings();
    }

    public void Save(DesktopPetSettings settings)
    {
        var directory = Path.GetDirectoryName(_settingsPath);
        if (!string.IsNullOrWhiteSpace(directory))
        {
            Directory.CreateDirectory(directory);
        }

        using var stream = File.Create(_settingsPath);
        JsonSerializer.Serialize(stream, settings, DesktopPetJsonContext.Default.DesktopPetSettings);
    }
}
