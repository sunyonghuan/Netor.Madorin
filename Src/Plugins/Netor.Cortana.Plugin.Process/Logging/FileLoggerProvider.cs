using Microsoft.Extensions.Logging;

using Netor.Cortana.Plugin.Process.Settings;

namespace Netor.Cortana.Plugin.Process.Logging;

/// <summary>
/// 文件日志 Provider。日志文件路径 <c>{DataDirectory}/logs/plugin.log</c>，
/// 首次写入时才打开文件（延迟到 <see cref="PluginSettings"/> 可用之后）。
/// <para>
/// AOT 安全：不使用反射、Emit 或运行时类型发现。
/// 线程安全：写入通过 <see cref="Lock"/> 串行化。
/// </para>
/// </summary>
[ProviderAlias("File")]
internal sealed class FileLoggerProvider : ILoggerProvider
{
    private readonly PluginSettingsAccessor _settings;
    private readonly LogLevel _minimumLevel;
    private readonly Lock _writeLock = new();
    private StreamWriter? _writer;
    private string? _currentPath;
    private bool _disposed;

    public FileLoggerProvider(PluginSettingsAccessor settings, LogLevel minimumLevel)
    {
        _settings = settings;
        _minimumLevel = minimumLevel;
    }

    /// <inheritdoc />
    public ILogger CreateLogger(string categoryName)
        => new FileLogger(categoryName, _minimumLevel, WriteLine);

    /// <summary>
    /// 真正写入日志行。在锁内打开文件（如未打开），然后写入并 flush。
    /// </summary>
    private void WriteLine(string line)
    {
        if (_disposed)
            return;

        lock (_writeLock)
        {
            if (_disposed)
                return;

            EnsureWriterOpened();
            if (_writer is null)
                return;

            try
            {
                _writer.WriteLine(line);
                _writer.Flush();
            }
            catch
            {
                // 写入失败不抛，避免日志问题影响插件主流程。
                // 此时可以尝试关闭 writer 以便下次重试。
                TryCloseWriter();
            }
        }
    }

    private void EnsureWriterOpened()
    {
        if (_writer is not null)
            return;

        if (!_settings.IsInitialized)
            return;

        var dir = _settings.Value.DataDirectory;
        if (string.IsNullOrEmpty(dir))
            return;

        try
        {
            var logDir = Path.Combine(dir, "logs");
            Directory.CreateDirectory(logDir);

            var path = Path.Combine(logDir, "plugin.log");
            _currentPath = path;

            var stream = new FileStream(
                path,
                FileMode.Append,
                FileAccess.Write,
                FileShare.Read);
            _writer = new StreamWriter(stream) { AutoFlush = false };
        }
        catch
        {
            // 打开文件失败（权限、路径等）：静默失败。
            // 宿主会通过 stderr 看到未打开的警告（由调用方控制）。
            TryCloseWriter();
        }
    }

    private void TryCloseWriter()
    {
        try
        {
            _writer?.Dispose();
        }
        catch
        {
            // 忽略关闭异常
        }
        _writer = null;
    }

    /// <inheritdoc />
    public void Dispose()
    {
        lock (_writeLock)
        {
            _disposed = true;
            TryCloseWriter();
        }
    }
}
