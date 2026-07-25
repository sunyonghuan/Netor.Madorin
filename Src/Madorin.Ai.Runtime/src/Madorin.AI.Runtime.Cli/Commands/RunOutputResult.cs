namespace Madorin.AI.Runtime.Cli.Commands;

internal sealed record RunOutputResult(
    int ExitCode,
    string? SessionId,
    string? RunId);
