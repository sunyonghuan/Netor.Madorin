using Madorin.AI.Runtime.Entities;

namespace Madorin.AI.Runtime.Persistence.Abstractions;

public interface IConversationStore
{
    public ValueTask AppendAsync(
        string sessionId,
        CanonicalMessage message,
        CancellationToken cancellationToken = default);

    public IAsyncEnumerable<CanonicalMessage> ReadAsync(
        string sessionId,
        CancellationToken cancellationToken = default);
}
