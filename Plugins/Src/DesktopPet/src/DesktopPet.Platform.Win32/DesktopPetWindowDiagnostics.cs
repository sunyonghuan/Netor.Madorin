namespace DesktopPet.Platform.Win32;

internal static class DesktopPetWindowDiagnostics
{
    public static void LogInfo(string message)
    {
        try
        {
            Write("INFO", message);
        }
        catch
        {
            // Avoid throwing from a Win32 callback.
        }
    }

    public static void Log(Exception exception, string message)
    {
        try
        {
            Write("ERROR", $"{message}: {exception}");
        }
        catch
        {
            // Avoid throwing from a Win32 callback.
        }
    }

    private static void Write(string level, string message)
    {
        var directory = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "DesktopPet",
            "logs");
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, "desktop-pet.log");
        File.AppendAllText(path, $"{DateTimeOffset.Now:O} [{level}] {message}{Environment.NewLine}");
    }
}
