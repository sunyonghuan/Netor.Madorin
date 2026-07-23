namespace Madorin.AI.Runtime.Contracts;

public sealed record RunCancelledEvent(
    string RunId,
    string SessionId);
