using Microsoft.Extensions.DependencyInjection;

namespace Netor.Madorin.Plugin.Native.Debugger;

/// <summary>
/// 调试运行选项
/// </summary>
public sealed class DebugOptions
{
    /// <summary>插件数据目录。</summary>
    public string? DataDirectory { get; set; }

    /// <summary>当前工作区目录。</summary>
    public string? WorkspaceDirectory { get; set; }

    /// <summary>插件目录。</summary>
    public string? PluginDirectory { get; set; }

    /// <summary>宿主 WebSocket 端口。</summary>
    public int WsPort { get; set; } = 9090;

    /// <summary>
    /// init.extensions 扩展字段，用于模拟生产宿主注入的运行时配置。
    /// </summary>
    public IDictionary<string, string>? Extensions { get; set; }

    /// <summary>额外服务注册回调。</summary>
    public Action<IServiceCollection>? ConfigureServices { get; set; }
}
