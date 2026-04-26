using Cortana.Plugins.Memory.Models;
using Microsoft.Extensions.Logging;

namespace Cortana.Plugins.Memory.Processing;

/// <summary>
/// 大模型能力不可用时的保守语义处理器。
/// </summary>
public sealed class FallbackMemorySemanticProcessor(ILogger<FallbackMemorySemanticProcessor> logger) : IMemorySemanticProcessor
{
    private const int MaximumSummaryLength = 240;
    private const int MinimumContentLength = 4; // 可调节的最小长度阈值
    private const double MinimumConfidenceForFragment = 0.5; // 降低置信度阈值以便生成更多候选

    public IReadOnlyList<MemorySemanticCandidate> ExtractCandidates(ObservationRecord observation, string traceId)
    {
        ArgumentNullException.ThrowIfNull(observation);

        var content = NormalizeContent(observation.Content);
        if (content.Length < MinimumContentLength) return [];

        logger.LogDebug("使用降级语义处理器处理观察记录 {ObservationId}，TraceId={TraceId}。", observation.Id, traceId);

        var memoryType = InferMemoryType(content);
        var topic = InferTopic(content, memoryType);
        var summary = content.Length <= MaximumSummaryLength ? content : content[..MaximumSummaryLength];

        var candidate = new MemorySemanticCandidate
        {
            MemoryType = memoryType,
            Topic = topic,
            Title = topic,
            Summary = summary,
            Detail = content,
            Keywords = ExtractKeywords(content, topic),
            Importance = GetImportance(memoryType),
            Confidence = 0.55,
            Novelty = 0.6,
            SourceObservation = observation
        };

        // 如果置信度低于 MinimumConfidenceForFragment，则略微提升以允许创建候选（保守策略）
        if (candidate.Confidence < MinimumConfidenceForFragment) candidate.Confidence = MinimumConfidenceForFragment;

        return [candidate];
    }

    private static string NormalizeContent(string? content)
    {
        if (string.IsNullOrWhiteSpace(content)) return string.Empty;
        return string.Join(' ', content.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
    }

    private static string InferMemoryType(string content)
    {
        if (ContainsAny(content, "喜欢", "偏好", "希望", "习惯", "倾向")) return "preference";
        if (ContainsAny(content, "必须", "不要", "禁止", "统一", "规则", "约束")) return "constraint";
        if (ContainsAny(content, "下一步", "待办", "计划", "任务", "实现", "处理")) return "task";
        return "fact";
    }

    private static string InferTopic(string content, string memoryType)
    {
        if (ContainsAny(content, "记忆", "memory", "Memory")) return "memory";
        if (ContainsAny(content, "插件", "plugin", "Plugin")) return "plugin";
        if (ContainsAny(content, "模型", "大模型", "LLM", "AI")) return "model";
        if (ContainsAny(content, "配置", "设置")) return "configuration";
        return memoryType;
    }

    private static IReadOnlyList<string> ExtractKeywords(string content, string topic)
    {
        var keywords = content
            .Split([' ', '\t', '\r', '\n', ',', '.', ';', ':', '，', '。', '；', '：', '！', '？', '!', '?', '、'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(static item => item.Length >= 2)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Take(8)
            .ToList();

        if (!keywords.Contains(topic, StringComparer.OrdinalIgnoreCase)) keywords.Insert(0, topic);
        return keywords;
    }

    private static double GetImportance(string memoryType)
    {
        return memoryType switch
        {
            "constraint" => 0.85,
            "preference" => 0.8,
            "task" => 0.7,
            _ => 0.65
        };
    }

    private static bool ContainsAny(string value, params string[] terms)
    {
        return terms.Any(term => value.Contains(term, StringComparison.OrdinalIgnoreCase));
    }
}
