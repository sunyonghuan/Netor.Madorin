namespace Madorin.AI.Runtime.Contracts;

public sealed record RunStatusChangedEvent(
    string RunId,
    string Status);
