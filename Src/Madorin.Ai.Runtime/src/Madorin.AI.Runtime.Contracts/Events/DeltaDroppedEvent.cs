namespace Madorin.AI.Runtime.Contracts;

public sealed record DeltaDroppedEvent(
    string RunId,
    long FromRunSequence,
    long ToRunSequence);
