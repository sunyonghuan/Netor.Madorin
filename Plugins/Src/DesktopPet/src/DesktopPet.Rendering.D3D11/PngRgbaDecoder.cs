using System.Buffers.Binary;
using System.IO.Compression;

namespace DesktopPet.Rendering.D3D11;

public static class PngRgbaDecoder
{
    private static readonly byte[] Signature = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A];

    public static PngImage Decode(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        return Decode(File.ReadAllBytes(path));
    }

    public static PngImage Decode(ReadOnlySpan<byte> pngBytes)
    {
        if (pngBytes.Length < Signature.Length || !pngBytes[..Signature.Length].SequenceEqual(Signature))
        {
            throw new InvalidDataException("PNG signature is invalid.");
        }

        var offset = Signature.Length;
        var width = 0;
        var height = 0;
        var colorType = 0;
        using var compressed = new MemoryStream();

        while (offset + 12 <= pngBytes.Length)
        {
            var length = checked((int)BinaryPrimitives.ReadUInt32BigEndian(pngBytes.Slice(offset, 4)));
            offset += 4;
            var chunkType = pngBytes.Slice(offset, 4);
            offset += 4;

            if (offset + length + 4 > pngBytes.Length)
            {
                throw new InvalidDataException("PNG chunk length exceeds available data.");
            }

            var chunkData = pngBytes.Slice(offset, length);
            offset += length;
            offset += 4;

            if (chunkType.SequenceEqual("IHDR"u8))
            {
                width = checked((int)BinaryPrimitives.ReadUInt32BigEndian(chunkData[..4]));
                height = checked((int)BinaryPrimitives.ReadUInt32BigEndian(chunkData.Slice(4, 4)));
                var bitDepth = chunkData[8];
                colorType = chunkData[9];
                var compressionMethod = chunkData[10];
                var filterMethod = chunkData[11];
                var interlaceMethod = chunkData[12];

                if (bitDepth != 8 || compressionMethod != 0 || filterMethod != 0 || interlaceMethod != 0)
                {
                    throw new NotSupportedException("Only non-interlaced 8-bit PNG textures are supported.");
                }

                if (colorType is not 2 and not 6)
                {
                    throw new NotSupportedException("Only RGB and RGBA PNG textures are supported.");
                }
            }
            else if (chunkType.SequenceEqual("IDAT"u8))
            {
                compressed.Write(chunkData);
            }
            else if (chunkType.SequenceEqual("IEND"u8))
            {
                break;
            }
        }

        if (width <= 0 || height <= 0 || compressed.Length == 0)
        {
            throw new InvalidDataException("PNG is missing IHDR or IDAT data.");
        }

        compressed.Position = 0;
        using var deflate = new ZLibStream(compressed, CompressionMode.Decompress);
        using var rawStream = new MemoryStream();
        deflate.CopyTo(rawStream);

        var bytesPerPixel = colorType == 6 ? 4 : 3;
        var stride = checked(width * bytesPerPixel);
        var expectedRawSize = checked((stride + 1) * height);
        var raw = rawStream.ToArray();
        if (raw.Length < expectedRawSize)
        {
            throw new InvalidDataException("PNG decompressed data is shorter than expected.");
        }

        var rgba = new byte[checked(width * height * 4)];
        var previous = new byte[stride];
        var current = new byte[stride];
        var rawOffset = 0;
        var rgbaOffset = 0;

        for (var y = 0; y < height; y++)
        {
            var filter = raw[rawOffset++];
            raw.AsSpan(rawOffset, stride).CopyTo(current);
            rawOffset += stride;
            UnfilterRow(filter, current, previous, bytesPerPixel);

            for (var x = 0; x < width; x++)
            {
                var sourceOffset = x * bytesPerPixel;
                rgba[rgbaOffset++] = current[sourceOffset];
                rgba[rgbaOffset++] = current[sourceOffset + 1];
                rgba[rgbaOffset++] = current[sourceOffset + 2];
                rgba[rgbaOffset++] = colorType == 6 ? current[sourceOffset + 3] : byte.MaxValue;
            }

            (previous, current) = (current, previous);
        }

        return new PngImage(width, height, rgba);
    }

    private static void UnfilterRow(byte filter, Span<byte> current, ReadOnlySpan<byte> previous, int bytesPerPixel)
    {
        switch (filter)
        {
            case 0:
                return;
            case 1:
                for (var i = 0; i < current.Length; i++)
                {
                    current[i] = unchecked((byte)(current[i] + Left(current, i, bytesPerPixel)));
                }

                return;
            case 2:
                for (var i = 0; i < current.Length; i++)
                {
                    current[i] = unchecked((byte)(current[i] + previous[i]));
                }

                return;
            case 3:
                for (var i = 0; i < current.Length; i++)
                {
                    current[i] = unchecked((byte)(current[i] + ((Left(current, i, bytesPerPixel) + previous[i]) >> 1)));
                }

                return;
            case 4:
                for (var i = 0; i < current.Length; i++)
                {
                    current[i] = unchecked((byte)(current[i] + Paeth(
                        Left(current, i, bytesPerPixel),
                        previous[i],
                        i >= bytesPerPixel ? previous[i - bytesPerPixel] : 0)));
                }

                return;
            default:
                throw new NotSupportedException($"PNG filter type {filter} is not supported.");
        }
    }

    private static int Left(ReadOnlySpan<byte> row, int index, int bytesPerPixel)
    {
        return index >= bytesPerPixel ? row[index - bytesPerPixel] : 0;
    }

    private static int Paeth(int left, int above, int upperLeft)
    {
        var prediction = left + above - upperLeft;
        var leftDistance = Math.Abs(prediction - left);
        var aboveDistance = Math.Abs(prediction - above);
        var upperLeftDistance = Math.Abs(prediction - upperLeft);

        if (leftDistance <= aboveDistance && leftDistance <= upperLeftDistance)
        {
            return left;
        }

        return aboveDistance <= upperLeftDistance ? above : upperLeft;
    }
}
