using System.ComponentModel;
using System.Globalization;
using System.Text;
using System.Text.Json;

using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

using Netor.Cortana.AI.Drivers;
using Netor.Cortana.AI.Providers;
using Netor.Cortana.AI.WorkMode;
using Netor.Cortana.Entitys;
using Netor.Cortana.Entitys.Services;
using Netor.Madorin.Plugin;
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
    /// <param name="agent">智能体实体。</param>
    /// <param name="provider">AI 提供商实体。</param>
    /// <param name="model">AI 模型实体。</param>
    /// <param name="additionalTools">额外工具列表（如工作模式工具），默认 null。</param>
    public AIAgent Build(
        AgentEntity agent,
        AiProviderEntity provider,
        AiModelEntity? model,
        IReadOnlyList<AIFunction>? additionalTools = null,
        ToolFilterMode toolFilterMode = ToolFilterMode.Full,
        bool enableChatHistory = true,
        bool skipAgentBoundTools = false)
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

        // 注入额外工具（如工作模式工具）。None 模式用于内部决策 Agent，禁止暴露任何工具。
        if (toolFilterMode != ToolFilterMode.None && additionalTools is { Count: > 0 })
        {
#pragma warning disable MAAI001
            providers.Add(new WorkModeToolsContextProvider(additionalTools));
#pragma warning restore MAAI001
        }

        // 组装插件和 MCP 工具 Provider
        var registeredTools = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        RegisterAdditionalToolNames(additionalTools, registeredTools);
        if (model.InteractionCapabilities.HasFlag(InteractionCapabilities.FunctionCall))
        {
            if (toolFilterMode != ToolFilterMode.None)
            {
                AssembleToolProviders(agent, providers, registeredTools, skipAgentBoundTools: skipAgentBoundTools);
            }

            AddToolFilteringProvider(providers, toolFilterMode);
            AddProviderToolLimitProvider(providers, driver, model);
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
                ChatHistoryProvider = enableChatHistory
                    ? services.GetRequiredService<ChatHistoryDataProvider>()
                    : null,
            })
            .AsBuilder()
            .Build();
