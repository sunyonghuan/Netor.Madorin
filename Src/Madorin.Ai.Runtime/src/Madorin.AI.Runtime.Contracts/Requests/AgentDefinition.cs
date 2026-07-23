namespace Madorin.AI.Runtime.Contracts;

public sealed record AgentDefinition(
    AgentRef AgentRef,
    string? ParticipantId = null);
