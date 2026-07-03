using System.Text.Json.Serialization;

namespace Netor.Cortana.AI.Delegation;

public sealed record DelegatedAgentTaskInput(
    string Task,
    string[] AttachmentPaths,
    string? OutputContract);

public sealed record StartAutonomousSubAgentTaskRequest(
    string ChildName,
    string ChildInstructions,
    string Task,
    string[] ToolMounts,
    string? ProviderId,
    string? ModelId,
    string[] AttachmentPaths,
    string? OutputContract);

public sealed record DelegatedAgentTaskStartResult(
    string JobId,
    string State,
    string ChildName,
    string Message);

public sealed record DelegatedAgentTaskStatusResult(
    string JobId,
    string State,
    string? ProgressDescription,
    DateTimeOffset UpdatedAt,
    string? Error = null);

public sealed record DelegatedAgentTaskResult(
    string JobId,
    string State,
    string? Result,
    string? ProgressDescription,
    string? Error);

public sealed record DelegatedAgentTaskCancelResult(
    string JobId,
    string State,
    string Message);

public sealed record DelegatedAgentErrorResult(string Error);

[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    WriteIndented = false)]
[JsonSerializable(typeof(SystemToolCatalogItem))]
[JsonSerializable(typeof(List<SystemToolCatalogItem>))]
[JsonSerializable(typeof(DelegatedAgentTaskInput))]
[JsonSerializable(typeof(StartAutonomousSubAgentTaskRequest))]
[JsonSerializable(typeof(DelegatedAgentTaskStartResult))]
[JsonSerializable(typeof(DelegatedAgentTaskStatusResult))]
[JsonSerializable(typeof(DelegatedAgentTaskResult))]
[JsonSerializable(typeof(DelegatedAgentTaskCancelResult))]
[JsonSerializable(typeof(DelegatedAgentErrorResult))]
[JsonSerializable(typeof(string[]))]
internal partial class DelegatedAgentJsonContext : JsonSerializerContext;
