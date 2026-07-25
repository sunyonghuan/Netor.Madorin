using System.Text.Json;

namespace Madorin.AI.Runtime.Persistence.Files;

/// <summary>Contains one consistent canonical-history snapshot for projection.</summary>
public sealed record ConversationCompactionSource(
    IReadOnlyList<ConversationRecordV1> Records,
    string HistorySha256);

/// <summary>Stores one provider-independent projected message.</summary>
public sealed record ConversationCompactMessageV1(
    string Role,
    JsonElement Content);

/// <summary>Defines the rebuildable V1 ContextProjection cache.</summary>
public sealed record ConversationCompactCacheV1(
    string Schema,
    string SessionId,
    string SourceHistorySha256,
    string Strategy,
    string ProviderId,
    string ModelId,
    int? KeepLastTokens,
    int SourceMessageCount,
    int DroppedMessageCount,
    int BeforeEstimatedTokens,
    int AfterEstimatedTokens,
    string EstimateSource,
    ConversationCompactMessageV1[] ProjectionMessages,
    DateTimeOffset CreatedAt)
{
    public const string CurrentSchema = "madorin.conversation.compact.v1";
}
