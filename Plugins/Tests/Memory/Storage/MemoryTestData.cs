using Cortana.Plugins.Memory.Models;
using Cortana.Plugins.Memory.Processing;

namespace Memory.Test.Storage;

internal static class MemoryTestData
{
    public const string AgentId = "agent-test";
    public const string WorkspaceId = "workspace-test";
    public const string OtherAgentId = "agent-other";
    public const string OtherWorkspaceId = "workspace-other";

    public static ObservationRecord Observation(string id, long timestamp = 1000, string? agentId = AgentId, string? workspaceId = WorkspaceId)
    {
        return new ObservationRecord
        {
            Id = id,
            AgentId = agentId,
            WorkspaceId = workspaceId,
            SessionId = $"session-{id}",
            TurnId = $"turn-{id}",
            MessageId = $"message-{id}",
            EventType = "conversation.user.message",
            Role = "user",
            Content = $"用户喜欢 C# 和 .NET 测试 {id}",
            AttachmentsJson = "[]",
            CreatedTimestamp = timestamp,
            ModelName = "test-model",
            TraceId = $"trace-{id}",
            SourceFactsJson = "{\"source\":\"test\"}",
            CreatedAt = ToTime(timestamp)
        };
    }

    public static MemoryFragment Fragment(string id, string topic = "dotnet", string summary = "用户喜欢 C# 测试", string? workspaceId = WorkspaceId)
    {
        return new MemoryFragment
        {
            Id = id,
            AgentId = AgentId,
            WorkspaceId = workspaceId,
            MemoryType = "preference",
            Topic = topic,
            Title = $"标题-{id}",
            Summary = summary,
            Detail = $"详情-{id}",
            KeywordsJson = "[\"csharp\",\"test\"]",
            TagsJson = "[\"unit\"]",
            EntitiesJson = "[\"dotnet\"]",
            SourceObservationIdsJson = "[\"obs-1\"]",
            SourceSessionIdsJson = "[\"session-1\"]",
            SourceTurnIdsJson = "[\"turn-1\"]",
            Importance = 0.8,
            Confidence = 0.9,
            EmotionalWeight = 0.1,
            Novelty = 0.7,
            SalienceScore = 0.85,
            RetentionScore = 0.82,
            DecayRate = 0.015,
            ReinforcementCount = 1,
            ClarityLevel = "clear",
            ConfirmationState = "confirmed",
            LifecycleState = "active",
            LastReinforcedAt = ToTime(1000),
            CreatedAt = ToTime(1000),
            UpdatedAt = ToTime(1000)
        };
    }

    public static MemoryAbstraction Abstraction(string id, string summary = "用户长期偏好 .NET")
    {
        return new MemoryAbstraction
        {
            Id = id,
            AgentId = AgentId,
            WorkspaceId = WorkspaceId,
            AbstractionType = "preference-summary",
            Title = $"抽象-{id}",
            Statement = summary,
            Summary = summary,
            SupportingMemoryIdsJson = "[\"fragment-1\",\"fragment-2\"]",
            CounterMemoryIdsJson = "[]",
            KeywordsJson = "[\"dotnet\"]",
            TagsJson = "[\"abstraction\"]",
            Importance = 0.75,
            Confidence = 0.88,
            StabilityScore = 0.8,
            RetentionScore = 0.81,
            DecayRate = 0.01,
            ClarityLevel = "clear",
            ConfirmationState = "confirmed",
            LifecycleState = "active",
            LastValidatedAt = ToTime(1000),
            CreatedAt = ToTime(1000),
            UpdatedAt = ToTime(1000)
        };
    }

    public static MemoryLink Link(string id, string sourceId = "fragment-1", string targetId = "abstraction-1")
    {
        return new MemoryLink
        {
            Id = id,
            AgentId = AgentId,
            SourceMemoryId = sourceId,
            SourceMemoryKind = "fragment",
            TargetMemoryId = targetId,
            TargetMemoryKind = "abstraction",
            RelationType = "supports",
            Weight = 0.9,
            EvidenceCount = 2,
            Confidence = 0.85,
            CreatedAt = ToTime(1000),
            UpdatedAt = ToTime(1000)
        };
    }

    public static MemoryEvent Event(string id)
    {
        return new MemoryEvent
        {
            EventId = id,
            AgentId = AgentId,
            EventType = "fragment.extracted",
            PayloadJson = "{\"id\":\"fragment-1\"}",
            ProcessedAt = ToTime(1000)
        };
    }

    public static RecallLog RecallLog(string id)
    {
        return new RecallLog
        {
            Id = id,
            RequestId = $"request-{id}",
            AgentId = AgentId,
            WorkspaceId = WorkspaceId,
            QueryText = "C# 测试",
            QueryIntent = "answer",
            TriggerSource = "unit-test",
            HitMemoryIdsJson = "[\"fragment-1\"]",
            SupportingMemoryIdsJson = "[\"fragment-1\"]",
            SuppressedMemoryIdsJson = "[]",
            RecallSummary = "召回 1 条记忆。",
            Confidence = 0.8,
            BudgetJson = "{\"max\":1}",
            AppliedPolicyJson = "{\"source\":\"test\"}",
            TraceId = $"trace-{id}",
            CreatedAt = ToTime(1000)
        };
    }

    public static MemoryMutation Mutation(string id, string memoryId = "fragment-1")
    {
        return new MemoryMutation
        {
            Id = id,
            AgentId = AgentId,
            MemoryId = memoryId,
            MemoryKind = "fragment",
            MutationType = "create",
            BeforeJson = null,
            AfterJson = "{\"id\":\"fragment-1\"}",
            Reason = "测试创建。",
            TraceId = $"trace-{id}",
            CreatedAt = ToTime(1000)
        };
    }

    public static MemorySetting Setting(string id, string key = "recall.maxMemoryCount", string value = "5", string? agentId = null, string? workspaceId = null, bool enabled = true)
    {
        return new MemorySetting
        {
            Id = id,
            AgentId = agentId,
            WorkspaceId = workspaceId,
            SettingKey = key,
            SettingValue = value,
            ValueType = "int",
            Category = "recall",
            Description = "测试配置。",
            IsEnabled = enabled,
            CreatedAt = ToTime(1000),
            UpdatedAt = ToTime(1000)
        };
    }

    public static MemoryProcessingState ProcessingState(string processorName = "processor-test")
    {
        return new MemoryProcessingState
        {
            ProcessorName = processorName,
            AgentId = AgentId,
            WorkspaceId = WorkspaceId,
            State = "running",
            LastObservationTimestamp = 1000,
            LastObservationId = "obs-1",
            ProcessedCount = 3,
            CreatedFragmentCount = 2,
            MergedFragmentCount = 1,
            CreatedAbstractionCount = 1,
            LastError = null,
            LockedUntil = ToTime(2000),
            CreatedAt = ToTime(1000),
            UpdatedAt = ToTime(1000)
        };
    }

    public static string ToTime(long timestamp)
    {
        return DateTimeOffset.FromUnixTimeMilliseconds(timestamp).UtcDateTime.ToString("O");
    }
}
