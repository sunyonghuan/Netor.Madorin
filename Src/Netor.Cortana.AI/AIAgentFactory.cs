using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

using Netor.Cortana.AI.Drivers;
using Netor.Cortana.AI.Providers;
using Netor.Cortana.Entitys;
using Netor.Cortana.Entitys.Services;
using Netor.Cortana.Plugin;

namespace Netor.Cortana.AI;

/// <summary>
/// 智能体工厂，根据提供商和智能体配置创建 <see cref="AIAgent"/> 实例。
/// 支持 Ollama（本地网络）和 OpenAI 兼容协议两种模式。
/// </summary>
public sealed class AIAgentFactory(
    IAppPaths appPaths,
    IEnumerable<AIContextProvider> builtInProviders,
    PluginLoader pluginLoader,
    AiProviderDriverRegistry driverRegistry,
    IServiceProvider services,
    ILogger<AIAgentFactory> logger)
{
    public TokenTrackingChatClient? ChatClient { get; private set; }

    // ──────── Token 使用量跨 ChatClient 持久化 ────────
    // ChatClient 会因切换模型/厂商/智能体或 @提及子智能体而重建；
    // 为了让 UI 进度条稳定显示"最近一次真实用量"，把状态提升到工厂层。

    private long _lastInputTokens;
    private long _maxContextTokens = 128_000;

    /// <summary>最近一次模型调用实际使用的输入 token（= 当前上下文占用）。跨 ChatClient 重建保留。</summary>
    public long LastInputTokens => Volatile.Read(ref _lastInputTokens);

    /// <summary>当前模型的上下文窗口上限。跟随模型切换更新。</summary>
    public long MaxContextTokens => Volatile.Read(ref _maxContextTokens);

    /// <summary>上下文使用比例 0.0 ~ 1.0（可能 &gt; 1 表示超出）。</summary>
    public double ContextUsageRatio
    {
        get
        {
            var max = MaxContextTokens;
            return max > 0 ? (double)LastInputTokens / max : 0;
        }
    }

    /// <summary>当 token 使用量更新时触发（UI 可订阅此事件实时刷新进度条）。</summary>
    public event Action? TokenUsageChanged;

    /// <summary>
    /// 创建 <see cref="TokenTrackingChatClient"/> 包装器：
    /// <list type="bullet">
    /// <item>更新 <see cref="MaxContextTokens"/> 为当前模型上限；</item>
    /// <item>注入 usage 观察者，每次真实用量上报时同步 <see cref="LastInputTokens"/>；</item>
    /// <item>不清零旧值 —— 在新用量到达之前保留上一次显示，避免进度条闪烁归零。</item>
    /// </list>
    /// </summary>
    private TokenTrackingChatClient CreateTrackingClient(IChatClient inner, long maxContextTokens, bool enableReasoning)
    {
        var normalizedMax = maxContextTokens <= 0 ? 128_000 : maxContextTokens;
        Interlocked.Exchange(ref _maxContextTokens, normalizedMax);

        return new TokenTrackingChatClient(inner, normalizedMax, usage =>
        {
            var inputTokens = usage.InputTokenCount ?? 0;
            if (inputTokens <= 0) return;
            Interlocked.Exchange(ref _lastInputTokens, inputTokens);
            try { TokenUsageChanged?.Invoke(); }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "TokenUsageChanged 事件订阅者抛出异常");
            }
        }, enableReasoning);
    }

    /// <summary>重置 token 统计（新建会话或切换工作区时调用）。</summary>
    public void ResetTokenStats()
    {
        Interlocked.Exchange(ref _lastInputTokens, 0);
        try { TokenUsageChanged?.Invoke(); }
        catch { /* ignore */ }
    }

    /// <summary>
    /// 获取所有可用厂商驱动定义，供 UI 构建无关化选择器。
    /// </summary>
    public IReadOnlyList<AiProviderDriverDefinition> GetDriverDefinitions()
    {
        return driverRegistry.GetDefinitions();
    }

    /// <summary>
    /// 判断驱动类型是否已注册。
    /// </summary>
    public bool IsDriverRegistered(string? driverId)
    {
        return driverRegistry.IsRegistered(driverId);
    }

    /// <summary>
    /// 构建 <see cref="AIAgent"/>，组装内置提供器、插件提供器和历史记录提供器。
    /// 技能目录：应用启动目录/skills + 工作区/.cortana/skills。
    /// </summary>
    public AIAgent Build(AgentEntity agent, AiProviderEntity provider, AiModelEntity? model)
    {
        ArgumentNullException.ThrowIfNull(agent);
        ArgumentNullException.ThrowIfNull(provider);
        ArgumentNullException.ThrowIfNull(model);

        var driver = driverRegistry.Resolve(provider);

        var skillDirs = new List<string>
        {
            appPaths.UserSkillsDirectory,
            appPaths.WorkspaceSkillsDirectory
        };

        // 内置 Provider（通过 DI 批量注入）+ 技能目录
#pragma warning disable MAAI001
        var providers = new List<AIContextProvider>
        {
            new AgentSkillsProvider(skillDirs)
        };
#pragma warning restore MAAI001

        providers.AddRange(builtInProviders);

        // 组装插件和 MCP 工具 Provider
        var registeredTools = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        AssembleToolProviders(agent, providers, registeredTools);

        var enableReasoning = model.InteractionCapabilities.HasFlag(InteractionCapabilities.Reasoning);
        ChatClient = CreateTrackingClient(
            driver.CreateChatClient(provider, model),
            model.ContextLength,
            enableReasoning);

#pragma warning disable MAAI001
        return ChatClient
            .AsBuilder()
            .BuildAIAgent(new ChatClientAgentOptions
            {
                Name = agent.Name,
                AIContextProviders = providers,
                ChatOptions = driver.BuildChatOptions(provider, agent),
                ChatHistoryProvider = services.GetRequiredService<ChatHistoryDataProvider>(),
            })
            .AsBuilder()
            .Build();
#pragma warning restore MAAI001
    }

    /// <summary>
    /// 构建带有子智能体工具的主 <see cref="AIAgent"/>。
    /// 每个被提及的子智能体通过 <c>AsAIFunction()</c> 包装为工具函数注入主 Agent。
    /// </summary>
    public AIAgent BuildWithSubAgents(
        AgentEntity mainAgent,
        AiProviderEntity mainProvider,
        AiModelEntity mainModel,
        List<AgentMention> mentions,
        AiProviderService providerService,
        AiModelService modelService)
    {
        ArgumentNullException.ThrowIfNull(mainAgent);
        ArgumentNullException.ThrowIfNull(mainProvider);
        ArgumentNullException.ThrowIfNull(mainModel);

        var driver = driverRegistry.Resolve(mainProvider);

        var skillDirs = new List<string>
        {
            appPaths.UserSkillsDirectory,
            appPaths.WorkspaceSkillsDirectory
        };

#pragma warning disable MAAI001
        var providers = new List<AIContextProvider>
        {
            new AgentSkillsProvider(skillDirs)
        };
#pragma warning restore MAAI001

        providers.AddRange(builtInProviders);

        // 组装主智能体的插件和 MCP 工具
        var registeredTools = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        AssembleToolProviders(mainAgent, providers, registeredTools);

        // 构建子智能体并包装为 AIFunction
        var subAgentFunctions = new List<AIFunction>();
        var processedAgentIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var mention in mentions)
        {
            var subAgentEntity = mention.Agent;

            // 同名智能体去重
            if (!processedAgentIds.Add(subAgentEntity.Id)) continue;

            var (subProvider, subModel) = ResolveSubAgentProviderAndModel(
                subAgentEntity, mainProvider, mainModel, providerService, modelService);

            if (subProvider is null || subModel is null)
            {
                logger.LogWarning("子智能体 [{Name}] 的厂商或模型无法解析，跳过", subAgentEntity.Name);
                continue;
            }

            var subAgent = BuildSubAgent(subAgentEntity, subProvider, subModel);
            var agentFunction = subAgent.AsAIFunction(
                new AIFunctionFactoryOptions
                {
                    Name = $"agent_{subAgentEntity.Name}",
                    Description = string.IsNullOrWhiteSpace(subAgentEntity.Description)
                        ? $"调用子智能体「{subAgentEntity.Name}」来处理任务"
                        : subAgentEntity.Description
                });

            subAgentFunctions.Add(agentFunction);
            logger.LogInformation("已注入子智能体工具：agent_{Name}（{PluginCount} 个插件，{McpCount} 个 MCP）",
                subAgentEntity.Name, subAgentEntity.EnabledPluginIds.Count, subAgentEntity.EnabledMcpServerIds.Count);
        }

        // 通过 SubAgentContextProvider 注入子智能体工具
        if (subAgentFunctions.Count > 0)
        {
#pragma warning disable MAAI001
            providers.Add(new SubAgentContextProvider(subAgentFunctions));
#pragma warning restore MAAI001
        }

        var enableReasoning2 = mainModel.InteractionCapabilities.HasFlag(InteractionCapabilities.Reasoning);
        ChatClient = CreateTrackingClient(
            driver.CreateChatClient(mainProvider, mainModel),
            mainModel.ContextLength,
            enableReasoning2);

