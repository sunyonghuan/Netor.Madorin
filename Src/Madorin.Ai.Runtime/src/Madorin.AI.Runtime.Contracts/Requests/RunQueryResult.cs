using Madorin.AI.Runtime.Entities;

namespace Madorin.AI.Runtime.Contracts;

public sealed record RunQueryResult(
    string RunId,
    string SessionId,
    RunStatus Status,
    string? TerminalText);
