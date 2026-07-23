using System.Text.Json.Serialization;
using Madorin.AI.Runtime.Contracts;
using Madorin.AI.Runtime.Tools.Abstractions;

namespace Madorin.AI.Runtime.Providers.Abstractions;

[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
[JsonSerializable(typeof(InvocationCompletedProviderEvent))]
[JsonSerializable(typeof(InvocationFailedProviderEvent))]
[JsonSerializable(typeof(ProviderCapabilities))]
[JsonSerializable(typeof(ProviderCapabilityProbeResult))]
[JsonSerializable(typeof(ProviderConfigurationField))]
[JsonSerializable(typeof(ProviderConfigurationSchema))]
[JsonSerializable(typeof(ProviderExtensionData))]
[JsonSerializable(typeof(ProviderExtensionData[]))]
[JsonSerializable(typeof(ProviderModel))]
[JsonSerializable(typeof(ProviderModel[]))]
[JsonSerializable(typeof(ReasoningDeltaProviderEvent))]
[JsonSerializable(typeof(ProjectionAdjustedProviderEvent))]
[JsonSerializable(typeof(RuntimeError))]
[JsonSerializable(typeof(RuntimeProviderEvent))]
[JsonSerializable(typeof(RuntimeProviderMessage))]
[JsonSerializable(typeof(RuntimeProviderRequest))]
[JsonSerializable(typeof(RuntimeStructuredOutput))]
[JsonSerializable(typeof(TextDeltaProviderEvent))]
[JsonSerializable(typeof(ToolCallCompleteProviderEvent))]
[JsonSerializable(typeof(ToolCallDeltaProviderEvent))]
[JsonSerializable(typeof(ToolDescriptor))]
[JsonSerializable(typeof(ToolDescriptor[]))]
[JsonSerializable(typeof(UsageUpdatedProviderEvent))]
[JsonSerializable(typeof(ProviderTokenEstimate))]
public sealed partial class RuntimeProviderJsonContext : JsonSerializerContext;
