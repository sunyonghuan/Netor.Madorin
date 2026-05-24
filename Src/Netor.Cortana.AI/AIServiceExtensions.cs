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
using Netor.Cortana.AI.Workflow;
using Netor.Cortana.AI.Workflow.Title;
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

        // 阶段 2B：Workflow 模式后端骨架
        services.AddSingleton<IChatCompactionClientResolver, ChatCompactionClientResolver>();
        services.AddSingleton<WorkflowTaskRepository>();
        services.AddSingleton<WorkflowStepRepository>();
        services.AddSingleton<IWorkflowTitleGenerator, WorkflowTitleGenerator>();
        services.AddSingleton(new WorkflowExecutorOptions());

        // 阶段 5B Phase 2：Checkpoint 持久化（SDK CheckpointManager 注入到 WorkflowExecutor）
        // 详见 docs/未来版本策划/多智能体编排模式策划/04-实施阶段.md §5B.2
        services.AddSingleton<WorkflowCheckpointRepository>();
        services.AddSingleton<Workflow.Checkpointing.SqliteCheckpointStore>();
        services.AddSingleton<Microsoft.Agents.AI.Workflows.CheckpointManager>(sp =>
            Microsoft.Agents.AI.Workflows.CheckpointManager.CreateJson(
                sp.GetRequiredService<Workflow.Checkpointing.SqliteCheckpointStore>()));

        // P2-2：动态子智能体 Registry（任务级生命周期，Manager 通过 create_subagent 工具创建临时子智能体）
        // 详见 Docs/未来版本策划/聊天式任务发起与动态智能体/01-P2方案设计.md §2-A
        services.AddSingleton<Workflow.DynamicAgents.DynamicAgentRegistry>();

        // P2-4：动态子智能体创建审批闸（与 Registry 同生命周期；CreateSubAgentTool 内 await 用户决策）
        // 详见 Docs/未来版本策划/聊天式任务发起与动态智能体/03-实施阶段.md §4 plan §A.2
        services.AddSingleton<Workflow.DynamicAgents.DynamicAgentCreationGate>();

        services.AddSingleton<WorkflowExecutor>();
        services.AddSingleton<IWorkflowExecutor>(sp => sp.GetRequiredService<WorkflowExecutor>());
        services.AddSingleton<IHostedService>(sp => sp.GetRequiredService<WorkflowExecutor>());

        // 阶段 5B Phase 3：Chat↔Workflow 桥接（详见 04-实施阶段.md §5B.3）
        // - SuggestionDetector：启发式判断复杂任务，由 AiChatHostedService 调用
        // - BackflowService：把 Workflow FinalReport 回灌到 Chat 会话，由 WorkspaceTab UI 调用
        services.AddSingleton<Workflow.Bridges.WorkflowSuggestionDetector>();
        services.AddSingleton<Workflow.Bridges.WorkflowToChatBackflowService>();

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