using System.Buffers;
using System.Text.Json;

namespace Madorin.AI.Runtime.Persistence.Files;

/// <summary>Classifies one canonical JSONL file without changing it.</summary>
public enum ConversationIntegrityStatus
{
    /// <summary>The header and all records are valid.</summary>
    Valid,

    /// <summary>Only the final record is damaged and may be truncated safely.</summary>
    RecoverableTailDamage,

    /// <summary>The file cannot be repaired by truncating its final record.</summary>
    UnrecoverableDamage
}

/// <summary>Describes one canonical record location used by the SQLite message index.</summary>
public sealed record ConversationIntegrityRecord(
    string MessageId,
    long Sequence,
    long FileOffset,
    long RecordLength);

/// <summary>Contains the read-only inspection result for one canonical JSONL file.</summary>
public sealed record ConversationFileIntegrityResult(
    string SessionId,
    string FilePath,
    ConversationIntegrityStatus Status,
    ConversationIntegrityRecord[] Records,
    string? Diagnostic);

/// <summary>Inspects canonical conversation files without creating or modifying storage.</summary>
public static class ConversationIntegrityInspector
{
    private const int MaxJsonLineBytes = 16 * 1024 * 1024;
    private const string HeaderSchema = "madorin.conversation.v1";

    /// <summary>Inspects every JSONL file in an existing Runtime data directory.</summary>
    public static async Task<IReadOnlyList<ConversationFileIntegrityResult>> InspectAsync(
        string dataDirectory,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(dataDirectory);
        var messagesDirectory = Path.Combine(Path.GetFullPath(dataDirectory), "messages");
        if (!Directory.Exists(messagesDirectory))
        {
            return [];
        }

        var files = Directory.EnumerateFiles(messagesDirectory, "*.jsonl")
            .Order(StringComparer.Ordinal)
            .ToArray();
        var results = new ConversationFileIntegrityResult[files.Length];
        for (var index = 0; index < files.Length; index++)
        {
            ct.ThrowIfCancellationRequested();
            results[index] = await InspectFileAsync(files[index], ct).ConfigureAwait(false);
        }

        return results;
    }

    private static async Task<ConversationFileIntegrityResult> InspectFileAsync(
        string path,
        CancellationToken ct)
    {
        var sessionId = Path.GetFileNameWithoutExtension(path);
        try
        {
            await using var stream = new FileStream(path, new FileStreamOptions
            {
                Mode = FileMode.Open,
                Access = FileAccess.Read,
                Share = FileShare.ReadWrite,
                BufferSize = 64 * 1024,
                Options = FileOptions.Asynchronous | FileOptions.SequentialScan
            });
            if (stream.Length == 0)
            {
                return Unrecoverable(sessionId, path, "The conversation file is empty.");
            }

            var records = new List<ConversationIntegrityRecord>();
            var lineBuffer = new ArrayBufferWriter<byte>();
            var rentedBuffer = ArrayPool<byte>.Shared.Rent(64 * 1024);
            long lineStart = 0;
            long bufferOffset = 0;
            var lineNumber = 0;
            long lastSequence = 0;
            try
            {
                while (true)
                {
                    var read = await stream.ReadAsync(rentedBuffer, ct).ConfigureAwait(false);
                    if (read == 0)
                    {
                        break;
                    }

                    var segmentStart = 0;
                    for (var index = 0; index < read; index++)
                    {
                        if (rentedBuffer[index] != (byte)'\n')
                        {
                            continue;
                        }

                        AppendLineBytes(
                            lineBuffer,
                            rentedBuffer.AsSpan(segmentStart, index - segmentStart));
                        var lineEnd = bufferOffset + index + 1;
                        var result = ProcessLine(
                            sessionId,
                            path,
                            lineNumber,
                            lineStart,
                            lineEnd - lineStart,
                            lineBuffer.WrittenSpan,
                            lastSequence,
                            records,
                            isLastLine: lineEnd == stream.Length);
                        if (result is not null)
                        {
                            return result;
                        }

                        if (lineNumber > 0)
                        {
                            lastSequence++;
                        }

                        lineNumber++;
                        lineStart = lineEnd;
                        lineBuffer.Clear();
                        segmentStart = index + 1;
                    }

                    AppendLineBytes(
                        lineBuffer,
                        rentedBuffer.AsSpan(segmentStart, read - segmentStart));
                    if (lineBuffer.WrittenCount > MaxJsonLineBytes)
                    {
                        return Unrecoverable(
                            sessionId,
                            path,
                            $"A JSONL line exceeds {MaxJsonLineBytes} bytes.");
                    }

                    bufferOffset += read;
                }

                if (lineBuffer.WrittenCount > 0)
                {
                    var result = ProcessLine(
                        sessionId,
                        path,
                        lineNumber,
                        lineStart,
                        stream.Length - lineStart,
                        lineBuffer.WrittenSpan,
                        lastSequence,
                        records,
                        isLastLine: true);
                    if (result is not null)
                    {
                        return result;
                    }

                    lineNumber++;
                }
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(rentedBuffer);
            }

            if (lineNumber == 0)
            {
                return Unrecoverable(sessionId, path, "The conversation header is missing.");
            }

            return new ConversationFileIntegrityResult(
                sessionId,
                path,
                ConversationIntegrityStatus.Valid,
                [.. records],
                null);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return Unrecoverable(sessionId, path, ex.Message);
        }
    }

