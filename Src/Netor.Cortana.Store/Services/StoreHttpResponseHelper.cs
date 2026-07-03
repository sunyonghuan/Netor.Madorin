using System.Net;
using System.Text.Json;

namespace Netor.Cortana.Store.Services;

internal static class StoreHttpResponseHelper
{
    public static async Task EnsureSuccessAsync(
        HttpResponseMessage response,
        string operationName,
        CancellationToken cancellationToken)
    {
        if (response.IsSuccessStatusCode)
        {
            return;
        }

        var message = await ReadProblemMessageAsync(response, cancellationToken);
        if (string.IsNullOrWhiteSpace(message))
        {
            message = $"{operationName}失败：HTTP {(int)response.StatusCode} {response.ReasonPhrase}".TrimEnd();
        }

        throw new HttpRequestException(message, null, response.StatusCode);
    }

    private static async Task<string?> ReadProblemMessageAsync(
        HttpResponseMessage response,
        CancellationToken cancellationToken)
    {
        if (response.Content.Headers.ContentLength == 0)
        {
            return null;
        }

        string content;
        try
        {
            content = await response.Content.ReadAsStringAsync(cancellationToken);
        }
        catch (Exception ex) when (ex is IOException or JsonException)
        {
            return null;
        }

        if (string.IsNullOrWhiteSpace(content) || content.TrimStart().StartsWith('<'))
        {
            return null;
        }

        var trimmedContent = content.Trim();
        try
        {
            using var document = JsonDocument.Parse(trimmedContent);
            var root = document.RootElement;
            return TryGetString(root, "detail")
                ?? TryGetString(root, "message")
                ?? TryGetString(root, "title");
        }
        catch (JsonException)
        {
            return trimmedContent.Length <= 500 ? trimmedContent : trimmedContent[..500];
        }
    }

    private static string? TryGetString(JsonElement element, string propertyName)
        => element.ValueKind == JsonValueKind.Object
           && element.TryGetProperty(propertyName, out var property)
           && property.ValueKind == JsonValueKind.String
            ? property.GetString()
            : null;
}
