using Madorin.AI.Runtime.Entities;

namespace Madorin.AI.Runtime.Contracts;

public sealed record RunListItem(
    string RunId,
    string SessionId,
    RunStatus Status,
    DateTimeOffset StartedAt);
