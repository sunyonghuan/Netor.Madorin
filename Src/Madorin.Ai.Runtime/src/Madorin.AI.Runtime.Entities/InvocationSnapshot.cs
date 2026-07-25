namespace Madorin.AI.Runtime.Entities;

public sealed record InvocationSnapshot(
    string InvocationId,
    string AgentId,
    string ProviderId,
    string ModelId,
    string PromptHash,
    string? GlobalMemoryHash,
    string? ProjectMemoryHash,
    string ToolCatalogVersion,
    string ProjectionVersion,
    DateTimeOffset StartedAt,
    string? ParentInvocationId = null,
    string? WorkStepId = null,
    string? WorkPlanVersion = null,
    ContextProjectionSnapshot? ContextProjection = null);

/// <summary>Immutable context-projection decision captured for one Invocation.</summary>
public sealed record ContextProjectionSnapshot(
    string Strategy,
    string[] RetainedItems,
    int DroppedMessageCount,
    int IncludedMessageCount,
    int EstimatedTokens,
    string EstimateSource,
    int TokenLimit,
    int SummarizedUnitCount = 0);
