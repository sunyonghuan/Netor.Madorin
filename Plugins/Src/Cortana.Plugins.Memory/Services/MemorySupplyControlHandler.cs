using System.Security.Cryptography;
using System.Text;
using Cortana.Plugins.Memory.Models;

namespace Cortana.Plugins.Memory.Services;

/// <summary>
/// 处理宿主长期记忆上下文供应控制面请求。
/// </summary>
public sealed class MemorySupplyControlHandler(IMemorySupplyService supplyService)
{
    /// <summary>
    /// 处理长期记忆上下文供应请求。
    /// </summary>
    public MemoryContextSupplyPackage Handle(MemoryContextSupplyRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);

        if (string.IsNullOrWhiteSpace(request.AgentId))
        {
            throw new ArgumentException("agentId 不能为空。", nameof(request));
        }

        var result = supplyService.Supply(new MemorySupplyRequest
        {
            RequestId = request.RequestId,
            AgentId = request.AgentId,
            WorkspaceId = NormalizeWorkspaceId(request.WorkspaceId) ?? NormalizeWorkspaceId(request.WorkspaceDirectory),
            Scenario = request.Scenario,
            CurrentTask = request.CurrentTask,
            SessionTitle = request.SessionTitle,
            RecentMessages = request.RecentMessages
                .Where(static message => !string.IsNullOrWhiteSpace(message.Content))
                .Select(static message => string.IsNullOrWhiteSpace(message.Role)
                    ? message.Content.Trim()
                    : $"{message.Role.Trim()}: {message.Content.Trim()}")
                .TakeLast(20)
                .ToList(),
            TriggerSource = Normalize(request.TriggerSource) ?? "memory.supply.request",
            MaxMemoryCount = request.MaxMemoryCount,
            MaxTokenBudget = request.MaxTokenBudget,
            TraceId = request.TraceId
        });

        return new MemoryContextSupplyPackage
        {
            RequestId = result.RequestId,
            Enabled = result.Enabled,
            Summary = result.Summary ?? string.Empty,
            Confidence = result.Confidence,
            Groups = result.Groups.Select(ToProtocolGroup).ToList(),
            Items = result.Items.Select(ToProtocolItem).ToList(),
            Budget = new MemoryContextSupplyBudget
            {
                MaxMemoryCount = result.Budget.MaxMemoryCount,
                UsedMemoryCount = result.Budget.UsedMemoryCount,
                MaxTokenBudget = result.Budget.MaxTokenBudget ?? 0,
                EstimatedTokens = result.Budget.EstimatedTokens ?? 0
            },
            AppliedPolicy = new MemoryContextSupplyPolicy
            {
                SupplyEnabled = result.AppliedPolicy.SupplyEnabled,
                MaxMemoryCount = result.AppliedPolicy.MaxMemoryCount,
                RecallMinimumConfidence = result.AppliedPolicy.RecallMinimumConfidence,
                Ranking = result.AppliedPolicy.Ranking,
                Grouping = result.AppliedPolicy.Grouping
            },
            TraceId = result.TraceId,
            ProducerVersion = MemoryContextSupplyProtocol.Version
        };
    }

    private static MemoryContextSupplyGroup ToProtocolGroup(MemorySupplyGroup group)
    {
        return new MemoryContextSupplyGroup
        {
            GroupKey = group.GroupKey,
            Title = group.Title,
            Items = group.Items.Select(ToProtocolItem).ToList(),
            Priority = group.Priority
        };
    }

    private static MemoryContextSupplyItem ToProtocolItem(MemorySupplyItem item)
    {
        return new MemoryContextSupplyItem
        {
            Id = item.Id,
            Kind = item.Kind,
            Topic = item.Topic,
            Title = item.Title ?? string.Empty,
            Content = item.Content,
            Reason = item.Reason,
            Confidence = item.Confidence,
            Score = item.Score,
            SourceRecallScore = item.SourceRecallScore,
            LifecycleState = item.LifecycleState,
            ConfirmationState = item.ConfirmationState,
            UpdatedAt = item.UpdatedAt
        };
    }

    private static string? Normalize(string? value)
    {
        return string.IsNullOrWhiteSpace(value) ? null : value.Trim();
    }

    private static string? NormalizeWorkspaceId(string? value)
    {
        var normalized = Normalize(value);
        if (normalized is null) return null;
        if (IsHexMd5(normalized)) return normalized.ToLowerInvariant();

        var hash = MD5.HashData(Encoding.UTF8.GetBytes(normalized));
        return Convert.ToHexString(hash).ToLowerInvariant();
    }

    private static bool IsHexMd5(string value)
    {
        return value.Length == 32 && value.All(static c =>
            c is >= '0' and <= '9' or >= 'a' and <= 'f' or >= 'A' and <= 'F');
    }
}
