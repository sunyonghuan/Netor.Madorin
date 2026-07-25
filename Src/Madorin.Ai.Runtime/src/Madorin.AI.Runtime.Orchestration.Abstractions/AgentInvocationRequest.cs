using Madorin.AI.Runtime.Contracts;
using Madorin.AI.Runtime.Entities;
using Madorin.AI.Runtime.Providers.Abstractions;
using Madorin.AI.Runtime.Tools.Abstractions;

namespace Madorin.AI.Runtime.Orchestration.Abstractions;

public sealed record AgentInvocationRequest(
    string InvocationId,
    string RunId,
    string SessionId,
    string AgentId,
    string ProviderId,
    string ModelId,
    RuntimeProviderMessage[] Messages,
    InvocationSnapshot Snapshot,
    ToolDescriptor[]? AvailableTools = null,
    RuntimeStructuredOutput? StructuredOutput = null,
    bool IsIdempotent = true,
    bool HasIrreversibleToolSideEffects = false,
    string? ParentAgentId = null);
