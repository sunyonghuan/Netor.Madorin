namespace Madorin.AI.Runtime.Contracts;

public sealed record SessionRehydrateParameters(
    string SessionId,
    AgentDefinition[] AgentDefinitions);
