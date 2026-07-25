using System.Text.Json;

namespace Madorin.AI.Runtime.Persistence.Files;

/// <summary>Describes one append-only Session maintenance audit event.</summary>
public sealed record SessionMaintenanceAuditRecord(
    string Schema,
    string AuditId,
    string RuntimeInstanceId,
    string SessionId,
    string Operation,
    string Phase,
    string? Outcome,
    string Detail,
    DateTimeOffset CreatedAt);

/// <summary>Persists Session maintenance audit events outside deletable Session state.</summary>
public sealed class SessionMaintenanceAuditLog : IDisposable
{
    public const string CurrentSchema = "madorin.session-maintenance-audit.v1";

    private static readonly byte[] NewLine = [(byte)'\n'];
    private readonly SemaphoreSlim _writeGate = new(1, 1);

    public SessionMaintenanceAuditLog(string dataDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(dataDirectory);
        var normalizedDataDirectory = Path.GetFullPath(dataDirectory);
        FilePath = Path.Combine(normalizedDataDirectory, "audit", "session-maintenance.jsonl");
    }

    /// <summary>Gets the append-only audit file path.</summary>
    public string FilePath { get; }

    /// <summary>Appends and flushes one complete audit event.</summary>
    public async Task AppendAsync(
        SessionMaintenanceAuditRecord record,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(record);
        Validate(record);

        var payload = JsonSerializer.SerializeToUtf8Bytes(
            record,
            ConversationJsonContext.Default.SessionMaintenanceAuditRecord);
        var line = new byte[payload.Length + NewLine.Length];
        payload.CopyTo(line, 0);
        NewLine.CopyTo(line, payload.Length);

        await _writeGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var directory = Path.GetDirectoryName(FilePath)
                ?? throw new InvalidDataException("The Session audit path has no parent directory.");
            Directory.CreateDirectory(directory);
            await using var stream = new FileStream(FilePath, new FileStreamOptions
            {
                Mode = FileMode.Append,
                Access = FileAccess.Write,
                Share = FileShare.Read,
                Options = FileOptions.Asynchronous | FileOptions.WriteThrough
            });
            await stream.WriteAsync(line, ct).ConfigureAwait(false);
            await stream.FlushAsync(ct).ConfigureAwait(false);
            stream.Flush(flushToDisk: true);
        }
        finally
        {
            _writeGate.Release();
        }
    }

    /// <summary>Reads all complete audit events in append order.</summary>
    public async Task<IReadOnlyList<SessionMaintenanceAuditRecord>> ReadAllAsync(
        CancellationToken ct = default)
    {
        if (!File.Exists(FilePath))
        {
            return [];
        }

        var records = new List<SessionMaintenanceAuditRecord>();
        await using var stream = new FileStream(FilePath, new FileStreamOptions
        {
            Mode = FileMode.Open,
            Access = FileAccess.Read,
            Share = FileShare.ReadWrite | FileShare.Delete,
            Options = FileOptions.Asynchronous | FileOptions.SequentialScan
        });
        using var reader = new StreamReader(stream);
        while (await reader.ReadLineAsync(ct).ConfigureAwait(false) is { } line)
        {
            if (string.IsNullOrWhiteSpace(line))
            {
                continue;
            }

            var record = JsonSerializer.Deserialize(
                    line,
                    ConversationJsonContext.Default.SessionMaintenanceAuditRecord)
                ?? throw new InvalidDataException("A Session maintenance audit record is empty.");
            Validate(record);
            records.Add(record);
        }

        return records;
    }

    /// <inheritdoc />
    public void Dispose() => _writeGate.Dispose();

    private static void Validate(SessionMaintenanceAuditRecord record)
    {
        if (!string.Equals(record.Schema, CurrentSchema, StringComparison.Ordinal))
        {
            throw new InvalidDataException(
                $"Unsupported Session maintenance audit schema '{record.Schema}'.");
        }

        ArgumentException.ThrowIfNullOrWhiteSpace(record.AuditId);
        ArgumentException.ThrowIfNullOrWhiteSpace(record.RuntimeInstanceId);
        ArgumentException.ThrowIfNullOrWhiteSpace(record.SessionId);
        ArgumentException.ThrowIfNullOrWhiteSpace(record.Operation);
        ArgumentException.ThrowIfNullOrWhiteSpace(record.Phase);
        ArgumentException.ThrowIfNullOrWhiteSpace(record.Detail);
    }
}
