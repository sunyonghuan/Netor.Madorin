namespace Madorin.AI.Runtime.Contracts;

public sealed record RuntimeError(
    string Code,
    string Message,
    bool IsRetryable,
    string DiagnosticId);
