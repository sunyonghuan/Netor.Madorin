namespace Madorin.AI.Runtime.Providers.Abstractions;

/// <summary>Describes configuration accepted by a Provider adapter without exposing SDK types.</summary>
public sealed record ProviderConfigurationSchema(
    string ProviderId,
    string Version,
    IReadOnlyList<ProviderConfigurationField> Fields)
{
    public static ProviderConfigurationSchema ForProvider(string providerId) =>
        new(
            providerId,
            "1",
            [
                new("apiKey", ProviderConfigurationValueKind.Secret, IsRequired: true),
                new("baseUrl", ProviderConfigurationValueKind.Uri, IsRequired: true),
                new("models", ProviderConfigurationValueKind.TextArray, IsRequired: false)
            ]);
}

public sealed record ProviderConfigurationField(
    string Name,
    ProviderConfigurationValueKind Kind,
    bool IsRequired,
    string? Description = null);

public enum ProviderConfigurationValueKind
{
    Text,
    Secret,
    Uri,
    Boolean,
    WholeNumber,
    TextArray
}
