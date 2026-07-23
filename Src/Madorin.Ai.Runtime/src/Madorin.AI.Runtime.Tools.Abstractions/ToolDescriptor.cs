using Madorin.AI.Runtime.Contracts;

namespace Madorin.AI.Runtime.Tools.Abstractions;

public sealed record ToolDescriptor(
    string ToolId,
    string Namespace,
    string DisplayName,
    string Description,
    string InputSchemaJson,
    string OutputSchemaJson = "{}",
    string RiskLevel = "low",
    bool RequiresApproval = false,
    int TimeoutSeconds = 30,
    IReadOnlyList<string>? Tags = null,
    IReadOnlyList<string>? Capabilities = null,
    ToolRiskLevel Risk = ToolRiskLevel.Low,
    ToolExecutionTarget ExecutionTarget = ToolExecutionTarget.Builtin,
    bool IsIdempotent = true);
