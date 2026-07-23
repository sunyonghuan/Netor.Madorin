namespace Madorin.AI.Runtime.Entities;

/// <summary>Represents the persisted query view of a Runtime Run.</summary>
public sealed record RunSnapshot(
    string RunId,
    string SessionId,
    RunStatus Status,
    string? TerminalText);
