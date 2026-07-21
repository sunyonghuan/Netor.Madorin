using Madorin.AI.Runtime.Entities;
using Madorin.AI.Runtime.Providers.Abstractions;

namespace Madorin.AI.Runtime.Orchestration.Abstractions;

public sealed record AgentInvocationRequest(
    string InvocationId,
    string RunId,
    string SessionId,
    string AgentId,
    RuntimeMode Mode,
    RuntimeProviderRequest ProviderRequest);
