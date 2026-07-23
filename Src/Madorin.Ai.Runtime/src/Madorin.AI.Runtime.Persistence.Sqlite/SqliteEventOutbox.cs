using System.Globalization;
using System.Text;
using Madorin.AI.Runtime.Persistence.Abstractions;
using Microsoft.Data.Sqlite;

namespace Madorin.AI.Runtime.Persistence.Sqlite;

/// <summary>SQLite-backed event Outbox with a transactionally allocated GSN.</summary>
public sealed class SqliteEventOutbox : IEventOutbox, IDisposable
{
    public const long DefaultMaxMemoryReplayBytes = 32L * 1024 * 1024;
    public const long DefaultMaxReplayBytes = 512L * 1024 * 1024;
    public const string DiagnosticsMeterName = "Madorin.AI.Runtime.Outbox";
    private const long CriticalReserveUpperBound = 1024L * 1024;

    private const string AcknowledgedStatus = "acknowledged";
    private const string PendingStatus = "pending";
    private const string SentStatus = "sent";

    private readonly SqliteConnection _connection;
    private readonly SortedDictionary<long, MemoryReplayEntry> _memoryReplay = [];
    private readonly long _maxMemoryReplayBytes;
    private readonly string? _replayDirectory;
    private readonly long _maxReplayBytes;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private long _backpressureCount;
    private long _droppedDeltaCount;
    private long _memoryReplayBytes;
    private long _memorySpillCount;
    private long _replayBytes;

