using Cortana.Plugins.Memory.Models;
using System.Text.Json;

namespace Cortana.Plugins.Memory.Processing;

/// <summary>
/// 保守的抽象生成器：基于 fragment.summary 做简单聚合与合并。
/// </summary>
public sealed class FallbackMemoryAbstractionGenerator : IMemoryAbstractionGenerator
{
    public MemoryAbstraction GenerateAbstraction(string agentId, string? workspaceId, string topic, IReadOnlyList<MemoryFragment> fragments, string traceId)
    {
        if (fragments == null || fragments.Count == 0) throw new ArgumentException("fragments 不能为空。", nameof(fragments));

        // 简单策略：取前几个 summary 作为 supporting 文本，拼接并去重短句，生成 statement 与 summary
        var top = fragments.Take(6).ToList();
        var supportingIds = top.Select(f => f.Id).ToArray();
        var supportingSummaries = top.Select(f => f.Summary?.Trim()).Where(s => !string.IsNullOrWhiteSpace(s)).Distinct().ToList();

        // 尝试更紧凑的合并策略：优先取较短的句子拼接，避免生成过长或无意义的 statement
        var statement = MergeSummariesToStatement(supportingSummaries);
        var summary = statement.Length <= 200 ? statement : statement[..200];

        var now = DateTimeOffset.UtcNow.ToString("O");
        return new MemoryAbstraction
        {
            Id = $"abstraction-{Guid.NewGuid().ToString("N")[..24]}",
            AgentId = agentId,
            WorkspaceId = workspaceId,
            AbstractionType = "topic-summary",
            Title = topic,
            Statement = statement,
            Summary = summary,
            SupportingMemoryIdsJson = JsonSerializer.Serialize(supportingIds),
            KeywordsJson = JsonSerializer.Serialize(new[] { topic }),
            Importance = top.Average(f => f.Importance),
            Confidence = Math.Min(1.0, top.Average(f => f.Confidence) + 0.05),
            StabilityScore = top.Average(f => f.Confidence),
            RetentionScore = top.Average(f => f.RetentionScore),
            DecayRate = top.Average(f => f.DecayRate),
            CreatedAt = now,
            UpdatedAt = now
        };
    }

    private static string MergeSummariesToStatement(IReadOnlyList<string> summaries)
    {
        // 简单拼接并去重句子
        var pieces = new List<string>();
        foreach (var s in summaries)
        {
            var parts = s.Split(new[] { '.', '。', ';', '；' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            foreach (var p in parts)
            {
                var t = p.Trim();
                if (t.Length == 0) continue;
                if (!pieces.Any(x => x.Equals(t, StringComparison.OrdinalIgnoreCase))) pieces.Add(t);
            }
        }

        return string.Join("; ", pieces);
    }
}
