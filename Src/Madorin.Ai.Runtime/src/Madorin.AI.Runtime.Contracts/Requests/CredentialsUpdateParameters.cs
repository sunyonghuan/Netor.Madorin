namespace Madorin.AI.Runtime.Contracts;

/// <summary>Credentials supplied by the authenticated Host for one active Run.</summary>
public sealed record CredentialsUpdateParameters(
    string RunId,
    string ProviderId,
    string Credential,
    string? ProviderProfileId = null,
    DateTimeOffset? ExpiresAt = null);
