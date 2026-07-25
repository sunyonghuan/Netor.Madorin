namespace Madorin.AI.Runtime.Server;

/// <summary>Describes the projection produced or planned by Session compaction.</summary>
public sealed record SessionCompactionResult(
    string SessionId,
    SessionCompactionStrategy Strategy,
    string ProviderId,
    string ModelId,
    int? KeepLastTokens,
    int SourceMessageCount,
    int ProjectedMessageCount,
    int DroppedMessageCount,
    int BeforeEstimatedTokens,
    int? AfterEstimatedTokens,
    string EstimateSource,
    bool DryRun,
    bool CacheHit,
    bool CacheWritten);
