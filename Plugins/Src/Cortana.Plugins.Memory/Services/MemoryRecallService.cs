using System.Text.Json;
using Cortana.Plugins.Memory.Models;
using Cortana.Plugins.Memory.Storage;

namespace Cortana.Plugins.Memory.Services;

/// <summary>
/// 基于本地记忆库执行记忆召回，并记录召回日志。
/// </summary>
public sealed class MemoryRecallService(IMemoryStore store, IMemorySettingsService settingsService) : IMemoryRecallService
{
    public MemoryRecallResult Recall(MemoryRecallRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (string.IsNullOrWhiteSpace(request.AgentId)) throw new ArgumentException("智能体标识不能为空。", nameof(request));

        var options = settingsService.GetRecallOptions(request.AgentId, request.WorkspaceId);
        var candidates = store.SearchRecallCandidates(
            request.AgentId,
            request.WorkspaceId,
            request.QueryText,
            options.MinimumConfidence,
            options.IncludeCandidateMemories,
            options.MaxMemoryCount);

        var items = candidates
            .OrderByDescending(static item => item.RecallScore)
            .Take(options.MaxMemoryCount)
            .ToList();

        var windows = items
            .GroupBy(static item => string.IsNullOrWhiteSpace(item.Topic) ? item.Kind : item.Topic, StringComparer.OrdinalIgnoreCase)
            .OrderByDescending(static group => group.Max(item => item.RecallScore))
            .Take(options.MaxWindowCount)
            .Select(static group => new MemoryRecallWindow
            {
                Topic = group.Key,
                Title = group.Key,
                Items = group.OrderByDescending(static item => item.RecallScore).ToList()
            })
            .ToList();

        var windowItems = windows
            .SelectMany(static window => window.Items)
            .GroupBy(static item => item.Id, StringComparer.OrdinalIgnoreCase)
            .Select(static group => group.First())
            .OrderByDescending(static item => item.RecallScore)
            .ToList();

        var accessedAt = DateTimeOffset.UtcNow.ToString("O");
        store.RecordMemoryAccesses(windowItems, accessedAt);

        var confidence = windowItems.Count == 0 ? 0 : Math.Round(windowItems.Average(static item => item.Confidence), 4);
        var summary = BuildSummary(windowItems, windows);

        store.InsertRecallLog(new RecallLog
        {
            Id = Guid.NewGuid().ToString("N"),
            RequestId = request.RequestId,
            AgentId = request.AgentId,
            WorkspaceId = request.WorkspaceId,
            QueryText = request.QueryText,
            QueryIntent = request.QueryIntent,
            TriggerSource = request.TriggerSource,
            HitMemoryIdsJson = JsonSerializer.Serialize(windowItems.Select(static item => item.Id)),
            SupportingMemoryIdsJson = JsonSerializer.Serialize(windowItems.Select(static item => item.Id)),
            SuppressedMemoryIdsJson = "[]",
            RecallSummary = summary,
            Confidence = confidence,
            BudgetJson = JsonSerializer.Serialize(new
            {
                options.MaxWindowCount,
                options.MaxMemoryCount,
                options.MinimumConfidence
            }),
            AppliedPolicyJson = JsonSerializer.Serialize(new
            {
                options.IncludeCandidateMemories,
                Ranking = "confidence/salience/retention/access/text-match",
                Source = "memory-store-keyword",
                SupportsPendingCandidateOnQueryMatch = true
            }),
            TraceId = request.TraceId,
            CreatedAt = accessedAt
        });

        return new MemoryRecallResult
        {
            RequestId = request.RequestId,
            Windows = windows,
            Items = windowItems,
            Confidence = confidence,
            Summary = summary
        };
    }

    private static string BuildSummary(IReadOnlyList<MemoryRecallItem> items, IReadOnlyList<MemoryRecallWindow> windows)
    {
        if (items.Count == 0) return "未召回到可用记忆。";

        var topTopics = windows
            .Take(3)
            .Select(static window => window.Topic)
            .Where(static topic => !string.IsNullOrWhiteSpace(topic));

        return $"召回 {items.Count} 条记忆，形成 {windows.Count} 个窗口，主要主题：{string.Join("、", topTopics)}。";
    }
}
