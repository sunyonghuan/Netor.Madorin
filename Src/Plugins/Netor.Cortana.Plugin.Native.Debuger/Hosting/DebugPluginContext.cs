using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

using Netor.Cortana.Plugin;

using System.Net.Http;

namespace Netor.Cortana.Plugin.Native.Debugger.Hosting;

/// <summary>
/// 调试环境下的插件上下文实现
/// 模拟真实宿主环境，为插件提供隔离的测试上下文
/// </summary>
public class DebugPluginContext : IPluginContext
{
    /// <summary>插件专属的数据存储目录</summary>
    public string DataDirectory { get; }

    /// <summary>当前工作区目录</summary>
    public string WorkspaceDirectory { get; }

    /// <summary>日志工厂</summary>
    public ILoggerFactory LoggerFactory { get; }

    /// <summary>HTTP 客户端工厂</summary>
    public IHttpClientFactory HttpClientFactory { get; }

    /// <summary>WebSocket 服务器端口</summary>
    public int WsPort { get; }

    /// <summary>对话事实 Feed 专用端口</summary>
    public int FeedPort { get; }

    /// <summary>
    /// 创建调试上下文
    /// </summary>
    /// <param name="dataDirectory">数据目录，默认为 .debug_data</param>
    /// <param name="workspaceDirectory">工作区目录，默认为 .debug_workspace</param>
    /// <param name="wsPort">WS 端口，默认 9090</param>
    /// <param name="loggerFactory">日志工厂</param>
    public DebugPluginContext(
        string? dataDirectory = null,
        string? workspaceDirectory = null,
        int wsPort = 9090,
        int feedPort = 9091,
        ILoggerFactory? loggerFactory = null)
    {
        DataDirectory = dataDirectory ?? Path.Combine(AppContext.BaseDirectory, ".debug_data");
        WorkspaceDirectory = workspaceDirectory ?? Path.Combine(AppContext.BaseDirectory, ".debug_workspace");
        WsPort = wsPort;
        FeedPort = feedPort;
        LoggerFactory = loggerFactory ?? CreateDefaultLoggerFactory();
        HttpClientFactory = CreateDefaultHttpClientFactory();

        Directory.CreateDirectory(DataDirectory);
        Directory.CreateDirectory(WorkspaceDirectory);
    }

    private static ILoggerFactory CreateDefaultLoggerFactory()
    {
        return global::Microsoft.Extensions.Logging.LoggerFactory.Create(builder =>
        {
            builder.AddConsole();
            builder.SetMinimumLevel(LogLevel.Debug);
        });
    }

    private static IHttpClientFactory CreateDefaultHttpClientFactory()
    {
        var services = new ServiceCollection();
        services.AddHttpClient();
        var provider = services.BuildServiceProvider();
        return provider.GetRequiredService<IHttpClientFactory>();
    }
}