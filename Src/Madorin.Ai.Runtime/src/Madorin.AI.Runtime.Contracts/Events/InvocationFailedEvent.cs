namespace Madorin.AI.Runtime.Contracts;

public sealed record InvocationFailedEvent(
    string InvocationId,
    RuntimeError Error);
