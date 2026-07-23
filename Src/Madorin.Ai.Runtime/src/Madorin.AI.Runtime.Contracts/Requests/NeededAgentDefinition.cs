namespace Madorin.AI.Runtime.Contracts;

public sealed record NeededAgentDefinition(
    string AgentId,
    string ResolvedPromptHash,
    string DefinitionType,
    string? ParticipantId = null);
