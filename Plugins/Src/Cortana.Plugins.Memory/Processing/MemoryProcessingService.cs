using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Cortana.Plugins.Memory.Models;
using Cortana.Plugins.Memory.Storage;
using Microsoft.Extensions.Logging;

namespace Cortana.Plugins.Memory.Processing;

/// <summary>
/// 默认记忆数据处理服务。
/// </summary>
public sealed class MemoryProcessingService(
    IMemoryStore store,
    IMemorySemanticProcessor semanticProcessor,
    IMemoryAbstractionService abstractionService,
    ILogger<MemoryProcessingService> logger) : IMemoryProcessingService
{
    public const string FragmentExtractionProcessorName = "fragment-extraction";

    public MemoryProcessingResult Process(MemoryProcessingRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);

        store.EnsureInitialized();
        var traceId = string.IsNullOrWhiteSpace(request.TraceId) ? request.RequestId : request.TraceId;
        var state = store.GetProcessingState(FragmentExtractionProcessorName, request.AgentId, request.WorkspaceId);
        var now = DateTimeOffset.UtcNow.ToString("O");
        state.State = "running";
        state.LastError = null;
        state.UpdatedAt = now;
        store.UpsertProcessingState(state);

        var result = new MemoryProcessingResult
        {
            RequestId = request.RequestId
        };

        try
        {
            var limit = request.MaxObservationCount <= 0 ? 100 : request.MaxObservationCount;
            var observations = store.GetUnprocessedObservations(FragmentExtractionProcessorName, request.AgentId, request.WorkspaceId, limit);
            foreach (var observation in observations)
            {
                ProcessObservation(observation, traceId, result);
                result.ProcessedObservationCount++;
                state.LastObservationTimestamp = observation.CreatedTimestamp;
                state.LastObservationId = observation.Id;
            }

            state.ProcessedCount += result.ProcessedObservationCount;
            state.CreatedFragmentCount += result.CreatedFragmentCount;
            state.MergedFragmentCount += result.MergedFragmentCount;
            state.CreatedAbstractionCount += result.CreatedAbstractionCount;
            state.State = "completed";
            state.UpdatedAt = DateTimeOffset.UtcNow.ToString("O");
            store.UpsertProcessingState(state);

            result.State = "completed";
            result.Summary = $"处理观察记录 {result.ProcessedObservationCount} 条，创建长期记忆 {result.CreatedFragmentCount} 条，合并 {result.MergedFragmentCount} 条，失败 {result.FailedObservationCount} 条。";
            InsertProcessingEvent("processing.completed", request.AgentId ?? "global", result, traceId);

            // 触发抽象记忆生成（降级实现或宿主适配后会使用模型）
            try
            {
                abstractionService.RunAbstractionPass(request.AgentId, request.WorkspaceId);
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "抽象记忆生成过程发生异常（不影响本次处理结果），RequestId={RequestId}。", request.RequestId);
            }

            return result;
        }
        catch (Exception ex) when (ex is MemoryStorageException or InvalidOperationException or ArgumentException)
        {
            logger.LogError(ex, "记忆数据处理失败，RequestId={RequestId}，TraceId={TraceId}。", request.RequestId, traceId);
            state.State = "failed";
            state.LastError = ex.Message;
            state.UpdatedAt = DateTimeOffset.UtcNow.ToString("O");
            store.UpsertProcessingState(state);

            result.State = "failed";
            result.Summary = ex.Message;
            InsertProcessingEvent("processing.failed", request.AgentId ?? "global", result, traceId);
            return result;
        }
    }

    public MemoryProcessingState GetState(string processorName, string? agentId, string? workspaceId)
    {
        return store.GetProcessingState(processorName, agentId, workspaceId);
    }

    private void ProcessObservation(ObservationRecord observation, string traceId, MemoryProcessingResult result)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(observation.AgentId) || string.IsNullOrWhiteSpace(observation.Content)) return;

            var candidates = semanticProcessor.ExtractCandidates(observation, traceId);
            foreach (var candidate in candidates)
            {
                if (string.IsNullOrWhiteSpace(candidate.Summary)) continue;

                var existing = store.SearchSimilarFragments(observation.AgentId, observation.WorkspaceId, candidate.MemoryType, candidate.Summary, 1).FirstOrDefault();
                if (existing is null)
                {
                    var fragment = CreateFragment(candidate, traceId);
                    store.UpsertMemoryFragment(fragment);
                    result.CreatedFragmentCount++;
                    InsertMutation(fragment.AgentId, fragment.Id, "fragment", "create", null, JsonSerializer.Serialize(fragment), "记忆数据处理创建长期记忆。", traceId);
                    InsertProcessingEvent("fragment.extracted", fragment.AgentId, new { FragmentId = fragment.Id, ObservationId = observation.Id, traceId }, traceId);
                }
                else
                {
                    var merged = MergeFragment(existing, candidate);
                    store.UpsertMemoryFragment(merged);
                    result.MergedFragmentCount++;
                    InsertMutation(merged.AgentId, merged.Id, "fragment", "merge", JsonSerializer.Serialize(existing), JsonSerializer.Serialize(merged), "记忆数据处理合并相似长期记忆。", traceId);
                }
            }
        }
        catch (Exception ex) when (ex is InvalidOperationException or ArgumentException or JsonException)
        {
            result.FailedObservationCount++;
            logger.LogWarning(ex, "处理观察记录 {ObservationId} 失败，TraceId={TraceId}。", observation.Id, traceId);
            InsertProcessingEvent("processing.failed", observation.AgentId ?? "global", new { observation.Id, Error = ex.Message, traceId }, traceId);
        }
    }

    private static MemoryFragment CreateFragment(MemorySemanticCandidate candidate, string traceId)
    {
        var observation = candidate.SourceObservation;
        var now = DateTimeOffset.UtcNow.ToString("O");
        var salience = CalculateSalience(candidate.Importance, candidate.Confidence, candidate.Novelty, observation.Role);
        var id = CreateStableId(observation.AgentId ?? "global", observation.WorkspaceId, candidate.MemoryType, candidate.Summary);

        return new MemoryFragment
        {
            Id = id,
            AgentId = observation.AgentId ?? "global",
            WorkspaceId = observation.WorkspaceId,
            MemoryType = candidate.MemoryType,
            Topic = candidate.Topic,
            Title = candidate.Title,
            Summary = candidate.Summary,
            Detail = candidate.Detail,
            KeywordsJson = JsonSerializer.Serialize(candidate.Keywords),
            TagsJson = JsonSerializer.Serialize(new[] { "memory-processing", $"trace:{traceId}" }),
            SourceObservationIdsJson = JsonSerializer.Serialize(new[] { observation.Id }),
            SourceSessionIdsJson = JsonSerializer.Serialize(new[] { observation.SessionId }),
            SourceTurnIdsJson = string.IsNullOrWhiteSpace(observation.TurnId) ? null : JsonSerializer.Serialize(new[] { observation.TurnId }),
            Importance = Clamp(candidate.Importance),
            Confidence = Clamp(candidate.Confidence),
            Novelty = Clamp(candidate.Novelty),
            SalienceScore = salience,
            RetentionScore = salience,
            DecayRate = 0.015,
            ReinforcementCount = 1,
            ClarityLevel = "clear",
            ConfirmationState = candidate.Confidence >= 0.7 ? "confirmed" : "pending",
            LifecycleState = candidate.Confidence >= 0.7 ? "active" : "candidate",
            LastReinforcedAt = now,
            CreatedAt = now,
            UpdatedAt = now
        };
    }

    private static MemoryFragment MergeFragment(MemoryFragment existing, MemorySemanticCandidate candidate)
    {
        var observation = candidate.SourceObservation;
        var now = DateTimeOffset.UtcNow.ToString("O");
        existing.SourceObservationIdsJson = MergeJsonArray(existing.SourceObservationIdsJson, observation.Id);
        existing.SourceSessionIdsJson = MergeJsonArray(existing.SourceSessionIdsJson, observation.SessionId);
        if (!string.IsNullOrWhiteSpace(observation.TurnId)) existing.SourceTurnIdsJson = MergeJsonArray(existing.SourceTurnIdsJson, observation.TurnId);
        existing.Confidence = Clamp(Math.Max(existing.Confidence, candidate.Confidence) + 0.03);
        existing.Importance = Clamp(Math.Max(existing.Importance, candidate.Importance));
        existing.Novelty = Clamp(Math.Max(existing.Novelty, candidate.Novelty));
        existing.SalienceScore = CalculateSalience(existing.Importance, existing.Confidence, existing.Novelty, observation.Role);
        existing.RetentionScore = Clamp(Math.Max(existing.RetentionScore, existing.SalienceScore) + 0.02);
        existing.ReinforcementCount++;
        existing.LastReinforcedAt = now;
        existing.UpdatedAt = now;
        if (existing.Confidence >= 0.7)
        {
            existing.ConfirmationState = "confirmed";
            existing.LifecycleState = "active";
        }

        return existing;
    }

    private void InsertProcessingEvent(string eventType, string agentId, object payload, string traceId)
    {
        store.InsertMemoryEvent(new MemoryEvent
        {
            EventId = Guid.NewGuid().ToString("N"),
            AgentId = string.IsNullOrWhiteSpace(agentId) ? "global" : agentId,
            EventType = eventType,
            PayloadJson = JsonSerializer.Serialize(payload),
            ProcessedAt = DateTimeOffset.UtcNow.ToString("O")
        });
    }

    private void InsertMutation(string agentId, string memoryId, string memoryKind, string mutationType, string? beforeJson, string? afterJson, string reason, string traceId)
    {
        store.InsertMemoryMutation(new MemoryMutation
        {
            Id = Guid.NewGuid().ToString("N"),
            AgentId = agentId,
            MemoryId = memoryId,
            MemoryKind = memoryKind,
            MutationType = mutationType,
            BeforeJson = beforeJson,
            AfterJson = afterJson,
            Reason = reason,
            TraceId = traceId,
            CreatedAt = DateTimeOffset.UtcNow.ToString("O")
        });
    }

    private static string MergeJsonArray(string? json, string value)
    {
        var values = string.IsNullOrWhiteSpace(json)
            ? []
            : JsonSerializer.Deserialize<List<string>>(json) ?? [];

        if (!values.Contains(value, StringComparer.Ordinal)) values.Add(value);
        return JsonSerializer.Serialize(values);
    }

    private static double CalculateSalience(double importance, double confidence, double novelty, string role)
    {
        var roleWeight = string.Equals(role, "user", StringComparison.OrdinalIgnoreCase) ? 1d : 0.7d;
        return Clamp(importance * 0.35 + confidence * 0.30 + novelty * 0.20 + roleWeight * 0.15);
    }

    private static double Clamp(double value)
    {
        return Math.Min(1d, Math.Max(0d, value));
    }

    private static string CreateStableId(string agentId, string? workspaceId, string memoryType, string summary)
    {
        var source = $"{agentId}|{workspaceId}|{memoryType}|{NormalizeFingerprintText(summary)}";
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(source));
        return $"fragment-{Convert.ToHexString(hash).ToLowerInvariant()[..32]}";
    }

    private static string NormalizeFingerprintText(string value)
    {
        var builder = new StringBuilder(value.Length);
        foreach (var c in value.Trim().ToLowerInvariant())
        {
            if (!char.IsPunctuation(c) && !char.IsWhiteSpace(c)) builder.Append(c);
        }

        return builder.ToString();
    }
}
