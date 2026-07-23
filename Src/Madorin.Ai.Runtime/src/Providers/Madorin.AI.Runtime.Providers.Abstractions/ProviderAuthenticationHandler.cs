using System.Net.Http.Headers;

namespace Madorin.AI.Runtime.Providers.Abstractions;

/// <summary>Applies an updateable Provider credential without recreating the HTTP pipeline.</summary>
public sealed class ProviderAuthenticationHandler : DelegatingHandler
{
    private readonly string _headerName;
    private readonly string? _scheme;
    private string _credential;

    public ProviderAuthenticationHandler(
        string headerName,
        string? scheme,
        string credential,
        HttpMessageHandler innerHandler)
        : base(innerHandler)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(headerName);
        ArgumentException.ThrowIfNullOrWhiteSpace(credential);
        _headerName = headerName;
        _scheme = scheme;
        _credential = credential;
    }

    public void Update(string credential)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(credential);
        Volatile.Write(ref _credential, credential);
    }

    protected override Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request,
        CancellationToken cancellationToken)
    {
        var credential = Volatile.Read(ref _credential);
        request.Headers.Remove(_headerName);
        if (string.Equals(_headerName, "Authorization", StringComparison.OrdinalIgnoreCase))
        {
            request.Headers.Authorization = new AuthenticationHeaderValue(_scheme ?? "Bearer", credential);
        }
        else
        {
            request.Headers.Remove("Authorization");
            var value = _scheme is null ? credential : $"{_scheme} {credential}";
            request.Headers.TryAddWithoutValidation(_headerName, value);
        }

        return base.SendAsync(request, cancellationToken);
    }
}