#pragma warning disable MAAI001
        return ChatClient
            .AsBuilder()
            .BuildAIAgent(new ChatClientAgentOptions
            {
                Name = mainAgent.Name,
                AIContextProviders = providers,
                ChatOptions = driver.BuildChatOptions(mainProvider, mainAgent),
                ChatHistoryProvider = services.GetRequiredService<ChatHistoryDataProvider>(),
            })
            .AsBuilder()
            .Build();
#pragma warning restore MAAI001
    }

    /// <summary>
    /// 构建子智能体（轻量）：仅携带 instructions + plugins + MCP，不带历史/memory/skills。
    /// </summary>
    private AIAgent BuildSubAgent(AgentEntity agent, AiProviderEntity provider, AiModelEntity model)
    {
        var driver = driverRegistry.Resolve(provider);

        var providers = new List<AIContextProvider>();
        var registeredTools = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        AssembleToolProviders(agent, providers, registeredTools);

        var chatClient = driver.CreateChatClient(provider, model);

#pragma warning disable MAAI001
        return chatClient
            .AsBuilder()
            .BuildAIAgent(new ChatClientAgentOptions
            {
                Name = agent.Name,
                Description = agent.Description,
                AIContextProviders = providers,
                ChatOptions = driver.BuildChatOptions(provider, agent),
            })
            .AsBuilder()
            .Build();
#pragma warning restore MAAI001
    }

    /// <summary>
    /// 解析子智能体的厂商和模型：优先使用子智能体自身配置，为空时跟随主智能体。
    /// </summary>
    private static (AiProviderEntity? Provider, AiModelEntity? Model) ResolveSubAgentProviderAndModel(
        AgentEntity subAgent,
        AiProviderEntity mainProvider,
        AiModelEntity mainModel,
        AiProviderService providerService,
        AiModelService modelService)
    {
        var provider = string.IsNullOrEmpty(subAgent.DefaultProviderId)
            ? mainProvider
            : providerService.GetById(subAgent.DefaultProviderId) ?? mainProvider;

        var model = string.IsNullOrEmpty(subAgent.DefaultModelId)
            ? mainModel
            : modelService.GetById(subAgent.DefaultModelId) ?? mainModel;

        return (provider, model);
    }

    /// <summary>
    /// 组装智能体已启用的插件 Provider 和 MCP Server Provider 到 providers 列表。
    /// </summary>
    private void AssembleToolProviders(
        AgentEntity agent,
        List<AIContextProvider> providers,
        Dictionary<string, string> registeredTools)
    {
        // 合并该智能体已启用的插件 Provider
        var enabledIds = agent.EnabledPluginIds;

        foreach (var plugin in pluginLoader.GetActivePlugins())
        {
            if (!enabledIds.Contains(plugin.Id)) continue;

            var excluded = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            foreach (var tool in plugin.Tools)
            {
                var toolName = tool.Name;

                if (toolName.StartsWith("sys_", StringComparison.OrdinalIgnoreCase))
                {
                    logger.LogWarning(
                        "工具名称冲突，已跳过：工具 '{ToolName}' 使用了系统保留前缀 'sys_'，来源：插件 [{PluginName}]({PluginId} v{Version})",
                        toolName, plugin.Name, plugin.Id, plugin.Version);
                    excluded.Add(toolName);
                    continue;
                }

                if (registeredTools.TryGetValue(toolName, out var existingSource))
                {
                    logger.LogWarning(
                        "工具名称冲突，已跳过：工具 '{ToolName}' 来自插件 [{PluginName}]({PluginId} v{Version}) 与已注册的 [{ExistingSource}] 重复",
                        toolName, plugin.Name, plugin.Id, plugin.Version, existingSource);
                    excluded.Add(toolName);
                }
                else
                {
                    registeredTools[toolName] = $"插件 {plugin.Name}({plugin.Id})";
                }
            }

            providers.Add(excluded.Count > 0
                ? new PluginContextProvider(plugin, excluded)
                : new PluginContextProvider(plugin));
        }

        // 合并该智能体已启用的 MCP Server Provider
        var enabledMcpIds = agent.EnabledMcpServerIds;

        foreach (var mcpHost in pluginLoader.GetActiveMcpServers())
        {
            if (!enabledMcpIds.Contains(mcpHost.Id)) continue;

            var excluded = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            foreach (var tool in mcpHost.Tools)
            {
                var toolName = tool.Name;

                if (toolName.StartsWith("sys_", StringComparison.OrdinalIgnoreCase))
                {
                    logger.LogWarning(
                        "工具名称冲突，已跳过：工具 '{ToolName}' 使用了系统保留前缀 'sys_'，来源：MCP 服务器 [{McpName}]({McpId})",
                        toolName, mcpHost.Name, mcpHost.Id);
                    excluded.Add(toolName);
                    continue;
                }

                if (registeredTools.TryGetValue(toolName, out var existingSource))
                {
                    logger.LogWarning(
                        "工具名称冲突，已跳过：工具 '{ToolName}' 来自 MCP 服务器 [{McpName}]({McpId}) 与已注册的 [{ExistingSource}] 重复",
                        toolName, mcpHost.Name, mcpHost.Id, existingSource);
                    excluded.Add(toolName);
                }
                else
                {
                    registeredTools[toolName] = $"MCP {mcpHost.Name}({mcpHost.Id})";
                }
            }

            providers.Add(excluded.Count > 0
                ? new McpContextProvider(mcpHost, excluded)
                : new McpContextProvider(mcpHost));
        }
    }
}