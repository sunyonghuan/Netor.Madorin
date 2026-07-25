using System.CommandLine;
using System.Text;
using System.Text.Json;

namespace Madorin.AI.Runtime.Cli.Commands;

internal static class CliOutput
{
    public static bool IsJson(ParseResult parseResult, Option<bool> jsonOption) =>
        parseResult.GetValue(jsonOption);

    public static void WriteJson(TextWriter output, Action<Utf8JsonWriter> writeValue)
    {
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
        {
            writeValue(writer);
        }

        using var document = JsonDocument.Parse(
            stream.GetBuffer().AsMemory(0, checked((int)stream.Length)));
        WriteNormalizedEnvelope(output, document.RootElement);
    }

    public static void WriteFailure(
        TextWriter output,
        string code,
        string message,
        Action<Utf8JsonWriter>? writeData = null,
        bool isRetryable = false,
        string? diagnosticId = null)
    {
        var resolvedDiagnosticId = string.IsNullOrWhiteSpace(diagnosticId)
            ? CreateDiagnosticId()
            : diagnosticId;
        WriteRawJson(output, writer =>
        {
            writer.WriteStartObject();
            writer.WriteBoolean("success", false);
            writer.WritePropertyName("error");
            writer.WriteStartObject();
            writer.WriteString("code", code);
            writer.WriteString("message", message);
            writer.WriteBoolean("isRetryable", isRetryable);
            writer.WriteString("diagnosticId", resolvedDiagnosticId);
            writer.WriteEndObject();
            if (writeData is not null)
            {
                writer.WritePropertyName("data");
                writeData(writer);
            }

            WriteEmptyWarnings(writer);
            writer.WriteString("diagnosticId", resolvedDiagnosticId);
            writer.WriteEndObject();
        });
    }

    private static void WriteNormalizedEnvelope(TextWriter output, JsonElement value)
    {
        if (value.ValueKind is not JsonValueKind.Object)
        {
            WriteSuccess(output, value);
            return;
        }

        var hasExplicitSuccess = value.TryGetProperty("success", out var successValue)
            && successValue.ValueKind is JsonValueKind.True or JsonValueKind.False;
        var isSuccess = hasExplicitSuccess
            ? successValue.GetBoolean()
            : !IsLegacyFailure(value);
        if (isSuccess)
        {
            var hasExplicitData = value.TryGetProperty("data", out var explicitData);
            var data = hasExplicitData
                ? explicitData
                : value;
            WriteSuccess(
                output,
                data,
                value,
                stripEnvelopeProperties: !hasExplicitData);
            return;
        }

        WriteFailure(output, value);
    }

    private static void WriteSuccess(
        TextWriter output,
        JsonElement data,
        JsonElement envelopeSource = default,
        bool stripEnvelopeProperties = false)
    {
        WriteRawJson(output, writer =>
        {
            writer.WriteStartObject();
            writer.WriteBoolean("success", true);
            writer.WritePropertyName("data");
            if (stripEnvelopeProperties && data.ValueKind is JsonValueKind.Object)
            {
                WriteObjectWithoutEnvelopeProperties(writer, data);
            }
            else
            {
                data.WriteTo(writer);
            }

            WriteWarnings(writer, envelopeSource);
            WriteDiagnosticId(writer, envelopeSource, fallback: null);
            writer.WriteEndObject();
        });
    }

