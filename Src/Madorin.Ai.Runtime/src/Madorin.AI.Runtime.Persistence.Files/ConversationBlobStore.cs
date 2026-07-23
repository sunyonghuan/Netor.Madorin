using System.Security.Cryptography;

namespace Madorin.AI.Runtime.Persistence.Files;

/// <summary>Content-addressed blob storage for oversized conversation content.</summary>
public sealed class ConversationBlobStore
{
    private readonly string _blobDirectory;

    public ConversationBlobStore(string dataDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(dataDirectory);
        _blobDirectory = Path.Combine(Path.GetFullPath(dataDirectory), "blobs");
        Directory.CreateDirectory(_blobDirectory);
    }

    public string BlobDirectory => _blobDirectory;

    public async Task<ConversationBlobReference> StoreAsync(
        ReadOnlyMemory<byte> content,
        string contentType,
        string accessScope,
        DateTimeOffset? expiresAt = null,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(contentType);
        ArgumentException.ThrowIfNullOrWhiteSpace(accessScope);
        var hash = Convert.ToHexString(SHA256.HashData(content.Span)).ToLowerInvariant();
        var path = GetBlobPath(hash);
        if (!File.Exists(path))
        {
            var tempPath = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try
            {
                await using (var stream = new FileStream(
                    tempPath,
                    FileMode.CreateNew,
                    FileAccess.Write,
                    FileShare.None,
                    64 * 1024,
                    FileOptions.Asynchronous | FileOptions.SequentialScan))
                {
                    await stream.WriteAsync(content, ct).ConfigureAwait(false);
                    await stream.FlushAsync(ct).ConfigureAwait(false);
                }

                File.Move(tempPath, path, overwrite: false);
            }
            catch
            {
                if (File.Exists(tempPath))
                {
                    File.Delete(tempPath);
                }

                if (!File.Exists(path))
                {
                    throw;
                }
            }
        }

        return new ConversationBlobReference(
            hash,
            content.Length,
            hash,
            contentType,
            accessScope,
            expiresAt ?? DateTimeOffset.UtcNow.AddDays(30));
    }

    public async Task ValidateAsync(ConversationBlobReference reference, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(reference);
        if (!string.Equals(reference.BlobId, reference.Sha256, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException("The conversation Blob identifier does not match its SHA-256 metadata.");
        }

        var path = GetBlobPath(reference.BlobId);
        var file = new FileInfo(path);
        if (!file.Exists || file.Length != reference.Length)
        {
            throw new InvalidDataException("The conversation Blob content is missing or its length does not match metadata.");
        }

        await using var stream = OpenRead(reference.BlobId);
        var actual = Convert.ToHexString(await SHA256.HashDataAsync(stream, ct).ConfigureAwait(false))
            .ToLowerInvariant();
        if (!string.Equals(actual, reference.Sha256, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException("The conversation Blob content failed SHA-256 validation.");
        }
    }

    public Stream OpenRead(string blobId)
    {
        ValidateBlobId(blobId);
        var path = GetBlobPath(blobId);
        if (!File.Exists(path))
        {
            throw new FileNotFoundException($"Conversation Blob '{blobId}' was not found.", path);
        }

        return new FileStream(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            64 * 1024,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
    }

    public bool Exists(string blobId)
    {
        ValidateBlobId(blobId);
        return File.Exists(GetBlobPath(blobId));
    }

    public int CountReferences()
    {
        if (!Directory.Exists(_blobDirectory))
        {
            return 0;
        }

        return Directory.EnumerateFiles(_blobDirectory, "*.blob").Count();
    }

    public string[] ListBlobIds(int maxCount)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maxCount);
        if (!Directory.Exists(_blobDirectory))
        {
            return [];
        }

        return Directory.EnumerateFiles(_blobDirectory, "*.blob")
            .Select(static path => Path.GetFileNameWithoutExtension(path)!)
            .OrderBy(static id => id, StringComparer.Ordinal)
            .Take(maxCount)
            .ToArray();
    }

    private string GetBlobPath(string blobId)
    {
        ValidateBlobId(blobId);
        return Path.Combine(_blobDirectory, blobId + ".blob");
    }

    private static void ValidateBlobId(string blobId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(blobId);
        if (blobId.Length is not 64
            || blobId.Any(static ch => !Uri.IsHexDigit(ch)))
        {
            throw new ArgumentException("The Blob identifier must be a 64-character hex SHA-256 digest.", nameof(blobId));
        }
    }
}

public sealed record ConversationBlobReference(
    string BlobId,
    long Length,
    string Sha256,
    string ContentType,
    string AccessScope,
    DateTimeOffset ExpiresAt);
