using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;

using Netor.Cortana.AI.Orchestration;
using Netor.Cortana.AI.Providers;
using Netor.Cortana.Entitys;
using Netor.Cortana.Entitys.Services;

namespace Netor.Cortana.AI;

/// <summary>
/// 专家模式当前默认 Provider / Agent / Model 的解析与 Agent 重建判定入口。
/// 阶段 2 先把默认配置字段和重建判定从 AiChatHostedService 中抽离，
/// 保持对外公共访问器和 Change* 方法签名不变。
/// </summary>
public sealed class ChatAgentResolver(
    AIAgentFactory factory,
    AiProviderService providerService,
    AgentService agentService,
    AiModelService modelService,
    ToolContextVersionService toolContextVersionService,
    ILogger<ChatAgentResolver> logger)
{
    private long _builtToolContextVersion = -1;
    private string _builtProviderId = string.Empty;
    private string _builtAgentId = string.Empty;
    private string _builtModelId = string.Empty;
    private AIAgent? _cachedAgent;

    /// <summary>当前默认 Provider。</summary>
    public AiProviderEntity? CurrentProvider { get; private set; }

    /// <summary>当前默认 Agent。</summary>
    public AgentEntity? CurrentAgent { get; private set; }

    /// <summary>当前默认 Model。</summary>
    public AiModelEntity? CurrentModel { get; private set; }

    /// <summary>
    /// 加载默认 Provider / Agent / Model。
    /// </summary>
    public void LoadDefaults()
    {
        var providers = providerService.GetAll();
        CurrentProvider = providers.FirstOrDefault(static p => p.IsDefault) ?? providers.FirstOrDefault();
        CurrentAgent = agentService.GetDefaultOrFirst();
        CurrentModel = ResolveDefaultModel(CurrentProvider?.Id);
        ResetBuiltState();
        _cachedAgent = null;
    }

    /// <summary>
    /// 更新下一轮默认 Provider。不会立刻清空当前 turn 正在使用的 Agent / Session。
    /// </summary>
    public void ChangeProvider(string providerId)
    {
        CurrentProvider = providerService.GetById(providerId);
        if (CurrentProvider is null)
        {
            CurrentModel = null;
            return;
        }

        if (CurrentModel is null || !string.Equals(CurrentModel.ProviderId, CurrentProvider.Id, StringComparison.Ordinal))
        {
            CurrentModel = ResolveDefaultModel(CurrentProvider.Id);
        }
    }

    /// <summary>
    /// 更新下一轮默认 Model。若模型归属于其它 Provider，会同步切换默认 Provider。
    /// </summary>
    public void ChangeModel(string modelId)
    {
        var model = modelService.GetById(modelId);
        if (model is null)
        {
            return;
        }

        CurrentModel = model;
        if (CurrentProvider is null || !string.Equals(CurrentProvider.Id, model.ProviderId, StringComparison.Ordinal))
        {
            CurrentProvider = providerService.GetById(model.ProviderId);
        }
    }

    /// <summary>
    /// 更新下一轮默认 Agent。
    /// </summary>
    public void ChangeAgent(string agentId)
    {
        CurrentAgent = agentService.GetByName(agentId);
    }

    public AIAgent? BuildAgentForTurn(
        IReadOnlyList<AgentMention> mentions,
        IReadOnlyList<AIFunction>? additionalTools = null)
    {
        _cachedAgent = BuildAgentForTurn(_cachedAgent, mentions, additionalTools);
        return _cachedAgent;
    }

    /// <summary>
    /// 为后续 <see cref="IAgentOrchestrator"/> 接入预留的薄转发入口。
    /// 当前仍复用 mentions 驱动的既有构建语义，不改变运行时行为。
    /// </summary>
    public AIAgent? BuildAgentForTurn(
        AgentOrchestrationRequest request,
        IReadOnlyList<AIFunction>? additionalTools = null)
    {
        ArgumentNullException.ThrowIfNull(request);
        return BuildAgentForTurn(request.Mentions, additionalTools);
    }

    /// <summary>
    /// 在当前默认选择下为新一轮对话解析要使用的 Agent。
    /// </summary>
    public AIAgent? BuildAgentForTurn(
        AIAgent? currentAgent,
        IReadOnlyList<AgentMention> mentions,
        IReadOnlyList<AIFunction>? additionalTools = null)
    {
        if (CurrentProvider is null || CurrentAgent is null || CurrentModel is null)
        {
            return null;
        }

        currentAgent = InvalidateAgentIfSelectionChanged(currentAgent);
        currentAgent = InvalidateAgentIfToolContextChanged(currentAgent);

        if (mentions.Count >= 2)
        {
            var rebuilt = factory.BuildWithSubAgents(
                CurrentAgent,
                CurrentProvider,
                CurrentModel,
                [.. mentions],
                providerService,
                modelService,
                additionalTools);
            MarkAgentBuilt();
            _cachedAgent = rebuilt;
            return rebuilt;
        }

        if (mentions.Count > 0 || currentAgent is null)
        {
            var rebuilt = factory.Build(CurrentAgent, CurrentProvider, CurrentModel, additionalTools);
            MarkAgentBuilt();
            _cachedAgent = rebuilt;
            return rebuilt;
        }

        _cachedAgent = currentAgent;
        return currentAgent;
    }

    /// <summary>
    /// 当工具上下文版本变化时使当前 Agent 失效，但不清空默认配置。
    /// </summary>
    public AIAgent? InvalidateAgentIfToolContextChanged(AIAgent? currentAgent)
    {
        var currentVersion = toolContextVersionService.Current;
        if (_builtToolContextVersion == currentVersion)
        {
            return currentAgent;
        }

        RefreshCurrentAgentSnapshot();
        if (currentAgent is not null)
        {
            logger.LogInformation(
                "工具上下文版本已从 {BuiltVersion} 更新到 {CurrentVersion}，Agent 已标记失效（Session 保留）",
                _builtToolContextVersion,
                currentVersion);
        }

        return null;
    }

    /// <summary>
    /// 主动使当前 Agent 缓存失效。
    /// </summary>
    public void InvalidateAgent(string source)
    {
        RefreshCurrentAgentSnapshot();
        ResetBuiltState();
        _cachedAgent = null;
        logger.LogInformation("{Source}已变更，Agent 已标记失效（Session 保留），下次对话自动重建", source);
    }

    /// <summary>
    /// 推进工具上下文版本，并使当前 Agent 缓存失效。
    /// </summary>
    public long BumpToolContextVersion(string source)
    {
        var version = toolContextVersionService.Bump();
        RefreshCurrentAgentSnapshot();
        ResetBuiltState();
        _cachedAgent = null;
        logger.LogInformation("{Source}已变更，工具上下文版本推进到 {Version}，Agent 已标记失效（Session 保留）", source, version);
        return version;
    }

    public void ClearCachedAgent()
    {
        _cachedAgent = null;
    }

    private AIAgent? InvalidateAgentIfSelectionChanged(AIAgent? currentAgent)
    {
        if (currentAgent is null)
        {
            return null;
        }

        var providerId = CurrentProvider?.Id ?? string.Empty;
        var agentId = CurrentAgent?.Id ?? string.Empty;
        var modelId = CurrentModel?.Id ?? string.Empty;

        if (string.Equals(_builtProviderId, providerId, StringComparison.Ordinal)
            && string.Equals(_builtAgentId, agentId, StringComparison.Ordinal)
            && string.Equals(_builtModelId, modelId, StringComparison.Ordinal))
        {
            return currentAgent;
        }

        logger.LogInformation(
            "默认配置已变化，Agent 将按下一轮默认值重建。Provider：{ProviderId}，Agent：{AgentId}，Model：{ModelId}",
            providerId,
            agentId,
            modelId);
        return null;
    }

    private void MarkAgentBuilt()
    {
        _builtToolContextVersion = toolContextVersionService.Current;
        _builtProviderId = CurrentProvider?.Id ?? string.Empty;
        _builtAgentId = CurrentAgent?.Id ?? string.Empty;
        _builtModelId = CurrentModel?.Id ?? string.Empty;
    }

    private void ResetBuiltState()
    {
        _builtToolContextVersion = -1;
        _builtProviderId = string.Empty;
        _builtAgentId = string.Empty;
        _builtModelId = string.Empty;
    }

    private void RefreshCurrentAgentSnapshot()
    {
        if (CurrentAgent is null)
        {
            return;
        }

        CurrentAgent = agentService.GetByName(CurrentAgent.Id) ?? CurrentAgent;
    }

    private AiModelEntity? ResolveDefaultModel(string? providerId)
    {
        if (string.IsNullOrWhiteSpace(providerId))
        {
            return null;
        }

        var models = modelService.GetByProviderId(providerId);
        return models.FirstOrDefault(static model => model.IsDefault) ?? models.FirstOrDefault();
    }
}
