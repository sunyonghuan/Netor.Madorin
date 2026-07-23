namespace Madorin.AI.Runtime.Contracts;

public sealed record SessionRehydrateResult(
    string SessionId,
    string Status,
    string[] Mismatched);
