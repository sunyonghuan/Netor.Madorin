using Netor.Cortana.Store.Models;

namespace Netor.Cortana.Store.Abstractions;

public interface IPlatformConnectionTester
{
    Task<PlatformConnectionTestResult> TestAsync(string baseUrl, CancellationToken cancellationToken = default);
}
