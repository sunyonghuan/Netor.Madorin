namespace Madorin.AI.Runtime.Contracts;

public sealed record RuntimeVersionInfo(
    string Product,
    string Version,
    string ProtocolVersion,
    string Framework,
    string Platform,
    string Architecture);
