using System.Text.Json;

namespace Madorin.AI.Runtime.Contracts;

public sealed record RuntimeEventEnvelope(
    long GlobalSequence,
    string RunId,
    long RunSequence,
    string MessageType,
    JsonElement Payload);
