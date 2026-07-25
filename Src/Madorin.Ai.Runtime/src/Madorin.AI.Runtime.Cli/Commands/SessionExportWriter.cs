using System.Buffers;
using System.Globalization;
using System.Text;
using System.Text.Json;
using Madorin.AI.Runtime.Entities;
using Madorin.AI.Runtime.Persistence.Files;

namespace Madorin.AI.Runtime.Cli.Commands;

internal enum SessionExportFormat
{
    Markdown,
    Text,
    JsonLines
}

internal static class SessionExportWriter
{
    public static bool TryParseFormat(string? value, out SessionExportFormat format)
    {
        switch (value?.ToLowerInvariant())
        {
            case null:
            case "markdown":
                format = SessionExportFormat.Markdown;
                return true;
            case "txt":
                format = SessionExportFormat.Text;
                return true;
            case "jsonl":
                format = SessionExportFormat.JsonLines;
                return true;
            default:
                format = default;
                return false;
        }
    }

    public static async Task WriteAsync(
        TextWriter output,
        PersistedSessionSnapshot snapshot,
        IReadOnlyList<ConversationRecordV1> records,
        SessionExportFormat format,
        bool includeReasoning,
        bool includeToolCalls,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(output);
        ArgumentNullException.ThrowIfNull(snapshot);
        ArgumentNullException.ThrowIfNull(records);

        if (format is SessionExportFormat.Markdown)
        {
            await WriteMarkdownHeaderAsync(output, snapshot, records.Count, ct)
                .ConfigureAwait(false);
        }
        else if (format is SessionExportFormat.Text)
        {
            await WriteTextHeaderAsync(output, snapshot, records.Count, ct)
                .ConfigureAwait(false);
        }

        foreach (var record in records)
        {
            ct.ThrowIfCancellationRequested();
            var blocks = FilterContent(record.Content, includeReasoning, includeToolCalls);
            if (blocks.Count == 0 && format is not SessionExportFormat.JsonLines)
            {
                continue;
            }

            switch (format)
            {
                case SessionExportFormat.Markdown:
                    await WriteMarkdownRecordAsync(output, record, blocks, ct)
                        .ConfigureAwait(false);
                    break;
                case SessionExportFormat.Text:
                    await WriteTextRecordAsync(output, record, blocks, ct)
                        .ConfigureAwait(false);
                    break;
                case SessionExportFormat.JsonLines:
                    await WriteJsonLineAsync(output, record, blocks, ct)
                        .ConfigureAwait(false);
                    break;
                default:
                    throw new ArgumentOutOfRangeException(nameof(format));
            }
        }
    }

    private static List<JsonElement> FilterContent(
        JsonElement content,
        bool includeReasoning,
        bool includeToolCalls)
    {
        var result = new List<JsonElement>();
        if (content.ValueKind is not JsonValueKind.Array)
        {
            return result;
        }

        foreach (var block in content.EnumerateArray())
        {
            if (block.ValueKind is not JsonValueKind.Object
                || !block.TryGetProperty("type", out var typeProperty))
            {
                continue;
            }

            var include = typeProperty.GetString() switch
            {
                "text" or "blob_ref" => true,
                "reasoning" => includeReasoning,
                "tool_call" or "tool_result" => includeToolCalls,
                _ => false
            };
            if (include)
            {
                result.Add(block);
            }
        }

        return result;
    }

    private static async Task WriteMarkdownHeaderAsync(
        TextWriter output,
        PersistedSessionSnapshot snapshot,
        int messageCount,
        CancellationToken ct)
    {
        await output.WriteLineAsync($"# Session {snapshot.SessionId}".AsMemory(), ct)
            .ConfigureAwait(false);
        await output.WriteLineAsync(
                ($"Mode: {snapshot.Mode.ToString().ToLowerInvariant()} | " +
                 $"Created: {snapshot.CreatedAt:O} | " +
                 $"Messages: {messageCount.ToString(CultureInfo.InvariantCulture)}").AsMemory(),
                ct)
            .ConfigureAwait(false);
        await output.WriteLineAsync(ReadOnlyMemory<char>.Empty, ct).ConfigureAwait(false);
    }

