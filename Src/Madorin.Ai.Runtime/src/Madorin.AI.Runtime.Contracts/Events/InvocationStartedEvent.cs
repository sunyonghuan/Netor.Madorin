using Madorin.AI.Runtime.Entities;

namespace Madorin.AI.Runtime.Contracts;

public sealed record InvocationStartedEvent(
    string InvocationId,
    InvocationSnapshot Snapshot);
