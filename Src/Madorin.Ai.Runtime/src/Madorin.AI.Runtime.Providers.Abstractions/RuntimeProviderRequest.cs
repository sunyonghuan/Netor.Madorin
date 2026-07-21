namespace Madorin.AI.Runtime.Providers.Abstractions;

public sealed record RuntimeProviderRequest(
    string ModelId,
    IReadOnlyList<RuntimeProviderMessage> Messages);