    private static async Task WriteMarkdownRecordAsync(
        TextWriter output,
        ConversationRecordV1 record,
        IReadOnlyList<JsonElement> blocks,
        CancellationToken ct)
    {
        await output.WriteLineAsync("---".AsMemory(), ct).ConfigureAwait(false);
        await output.WriteLineAsync(
                $"**{GetRoleLabel(record.Role)}** ({record.Timestamp:O})".AsMemory(),
                ct)
            .ConfigureAwait(false);
        await output.WriteLineAsync(ReadOnlyMemory<char>.Empty, ct).ConfigureAwait(false);
        await WriteBlocksAsync(output, blocks, markdown: true, ct).ConfigureAwait(false);
        await output.WriteLineAsync(ReadOnlyMemory<char>.Empty, ct).ConfigureAwait(false);
    }

    private static async Task WriteTextHeaderAsync(
        TextWriter output,
        PersistedSessionSnapshot snapshot,
        int messageCount,
        CancellationToken ct)
    {
        await output.WriteLineAsync($"SESSION: {snapshot.SessionId}".AsMemory(), ct)
            .ConfigureAwait(false);
        await output.WriteLineAsync(
                $"MODE: {snapshot.Mode.ToString().ToUpperInvariant()}".AsMemory(),
                ct)
            .ConfigureAwait(false);
        await output.WriteLineAsync($"CREATED: {snapshot.CreatedAt:O}".AsMemory(), ct)
            .ConfigureAwait(false);
        await output.WriteLineAsync(
                $"MESSAGES: {messageCount.ToString(CultureInfo.InvariantCulture)}".AsMemory(),
                ct)
            .ConfigureAwait(false);
        await output.WriteLineAsync(ReadOnlyMemory<char>.Empty, ct).ConfigureAwait(false);
    }

    private static async Task WriteTextRecordAsync(
        TextWriter output,
        ConversationRecordV1 record,
        IReadOnlyList<JsonElement> blocks,
        CancellationToken ct)
    {
        await output.WriteLineAsync($"{GetRoleLabel(record.Role)}:".AsMemory(), ct)
            .ConfigureAwait(false);
        await WriteBlocksAsync(output, blocks, markdown: false, ct).ConfigureAwait(false);
        await output.WriteLineAsync(ReadOnlyMemory<char>.Empty, ct).ConfigureAwait(false);
    }

    private static async Task WriteJsonLineAsync(
        TextWriter output,
        ConversationRecordV1 record,
        IReadOnlyList<JsonElement> blocks,
        CancellationToken ct)
    {
        var buffer = new ArrayBufferWriter<byte>();
        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartObject();
            writer.WriteString("messageId", record.MessageId);
            writer.WriteNumber("sequence", record.Sequence);
            writer.WriteString("invocationId", record.InvocationId);
            writer.WriteString("agentId", record.AgentId);
            writer.WriteString("role", record.Role);
            writer.WriteStartArray("content");
            foreach (var block in blocks)
            {
                block.WriteTo(writer);
            }

            writer.WriteEndArray();
            writer.WriteString("timestamp", record.Timestamp);
            if (record.UsageReference is null)
            {
                writer.WriteNull("usageReference");
            }
            else
            {
                writer.WriteString("usageReference", record.UsageReference);
            }

            writer.WritePropertyName("summaryMetadata");
            if (record.SummaryMetadata is { } summaryMetadata)
            {
                summaryMetadata.WriteTo(writer);
            }
            else
            {
                writer.WriteNullValue();
            }

            writer.WriteEndObject();
            writer.Flush();
        }

