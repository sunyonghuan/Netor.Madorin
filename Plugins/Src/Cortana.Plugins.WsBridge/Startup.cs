using Cortana.Plugins.WsBridge.Core;
using Cortana.Plugins.WsBridge.Services;

using Microsoft.Extensions.DependencyInjection;

using Netor.Cortana.Plugin.Native;

namespace Cortana.Plugins.WsBridge;

/// <summary>
/// WebSocket 中转插件入口，负责 DI 注册。
/// </summary>
[Plugin(
    Id = "wsbridge",
    Name = "WebSocket 中转插件",
    Version = "1.0.0",
    Description = "通用 WebSocket 中转插件，实现 AI ↔ 插件 ↔ 外部应用的双向消息路由。",
    Tags = ["WebSocket", "中转", "桥接", "消息路由"],
    Instructions = "使用 ws_bridge_connect 建立中转连接（需指定适配器和外部 WS 地址），ws_bridge_send 发送消息，ws_bridge_stop 中止回复，ws_bridge_status 查看状态，ws_bridge_disconnect 关闭连接。")]
public static partial class Startup
{
    public static void Configure(IServiceCollection services)
    {
        services.AddLogging();
        services.AddSingleton<AdapterRegistry>();
        services.AddSingleton<BridgeSessionManager>();
        services.AddHostedService<BridgeBackgroundService>();
    }
}
