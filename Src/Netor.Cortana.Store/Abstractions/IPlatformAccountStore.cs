using Netor.Cortana.Store.Models;

namespace Netor.Cortana.Store.Abstractions;

public interface IPlatformAccountStore
{
    Task<PlatformAccountSession?> LoadAsync(CancellationToken cancellationToken = default);

    Task SaveAsync(PlatformAccountSession session, CancellationToken cancellationToken = default);

    Task ClearAsync(CancellationToken cancellationToken = default);
}
