namespace Netor.Cortana.Store.Models;

public sealed record PlatformConnectionTestResult(
    bool Succeeded,
    string Message,
    PlatformConnectionFailureKind FailureKind = PlatformConnectionFailureKind.None);

public enum PlatformConnectionFailureKind
{
    None,
    InvalidUrl,
    LocalServiceUnavailable,
    Unreachable,
    SslFailure,
    UnexpectedStatusCode,
    MissingHealthEndpoint
}
