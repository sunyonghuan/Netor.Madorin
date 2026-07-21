namespace Madorin.AI.Runtime.Entities;

public sealed record RuntimeEventRecord(
    long GlobalSequence,
    string RunId,
    long RunSequence,
    string MessageType,
    string PayloadJson,
    DateTimeOffset CreatedAt);
