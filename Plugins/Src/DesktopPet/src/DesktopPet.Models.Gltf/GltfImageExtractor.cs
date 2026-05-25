namespace DesktopPet.Models.Gltf;

internal static class GltfImageExtractor
{
    public static byte[]? ExtractImageBytes(GltfManifest manifest, byte[] binaryChunk, int imageIndex, string modelDirectory)
    {
        var images = manifest.Images;
        if (images is null || imageIndex < 0 || imageIndex >= images.Count)
        {
            return null;
        }

        var image = images[imageIndex];

        if (!string.IsNullOrWhiteSpace(image.Uri))
        {
            if (image.Uri.StartsWith("data:", StringComparison.OrdinalIgnoreCase))
            {
                return DecodeDataUri(image.Uri);
            }

            var filePath = Path.Combine(modelDirectory, image.Uri);
            return File.Exists(filePath) ? File.ReadAllBytes(filePath) : null;
        }

        if (image.BufferView.HasValue)
        {
            return ExtractFromBufferView(manifest, binaryChunk, image.BufferView.Value);
        }

        return null;
    }

    private static byte[]? ExtractFromBufferView(GltfManifest manifest, byte[] binaryChunk, int bufferViewIndex)
    {
        var bufferViews = manifest.BufferViews;
        if (bufferViews is null || bufferViewIndex < 0 || bufferViewIndex >= bufferViews.Count)
        {
            return null;
        }

        var bv = bufferViews[bufferViewIndex];
        var offset = bv.ByteOffset ?? 0;
        var length = bv.ByteLength;

        if (offset + length > binaryChunk.Length)
        {
            return null;
        }

        return binaryChunk.AsSpan(offset, length).ToArray();
    }

    private static byte[]? DecodeDataUri(string uri)
    {
        var commaIndex = uri.IndexOf(',', StringComparison.Ordinal);
        if (commaIndex < 0)
        {
            return null;
        }

        var header = uri.AsSpan(5, commaIndex - 5);
        var isBase64 = header.Contains(";base64", StringComparison.OrdinalIgnoreCase);
        var data = uri.AsSpan(commaIndex + 1);

        return isBase64
            ? Convert.FromBase64String(data.ToString())
            : null;
    }
}
