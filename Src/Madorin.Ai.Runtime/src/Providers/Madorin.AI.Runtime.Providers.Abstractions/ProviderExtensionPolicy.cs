using System.Buffers;
using System.Text;
using System.Text.Json;
using Madorin.AI.Runtime.Contracts;

namespace Madorin.AI.Runtime.Providers.Abstractions;

public static class ProviderExtensionPolicy
{
    public const string ReasoningSignatureName = "reasoning.signature";

    public static string GetNamespace(string providerId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(providerId);
        var normalized = new StringBuilder(providerId.Length);
        foreach (var character in providerId)
        {
            normalized.Append(character switch
            {
                >= 'a' and <= 'z' or >= '0' and <= '9' or '-' => character,
                >= 'A' and <= 'Z' => char.ToLowerInvariant(character),
                _ => '-'
            });
        }

        var normalizedProviderId = normalized.ToString().Trim('-');
        if (normalizedProviderId.Length == 0)
        {
            throw new ArgumentException("Provider ID must contain an ASCII letter or digit.", nameof(providerId));
        }

        return $"provider.{normalizedProviderId}";
    }

    public static ProviderExtensionData CreateRedactedJson(
        string providerId,
        string name,
        JsonElement value)
    {
        var originalBytes = Encoding.UTF8.GetByteCount(value.GetRawText());
        if (originalBytes > ProviderExtensionData.MaximumValueBytes)
        {
            var buffer = new ArrayBufferWriter<byte>();
            using (var writer = new Utf8JsonWriter(buffer))
            {
                writer.WriteStartObject();
                writer.WriteBoolean("truncated", true);
                writer.WriteNumber("originalBytes", originalBytes);
                writer.WriteEndObject();
            }

            using var summary = JsonDocument.Parse(buffer.WrittenMemory);
            return new ProviderExtensionData(GetNamespace(providerId), name, summary.RootElement);
        }

        var redactedBuffer = new ArrayBufferWriter<byte>();
        using (var writer = new Utf8JsonWriter(redactedBuffer))
        {
            WriteRedacted(writer, value, propertyName: null);
        }

        using var document = JsonDocument.Parse(redactedBuffer.WrittenMemory);
        return new ProviderExtensionData(GetNamespace(providerId), name, document.RootElement);
    }

    public static string? GetReasoningSignature(
        ReasoningContentBlock reasoning,
        string providerId)
    {
        var expectedNamespace = GetNamespace(providerId);
        var extension = reasoning.ProviderExtensions?.FirstOrDefault(candidate =>
            string.Equals(candidate.Namespace, expectedNamespace, StringComparison.Ordinal)
            && string.Equals(candidate.Name, ReasoningSignatureName, StringComparison.Ordinal));
        return extension?.Value.ValueKind == JsonValueKind.String
            ? extension.Value.GetString()
            : null;
    }

    private static void WriteRedacted(
        Utf8JsonWriter writer,
        JsonElement value,
        string? propertyName)
    {
        if (propertyName is not null && IsSensitive(propertyName))
        {
            writer.WriteStringValue("[redacted]");
            return;
        }

        switch (value.ValueKind)
        {
            case JsonValueKind.Object:
                writer.WriteStartObject();
                foreach (var property in value.EnumerateObject())
                {
                    writer.WritePropertyName(property.Name);
                    WriteRedacted(writer, property.Value, property.Name);
                }

                writer.WriteEndObject();
                break;
            case JsonValueKind.Array:
                writer.WriteStartArray();
                foreach (var item in value.EnumerateArray())
                {
                    WriteRedacted(writer, item, propertyName: null);
                }

                writer.WriteEndArray();
                break;
            default:
                value.WriteTo(writer);
                break;
        }
    }

    private static bool IsSensitive(string name) =>
        name.Contains("authorization", StringComparison.OrdinalIgnoreCase)
        || name.Contains("api-key", StringComparison.OrdinalIgnoreCase)
        || name.Contains("apikey", StringComparison.OrdinalIgnoreCase)
        || name.Contains("api_key", StringComparison.OrdinalIgnoreCase)
        || name.Contains("credential", StringComparison.OrdinalIgnoreCase)
        || name.Contains("secret", StringComparison.OrdinalIgnoreCase)
        || name.Contains("prompt", StringComparison.OrdinalIgnoreCase)
        || name.Contains("responsebody", StringComparison.OrdinalIgnoreCase)
        || name.Contains("response_body", StringComparison.OrdinalIgnoreCase);
}
