namespace Madorin.AI.Runtime.Providers.Abstractions;

/// <summary>Allows Provider policies to wrap an existing managed <see cref="HttpClient"/>.</summary>
public sealed class HttpClientForwardingHandler : HttpMessageHandler
{
    private readonly Uri? _baseAddress;
    private readonly bool _disposeHttpClient;
    private readonly HttpClient _httpClient;

    public HttpClientForwardingHandler(
        HttpClient httpClient,
        bool disposeHttpClient = false,
        Uri? baseAddress = null)
    {
        ArgumentNullException.ThrowIfNull(httpClient);
        _httpClient = httpClient;
        _disposeHttpClient = disposeHttpClient;
        _baseAddress = baseAddress ?? httpClient.BaseAddress;
    }

    protected override async Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request,
        CancellationToken cancellationToken)
    {
        using var forwardedRequest = await CloneRequestAsync(request, cancellationToken)
            .ConfigureAwait(false);
        if (forwardedRequest.RequestUri is { IsAbsoluteUri: false } relativeUri)
        {
            forwardedRequest.RequestUri = _baseAddress is null
                ? throw new InvalidOperationException("A base address is required for relative Provider requests.")
                : new Uri(_baseAddress, relativeUri);
        }

        return await _httpClient.SendAsync(
            forwardedRequest,
            HttpCompletionOption.ResponseHeadersRead,
            cancellationToken).ConfigureAwait(false);
    }

    private static async Task<HttpRequestMessage> CloneRequestAsync(
        HttpRequestMessage request,
        CancellationToken cancellationToken)
    {
        var clone = new HttpRequestMessage(request.Method, request.RequestUri)
        {
            Version = request.Version,
            VersionPolicy = request.VersionPolicy
        };
        foreach (var header in request.Headers)
        {
            clone.Headers.TryAddWithoutValidation(header.Key, header.Value);
        }

        if (request.Content is not null)
        {
            var content = new ByteArrayContent(
                await request.Content.ReadAsByteArrayAsync(cancellationToken).ConfigureAwait(false));
            foreach (var header in request.Content.Headers)
            {
                content.Headers.TryAddWithoutValidation(header.Key, header.Value);
            }

            clone.Content = content;
        }

        return clone;
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing && _disposeHttpClient)
        {
            _httpClient.Dispose();
        }

        base.Dispose(disposing);
    }
}
