using System.ComponentModel;

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
        }, enableReasoning, appPaths);
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
        if (model.InteractionCapabilities.HasFlag(InteractionCapabilities.FunctionCall))
        {
            AssembleToolProviders(agent, providers, registeredTools);
        }
        else
        {
            logger.LogInformation("模型 {Model} 未启用函数调用，跳过工具配送。", model.Name);
        }

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
                Id = agent.Id,
                Name = agent.Name,
                Description = agent.Description,
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
        var mainFuncEnabled = mainModel.InteractionCapabilities.HasFlag(InteractionCapabilities.FunctionCall);
        if (mainFuncEnabled)
        {
            AssembleToolProviders(mainAgent, providers, registeredTools);
        }
        else
        {
            logger.LogInformation("主模型 {Model} 未启用函数调用，跳过工具配送与子智能体工具注入。", mainModel.Name);
        }

        // 构建子智能体并包装为 AIFunction（仅当主模型支持函数调用时才注入）
        var subAgentFunctions = new List<AIFunction>();
        var processedAgentIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        if (mainFuncEnabled)
        {
            foreach (var mention in mentions)
            {
                var subAgentEntity = mention.Agent;

                if (string.Equals(subAgentEntity.Id, mainAgent.Id, StringComparison.OrdinalIgnoreCase))
                {
                    logger.LogInformation("跳过与主智能体相同的子智能体：{Name}", subAgentEntity.Name);
                    continue;
                }

                // 同一智能体去重
                if (!processedAgentIds.Add(subAgentEntity.Id)) continue;

                var (subProvider, subModel) = ResolveSubAgentProviderAndModel(
                    subAgentEntity, mainProvider, mainModel, providerService, modelService);

                if (subProvider is null || subModel is null)
                {
                    logger.LogWarning("子智能体 [{Name}] 的厂商或模型无法解析，跳过", subAgentEntity.Name);
                    continue;
                }

                var subAgent = BuildSubAgent(subAgentEntity, subProvider, subModel);

                // OpenAI 协议要求 function.name 仅允许 [a-zA-Z0-9_-]，子智能体显示名常含中文，
                // 直接拼接会触发 400 invalid_request_error。这里改用 Id 前 8 位作为稳定且安全的后缀，
                // 可读名称放进 Description 让模型理解用途。
                var safeIdPart = new string([.. (subAgentEntity.Id ?? string.Empty)
                    .Where(c => c is >= 'a' and <= 'z'or >= 'A' and <= 'Z'or >= '0' and <= '9')
                    .Take(8)]);
                if (string.IsNullOrEmpty(safeIdPart)) safeIdPart = "unknown";
                var functionName = $"agent_{safeIdPart}";

                // 阶段 1：自定义委托替换 SDK 默认 AsAIFunction(options) 的单参数签名，
                // 让主 Agent 在调用子 Agent 时显式传递附件路径与描述（详见 03-编排模式与边界约束.md §7.2）。
                // subAgent / subAgentEntity 是 foreach 体内的局部变量，C# 闭包语义保证每次迭代独立。

                [Description("调用子智能体并可选携带附件来处理任务")]
                async Task<string> InvokeAgentWithAttachmentsAsync(
                    [Description("子智能体要回答的具体问题")] string query,
                    [Description("附件绝对路径数组（可空）")] string[]? attachmentPaths,
                    [Description("主 Agent 对每个附件的简短描述（可空，长度应与 attachmentPaths 一致）")] string[]? attachmentDescriptions,
                    CancellationToken ct)
                {
                    var contents = new List<AIContent> { new TextContent(query) };

                    if (attachmentPaths is { Length: > 0 })
                    {
                        for (var i = 0; i < attachmentPaths.Length; i++)
                        {
                            var path = attachmentPaths[i];
                            if (string.IsNullOrWhiteSpace(path) || !File.Exists(path)) continue;

                            var description = attachmentDescriptions is { Length: > 0 } && i < attachmentDescriptions.Length
                                ? attachmentDescriptions[i]
                                : Path.GetFileName(path);

                            var mime = GuessMimeFromPath(path);
                            if (IsImageMime(mime))
                            {
                                var data = await DataContent.LoadFromAsync(path, mime, cancellationToken: ct).ConfigureAwait(false);
                                contents.Add(data);
                                contents.Add(new TextContent($" ![{description}]({path}) "));
                            }
                            else
                            {
                                contents.Add(new TextContent($" [{description}]({path}) "));
                            }
                        }
                    }

                    var msg = new ChatMessage(ChatRole.User, contents);
                    var response = await subAgent.RunAsync([msg], cancellationToken: ct).ConfigureAwait(false);
                    return response.Text ?? string.Empty;
                }

                var agentFunction = AIFunctionFactory.Create(
                    InvokeAgentWithAttachmentsAsync,
                    new AIFunctionFactoryOptions
                    {
                        Name = functionName,
                        Description = string.IsNullOrWhiteSpace(subAgentEntity.Description)
                            ? $"调用子智能体「{subAgentEntity.Name}」来处理任务（可附带附件）"
                            : $"[{subAgentEntity.Name}] {subAgentEntity.Description}（可附带附件）"
                    });

                subAgentFunctions.Add(agentFunction);
                logger.LogInformation("已注入子智能体工具：{FunctionName}（显示名：{DisplayName}，{PluginCount} 个插件，{McpCount} 个 MCP）",
                    functionName, subAgentEntity.Name, subAgentEntity.EnabledPluginIds.Count, subAgentEntity.EnabledMcpServerIds.Count);
            }
        }

        // 通过 SubAgentContextProvider 注入子智能体工具
        if (subAgentFunctions.Count > 0)
        {
#pragma warning disable MAAI001
            providers.Add(new SubAgentContextProvider(subAgentFunctions));
#pragma warning restore MAAI001
        }

        // 阶段 1：当 mentions >= 2 且主模型支持 FunctionCall 时，注入 Coordinator instructions
        // 让主 Agent 进入"协调者"模式（先制定计划、按工具签名传附件、最终汇总）。
        // 详见 docs/未来版本策划/多智能体编排模式策划/04-实施阶段.md §阶段 1。
        if (mainFuncEnabled && mentions.Count >= 2)
        {
            var mentionAgents = mentions.Select(m => m.Agent).ToList();
#pragma warning disable MAAI001
            providers.Add(new OrchestrationInstructionsProvider(mentionAgents));
#pragma warning restore MAAI001
            logger.LogInformation("已启用 Coordinator 模式：mentions={Count}", mentions.Count);
        }

        var enableReasoning2 = mainModel.InteractionCapabilities.HasFlag(InteractionCapabilities.Reasoning);
        ChatClient = CreateTrackingClient(
            driver.CreateChatClient(mainProvider, mainModel),
            mainModel.ContextLength,
            enableReasoning2);

        // 阶段 0：当主模型不支持 FunctionCall 且用户 @ 了子智能体时，给一次用户可见提示（追加到 instructions 末尾），
        // 配合 logger.Warning 让"静默丢弃"问题可观测。
        var chatOptions = driver.BuildChatOptions(mainProvider, mainAgent);
        if (!mainFuncEnabled && mentions.Count > 0)
        {
            logger.LogWarning(
                "Main model '{Model}' does not support function call; {Count} mentioned sub-agents are dropped.",
                mainModel.Name, mentions.Count);

            var fallbackHint = $"\n\n[Note] 用户 @ 了 {mentions.Count} 个子智能体，但当前模型不支持工具调用，已退回单 Agent 模式。";
#pragma warning disable MEAI001
            chatOptions.Instructions = (chatOptions.Instructions ?? string.Empty) + fallbackHint;
#pragma warning restore MEAI001
        }

