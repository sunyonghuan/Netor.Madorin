using System.Text;
using Cortana.Plugins.Memory.Models;
using Cortana.Plugins.Memory.Storage;

namespace Cortana.Plugins.Memory.Services;

/// <summary>
/// 基于召回服务生成结构化主动记忆供应包。
/// </summary>
public sealed class MemorySupplyService(
    IMemoryRecallService recallService,
    IMemoryStore store,
    IMemorySettingsService settingsService) : IMemorySupplyService
{
    private const int MaximumToolMemoryCount = 50;
    private const string GlobalProfileGroupKey = "global-profile";
    private const string WorkspaceProfileGroupKey = "workspace-profile";
    private const string SessionContinuityGroupKey = "session-continuity";
    private const string TaskRelatedGroupKey = "task-related";

    public MemorySupplyResult Supply(MemorySupplyRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (string.IsNullOrWhiteSpace(request.AgentId)) throw new ArgumentException("智能体标识不能为空。", nameof(request));

        var supplyOptions = settingsService.GetSupplyOptions(request.AgentId, request.WorkspaceId);
        var recallOptions = settingsService.GetRecallOptions(request.AgentId, request.WorkspaceId);
        var maxMemoryCount = NormalizeLimit(request.MaxMemoryCount, supplyOptions.MaxMemoryCount);
        var estimatedTokens = 0;

        if (!supplyOptions.Enabled || maxMemoryCount <= 0)
        {
            return CreateDisabledResult(request, supplyOptions, recallOptions, maxMemoryCount);
        }

        var layeredItems = RecallLayeredSupplyItems(request, supplyOptions, recallOptions, maxMemoryCount);
        var items = layeredItems
            .GroupBy(static item => item.Item.Id, StringComparer.OrdinalIgnoreCase)
            .Select(static group => group
                .OrderBy(static item => item.Priority)
                .ThenByDescending(static item => item.Item.RecallScore)
                .First())
            .OrderBy(static item => item.Priority)
            .ThenByDescending(static item => item.Item.RecallScore)
            .Take(maxMemoryCount)
            .Select(static item => ToSupplyItem(item.Item, item.GroupKey))
            .ToList();

        if (request.MaxTokenBudget is > 0)
        {
            items = TakeWithinBudget(items, request.MaxTokenBudget.Value, out estimatedTokens);
        }
        else
        {
            estimatedTokens = items.Sum(static item => EstimateTokens(item.Content));
        }

        var groups = items
            .GroupBy(static item => GetGroupKey(item))
            .Select(static group => new MemorySupplyGroup
            {
                GroupKey = group.Key,
                Title = GetGroupTitle(group.Key),
                Priority = GetGroupPriority(group.Key),
                Items = group.OrderByDescending(static item => item.Score).ToList()
            })
            .OrderBy(static group => group.Priority)
            .ThenByDescending(static group => group.Items.Max(item => item.Score))
            .ToList();

        return new MemorySupplyResult
        {
            RequestId = request.RequestId,
            Enabled = true,
            Groups = groups,
            Items = items,
            Confidence = items.Count == 0 ? 0 : Math.Round(items.Average(static item => item.Confidence), 4),
            Summary = BuildSummary(items, groups),
            Budget = new MemorySupplyBudget
            {
                MaxMemoryCount = maxMemoryCount,
                UsedMemoryCount = items.Count,
                MaxTokenBudget = request.MaxTokenBudget,
                EstimatedTokens = estimatedTokens
            },
            AppliedPolicy = new MemorySupplyPolicy
            {
                SupplyEnabled = true,
                MaxMemoryCount = maxMemoryCount,
                RecallMinimumConfidence = recallOptions.MinimumConfidence,
                Ranking = "layered/global-profile/workspace-profile/session-continuity/task-related/recallScore",
                Grouping = "global-profile/workspace-profile/session-continuity/task-related"
            },
            TraceId = request.TraceId
        };
    }

    private IReadOnlyList<LayeredRecallItem> RecallLayeredSupplyItems(
        MemorySupplyRequest request,
        MemorySupplyOptions supplyOptions,
        MemoryRecallOptions recallOptions,
        int maxMemoryCount)
    {
        var result = new List<LayeredRecallItem>();
        var workspaceId = NormalizeOptional(request.WorkspaceId);

        if (supplyOptions.IncludeWorkspaceProfile && workspaceId is not null)
        {
            result.AddRange(store.GetProfileRecallCandidates(
                    request.AgentId,
                    workspaceId,
                    false,
                    recallOptions.MinimumConfidence,
                    recallOptions.IncludeCandidateMemories,
                    NormalizeLayerLimit(supplyOptions.WorkspaceProfileMaxCount, 5, maxMemoryCount))
                .Select(static item => new LayeredRecallItem(item, WorkspaceProfileGroupKey, 0)));
        }

        if (supplyOptions.IncludeGlobalProfile)
        {
            result.AddRange(store.GetProfileRecallCandidates(
                    request.AgentId,
                    null,
                    true,
                    recallOptions.MinimumConfidence,
                    recallOptions.IncludeCandidateMemories,
                    NormalizeLayerLimit(supplyOptions.GlobalProfileMaxCount, 3, maxMemoryCount))
                .Select(static item => new LayeredRecallItem(item, GlobalProfileGroupKey, 1)));
        }

        if (supplyOptions.IncludeSessionContinuity && workspaceId is not null)
        {
            result.AddRange(store.GetRecentRecallCandidates(
                    request.AgentId,
                    workspaceId,
                    recallOptions.MinimumConfidence,
                    recallOptions.IncludeCandidateMemories,
                    NormalizeLayerLimit(supplyOptions.SessionContinuityMaxCount, 2, maxMemoryCount))
                .Select(static item => new LayeredRecallItem(item, SessionContinuityGroupKey, 2)));
        }

        if (supplyOptions.IncludeTaskRecall)
        {
            var primaryQuery = BuildPrimaryQueryText(request);
            result.AddRange(RecallLayered(request, recallOptions, primaryQuery, NormalizeLayerLimit(supplyOptions.TaskRecallMaxCount, 5, maxMemoryCount))
                .Select(static item => new LayeredRecallItem(item, TaskRelatedGroupKey, 3)));

            var secondaryQuery = BuildSecondaryQueryText(request);
            if (!string.IsNullOrWhiteSpace(secondaryQuery) && !string.Equals(primaryQuery, secondaryQuery, StringComparison.Ordinal))
            {
                result.AddRange(RecallLayered(request, recallOptions, secondaryQuery, NormalizeLayerLimit(supplyOptions.TaskRecallMaxCount, 5, maxMemoryCount))
                    .Select(static item => new LayeredRecallItem(item, TaskRelatedGroupKey, 3)));
            }
        }

        store.RecordMemoryAccesses(result.Select(static item => item.Item), DateTimeOffset.UtcNow.ToString("O"));
        return result;
    }

    private IReadOnlyList<MemoryRecallItem> RecallLayered(
        MemorySupplyRequest request,
        MemoryRecallOptions recallOptions,
        string? queryText,
        int maxMemoryCount)
    {
        var workspaceId = NormalizeOptional(request.WorkspaceId);
        if (workspaceId is null)
        {
            return recallService.Recall(new MemoryRecallRequest
            {
                RequestId = request.RequestId,
                AgentId = request.AgentId,
                WorkspaceId = null,
                QueryText = queryText,
                QueryIntent = request.Scenario,
                TriggerSource = request.TriggerSource,
                TraceId = request.TraceId
            }).Items;
        }

        var scopes = new[]
        {
            new RecallScope(request.AgentId, false, workspaceId, false, 0, maxMemoryCount),
            new RecallScope(request.AgentId, false, null, true, 1, Math.Max(2, maxMemoryCount / 2)),
            new RecallScope(null, true, workspaceId, false, 2, Math.Max(2, maxMemoryCount / 3)),
            new RecallScope(null, true, null, true, 3, Math.Min(3, Math.Max(1, maxMemoryCount / 4)))
        };

        var ranked = new List<ScopedRecallItem>();
        foreach (var scope in scopes)
        {
            var items = store.SearchRecallCandidatesInScope(
                scope.AgentId,
                scope.AgentMustBeNull,
                scope.WorkspaceId,
                scope.WorkspaceMustBeNull,
                queryText,
                recallOptions.MinimumConfidence,
                recallOptions.IncludeCandidateMemories,
                scope.Limit);

            ranked.AddRange(items.Select(item => new ScopedRecallItem(item, scope.Priority)));
        }

        var selected = ranked
            .GroupBy(static item => item.Item.Id, StringComparer.OrdinalIgnoreCase)
            .Select(static group => group
                .OrderBy(static item => item.ScopePriority)
                .ThenByDescending(static item => item.Item.RecallScore)
                .First())
            .OrderBy(static item => item.ScopePriority)
            .ThenByDescending(static item => item.Item.RecallScore)
            .Take(maxMemoryCount)
            .Select(static item => item.Item)
            .ToList();

        if (selected.Count == 0)
        {
            selected = recallService.Recall(new MemoryRecallRequest
            {
                RequestId = request.RequestId,
                AgentId = request.AgentId,
                WorkspaceId = null,
                QueryText = queryText,
                QueryIntent = request.Scenario,
                TriggerSource = request.TriggerSource,
                TraceId = request.TraceId
            }).Items
                .Take(maxMemoryCount)
                .ToList();
        }

        return selected;
    }

    private static MemorySupplyResult CreateDisabledResult(
        MemorySupplyRequest request,
        MemorySupplyOptions supplyOptions,
        MemoryRecallOptions recallOptions,
        int maxMemoryCount)
    {
        return new MemorySupplyResult
        {
            RequestId = request.RequestId,
            Enabled = false,
            Summary = "主动记忆供应未启用。",
            Budget = new MemorySupplyBudget
            {
                MaxMemoryCount = Math.Max(0, maxMemoryCount),
                UsedMemoryCount = 0,
                MaxTokenBudget = request.MaxTokenBudget,
                EstimatedTokens = 0
            },
            AppliedPolicy = new MemorySupplyPolicy
            {
                SupplyEnabled = supplyOptions.Enabled,
                MaxMemoryCount = Math.Max(0, maxMemoryCount),
                RecallMinimumConfidence = recallOptions.MinimumConfidence,
                Ranking = "disabled",
                Grouping = "none"
            },
            TraceId = request.TraceId
        };
    }

    private static int NormalizeLimit(int? requestedLimit, int configuredLimit)
    {
        var limit = requestedLimit.GetValueOrDefault(configuredLimit);
        if (limit <= 0) return 0;
        return Math.Min(limit, MaximumToolMemoryCount);
    }

    private static string? NormalizeOptional(string? value)
    {
        return string.IsNullOrWhiteSpace(value) ? null : value.Trim();
    }

    private sealed record RecallScope(string? AgentId, bool AgentMustBeNull, string? WorkspaceId, bool WorkspaceMustBeNull, int Priority, int Limit);

    private sealed record ScopedRecallItem(MemoryRecallItem Item, int ScopePriority);

    private sealed record LayeredRecallItem(MemoryRecallItem Item, string GroupKey, int Priority);

    private static int NormalizeLayerLimit(int configuredLimit, int fallbackLimit, int maxMemoryCount)
    {
        var limit = configuredLimit <= 0 ? fallbackLimit : configuredLimit;
        return Math.Clamp(limit, 1, Math.Max(1, maxMemoryCount));
    }

    /// <summary>
    /// 路径 1：用最新用户消息构建精确查询文本。
    /// </summary>
    private static string BuildPrimaryQueryText(MemorySupplyRequest request)
    {
        var builder = new StringBuilder();

        // 优先取最后一条用户消息（最新意图）
        var lastUserMessage = request.RecentMessages
            .Where(static m => !string.IsNullOrWhiteSpace(m))
            .LastOrDefault();

        if (!string.IsNullOrWhiteSpace(lastUserMessage))
        {
            // 去掉 "user: " 前缀
            var content = lastUserMessage;
            if (content.StartsWith("user:", StringComparison.OrdinalIgnoreCase))
                content = content[5..].TrimStart();
            Append(builder, content);
        }
        else
        {
            Append(builder, request.CurrentTask);
        }

        return builder.Length == 0 ? request.TriggerSource ?? "memory supply context" : builder.ToString();
    }

    /// <summary>
    /// 路径 2：用 session 标题 + 场景构建宽泛查询文本。
    /// </summary>
    private static string BuildSecondaryQueryText(MemorySupplyRequest request)
    {
        var builder = new StringBuilder();
        Append(builder, request.SessionTitle);
        Append(builder, request.Scenario);

        // 如果有多条消息，取前几条补充主题上下文
        foreach (var message in request.RecentMessages.Where(static m => !string.IsNullOrWhiteSpace(m)).Take(2))
        {
            var content = message;
            if (content.StartsWith("user:", StringComparison.OrdinalIgnoreCase))
                content = content[5..].TrimStart();
            Append(builder, content);
        }

        return builder.ToString();
    }

    private static void Append(StringBuilder builder, string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return;
        if (builder.Length > 0) builder.AppendLine();
        builder.Append(value.Trim());
    }

    private static MemorySupplyItem ToSupplyItem(MemoryRecallItem item, string groupKey)
    {
        var content = string.IsNullOrWhiteSpace(item.Detail) ? item.Summary : item.Detail;
        return new MemorySupplyItem
        {
            Id = item.Id,
            Kind = item.Kind,
            Topic = item.Topic,
            Title = item.Title,
            Content = content ?? string.Empty,
            Reason = GetSupplyReason(groupKey, item.Topic),
            Confidence = item.Confidence,
            Score = Math.Round(item.RecallScore, 4),
            SourceRecallScore = Math.Round(item.RecallScore, 4),
            LifecycleState = item.LifecycleState,
            ConfirmationState = item.ConfirmationState,
            UpdatedAt = item.UpdatedAt
        };
    }

    private static List<MemorySupplyItem> TakeWithinBudget(IReadOnlyList<MemorySupplyItem> items, int maxTokenBudget, out int estimatedTokens)
    {
        var selected = new List<MemorySupplyItem>();
        estimatedTokens = 0;

        foreach (var item in items)
        {
            var itemTokens = EstimateTokens(item.Content);
            if (selected.Count > 0 && estimatedTokens + itemTokens > maxTokenBudget) break;

            selected.Add(item);
            estimatedTokens += itemTokens;

            if (estimatedTokens >= maxTokenBudget) break;
        }

        return selected;
    }

    private static int EstimateTokens(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return 0;
        return Math.Max(1, (int)Math.Ceiling(value.Length / 4d));
    }

    private static string BuildSummary(IReadOnlyList<MemorySupplyItem> items, IReadOnlyList<MemorySupplyGroup> groups)
    {
        if (items.Count == 0) return "未供应可用记忆。";

        var topGroups = groups
            .Take(3)
            .Select(static group => group.Title)
            .Where(static title => !string.IsNullOrWhiteSpace(title));

        return $"供应 {items.Count} 条记忆，形成 {groups.Count} 个分组，主要分组：{string.Join("、", topGroups)}。";
    }

    private static string GetGroupKey(MemorySupplyItem item)
    {
        if (item.Reason.Contains("当前工作区", StringComparison.OrdinalIgnoreCase)) return WorkspaceProfileGroupKey;
        if (item.Reason.Contains("全局用户画像", StringComparison.OrdinalIgnoreCase)) return GlobalProfileGroupKey;
        if (item.Reason.Contains("会话连续性", StringComparison.OrdinalIgnoreCase)) return SessionContinuityGroupKey;
        if (item.Reason.Contains("当前任务", StringComparison.OrdinalIgnoreCase)) return TaskRelatedGroupKey;

        var text = $"{item.Topic} {item.Title} {item.Content}";
        if (ContainsAny(text, "偏好", "preference", "习惯", "喜欢")) return "preference";
        if (ContainsAny(text, "约束", "constraint", "规则", "禁止", "必须", "规范")) return "constraint";
        if (ContainsAny(text, "任务", "task", "待办", "计划", "进度")) return "task";
        if (ContainsAny(text, "事实", "fact", "项目", "仓库", "环境")) return "fact";
        return "other";
    }

    private static bool ContainsAny(string value, params string[] terms)
    {
        return terms.Any(term => value.Contains(term, StringComparison.OrdinalIgnoreCase));
    }

    private static string GetGroupTitle(string groupKey)
    {
        return groupKey switch
        {
            WorkspaceProfileGroupKey => "当前工作区上下文",
            GlobalProfileGroupKey => "全局用户画像",
            SessionContinuityGroupKey => "当前会话连续性",
            TaskRelatedGroupKey => "当前任务相关记忆",
            "preference" => "偏好记忆",
            "constraint" => "约束记忆",
            "fact" => "事实记忆",
            "task" => "任务记忆",
            "abstraction" => "抽象记忆",
            _ => "其他记忆"
        };
    }

    private static int GetGroupPriority(string groupKey)
    {
        return groupKey switch
        {
            WorkspaceProfileGroupKey => 0,
            GlobalProfileGroupKey => 1,
            SessionContinuityGroupKey => 2,
            TaskRelatedGroupKey => 3,
            "constraint" => 1,
            "preference" => 2,
            "task" => 3,
            "fact" => 4,
            _ => 9
        };
    }

    private static string GetSupplyReason(string groupKey, string? topic)
    {
        var topicText = string.IsNullOrWhiteSpace(topic) ? string.Empty : $"，主题“{topic}”";
        return groupKey switch
        {
            WorkspaceProfileGroupKey => $"当前工作区长期上下文{topicText}。",
            GlobalProfileGroupKey => $"全局用户画像长期记忆{topicText}。",
            SessionContinuityGroupKey => $"当前会话连续性记忆{topicText}。",
            TaskRelatedGroupKey => $"与当前任务相关的召回记忆{topicText}。",
            _ => string.IsNullOrWhiteSpace(topic) ? "与当前上下文相关。" : $"与主题“{topic}”相关。"
        };
    }
}