    public SqliteEventOutbox(
        SqliteConnection connection,
        string? replayDirectory = null,
        long maxReplayBytes = DefaultMaxReplayBytes,
        long maxMemoryReplayBytes = DefaultMaxMemoryReplayBytes)
    {
        _connection = connection ?? throw new ArgumentNullException(nameof(connection));
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maxReplayBytes);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maxMemoryReplayBytes);

        _maxMemoryReplayBytes = maxMemoryReplayBytes;
        _replayDirectory = string.IsNullOrWhiteSpace(replayDirectory)
            ? null
            : Path.GetFullPath(replayDirectory);
        _maxReplayBytes = maxReplayBytes;
        if (_replayDirectory is not null)
        {
            Directory.CreateDirectory(_replayDirectory);
            _replayBytes = Directory.EnumerateFiles(_replayDirectory, "*.evt")
                .Select(static path => new FileInfo(path).Length)
                .Sum();
        }

        EventOutboxMetrics.RecordConfiguredCapacity(
            _maxMemoryReplayBytes,
            _maxReplayBytes);
        EventOutboxMetrics.RecordRetainedBytes(_memoryReplayBytes, _replayBytes);
    }

    public async Task<long> AppendAsync(
        string runId,
        long runSequence,
        string messageType,
        string payloadJson,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(runId);
        ArgumentException.ThrowIfNullOrWhiteSpace(messageType);
        ArgumentNullException.ThrowIfNull(payloadJson);
        ArgumentOutOfRangeException.ThrowIfNegative(runSequence);

        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var payloadBytes = Encoding.UTF8.GetByteCount(payloadJson);
            return await AppendCoreAsync(
                runId,
                runSequence,
                messageType,
                payloadJson,
                payloadBytes,
                ct).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<long?> AppendDeltaAsync(
        string runId,
        long runSequence,
        string messageType,
        string payloadJson,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(runId);
        ArgumentException.ThrowIfNullOrWhiteSpace(messageType);
        ArgumentNullException.ThrowIfNull(payloadJson);
        ArgumentOutOfRangeException.ThrowIfNegative(runSequence);

        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var payloadBytes = Encoding.UTF8.GetByteCount(payloadJson);
            if (!HasReplayCapacity(payloadBytes, ReserveForCriticalEvents()))
            {
                Interlocked.Increment(ref _droppedDeltaCount);
                RecordBackpressure("disk", "quota");
                EventOutboxMetrics.RecordDeltaDropped();
                return null;
            }

            return await AppendCoreAsync(
                runId,
                runSequence,
                messageType,
                payloadJson,
                payloadBytes,
                ct).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>Returns the bytes retained by the disk replay mirror.</summary>
    public long ReplayBytes => Interlocked.Read(ref _replayBytes);

    /// <summary>Returns the bytes retained by the bounded in-memory replay tier.</summary>
    public long MemoryReplayBytes => Interlocked.Read(ref _memoryReplayBytes);

    /// <summary>Returns the number of events spilled from memory to persistent replay.</summary>
    public long MemorySpillCount => Interlocked.Read(ref _memorySpillCount);

    /// <summary>Returns the number of Delta events dropped before GSN allocation.</summary>
    public long DroppedDeltaCount => Interlocked.Read(ref _droppedDeltaCount);

    /// <summary>Returns the number of capacity-driven backpressure actions.</summary>
    public long BackpressureCount => Interlocked.Read(ref _backpressureCount);

    /// <summary>Gets the next Run-local sequence for an out-of-band critical event.</summary>
    public async Task<long> GetNextRunSequenceAsync(
        string runId,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(runId);
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            await using var command = _connection.CreateCommand();
            command.CommandText = """
                SELECT COALESCE(MAX(run_sequence), -1) + 1
                FROM event_outbox
                WHERE run_id = $runId;
                """;
            command.Parameters.AddWithValue("$runId", runId);
            var value = await command.ExecuteScalarAsync(ct).ConfigureAwait(false);
            return Convert.ToInt64(value, CultureInfo.InvariantCulture);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<bool> ContainsAsync(
        string runId,
        string messageType,
        string payloadJson,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(runId);
        ArgumentException.ThrowIfNullOrWhiteSpace(messageType);
        ArgumentNullException.ThrowIfNull(payloadJson);

        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            await using var command = _connection.CreateCommand();
            command.CommandText = """
                SELECT EXISTS(
                    SELECT 1
                    FROM event_outbox
                    WHERE run_id = $runId
                      AND message_type = $messageType
                      AND payload = $payload);
                """;
            command.Parameters.AddWithValue("$runId", runId);
            command.Parameters.AddWithValue("$messageType", messageType);
            command.Parameters.AddWithValue("$payload", payloadJson);
            return Convert.ToInt64(
                await command.ExecuteScalarAsync(ct).ConfigureAwait(false),
                CultureInfo.InvariantCulture) != 0;
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task<long> AppendCoreAsync(
        string runId,
        long runSequence,
        string messageType,
        string payloadJson,
        int payloadBytes,
        CancellationToken ct)
    {
        using var transaction = _connection.BeginTransaction(deferred: false);

        await using var updateCommand = _connection.CreateCommand();
        updateCommand.Transaction = transaction;
        updateCommand.CommandText = """
            UPDATE gsn_sequence
            SET next_gsn = next_gsn + 1
            WHERE id = 1;
            """;
        var affected = await updateCommand.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        if (affected != 1)
        {
            throw new InvalidDataException("The GSN sequence seed row is missing.");
        }

        await using var selectCommand = _connection.CreateCommand();
        selectCommand.Transaction = transaction;
        selectCommand.CommandText = "SELECT next_gsn - 1 FROM gsn_sequence WHERE id = 1;";
        var value = await selectCommand.ExecuteScalarAsync(ct).ConfigureAwait(false);
        var gsn = Convert.ToInt64(value, CultureInfo.InvariantCulture);

        await using var insertCommand = _connection.CreateCommand();
        insertCommand.Transaction = transaction;
        insertCommand.CommandText = """
            INSERT INTO event_outbox(
                gsn,
                run_id,
                run_sequence,
                message_type,
                payload,
                status,
                created_at)
            VALUES(
                $gsn,
                $runId,
                $runSequence,
                $messageType,
                $payload,
                $status,
                $createdAt);
            """;
        insertCommand.Parameters.AddWithValue("$gsn", gsn);
        insertCommand.Parameters.AddWithValue("$runId", runId);
        insertCommand.Parameters.AddWithValue("$runSequence", runSequence);
        insertCommand.Parameters.AddWithValue("$messageType", messageType);
        insertCommand.Parameters.AddWithValue("$payload", payloadJson);
        insertCommand.Parameters.AddWithValue("$status", PendingStatus);
        insertCommand.Parameters.AddWithValue("$createdAt", FormatTimestamp(DateTimeOffset.UtcNow));
        await insertCommand.ExecuteNonQueryAsync(ct).ConfigureAwait(false);

        transaction.Commit();
        await WriteReplayCopyAsync(gsn, payloadJson, payloadBytes, ct).ConfigureAwait(false);
        CacheInMemory(
            new OutboxEntry(gsn, runId, runSequence, messageType, payloadJson),
            payloadBytes);
        return gsn;
    }

    public async Task MarkSentAsync(long gsn, CancellationToken ct = default)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(gsn);
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            await using var command = _connection.CreateCommand();
            command.CommandText = """
                UPDATE event_outbox
                SET status = $sent
                WHERE gsn = $gsn AND status = $pending;
                """;
            command.Parameters.AddWithValue("$sent", SentStatus);
            command.Parameters.AddWithValue("$pending", PendingStatus);
            command.Parameters.AddWithValue("$gsn", gsn);
            await command.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task AcknowledgeAsync(long lastConfirmedGsn, CancellationToken ct = default)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(lastConfirmedGsn);
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            using var transaction = _connection.BeginTransaction(deferred: false);
            await using var command = _connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = """
                UPDATE event_outbox
                SET status = $acknowledged,
                    acked_at = $ackedAt
                WHERE gsn <= $lastConfirmedGsn
                  AND acked_at IS NULL;
                """;
            command.Parameters.AddWithValue("$acknowledged", AcknowledgedStatus);
            command.Parameters.AddWithValue("$ackedAt", FormatTimestamp(DateTimeOffset.UtcNow));
            command.Parameters.AddWithValue("$lastConfirmedGsn", lastConfirmedGsn);
            await command.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
            transaction.Commit();
            DeleteMemoryReplayThrough(lastConfirmedGsn);
            DeleteReplayCopiesThrough(lastConfirmedGsn);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<IReadOnlyList<OutboxEntry>> LoadPendingAsync(CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            await using var command = _connection.CreateCommand();
            command.CommandText = """
                SELECT gsn, run_id, run_sequence, message_type, payload
                FROM event_outbox
                WHERE acked_at IS NULL
                  AND status IN ($pending, $sent)
                ORDER BY gsn;
                """;
            command.Parameters.AddWithValue("$pending", PendingStatus);
            command.Parameters.AddWithValue("$sent", SentStatus);

            var entries = new List<OutboxEntry>();
            await using var reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);
            while (await reader.ReadAsync(ct).ConfigureAwait(false))
            {
                entries.Add(new OutboxEntry(
                    reader.GetInt64(0),
                    reader.GetString(1),
                    reader.GetInt64(2),
                    reader.GetString(3),
                    reader.GetString(4)));
            }

            foreach (var entry in entries)
            {
                CacheInMemory(entry, Encoding.UTF8.GetByteCount(entry.PayloadJson));
            }

            return entries;
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<long> GetAcknowledgedGsnAsync(CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            await using var command = _connection.CreateCommand();
            command.CommandText = """
                SELECT COALESCE(MAX(gsn), 0)
                FROM event_outbox
                WHERE status = $acknowledged AND acked_at IS NOT NULL;
                """;
            command.Parameters.AddWithValue("$acknowledged", AcknowledgedStatus);
            var value = await command.ExecuteScalarAsync(ct).ConfigureAwait(false);
            return Convert.ToInt64(value, CultureInfo.InvariantCulture);
        }
        finally
        {
            _gate.Release();
        }
    }

    public void Dispose()
    {
        _gate.Dispose();
    }

    private static string FormatTimestamp(DateTimeOffset value) =>
        value.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture);

    private bool HasReplayCapacity(int payloadBytes, long reserveBytes = 0) =>
        _replayDirectory is null
        || payloadBytes + reserveBytes <= _maxReplayBytes - Interlocked.Read(ref _replayBytes);

    private long ReserveForCriticalEvents() =>
        Math.Min(CriticalReserveUpperBound, Math.Max(64, _maxReplayBytes / 4));

    private async Task WriteReplayCopyAsync(
        long gsn,
        string payloadJson,
        int payloadBytes,
        CancellationToken ct)
    {
        if (_replayDirectory is null)
        {
            return;
        }

        // Critical events remain durable in SQLite when the optional mirror is full.
        if (!HasReplayCapacity(payloadBytes))
        {
            return;
        }

        var path = Path.Combine(_replayDirectory, $"{gsn:D20}.evt");
        var temporary = path + ".tmp";
        await File.WriteAllBytesAsync(temporary, Encoding.UTF8.GetBytes(payloadJson), ct)
            .ConfigureAwait(false);
        File.Move(temporary, path, overwrite: false);
        Interlocked.Add(ref _replayBytes, payloadBytes);
        EventOutboxMetrics.RecordRetainedBytes(MemoryReplayBytes, ReplayBytes);
    }

    private void CacheInMemory(OutboxEntry entry, int payloadBytes)
    {
        if (_memoryReplay.ContainsKey(entry.Gsn))
        {
            return;
        }

        if (payloadBytes > _maxMemoryReplayBytes)
        {
            RecordMemorySpill();
            return;
        }

        while (_memoryReplay.Count > 0
               && payloadBytes > _maxMemoryReplayBytes - _memoryReplayBytes)
        {
            var oldest = _memoryReplay.First();
            _memoryReplay.Remove(oldest.Key);
            Interlocked.Add(ref _memoryReplayBytes, -oldest.Value.PayloadBytes);
            RecordMemorySpill();
        }

        _memoryReplay.Add(entry.Gsn, new MemoryReplayEntry(entry, payloadBytes));
        Interlocked.Add(ref _memoryReplayBytes, payloadBytes);
        EventOutboxMetrics.RecordRetainedBytes(MemoryReplayBytes, ReplayBytes);
    }

    private void DeleteMemoryReplayThrough(long gsn)
    {
        foreach (var entry in _memoryReplay.TakeWhile(pair => pair.Key <= gsn).ToArray())
        {
            _memoryReplay.Remove(entry.Key);
            Interlocked.Add(ref _memoryReplayBytes, -entry.Value.PayloadBytes);
        }

        EventOutboxMetrics.RecordRetainedBytes(MemoryReplayBytes, ReplayBytes);
    }

    private void DeleteReplayCopiesThrough(long gsn)
    {
        if (_replayDirectory is null)
        {
            return;
        }

        foreach (var path in Directory.EnumerateFiles(_replayDirectory, "*.evt"))
        {
            var name = Path.GetFileNameWithoutExtension(path);
            if (!long.TryParse(
                    name,
                    NumberStyles.None,
                    CultureInfo.InvariantCulture,
                    out var fileGsn)
                || fileGsn > gsn)
            {
                continue;
            }

            var length = new FileInfo(path).Length;
            try
            {
                File.Delete(path);
            }
            catch (IOException)
            {
                continue;
            }

            Interlocked.Add(ref _replayBytes, -length);
        }

        EventOutboxMetrics.RecordRetainedBytes(MemoryReplayBytes, ReplayBytes);
    }

    private void RecordMemorySpill()
    {
        Interlocked.Increment(ref _memorySpillCount);
        RecordBackpressure("memory", "spill");
        EventOutboxMetrics.RecordMemorySpill();
    }

    private void RecordBackpressure(string tier, string reason)
    {
        Interlocked.Increment(ref _backpressureCount);
        EventOutboxMetrics.RecordBackpressure(tier, reason);
    }

    private sealed record MemoryReplayEntry(OutboxEntry Entry, int PayloadBytes);
}
