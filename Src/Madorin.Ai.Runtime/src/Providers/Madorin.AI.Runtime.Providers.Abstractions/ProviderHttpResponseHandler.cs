namespace Madorin.AI.Runtime.Providers.Abstractions;

/// <summary>Converts non-success responses to redacted Provider HTTP exceptions.</summary>
public sealed class ProviderHttpResponseHandler(HttpMessageHandler innerHandler)
    : DelegatingHandler(innerHandler)
{
    protected override async Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request,
        CancellationToken cancellationToken)
    {
        var response = await base.SendAsync(request, cancellationToken).ConfigureAwait(false);
        if (response.IsSuccessStatusCode)
        {
            return response;
        }

        var retryAfter = response.Headers.TryGetValues("retry-after", out var retryAfterValues)
            ? retryAfterValues.FirstOrDefault()
            : null;
        var requestId = GetFirstHeader(response, "request-id")
            ?? GetFirstHeader(response, "x-request-id");
        var statusCode = response.StatusCode;
        response.Dispose();
        throw new ProviderHttpException(statusCode, retryAfter, requestId);
    }

    private static string? GetFirstHeader(HttpResponseMessage response, string name) =>
        response.Headers.TryGetValues(name, out var values) ? values.FirstOrDefault() : null;
}
