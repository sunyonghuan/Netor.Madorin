namespace Madorin.AI.Runtime.Contracts;

public sealed record ContextProjectionAdjustedEvent(
    string InvocationId,
    int DroppedMessageCount,
    int IncludedMessageCount,
    int EstimatedTokens,
    string EstimateSource,
    string Strategy);
