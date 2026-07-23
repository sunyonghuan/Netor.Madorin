namespace Madorin.AI.Runtime.Contracts;

public sealed record HandshakeClientHello(
    string HostInstanceId,
    string NonceC,
    long TimestampUnixMilliseconds = 0);

public sealed record HandshakeServerHello(
    string RuntimeInstanceId,
    string NonceS,
    string RuntimeResponse);

public sealed record HandshakeClientConfirmation(
    string HostInstanceId,
    string Proof);

public sealed record EventChannelHello(
    string HostInstanceId,
    string Proof);
