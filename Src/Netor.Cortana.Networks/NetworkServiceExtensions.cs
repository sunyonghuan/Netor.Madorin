using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

using Netor.Cortana.Entitys;

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

        services.AddSingleton<WebSocketServerService>();
        services.AddSingleton<IHostedService>(sp => sp.GetRequiredService<WebSocketServerService>());
        services.AddSingleton<IChatTransport>(sp => sp.GetRequiredService<WebSocketServerService>());
        services.AddSingleton<WebSocketEventRelayService>();
        services.AddSingleton<IHostedService>(sp => sp.GetRequiredService<WebSocketEventRelayService>());
        services.AddSingleton<WebSocketFeedServerService>();
        services.AddSingleton<IHostedService>(sp => sp.GetRequiredService<WebSocketFeedServerService>());
        services.AddSingleton<WebSocketConversationFeedRelayService>();
        services.AddSingleton<IHostedService>(sp => sp.GetRequiredService<WebSocketConversationFeedRelayService>());

        services.AddSingleton<WebSocketChatOutputChannel>();
        services.AddSingleton<IAiOutputChannel>(sp => sp.GetRequiredService<WebSocketChatOutputChannel>());

        services.AddSingleton<WebSocketInputChannel>();
        services.AddSingleton<IHostedService>(sp => sp.GetRequiredService<WebSocketInputChannel>());
        services.AddSingleton<IAiInputChannel>(sp => sp.GetRequiredService<WebSocketInputChannel>());

        return services;
    }
}