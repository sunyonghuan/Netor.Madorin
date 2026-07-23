namespace Madorin.AI.Runtime.Contracts;

public sealed record ExistingSessionRunRequest(
    string SessionId,
    string RunIdempotencyKey,
    NextTurnSelection? TurnOverride = null,
    ContentBlock[]? InputOverride = null,
    int? ExpectedSelectionVersion = null);
