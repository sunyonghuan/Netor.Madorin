using Microsoft.Agents.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

using Netor.Cortana.AI.Providers;
using Netor.Cortana.Entitys;

namespace Netor.Cortana.AI;

/// <summary>
/// AI 模块 DI 注册扩展方法。
/// </summary>
public static class AIServiceExtensions
{
    /// <summary>
    /// 注册 AI 模块所有服务到 DI 容器。
    /// </summary>
    public static IServiceCollection AddCortanaAI(this IServiceCollection services)
    {
        // Providers（同时作为 AIContextProvider 注入到 AIAgentFactory）
        services.AddSingleton<FileMemoryProvider>();
        services.AddSingleton<AIContextProvider>(sp => sp.GetRequiredService<FileMemoryProvider>());
        services.AddSingleton<ChatHistoryDataProvider>();

        // 核心服务
        services.AddSingleton<AIAgentFactory>();
        services.AddSingleton<AiChatHostedService>();
        services.AddSingleton<IHostedService>(sp => sp.GetRequiredService<AiChatHostedService>());
        services.AddSingleton<IAiChatEngine>(sp => sp.GetRequiredService<AiChatHostedService>());
        services.AddTransient<AiModelFetcherService>();

        return services;
    }
}