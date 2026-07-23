using System.Text.Json;

namespace Madorin.AI.Runtime.Persistence.Files;

/// <summary>Represents one version-1 canonical JSONL conversation record.</summary>
public sealed record ConversationRecordV1(
    string MessageId,
    long Sequence,
    string InvocationId,
    string AgentId,
    string Role,
    JsonElement Content,
    DateTimeOffset Timestamp,
    string? UsageReference = null,
    JsonElement? SummaryMetadata = null);
