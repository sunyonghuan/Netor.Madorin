namespace Madorin.AI.Runtime.Contracts;

public sealed record RunCompletedEvent(
    string RunId,
    string SessionId);
