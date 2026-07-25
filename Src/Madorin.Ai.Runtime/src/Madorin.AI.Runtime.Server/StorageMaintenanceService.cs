using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Data.Sqlite;

namespace Madorin.AI.Runtime.Server;

/// <summary>Checks Blob references and performs recoverable orphan collection.</summary>
public static class StorageMaintenanceService
{
    public const int DefaultOlderThanDays = 7;

    /// <summary>Inspects Blob references and files without modifying Runtime storage.</summary>
    public static async Task<StorageCheckResult> CheckAsync(
        string dataDirectory,
        bool verifyHashes = false,
        CancellationToken ct = default)
    {
        var inspection = await InspectAsync(dataDirectory, verifyHashes, ct)
            .ConfigureAwait(false);
        return inspection.Result;
    }

    /// <summary>Creates a read-only garbage-collection plan.</summary>
    public static async Task<StorageGcPlan> PlanGcAsync(
        string dataDirectory,
        int olderThanDays = DefaultOlderThanDays,
        CancellationToken ct = default)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(olderThanDays);
        var inspection = await InspectAsync(dataDirectory, verifyHashes: false, ct)
            .ConfigureAwait(false);
        var cutoffUtc = DateTimeOffset.UtcNow.AddDays(-olderThanDays);
        var candidates = inspection.OrphanFiles
            .Where(file => file.LastWriteTimeUtc <= cutoffUtc.UtcDateTime)
            .OrderBy(static file => file.BlobId, StringComparer.Ordinal)
            .ToArray();
        var protectedByAgeCount = inspection.OrphanFiles.Length - candidates.Length;
        var blockingIssues = inspection.Result.ReferenceScanComplete
            ? []
            : inspection.Result.Issues;
        return new StorageGcPlan(
            inspection.Result.DataDirectory,
            inspection.Result.BlobDirectory,
            olderThanDays,
            cutoffUtc,
            inspection.Result.ReferenceScanComplete,
            inspection.Result.ReferenceCount,
            inspection.Result.BlobFileCount,
            protectedByAgeCount,
            candidates.Sum(static file => file.Length),
            candidates.Select(static file => file.BlobId).ToArray(),
            blockingIssues);
    }

    /// <summary>Moves old orphan Blob files into a recovery point under the workspace lease.</summary>
    public static async Task<StorageGcResult> CollectGarbageAsync(
        string workspaceRoot,
        string dataDirectory,
        int olderThanDays = DefaultOlderThanDays,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(workspaceRoot);
        ArgumentOutOfRangeException.ThrowIfNegative(olderThanDays);
        var preliminaryPlan = await PlanGcAsync(dataDirectory, olderThanDays, ct)
            .ConfigureAwait(false);
        if (!preliminaryPlan.CanCollect)
        {
            throw new StorageGcBlockedException(preliminaryPlan);
        }

        WorkspaceWriteLock workspaceLock;
        try
        {
            workspaceLock = await WorkspaceWriteLock.AcquireAsync(
                    workspaceRoot,
                    "storage-gc-" + Guid.NewGuid().ToString("N"),
                    ct: ct)
                .ConfigureAwait(false);
        }
        catch (InvalidOperationException ex)
        {
            throw new StorageMaintenanceLockException(ex.Message, ex);
        }

        await using (workspaceLock.ConfigureAwait(false))
        {
            var plan = await PlanGcAsync(dataDirectory, olderThanDays, ct)
                .ConfigureAwait(false);
            if (!plan.CanCollect)
            {
                throw new StorageGcBlockedException(plan);
            }

            if (plan.CandidateBlobIds.Length == 0)
            {
                return new StorageGcResult(
                    plan.DataDirectory,
                    plan.BlobDirectory,
                    plan.OlderThanDays,
                    plan.CutoffUtc,
                    plan.ProtectedByAgeCount,
                    0,
                    [],
                    null);
            }

            string? recoveryPointPath = null;
            var collectedBlobIds = new List<string>();
            long collectedBytes = 0;
            try
            {
                recoveryPointPath = await CreateRecoveryPointAsync(plan, ct)
                    .ConfigureAwait(false);
                var recoveryBlobDirectory = Path.Combine(recoveryPointPath, "blobs");
                foreach (var blobId in plan.CandidateBlobIds)
                {
                    ct.ThrowIfCancellationRequested();
                    var source = Path.Combine(plan.BlobDirectory, blobId + ".blob");
                    var destination = Path.Combine(recoveryBlobDirectory, blobId + ".blob");
                    var length = new FileInfo(source).Length;
                    File.Move(source, destination, overwrite: false);
                    collectedBlobIds.Add(blobId);
                    collectedBytes = checked(collectedBytes + length);
                }

                return new StorageGcResult(
                    plan.DataDirectory,
                    plan.BlobDirectory,
                    plan.OlderThanDays,
                    plan.CutoffUtc,
                    plan.ProtectedByAgeCount,
                    collectedBytes,
                    [.. collectedBlobIds],
                    recoveryPointPath);
            }
            catch (OperationCanceledException ex) when (recoveryPointPath is not null)
            {
                throw new StorageGcException(
                    $"Blob garbage collection was cancelled. Recovery point retained at '{recoveryPointPath}'.",
                    recoveryPointPath,
                    collectedBlobIds,
                    ex);
            }
            catch (Exception ex) when (ex is IOException
                or UnauthorizedAccessException
                or InvalidDataException)
            {
                throw new StorageGcException(
                    recoveryPointPath is null
                        ? $"Blob garbage collection did not start because its recovery point failed: {ex.Message}"
                        : $"Blob garbage collection failed. Recovery point retained at '{recoveryPointPath}': {ex.Message}",
                    recoveryPointPath,
                    collectedBlobIds,
                    ex);
            }
        }
    }

    private static async Task<StorageInspection> InspectAsync(
        string dataDirectory,
        bool verifyHashes,
        CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(dataDirectory);
        var fullDataDirectory = Path.GetFullPath(dataDirectory);
        var blobDirectory = Path.Combine(fullDataDirectory, "blobs");
        var state = new InspectionState();
        if (!Directory.Exists(fullDataDirectory))
        {
            state.FailScan(
                "data_directory_missing",
                $"Data directory '{fullDataDirectory}' was not found.");
            return state.CreateInspection(fullDataDirectory, blobDirectory, verifyHashes, []);
        }

        await ReadCanonicalReferencesAsync(fullDataDirectory, state, ct)
            .ConfigureAwait(false);
        await ReadDatabaseReferencesAsync(fullDataDirectory, state, ct)
            .ConfigureAwait(false);
        var files = ReadBlobFiles(blobDirectory, state);
        await EvaluateFilesAsync(files, state, verifyHashes, ct).ConfigureAwait(false);
        var orphanFiles = state.ScanComplete
            ? files.Where(file => !state.References.ContainsKey(file.BlobId)).ToArray()
            : [];
        foreach (var orphan in orphanFiles)
        {
            state.AddIssue(
                "orphan_blob",
                "warning",
                $"Blob '{orphan.BlobId}' is not referenced by canonical messages or tool results.",
                orphan.BlobId);
        }

        return state.CreateInspection(
            fullDataDirectory,
            blobDirectory,
            verifyHashes,
            orphanFiles);
    }

    private static async Task ReadCanonicalReferencesAsync(
        string dataDirectory,
        InspectionState state,
        CancellationToken ct)
    {
        var messagesDirectory = Path.Combine(dataDirectory, "messages");
        if (!Directory.Exists(messagesDirectory))
        {
            state.FailScan(
                "messages_directory_missing",
                $"Messages directory '{messagesDirectory}' was not found.");
            return;
        }

        foreach (var path in Directory.EnumerateFiles(messagesDirectory, "*.jsonl")
                     .Order(StringComparer.Ordinal))
        {
            var lineNumber = 0;
            try
            {
                await foreach (var line in File.ReadLinesAsync(path, ct).ConfigureAwait(false))
                {
                    lineNumber++;
                    if (string.IsNullOrWhiteSpace(line))
                    {
                        continue;
                    }

                    using var document = JsonDocument.Parse(line);
                    CollectJsonReferences(
                        document.RootElement,
                        state,
                        $"{Path.GetFileName(path)}:{lineNumber}");
                }
            }
            catch (Exception ex) when (ex is JsonException
                or IOException
                or UnauthorizedAccessException)
            {
                state.FailScan(
                    "canonical_reference_scan_failed",
                    $"Canonical message reference scan failed at '{path}' line {lineNumber}: {ex.Message}");
            }
        }
    }

    private static void CollectJsonReferences(
        JsonElement element,
        InspectionState state,
        string source)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.Array:
                foreach (var item in element.EnumerateArray())
                {
                    CollectJsonReferences(item, state, source);
                }

                return;
            case JsonValueKind.Object:
                if (element.TryGetProperty("type", out var type)
                    && type.ValueKind == JsonValueKind.String
                    && string.Equals(type.GetString(), "blob_ref", StringComparison.Ordinal))
                {
                    if (!element.TryGetProperty("blob", out var blob)
                        || blob.ValueKind != JsonValueKind.Object
                        || !blob.TryGetProperty("blobId", out var blobIdElement)
                        || blobIdElement.ValueKind != JsonValueKind.String
                        || string.IsNullOrWhiteSpace(blobIdElement.GetString()))
                    {
                        state.FailScan(
                            "blob_reference_invalid",
                            $"Blob reference at '{source}' does not contain a usable blobId.");
                    }
                    else
                    {
                        var length = blob.TryGetProperty("length", out var lengthElement)
                            && lengthElement.TryGetInt64(out var parsedLength)
                                ? parsedLength
                                : (long?)null;
                        var sha256 = blob.TryGetProperty("sha256", out var shaElement)
                            && shaElement.ValueKind == JsonValueKind.String
                                ? shaElement.GetString()
                                : null;
                        state.AddReference(
                            blobIdElement.GetString()!,
                            length,
                            sha256,
                            source);
                    }
                }

                foreach (var property in element.EnumerateObject())
                {
                    CollectJsonReferences(property.Value, state, source);
                }

                return;
            default:
                return;
        }
    }

    private static async Task ReadDatabaseReferencesAsync(
        string dataDirectory,
        InspectionState state,
        CancellationToken ct)
    {
        var databasePath = Path.Combine(dataDirectory, "state.db");
        if (!File.Exists(databasePath))
        {
            state.FailScan(
                "state_database_missing",
                $"State database '{databasePath}' was not found.");
            return;
        }

        try
        {
            await using var snapshot = await OpenDatabaseSnapshotAsync(databasePath, ct)
                .ConfigureAwait(false);
            await using var command = snapshot.Connection.CreateCommand();
            command.CommandText = """
                SELECT result_blob_id,
                       result_blob_length,
                       result_blob_sha256,
                       call_id
                FROM tool_intents
                WHERE result_blob_id IS NOT NULL
                  AND result_blob_id <> ''
                ORDER BY result_blob_id, call_id;
                """;
            await using var reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);
            while (await reader.ReadAsync(ct).ConfigureAwait(false))
            {
                state.AddReference(
                    reader.GetString(0),
                    reader.IsDBNull(1) ? null : reader.GetInt64(1),
                    reader.IsDBNull(2) ? null : reader.GetString(2),
                    "tool_intents:" + reader.GetString(3));
            }
        }
        catch (Exception ex) when (ex is IOException
            or UnauthorizedAccessException
            or InvalidDataException
            or SqliteException)
        {
            state.FailScan(
                "database_reference_scan_failed",
                $"SQLite Blob reference scan failed for '{databasePath}': {ex.Message}");
        }
    }

    private static BlobFile[] ReadBlobFiles(
        string blobDirectory,
        InspectionState state)
    {
        if (!Directory.Exists(blobDirectory))
        {
            state.FailScan(
                "blob_directory_missing",
                $"Blob directory '{blobDirectory}' was not found.");
            return [];
        }

        var files = new List<BlobFile>();
        var blobFileCount = 0;
        try
        {
            foreach (var path in Directory.EnumerateFiles(blobDirectory, "*.blob")
                         .Order(StringComparer.Ordinal))
            {
                blobFileCount++;
                var blobId = Path.GetFileNameWithoutExtension(path);
                if (!IsCanonicalBlobId(blobId))
                {
                    state.AddIssue(
                        "invalid_blob_filename",
                        "error",
                        $"Blob file '{Path.GetFileName(path)}' is not named with a lowercase SHA-256 digest.");
                    continue;
                }

                var info = new FileInfo(path);
                files.Add(new BlobFile(
                    blobId,
                    path,
                    info.Length,
                    info.LastWriteTimeUtc));
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            state.FailScan(
                "blob_file_scan_failed",
                $"Blob file scan failed for '{blobDirectory}': {ex.Message}");
        }

        state.BlobFileCount = blobFileCount;
        return [.. files];
    }

    private static async Task EvaluateFilesAsync(
        IReadOnlyList<BlobFile> files,
        InspectionState state,
        bool verifyHashes,
        CancellationToken ct)
    {
        var filesById = files.ToDictionary(
            static file => file.BlobId,
            StringComparer.Ordinal);
        foreach (var reference in state.References.Values.OrderBy(
                     static reference => reference.BlobId,
                     StringComparer.Ordinal))
        {
            ct.ThrowIfCancellationRequested();
            if (!filesById.TryGetValue(reference.BlobId, out var file))
            {
                state.AddIssue(
                    "referenced_blob_missing",
                    "error",
                    $"Referenced Blob '{reference.BlobId}' was not found.",
                    reference.BlobId);
                continue;
            }

            if (reference.Length is { } expectedLength && file.Length != expectedLength)
            {
                state.AddIssue(
                    "blob_length_mismatch",
                    "error",
                    $"Blob '{reference.BlobId}' has length {file.Length}, expected {expectedLength}.",
                    reference.BlobId);
            }

            if (!verifyHashes)
            {
                continue;
            }

            try
            {
                var actualHash = await ComputeSha256Async(file.Path, ct).ConfigureAwait(false);
                var expectedHash = reference.Sha256 ?? reference.BlobId;
                if (!string.Equals(actualHash, expectedHash, StringComparison.OrdinalIgnoreCase)
                    || !string.Equals(actualHash, reference.BlobId, StringComparison.Ordinal))
                {
                    state.AddIssue(
                        "blob_hash_mismatch",
                        "error",
                        $"Blob '{reference.BlobId}' failed SHA-256 validation.",
                        reference.BlobId);
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                state.AddIssue(
                    "blob_hash_read_failed",
                    "error",
                    $"Blob '{reference.BlobId}' could not be hashed: {ex.Message}",
                    reference.BlobId);
            }
        }
    }

    private static async Task<string> ComputeSha256Async(
        string path,
        CancellationToken ct)
    {
        await using var stream = new FileStream(path, new FileStreamOptions
        {
            Mode = FileMode.Open,
            Access = FileAccess.Read,
            Share = FileShare.Read,
            BufferSize = 64 * 1024,
            Options = FileOptions.Asynchronous | FileOptions.SequentialScan
        });
        return Convert.ToHexStringLower(
            await SHA256.HashDataAsync(stream, ct).ConfigureAwait(false));
    }

    private static async Task<string> CreateRecoveryPointAsync(
        StorageGcPlan plan,
        CancellationToken ct)
    {
        var backupDirectory = Path.Combine(plan.DataDirectory, "backups");
        var backupDirectoryExisted = Directory.Exists(backupDirectory);
        var recoveryPointPath = Path.Combine(
            backupDirectory,
            "storage-gc-"
            + DateTimeOffset.UtcNow.ToString("yyyyMMddTHHmmssfffZ", CultureInfo.InvariantCulture)
            + "-"
            + Guid.NewGuid().ToString("N"));
        try
        {
            var recoveryBlobDirectory = Path.Combine(recoveryPointPath, "blobs");
            Directory.CreateDirectory(recoveryBlobDirectory);
            var manifestPath = Path.Combine(recoveryPointPath, "manifest.txt");
            await using var stream = new FileStream(manifestPath, new FileStreamOptions
            {
                Mode = FileMode.CreateNew,
                Access = FileAccess.Write,
                Share = FileShare.None,
                BufferSize = 16 * 1024,
                Options = FileOptions.Asynchronous | FileOptions.WriteThrough
            });
            await using var writer = new StreamWriter(
                stream,
                new UTF8Encoding(encoderShouldEmitUTF8Identifier: false),
                bufferSize: 16 * 1024,
                leaveOpen: true);
            await writer.WriteLineAsync("madorin-storage-gc-v1".AsMemory(), ct)
                .ConfigureAwait(false);
            await writer.WriteLineAsync(
                    $"cutoffUtc={plan.CutoffUtc:O}".AsMemory(),
                    ct)
                .ConfigureAwait(false);
            foreach (var blobId in plan.CandidateBlobIds)
            {
                var sourcePath = Path.Combine(plan.BlobDirectory, blobId + ".blob");
                var length = new FileInfo(sourcePath).Length;
                await writer.WriteLineAsync(
                        $"{blobId}\t{length.ToString(CultureInfo.InvariantCulture)}".AsMemory(),
                        ct)
                    .ConfigureAwait(false);
            }

            await writer.FlushAsync(ct).ConfigureAwait(false);
            await stream.FlushAsync(ct).ConfigureAwait(false);
            stream.Flush(flushToDisk: true);
            return recoveryPointPath;
        }
        catch
        {
            TryDeleteDirectory(recoveryPointPath);
            if (!backupDirectoryExisted)
            {
                TryDeleteDirectoryIfEmpty(backupDirectory);
            }

            throw;
        }
    }

    private static async Task<DatabaseSnapshot> OpenDatabaseSnapshotAsync(
        string databasePath,
        CancellationToken ct)
    {
        if (!File.Exists(databasePath + "-wal"))
        {
            var directConnection = CreateReadOnlyConnection(
                new Uri(databasePath).AbsoluteUri + "?immutable=1");
            await directConnection.OpenAsync(ct).ConfigureAwait(false);
            return new DatabaseSnapshot(directConnection, temporaryDirectory: null);
        }

        var temporaryDirectory = Path.Combine(
            Path.GetTempPath(),
            "madorin-storage-check-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(temporaryDirectory);
        try
        {
            var snapshotDatabasePath = Path.Combine(temporaryDirectory, "state.db");
            var snapshotWalPath = snapshotDatabasePath + "-wal";
            for (var attempt = 0; attempt < 3; attempt++)
            {
                ct.ThrowIfCancellationRequested();
                var before = CaptureDatabaseFiles(databasePath);
                if (before.Wal is null)
                {
                    TryDeleteDirectory(temporaryDirectory);
                    var directConnection = CreateReadOnlyConnection(
                        new Uri(databasePath).AbsoluteUri + "?immutable=1");
                    await directConnection.OpenAsync(ct).ConfigureAwait(false);
                    return new DatabaseSnapshot(directConnection, temporaryDirectory: null);
                }

                try
                {
                    await CopySharedFileAsync(databasePath, snapshotDatabasePath, ct)
                        .ConfigureAwait(false);
                    await CopySharedFileAsync(databasePath + "-wal", snapshotWalPath, ct)
                        .ConfigureAwait(false);
                }
                catch (IOException) when (!File.Exists(databasePath)
                    || !File.Exists(databasePath + "-wal"))
                {
                    File.Delete(snapshotDatabasePath);
                    File.Delete(snapshotWalPath);
                    continue;
                }

                if (before == CaptureDatabaseFiles(databasePath))
                {
                    var snapshotConnection = CreateReadOnlyConnection(snapshotDatabasePath);
                    await snapshotConnection.OpenAsync(ct).ConfigureAwait(false);
                    return new DatabaseSnapshot(snapshotConnection, temporaryDirectory);
                }

                File.Delete(snapshotDatabasePath);
                File.Delete(snapshotWalPath);
            }

            throw new InvalidDataException(
                "SQLite database files changed while the read-only Blob reference snapshot was being created.");
        }
        catch
        {
            TryDeleteDirectory(temporaryDirectory);
            throw;
        }
    }

    private static async Task CopySharedFileAsync(
        string sourcePath,
        string destinationPath,
        CancellationToken ct)
    {
        await using var source = new FileStream(sourcePath, new FileStreamOptions
        {
            Mode = FileMode.Open,
            Access = FileAccess.Read,
            Share = FileShare.ReadWrite | FileShare.Delete,
            BufferSize = 64 * 1024,
            Options = FileOptions.Asynchronous | FileOptions.SequentialScan
        });
        await using var destination = new FileStream(destinationPath, new FileStreamOptions
        {
            Mode = FileMode.Create,
            Access = FileAccess.Write,
            Share = FileShare.None,
            BufferSize = 64 * 1024,
            Options = FileOptions.Asynchronous | FileOptions.SequentialScan
        });
        await source.CopyToAsync(destination, 64 * 1024, ct).ConfigureAwait(false);
        await destination.FlushAsync(ct).ConfigureAwait(false);
        destination.Flush(flushToDisk: true);
    }

    private static SqliteConnection CreateReadOnlyConnection(string dataSource) =>
        new(new SqliteConnectionStringBuilder
        {
            DataSource = dataSource,
            Mode = SqliteOpenMode.ReadOnly,
            Cache = SqliteCacheMode.Private,
            Pooling = false,
            DefaultTimeout = 5
        }.ToString());

    private static DatabaseFileSnapshot CaptureDatabaseFiles(string databasePath) =>
        new(CaptureFile(databasePath), CaptureFile(databasePath + "-wal"));

    private static DatabaseFileIdentity? CaptureFile(string path)
    {
        if (!File.Exists(path))
        {
            return null;
        }

        var info = new FileInfo(path);
        return new DatabaseFileIdentity(info.Length, info.LastWriteTimeUtc);
    }

    private static bool IsCanonicalBlobId(string? value) =>
        value is { Length: 64 }
        && value.All(static character => character is >= '0' and <= '9' or >= 'a' and <= 'f');

    private static void TryDeleteDirectory(string path)
    {
        try
        {
            if (Directory.Exists(path))
            {
                Directory.Delete(path, recursive: true);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Cleanup is best effort and must not replace the operation's original failure.
        }
    }

    private static void TryDeleteDirectoryIfEmpty(string path)
    {
        try
        {
            if (Directory.Exists(path)
                && !Directory.EnumerateFileSystemEntries(path).Any())
            {
                Directory.Delete(path);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Cleanup is best effort and must not replace the operation's original failure.
        }
    }

    private sealed class InspectionState
    {
        private readonly List<StorageCheckIssue> _issues = [];

        public Dictionary<string, BlobReferenceState> References { get; } =
            new(StringComparer.Ordinal);

        public bool ScanComplete { get; private set; } = true;

        public int BlobFileCount { get; set; }

        public void AddReference(
            string blobId,
            long? length,
            string? sha256,
            string source)
        {
            if (!IsCanonicalBlobId(blobId))
            {
                FailScan(
                    "blob_reference_invalid",
                    $"Blob reference at '{source}' contains invalid Blob identifier '{blobId}'.",
                    blobId);
                return;
            }

            if (length < 0)
            {
                AddIssue(
                    "blob_metadata_invalid",
                    "error",
                    $"Blob '{blobId}' has a negative length at '{source}'.",
                    blobId);
                length = null;
            }

            if (sha256 is not null && !IsCanonicalBlobId(sha256))
            {
                AddIssue(
                    "blob_metadata_invalid",
                    "error",
                    $"Blob '{blobId}' has invalid SHA-256 metadata at '{source}'.",
                    blobId);
                sha256 = null;
            }

            if (sha256 is not null
                && !string.Equals(blobId, sha256, StringComparison.Ordinal))
            {
                AddIssue(
                    "blob_metadata_mismatch",
                    "error",
                    $"Blob '{blobId}' does not match its SHA-256 metadata at '{source}'.",
                    blobId);
            }

            if (!References.TryGetValue(blobId, out var reference))
            {
                References.Add(blobId, new BlobReferenceState(blobId, length, sha256));
                return;
            }

            if (reference.Length is { } existingLength
                && length is { } newLength
                && existingLength != newLength)
            {
                AddIssue(
                    "blob_metadata_conflict",
                    "error",
                    $"Blob '{blobId}' has conflicting length metadata.",
                    blobId);
            }
            else if (reference.Length is null)
            {
                reference.Length = length;
            }

            if (reference.Sha256 is not null
                && sha256 is not null
                && !string.Equals(reference.Sha256, sha256, StringComparison.Ordinal))
            {
                AddIssue(
                    "blob_metadata_conflict",
                    "error",
                    $"Blob '{blobId}' has conflicting SHA-256 metadata.",
                    blobId);
            }
            else if (reference.Sha256 is null)
            {
                reference.Sha256 = sha256;
            }
        }

        public void AddIssue(
            string code,
            string severity,
            string message,
            string? blobId = null) =>
            _issues.Add(new StorageCheckIssue(code, severity, message, blobId));

        public void FailScan(string code, string message, string? blobId = null)
        {
            ScanComplete = false;
            AddIssue(code, "error", message, blobId);
        }

        public StorageInspection CreateInspection(
            string dataDirectory,
            string blobDirectory,
            bool verifyHashes,
            IReadOnlyList<BlobFile> orphanFiles)
        {
            var orderedIssues = _issues
                .OrderBy(static issue => issue.Code, StringComparer.Ordinal)
                .ThenBy(static issue => issue.BlobId, StringComparer.Ordinal)
                .ThenBy(static issue => issue.Message, StringComparer.Ordinal)
                .ToArray();
            return new StorageInspection(
                new StorageCheckResult(
                    dataDirectory,
                    blobDirectory,
                    verifyHashes,
                    ScanComplete,
                    References.Count,
                    BlobFileCount,
                    orphanFiles.Count,
                    orderedIssues),
                [.. orphanFiles]);
        }
    }

    private sealed class BlobReferenceState(
        string blobId,
        long? length,
        string? sha256)
    {
        public string BlobId { get; } = blobId;

        public long? Length { get; set; } = length;

        public string? Sha256 { get; set; } = sha256;
    }

    private sealed record StorageInspection(
        StorageCheckResult Result,
        BlobFile[] OrphanFiles);

    private sealed record BlobFile(
        string BlobId,
        string Path,
        long Length,
        DateTime LastWriteTimeUtc);

    private sealed record DatabaseFileIdentity(long Length, DateTime LastWriteTimeUtc);

    private sealed record DatabaseFileSnapshot(
        DatabaseFileIdentity? Database,
        DatabaseFileIdentity? Wal);

    private sealed class DatabaseSnapshot(
        SqliteConnection connection,
        string? temporaryDirectory) : IAsyncDisposable
    {
        public SqliteConnection Connection { get; } = connection;

        public async ValueTask DisposeAsync()
        {
            await Connection.DisposeAsync().ConfigureAwait(false);
            if (temporaryDirectory is not null)
            {
                TryDeleteDirectory(temporaryDirectory);
            }
        }
    }
}
