namespace Madorin.AI.Runtime.Contracts;

public static class RuntimeErrorCodes
{
    public const string ProtocolNoIntersection = nameof(ProtocolNoIntersection);
    public const string AuthenticationFailed = nameof(AuthenticationFailed);
    public const string CapabilityNotSupported = nameof(CapabilityNotSupported);
    public const string CapabilityInsufficient = nameof(CapabilityInsufficient);
    public const string ProviderProtocolError = nameof(ProviderProtocolError);
    public const string ProviderRateLimited = nameof(ProviderRateLimited);
    public const string ProviderRequestFailed = nameof(ProviderRequestFailed);
    public const string ProviderTimeout = nameof(ProviderTimeout);
    public const string LimitsIncompatible = nameof(LimitsIncompatible);
    public const string WorkspaceMismatch = nameof(WorkspaceMismatch);
    public const string RunBusy = nameof(RunBusy);
    public const string RunTimedOut = nameof(RunTimedOut);
    public const string SessionNotFound = nameof(SessionNotFound);
    public const string SelectionConflict = nameof(SelectionConflict);
    public const string RehydrateHashMismatch = nameof(RehydrateHashMismatch);
    public const string InstanceMismatch = nameof(InstanceMismatch);
    public const string ToolNotFound = nameof(ToolNotFound);
    public const string ToolArgumentsInvalid = nameof(ToolArgumentsInvalid);
    public const string ToolPermissionRequired = nameof(ToolPermissionRequired);
    public const string ToolApprovalRequired = nameof(ToolApprovalRequired);
    public const string ToolExecutorUnavailable = nameof(ToolExecutorUnavailable);
    public const string ToolAuditWriteFailed = nameof(ToolAuditWriteFailed);
    public const string ToolExecutionFailed = nameof(ToolExecutionFailed);
    public const string ToolExecutionCancelled = nameof(ToolExecutionCancelled);
    public const string ToolExecutionTimedOut = nameof(ToolExecutionTimedOut);
    public const string ToolResultInvalid = nameof(ToolResultInvalid);
    public const string ToolResultUnknown = nameof(ToolResultUnknown);
    public const string ToolGrantRevoked = nameof(ToolGrantRevoked);
    public const string ToolCallConflict = nameof(ToolCallConflict);
    public const string ToolCallInProgress = nameof(ToolCallInProgress);
    public const string ToolIntentInvalid = nameof(ToolIntentInvalid);
}
