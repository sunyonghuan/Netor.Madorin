using System.Text.Json;

namespace Madorin.AI.Runtime.Contracts;

public sealed record RuntimeEventEnvelope(
    string RuntimeInstanceId,
    long Gsn,
    string RunId,
    long RunSequence,
    string MessageType,
    DateTimeOffset Timestamp,
    JsonElement Payload);
