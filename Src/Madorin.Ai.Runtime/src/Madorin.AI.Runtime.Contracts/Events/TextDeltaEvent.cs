namespace Madorin.AI.Runtime.Contracts;

public sealed record TextDeltaEvent(
    string InvocationId,
    string Delta);