    private static ConversationFileIntegrityResult? ProcessLine(
        string sessionId,
        string path,
        int lineNumber,
        long lineStart,
        long lineLength,
        ReadOnlySpan<byte> rawLine,
        long lastSequence,
        List<ConversationIntegrityRecord> records,
        bool isLastLine)
    {
        var jsonLine = rawLine is [.., (byte)'\r'] ? rawLine[..^1] : rawLine;
        try
        {
            if (jsonLine.Length == 0)
            {
                throw new JsonException("A JSONL line cannot be empty.");
            }

            if (lineNumber == 0)
            {
                var header = JsonSerializer.Deserialize(
                    jsonLine,
                    ConversationJsonContext.Default.ConversationHeader)
                    ?? throw new JsonException("The conversation header was empty.");
                if (!string.Equals(header.Schema, HeaderSchema, StringComparison.Ordinal)
                    || !string.Equals(header.SessionId, sessionId, StringComparison.Ordinal)
                    || string.IsNullOrWhiteSpace(header.Mode))
                {
                    throw new InvalidDataException("The conversation header is invalid.");
                }

                return null;
            }

            var record = JsonSerializer.Deserialize(
                jsonLine,
                ConversationJsonContext.Default.ConversationRecordV1)
                ?? throw new JsonException("The conversation record was empty.");
            ValidateRecord(record);
            if (record.Sequence != lastSequence + 1)
            {
                throw new InvalidDataException(
                    $"Conversation sequence {record.Sequence} does not follow {lastSequence}.");
            }

            records.Add(new ConversationIntegrityRecord(
                record.MessageId,
                record.Sequence,
                lineStart,
                lineLength));
            return null;
        }
        catch (Exception ex) when (ex is JsonException
            or NotSupportedException
            or ArgumentException
            or InvalidDataException)
        {
            if (lineNumber > 0 && isLastLine)
            {
                return new ConversationFileIntegrityResult(
                    sessionId,
                    path,
                    ConversationIntegrityStatus.RecoverableTailDamage,
                    [.. records],
                    ex.Message);
            }

            return Unrecoverable(sessionId, path, ex.Message, records);
        }
    }

    private static void ValidateRecord(ConversationRecordV1 record)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(record.MessageId);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(record.Sequence);
        ArgumentException.ThrowIfNullOrWhiteSpace(record.InvocationId);
        ArgumentException.ThrowIfNullOrWhiteSpace(record.AgentId);
        ArgumentException.ThrowIfNullOrWhiteSpace(record.Role);
        if (record.Role is not ("user" or "assistant" or "tool"))
        {
            throw new ArgumentOutOfRangeException(nameof(record), "The message role is not supported.");
        }

        if (record.Content.ValueKind is not JsonValueKind.Array)
        {
            throw new ArgumentException("Conversation content must be a JSON array.", nameof(record));
        }

        foreach (var block in record.Content.EnumerateArray())
        {
            if (block.ValueKind is not JsonValueKind.Object
                || !block.TryGetProperty("type", out var discriminator)
                || discriminator.ValueKind is not JsonValueKind.String
                || discriminator.GetString() is not (
                    "text" or "reasoning" or "tool_call" or "tool_result" or "blob_ref"))
            {
                throw new ArgumentException(
                    "Conversation content contains an unsupported protocol block.",
                    nameof(record));
            }
        }
    }

    private static void AppendLineBytes(ArrayBufferWriter<byte> writer, ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length == 0)
        {
            return;
        }

        bytes.CopyTo(writer.GetSpan(bytes.Length));
        writer.Advance(bytes.Length);
    }

    private static ConversationFileIntegrityResult Unrecoverable(
        string sessionId,
        string path,
        string diagnostic,
        IReadOnlyCollection<ConversationIntegrityRecord>? records = null) =>
        new(
            sessionId,
            path,
            ConversationIntegrityStatus.UnrecoverableDamage,
            records is null ? [] : [.. records],
            diagnostic);
}
