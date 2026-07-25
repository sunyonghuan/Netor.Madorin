using System.Text.Json.Serialization;

namespace Madorin.AI.Runtime.Persistence.Files;

[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
[JsonSerializable(typeof(ConversationHeader))]
[JsonSerializable(typeof(ConversationRecordV1))]
[JsonSerializable(typeof(ConversationCompactCacheV1))]
[JsonSerializable(typeof(SessionMaintenanceAuditRecord))]
internal sealed partial class ConversationJsonContext : JsonSerializerContext;

internal sealed record ConversationHeader(
    string Schema,
    string SessionId,
    string Mode,
    DateTimeOffset CreatedAt);
