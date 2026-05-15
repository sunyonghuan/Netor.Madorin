using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

using Netor.Cortana.AI.Memory;
using Netor.Cortana.Entitys;
using Netor.Cortana.Networks.Proxy;

namespace Netor.Cortana.Networks;

/// <summary>
/// Networks 模块 DI 注册扩展方法。
/// </summary>
public static class NetworkServiceExtensions
{
    /// <summary>
    /// 注册 Networks 模块所有服务到 DI 容器。
    /// </summary>
    public static IServiceCollection AddCortanaNetworks(this IServiceCollection services)
    {
        services.AddSingleton<WebSocketRequestContext>();

        services.AddSingleton<WebSocketEventRelayService>();
        services.AddSingleton<IHostedService>(sp => sp.GetRequiredService<WebSocketEventRelayService>());
        services.AddSingleton<WebSocketPluginBusServerService>();
        services.AddSingleton<IHostedService>(sp => sp.GetRequiredService<WebSocketPluginBusServerService>());
        services.AddSingleton<IChatTransport>(sp => sp.GetRequiredService<WebSocketPluginBusServerService>());
        services.AddSingleton<IPluginBusBroadcaster>(sp => sp.GetRequiredService<WebSocketPluginBusServerService>());
        services.AddSingleton<ILongMemorySupplyClient>(sp => sp.GetRequiredService<WebSocketPluginBusServerService>());
        services.AddSingleton<WebSocketConversationFeedRelayService>();
        services.AddSingleton<IHostedService>(sp => sp.GetRequiredService<WebSocketConversationFeedRelayService>());

        // 阶段 2B 新增：Workflow 任务事件 Relay
        services.AddSingleton<WebSocketWorkflowFeedRelayService>();
        services.AddSingleton<IHostedService>(sp => sp.GetRequiredService<WebSocketWorkflowFeedRelayService>());

        services.AddSingleton<WebSocketChatOutputChannel>();
        services.AddSingleton<IAiOutputChannel>(sp => sp.GetRequiredService<WebSocketChatOutputChannel>());

        services.AddSingleton<WebSocketInputChannel>();
        services.AddSingleton<IHostedService>(sp => sp.GetRequiredService<WebSocketInputChannel>());
        services.AddSingleton<IAiInputChannel>(sp => sp.GetRequiredService<WebSocketInputChannel>());

        services.AddSingleton<OllamaProxyOptionsReader>();
        services.AddSingleton<ProxyModelEndpoints>();
        services.AddSingleton<ProxyChatEndpoints>();
        services.AddSingleton<DeepSeekReasoningReplayCache>();
        services.AddSingleton<OpenAiCompatibleRawProxy>();
        services.AddSingleton<OpenAiCompatibleEndpoints>();
        services.AddSingleton<ProxyRouteDispatcher>();
        services.AddSingleton<OllamaProxyServerService>();
        services.AddSingleton<IHostedService>(sp => sp.GetRequiredService<OllamaProxyServerService>());

        return services;
    }
}
