using Madorin.AI.Runtime.Entities;

namespace Madorin.AI.Runtime.Persistence.Abstractions;

public interface ISessionRepository
{
    public ValueTask<SessionDescriptor?> GetAsync(
        string sessionId,
        CancellationToken cancellationToken = default);

    public ValueTask UpsertAsync(
        SessionDescriptor session,
        CancellationToken cancellationToken = default);
}
