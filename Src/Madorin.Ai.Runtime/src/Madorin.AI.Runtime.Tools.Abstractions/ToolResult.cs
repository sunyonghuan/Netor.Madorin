using Madorin.AI.Runtime.Contracts;

namespace Madorin.AI.Runtime.Tools.Abstractions;

public sealed record ToolResult(
    string CallId,
    string ToolId,
    bool Success,
    string? OutputJson = "{}",
    string? Error = null,
    BlobReference? ResultBlob = null,
    string? ResultHash = null);
