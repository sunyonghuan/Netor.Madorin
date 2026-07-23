using System.Buffers.Binary;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using Madorin.AI.Runtime.Contracts.Serialization;

namespace Madorin.AI.Runtime.Transport.NamedPipes;

public enum BlobFrameOperation : byte
{
    Begin = 1,
    Chunk = 2,
    Complete = 3,
    Read = 4,
    Abort = 5
}

public readonly record struct BlobFrame(
    BlobFrameOperation Operation,
    bool IsResponse,
    ReadOnlyMemory<byte> Metadata,
    ReadOnlyMemory<byte> Data);

/// <summary>Binary Blob subchannel framing. Metadata is JSON; content bytes are never Base64 encoded.</summary>
public static class BlobFrameProtocol
{
    private const int HeaderLength = 9;
    private const int MaxMetadataBytes = 64 * 1024;
    private static ReadOnlySpan<byte> Magic => "MDB1"u8;

    public static bool IsBlobFrame(ReadOnlyMemory<byte> frame) =>
        frame.Length >= HeaderLength && frame.Span[..Magic.Length].SequenceEqual(Magic);

    public static byte[] Encode<T>(
        BlobFrameOperation operation,
        T? metadata,
        JsonTypeInfo<T> metadataTypeInfo,
        ReadOnlyMemory<byte> data = default,
        bool isResponse = false)
    {
        var metadataBytes = metadata is null
            ? []
            : JsonSerializer.SerializeToUtf8Bytes(metadata, metadataTypeInfo);
        return EncodeRaw(operation, metadataBytes, data, isResponse);
    }

    public static byte[] EncodeRaw(
        BlobFrameOperation operation,
        ReadOnlyMemory<byte> metadata,
        ReadOnlyMemory<byte> data = default,
        bool isResponse = false)
    {
        if (metadata.Length > MaxMetadataBytes)
        {
            throw new InvalidDataException("Blob metadata exceeds the maximum size.");
        }

        var totalLength = checked(HeaderLength + metadata.Length + data.Length);
        if (totalLength > NamedPipeTransport.MaxControlMessageBytes - 44)
        {
            throw new InvalidDataException("Blob frame exceeds the control frame size limit.");
        }

        var frame = new byte[totalLength];
        Magic.CopyTo(frame);
        frame[4] = (byte)((byte)operation | (isResponse ? 0x80 : 0));
        BinaryPrimitives.WriteUInt32BigEndian(
            frame.AsSpan(5, sizeof(uint)),
            checked((uint)metadata.Length));
        metadata.Span.CopyTo(frame.AsSpan(HeaderLength, metadata.Length));
        data.Span.CopyTo(frame.AsSpan(HeaderLength + metadata.Length));
        return frame;
    }

    public static BlobFrame Decode(ReadOnlyMemory<byte> frame)
    {
        if (!IsBlobFrame(frame))
        {
            throw new InvalidDataException("The Blob frame magic or header is invalid.");
        }

        var span = frame.Span;
        var operationByte = span[4];
        var operation = (BlobFrameOperation)(operationByte & 0x7f);
        if (!Enum.IsDefined(operation))
        {
            throw new InvalidDataException($"The Blob operation {operationByte & 0x7f} is unknown.");
        }

        var metadataLength = BinaryPrimitives.ReadUInt32BigEndian(span[5..9]);
        if (metadataLength > MaxMetadataBytes
            || metadataLength > frame.Length - HeaderLength)
        {
            throw new InvalidDataException("The Blob metadata length is invalid.");
        }

        var metadataStart = HeaderLength;
        var dataStart = checked(metadataStart + (int)metadataLength);
        return new BlobFrame(
            operation,
            (operationByte & 0x80) != 0,
            frame.Slice(metadataStart, (int)metadataLength).ToArray(),
            frame[dataStart..].ToArray());
    }

    public static T DeserializeMetadata<T>(BlobFrame frame, JsonTypeInfo<T> typeInfo)
    {
        if (frame.Metadata.Length == 0)
        {
            throw new InvalidDataException("The Blob frame metadata is missing.");
        }

        return JsonSerializer.Deserialize(frame.Metadata.Span, typeInfo)
            ?? throw new InvalidDataException("The Blob frame metadata is empty.");
    }
}
