namespace Madorin.AI.Runtime.Server;

/// <summary>Describes canonical history and the effective context projection for one Session.</summary>
public sealed record SessionContextStatus(
    string SessionId,
    int CanonicalMessageCount,
    int CanonicalEstimatedTokens,
    int ProjectionMessageCount,
    int ProjectionEstimatedTokens,
    string ProjectionStrategy,
    bool CacheCurrent,
    DateTimeOffset? CacheWrittenAt);