#pragma warning restore MAAI001
    }

    /// <summary>
    /// 构建带有子智能体工具的主 <see cref="AIAgent"/>。
    /// 每个被提及的子智能体通过 <c>AsAIFunction()</c> 包装为工具函数注入主 Agent。
    /// </summary>
    /// <param name="mainAgent">主智能体实体。</param>
    /// <param name="mainProvider">主 AI 提供商实体。</param>
    /// <param name="mainModel">主 AI 模型实体。</param>
    /// <param name="mentions">子智能体提及列表。</param>
    /// <param name="providerService">AI 提供商服务。</param>
    /// <param name="modelService">AI 模型服务。</param>
    /// <param name="additionalTools">额外工具列表（如工作模式工具），默认 null。</param>
    public AIAgent BuildWithSubAgents(
        AgentEntity mainAgent,
        AiProviderEntity mainProvider,
        AiModelEntity mainModel,
        List<AgentMention> mentions,
        AiProviderService providerService,
        AiModelService modelService,
        IReadOnlyList<AIFunction>? additionalTools = null,
        string? workModeTaskId = null,
        IPublisher? workModePublisher = null,
        WorkExecutionLogService? workModeLogService = null,
        ToolFilterMode toolFilterMode = ToolFilterMode.Full,
        bool enableChatHistory = true,
        bool skipAgentBoundTools = false)
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

        // 注入额外工具（如工作模式工具）
        if (additionalTools is { Count: > 0 })
        {
#pragma warning disable MAAI001
            providers.Add(new WorkModeToolsContextProvider(additionalTools));
#pragma warning restore MAAI001
        }

        // 组装主智能体的插件和 MCP 工具
        var registeredTools = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        RegisterAdditionalToolNames(additionalTools, registeredTools);
        var mainFuncEnabled = mainModel.InteractionCapabilities.HasFlag(InteractionCapabilities.FunctionCall);
        if (mainFuncEnabled)
        {
            AssembleToolProviders(mainAgent, providers, registeredTools, skipAgentBoundTools: skipAgentBoundTools);
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
                    [Description("附件绝对路径列表（可空）。用 JSON 数组字符串或逗号/换行分隔文本表示")] string? attachmentPaths,
                    [Description("主 Agent 对每个附件的简短描述（可空，长度应与 attachmentPaths 一致）。用 JSON 数组字符串或逗号/换行分隔文本表示")] string? attachmentDescriptions,
                    CancellationToken ct)
                {
                    var contents = new List<AIContent> { new TextContent(query) };
                    var parsedAttachmentPaths = ParseStringListArgument(attachmentPaths);
                    var parsedAttachmentDescriptions = ParseStringListArgument(attachmentDescriptions);

                    if (parsedAttachmentPaths is { Count: > 0 })
                    {
                        for (var i = 0; i < parsedAttachmentPaths.Count; i++)
                        {
                            var path = parsedAttachmentPaths[i];
                            if (string.IsNullOrWhiteSpace(path) || !File.Exists(path)) continue;

                            var description = parsedAttachmentDescriptions is { Count: > 0 } && i < parsedAttachmentDescriptions.Count
                                ? parsedAttachmentDescriptions[i]
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
                    if (!string.IsNullOrWhiteSpace(workModeTaskId) &&
                        workModePublisher is not null &&
                        workModeLogService is not null)
                    {
                        var callScope = $"{functionName}_{Guid.NewGuid():N}";
                        var streamProcessor = new WorkModeStreamProcessor(
                            workModeTaskId,
                            workModePublisher,
                            workModeLogService,
                            authorName: subAgentEntity.Name,
                            callIdPrefix: callScope);

                        await foreach (var chunk in subAgent.RunStreamingAsync([msg], cancellationToken: ct).ConfigureAwait(false))
                        {
                            if (chunk.Contents.Count > 0)
                            {
                                await streamProcessor.ProcessChunkAsync(chunk.Contents, ct).ConfigureAwait(false);
                            }
                        }

                        await streamProcessor.FlushAsync(ct).ConfigureAwait(false);
                        return streamProcessor.GetAccumulatedText();
                    }

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
                    functionName, subAgentEntity.Name, subAgentEntity.BoundPlugins.Count, subAgentEntity.BoundMcp.Count);
            }
        }

        // 通过 SubAgentContextProvider 注入子智能体工具
        if (subAgentFunctions.Count > 0)
        {
#pragma warning disable MAAI001
            providers.Add(new SubAgentContextProvider(subAgentFunctions));
#pragma warning restore MAAI001
        }

        AddToolFilteringProvider(providers, toolFilterMode);
        AddProviderToolLimitProvider(providers, driver, mainModel);

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
                ChatHistoryProvider = enableChatHistory
                    ? services.GetRequiredService<ChatHistoryDataProvider>()
                    : null,
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
    /// <param name="mainProvider">主厂商；specialist 跟随此值。</param>
    /// <param name="mainModel">主模型；specialist 跟随此值。</param>
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
    /// <param name="fallbackProvider">兜底厂商；参与者默认跟随此值。</param>
    /// <param name="fallbackModel">兜底模型；参与者默认跟随此值。</param>
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
    /// <param name="taskId">兼容旧调用方的任务 ID 参数；当前构建路径不再消费。</param>
    /// <param name="maxSubAgents">兼容旧调用方的动态子智能体数量参数；当前构建路径不再消费。</param>
    /// <param name="overrideProviderId">
    /// 用户在 WorkflowDetailView UI 输入框下方明确选择的 Provider ID，仅作用于 Manager（participants[0]）。
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

            // 第一个参与者按约定（LoadAndBuildParticipants 维护）是 Manager。
            var isManager = participants.Count == 0;

            // Manager 优先使用 override（用户 UI 选的 Provider/Model）。
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
                    "Manager [{Name}] 使用用户 UI 指定的 Provider/Model：{Provider}/{Model}",
                    participantEntity.Name, provider.Name, model.Name);
            }
            // 如果 override 没设或部分解析失败，回退到调用方传入的 Provider/Model。
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

            var participant = BuildSubAgent(
                participantEntity, provider, model, out var tracker, taskBlacklist);
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
    /// 构建工作模式 C 层专员：不带聊天历史，但携带文件 / PowerShell 等真实执行工具。
    /// </summary>
    internal AIAgent BuildWorkModeSubAgent(AgentEntity agent, AiProviderEntity provider, AiModelEntity model)
        => BuildSubAgent(agent, provider, model, out _, includeWorkModeExecutionProviders: true);

    internal AIAgent BuildDelegatedSubAgent(
        AgentEntity agent,
        AiProviderEntity provider,
        AiModelEntity model,
        IReadOnlyCollection<string> mountedToolNames)
        => BuildSubAgent(
            agent,
            provider,
            model,
            out _,
            includeWorkModeExecutionProviders: true,
            mountedToolNames: mountedToolNames,
            includeAllAvailableTools: true);

    private AIAgent BuildSubAgent(AgentEntity agent, AiProviderEntity provider, AiModelEntity model)
        => BuildSubAgent(agent, provider, model, out _);

    /// <summary>
    /// 阶段 6 Phase 1：构建子智能体（轻量），并通过 out 参数回传其 <see cref="TokenTrackingChatClient"/>，
    /// 让调用方（如 Workflow）在 step 完成时取 token 数据持久化到 OrchestrationStep。
    /// 阶段 6 Phase 2 新增：可选 taskBlacklist 参数，按 "pluginId:toolName" 在工具组装阶段过滤掉本次任务屏蔽的高风险工具。
    /// 详见 docs/未来版本策划/多智能体编排模式策划/04-实施阶段.md §阶段 6 #1 #2。
    /// </summary>
    private AIAgent BuildSubAgent(
        AgentEntity agent,
        AiProviderEntity provider,
        AiModelEntity model,
        out TokenTrackingChatClient tracker,
        IReadOnlyCollection<string>? taskBlacklist = null,
        bool includeWorkModeExecutionProviders = false,
        IReadOnlyCollection<string>? mountedToolNames = null,
        bool includeAllAvailableTools = false)
    {
        var driver = driverRegistry.Resolve(provider);

        var providers = new List<AIContextProvider>();
        if (includeWorkModeExecutionProviders)
        {
            providers.AddRange(builtInProviders.Where(IsWorkModeExecutionProvider));
        }

        var registeredTools = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (model.InteractionCapabilities.HasFlag(InteractionCapabilities.FunctionCall))
        {
            if (includeAllAvailableTools)
            {
                AssembleAllAvailableToolProviders(providers, registeredTools, mountedToolNames, taskBlacklist);
            }
            else
            {
                AssembleToolProviders(agent, providers, registeredTools, taskBlacklist);
            }

            if (mountedToolNames is not null)
            {
#pragma warning disable MAAI001
                providers.Add(new ToolMountFilteringContextProvider(mountedToolNames));
#pragma warning restore MAAI001
            }

            AddProviderToolLimitProvider(providers, driver, model);

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

    private static bool IsWorkModeExecutionProvider(AIContextProvider provider)
    {
        var type = provider.GetType();
        return string.Equals(type.Name, "FileBrowserProvider", StringComparison.Ordinal)
            || string.Equals(type.Name, "FileOperationProvider", StringComparison.Ordinal)
            || string.Equals(type.Name, "PowerShellProvider", StringComparison.Ordinal);
    }

    /// <summary>
    /// 解析子智能体的厂商和模型：文件版 Agent 不再保存 Provider/Model，统一跟随调用方传入的上下文。
    /// </summary>
    internal static (AiProviderEntity? Provider, AiModelEntity? Model) ResolveSubAgentProviderAndModel(
        AgentEntity subAgent,
        AiProviderEntity mainProvider,
        AiModelEntity mainModel,
        AiProviderService providerService,
        AiModelService modelService)
    {
        ArgumentNullException.ThrowIfNull(subAgent);
        ArgumentNullException.ThrowIfNull(mainProvider);
        ArgumentNullException.ThrowIfNull(mainModel);

        return (mainProvider, mainModel);
    }

    /// <summary>
    /// 组装智能体已启用的插件 Provider 和 MCP Server Provider 到 providers 列表。
    /// 阶段 6 Phase 2 新增：可选 taskBlacklist 参数，按 "pluginId:toolName" 过滤掉对应函数（决策 6-2-A 黑名单 + 6-2-B 粒度）。
    /// </summary>
    private void AssembleToolProviders(
        AgentEntity agent,
        List<AIContextProvider> providers,
        Dictionary<string, string> registeredTools,
        IReadOnlyCollection<string>? taskBlacklist = null,
        bool skipAgentBoundTools = false)
    {
        var blacklistSet = NormalizeTaskBlacklist(taskBlacklist);

        var globalPluginService = services.GetService<GlobalPluginService>();
        var globalPluginIds = globalPluginService?.GetEnabledPluginIds() ?? [];
        var injectedPluginIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        // 合并全局插件 Provider。仅全局插件目录中的插件允许生效。
        foreach (var pluginInfo in pluginLoader.GetLoadedPluginInfos())
        {
            var plugin = pluginInfo.Plugin;
            if (pluginInfo.Scope != PluginInstallScope.Global) continue;
            if (!globalPluginIds.Contains(plugin.Id, StringComparer.OrdinalIgnoreCase)) continue;
            if (IsToolProviderEntirelyBlacklisted(blacklistSet, plugin.Id))
            {
                logger.LogInformation("任务级黑名单：跳过整个全局插件 [{PluginName}]({PluginId})", plugin.Name, plugin.Id);
                continue;
            }

            AddPluginProvider(plugin, providers, registeredTools, blacklistSet);
            injectedPluginIds.Add(plugin.Id);
        }

        // 合并该智能体 manifest.bound_plugins 中绑定的插件 Provider。
        var (enabledIds, enabledMcpIds) = GetAgentBoundTools(agent, skipAgentBoundTools);

        foreach (var plugin in pluginLoader.GetActivePlugins())
        {
            if (!enabledIds.Contains(plugin.Id, StringComparer.OrdinalIgnoreCase)) continue;
            if (injectedPluginIds.Contains(plugin.Id)) continue;
            if (IsToolProviderEntirelyBlacklisted(blacklistSet, plugin.Id))
            {
                logger.LogInformation("任务级黑名单：跳过整个智能体级插件 [{PluginName}]({PluginId})", plugin.Name, plugin.Id);
                continue;
            }

            AddPluginProvider(plugin, providers, registeredTools, blacklistSet);
        }

        // 合并该智能体 manifest.bound_mcp 中绑定的 MCP Server Provider。
        foreach (var mcpHost in pluginLoader.GetActiveMcpServers())
        {
            if (!enabledMcpIds.Contains(mcpHost.Id, StringComparer.OrdinalIgnoreCase)) continue;
            if (IsToolProviderEntirelyBlacklisted(blacklistSet, mcpHost.Id))
            {
                logger.LogInformation("任务级黑名单：跳过整个 MCP 服务器 [{McpName}]({McpId})", mcpHost.Name, mcpHost.Id);
                continue;
            }

            AddMcpProvider(mcpHost, providers, registeredTools, blacklistSet);
        }
    }

    private void AssembleAllAvailableToolProviders(
        List<AIContextProvider> providers,
        Dictionary<string, string> registeredTools,
        IReadOnlyCollection<string>? mountedToolNames = null,
        IReadOnlyCollection<string>? taskBlacklist = null)
    {
        var blacklistSet = NormalizeTaskBlacklist(taskBlacklist);
        var mountedSet = NormalizeMountedToolNames(mountedToolNames);
        var injectedPluginIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var pluginInfo in pluginLoader.GetLoadedPluginInfos())
        {
            var plugin = pluginInfo.Plugin;
            if (!injectedPluginIds.Add(plugin.Id))
            {
                continue;
            }

            if (IsToolProviderEntirelyBlacklisted(blacklistSet, plugin.Id))
            {
                logger.LogInformation("任务级黑名单：跳过整个插件 [{PluginName}]({PluginId})", plugin.Name, plugin.Id);
                continue;
            }

            if (!ContainsMountedTool(plugin.Tools, mountedSet))
            {
                continue;
            }

            AddPluginProvider(plugin, providers, registeredTools, blacklistSet);
        }

        foreach (var mcpHost in pluginLoader.GetActiveMcpServers())
        {
            if (IsToolProviderEntirelyBlacklisted(blacklistSet, mcpHost.Id))
            {
                logger.LogInformation("任务级黑名单：跳过整个 MCP 服务器 [{McpName}]({McpId})", mcpHost.Name, mcpHost.Id);
                continue;
            }

            if (!ContainsMountedTool(mcpHost.Tools, mountedSet))
            {
                continue;
            }

            AddMcpProvider(mcpHost, providers, registeredTools, blacklistSet);
        }
    }

    private static HashSet<string>? NormalizeTaskBlacklist(IReadOnlyCollection<string>? taskBlacklist)
    {
        if (taskBlacklist is not { Count: > 0 })
        {
            return null;
        }

        var blacklistSet = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var item in taskBlacklist)
        {
            if (!string.IsNullOrWhiteSpace(item))
            {
                blacklistSet.Add(item.Trim());
            }
        }

        return blacklistSet.Count > 0 ? blacklistSet : null;
    }

    private static bool IsToolProviderEntirelyBlacklisted(HashSet<string>? blacklistSet, string providerId)
        => blacklistSet is not null && blacklistSet.Contains(providerId);

    private static HashSet<string>? NormalizeMountedToolNames(IReadOnlyCollection<string>? mountedToolNames)
    {
        if (mountedToolNames is null)
        {
            return null;
        }

        var mountedSet = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var toolName in mountedToolNames)
        {
            if (!string.IsNullOrWhiteSpace(toolName))
            {
                mountedSet.Add(toolName.Trim());
            }
        }

        return mountedSet;
    }

    private static bool ContainsMountedTool(IEnumerable<AITool> tools, HashSet<string>? mountedToolNames)
    {
        if (mountedToolNames is null)
        {
            return true;
        }

        foreach (var tool in tools)
        {
            if (!string.IsNullOrWhiteSpace(tool.Name) && mountedToolNames.Contains(tool.Name))
            {
                return true;
            }
        }

        return false;
    }

    private static void AddToolFilteringProvider(List<AIContextProvider> providers, ToolFilterMode mode)
    {
        if (mode != ToolFilterMode.Full)
        {
            providers.Add(new ToolFilteringContextProvider(mode));
        }
    }

    private void AddProviderToolLimitProvider(
        List<AIContextProvider> providers,
        IAiProviderDriver driver,
        AiModelEntity model)
    {
        var maxTools = ResolveMaxTools(driver.Definition);
        if (maxTools <= 0)
        {
            return;
        }

#pragma warning disable MAAI001
        providers.Add(new ProviderToolLimitContextProvider(
            driver.Definition.DisplayName,
            model.Name,
            maxTools,
            services.GetService<IPublisher>(),
            logger));
#pragma warning restore MAAI001
    }

    private int ResolveMaxTools(AiProviderDriverDefinition definition)
    {
        var envName = BuildProviderMaxToolsEnvironmentName(definition.Id);
        var envValue = Environment.GetEnvironmentVariable(envName);
        if (TryParseMaxTools(envValue, out var envMaxTools))
        {
            return envMaxTools;
        }

        var settingKey = BuildProviderMaxToolsSettingKey(definition.Id);
        var systemSettings = services.GetService<SystemSettingsService>();
        var defaultMaxTools = definition.DefaultMaxTools ?? 0;
        return systemSettings?.GetValue(settingKey, defaultMaxTools) ?? defaultMaxTools;
    }

    internal static string BuildProviderMaxToolsSettingKey(string providerId)
        => $"AI.Provider.{providerId}.MaxTools";

    internal static string BuildProviderMaxToolsEnvironmentName(string providerId)
    {
        var builder = new StringBuilder("CORTANA_");
        foreach (var c in providerId)
        {
            if (char.IsAsciiLetterOrDigit(c))
            {
                builder.Append(char.ToUpperInvariant(c));
            }
            else
            {
                builder.Append('_');
            }
        }

        builder.Append("_MAX_TOOLS");
        return builder.ToString();
    }

    private static bool TryParseMaxTools(string? value, out int maxTools)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            maxTools = 0;
            return false;
        }

        return int.TryParse(
            value.Trim(),
            NumberStyles.Integer,
            CultureInfo.InvariantCulture,
            out maxTools);
    }

    internal static void RegisterAdditionalToolNames(
        IReadOnlyList<AIFunction>? additionalTools,
        Dictionary<string, string> registeredTools)
    {
        ArgumentNullException.ThrowIfNull(registeredTools);
        if (additionalTools is null)
        {
            return;
        }

        foreach (var tool in additionalTools)
        {
            if (string.IsNullOrWhiteSpace(tool.Name))
            {
                continue;
            }

            registeredTools.TryAdd(tool.Name, $"额外工具 {tool.Name}");
        }
    }

    private (IReadOnlyList<string> Plugins, IReadOnlyList<string> McpServers) GetAgentBoundTools(
        AgentEntity agent,
        bool skipFileManifest = false)
    {
        if (skipFileManifest)
        {
            return (agent.BoundPlugins, agent.BoundMcp);
        }

        var fileService = services.GetService<AgentFileService>();
        var record = !string.IsNullOrWhiteSpace(agent.Id) ? fileService?.GetByName(agent.Id) : null;

        return record is null
            ? (agent.BoundPlugins, agent.BoundMcp)
            : (record.Manifest.BoundPlugins, record.Manifest.BoundMcp);
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

    private void AddMcpProvider(
        McpServerHost mcpHost,
        List<AIContextProvider> providers,
        Dictionary<string, string> registeredTools,
        HashSet<string>? blacklistSet = null)
    {
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

    private static List<string>? ParseStringListArgument(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        var text = value.Trim();
        if (text.StartsWith("[", StringComparison.Ordinal))
        {
            try
            {
                using var document = JsonDocument.Parse(text);
                if (document.RootElement.ValueKind == JsonValueKind.Array)
                {
                    return document.RootElement
                        .EnumerateArray()
                        .Where(static item => item.ValueKind == JsonValueKind.String)
                        .Select(static item => item.GetString()?.Trim())
                        .Where(static item => !string.IsNullOrWhiteSpace(item))
                        .Select(static item => item!)
                        .ToList();
                }
            }
            catch (JsonException)
            {
                // 回退到分隔符解析。
            }
        }

        return text.Split([',', ';', '\n', '\r'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(static item => !string.IsNullOrWhiteSpace(item))
            .ToList();
    }
}
