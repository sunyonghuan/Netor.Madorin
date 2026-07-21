using Madorin.AI.Runtime.Entities;

namespace Madorin.AI.Runtime.Orchestration.Abstractions;

public interface IContextProjectionBuilder
{
    public ValueTask<IReadOnlyList<CanonicalMessage>> BuildAsync(
        IReadOnlyList<CanonicalMessage> canonicalHistory,
        CancellationToken cancellationToken = default);
}
