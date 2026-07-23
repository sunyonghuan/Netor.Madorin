namespace Madorin.AI.Runtime.Providers.Abstractions;

/// <summary>Provides a shared connection pool for standalone Provider adapters.</summary>
public sealed class ProviderHttpClientFactory : IHttpClientFactory, IDisposable
{
    private readonly SocketsHttpHandler _handler = new()
    {
        PooledConnectionLifetime = TimeSpan.FromMinutes(10),
        AutomaticDecompression = System.Net.DecompressionMethods.All
    };

    public static IHttpClientFactory Shared { get; } = new ProviderHttpClientFactory();

    public HttpClient CreateClient(string name)
    {
        ArgumentNullException.ThrowIfNull(name);
        return new HttpClient(_handler, disposeHandler: false)
        {
            Timeout = Timeout.InfiniteTimeSpan
        };
    }

    public void Dispose()
    {
        _handler.Dispose();
    }
}
