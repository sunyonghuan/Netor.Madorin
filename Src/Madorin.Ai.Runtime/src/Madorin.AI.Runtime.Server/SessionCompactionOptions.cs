namespace Madorin.AI.Runtime.Server;

/// <summary>Identifies the projection strategy used by manual Session compaction.</summary>
public enum SessionCompactionStrategy
{
    Full,
    SlidingWindow,
    Summary
}

/// <summary>Controls one manual Session compaction operation.</summary>
public sealed record SessionCompactionOptions(
    SessionCompactionStrategy Strategy = SessionCompactionStrategy.Summary,
    string? ProviderId = null,
    string? ModelId = null,
    int? KeepLastTokens = null,
    bool DryRun = false,
    bool Force = false)
{
    public const int DefaultKeepLastTokens = 4096;
}
