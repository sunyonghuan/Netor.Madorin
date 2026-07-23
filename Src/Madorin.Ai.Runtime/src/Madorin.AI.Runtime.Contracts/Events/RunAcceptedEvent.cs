namespace Madorin.AI.Runtime.Contracts;

public sealed record RunAcceptedEvent(
    string SessionId,
    string RunId);
