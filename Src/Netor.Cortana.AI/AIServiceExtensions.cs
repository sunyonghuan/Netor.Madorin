using Microsoft.Agents.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

using Netor.Cortana.AI.Drivers;
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
        // 自定义 User-Agent 避免部分中转站 Cloudflare WAF 拦截
        services.AddTransient<UserAgentOverrideHandler>();
        services.AddHttpClient("OpenAiCompatible")
            .AddHttpMessageHandler<UserAgentOverrideHandler>();

        // Providers（同时作为 AIContextProvider 注入到 AIAgentFactory）
        services.AddSingleton<FileMemoryProvider>();
        services.AddSingleton<AIContextProvider>(sp => sp.GetRequiredService<FileMemoryProvider>());
        services.AddSingleton<ChatHistoryDataProvider>();
        services.AddSingleton<ModelPurposeResolver>();

        // 厂商驱动
        services.AddSingleton<IAiProviderDriver, OpenAiProviderDriver>();
        services.AddSingleton<IAiProviderDriver, AzureOpenAiProviderDriver>();
        services.AddSingleton<IAiProviderDriver, OllamaProviderDriver>();
        services.AddSingleton<IAiProviderDriver, AnthropicProviderDriver>();
        services.AddSingleton<IAiProviderDriver, GeminiProviderDriver>();
        services.AddSingleton<IAiProviderDriver, GlmProviderDriver>();
        services.AddSingleton<IAiProviderDriver, CustomProviderDriver>();
        services.AddSingleton<AiProviderDriverRegistry>();

        // 核心服务
        services.AddSingleton<AIAgentFactory>();
        services.AddSingleton<AiChatHostedService>();
        services.AddSingleton<IHostedService>(sp => sp.GetRequiredService<AiChatHostedService>());
        services.AddSingleton<IAiChatEngine>(sp => sp.GetRequiredService<AiChatHostedService>());
        services.AddTransient<AiModelFetcherService>();

        return services;
    }
}