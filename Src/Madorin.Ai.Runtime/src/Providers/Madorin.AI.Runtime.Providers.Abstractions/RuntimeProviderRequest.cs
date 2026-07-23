using System.Text.Json;
using System.Text.Json.Serialization;
using Madorin.AI.Runtime.Contracts;
using Madorin.AI.Runtime.Tools.Abstractions;

namespace Madorin.AI.Runtime.Providers.Abstractions;

public sealed record RuntimeProviderRequest(
    string InvocationId,
    string AgentId,
    string ProviderId,
    string ModelId,
    RuntimeProviderMessage[] Messages,
    ToolDescriptor[]? Tools = null,
    float? Temperature = null,
    int? MaxTokens = null,
    RuntimeStructuredOutput? StructuredOutput = null,
    string? InternalRequestId = null,
    int AttemptNumber = 0,
    bool IsIdempotent = true,
    bool HasIrreversibleToolSideEffects = false,
    [property: JsonIgnore] CancellationToken CancellationToken = default)
{
    public RuntimeProviderRequest CreateAttempt(int attemptNumber)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(attemptNumber);

        if (attemptNumber > 0 && (!IsIdempotent || HasIrreversibleToolSideEffects))
        {
            throw new InvalidOperationException(
                "The Provider request cannot be retried after irreversible or non-idempotent work.");
        }

        return this with
        {
            InternalRequestId = Guid.NewGuid().ToString("N"),
            AttemptNumber = attemptNumber
        };
    }
}

/// <summary>Describes a provider-independent JSON response schema.</summary>
public sealed record RuntimeStructuredOutput(
    string Name,
    JsonElement Schema,
    string? Description = null);
