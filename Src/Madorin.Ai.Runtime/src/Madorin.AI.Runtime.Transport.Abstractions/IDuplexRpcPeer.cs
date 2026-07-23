using System.Text.Json;
using Madorin.AI.Runtime.Contracts;

namespace Madorin.AI.Runtime.Transport.Abstractions;

/// <summary>A full-duplex JSON-RPC peer with one receive owner and correlated requests.</summary>
public interface IDuplexRpcPeer : IControlChannel
{
    /// <summary>Completes when the receive pump terminates.</summary>
    public Task Completion { get; }

    /// <summary>Registers the handler for requests initiated by the remote peer.</summary>
    public void SetRequestHandler(
        Func<JsonRpcRequest, CancellationToken, ValueTask<JsonRpcResponse>> handler);

    /// <summary>Sends a typed JSON-RPC request.</summary>
    public ValueTask<JsonRpcResponse> SendRequestAsync(
        string method,
        JsonElement? parameters = null,
        CancellationToken ct = default);

    /// <summary>Sends a typed JSON-RPC request with an explicit timeout.</summary>
    public ValueTask<JsonRpcResponse> SendRequestAsync(
        string method,
        JsonElement? parameters,
        TimeSpan timeout,
        CancellationToken ct = default);
}
