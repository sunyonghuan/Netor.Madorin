using DesktopPet.Behaviors;
using System.Buffers;
using System.Text;
using System.Text.Json;

namespace DesktopPet.Ai;

public sealed class PetMcpServer
{
    private readonly PetBehaviorStateMachine _stateMachine;

    public PetMcpServer(PetBehaviorStateMachine? stateMachine = null)
    {
        _stateMachine = stateMachine ?? new PetBehaviorStateMachine();
    }

    public async Task RunAsync(Stream input, Stream output, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(input);
        ArgumentNullException.ThrowIfNull(output);

        using var reader = new StreamReader(input, Encoding.UTF8, detectEncodingFromByteOrderMarks: false, bufferSize: 4096, leaveOpen: true);

        while (!cancellationToken.IsCancellationRequested)
        {
            var line = await reader.ReadLineAsync(cancellationToken).ConfigureAwait(false);
            if (line is null)
            {
                break;
            }

            if (string.IsNullOrWhiteSpace(line))
            {
                continue;
            }

            using var document = JsonDocument.Parse(line);
            var request = document.RootElement;
            if (!request.TryGetProperty("id", out var id))
            {
                continue;
            }

            using var result = HandleRequest(request);
            var responseBytes = CreateResponseBytes(id, result);
            await output.WriteAsync(responseBytes, cancellationToken).ConfigureAwait(false);
            await output.WriteAsync("\n"u8.ToArray(), cancellationToken).ConfigureAwait(false);
        }
    }

    public JsonDocument HandleRequest(string json)
    {
        using var document = JsonDocument.Parse(json);
        return HandleRequest(document.RootElement);
    }

    private JsonDocument HandleRequest(JsonElement request)
    {
        var method = request.GetProperty("method").GetString();
        return method switch
        {
            "initialize" => JsonDocument.Parse(PetMcpJson.InitializeResult),
            "tools/list" => JsonDocument.Parse(PetMcpJson.ToolsListResult),
            "tools/call" => HandleToolCall(request),
            _ => CreateError(-32601, $"Method not found: {method}")
        };
    }

    private JsonDocument HandleToolCall(JsonElement request)
    {
        var parameters = request.TryGetProperty("params", out var paramsElement)
            ? paramsElement
            : default;

        var name = parameters.ValueKind == JsonValueKind.Object && parameters.TryGetProperty("name", out var nameElement)
            ? nameElement.GetString()
            : null;

        var arguments = parameters.ValueKind == JsonValueKind.Object && parameters.TryGetProperty("arguments", out var argumentsElement)
            ? argumentsElement
            : default;

        return name switch
        {
            "pet.show" => ApplyEvent(new PetEvent(PetEventKind.Show)),
            "pet.hide" => ApplyEvent(new PetEvent(PetEventKind.Hide)),
            "pet.say" => ApplyEvent(new PetEvent(PetEventKind.Speak, ReadTextArgument(arguments))),
            "pet.think" => ApplyEvent(new PetEvent(PetEventKind.Think, ReadTextArgument(arguments))),
            "pet.status" => CreateToolResult(ToStatusJson(_stateMachine.Current)),
            _ => CreateError(-32602, $"Unknown tool: {name}")
        };
    }

    private JsonDocument ApplyEvent(PetEvent petEvent)
    {
        return CreateToolResult(ToStatusJson(_stateMachine.Apply(petEvent)));
    }

    private static string? ReadTextArgument(JsonElement arguments)
    {
        if (arguments.ValueKind != JsonValueKind.Object || !arguments.TryGetProperty("text", out var textElement))
        {
            return null;
        }

        return textElement.ValueKind == JsonValueKind.String ? textElement.GetString() : null;
    }

    private static string ToStatusJson(PetBehaviorSnapshot snapshot)
    {
        var buffer = new ArrayBufferWriter<byte>();
        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartObject();
            writer.WriteString("state", snapshot.State.ToString());
            writer.WriteString("subtitle", snapshot.Subtitle);
            writer.WriteBoolean("isVisible", snapshot.IsVisible);
            writer.WriteBoolean("isSpeaking", snapshot.IsSpeaking);
            writer.WriteBoolean("isThinking", snapshot.IsThinking);
            writer.WriteString("updatedAt", snapshot.UpdatedAt);
            writer.WriteEndObject();
        }

        return Encoding.UTF8.GetString(buffer.WrittenSpan);
    }

    private static JsonDocument CreateToolResult(string text)
    {
        var buffer = new ArrayBufferWriter<byte>();
        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartObject();
            writer.WritePropertyName("content");
            writer.WriteStartArray();
            writer.WriteStartObject();
            writer.WriteString("type", "text");
            writer.WriteString("text", text);
            writer.WriteEndObject();
            writer.WriteEndArray();
            writer.WriteEndObject();
        }

        return JsonDocument.Parse(buffer.WrittenMemory);
    }

    private static JsonDocument CreateError(int code, string message)
    {
        var buffer = new ArrayBufferWriter<byte>();
        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartObject();
            writer.WritePropertyName("error");
            writer.WriteStartObject();
            writer.WriteNumber("code", code);
            writer.WriteString("message", message);
            writer.WriteEndObject();
            writer.WriteEndObject();
        }

        return JsonDocument.Parse(buffer.WrittenMemory);
    }

    private static byte[] CreateResponseBytes(JsonElement id, JsonDocument result)
    {
        var buffer = new ArrayBufferWriter<byte>();
        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartObject();
            writer.WriteString("jsonrpc", "2.0");
            writer.WritePropertyName("id");
            id.WriteTo(writer);

            if (result.RootElement.TryGetProperty("error", out var error))
            {
                writer.WritePropertyName("error");
                error.WriteTo(writer);
            }
            else
            {
                writer.WritePropertyName("result");
                result.RootElement.WriteTo(writer);
            }

            writer.WriteEndObject();
        }

        return buffer.WrittenMemory.ToArray();
    }
}
