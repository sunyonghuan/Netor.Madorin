using System.Buffers;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Data.Sqlite;

namespace Madorin.AI.Runtime.Persistence.Files;

public sealed class ConversationStore
{
    private const string DefaultMode = "Expert";
    private const string HeaderSchema = "madorin.conversation.v1";
    private static readonly byte[] NewLineBytes = "\n"u8.ToArray();

    private readonly string _databasePath;
    private readonly string _messagesDirectory;
    private readonly ConversationStoreOptions _options;
    private readonly ConversationBlobStore _blobStore;
    private readonly ConcurrentDictionary<string, SessionState> _sessionStates =
        new(StringComparer.Ordinal);

    public ConversationStore(string dataDirectory, ConversationStoreOptions? options = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(dataDirectory);
        _options = options ?? new ConversationStoreOptions();
        _options.Validate();
        var fullDataDirectory = Path.GetFullPath(dataDirectory);
        _messagesDirectory = Path.Combine(fullDataDirectory, "messages");
        _databasePath = Path.Combine(fullDataDirectory, "state.db");
        Directory.CreateDirectory(_messagesDirectory);
        _blobStore = new ConversationBlobStore(fullDataDirectory);
    }

    public Task AppendMessageAsync(
        string sessionId,
        ConversationRecordV1 record,
        CancellationToken ct = default) =>
        AppendMessageAsync(sessionId, DefaultMode, record, ct);

    public async Task AppendMessageAsync(
        string sessionId,
        string mode,
        ConversationRecordV1 record,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(mode);
        ArgumentNullException.ThrowIfNull(record);
        ValidateRecord(record);
        var path = GetSessionPath(sessionId);
        var state = GetSessionState(sessionId);
        EnterWriteQueue(state);
        try
        {
            await state.Gate.WaitAsync(ct).ConfigureAwait(false);
            try
            {
                await EnsureInitializedAsync(sessionId, path, state, ct).ConfigureAwait(false);
                var existing = await FindMessageByIdCoreAsync(
                    sessionId,
                    path,
                    record.MessageId,
                    ct).ConfigureAwait(false);
                if (existing is not null)
                {
                    if (!string.Equals(existing.InvocationId, record.InvocationId, StringComparison.Ordinal)
                        || !string.Equals(existing.AgentId, record.AgentId, StringComparison.Ordinal)
                        || !string.Equals(existing.Role, record.Role, StringComparison.Ordinal))
                    {
                        throw new InvalidOperationException(
                            $"Message ID '{record.MessageId}' is already bound to a different canonical message.");
                    }

                    return;
                }

                if (record.Sequence != state.LastSequence + 1)
                {
                    throw new InvalidOperationException(
                        $"Message sequence {record.Sequence} does not follow {state.LastSequence} for Session '{sessionId}'.");
                }

                await AppendAllocatedRecordAsync(sessionId, mode, path, state, record)
                    .ConfigureAwait(false);
            }
            finally
            {
                state.Gate.Release();
            }
        }
        finally
        {
            ExitWriteQueue(state);
        }
    }

    public Task<ConversationRecordV1> AppendMessageAsync(
        string sessionId,
        ConversationMessageDraft message,
        CancellationToken ct = default) =>
        AppendMessageAsync(sessionId, DefaultMode, message, ct);

    public async Task<ConversationRecordV1> AppendMessageAsync(
        string sessionId,
        string mode,
        ConversationMessageDraft message,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(mode);
        ArgumentNullException.ThrowIfNull(message);
        ValidateDraft(message);
        var path = GetSessionPath(sessionId);
        var state = GetSessionState(sessionId);
        EnterWriteQueue(state);
        try
        {
            await state.Gate.WaitAsync(ct).ConfigureAwait(false);
            try
            {
                await EnsureInitializedAsync(sessionId, path, state, ct).ConfigureAwait(false);
                ct.ThrowIfCancellationRequested();
                var record = new ConversationRecordV1(
                    Guid.NewGuid().ToString("N"),
                    state.LastSequence + 1,
                    message.InvocationId,
                    message.AgentId,
                    message.Role,
                    message.Content.Clone(),
                    message.Timestamp,
                    message.UsageReference,
                    message.SummaryMetadata?.Clone());
                await AppendAllocatedRecordAsync(sessionId, mode, path, state, record)
                    .ConfigureAwait(false);
                return record;
            }
            finally
            {
                state.Gate.Release();
            }
        }
        finally
        {
            ExitWriteQueue(state);
        }
    }

    public Task<IReadOnlyList<ConversationRecordV1>> ReadAllAsync(
        string sessionId,
        CancellationToken ct = default) =>
        ReadAllAsync(sessionId, options: null, ct);

