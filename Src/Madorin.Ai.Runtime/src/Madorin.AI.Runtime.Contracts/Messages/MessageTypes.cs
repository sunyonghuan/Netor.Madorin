namespace Madorin.AI.Runtime.Contracts;

public static class MessageTypes
{
    public const string NewSessionRun = "run.new_session";
    public const string ExistingSessionRun = "run.existing_session";
    public const string RunCancel = "run.cancel";
    public const string RunList = "run.list";
    public const string RunQuery = "run.query";
    public const string RuntimeStatus = "runtime.status";
    public const string SessionGet = "session.get";
    public const string SessionList = "session.list";
    public const string SessionResume = "session.resume";
    public const string SessionMessagesList = "session.messages.list";
    public const string SessionSelectionUpdate = "session.selection.update";
    public const string SessionRehydrate = "session.rehydrate";
    public const string RunAccepted = "run.accepted";
    public const string InvocationStarted = "invocation.started";
    public const string InvocationCompleted = "invocation.completed";
    public const string InvocationFailed = "invocation.failed";
    public const string TextDelta = "output.delta";
    public const string ReasoningDelta = "reasoning.delta";
    public const string ToolCallDelta = "tool.call.delta";
    public const string ToolCallCompleted = "tool.call.completed";
    public const string UsageUpdated = "usage.updated";
    public const string ContextProjectionAdjusted = "context.projection.adjusted";
    public const string ProviderProjectionAdjusted = "provider.projection.adjusted";
    public const string RunCompleted = "run.completed";
    public const string RunFailed = "run.failed";
    public const string RunCancelled = "run.cancelled";
    public const string DeltaDropped = "delta.dropped";
    public const string RunStatusChanged = "run.stage.changed";
    public const string CredentialsRefreshRequested = "credentials.refresh.request";
    public const string CredentialsUpdate = "credentials.update";
    public const string ToolCatalogReplace = "tool.catalog.replace";
    public const string ToolCatalogPatch = "tool.catalog.patch";
    public const string ToolPermissionRequest = "tool.permission.request";
    public const string ToolPermissionResponse = "tool.permission.response";
    public const string ApprovalRequest = "approval.request";
    public const string ApprovalResponse = "approval.response";
    public const string ToolCallRequest = "tool.call.request";
    public const string ToolCallResponse = "tool.call.response";
    public const string ToolCallCancel = "tool.call.cancel";
    public const string ToolResultQuery = "tool.result.query";
    public const string GrantRevoke = "grant.revoke";
    public const string MeetingHitlRequest = "meeting.hitl.request";
    public const string MeetingHitlResponse = "meeting.hitl.response";
    public const string MeetingSelectionUpdate = "meeting.selection.update";
}
