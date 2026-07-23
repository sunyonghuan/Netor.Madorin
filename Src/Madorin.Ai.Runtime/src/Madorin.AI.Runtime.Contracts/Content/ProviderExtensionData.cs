using System.Buffers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Madorin.AI.Runtime.Contracts;

/// <summary>Contains bounded, namespaced Provider data that core Runtime flows may ignore.</summary>
public sealed record ProviderExtensionData
{
    public const int MaximumCollectionBytes = 65_536;
    public const int MaximumNameLength = 128;
    public const int MaximumNamespaceLength = 128;
    public const int MaximumValueBytes = 16_384;

    [JsonConstructor]
    public ProviderExtensionData(string @namespace, string name, JsonElement value)
    {
        ValidateIdentifier(@namespace, nameof(@namespace), MaximumNamespaceLength, requireProviderPrefix: true);
        ValidateIdentifier(name, nameof(name), MaximumNameLength, requireProviderPrefix: false);

        var valueBytes = Encoding.UTF8.GetByteCount(value.GetRawText());
        if (valueBytes > MaximumValueBytes)
        {
            throw new ArgumentOutOfRangeException(
                nameof(value),
                valueBytes,
                $"Provider extension values cannot exceed {MaximumValueBytes} UTF-8 bytes.");
        }

        Namespace = @namespace;
        Name = name;
        Value = value.Clone();
    }

    public string Namespace { get; }

    public string Name { get; }

    public JsonElement Value { get; }

    public static ProviderExtensionData CreateString(
        string @namespace,
        string name,
        string value)
    {
        ArgumentNullException.ThrowIfNull(value);

        var buffer = new ArrayBufferWriter<byte>();
        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStringValue(value);
        }

        using var document = JsonDocument.Parse(buffer.WrittenMemory);
        return new ProviderExtensionData(@namespace, name, document.RootElement);
    }

    public static void ValidateCollection(IReadOnlyList<ProviderExtensionData>? extensions)
    {
        if (extensions is null)
        {
            return;
        }

        var totalBytes = 0;
        foreach (var extension in extensions)
        {
            ArgumentNullException.ThrowIfNull(extension);
            totalBytes = checked(totalBytes
                + Encoding.UTF8.GetByteCount(extension.Namespace)
                + Encoding.UTF8.GetByteCount(extension.Name)
                + Encoding.UTF8.GetByteCount(extension.Value.GetRawText()));
            if (totalBytes > MaximumCollectionBytes)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(extensions),
                    totalBytes,
                    $"Provider extensions cannot exceed {MaximumCollectionBytes} UTF-8 bytes in total.");
            }
        }
    }

    private static void ValidateIdentifier(
        string value,
        string parameterName,
        int maximumLength,
        bool requireProviderPrefix)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value, parameterName);
        if (value.Length > maximumLength)
        {
            throw new ArgumentOutOfRangeException(parameterName, value.Length, $"Maximum length is {maximumLength}.");
        }

        if (requireProviderPrefix && !value.StartsWith("provider.", StringComparison.Ordinal))
        {
            throw new ArgumentException("Provider extension namespaces must start with 'provider.'.", parameterName);
        }

        if (value[0] is '.' or '-' || value[^1] is '.' or '-')
        {
            throw new ArgumentException("Provider extension identifiers cannot start or end with punctuation.", parameterName);
        }

        foreach (var character in value)
        {
            if (character is not (>= 'a' and <= 'z')
                and not (>= '0' and <= '9')
                and not '.'
                and not '-')
            {
                throw new ArgumentException(
                    "Provider extension identifiers use lowercase ASCII letters, digits, dots, and hyphens.",
                    parameterName);
            }
        }
    }
}
