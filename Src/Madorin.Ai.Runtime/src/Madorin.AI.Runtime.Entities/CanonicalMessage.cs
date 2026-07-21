namespace Madorin.AI.Runtime.Entities;

public sealed record CanonicalMessage(
    string MessageId,
    long Sequence,
    string InvocationId,
    string AgentId,
    string Role,
    string Content,
    DateTimeOffset Timestamp);