        var line = Encoding.UTF8.GetString(buffer.WrittenSpan);
        await output.WriteLineAsync(line.AsMemory(), ct).ConfigureAwait(false);
    }

    private static async Task WriteBlocksAsync(
        TextWriter output,
        IReadOnlyList<JsonElement> blocks,
        bool markdown,
        CancellationToken ct)
    {
        for (var index = 0; index < blocks.Count; index++)
        {
            if (index > 0)
            {
                await output.WriteLineAsync(ReadOnlyMemory<char>.Empty, ct).ConfigureAwait(false);
            }

            var block = blocks[index];
            var type = block.GetProperty("type").GetString();
            switch (type)
            {
                case "text":
                    await WritePropertyAsync(output, block, "text", ct).ConfigureAwait(false);
                    break;
                case "reasoning":
                    await output.WriteLineAsync("[REASONING]".AsMemory(), ct).ConfigureAwait(false);
                    await WriteFirstPropertyAsync(output, block, "content", "text", ct)
                        .ConfigureAwait(false);
                    break;
                case "tool_call":
                    await WriteStructuredBlockAsync(output, block, "TOOL CALL", markdown, ct)
                        .ConfigureAwait(false);
                    break;
                case "tool_result":
                    await WriteStructuredBlockAsync(output, block, "TOOL RESULT", markdown, ct)
                        .ConfigureAwait(false);
                    break;
                case "blob_ref":
                    await output.WriteLineAsync(GetBlobMarker(block).AsMemory(), ct)
                        .ConfigureAwait(false);
                    break;
            }
        }
    }

    private static async Task WriteFirstPropertyAsync(
        TextWriter output,
        JsonElement block,
        string firstPropertyName,
        string secondPropertyName,
        CancellationToken ct)
    {
        if (block.TryGetProperty(firstPropertyName, out var value)
            || block.TryGetProperty(secondPropertyName, out value))
        {
            await output.WriteLineAsync(GetDisplayValue(value).AsMemory(), ct).ConfigureAwait(false);
        }
    }

    private static async Task WritePropertyAsync(
        TextWriter output,
        JsonElement block,
        string propertyName,
        CancellationToken ct)
    {
        if (block.TryGetProperty(propertyName, out var value))
        {
            await output.WriteLineAsync(GetDisplayValue(value).AsMemory(), ct).ConfigureAwait(false);
        }
    }

    private static async Task WriteStructuredBlockAsync(
        TextWriter output,
        JsonElement block,
        string label,
        bool markdown,
        CancellationToken ct)
    {
        await output.WriteLineAsync($"[{label}]".AsMemory(), ct).ConfigureAwait(false);
        if (markdown)
        {
            await output.WriteLineAsync("```json".AsMemory(), ct).ConfigureAwait(false);
        }

        await output.WriteLineAsync(FormatJson(block).AsMemory(), ct).ConfigureAwait(false);
        if (markdown)
        {
            await output.WriteLineAsync("```".AsMemory(), ct).ConfigureAwait(false);
        }
    }

    private static string FormatJson(JsonElement value)
    {
        var buffer = new ArrayBufferWriter<byte>();
        using (var writer = new Utf8JsonWriter(buffer, new JsonWriterOptions { Indented = true }))
        {
            value.WriteTo(writer);
            writer.Flush();
        }

        return Encoding.UTF8.GetString(buffer.WrittenSpan);
    }

    private static string GetDisplayValue(JsonElement value) => value.ValueKind switch
    {
        JsonValueKind.String => value.GetString() ?? string.Empty,
        _ => FormatJson(value)
    };

    private static string GetBlobMarker(JsonElement block)
    {
        if (block.TryGetProperty("blob", out var blob)
            && blob.ValueKind is JsonValueKind.Object
            && blob.TryGetProperty("blobId", out var blobId)
            && blobId.ValueKind is JsonValueKind.String)
        {
            return $"[blob omitted: {blobId.GetString()}]";
        }

        return "[blob omitted]";
    }

    private static string GetRoleLabel(string role) => role.ToLowerInvariant() switch
    {
        "user" => "USER",
        "assistant" => "ASSISTANT",
        "system" => "SYSTEM",
        "tool" => "TOOL",
        _ => role.ToUpperInvariant()
    };
}
