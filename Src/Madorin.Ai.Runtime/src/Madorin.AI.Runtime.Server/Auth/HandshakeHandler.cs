using Madorin.AI.Runtime.Transport.NamedPipes;

namespace Madorin.AI.Runtime.Server.Auth;

/// <summary>Implements the V1 challenge/response primitives without transporting the shared secret.</summary>
public sealed class HandshakeHandler
{
    public static byte[] CreateNonce() => HandshakeProtocol.CreateNonce();

    public static string ComputeRuntimeResponse(
        string secret,
        string runtimeInstanceId,
        ReadOnlySpan<byte> nonceC,
        ReadOnlySpan<byte> nonceS)
        => HandshakeProtocol.ComputeRuntimeResponse(
            secret,
            runtimeInstanceId,
            nonceC,
            nonceS);

    public static string ComputeRuntimeResponse(
        string secret,
        string runtimeInstanceId,
        long timestampUnixMilliseconds,
        ReadOnlySpan<byte> nonceC,
        ReadOnlySpan<byte> nonceS)
        => HandshakeProtocol.ComputeRuntimeResponse(
            secret,
            runtimeInstanceId,
            timestampUnixMilliseconds,
            nonceC,
            nonceS);

    public static bool VerifyRuntimeResponse(
        string secret,
        string runtimeInstanceId,
        ReadOnlySpan<byte> nonceC,
        ReadOnlySpan<byte> nonceS,
        string response)
        => HandshakeProtocol.VerifyRuntimeResponse(
            secret,
            runtimeInstanceId,
            nonceC,
            nonceS,
            response);

    public static bool VerifyRuntimeResponse(
        string secret,
        string runtimeInstanceId,
        long timestampUnixMilliseconds,
        ReadOnlySpan<byte> nonceC,
        ReadOnlySpan<byte> nonceS,
        string response)
        => HandshakeProtocol.VerifyRuntimeResponse(
            secret,
            runtimeInstanceId,
            timestampUnixMilliseconds,
            nonceC,
            nonceS,
            response);

    public static HandshakeResult Complete(
        string secret,
        string hostInstanceId,
        string runtimeInstanceId,
        ReadOnlySpan<byte> nonceC,
        ReadOnlySpan<byte> nonceS)
    {
        var sessionKey = DeriveSessionKey(secret, hostInstanceId, runtimeInstanceId, nonceC, nonceS);
        return new HandshakeResult(sessionKey, hostInstanceId, runtimeInstanceId);
    }

    public static string DeriveSessionKey(
        string secret,
        string hostInstanceId,
        string runtimeInstanceId,
        ReadOnlySpan<byte> nonceC,
        ReadOnlySpan<byte> nonceS)
        => HandshakeProtocol.DeriveSessionKey(
            secret,
            hostInstanceId,
            runtimeInstanceId,
            nonceC,
            nonceS);

}
