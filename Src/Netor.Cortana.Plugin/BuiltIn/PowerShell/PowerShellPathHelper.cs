namespace Netor.Cortana.Plugin.BuiltIn.PowerShell;

/// <summary>
/// PowerShell 路径检测（优先 PowerShell 7+，回退到 Windows PowerShell 5.1）
/// </summary>
internal static class PowerShellPathHelper
{
    private static string? _cached;

    public static string GetPath()
    {
        if (_cached is not null)
            return _cached;

        // PowerShell 7+
        var psCorePath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
            "PowerShell", "pwsh.exe");

        if (File.Exists(psCorePath))
            return _cached = psCorePath;

        // Windows PowerShell 5.1
        var winPsPath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.System),
            "WindowsPowerShell", "v1.0", "powershell.exe");

        if (File.Exists(winPsPath))
            return _cached = winPsPath;

        return _cached = "powershell.exe";
    }
}
