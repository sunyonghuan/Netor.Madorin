namespace Madorin.AI.Runtime.Server.Auth;

public sealed record HandshakeResult(
    string SessionKey,
    string HostInstanceId,
    string RuntimeInstanceId);
