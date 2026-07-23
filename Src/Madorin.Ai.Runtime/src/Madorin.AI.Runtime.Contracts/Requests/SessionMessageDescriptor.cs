namespace Madorin.AI.Runtime.Contracts;

public sealed record SessionMessageDescriptor(
    string MessageId,
    long Sequence,
    string InvocationId,
    string AgentId,
    string Role,
    DateTimeOffset CreatedAt);
