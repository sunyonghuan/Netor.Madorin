namespace Madorin.AI.Runtime.Contracts;

/// <summary>Requests that the Host refresh credentials without exposing the credential value.</summary>
public sealed record CredentialsRefreshRequestedEvent(
    string RunId,
    string ProviderId,
    string Reason);
