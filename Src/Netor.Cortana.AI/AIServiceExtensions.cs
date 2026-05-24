using Microsoft.Agents.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

using Netor.Cortana.AI.Drivers;
using Netor.Cortana.AI.Memory;
using Netor.Cortana.AI.Orchestration;
using Netor.Cortana.AI.Providers;
using Netor.Cortana.AI.TaskEngine;
using Netor.Cortana.AI.TaskEngine.Agents;
using Netor.Cortana.AI.TaskEngine.Persistence;
using Netor.Cortana.AI.TaskEngine.Scheduling;
using Netor.Cortana.Entitys;
using Netor.Cortana.Entitys.Services;
using Netor.Cortana.AI.Proxys;
using Netor.Cortana.Entitys.Proxy;

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
        services.AddTransient<DeepseekOverrideHandler>()
            .AddHttpClient("Deepseek")
            .AddHttpMessageHandler<DeepseekOverrideHandler>();
        // Providers（同时作为 AIContextProvider 注入到 AIAgentFactory）
        services.AddSingleton<ProjectSettingsProvider>();
        services.AddSingleton<AIContextProvider>(sp => sp.GetRequiredService<ProjectSettingsProvider>());
        services.AddSingleton<LongMemoryContextProvider>();
        services.AddSingleton<AIContextProvider>(sp => sp.GetRequiredService<LongMemoryContextProvider>());
        services.AddSingleton<ChatHistoryDataProvider>();
        services.AddSingleton<ModelPurposeResolver>();
        services.AddSingleton<IHostCapabilityBroker, HostCapabilityBroker>();
        services.AddSingleton<IPluginModelCapabilityService, PluginModelCapabilityService>();

        // 厂商驱动
        services.AddSingleton<IAiProviderDriver, OpenAiProviderDriver>();
        services.AddSingleton<IAiProviderDriver, AzureOpenAiProviderDriver>();
        services.AddSingleton<IAiProviderDriver, OllamaProviderDriver>();
        services.AddSingleton<IAiProviderDriver, AnthropicProviderDriver>();
        services.AddSingleton<IAiProviderDriver, DeepseekProviderDriver>();
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

        // 阶段 2A：Chat 模式编排器（None / ToolDelegation / HandoffChat）
        services.AddSingleton(new AgentOrchestratorOptions());
        services.AddSingleton<IAgentOrchestrator, AgentOrchestrator>();

        // 摘要/压缩模型解析器（TaskEngine SubAgentRunner 也依赖此服务）
        services.AddSingleton<IChatCompactionClientResolver, ChatCompactionClientResolver>();

        // Proxy 独立外部调用通道：不复用主聊天会话。
        services.AddSingleton<ProxyUsageTracker>();
        services.AddSingleton<IAiProxyAgentBackend, CortanaOllamaProxyAgentBackend>();

        // P4 任务执行引擎（Commit P4-1）
        // 详见 docs/未来版本策划/聊天式任务发起与动态智能体/04-P4方案设计-任务执行引擎.md §8.1
        services.AddSingleton(new TaskEngineOptions());
        services.AddSingleton<GlobalLlmThrottle>(sp =>
            new GlobalLlmThrottle(sp.GetRequiredService<TaskEngineOptions>().MaxLlmConcurrency));
        services.AddSingleton<IStepScheduler, StepScheduler>();

        // P4-2: 计划持久化（文件系统实现）
        services.AddSingleton<TaskFileResolver>();
        services.AddSingleton<IPlanPersistence, FilePlanPersistence>();

        // P4-3: 主智能体编排器（子智能体动态创建 + 四阶段流程）
        services.AddSingleton<SubAgentRunner>();
        services.AddSingleton<IOrchestratorAgent, OrchestratorAgent>();

        // P4-5: 启用任务执行引擎（P4-4 UI 已就绪，可以展示进度）
        services.AddSingleton<TaskExecutionEngine>();
        services.AddSingleton<IHostedService>(sp => sp.GetRequiredService<TaskExecutionEngine>());

        return services;
    }
}