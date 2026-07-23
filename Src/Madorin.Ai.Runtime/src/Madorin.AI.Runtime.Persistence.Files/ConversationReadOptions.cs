namespace Madorin.AI.Runtime.Persistence.Files;

/// <summary>Controls how Blob-backed conversation content is presented on read.</summary>
public enum ConversationBlobReadMode
{
    /// <summary>Return Blob references with metadata only (default).</summary>
    Metadata = 0,

    /// <summary>Expand Blob text content when it is available and within limits.</summary>
    Stream = 1,

    /// <summary>Omit Blob payload and mark the block as omitted when unavailable or oversized.</summary>
    Omit = 2
}

/// <summary>Read options for CanonicalHistory APIs.</summary>
public sealed record ConversationReadOptions(
    ConversationBlobReadMode BlobMode = ConversationBlobReadMode.Metadata,
    long MaxExpandedBlobBytes = 1 * 1024 * 1024);
