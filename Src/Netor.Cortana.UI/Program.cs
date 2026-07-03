namespace Netor.Cortana.UI;

internal static class Program
{
    [STAThread]
    private static void Main(string[] args)
    {
        RegisterGlobalExceptionLogging();

        try
        {
            BuildAvaloniaApp()
                .StartWithClassicDesktopLifetime(args);
        }
        catch (Exception ex)
        {
            WriteFatalLog("主程序未处理异常", ex);
            throw;
        }
    }

    private static void RegisterGlobalExceptionLogging()
    {
        AppDomain.CurrentDomain.UnhandledException += (_, args) =>
        {
            if (args.ExceptionObject is Exception ex)
            {
                WriteFatalLog("AppDomain 未处理异常", ex);
            }
            else
            {
                WriteFatalLog($"AppDomain 未处理异常：{args.ExceptionObject}");
            }
        };

        TaskScheduler.UnobservedTaskException += (_, args) =>
        {
            WriteFatalLog("Task 未观察异常", args.Exception);
            args.SetObserved();
        };
    }

    private static void WriteFatalLog(string message, Exception? exception = null)
    {
        try
        {
            var baseDir = Path.GetDirectoryName(Environment.ProcessPath) ?? Directory.GetCurrentDirectory();
            var logDir = Environment.GetEnvironmentVariable("CORTANA_LOG_DIR") ?? Path.Combine(baseDir, "logs");
            Directory.CreateDirectory(logDir);
            var line = exception is null
                ? $"{DateTimeOffset.Now:O} {message}{Environment.NewLine}"
                : $"{DateTimeOffset.Now:O} {message}{Environment.NewLine}{exception}{Environment.NewLine}";
            File.AppendAllText(Path.Combine(logDir, "fatal.log"), line);
        }
        catch
        {
            // ignore fatal logging failure
        }
    }

    /// <summary>
    /// 构建 Avalonia 应用实例，供启动和设计器预览使用。
    /// </summary>
    public static AppBuilder BuildAvaloniaApp()
        => AppBuilder.Configure<App>()
            .UsePlatformDetect()
            .LogToTrace();
}