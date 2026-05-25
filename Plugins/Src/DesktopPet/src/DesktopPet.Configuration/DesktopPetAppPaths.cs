namespace DesktopPet.Configuration;

public sealed class DesktopPetAppPaths
{
    public DesktopPetAppPaths(string? baseDirectory = null)
    {
        BaseDirectory = baseDirectory ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "DesktopPet");
        ConfigurationDirectory = BaseDirectory;
        LogsDirectory = Path.Combine(BaseDirectory, "logs");

        Directory.CreateDirectory(ConfigurationDirectory);
        Directory.CreateDirectory(LogsDirectory);
    }

    public string BaseDirectory { get; }

    public string ConfigurationDirectory { get; }

    public string LogsDirectory { get; }
}