#pragma warning disable MAAI001
        return ChatClient
            .AsBuilder()
            .BuildAIAgent(new ChatClientAgentOptions
            {
                Id = mainAgent.Id,
                Name = mainAgent.Name,
                Description = mainAgent.Description,
                AIContextProviders = providers,
                ChatOptions = chatOptions,
                ChatHistoryProvider = services.GetRequiredService<ChatHistoryDataProvider>(),
            })
            .AsBuilder()
            .Build();
#pragma warning restore MAAI001
    }

    /// <summary>
    /// 阶段 3A：构建 HandoffChat 所需的 triage + specialists 智能体集合。
    /// triage 走完整 <see cref="Build"/>（带 ChatHistoryDataProvider / Skills），specialists 走轻量 <see cref="BuildSubAgent"/>。
    /// 返回的 AIAgent 集合由调用方（<c>AgentOrchestrator</c>）传给 <c>HandoffChatAgentBuilder</c> 组装 Workflow。
    /// 详见：docs/未来版本策划/多智能体编排模式策划/04-实施阶段.md §阶段 3A。
    /// </summary>
    /// <param name="triageEntity">分流入口智能体实体。</param>
    /// <param name="mainProvider">主厂商；当 specialist 自身未配置 DefaultProviderId 时跟随此值。</param>
    /// <param name="mainModel">主模型；当 specialist 自身未配置 DefaultModelId 时跟随此值。</param>
    /// <param name="specialistEntities">候选专家智能体实体列表。重复 ID / 与 triage 同 ID 会被自动跳过。</param>
    /// <param name="providerService">用于解析 specialist 自身厂商。</param>
    /// <param name="modelService">用于解析 specialist 自身模型。</param>
    /// <returns>(triage, specialists) 元组；specialists 可能因解析失败被过滤，调用方需自行处理空列表场景。</returns>
    public (AIAgent Triage, IReadOnlyList<AIAgent> Specialists) BuildHandoffAgents(
        AgentEntity triageEntity,
        AiProviderEntity mainProvider,
        AiModelEntity mainModel,
        IReadOnlyList<AgentEntity> specialistEntities,
        AiProviderService providerService,
        AiModelService modelService)
    {
        ArgumentNullException.ThrowIfNull(triageEntity);
        ArgumentNullException.ThrowIfNull(mainProvider);
        ArgumentNullException.ThrowIfNull(mainModel);
        ArgumentNullException.ThrowIfNull(specialistEntities);
        ArgumentNullException.ThrowIfNull(providerService);
        ArgumentNullException.ThrowIfNull(modelService);

        // triage 走完整 Build：保留 ChatHistoryDataProvider / Skills / 全部内置 Provider。
        // 注意 WorkflowHostAgent 接管的是顶层 session（WorkflowSession），triage 自身的
        // ChatHistoryProvider 不会被 SDK 直接调用，但 ChatHistoryDataProvider 提供的
        // ProjectSettings / LongMemory 等 InvokingContext 仍随 ChatClient 调用链生效。
        var triage = Build(triageEntity, mainProvider, mainModel);

        var specialists = new List<AIAgent>(specialistEntities.Count);
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { triageEntity.Id };

        foreach (var specialistEntity in specialistEntities)
        {
            // 与 triage 同 ID 或重复 specialist 全部跳过
            if (!seen.Add(specialistEntity.Id))
            {
                logger.LogInformation("HandoffChat 跳过重复智能体：{Name}", specialistEntity.Name);
                continue;
            }

            var (subProvider, subModel) = ResolveSubAgentProviderAndModel(
                specialistEntity, mainProvider, mainModel, providerService, modelService);

            if (subProvider is null || subModel is null)
            {
                logger.LogWarning(
                    "HandoffChat specialist [{Name}] 厂商或模型无法解析，跳过",
                    specialistEntity.Name);
                continue;
            }

            var specialist = BuildSubAgent(specialistEntity, subProvider, subModel);
            specialists.Add(specialist);
        }

        return (triage, specialists);
    }

    /// <summary>
    /// 构建子智能体（轻量）：仅携带 instructions + plugins + MCP，不带历史/memory/skills。
    /// </summary>
    private AIAgent BuildSubAgent(AgentEntity agent, AiProviderEntity provider, AiModelEntity model)
    {
        var driver = driverRegistry.Resolve(provider);

        var providers = new List<AIContextProvider>();
        var registeredTools = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (model.InteractionCapabilities.HasFlag(InteractionCapabilities.FunctionCall))
        {
            AssembleToolProviders(agent, providers, registeredTools);
        }
        else
        {
            logger.LogInformation("子智能体模型 {Model} 未启用函数调用，跳过工具配送。", model.Name);
        }

        var chatClient = driver.CreateChatClient(provider, model);

#pragma warning disable MAAI001
        return chatClient
            .AsBuilder()
            .BuildAIAgent(new ChatClientAgentOptions
            {
                Id = agent.Id,
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
        var globalPluginService = services.GetService<GlobalPluginService>();
        var globalPluginIds = globalPluginService?.GetEnabledPluginIds() ?? [];
        var injectedPluginIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        // 合并全局插件 Provider。仅全局插件目录中的插件允许生效。
        foreach (var pluginInfo in pluginLoader.GetLoadedPluginInfos())
        {
            var plugin = pluginInfo.Plugin;
            if (pluginInfo.Scope != PluginInstallScope.Global) continue;
            if (!globalPluginIds.Contains(plugin.Id, StringComparer.OrdinalIgnoreCase)) continue;

            AddPluginProvider(plugin, providers, registeredTools);
            injectedPluginIds.Add(plugin.Id);
        }

        // 合并该智能体已启用的插件 Provider
        var enabledIds = agent.EnabledPluginIds;

        foreach (var plugin in pluginLoader.GetActivePlugins())
        {
            if (!enabledIds.Contains(plugin.Id)) continue;
            if (injectedPluginIds.Contains(plugin.Id)) continue;

            AddPluginProvider(plugin, providers, registeredTools);
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

    private void AddPluginProvider(
        IPlugin plugin,
        List<AIContextProvider> providers,
        Dictionary<string, string> registeredTools)
    {
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

    // ==================================================================================
    // 阶段 1：MIME 类型 helper（仅供子 Agent 工具委托使用，作用域局部于本工厂）
    // 与 AiChatHostedService 的同名方法保持语义一致；阶段 2A 起可下沉到公共工具类。
    // ==================================================================================

    private static bool IsImageMime(string mimeType) =>
        !string.IsNullOrEmpty(mimeType) &&
        mimeType.StartsWith("image/", StringComparison.OrdinalIgnoreCase);

    private static string GuessMimeFromPath(string path)
    {
        if (string.IsNullOrWhiteSpace(path)) return "application/octet-stream";

        var ext = Path.GetExtension(path);
        if (string.IsNullOrEmpty(ext)) return "application/octet-stream";

        return ext.ToLowerInvariant() switch
        {
            ".png" => "image/png",
            ".jpg" or ".jpeg" => "image/jpeg",
            ".gif" => "image/gif",
            ".bmp" => "image/bmp",
            ".webp" => "image/webp",
            ".svg" => "image/svg+xml",
            ".tiff" or ".tif" => "image/tiff",
            ".heic" => "image/heic",
            ".mp3" => "audio/mpeg",
            ".wav" => "audio/wav",
            ".ogg" => "audio/ogg",
            ".m4a" => "audio/mp4",
            ".flac" => "audio/flac",
            ".mp4" => "video/mp4",
            ".webm" => "video/webm",
            ".mov" => "video/quicktime",
            ".pdf" => "application/pdf",
            ".txt" or ".md" or ".log" => "text/plain",
            ".json" => "application/json",
            ".xml" => "application/xml",
            ".html" or ".htm" => "text/html",
            ".csv" => "text/csv",
            _ => "application/octet-stream",
        };
    }
}