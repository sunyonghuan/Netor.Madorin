using Madorin.AI.Runtime.Entities;

namespace Madorin.AI.Runtime.Contracts;

public sealed record NewSessionRunRequest(
    string SessionIdempotencyKey,
    string RunIdempotencyKey,
    RuntimeMode Mode,
    NextTurnSelection Selection,
    ContentBlock[] InitialInput,
    string? WorkspaceId = null);
