using System.Collections.Concurrent;
using System.Security.Cryptography;
using Madorin.AI.Runtime.Contracts;
using Madorin.AI.Runtime.Tools.Abstractions;

namespace Madorin.AI.Runtime.Server;

internal sealed class BlobStagingStore : IToolResultBlobStore, IAsyncDisposable
{
    private readonly string _stagingDirectory;
    private readonly string _blobDirectory;
    private readonly long _maxBlobBytes;
    private readonly long _maxBlobBytesPerRun;
    private readonly long _maxGlobalBlobBytes;
    private readonly ConcurrentDictionary<string, UploadState> _uploads = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, BlobReference> _references = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, long> _runUsage = new(StringComparer.Ordinal);
    private readonly SemaphoreSlim _gate = new(1, 1);
    private long _globalUsage;
    private int _disposed;

    public BlobStagingStore(
        string dataDirectory,
        RuntimeLimits limits,
        long maxGlobalBlobBytes)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(dataDirectory);
        ArgumentNullException.ThrowIfNull(limits);
        if (limits.MaxBlobBytes <= 0
            || limits.MaxBlobBytesPerRun <= 0
            || maxGlobalBlobBytes <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(limits), "Blob quotas must be positive.");
        }

        _stagingDirectory = Path.Combine(dataDirectory, "staging");
        _blobDirectory = Path.Combine(dataDirectory, "blobs");
        Directory.CreateDirectory(_stagingDirectory);
        Directory.CreateDirectory(_blobDirectory);
        _maxBlobBytes = limits.MaxBlobBytes;
        _maxBlobBytesPerRun = limits.MaxBlobBytesPerRun;
        _maxGlobalBlobBytes = maxGlobalBlobBytes;
        _globalUsage = Directory.EnumerateFiles(_blobDirectory, "*.blob")
            .Select(static path => new FileInfo(path).Length)
            .Aggregate(0L, static (total, length) => checked(total + length));
    }

    public async Task<BlobReference> StoreAsync(
        string runId,
        ReadOnlyMemory<byte> content,
        string contentType,
        string accessScope,
        DateTimeOffset expiresAt,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(runId);
        ArgumentException.ThrowIfNullOrWhiteSpace(contentType);
        ArgumentException.ThrowIfNullOrWhiteSpace(accessScope);
        if (content.Length > _maxBlobBytes)
        {
            throw new InvalidDataException("The tool result exceeds the single-Blob quota.");
        }

        var uploadId = Guid.NewGuid().ToString("N");
        var owner = $"runtime-tool-result:{runId}";
        var hash = Convert.ToHexString(SHA256.HashData(content.Span)).ToLowerInvariant();
        await BeginAsync(
            new BlobBeginRequest(
                uploadId,
                runId,
                contentType,
                accessScope,
                expiresAt,
                content.Length),
            owner,
            ct).ConfigureAwait(false);
        try
        {
            if (!content.IsEmpty)
            {
                await AppendChunkAsync(
                    new BlobChunkRequest(uploadId),
                    content,
                    owner,
                    ct).ConfigureAwait(false);
            }

            return await CompleteAsync(
                new BlobCompleteRequest(uploadId, content.Length, hash),
                owner,
                ct).ConfigureAwait(false);
        }
        catch
        {
            await AbortUploadAsync(uploadId, owner, CancellationToken.None).ConfigureAwait(false);
            throw;
        }
    }

    public async Task ValidateReferenceAsync(
        BlobReference reference,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(reference);
        ValidateBlobId(reference.BlobId);
        ValidateBlobId(reference.Sha256);
        ArgumentException.ThrowIfNullOrWhiteSpace(reference.ContentType);
        ArgumentException.ThrowIfNullOrWhiteSpace(reference.AccessScope);
        if (!string.Equals(reference.BlobId, reference.Sha256, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException("The Blob identifier does not match its SHA-256 metadata.");
        }

        if (reference.Length < 0 || reference.Length > _maxBlobBytes)
        {
            throw new InvalidDataException("The Blob length is outside the configured quota.");
        }

        if (reference.ExpiresAt <= DateTimeOffset.UtcNow)
        {
            throw new InvalidDataException("The Blob lease has expired.");
        }

        var path = GetBlobPath(reference.BlobId);
        var file = new FileInfo(path);
        if (!file.Exists || file.Length != reference.Length)
        {
            throw new InvalidDataException("The Blob content is missing or its length does not match metadata.");
        }

        await using var stream = new FileStream(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            128 * 1024,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        var actualHash = Convert.ToHexString(
            await SHA256.HashDataAsync(stream, ct).ConfigureAwait(false));
        if (!string.Equals(actualHash, reference.Sha256, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException("The Blob content failed SHA-256 validation.");
        }
    }

    public async Task BeginAsync(BlobBeginRequest request, string owner, CancellationToken ct)
    {
        ValidateOwner(owner);
        ValidateBeginRequest(request);
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            ThrowIfDisposed();
            ValidateExpectedLength(request);
            if (!_uploads.TryAdd(
                    request.UploadId,
                    new UploadState(
                        request,
                        owner,
                        Path.Combine(_stagingDirectory, request.UploadId + ".part"))))
            {
                throw new InvalidOperationException("The Blob upload identifier is already active.");
            }

            try
            {
                var upload = _uploads[request.UploadId];
                upload.Stream = new FileStream(
                    upload.StagingPath,
                    FileMode.CreateNew,
                    FileAccess.Write,
                    FileShare.None,
                    128 * 1024,
                    FileOptions.Asynchronous | FileOptions.SequentialScan);
            }
            catch
            {
                _uploads.TryRemove(request.UploadId, out _);
                TryDelete(request.UploadId);
                throw;
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task AppendChunkAsync(
        BlobChunkRequest request,
        ReadOnlyMemory<byte> data,
        string owner,
        CancellationToken ct)
    {
        ValidateOwner(owner);
        if (data.Length == 0)
        {
            throw new InvalidDataException("Empty Blob chunks are not allowed.");
        }

        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            ThrowIfDisposed();
            if (!_uploads.TryGetValue(request.UploadId, out var upload)
                || !string.Equals(upload.Owner, owner, StringComparison.Ordinal))
            {
                throw new InvalidDataException("The Blob upload was not found for this session.");
            }

            var nextLength = checked(upload.Length + data.Length);
            if (nextLength > _maxBlobBytes)
            {
                throw new InvalidDataException("The Blob exceeds the single-Blob quota.");
            }

            var runUsage = _runUsage.GetValueOrDefault(upload.Request.RunId);
            var activeRunUsage = _uploads.Values
                .Where(candidate => string.Equals(candidate.Request.RunId, upload.Request.RunId, StringComparison.Ordinal))
                .Sum(static candidate => candidate.Length);
            if (checked(runUsage + activeRunUsage - upload.Length + nextLength) > _maxBlobBytesPerRun)
            {
                throw new InvalidDataException("The Blob exceeds the per-Run quota.");
            }

            var activeGlobalUsage = _uploads.Values.Sum(static candidate => candidate.Length);
            if (checked(_globalUsage + activeGlobalUsage - upload.Length + nextLength) > _maxGlobalBlobBytes)
            {
                throw new InvalidDataException("The Blob exceeds the global quota.");
            }

            await upload.Stream!.WriteAsync(data, ct).ConfigureAwait(false);
            upload.Hash.AppendData(data.Span);
            upload.Length = nextLength;
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<BlobReference> CompleteAsync(
        BlobCompleteRequest request,
        string owner,
        CancellationToken ct)
    {
        ValidateOwner(owner);
        if (request.Length < 0 || request.Sha256.Length != 64)
        {
            throw new InvalidDataException("The Blob completion metadata is invalid.");
        }

        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            ThrowIfDisposed();
            if (!_uploads.TryGetValue(request.UploadId, out var upload)
                || !string.Equals(upload.Owner, owner, StringComparison.Ordinal))
            {
                throw new InvalidDataException("The Blob upload was not found for this session.");
            }

            if (upload.Length != request.Length)
            {
                throw new InvalidDataException("The Blob length does not match the uploaded data.");
            }

            var actualHash = Convert.ToHexString(upload.Hash.GetHashAndReset()).ToLowerInvariant();
            upload.Hash.Dispose();
            if (!string.Equals(actualHash, request.Sha256, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidDataException("The Blob SHA-256 does not match the uploaded data.");
            }

            await upload.Stream!.FlushAsync(ct).ConfigureAwait(false);
            upload.Stream.Flush(flushToDisk: true);
            await upload.Stream.DisposeAsync().ConfigureAwait(false);
            upload.Stream = null;

            var blobId = actualHash;
            var target = GetBlobPath(blobId);
            var targetExists = File.Exists(target);
            if (targetExists)
            {
                TryDelete(upload.UploadId);
            }
            else
            {
                File.Move(upload.StagingPath, target);
            }

            _uploads.TryRemove(upload.UploadId, out _);
            if (!targetExists)
            {
                _globalUsage = checked(_globalUsage + upload.Length);
            }

            _runUsage.AddOrUpdate(
                upload.Request.RunId,
                upload.Length,
                (_, existing) => checked(existing + upload.Length));
            var reference = new BlobReference(
                blobId,
                upload.Length,
                actualHash,
                upload.Request.ContentType,
                upload.Request.AccessScope,
                upload.Request.ExpiresAt);
            _references[blobId] = reference;
            return reference;
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<BlobReference> GetReferenceAsync(
        BlobReadRequest request,
        string owner,
        CancellationToken ct)
    {
        ValidateOwner(owner);
        ValidateBlobId(request.BlobId);
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var path = GetBlobPath(request.BlobId);
            if (!File.Exists(path))
            {
                throw new FileNotFoundException("The requested Blob does not exist.");
            }

            if (_references.TryGetValue(request.BlobId, out var reference)
                && reference.ExpiresAt <= DateTimeOffset.UtcNow)
            {
                throw new InvalidOperationException("The Blob lease has expired.");
            }

            return _references.GetValueOrDefault(
                request.BlobId,
                new BlobReference(
                    request.BlobId,
                    new FileInfo(path).Length,
                    request.BlobId,
                    "application/octet-stream",
                    "session",
                    DateTimeOffset.UtcNow.AddMinutes(10)));
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<BlobReadResult> ReadChunkAsync(
        BlobReadRequest request,
        string owner,
        CancellationToken ct)
    {
        var reference = await GetReferenceAsync(request, owner, ct).ConfigureAwait(false);
        if (request.Offset < 0 || request.Offset > reference.Length)
        {
            throw new ArgumentOutOfRangeException(nameof(request));
        }

        if (request.MaxBytes < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(request));
        }

        if (request.MaxBytes == 0)
        {
            return new BlobReadResult(reference, request.Offset, [], request.Offset >= reference.Length);
        }

        var count = Math.Min(request.MaxBytes, 256 * 1024);
        var remaining = checked(reference.Length - request.Offset);
        count = (int)Math.Min(count, remaining);
        if (count == 0)
        {
            return new BlobReadResult(reference, request.Offset, [], true);
        }

        var bytes = new byte[count];
        await using var stream = new FileStream(
            GetBlobPath(reference.BlobId),
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            128 * 1024,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        stream.Seek(request.Offset, SeekOrigin.Begin);
        var read = 0;
        while (read < bytes.Length)
        {
            var current = await stream.ReadAsync(bytes.AsMemory(read), ct).ConfigureAwait(false);
            if (current == 0)
            {
                break;
            }

            read += current;
        }

        if (read != bytes.Length)
        {
            Array.Resize(ref bytes, read);
        }

        return new BlobReadResult(reference, request.Offset, bytes, request.Offset + read >= reference.Length);
    }

    public async Task AbortSessionAsync(string owner, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(owner))
        {
            return;
        }

        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            foreach (var upload in _uploads.Values.Where(candidate =>
                         string.Equals(candidate.Owner, owner, StringComparison.Ordinal)).ToArray())
            {
                _uploads.TryRemove(upload.UploadId, out _);
                await DisposeUploadAsync(upload).ConfigureAwait(false);
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task AbortUploadAsync(
        string uploadId,
        string owner,
        CancellationToken ct = default)
    {
        ValidateOwner(owner);
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (_uploads.TryGetValue(uploadId, out var upload)
                && string.Equals(upload.Owner, owner, StringComparison.Ordinal)
                && _uploads.TryRemove(uploadId, out _))
            {
                await DisposeUploadAsync(upload).ConfigureAwait(false);
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            foreach (var upload in _uploads.Values.ToArray())
            {
                await DisposeUploadAsync(upload).ConfigureAwait(false);
            }

            _uploads.Clear();
        }
        finally
        {
            _gate.Release();
            _gate.Dispose();
        }
    }

    private static void ValidateBeginRequest(BlobBeginRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (!Guid.TryParseExact(request.UploadId, "N", out _)
            || string.IsNullOrWhiteSpace(request.RunId)
            || string.IsNullOrWhiteSpace(request.ContentType)
            || string.IsNullOrWhiteSpace(request.AccessScope)
            || request.Length is < 0
            || request.ExpiresAt <= DateTimeOffset.UtcNow)
        {
            throw new InvalidDataException("The Blob begin metadata is invalid.");
        }
    }

    private void ValidateExpectedLength(BlobBeginRequest request)
    {
        if (request.Length is not { } length)
        {
            return;
        }

        if (length > _maxBlobBytes)
        {
            throw new InvalidDataException("The Blob exceeds the single-Blob quota.");
        }

        var reservedRunBytes = _uploads.Values
            .Where(candidate => string.Equals(
                candidate.Request.RunId,
                request.RunId,
                StringComparison.Ordinal))
            .Sum(static candidate => candidate.Request.Length ?? candidate.Length);
        if (checked(_runUsage.GetValueOrDefault(request.RunId) + reservedRunBytes + length)
            > _maxBlobBytesPerRun)
        {
            throw new InvalidDataException("The Blob exceeds the per-Run quota.");
        }

        var reservedGlobalBytes = _uploads.Values
            .Sum(static candidate => candidate.Request.Length ?? candidate.Length);
        if (checked(_globalUsage + reservedGlobalBytes + length) > _maxGlobalBlobBytes)
        {
            throw new InvalidDataException("The Blob exceeds the global quota.");
        }
    }

    private static void ValidateOwner(string owner) =>
        ArgumentException.ThrowIfNullOrWhiteSpace(owner);

    private static void ValidateBlobId(string blobId)
    {
        if (blobId.Length != 64 || blobId.Any(static c => !Uri.IsHexDigit(c)))
        {
            throw new InvalidDataException("The Blob identifier is not a SHA-256 digest.");
        }
    }

    private string GetBlobPath(string blobId)
    {
        ValidateBlobId(blobId);
        return Path.Combine(_blobDirectory, blobId.ToLowerInvariant() + ".blob");
    }

    private void TryDelete(string uploadId)
    {
        try { File.Delete(Path.Combine(_stagingDirectory, uploadId + ".part")); }
        catch (IOException) { }
    }

    private async Task DisposeUploadAsync(UploadState upload)
    {
        upload.Hash.Dispose();
        if (upload.Stream is not null)
        {
            await upload.Stream.DisposeAsync().ConfigureAwait(false);
            upload.Stream = null;
        }

        TryDelete(upload.UploadId);
    }

    private void ThrowIfDisposed() => ObjectDisposedException.ThrowIf(_disposed != 0, this);

    private sealed class UploadState
    {
        public UploadState(BlobBeginRequest request, string owner, string stagingPath)
        {
            Request = request;
            Owner = owner;
            StagingPath = stagingPath;
        }

        public string UploadId => Request.UploadId;

        public BlobBeginRequest Request { get; }

        public string Owner { get; }

        public string StagingPath { get; }

        public FileStream? Stream { get; set; }

        public IncrementalHash Hash { get; } = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);

        public long Length { get; set; }
    }
}

internal sealed record BlobReadResult(
    BlobReference Reference,
    long Offset,
    byte[] Data,
    bool EndOfBlob);