    private static void WriteFailure(
        TextWriter output,
        JsonElement value)
    {
        var error = value.TryGetProperty("error", out var explicitError)
            && explicitError.ValueKind is JsonValueKind.Object
                ? explicitError
                : default;
        var code = GetString(error, "code") ?? "CommandFailed";
        var message = GetString(error, "message")
            ?? GetString(value, "message")
            ?? CreateFailureMessage(value);
        var isRetryable = error.ValueKind is JsonValueKind.Object
            && error.TryGetProperty("isRetryable", out var retryable)
            && retryable.ValueKind is JsonValueKind.True;
        var diagnosticId = GetString(error, "diagnosticId")
            ?? GetString(value, "diagnosticId")
            ?? CreateDiagnosticId();

        WriteRawJson(output, writer =>
        {
            writer.WriteStartObject();
            writer.WriteBoolean("success", false);
            writer.WritePropertyName("error");
            writer.WriteStartObject();
            writer.WriteString("code", code);
            writer.WriteString("message", message);
            writer.WriteBoolean("isRetryable", isRetryable);
            writer.WriteString("diagnosticId", diagnosticId);
            writer.WriteEndObject();

            if (HasFailureData(value))
            {
                writer.WritePropertyName("data");
                WriteObjectWithoutEnvelopeProperties(writer, value);
            }

            WriteWarnings(writer, value);
            writer.WriteString("diagnosticId", diagnosticId);
            writer.WriteEndObject();
        });
    }

    private static bool IsLegacyFailure(JsonElement value)
    {
        if (value.TryGetProperty("healthy", out var healthy)
            && healthy.ValueKind is JsonValueKind.False)
        {
            return true;
        }

        var status = GetString(value, "status");
        if (status is null)
        {
            return false;
        }

        return status.Equals("error", StringComparison.OrdinalIgnoreCase)
            || status.Equals("failed", StringComparison.OrdinalIgnoreCase)
            || status.Equals("issues", StringComparison.OrdinalIgnoreCase)
            || status.Equals("blocked", StringComparison.OrdinalIgnoreCase)
            || status.Equals("partial", StringComparison.OrdinalIgnoreCase);
    }

    private static bool HasFailureData(JsonElement value)
    {
        foreach (var property in value.EnumerateObject())
        {
            if (!IsEnvelopeProperty(property.Name))
            {
                return true;
            }
        }

        return false;
    }

    private static string CreateFailureMessage(JsonElement value)
    {
        var command = GetString(value, "command");
        return command is null ? "The command failed." : $"{command} failed.";
    }

    private static string? GetString(JsonElement value, string propertyName)
    {
        if (value.ValueKind is not JsonValueKind.Object
            || !value.TryGetProperty(propertyName, out var property)
            || property.ValueKind is not JsonValueKind.String)
        {
            return null;
        }

        return property.GetString();
    }

    private static void WriteObjectWithoutEnvelopeProperties(
        Utf8JsonWriter writer,
        JsonElement value)
    {
        writer.WriteStartObject();
        foreach (var property in value.EnumerateObject())
        {
            if (IsEnvelopeProperty(property.Name))
            {
                continue;
            }

            property.WriteTo(writer);
        }

        writer.WriteEndObject();
    }

    private static bool IsEnvelopeProperty(string propertyName) =>
        propertyName is "success" or "data" or "error" or "warnings" or "diagnosticId";

    private static void WriteWarnings(Utf8JsonWriter writer, JsonElement source)
    {
        writer.WritePropertyName("warnings");
        if (source.ValueKind is JsonValueKind.Object
            && source.TryGetProperty("warnings", out var warnings)
            && warnings.ValueKind is JsonValueKind.Array)
        {
            warnings.WriteTo(writer);
            return;
        }

        writer.WriteStartArray();
        writer.WriteEndArray();
    }

    private static void WriteEmptyWarnings(Utf8JsonWriter writer)
    {
        writer.WriteStartArray("warnings");
        writer.WriteEndArray();
    }

    private static void WriteDiagnosticId(
        Utf8JsonWriter writer,
        JsonElement source,
        string? fallback)
    {
        var diagnosticId = GetString(source, "diagnosticId") ?? fallback;
        if (diagnosticId is null)
        {
            writer.WriteNull("diagnosticId");
            return;
        }

        writer.WriteString("diagnosticId", diagnosticId);
    }

    private static void WriteRawJson(
        TextWriter output,
        Action<Utf8JsonWriter> writeValue)
    {
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
        {
            writeValue(writer);
        }

        output.WriteLine(
            Encoding.UTF8.GetString(
                stream.GetBuffer(),
                0,
                checked((int)stream.Length)));
    }

    private static string CreateDiagnosticId() => $"diag_{Guid.NewGuid():N}";
}
