namespace Madorin.AI.Runtime.Contracts;

public sealed record AgentRef(
    string AgentId,
    string PromptTemplateVersion,
    string SystemPrompt,
    string? ProviderId = null,
    string? ModelId = null,
    string[]? AllowedToolIds = null,
    string[]? SkillIds = null);
