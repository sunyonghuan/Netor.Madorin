using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;

namespace Netor.Cortana.AI.Orchestration;

/// <summary>
/// Chat 模式默认编排器。
/// 当前阶段仅把“编排模式解析 + 结果快照”从调用方中收口，
/// 真正的 Handoff / Workflow 执行留给后续阶段扩展。
/// </summary>
public sealed class AgentOrchestrator(
    ChatAgentResolver agentResolver,
    AgentOrchestratorOptions options,
    ILogger<AgentOrchestrator> logger) : IAgentOrchestrator
{
    private AgentOrchestrationResult? _lastResult;

    public Task<AgentOrchestrationResult> BuildAsync(
        AgentOrchestrationRequest request,
        IReadOnlyList<AIFunction>? additionalTools = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        cancellationToken.ThrowIfCancellationRequested();

        var warnings = new List<string>();
        var effectiveMode = ResolveEffectiveMode(request, warnings);
        var agent = agentResolver.BuildAgentForTurn(request, additionalTools)
            ?? throw new InvalidOperationException("无法根据当前默认配置构建 Agent。");

        _lastResult = new AgentOrchestrationResult(
            agent,
            effectiveMode,
            ResolveUsedAgentIds(request, effectiveMode),
            warnings,
            []);

        if (warnings.Count > 0)
        {
            logger.LogInformation(
                "Chat 编排已回退到 {Mode}。Warnings: {Warnings}",
                effectiveMode,
                string.Join(" | ", warnings));
        }

        return Task.FromResult(_lastResult);
    }

    public AgentOrchestrationResult? GetLastResult()
    {
        return _lastResult;
    }

    private AgentOrchestrationMode ResolveEffectiveMode(
        AgentOrchestrationRequest request,
        List<string> warnings)
    {
        if (request.Mode != AgentOrchestrationMode.HandoffChat)
        {
            return request.Mode;
        }

        warnings.Add(
            $"当前版本尚未启用 HandoffChat，已回退到现有 Chat 编排路径（MaxRounds={options.MaxRounds}, MaxSubTasks={options.MaxSubTasks}）。");

        return request.Mentions.Count >= 2
            ? AgentOrchestrationMode.ToolDelegation
            : AgentOrchestrationMode.None;
    }

    private static IReadOnlyList<string> ResolveUsedAgentIds(
        AgentOrchestrationRequest request,
        AgentOrchestrationMode effectiveMode)
    {
        if (effectiveMode == AgentOrchestrationMode.None)
        {
            return [];
        }

        return [.. request.Mentions
            .Select(static mention => mention.Agent.Id)
            .Where(static id => !string.IsNullOrWhiteSpace(id))
            .Distinct(StringComparer.Ordinal)];
    }
}
