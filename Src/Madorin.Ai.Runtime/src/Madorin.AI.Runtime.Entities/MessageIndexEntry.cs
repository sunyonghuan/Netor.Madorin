namespace Madorin.AI.Runtime.Entities;

public sealed record MessageIndexEntry(
    string MessageId,
    string SessionId,
    long Sequence,
    string InvocationId,
    string AgentId,
    string Role,
    long FileOffset,
    long RecordLength,
    DateTimeOffset CreatedAt);
