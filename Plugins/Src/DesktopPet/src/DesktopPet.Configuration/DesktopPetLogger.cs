namespace DesktopPet.Configuration;

public sealed class DesktopPetLogger
{
    private readonly object _syncRoot = new();
    private readonly string _logPath;

    public DesktopPetLogger(DesktopPetAppPaths paths)
    {
        ArgumentNullException.ThrowIfNull(paths);
        Directory.CreateDirectory(paths.LogsDirectory);
        _logPath = Path.Combine(paths.LogsDirectory, "desktop-pet.log");
    }

    public string LogPath => _logPath;

    public void Info(string message)
    {
        Write("INFO", message);
    }

    public void Error(Exception exception, string message)
    {
        ArgumentNullException.ThrowIfNull(exception);
        Write("ERROR", $"{message}: {exception}");
    }

    private void Write(string level, string message)
    {
        lock (_syncRoot)
        {
            File.AppendAllText(_logPath, $"{DateTimeOffset.Now:O} [{level}] {message}{Environment.NewLine}");
        }
    }
}
