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
    string? WorkPlanVersion = null);
