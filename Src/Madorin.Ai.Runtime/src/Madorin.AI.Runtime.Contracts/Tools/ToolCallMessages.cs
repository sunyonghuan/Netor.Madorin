using System.Text.Json;

namespace Madorin.AI.Runtime.Contracts;

/// <summary>Requests execution of one host or MCP tool.</summary>
public sealed record ToolCallRequest(
    string CorrelationId,
    string CallId,
    string RunId,
    string SessionId,
    string InvocationId,
    string AgentId,
    string ToolId,
    string ToolCatalogVersion,
    JsonElement Arguments,
    int TimeoutMilliseconds,
    string GrantId);

/// <summary>Returns a terminal or non-terminal host tool state.</summary>
public sealed record ToolCallResponse(
    string CorrelationId,
    string CallId,
    ToolCallStatus Status,
    JsonElement? Result = null,
    string? ResultHash = null,
    BlobReference? ResultBlob = null,
    string? ErrorCode = null,
    string? ErrorMessage = null,
    string? DiagnosticId = null);

/// <summary>Requests cancellation of an in-flight host tool call.</summary>
public sealed record ToolCallCancelRequest(
    string CorrelationId,
    string CallId,
    string? Reason = null);

/// <summary>Queries a durable host result before deciding whether a sent call can be retried.</summary>
public sealed record ToolResultQueryRequest(
    string CorrelationId,
    string CallId);

/// <summary>Returns the host's durable knowledge for a previously sent call.</summary>
public sealed record ToolResultQueryResponse(
    string CorrelationId,
    string CallId,
    ToolCallStatus Status,
    JsonElement? Result = null,
    string? ResultHash = null,
    BlobReference? ResultBlob = null,
    string? ErrorCode = null,
    string? ErrorMessage = null);
