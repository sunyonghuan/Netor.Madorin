using System.Text.Json.Serialization;
using Madorin.AI.Runtime.Contracts;

namespace Madorin.AI.Runtime.Providers.Abstractions;

[JsonPolymorphic(TypeDiscriminatorPropertyName = "type")]
[JsonDerivedType(typeof(TextDeltaProviderEvent), "text_delta")]
[JsonDerivedType(typeof(ReasoningDeltaProviderEvent), "reasoning_delta")]
[JsonDerivedType(typeof(ToolCallDeltaProviderEvent), "tool_call_delta")]
[JsonDerivedType(typeof(ToolCallCompleteProviderEvent), "tool_call_complete")]
[JsonDerivedType(typeof(UsageUpdatedProviderEvent), "usage_updated")]
[JsonDerivedType(typeof(InvocationCompletedProviderEvent), "invocation_completed")]
[JsonDerivedType(typeof(InvocationFailedProviderEvent), "invocation_failed")]
[JsonDerivedType(typeof(ProjectionAdjustedProviderEvent), "projection_adjusted")]
public abstract record RuntimeProviderEvent(string InvocationId);

public sealed record TextDeltaProviderEvent(
    string InvocationId,
    string Delta) : RuntimeProviderEvent(InvocationId);

public sealed record ReasoningDeltaProviderEvent(
    string InvocationId,
    string Delta,
    ProviderExtensionData[]? ProviderExtensions = null) : RuntimeProviderEvent(InvocationId);

public sealed record ToolCallDeltaProviderEvent(
    string InvocationId,
    string CallId,
    string Name,
    string ArgumentsDelta) : RuntimeProviderEvent(InvocationId);

public sealed record ToolCallCompleteProviderEvent(
    string InvocationId,
    string CallId,
    string ToolId,
    string Name,
    string ArgumentsJson) : RuntimeProviderEvent(InvocationId);

public sealed record UsageUpdatedProviderEvent(
    string InvocationId,
    int? InputTokens,
    int? OutputTokens,
    ProviderUsageAccuracy Accuracy = ProviderUsageAccuracy.Exact) : RuntimeProviderEvent(InvocationId);

public sealed record ProjectionAdjustedProviderEvent(
    string InvocationId,
    string Code,
    string ExtensionNamespace,
    string ExtensionName) : RuntimeProviderEvent(InvocationId);

public sealed record InvocationCompletedProviderEvent(
    string InvocationId,
    string FinishReason) : RuntimeProviderEvent(InvocationId);

public sealed record InvocationFailedProviderEvent(
    string InvocationId,
    RuntimeError Error) : RuntimeProviderEvent(InvocationId);
