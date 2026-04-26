using System.Text.Json;
using Cortana.Plugins.Memory.Models;
using Cortana.Plugins.Memory.Storage;

namespace Cortana.Plugins.Memory.Services;

/// <summary>
/// 默认人工记忆写入服务。
/// </summary>
public sealed class MemoryNoteService(IMemoryStore store) : IMemoryNoteService
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private static readonly HashSet<string> AllowedMemoryTypes = new(StringComparer.OrdinalIgnoreCase)
    {
        "fact",
        "preference",
        "task",
        "constraint",
        "note"
    };

    /// <inheritdoc />
    public MemoryAddNoteResult AddNote(MemoryAddNoteRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (string.IsNullOrWhiteSpace(request.AgentId)) throw new ArgumentException("智能体标识不能为空。", nameof(request));
        if (string.IsNullOrWhiteSpace(request.Content)) throw new ArgumentException("记忆内容不能为空。", nameof(request));
        if (string.IsNullOrWhiteSpace(request.MemoryType)) throw new ArgumentException("记忆类型不能为空。", nameof(request));
        if (!AllowedMemoryTypes.Contains(request.MemoryType)) throw new ArgumentException("记忆类型只支持 fact、preference、task、constraint 或 note。", nameof(request));
        if (string.IsNullOrWhiteSpace(request.Reason)) throw new ArgumentException("写入原因不能为空。", nameof(request));
        if (!request.UserConfirmed) throw new InvalidOperationException("写入人工记忆前必须获得用户明确授权。");

        var now = DateTimeOffset.UtcNow.ToString("O");
        var traceId = string.IsNullOrWhiteSpace(request.TraceId) ? Guid.NewGuid().ToString("N") : request.TraceId.Trim();
        var content = request.Content.Trim();
        var topic = string.IsNullOrWhiteSpace(request.Topic) ? request.MemoryType.Trim().ToLowerInvariant() : request.Topic.Trim();
        var memoryType = request.MemoryType.Trim().ToLowerInvariant();
        var fragment = new MemoryFragment
        {
            Id = $"manual.{Guid.NewGuid():N}",
            AgentId = request.AgentId.Trim(),
            WorkspaceId = string.IsNullOrWhiteSpace(request.WorkspaceId) ? null : request.WorkspaceId.Trim(),
            MemoryType = memoryType,
            Topic = topic,
            Title = BuildTitle(content),
            Summary = content,
            Detail = content,
            KeywordsJson = "[]",
            TagsJson = JsonSerializer.Serialize(new[] { "manual", "tool" }, JsonOptions),
            EntitiesJson = "[]",
            SourceObservationIdsJson = "[]",
            SourceSessionIdsJson = "[]",
            SourceTurnIdsJson = "[]",
            Importance = 0.6,
            Confidence = 0.6,
            EmotionalWeight = 0,
            Novelty = 0.5,
            SalienceScore = 0.6,
            RetentionScore = 0.6,
            DecayRate = 0.015,
            ClarityLevel = "clear",
            ConfirmationState = "pending",
            LifecycleState = "candidate",
            CompatibilityTagsJson = JsonSerializer.Serialize(new[] { "manual-note-v1" }, JsonOptions),
            CreatedAt = now,
            UpdatedAt = now
        };

        var mutation = new MemoryMutation
        {
            Id = $"mutation.{Guid.NewGuid():N}",
            AgentId = fragment.AgentId,
            MemoryId = fragment.Id,
            MemoryKind = "fragment",
            MutationType = "manual_create",
            BeforeJson = null,
            AfterJson = JsonSerializer.Serialize(fragment, JsonOptions),
            Reason = request.Reason.Trim(),
            TraceId = traceId,
            CreatedAt = now
        };

        store.AddManualMemory(fragment, mutation);

        return new MemoryAddNoteResult
        {
            MemoryId = fragment.Id,
            Kind = "fragment",
            MemoryType = fragment.MemoryType,
            Topic = fragment.Topic,
            LifecycleState = fragment.LifecycleState,
            ConfirmationState = fragment.ConfirmationState,
            MutationId = mutation.Id,
            CreatedAt = fragment.CreatedAt,
            Summary = "人工记忆已写入候选区，等待后续确认。"
        };
    }

    private static string BuildTitle(string content)
    {
        const int maximumTitleLength = 40;
        var normalized = content.ReplaceLineEndings(" ").Trim();
        return normalized.Length <= maximumTitleLength ? normalized : normalized[..maximumTitleLength];
    }
}
