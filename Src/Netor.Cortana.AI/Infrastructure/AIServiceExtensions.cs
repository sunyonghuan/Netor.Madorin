using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

using Netor.Cortana.AI.Delegation;
using Netor.Cortana.AI.Drivers;
using Netor.Cortana.AI.Handoff;
using Netor.Cortana.AI.Memory;
using Netor.Cortana.AI.Orchestration;
using Netor.Cortana.AI.Providers;
using Netor.Cortana.AI.MeetingMode;
using Netor.Cortana.AI.WorkMode;
using Netor.Cortana.AI.WorkMode.Background;
using Netor.Cortana.AI.WorkMode.Prompts;
using Netor.Cortana.AI.WorkMode.ProjectLead;
using Netor.Cortana.AI.WorkMode.Reliability;
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
        services.AddTransient<KimiOverrideHandler>()
            .AddHttpClient("Kimi")
            .AddHttpMessageHandler<KimiOverrideHandler>();
        // Providers（同时作为 AIContextProvider 注入到 AIAgentFactory）
        services.AddSingleton<ProjectSettingsProvider>();
        services.AddSingleton<Microsoft.Agents.AI.AIContextProvider>(sp => sp.GetRequiredService<ProjectSettingsProvider>());
        services.AddSingleton<LongMemoryContextProvider>();
        services.AddSingleton<Microsoft.Agents.AI.AIContextProvider>(sp => sp.GetRequiredService<LongMemoryContextProvider>());
        services.AddSingleton<ChatHistoryDataProvider>();
        services.AddSingleton<ModelPurposeResolver>();
        services.AddSingleton<IHostCapabilityBroker, HostCapabilityBroker>();
        services.AddSingleton<IPluginModelCapabilityService, PluginModelCapabilityService>();
        services.AddSingleton<ToolContextVersionService>();
        services.AddSingleton<SkillDirectoryWatcherService>();
        services.AddSingleton<IHostedService>(sp => sp.GetRequiredService<SkillDirectoryWatcherService>());

        // 厂商驱动
        services.AddSingleton<IAiProviderDriver, AliyunProviderDriver>();
        services.AddSingleton<IAiProviderDriver, OpenAiProviderDriver>();
        services.AddSingleton<IAiProviderDriver, AzureOpenAiProviderDriver>();
        services.AddSingleton<IAiProviderDriver, OllamaProviderDriver>();
        services.AddSingleton<IAiProviderDriver, AnthropicProviderDriver>();
        services.AddSingleton<IAiProviderDriver, DeepseekProviderDriver>();
        services.AddSingleton<IAiProviderDriver, KimiProviderDriver>();
        services.AddSingleton<IAiProviderDriver, GeminiProviderDriver>();
        services.AddSingleton<IAiProviderDriver, GlmProviderDriver>();
        services.AddSingleton<IAiProviderDriver, CustomProviderDriver>();
        services.AddSingleton<AiProviderDriverRegistry>();

        // 核心服务
        services.AddSingleton<AIAgentFactory>();
        services.AddSingleton<ChatAgentResolver>();
        services.AddSingleton<AgentOrchestratorOptions>();
        services.AddSingleton<IAgentOrchestrator, AgentOrchestrator>();
        services.AddSingleton<ChatAttachmentLoader>();
        services.AddSingleton<ChatGenerationContextBuilder>();
        services.AddSingleton<ChatMessageWriter>();
        services.AddSingleton<ChatSessionService>();
        services.AddSingleton<ChatTurnCancellationRegistry>();
        services.AddSingleton<ChatSelectionContextService>();
        services.AddSingleton<ChatConfigurationCoordinator>();
        services.AddSingleton<ChatTurnLifecycleCoordinator>();
        services.AddSingleton<ChatTurnInterruptionCoordinator>();
        services.AddSingleton<ChatTurnPreparationService>();
        services.AddSingleton<ChatTurnExecutor>();
        services.AddSingleton<ChatGenerationTurnCoordinator>();
        services.AddSingleton<ChatImageTurnExecutor>();
        services.AddSingleton<ChatVideoTurnExecutor>();
        services.AddSingleton<ChatMessagePersistence>();
        services.AddSingleton<ChatOrchestrationDiagnosticsService>();
        services.AddSingleton<ChatConversationEventPublisher>();
        services.AddSingleton<ChatRealtimeEventPublisher>();
        services.AddSingleton<SystemToolCatalogService>();
        services.AddSingleton<DelegatedAgentJobExecutor>();
        services.AddSingleton<DelegatedAgentJobRunner>();
        services.AddSingleton<IHostedService>(sp => sp.GetRequiredService<DelegatedAgentJobRunner>());
        services.AddSingleton<ExpertDelegationTools>();
        services.AddSingleton<AiChatHostedService>();
        services.AddSingleton<IHostedService>(sp => sp.GetRequiredService<AiChatHostedService>());
        services.AddSingleton<IAiChatEngine>(sp => sp.GetRequiredService<AiChatHostedService>());
        services.AddTransient<AiModelFetcherService>();

        // 摘要/压缩模型解析器
        services.AddSingleton<IChatCompactionClientResolver, ChatCompactionClientResolver>();

        // Proxy 独立外部调用通道：不复用主聊天会话。
        services.AddSingleton<ProxyUsageTracker>();
        services.AddSingleton<IAiProxyAgentBackend, CortanaOllamaProxyAgentBackend>();

        // 工作模式
        services.AddSingleton<LocalFilePromptProvider>();
        services.AddSingleton<EmbeddedResourcePromptProvider>(sp =>
            new EmbeddedResourcePromptProvider(typeof(GeneralManagerAgentBuilder).Assembly));
        services.AddSingleton<IPromptProvider>(sp =>
            CompositePromptProvider.CreateDefault(
                sp.GetRequiredService<LocalFilePromptProvider>(),
                sp.GetRequiredService<EmbeddedResourcePromptProvider>()));
        services.AddSingleton<ICurrentSessionResolver, CurrentSessionResolver>();
        services.AddSingleton<IIntentClassifier, IntentClassifier>();
        services.AddSingleton<IWorkModeInputRouter, WorkModeInputRouter>();
        services.AddSingleton<GeneralManagerAgentBuilder>();
        services.AddSingleton<DeadlockDetector>();
        services.AddSingleton<WorkTaskCancellationRegistry>();
        services.AddSingleton<RunningStepInterruptService>();
        services.AddSingleton<WorkTaskTitleService>();
        services.AddSingleton<IWorkTaskReportCompactionService, WorkTaskReportCompactionService>();
        services.AddSingleton<WorkflowExecutor>();
        services.AddSingleton<WorkHandoffTools>();
        services.AddSingleton<WorkModeStartupService>();
        services.AddSingleton<IHostedService>(sp => sp.GetRequiredService<WorkModeStartupService>());
        services.AddSingleton<ProjectLeadWatchdogService>();
        services.AddSingleton<IHostedService>(sp => sp.GetRequiredService<ProjectLeadWatchdogService>());
        services.AddSingleton<MeetingStartupService>();
        services.AddSingleton<IHostedService>(sp => sp.GetRequiredService<MeetingStartupService>());
        services.AddSingleton<MeetingCancellationRegistry>();
        services.AddSingleton<MeetingAttachmentImporter>();
        services.AddSingleton<MeetingHistoryProvider>();
        services.AddSingleton<MeetingCompactionService>();
        services.AddSingleton<MeetingAgentBuilder>();
        services.AddSingleton<MeetingCompletionArchiveService>();
        services.AddSingleton<IHostedService>(sp => sp.GetRequiredService<MeetingCompletionArchiveService>());
        services.AddSingleton<MeetingExecutor>();
        services.AddSingleton<SubAgentJobExecutor>();
        services.AddSingleton<IBackgroundJobExecutor>(sp => sp.GetRequiredService<SubAgentJobExecutor>());

        return services;
    }
}
