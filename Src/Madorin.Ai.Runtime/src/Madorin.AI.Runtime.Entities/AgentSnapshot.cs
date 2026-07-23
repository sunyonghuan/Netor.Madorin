namespace Madorin.AI.Runtime.Entities;

public sealed record AgentSnapshot(
    string AgentId,
    string PromptTemplateVersion,
    string PromptHash,
    string? ProviderId,
    string? ModelId);
