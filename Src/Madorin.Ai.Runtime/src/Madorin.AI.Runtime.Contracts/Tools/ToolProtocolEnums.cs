namespace Madorin.AI.Runtime.Contracts;

/// <summary>Identifies where a catalog tool is executed.</summary>
public enum ToolExecutionTarget
{
    Builtin,
    Host,
    Mcp
}

/// <summary>Classifies the highest risk represented by a tool definition or grant.</summary>
public enum ToolRiskLevel
{
    Low,
    SensitiveRead,
    Write,
    Destructive,
    Process,
    PowerShell,
    Network
}

/// <summary>Represents a permission or approval decision.</summary>
public enum ToolAuthorizationDecision
{
    Granted,
    Denied,
    NeedsApproval
}

/// <summary>Represents the durable execution state reported by a host tool executor.</summary>
public enum ToolCallStatus
{
    Pending,
    Running,
    Succeeded,
    Failed,
    Cancelled,
    Unknown
}
