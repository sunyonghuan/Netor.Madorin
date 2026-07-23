using System.Net;

namespace Madorin.AI.Runtime.Providers.Abstractions;

/// <summary>Represents sanitized HTTP failure metadata safe for Runtime diagnostics.</summary>
public sealed class ProviderHttpException : HttpRequestException
{
    public ProviderHttpException(
        HttpStatusCode statusCode,
        string? retryAfter = null,
        string? requestId = null,
        Exception? innerException = null)
        : base(
            $"Provider HTTP request failed with status code {(int)statusCode}.",
            innerException,
            statusCode)
    {
        RetryAfter = retryAfter;
        RequestId = requestId;
    }

    public string? RetryAfter { get; }

    public string? RequestId { get; }
}
