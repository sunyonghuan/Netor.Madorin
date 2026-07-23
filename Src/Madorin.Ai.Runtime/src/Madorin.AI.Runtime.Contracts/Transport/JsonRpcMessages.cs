using System.Text.Json;
using System.Text.Json.Serialization;

namespace Madorin.AI.Runtime.Contracts;

public sealed record JsonRpcRequest(
    [property: JsonPropertyName("jsonrpc")]
    string JsonRpc,
    long Id,
    string Method,
    JsonElement? Params = null);

public sealed record JsonRpcResponse(
    [property: JsonPropertyName("jsonrpc")]
    string JsonRpc,
    long Id,
    JsonElement? Result = null,
    JsonRpcError? Error = null);

public sealed record JsonRpcError(
    int Code,
    string Message,
    JsonElement? Data = null);

public sealed record RunCancelParameters(string RunId);
