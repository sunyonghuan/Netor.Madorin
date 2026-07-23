using System.Buffers;
using System.Globalization;
using System.Net;
using System.Text.Json;
using Madorin.AI.Runtime.Contracts;

namespace Madorin.AI.Runtime.Providers.Abstractions;

/// <summary>Creates stable, redacted Runtime errors from Provider failures.</summary>
public static class ProviderErrorFactory
{
    public static RuntimeError Create(
        string providerName,
        Exception exception,
        int? statusCode = null,
        string? retryAfter = null,
        string? requestId = null,
        bool isProtocolError = false)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(providerName);
        ArgumentNullException.ThrowIfNull(exception);

        var metadata = InspectExceptionChain(exception);
        statusCode ??= metadata.StatusCode;
        retryAfter ??= metadata.RetryAfter;
        requestId ??= metadata.RequestId;

        var isTimeout = metadata.IsTimeout;
        isProtocolError |= metadata.IsProtocolError;
        var code = statusCode switch
        {
            401 or 403 => RuntimeErrorCodes.AuthenticationFailed,
            429 => RuntimeErrorCodes.ProviderRateLimited,
            _ when isTimeout => RuntimeErrorCodes.ProviderTimeout,
            _ when isProtocolError || exception is JsonException => RuntimeErrorCodes.ProviderProtocolError,
            _ => RuntimeErrorCodes.ProviderRequestFailed
        };
        var category = statusCode switch
        {
            401 or 403 => "authentication",
            429 => "rate_limit",
            _ when isTimeout => "timeout",
            _ when isProtocolError || exception is JsonException => "protocol",
            _ => "provider"
        };
        var isRetryable = statusCode is 408 or 409 or 429 or >= 500
            || isTimeout
            || metadata.IsHttpFailure;

        return new RuntimeError(
            code,
            category,
            $"{providerName} request failed with code '{GetProviderCode(statusCode, exception)}'.",
            isRetryable,
            CreateDetails(statusCode, retryAfter, requestId),
            Guid.NewGuid().ToString("N"));
    }

    private static FailureMetadata InspectExceptionChain(Exception exception)
    {
        int? statusCode = null;
        string? retryAfter = null;
        string? requestId = null;
        var isHttpFailure = false;
        var isProtocolError = false;
        var isTimeout = false;

        for (Exception? current = exception; current is not null; current = current.InnerException)
        {
            if (current is ProviderHttpException providerHttpException)
            {
                statusCode ??= (int?)providerHttpException.StatusCode;
                retryAfter ??= providerHttpException.RetryAfter;
                requestId ??= providerHttpException.RequestId;
                isHttpFailure = true;
            }
            else if (current is HttpRequestException httpRequestException)
            {
                statusCode ??= (int?)httpRequestException.StatusCode;
                isHttpFailure = true;
            }

            isProtocolError |= current is JsonException;
            isTimeout |= current is TimeoutException
                or TaskCanceledException { CancellationToken.IsCancellationRequested: false };
        }

        return new FailureMetadata(
            statusCode,
            retryAfter,
            requestId,
            isHttpFailure,
            isProtocolError,
            isTimeout);
    }

    private static string GetProviderCode(int? statusCode, Exception exception) =>
        statusCode?.ToString(CultureInfo.InvariantCulture) ?? exception.GetType().Name;

    private static JsonElement? CreateDetails(
        int? statusCode,
        string? retryAfter,
        string? requestId)
    {
        if (statusCode is null && retryAfter is null && requestId is null)
        {
            return null;
        }

        var buffer = new ArrayBufferWriter<byte>();
        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartObject();
            if (statusCode is not null)
            {
                writer.WriteNumber("statusCode", statusCode.Value);
            }

            if (retryAfter is not null)
            {
                writer.WriteString("retryAfter", retryAfter);
            }

            if (requestId is not null)
            {
                writer.WriteString("requestId", requestId);
            }

            writer.WriteEndObject();
        }

        using var document = JsonDocument.Parse(buffer.WrittenMemory);
        return document.RootElement.Clone();
    }

    private sealed record FailureMetadata(
        int? StatusCode,
        string? RetryAfter,
        string? RequestId,
        bool IsHttpFailure,
        bool IsProtocolError,
        bool IsTimeout);
}