    public async Task<ConversationRecordV1?> FindMessageByIdAsync(
        string sessionId,
        string messageId,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(messageId);
        var path = GetSessionPath(sessionId);
        var state = GetSessionState(sessionId);
        await state.Gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            await EnsureInitializedAsync(sessionId, path, state, ct).ConfigureAwait(false);
            return await FindMessageByIdCoreAsync(sessionId, path, messageId, ct)
                .ConfigureAwait(false);
        }
        finally
        {
            state.Gate.Release();
        }
    }

    public async Task<IReadOnlyList<ConversationRecordV1>> FindMessagesByInvocationIdAsync(
        string sessionId,
        string invocationId,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(invocationId);
        var path = GetSessionPath(sessionId);
        var state = GetSessionState(sessionId);
        await state.Gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            await EnsureInitializedAsync(sessionId, path, state, ct).ConfigureAwait(false);
            return await FindMessagesByInvocationIdCoreAsync(
                    sessionId,
                    path,
                    invocationId,
                    ct)
                .ConfigureAwait(false);
        }
        finally
        {
            state.Gate.Release();
        }
    }

    public async Task<IReadOnlyList<ConversationRecordV1>> ReadAllAsync(
        string sessionId,
        ConversationReadOptions? options,
        CancellationToken ct = default)
    {
        var path = GetSessionPath(sessionId);
        var state = GetSessionState(sessionId);
        await state.Gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            await RepairCoreAsync(sessionId, path, ct).ConfigureAwait(false);
            var scan = await ScanAsync(sessionId, path, ct).ConfigureAwait(false);
            state.LastSequence = scan.LastSequence;
            state.IsInitialized = true;
            await SynchronizeIndexesAsync(sessionId, scan.Records, ct).ConfigureAwait(false);
            var records = scan.Records.Select(static item => item.Record).ToArray();
            return await ApplyReadOptionsAsync(records, options, ct).ConfigureAwait(false);
        }
        finally
        {
            state.Gate.Release();
        }
    }

    public Task<IReadOnlyList<ConversationRecordV1>> ReadPageAsync(
        string sessionId,
        long? cursor,
        int pageSize,
        CancellationToken ct = default) =>
        ReadPageAsync(sessionId, cursor, pageSize, options: null, ct);

    public async Task<IReadOnlyList<ConversationRecordV1>> ReadPageAsync(
        string sessionId,
        long? cursor,
        int pageSize,
        ConversationReadOptions? options,
        CancellationToken ct = default)
    {
        if (cursor is { } value)
        {
            ArgumentOutOfRangeException.ThrowIfNegative(value);
        }

        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(pageSize);
        var path = GetSessionPath(sessionId);
        var state = GetSessionState(sessionId);
        await state.Gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            await EnsureInitializedAsync(sessionId, path, state, ct).ConfigureAwait(false);
            var effectiveCursor = cursor ?? 0;
            var entries = await LoadIndexPageAsync(sessionId, effectiveCursor, pageSize, ct)
                .ConfigureAwait(false);
            if (!IsCompleteIndexPage(entries, effectiveCursor, pageSize, state.LastSequence))
            {
                var scan = await ScanAsync(sessionId, path, ct).ConfigureAwait(false);
                state.LastSequence = scan.LastSequence;
                await SynchronizeIndexesAsync(sessionId, scan.Records, ct).ConfigureAwait(false);
                entries = await LoadIndexPageAsync(sessionId, effectiveCursor, pageSize, ct)
                    .ConfigureAwait(false);
            }

            if (!IsCompleteIndexPage(entries, effectiveCursor, pageSize, state.LastSequence))
            {
                throw new InvalidDataException(
                    $"The message index for Session '{sessionId}' does not match canonical JSONL after repair.");
            }

            var page = await ReadIndexedRecordsAsync(sessionId, path, entries, ct).ConfigureAwait(false);
            return await ApplyReadOptionsAsync(page, options, ct).ConfigureAwait(false);
        }
        finally
        {
            state.Gate.Release();
        }
    }

    private static bool IsCompleteIndexPage(
        IReadOnlyList<IndexLocation> entries,
        long cursor,
        int pageSize,
        long lastSequence)
    {
        var expectedCount = checked((int)Math.Min(pageSize, Math.Max(0, lastSequence - cursor)));
        if (entries.Count != expectedCount)
        {
            return false;
        }

        for (var index = 0; index < entries.Count; index++)
        {
            if (entries[index].Sequence != cursor + index + 1)
            {
                return false;
            }
        }

        return true;
    }

    private async Task<ConversationRecordV1?> FindMessageByIdCoreAsync(
        string sessionId,
        string path,
        string messageId,
        CancellationToken ct)
    {
        await using var connection = await OpenIndexConnectionAsync(ct).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT session_id, sequence, file_offset, record_length
            FROM message_index
            WHERE message_id = $messageId;
            """;
        command.Parameters.AddWithValue("$messageId", messageId);
        await using var reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);
        if (!await reader.ReadAsync(ct).ConfigureAwait(false))
        {
            return null;
        }

        var indexedSessionId = reader.GetString(0);
        if (!string.Equals(indexedSessionId, sessionId, StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                $"Message ID '{messageId}' is already bound to Session '{indexedSessionId}'.");
        }

        var location = new IndexLocation(
            messageId,
            reader.GetInt64(1),
            reader.GetInt64(2),
            reader.GetInt64(3));
        await reader.DisposeAsync().ConfigureAwait(false);
        var records = await ReadIndexedRecordsAsync(sessionId, path, [location], ct)
            .ConfigureAwait(false);
        return records[0];
    }

    private async Task<IReadOnlyList<ConversationRecordV1>> FindMessagesByInvocationIdCoreAsync(
        string sessionId,
        string path,
        string invocationId,
        CancellationToken ct)
    {
        await using var connection = await OpenIndexConnectionAsync(ct).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT message_id, sequence, file_offset, record_length
            FROM message_index
            WHERE session_id = $sessionId
              AND invocation_id = $invocationId
            ORDER BY sequence;
            """;
        command.Parameters.AddWithValue("$sessionId", sessionId);
        command.Parameters.AddWithValue("$invocationId", invocationId);
        var locations = new List<IndexLocation>();
        await using (var reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false))
        {
            while (await reader.ReadAsync(ct).ConfigureAwait(false))
            {
                locations.Add(new IndexLocation(
                    reader.GetString(0),
                    reader.GetInt64(1),
                    reader.GetInt64(2),
                    reader.GetInt64(3)));
            }
        }

        var records = await ReadIndexedRecordsAsync(sessionId, path, locations, ct)
            .ConfigureAwait(false);
        if (records.Any(record => !string.Equals(
                record.InvocationId,
                invocationId,
                StringComparison.Ordinal)))
        {
            throw new InvalidDataException(
                $"The message index for Session '{sessionId}' contains an invalid Invocation binding.");
        }

        return records;
    }

    public async Task<bool> RepairIfNeededAsync(
        string sessionId,
        CancellationToken ct = default)
    {
        var path = GetSessionPath(sessionId);
        var state = GetSessionState(sessionId);
        await state.Gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var repaired = await RepairCoreAsync(sessionId, path, ct).ConfigureAwait(false);
            var scan = await ScanAsync(sessionId, path, ct).ConfigureAwait(false);
            state.LastSequence = scan.LastSequence;
            state.IsInitialized = true;
            await SynchronizeIndexesAsync(sessionId, scan.Records, ct).ConfigureAwait(false);
            return repaired;
        }
        finally
        {
            state.Gate.Release();
        }
    }

    public async Task<long> GetLastSequenceAsync(
        string sessionId,
        CancellationToken ct = default)
    {
        var path = GetSessionPath(sessionId);
        var state = GetSessionState(sessionId);
        await state.Gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            await EnsureInitializedAsync(sessionId, path, state, ct).ConfigureAwait(false);
            return state.LastSequence;
        }
        finally
        {
            state.Gate.Release();
        }
    }

    private async Task AppendAllocatedRecordAsync(
        string sessionId,
        string mode,
        string path,
        SessionState state,
        ConversationRecordV1 record)
    {
        record = await OffloadOversizedContentAsync(record, CancellationToken.None).ConfigureAwait(false);
        var recordBytes = JsonSerializer.SerializeToUtf8Bytes(
            record,
            ConversationJsonContext.Default.ConversationRecordV1);
        if (recordBytes.Length > _options.MaxJsonLineBytes)
        {
            throw new InvalidOperationException(
                $"Conversation record size {recordBytes.Length} exceeds the {_options.MaxJsonLineBytes}-byte JSONL limit after Blob offload.");
        }

        RecordLocation location;
        try
        {
            await InjectFailureAsync(ConversationStoreFailurePoint.BeforeFileAppend)
                .ConfigureAwait(false);
            location = await AppendRecordCoreAsync(
                sessionId,
                mode,
                path,
                recordBytes).ConfigureAwait(false);
            state.LastSequence = record.Sequence;
        }
        catch
        {
            state.IsInitialized = false;
            throw;
        }

        try
        {
            await InjectFailureAsync(ConversationStoreFailurePoint.BeforeIndexWrite)
                .ConfigureAwait(false);
            await InsertIndexAsync(
                sessionId,
                record,
                location.Offset,
                location.Length,
                CancellationToken.None).ConfigureAwait(false);
            await InjectFailureAsync(ConversationStoreFailurePoint.AfterIndexWrite)
                .ConfigureAwait(false);
            state.NeedsIndexRepair = false;
        }
        catch
        {
            state.NeedsIndexRepair = true;
            throw;
        }
    }

    private static void ValidateDraft(ConversationMessageDraft message)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(message.InvocationId);
        ArgumentException.ThrowIfNullOrWhiteSpace(message.AgentId);
        ValidateRoleAndContent(message.Role, message.Content);
    }

    private static void ValidateRecord(ConversationRecordV1 record)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(record.MessageId);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(record.Sequence);
        ArgumentException.ThrowIfNullOrWhiteSpace(record.InvocationId);
        ArgumentException.ThrowIfNullOrWhiteSpace(record.AgentId);
        ValidateRoleAndContent(record.Role, record.Content);
    }

    private static void ValidateRoleAndContent(string role, JsonElement content)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(role);
        if (role is not ("user" or "assistant" or "tool"))
        {
            throw new ArgumentOutOfRangeException(nameof(role), "The message role is not supported.");
        }

        if (content.ValueKind is not JsonValueKind.Array)
        {
            throw new ArgumentException("Conversation content must be a JSON array.", nameof(content));
        }

        foreach (var block in content.EnumerateArray())
        {
            if (block.ValueKind is not JsonValueKind.Object
                || !block.TryGetProperty("type", out var discriminator)
                || discriminator.ValueKind is not JsonValueKind.String
                || discriminator.GetString() is not (
                    "text" or "reasoning" or "tool_call" or "tool_result" or "blob_ref"))
            {
                throw new ArgumentException(
                    "Conversation content contains an unsupported protocol block.",
                    nameof(content));
            }
        }
    }

    private async Task EnsureInitializedAsync(
        string sessionId,
        string path,
        SessionState state,
        CancellationToken ct)
    {
        if (state.IsInitialized && !state.NeedsIndexRepair)
        {
            return;
        }

        await RepairCoreAsync(sessionId, path, ct).ConfigureAwait(false);
        var scan = await ScanAsync(sessionId, path, ct).ConfigureAwait(false);
        state.LastSequence = scan.LastSequence;
        state.IsInitialized = true;
        await SynchronizeIndexesAsync(sessionId, scan.Records, ct).ConfigureAwait(false);
        state.NeedsIndexRepair = false;
    }

    private async Task<RecordLocation> AppendRecordCoreAsync(
        string sessionId,
        string mode,
        string path,
        byte[] recordBytes)
    {
        await using var stream = new FileStream(path, new FileStreamOptions
        {
            Mode = FileMode.OpenOrCreate,
            Access = FileAccess.ReadWrite,
            Share = FileShare.Read,
            BufferSize = 4096,
            Options = FileOptions.Asynchronous | FileOptions.SequentialScan | FileOptions.WriteThrough
        });
        stream.Seek(0, SeekOrigin.End);
        if (stream.Length == 0)
        {
            var header = new ConversationHeader(
                HeaderSchema,
                sessionId,
                mode,
                DateTimeOffset.UtcNow);
            var headerBytes = JsonSerializer.SerializeToUtf8Bytes(
                header,
                ConversationJsonContext.Default.ConversationHeader);
            await stream.WriteAsync(headerBytes, CancellationToken.None).ConfigureAwait(false);
            await stream.WriteAsync(NewLineBytes, CancellationToken.None).ConfigureAwait(false);
        }
        else if (!await EndsWithNewLineAsync(stream).ConfigureAwait(false))
        {
            stream.Seek(0, SeekOrigin.End);
            await stream.WriteAsync(NewLineBytes, CancellationToken.None).ConfigureAwait(false);
        }

        var offset = stream.Position;
        var prefixLength = Math.Max(1, recordBytes.Length / 2);
        await stream.WriteAsync(recordBytes.AsMemory(0, prefixLength), CancellationToken.None)
            .ConfigureAwait(false);
        await InjectFailureAsync(ConversationStoreFailurePoint.AfterRecordPrefix)
            .ConfigureAwait(false);
        await stream.WriteAsync(recordBytes.AsMemory(prefixLength), CancellationToken.None)
            .ConfigureAwait(false);
        await InjectFailureAsync(ConversationStoreFailurePoint.AfterRecordBytes)
            .ConfigureAwait(false);
        await stream.WriteAsync(NewLineBytes, CancellationToken.None).ConfigureAwait(false);
        await InjectFailureAsync(ConversationStoreFailurePoint.AfterLineTerminator)
            .ConfigureAwait(false);
        await stream.FlushAsync(CancellationToken.None).ConfigureAwait(false);
        stream.Flush(flushToDisk: true);
        await InjectFailureAsync(ConversationStoreFailurePoint.AfterFileFlush)
            .ConfigureAwait(false);
        return new RecordLocation(offset, recordBytes.LongLength + 1);
    }

    private static async Task<bool> EndsWithNewLineAsync(FileStream stream)
    {
        stream.Seek(-1, SeekOrigin.End);
        var buffer = new byte[1];
        var read = await stream.ReadAsync(buffer, CancellationToken.None).ConfigureAwait(false);
        stream.Seek(0, SeekOrigin.End);
        return read == 1 && buffer[0] == (byte)'\n';
    }

    private string GetSessionPath(string sessionId)
    {
        ValidateSessionId(sessionId);
        return Path.Combine(_messagesDirectory, $"{sessionId}.jsonl");
    }

    private SessionState GetSessionState(string sessionId) =>
        _sessionStates.GetOrAdd(sessionId, static _ => new SessionState());

    private void EnterWriteQueue(SessionState state)
    {
        var pendingWrites = Interlocked.Increment(ref state.PendingWrites);
        if (pendingWrites <= _options.MaxPendingWritesPerSession)
        {
            return;
        }

        Interlocked.Decrement(ref state.PendingWrites);
        throw new InvalidOperationException(
            $"The Session write queue has reached its {_options.MaxPendingWritesPerSession}-item limit.");
    }

    private static void ExitWriteQueue(SessionState state) =>
        Interlocked.Decrement(ref state.PendingWrites);

    private ValueTask InjectFailureAsync(ConversationStoreFailurePoint point) =>
        _options.FailureInjector is { } injector
            ? injector(point, CancellationToken.None)
            : ValueTask.CompletedTask;

    private static void ValidateSessionId(string sessionId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionId);
        if (sessionId is "." or ".."
            || Path.IsPathRooted(sessionId)
            || sessionId.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0
            || sessionId.Contains(Path.DirectorySeparatorChar)
            || sessionId.Contains(Path.AltDirectorySeparatorChar))
        {
            throw new ArgumentException("The Session ID is not valid for storage.", nameof(sessionId));
        }
    }

    private async Task<bool> RepairCoreAsync(
        string sessionId,
        string path,
        CancellationToken ct)
    {
        var scan = await ScanAsync(sessionId, path, ct).ConfigureAwait(false);
        if (scan.InvalidLastLineOffset is not { } validLength)
        {
            return false;
        }

        var backupPath = string.Concat(
            path,
            ".",
            DateTimeOffset.UtcNow.ToString("yyyyMMddHHmmssfff", CultureInfo.InvariantCulture),
            ".",
            Guid.NewGuid().ToString("N"),
            ".bak");
        File.Copy(path, backupPath, overwrite: false);

        await using (var stream = new FileStream(path, new FileStreamOptions
        {
            Mode = FileMode.Open,
            Access = FileAccess.Write,
            Share = FileShare.Read,
            BufferSize = 4096,
            Options = FileOptions.Asynchronous | FileOptions.WriteThrough
        }))
        {
            stream.SetLength(validLength);
            await stream.FlushAsync(CancellationToken.None).ConfigureAwait(false);
            stream.Flush(flushToDisk: true);
        }

        await DeleteIndexesAtOrAfterAsync(
            sessionId,
            validLength,
            CancellationToken.None).ConfigureAwait(false);
        return true;
    }

    private async Task<ScanResult> ScanAsync(
        string sessionId,
        string path,
        CancellationToken ct)
    {
        if (!File.Exists(path))
        {
            return new ScanResult([], 0, null);
        }

        await using var stream = new FileStream(path, new FileStreamOptions
        {
            Mode = FileMode.Open,
            Access = FileAccess.Read,
            Share = FileShare.ReadWrite,
            BufferSize = 64 * 1024,
            Options = FileOptions.Asynchronous | FileOptions.SequentialScan
        });
        var fileLength = stream.Length;
        if (fileLength == 0)
        {
            return new ScanResult([], 0, null);
        }

        var records = new List<IndexedRecord>();
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

                    AppendLineBytes(lineBuffer, rentedBuffer.AsSpan(segmentStart, index - segmentStart));
                    var lineEnd = bufferOffset + index + 1;
                    var isLastLine = lineEnd == fileLength;
                    if (!TryProcessLine(
                            sessionId,
                            lineNumber,
                            lineStart,
                            lineEnd - lineStart,
                            lineBuffer.WrittenSpan,
                            lastSequence,
                            records,
                            isLastLine,
                            out var nextSequence))
                    {
                        return new ScanResult(records, lastSequence, lineStart);
                    }

                    lastSequence = nextSequence;
                    lineNumber++;
                    lineStart = lineEnd;
                    lineBuffer.Clear();
                    segmentStart = index + 1;
                }

                AppendLineBytes(lineBuffer, rentedBuffer.AsSpan(segmentStart, read - segmentStart));
                if (lineBuffer.WrittenCount > _options.MaxJsonLineBytes)
                {
                    throw new InvalidDataException(
                        $"Conversation '{sessionId}' contains a JSONL line larger than {_options.MaxJsonLineBytes} bytes.");
                }

                bufferOffset += read;
            }

            if (lineBuffer.WrittenCount > 0)
            {
                if (!TryProcessLine(
                        sessionId,
                        lineNumber,
                        lineStart,
                        fileLength - lineStart,
                        lineBuffer.WrittenSpan,
                        lastSequence,
                        records,
                        isLastLine: true,
                        out var nextSequence))
                {
                    return new ScanResult(records, lastSequence, lineStart);
                }

                lastSequence = nextSequence;
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(rentedBuffer);
        }

        return new ScanResult(records, lastSequence, null);
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

    private static bool TryProcessLine(
        string sessionId,
        int lineNumber,
        long lineStart,
        long lineLength,
        ReadOnlySpan<byte> rawLine,
        long lastSequence,
        List<IndexedRecord> records,
        bool isLastLine,
        out long nextSequence)
    {
        nextSequence = lastSequence;
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
                ValidateHeader(sessionId, header);
                return true;
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

            records.Add(new IndexedRecord(record, lineStart, lineLength));
            nextSequence = record.Sequence;
            return true;
        }
        catch (Exception ex) when (IsInvalidRecordException(ex))
        {
            if (isLastLine)
            {
                return false;
            }

            throw new InvalidDataException(
                $"Conversation '{sessionId}' contains a damaged line before the end of the file.",
                ex);
        }
    }

    private static void ValidateHeader(string sessionId, ConversationHeader header)
    {
        if (!string.Equals(header.Schema, HeaderSchema, StringComparison.Ordinal)
            || !string.Equals(header.SessionId, sessionId, StringComparison.Ordinal)
            || string.IsNullOrWhiteSpace(header.Mode))
        {
            throw new InvalidDataException("The conversation header is invalid.");
        }
    }

    private static bool IsInvalidRecordException(Exception exception) =>
        exception is JsonException
            or NotSupportedException
            or ArgumentException
            or InvalidDataException;

    private async Task<ConversationHeader> ReadHeaderAsync(
        string sessionId,
        string path,
        CancellationToken ct)
    {
        await using var stream = new FileStream(path, new FileStreamOptions
        {
            Mode = FileMode.Open,
            Access = FileAccess.Read,
            Share = FileShare.ReadWrite,
            BufferSize = 4096,
            Options = FileOptions.Asynchronous | FileOptions.SequentialScan
        });
        using var reader = new StreamReader(stream, Encoding.UTF8);
        var headerLine = await reader.ReadLineAsync(ct).ConfigureAwait(false);
        if (string.IsNullOrEmpty(headerLine))
        {
            throw new InvalidDataException(
                $"Conversation '{sessionId}' is missing a header line.");
        }

        if (Encoding.UTF8.GetByteCount(headerLine) > _options.MaxJsonLineBytes)
        {
            throw new InvalidDataException(
                $"Conversation '{sessionId}' header exceeds the {_options.MaxJsonLineBytes}-byte limit.");
        }

        ConversationHeader header;
        try
        {
            header = JsonSerializer.Deserialize(
                headerLine,
                ConversationJsonContext.Default.ConversationHeader)
                ?? throw new JsonException("The conversation header was empty.");
        }
        catch (JsonException ex)
        {
            throw new InvalidDataException(
                $"Conversation '{sessionId}' contains an invalid header.", ex);
        }

        ValidateHeader(sessionId, header);
        return header;
    }

    private Task SynchronizeIndexesAsync(
        string sessionId,
        IReadOnlyList<IndexedRecord> records,
        CancellationToken ct) =>
        SynchronizeIndexesAsync(sessionId, records, header: null, ct);

    private async Task SynchronizeIndexesAsync(
        string sessionId,
        IReadOnlyList<IndexedRecord> records,
        ConversationHeader? header,
        CancellationToken ct)
    {
        await using var connection = await OpenIndexConnectionAsync(ct).ConfigureAwait(false);
        using var transaction = connection.BeginTransaction(deferred: false);

        if (header is not null)
        {
            var lastRecordTimestamp = records.Count > 0
                ? records[^1].Record.Timestamp.ToUniversalTime()
                : header.CreatedAt;
            var updatedAt = header.CreatedAt > lastRecordTimestamp
                ? header.CreatedAt
                : lastRecordTimestamp;
            var createdAtUtc = header.CreatedAt.ToUniversalTime()
                .ToString("O", CultureInfo.InvariantCulture);
            var updatedAtUtc = updatedAt.ToUniversalTime()
                .ToString("O", CultureInfo.InvariantCulture);

            await using (var sessionCmd = connection.CreateCommand())
            {
                sessionCmd.Transaction = transaction;
                sessionCmd.CommandText = """
                    INSERT INTO sessions(session_id, mode, status, created_at, updated_at)
                    VALUES($sessionId, $mode, 'Active', $createdAt, $updatedAt)
                    ON CONFLICT(session_id) DO UPDATE SET
                        mode = excluded.mode,
                        updated_at = MAX(sessions.updated_at, excluded.updated_at);
                    """;
                sessionCmd.Parameters.AddWithValue("$sessionId", sessionId);
                sessionCmd.Parameters.AddWithValue("$mode", header.Mode);
                sessionCmd.Parameters.AddWithValue("$createdAt", createdAtUtc);
                sessionCmd.Parameters.AddWithValue("$updatedAt", updatedAtUtc);
                await sessionCmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
            }
        }

        await using (var delete = connection.CreateCommand())
        {
            delete.Transaction = transaction;
            delete.CommandText = "DELETE FROM message_index WHERE session_id = $sessionId;";
            delete.Parameters.AddWithValue("$sessionId", sessionId);
            await delete.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        }

        foreach (var item in records)
        {
            await InsertIndexCoreAsync(
                connection,
                transaction,
                sessionId,
                item.Record,
                item.Offset,
                item.Length,
                ct).ConfigureAwait(false);
        }

        transaction.Commit();
    }

    private async Task InsertIndexAsync(
        string sessionId,
        ConversationRecordV1 record,
        long offset,
        long length,
        CancellationToken ct)
    {
        await using var connection = await OpenIndexConnectionAsync(ct).ConfigureAwait(false);
        await InsertIndexCoreAsync(
            connection,
            transaction: null,
            sessionId,
            record,
            offset,
            length,
            ct).ConfigureAwait(false);
    }

    private static async Task InsertIndexCoreAsync(
        SqliteConnection connection,
        SqliteTransaction? transaction,
        string sessionId,
        ConversationRecordV1 record,
        long offset,
        long length,
        CancellationToken ct)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        await using (var deleteConflict = connection.CreateCommand())
        {
            deleteConflict.Transaction = transaction;
            deleteConflict.CommandText = """
                DELETE FROM message_index
                WHERE session_id = $sessionId
                  AND sequence = $sequence
                  AND message_id <> $messageId;
                """;
            deleteConflict.Parameters.AddWithValue("$sessionId", sessionId);
            deleteConflict.Parameters.AddWithValue("$sequence", record.Sequence);
            deleteConflict.Parameters.AddWithValue("$messageId", record.MessageId);
            var removed = await deleteConflict.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
            if (removed > 0)
            {
                Trace.TraceWarning(
                    "Corrected {0} conflicting message index row(s) for Session {1}, sequence {2} from canonical JSONL.",
                    removed,
                    sessionId,
                    record.Sequence);
            }
        }

        command.CommandText = """
            INSERT INTO message_index(
                message_id,
                session_id,
                sequence,
                invocation_id,
                agent_id,
                role,
                file_offset,
                record_length,
                created_at)
            VALUES(
                $messageId,
                $sessionId,
                $sequence,
                $invocationId,
                $agentId,
                $role,
                $fileOffset,
                $recordLength,
                $createdAt)
            ON CONFLICT(message_id) DO UPDATE SET
                session_id = excluded.session_id,
                sequence = excluded.sequence,
                invocation_id = excluded.invocation_id,
                agent_id = excluded.agent_id,
                role = excluded.role,
                file_offset = excluded.file_offset,
                record_length = excluded.record_length,
                created_at = excluded.created_at;
            """;
        command.Parameters.AddWithValue("$messageId", record.MessageId);
        command.Parameters.AddWithValue("$sessionId", sessionId);
        command.Parameters.AddWithValue("$sequence", record.Sequence);
        command.Parameters.AddWithValue("$invocationId", record.InvocationId);
        command.Parameters.AddWithValue("$agentId", record.AgentId);
        command.Parameters.AddWithValue("$role", record.Role);
        command.Parameters.AddWithValue("$fileOffset", offset);
        command.Parameters.AddWithValue("$recordLength", length);
        command.Parameters.AddWithValue(
            "$createdAt",
            record.Timestamp.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture));
        await command.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
    }

    private async Task DeleteIndexesAtOrAfterAsync(
        string sessionId,
        long offset,
        CancellationToken ct)
    {
        await using var connection = await OpenIndexConnectionAsync(ct).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            DELETE FROM message_index
            WHERE session_id = $sessionId AND file_offset >= $fileOffset;
            """;
        command.Parameters.AddWithValue("$sessionId", sessionId);
        command.Parameters.AddWithValue("$fileOffset", offset);
        await command.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
    }

    private async Task<IReadOnlyList<IndexLocation>> LoadIndexPageAsync(
        string sessionId,
        long cursor,
        int pageSize,
        CancellationToken ct)
    {
        await using var connection = await OpenIndexConnectionAsync(ct).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT message_id, sequence, file_offset, record_length
            FROM message_index
            WHERE session_id = $sessionId AND sequence > $cursor
            ORDER BY sequence
            LIMIT $pageSize;
            """;
        command.Parameters.AddWithValue("$sessionId", sessionId);
        command.Parameters.AddWithValue("$cursor", cursor);
        command.Parameters.AddWithValue("$pageSize", pageSize);
        var entries = new List<IndexLocation>(pageSize);
        await using var reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);
        while (await reader.ReadAsync(ct).ConfigureAwait(false))
        {
            entries.Add(new IndexLocation(
                reader.GetString(0),
                reader.GetInt64(1),
                reader.GetInt64(2),
                reader.GetInt64(3)));
        }

        return entries;
    }

    private async Task<IReadOnlyList<ConversationRecordV1>> ReadIndexedRecordsAsync(
        string sessionId,
        string path,
        IReadOnlyList<IndexLocation> entries,
        CancellationToken ct)
    {
        if (entries.Count == 0)
        {
            return [];
        }

        await using var stream = new FileStream(path, new FileStreamOptions
        {
            Mode = FileMode.Open,
            Access = FileAccess.Read,
            Share = FileShare.ReadWrite,
            BufferSize = 4096,
            Options = FileOptions.Asynchronous | FileOptions.RandomAccess
        });
        var records = new List<ConversationRecordV1>(entries.Count);
        foreach (var entry in entries)
        {
            ct.ThrowIfCancellationRequested();
            if (entry.Offset < 0
                || entry.Length <= 0
                || entry.Length > _options.MaxJsonLineBytes + 2L
                || entry.Offset > stream.Length - entry.Length)
            {
                throw new InvalidDataException(
                    $"The message index for Session '{sessionId}', sequence {entry.Sequence} points outside canonical JSONL.");
            }

            var bytes = GC.AllocateUninitializedArray<byte>(checked((int)entry.Length));
            stream.Seek(entry.Offset, SeekOrigin.Begin);
            await stream.ReadExactlyAsync(bytes, ct).ConfigureAwait(false);
            var jsonLength = bytes.Length;
            if (jsonLength > 0 && bytes[jsonLength - 1] == (byte)'\n')
            {
                jsonLength--;
            }

            if (jsonLength > 0 && bytes[jsonLength - 1] == (byte)'\r')
            {
                jsonLength--;
            }

            var record = JsonSerializer.Deserialize(
                bytes.AsSpan(0, jsonLength),
                ConversationJsonContext.Default.ConversationRecordV1)
                ?? throw new InvalidDataException("An indexed conversation record was empty.");
            ValidateRecord(record);
            if (!string.Equals(record.MessageId, entry.MessageId, StringComparison.Ordinal)
                || record.Sequence != entry.Sequence)
            {
                throw new InvalidDataException(
                    $"The message index for Session '{sessionId}', sequence {entry.Sequence} does not match canonical JSONL.");
            }

            records.Add(record);
        }

        return records;
    }

    private async Task<SqliteConnection> OpenIndexConnectionAsync(CancellationToken ct)
    {
        var connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = _databasePath,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Cache = SqliteCacheMode.Shared,
            Pooling = false,
            DefaultTimeout = 5
        }.ToString();
        var connection = new SqliteConnection(connectionString);
        try
        {
            await connection.OpenAsync(ct).ConfigureAwait(false);
            await using var command = connection.CreateCommand();
            command.CommandText = """
                PRAGMA busy_timeout = 5000;
                CREATE TABLE IF NOT EXISTS sessions(
                    session_id TEXT PRIMARY KEY,
                    mode TEXT NOT NULL,
                    status TEXT NOT NULL,
                    created_at TEXT NOT NULL,
                    updated_at TEXT NOT NULL
                );
                CREATE INDEX IF NOT EXISTS idx_sessions_updated ON sessions(updated_at, session_id);
                CREATE INDEX IF NOT EXISTS idx_sessions_status ON sessions(status);
                CREATE TABLE IF NOT EXISTS message_index(
                    message_id TEXT PRIMARY KEY,
                    session_id TEXT NOT NULL,
                    sequence INTEGER NOT NULL,
                    invocation_id TEXT NOT NULL,
                    agent_id TEXT NOT NULL,
                    role TEXT NOT NULL,
                    file_offset INTEGER NOT NULL,
                    record_length INTEGER NOT NULL,
                    created_at TEXT NOT NULL
                );
                CREATE UNIQUE INDEX IF NOT EXISTS idx_message_index_session_sequence
                ON message_index(session_id, sequence);
                """;
            await command.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
            return connection;
        }
        catch
        {
            await connection.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }


    public ConversationBlobStore BlobStore => _blobStore;

    public async Task<ConversationHistoryMetadata> GetHistoryMetadataAsync(
        string sessionId,
        CancellationToken ct = default)
    {
        var records = await ReadAllAsync(sessionId, new ConversationReadOptions(ConversationBlobReadMode.Metadata), ct)
            .ConfigureAwait(false);
        if (records.Count == 0)
        {
            return new ConversationHistoryMetadata(0, null, null, 0, []);
        }

        var last = records[^1];
        var blobIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var record in records)
        {
            CollectBlobIds(record.Content, blobIds);
        }

        return new ConversationHistoryMetadata(
            records.Count,
            last.Sequence,
            last.MessageId,
            blobIds.Count,
            blobIds.OrderBy(static id => id, StringComparer.Ordinal).Take(32).ToArray());
    }

    public async Task<ConversationIndexRebuildResult> RebuildIndexesAsync(
        CancellationToken ct = default)
    {
        Directory.CreateDirectory(_messagesDirectory);
        var backupDir = Path.Combine(
            Path.GetDirectoryName(_databasePath)!,
            "backups",
            "rebuild-" + DateTimeOffset.UtcNow.ToString("yyyyMMddHHmmssfff", CultureInfo.InvariantCulture));
        Directory.CreateDirectory(backupDir);
        if (File.Exists(_databasePath))
        {
            File.Copy(
                _databasePath,
                Path.Combine(backupDir, "state.db"),
                overwrite: false);
        }

        var recoverable = new List<string>();
        var unrecoverable = new List<string>
        {
            "Run state cannot be reconstructed from JSONL alone.",
            "Tool Grants cannot be reconstructed from JSONL alone.",
            "Tool intents cannot be reconstructed from JSONL alone.",
            "Invocation snapshots cannot be reconstructed from JSONL alone."
        };

        var files = Directory.EnumerateFiles(_messagesDirectory, "*.jsonl")
            .OrderBy(static path => path, StringComparer.Ordinal)
            .ToArray();
        var sessionsRebuilt = 0;
        var messagesUpserted = 0;
        foreach (var file in files)
        {
            ct.ThrowIfCancellationRequested();
            var sessionId = Path.GetFileNameWithoutExtension(file);
            try
            {
                ValidateSessionId(sessionId);
                var state = GetSessionState(sessionId);
                await state.Gate.WaitAsync(ct).ConfigureAwait(false);
                try
                {
                    await RepairCoreAsync(sessionId, file, ct).ConfigureAwait(false);
                    var header = await ReadHeaderAsync(sessionId, file, ct).ConfigureAwait(false);
                    var scan = await ScanAsync(sessionId, file, ct).ConfigureAwait(false);
                    state.LastSequence = scan.LastSequence;
                    state.IsInitialized = true;
                    await SynchronizeIndexesAsync(sessionId, scan.Records, header, ct).ConfigureAwait(false);
                    state.NeedsIndexRepair = false;
                    sessionsRebuilt++;
                    messagesUpserted += scan.Records.Count;
                    recoverable.Add(
                        $"Session '{sessionId}': restored Session metadata and {scan.Records.Count} message index row(s).");
                }
                finally
                {
                    state.Gate.Release();
                }
            }
            catch (Exception ex) when (ex is InvalidDataException or ArgumentException or JsonException)
            {
                unrecoverable.Add($"Session '{sessionId}' could not be rebuilt: {ex.Message}");
            }
        }

        return new ConversationIndexRebuildResult(
            files.Length,
            sessionsRebuilt,
            messagesUpserted,
            [.. recoverable],
            [.. unrecoverable],
            backupDir);
    }

    private async Task<ConversationRecordV1> OffloadOversizedContentAsync(
        ConversationRecordV1 record,
        CancellationToken ct)
    {
        var recordBytes = JsonSerializer.SerializeToUtf8Bytes(
            record,
            ConversationJsonContext.Default.ConversationRecordV1);
        if (recordBytes.Length <= _options.MaxJsonLineBytes)
        {
            return record;
        }

        if (record.Content.ValueKind is not JsonValueKind.Array)
        {
            return record;
        }

        var contentNode = JsonNode.Parse(record.Content.GetRawText()) as JsonArray
            ?? throw new InvalidOperationException("Conversation content must be a JSON array.");
        while (true)
        {
            recordBytes = JsonSerializer.SerializeToUtf8Bytes(
                record with { Content = ToElement(contentNode) },
                ConversationJsonContext.Default.ConversationRecordV1);
            if (recordBytes.Length <= _options.MaxJsonLineBytes)
            {
                return record with
                {
                    Content = ToElement(contentNode)
                };
            }

            var candidateIndex = -1;
            var candidateBytes = 0;
            for (var index = 0; index < contentNode.Count; index++)
            {
                if (contentNode[index] is not JsonObject block
                    || block["type"]?.GetValue<string>() is not "text"
                    || block["text"]?.GetValue<string>() is not { Length: > 0 } text)
                {
                    continue;
                }

                var size = Encoding.UTF8.GetByteCount(text);
                if (size > candidateBytes)
                {
                    candidateBytes = size;
                    candidateIndex = index;
                }
            }

            if (candidateIndex < 0 || candidateBytes <= 0)
            {
                return record with
                {
                    Content = ToElement(contentNode)
                };
            }

            var textValue = contentNode[candidateIndex]!["text"]!.GetValue<string>();
            var blob = await _blobStore.StoreAsync(
                Encoding.UTF8.GetBytes(textValue),
                "text/plain; charset=utf-8",
                "conversation",
                expiresAt: DateTimeOffset.UtcNow.AddDays(365),
                ct).ConfigureAwait(false);
            contentNode[candidateIndex] = new JsonObject
            {
                ["type"] = "blob_ref",
                ["blob"] = new JsonObject
                {
                    ["blobId"] = blob.BlobId,
                    ["length"] = blob.Length,
                    ["sha256"] = blob.Sha256,
                    ["contentType"] = blob.ContentType,
                    ["accessScope"] = blob.AccessScope,
                    ["expiresAt"] = blob.ExpiresAt.ToString("O", CultureInfo.InvariantCulture)
                }
            };
        }
    }

    private async Task<IReadOnlyList<ConversationRecordV1>> ApplyReadOptionsAsync(
        IReadOnlyList<ConversationRecordV1> records,
        ConversationReadOptions? options,
        CancellationToken ct)
    {
        options ??= new ConversationReadOptions();
        if (options.BlobMode is ConversationBlobReadMode.Metadata
            || records.Count == 0)
        {
            return records;
        }

        var result = new ConversationRecordV1[records.Count];
        for (var index = 0; index < records.Count; index++)
        {
            ct.ThrowIfCancellationRequested();
            result[index] = await TransformRecordContentAsync(records[index], options, ct)
                .ConfigureAwait(false);
        }

        return result;
    }

    private async Task<ConversationRecordV1> TransformRecordContentAsync(
        ConversationRecordV1 record,
        ConversationReadOptions options,
        CancellationToken ct)
    {
        if (record.Content.ValueKind is not JsonValueKind.Array)
        {
            return record;
        }

        var contentNode = JsonNode.Parse(record.Content.GetRawText()) as JsonArray
            ?? throw new InvalidOperationException("Conversation content must be a JSON array.");
        var changed = false;
        for (var index = 0; index < contentNode.Count; index++)
        {
            if (contentNode[index] is not JsonObject block
                || block["type"]?.GetValue<string>() is not "blob_ref"
                || block["blob"] is not JsonObject blob)
            {
                continue;
            }

            var blobId = blob["blobId"]?.GetValue<string>();
            var length = blob["length"]?.GetValue<long>() ?? -1;
            if (string.IsNullOrWhiteSpace(blobId))
            {
                continue;
            }

            if (options.BlobMode is ConversationBlobReadMode.Omit
                || length < 0
                || length > options.MaxExpandedBlobBytes
                || !_blobStore.Exists(blobId))
            {
                contentNode[index] = new JsonObject
                {
                    ["type"] = "text",
                    ["text"] = $"[blob omitted: {blobId}]"
                };
                changed = true;
                continue;
            }

            if (options.BlobMode is ConversationBlobReadMode.Stream)
            {
                await using var stream = _blobStore.OpenRead(blobId);
                using var reader = new StreamReader(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
                var textValue = await reader.ReadToEndAsync(ct).ConfigureAwait(false);
                contentNode[index] = new JsonObject
                {
                    ["type"] = "text",
                    ["text"] = textValue
                };
                changed = true;
            }
        }

        return changed
            ? record with { Content = ToElement(contentNode) }
            : record;
    }


    private static JsonElement ToElement(JsonNode node)
    {
        using var document = JsonDocument.Parse(node.ToJsonString());
        return document.RootElement.Clone();
    }

    private static void CollectBlobIds(JsonElement content, HashSet<string> blobIds)
    {
        if (content.ValueKind is not JsonValueKind.Array)
        {
            return;
        }

        foreach (var block in content.EnumerateArray())
        {
            if (block.ValueKind is not JsonValueKind.Object
                || !block.TryGetProperty("type", out var type)
                || type.GetString() is not "blob_ref"
                || !block.TryGetProperty("blob", out var blob)
                || blob.ValueKind is not JsonValueKind.Object
                || !blob.TryGetProperty("blobId", out var blobId)
                || blobId.GetString() is not { Length: > 0 } id)
            {
                continue;
            }

            blobIds.Add(id);
        }
    }

    private sealed class SessionState
    {
        public SemaphoreSlim Gate { get; } = new(1, 1);

        public bool IsInitialized { get; set; }

        public bool NeedsIndexRepair { get; set; }

        public long LastSequence { get; set; }

        public int PendingWrites;
    }

    private sealed record IndexedRecord(ConversationRecordV1 Record, long Offset, long Length);

    private sealed record IndexLocation(
        string MessageId,
        long Sequence,
        long Offset,
        long Length);

    private sealed record ScanResult(
        IReadOnlyList<IndexedRecord> Records,
        long LastSequence,
        long? InvalidLastLineOffset);

    private readonly record struct RecordLocation(long Offset, long Length);
}

public sealed record ConversationHistoryMetadata(
    long MessageCount,
    long? LastMessageSeq,
    string? LastMessageId,
    int BlobReferenceCount,
    string[] BlobIds);
