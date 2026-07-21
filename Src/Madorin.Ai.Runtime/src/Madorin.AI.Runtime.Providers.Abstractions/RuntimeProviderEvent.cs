namespace Madorin.AI.Runtime.Providers.Abstractions;

public sealed record RuntimeProviderEvent(
    string Type,
    string? Text,
    long? InputTokens,
    long? OutputTokens);
