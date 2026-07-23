using Madorin.AI.Runtime.Contracts;
using Madorin.AI.Runtime.Tools.Abstractions;

namespace Madorin.AI.Runtime.Services.Tools;

public sealed record ToolGatewayResult(
    ToolGatewayResultKind Kind,
    ToolResult? Result = null,
    RuntimeError? Error = null);
