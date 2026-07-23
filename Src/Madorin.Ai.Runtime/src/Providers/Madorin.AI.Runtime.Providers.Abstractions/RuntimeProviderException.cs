using Madorin.AI.Runtime.Contracts;

namespace Madorin.AI.Runtime.Providers.Abstractions;

public sealed class RuntimeProviderException(RuntimeError error) : Exception(error.Message)
{
    public RuntimeError Error { get; } = error;
}
