namespace Madorin.AI.Runtime.Contracts;

public sealed record RunFailedEvent(
    string RunId,
    string SessionId,
    RuntimeError Error);
