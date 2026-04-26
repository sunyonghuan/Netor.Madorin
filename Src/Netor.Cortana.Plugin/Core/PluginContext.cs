using Microsoft.Extensions.Logging;

using Netor.Cortana.Entitys;
using Netor.Cortana.Plugin;

namespace Netor.Cortana.Plugin;

/// <summary>
/// <see cref="IPluginContext"/> 的宿主侧实现。
/// 为每个插件实例提供独立的数据目录和共享的宿主服务。
/// </summary>
public sealed class PluginContext : IPluginContext
{
    private readonly IAppPaths _appPaths;
    private readonly int _wsPort;
    private readonly int _feedPort;

    /// <inheritdoc />
    public string DataDirectory { get; }

    /// <inheritdoc />
    public string WorkspaceDirectory => _appPaths.WorkspaceDirectory;

    /// <inheritdoc />
    public ILoggerFactory LoggerFactory { get; }

    /// <inheritdoc />
    public IHttpClientFactory HttpClientFactory { get; }

    /// <inheritdoc />
    public int WsPort => _wsPort;
    public int FeedPort => _feedPort;

    public PluginContext(
        string dataDirectory,
        ILoggerFactory loggerFactory,
        IHttpClientFactory httpClientFactory,
        IAppPaths appPaths,
        int wsPort,
        int feedPort)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(dataDirectory);
        ArgumentNullException.ThrowIfNull(loggerFactory);
        ArgumentNullException.ThrowIfNull(httpClientFactory);
        ArgumentNullException.ThrowIfNull(appPaths);

        DataDirectory = dataDirectory;
        LoggerFactory = loggerFactory;
        HttpClientFactory = httpClientFactory;
        _appPaths = appPaths;
        _wsPort = wsPort;
        _feedPort = feedPort;

        if (!Directory.Exists(dataDirectory))
            Directory.CreateDirectory(dataDirectory);
    }
}
