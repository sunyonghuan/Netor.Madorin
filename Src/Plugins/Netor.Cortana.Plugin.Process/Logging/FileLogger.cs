using Microsoft.Extensions.Logging;

namespace Netor.Cortana.Plugin.Process.Logging;

/// <summary>
/// 文件日志记录器。实际写入委托给 <see cref="FileLoggerProvider"/>，
/// 本类只负责过滤级别、格式化消息。
/// <para>
/// AOT 安全：所有类型均为已知类型，不使用反射。
/// </para>
/// </summary>
internal sealed class FileLogger : ILogger
{
    private readonly string _category;
    private readonly LogLevel _minimumLevel;
    private readonly Action<string> _writeLine;

    public FileLogger(string category, LogLevel minimumLevel, Action<string> writeLine)
    {
        _category = category;
        _minimumLevel = minimumLevel;
        _writeLine = writeLine;
    }

    /// <inheritdoc />
    public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

    /// <inheritdoc />
    public bool IsEnabled(LogLevel logLevel) =>
        logLevel != LogLevel.None && logLevel >= _minimumLevel;

    /// <inheritdoc />
    public void Log<TState>(
        LogLevel logLevel,
        EventId eventId,
        TState state,
        Exception? exception,
        Func<TState, Exception?, string> formatter)
    {
        if (!IsEnabled(logLevel))
            return;

        ArgumentNullException.ThrowIfNull(formatter);

        var message = formatter(state, exception);
        if (string.IsNullOrEmpty(message) && exception is null)
            return;

        var levelTag = logLevel switch
        {
            LogLevel.Trace => "TRC",
            LogLevel.Debug => "DBG",
            LogLevel.Information => "INF",
            LogLevel.Warning => "WRN",
            LogLevel.Error => "ERR",
            LogLevel.Critical => "CRT",
            _ => "LOG"
        };

        var timestamp = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff");
        var line = $"[{timestamp}] [{levelTag}] [{_category}] {message}";
        if (exception is not null)
            line += Environment.NewLine + exception;

        _writeLine(line);
    }
}
