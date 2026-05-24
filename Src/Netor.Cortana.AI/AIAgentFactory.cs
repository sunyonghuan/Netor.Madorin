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
using Netor.EventHub;

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

    /// <summary>
    /// 阶段 6 Phase 1：为 sub-agent / Workflow 参与者构建轻量级 <see cref="TokenTrackingChatClient"/>。
    /// 与主 <see cref="CreateTrackingClient"/> 的区别：
    /// <list type="bullet">
    /// <item><b>不</b>写主对话的 <c>_lastInputTokens</c> / <c>_maxContextTokens</c>（避免子 agent 用量覆盖主 Chat 顶栏进度条）；</item>
    /// <item>仍然触发 <see cref="TokenUsageChanged"/> 让 UI 知道 token 在动（决策 6-1-C：复用现有事件，不引入新事件类型）；</item>
    /// <item>tracker 本身仍提供 <see cref="TokenTrackingChatClient.LastInputTokens"/> / <see cref="TokenTrackingChatClient.TotalOutputTokens"/>，
    /// 供 <see cref="TaskEngine.TaskExecutionEngine"/> 在 step 完成时取数。</item>
    /// </list>
    /// 详见 docs/未来版本策划/多智能体编排模式策划/04-实施阶段.md §阶段 6 #2。
    /// </summary>
    private TokenTrackingChatClient CreateSubAgentTrackingClient(IChatClient inner, long maxContextTokens, bool enableReasoning)
    {
        var normalizedMax = maxContextTokens <= 0 ? 128_000 : maxContextTokens;
        return new TokenTrackingChatClient(inner, normalizedMax, _ =>
        {
            try { TokenUsageChanged?.Invoke(); }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "TokenUsageChanged 事件订阅者抛出异常（sub-agent）");
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
    /// 阶段 3B：为 Workflow 模式（GroupChat / Magentic / ParallelAnalysis 等）构建参与者集合。
    /// 所有参与者都走轻量 <see cref="BuildSubAgent"/> 路径（不挂 ChatHistoryDataProvider / Skills），
    /// 因为 Workflow 模式有独立的 OrchestrationMessage 持久化路径，不复用 ChatMessages。
    /// 详见：docs/未来版本策划/多智能体编排模式策划/04-实施阶段.md §阶段 3B / §阶段 4B。
    /// </summary>
    /// <param name="participantEntities">参与者智能体实体列表。重复 ID 会被自动跳过。</param>
    /// <param name="fallbackProvider">兜底厂商；当参与者自身未配置 DefaultProviderId 时跟随此值。</param>
    /// <param name="fallbackModel">兜底模型；当参与者自身未配置 DefaultModelId 时跟随此值。</param>
    /// <param name="providerService">用于解析参与者自身厂商。</param>
    /// <param name="modelService">用于解析参与者自身模型。</param>
    /// <param name="trackerByAgentId">
    /// 阶段 6 Phase 1 新增：可选输出字典；非 null 时按 agent.Id 填充对应参与者的 <see cref="TokenTrackingChatClient"/>。
    /// Workflow 端在 step 完成时据此字典反查 tracker 取 token 数据。
    /// 调用前由调用方初始化（如 <c>new Dictionary&lt;string, TokenTrackingChatClient&gt;()</c>），方法仅写入。
    /// 详见 docs/未来版本策划/多智能体编排模式策划/04-实施阶段.md §阶段 6 #2。
    /// </param>
    /// <param name="taskBlacklist">
    /// 阶段 6 Phase 2 新增：任务级工具黑名单（"pluginId:toolName" 格式），决策 6-2-A 黑名单 + 6-2-B 粒度。
    /// 所有参与者共享同一份黑名单，本次任务对全员等效收窄高风险工具。
    /// 详见 docs/未来版本策划/多智能体编排模式策划/04-实施阶段.md §阶段 6 #1。
    /// </param>
    /// <param name="taskId">P2-2：任务 ID（非空时为 Manager 注入动态子智能体能力）。</param>
    /// <param name="maxSubAgents">P2-2：Manager 最多创建几个动态子智能体（默认 5，与 WorkflowInputVm.MaxSubAgents 一致）。</param>
    /// <param name="overrideProviderId">
    /// P2-2 修复 2026-05-17：用户在 WorkflowDetailView UI 输入框下方明确选择的 Provider ID，
    /// 决策 D-甲：仅作用于 Manager（participants[0]）+ 动态子智能体，不影响 GroupChat Members。
    /// 解决场景：Manager Agent 数据库里的 DefaultProviderId 指向已删除/失效的厂商。
    /// </param>
    /// <param name="overrideModelId">同上，用户 UI 选的 Model ID。</param>
    /// <returns>参与者 AIAgent 集合，按入参顺序保留；解析失败的参与者会被过滤掉。</returns>
    public IReadOnlyList<AIAgent> BuildWorkflowParticipants(
        IReadOnlyList<AgentEntity> participantEntities,
        AiProviderEntity fallbackProvider,
        AiModelEntity fallbackModel,
        AiProviderService providerService,
        AiModelService modelService,
        IDictionary<string, TokenTrackingChatClient>? trackerByAgentId = null,
        IReadOnlyCollection<string>? taskBlacklist = null,
        string? taskId = null,
        int maxSubAgents = 5,
        string? overrideProviderId = null,
        string? overrideModelId = null)
    {
        ArgumentNullException.ThrowIfNull(participantEntities);
        ArgumentNullException.ThrowIfNull(fallbackProvider);
        ArgumentNullException.ThrowIfNull(fallbackModel);
        ArgumentNullException.ThrowIfNull(providerService);
        ArgumentNullException.ThrowIfNull(modelService);

        var participants = new List<AIAgent>(participantEntities.Count);
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var participantEntity in participantEntities)
        {
            if (!seen.Add(participantEntity.Id))
            {
                logger.LogInformation("Workflow 跳过重复参与者：{Name}", participantEntity.Name);
                continue;
            }

            // P2-2：第一个参与者按约定（LoadAndBuildParticipants 维护）是 Manager。
            var isManager = participants.Count == 0;

            // P2-2 修复 2026-05-17：Manager 优先使用 override（用户 UI 选的 Provider/Model），
            // 决策 D-甲：仅 Manager + 动态子智能体（GroupChat Members 仍用各自 Agent.DefaultProviderId/ModelId）。
            // 详见 Docs/未来版本策划/聊天式任务发起与动态智能体/01-P2方案设计.md §2-D。
            AiProviderEntity? provider = null;
            AiModelEntity? model = null;
            if (isManager && !string.IsNullOrEmpty(overrideProviderId))
            {
                provider = providerService.GetById(overrideProviderId);
            }
            if (isManager && !string.IsNullOrEmpty(overrideModelId))
            {
                model = modelService.GetById(overrideModelId);
            }
            if (provider is not null && model is not null)
            {
                logger.LogInformation(
                    "Manager [{Name}] 使用用户 UI 指定的 Provider/Model（覆盖 Agent.DefaultXxxId）：{Provider}/{Model}",
                    participantEntity.Name, provider.Name, model.Name);
            }
            // 如果 override 没设或部分解析失败，回退到原有逻辑（Agent.DefaultXxxId > fallback）
            if (provider is null || model is null)
            {
                var resolved = ResolveSubAgentProviderAndModel(
                    participantEntity, fallbackProvider, fallbackModel, providerService, modelService);
                provider ??= resolved.Provider;
                model ??= resolved.Model;
            }

            if (provider is null || model is null)
            {
                logger.LogWarning(
                    "Workflow participant [{Name}] 厂商或模型无法解析，跳过",
                    participantEntity.Name);
                continue;
            }

            // 仅当 taskId 非空时为 Manager 启用动态子智能体能力（chat 模式 / 其他调用路径不传 taskId 即可向后兼容）。
            var isManagerForDynamicAgents = isManager && !string.IsNullOrEmpty(taskId);

            var participant = BuildSubAgent(
                participantEntity, provider, model, out var tracker, taskBlacklist,
                isManagerForDynamicAgents, taskId, maxSubAgents);
            participants.Add(participant);

            // 阶段 6 Phase 1：把 tracker 按 agent.Id 注册到字典，让 Workflow 在 step 完成时反查
            if (trackerByAgentId is not null && !string.IsNullOrEmpty(participantEntity.Id))
            {
                trackerByAgentId[participantEntity.Id] = tracker;
            }
        }

        return participants;
    }

    /// <summary>
    /// 构建子智能体（轻量）：仅携带 instructions + plugins + MCP，不带历史/memory/skills。
    /// </summary>
    private AIAgent BuildSubAgent(AgentEntity agent, AiProviderEntity provider, AiModelEntity model)
        => BuildSubAgent(agent, provider, model, out _);

    /// <summary>
    /// 阶段 6 Phase 1：构建子智能体（轻量），并通过 out 参数回传其 <see cref="TokenTrackingChatClient"/>，
    /// 让调用方（如 Workflow）在 step 完成时取 token 数据持久化到 OrchestrationStep。
    /// 阶段 6 Phase 2 新增：可选 taskBlacklist 参数，按 "pluginId:toolName" 在工具组装阶段过滤掉本次任务屏蔽的高风险工具。
    /// P2-2 新增：当 <paramref name="isManager"/> 为 true 时注入动态子智能体能力（P4 已迁移到 TaskEngine.OrchestratorAgent）。
    /// 详见 docs/未来版本策划/多智能体编排模式策划/04-实施阶段.md §阶段 6 #1 #2 +
    /// docs/未来版本策划/聊天式任务发起与动态智能体/01-P2方案设计.md §2-B/C。
    /// </summary>
    private AIAgent BuildSubAgent(
        AgentEntity agent,
        AiProviderEntity provider,
        AiModelEntity model,
        out TokenTrackingChatClient tracker,
        IReadOnlyCollection<string>? taskBlacklist = null,
        bool isManager = false,
        string? taskId = null,
        int maxSubAgents = 5)
    {
        var driver = driverRegistry.Resolve(provider);

        var providers = new List<AIContextProvider>();
        var registeredTools = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (model.InteractionCapabilities.HasFlag(InteractionCapabilities.FunctionCall))
        {
            AssembleToolProviders(agent, providers, registeredTools, taskBlacklist);

            // P4：动态子智能体能力已迁移到 TaskEngine.OrchestratorAgent（由编排器自主创建子智能体）。
            // 老 P2 的 DynamicAgentToolsProvider / CreateSubAgentTool / DynamicAgentCreationGate 已移除。
        }
        else
        {
            logger.LogInformation("子智能体模型 {Model} 未启用函数调用，跳过工具配送。", model.Name);
        }

        var enableReasoning = model.InteractionCapabilities.HasFlag(InteractionCapabilities.Reasoning);
        // 阶段 6 Phase 1：sub-agent ChatClient 必须包装 TokenTrackingChatClient，
        // 让 Workflow step 完成时能取到真实 token 数据（之前直接 driver.CreateChatClient 没有 tracking）。
        tracker = CreateSubAgentTrackingClient(driver.CreateChatClient(provider, model), model.ContextLength, enableReasoning);

#pragma warning disable MAAI001
        return tracker
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
    /// 阶段 6 Phase 2 新增：可选 taskBlacklist 参数，按 "pluginId:toolName" 过滤掉对应函数（决策 6-2-A 黑名单 + 6-2-B 粒度）。
    /// </summary>
    private void AssembleToolProviders(
        AgentEntity agent,
        List<AIContextProvider> providers,
        Dictionary<string, string> registeredTools,
        IReadOnlyCollection<string>? taskBlacklist = null)
    {
        // 阶段 6 Phase 2：把 taskBlacklist 规范化成大小写不敏感的 HashSet，传给下游 Add*Provider 做按工具过滤。
        // 空集合视为 null（无过滤），避免后续每次 Contains 调用都先判 null 再判 Count。
        HashSet<string>? blacklistSet = null;
        if (taskBlacklist is { Count: > 0 })
        {
            blacklistSet = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var item in taskBlacklist)
            {
                if (!string.IsNullOrWhiteSpace(item))
                    blacklistSet.Add(item.Trim());
            }
            if (blacklistSet.Count == 0) blacklistSet = null;
        }

        var globalPluginService = services.GetService<GlobalPluginService>();
        var globalPluginIds = globalPluginService?.GetEnabledPluginIds() ?? [];
        var injectedPluginIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        // 阶段 6 Phase 2 helper：plugin 整体级屏蔽判定（黑名单元素如果就是 plugin.Id，跳过整个 plugin 不注入）。
        // 决策 6-2-B 扩展：支持两种粒度同时存在
        //   1) "pluginId" 整体屏蔽（用于 UI 列表的简化模式：用户勾选屏蔽某高风险插件）
        //   2) "pluginId:toolName" 工具级屏蔽（保留细粒度能力）
        bool IsPluginEntirelyBlacklisted(string pluginId)
            => blacklistSet is not null && blacklistSet.Contains(pluginId);

        // 合并全局插件 Provider。仅全局插件目录中的插件允许生效。
        foreach (var pluginInfo in pluginLoader.GetLoadedPluginInfos())
        {
            var plugin = pluginInfo.Plugin;
            if (pluginInfo.Scope != PluginInstallScope.Global) continue;
            if (!globalPluginIds.Contains(plugin.Id, StringComparer.OrdinalIgnoreCase)) continue;
            if (IsPluginEntirelyBlacklisted(plugin.Id))
            {
                logger.LogInformation("任务级黑名单：跳过整个全局插件 [{PluginName}]({PluginId})", plugin.Name, plugin.Id);
                continue;
            }

            AddPluginProvider(plugin, providers, registeredTools, blacklistSet);
            injectedPluginIds.Add(plugin.Id);
        }

        // 合并该智能体已启用的插件 Provider
        var enabledIds = agent.EnabledPluginIds;

        foreach (var plugin in pluginLoader.GetActivePlugins())
        {
            if (!enabledIds.Contains(plugin.Id)) continue;
            if (injectedPluginIds.Contains(plugin.Id)) continue;
            if (IsPluginEntirelyBlacklisted(plugin.Id))
            {
                logger.LogInformation("任务级黑名单：跳过整个智能体级插件 [{PluginName}]({PluginId})", plugin.Name, plugin.Id);
                continue;
            }

            AddPluginProvider(plugin, providers, registeredTools, blacklistSet);
        }

        // 合并该智能体已启用的 MCP Server Provider
        var enabledMcpIds = agent.EnabledMcpServerIds;

        foreach (var mcpHost in pluginLoader.GetActiveMcpServers())
        {
            if (!enabledMcpIds.Contains(mcpHost.Id)) continue;
            if (IsPluginEntirelyBlacklisted(mcpHost.Id))
            {
                logger.LogInformation("任务级黑名单：跳过整个 MCP 服务器 [{McpName}]({McpId})", mcpHost.Name, mcpHost.Id);
                continue;
            }

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

                // 阶段 6 Phase 2：任务级黑名单过滤（按 mcpHostId:toolName 匹配）
                if (blacklistSet is not null && blacklistSet.Contains($"{mcpHost.Id}:{toolName}"))
                {
                    logger.LogInformation(
                        "任务级黑名单过滤：MCP 工具 '{ToolName}'（来源 [{McpName}]({McpId})）被本次任务屏蔽",
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
        Dictionary<string, string> registeredTools,
        HashSet<string>? blacklistSet = null)
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

            // 阶段 6 Phase 2：任务级黑名单过滤（按 pluginId:toolName 匹配，决策 6-2-B 粒度）
            if (blacklistSet is not null && blacklistSet.Contains($"{plugin.Id}:{toolName}"))
            {
                logger.LogInformation(
                    "任务级黑名单过滤：插件工具 '{ToolName}'（来源 [{PluginName}]({PluginId} v{Version})）被本次任务屏蔽",
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

    // ==================================================================================
    // P2-2：动态子智能体支持（Manager 通过 create_subagent 工具创建）
    // 详见 Docs/未来版本策划/聊天式任务发起与动态智能体/01-P2方案设计.md §2-B/C。
    // ==================================================================================

    /// <summary>
    /// P2-2：返回所有 plugin / MCP 中已注册的工具名集合，供 <c>CreateSubAgentTool</c> 校验 <c>requiredTools</c> 拼写。
    /// </summary>
    /// <remarks>
    /// 不区分 plugin 是全局还是 Agent 启用 —— 校验的是工具拼写是否真实存在（白名单准入由 BuildDynamicSubAgent 控制）。
    /// </remarks>
    public IReadOnlyCollection<string> GetAvailableToolNames()
    {
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var plugin in pluginLoader.GetActivePlugins())
        {
            foreach (var tool in plugin.Tools)
            {
                names.Add(tool.Name);
            }
        }

        foreach (var mcp in pluginLoader.GetActiveMcpServers())
        {
            foreach (var tool in mcp.Tools)
            {
                names.Add(tool.Name);
            }
        }

        return names;
    }

    /// <summary>
    /// P2-2：构建动态子智能体（瞬态，无 <c>AgentEntity</c> 表记录）。
    /// </summary>
    /// <param name="name">子智能体名称（CreateSubAgentTool 已校验合法性 + 唯一性）。</param>
    /// <param name="instructions">系统提示词。</param>
    /// <param name="provider">复用 Manager 的厂商。</param>
    /// <param name="model">复用 Manager 的模型。</param>
    /// <param name="requiredTools">工具白名单（仅注入这些工具，与 Manager 启用列表无关）。</param>
    public AIAgent BuildDynamicSubAgent(
        string name,
        string instructions,
        AiProviderEntity provider,
        AiModelEntity model,
        IReadOnlyList<string>? requiredTools)
    {
        ArgumentException.ThrowIfNullOrEmpty(name);
        ArgumentNullException.ThrowIfNull(provider);
        ArgumentNullException.ThrowIfNull(model);

        var driver = driverRegistry.Resolve(provider);

        // 构造临时 AgentEntity 仅用于 driver.BuildChatOptions（不入库，仅承载 Temperature / TopP 等参数）
        var transientAgent = new AgentEntity
        {
            Id = $"dynamic_{Guid.NewGuid():N}"[..16],
            Name = name,
            Description = $"动态子智能体 {name}",
            Instructions = instructions,
            DefaultProviderId = provider.Id,
            DefaultModelId = model.Id,
        };

        var providers = new List<AIContextProvider>();
        if (model.InteractionCapabilities.HasFlag(InteractionCapabilities.FunctionCall) &&
            requiredTools is { Count: > 0 })
        {
            AssembleDynamicToolProviders(requiredTools, providers);
        }
        else if (requiredTools is { Count: > 0 })
        {
            logger.LogInformation(
                "动态子智能体 [{Name}] 的模型 {Model} 不支持函数调用，requiredTools 被忽略",
                name, model.Name);
        }

        var enableReasoning = model.InteractionCapabilities.HasFlag(InteractionCapabilities.Reasoning);
        // 复用 sub-agent tracking 模式（事件累计，不污染主 Chat 的进度条）
        var trackingClient = CreateSubAgentTrackingClient(
            driver.CreateChatClient(provider, model), model.ContextLength, enableReasoning);

#pragma warning disable MAAI001
        return trackingClient
            .AsBuilder()
            .BuildAIAgent(new ChatClientAgentOptions
            {
                Id = transientAgent.Id,
                Name = transientAgent.Name,
                Description = transientAgent.Description,
                AIContextProviders = providers,
                ChatOptions = driver.BuildChatOptions(provider, transientAgent),
            })
            .AsBuilder()
            .Build();
#pragma warning restore MAAI001
    }

    /// <summary>
    /// P2-2：按 <paramref name="requiredTools"/> 白名单组装 Provider 列表（plugin/MCP）。
    /// 简化版的 <see cref="AssembleToolProviders"/>：
    /// - 不考虑 Agent 的 EnabledPluginIds / EnabledMcpServerIds（动态子智能体没有持久化 entity）
    /// - 不考虑任务级黑名单（动态子智能体的 requiredTools 已经是受信白名单）
    /// - 把不在 requiredTools 内的工具加入 exclude 列表（让 Plugin/MCP Provider 自动过滤）
    /// </summary>
    private void AssembleDynamicToolProviders(
        IReadOnlyList<string> requiredTools,
        List<AIContextProvider> providers)
    {
        var requiredSet = new HashSet<string>(requiredTools, StringComparer.OrdinalIgnoreCase);

        // 1. 遍历 plugin，构造 excluded 集合（不在白名单内的所有工具名）+ 注入 PluginContextProvider
        foreach (var plugin in pluginLoader.GetActivePlugins())
        {
            var excluded = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var hasMatch = false;
            foreach (var tool in plugin.Tools)
            {
                if (requiredSet.Contains(tool.Name))
                {
                    hasMatch = true;
                }
                else
                {
                    excluded.Add(tool.Name);
                }
            }

            if (hasMatch)
            {
                providers.Add(new Plugin.PluginContextProvider(plugin, excluded));
                logger.LogInformation(
                    "动态子智能体注入插件 [{Plugin}]，启用 {Enabled} 个工具",
                    plugin.Name, plugin.Tools.Count - excluded.Count);
            }
        }

        // 2. 遍历 MCP 服务器，同样按白名单过滤
        foreach (var mcpHost in pluginLoader.GetActiveMcpServers())
        {
            var excluded = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var hasMatch = false;
            foreach (var tool in mcpHost.Tools)
            {
                if (requiredSet.Contains(tool.Name))
                {
                    hasMatch = true;
                }
                else
                {
                    excluded.Add(tool.Name);
                }
            }

            if (hasMatch)
            {
                providers.Add(new Plugin.Mcp.McpContextProvider(mcpHost, excluded));
                logger.LogInformation(
                    "动态子智能体注入 MCP 服务器 [{Mcp}]，启用 {Enabled} 个工具",
                    mcpHost.Name, mcpHost.Tools.Count - excluded.Count);
            }
        }
    }
}