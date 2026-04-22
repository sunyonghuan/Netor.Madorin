using Microsoft.Extensions.Logging;

namespace Netor.Cortana.Plugin;

/// <summary>
/// 插件运行时上下文。
/// </summary>
public interface IPluginContext
{
    /// <summary>插件数据目录。</summary>
    string DataDirectory { get; }

    /// <summary>当前工作区目录。</summary>
    string WorkspaceDirectory { get; }

    /// <summary>日志工厂。</summary>
    ILoggerFactory LoggerFactory { get; }

    /// <summary>HTTP 客户端工厂。</summary>
    IHttpClientFactory HttpClientFactory { get; }

    /// <summary>宿主 WebSocket 端口。</summary>
    int WsPort { get; }
}