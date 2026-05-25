using System.Buffers.Binary;
using System.Text;
using System.Text.Json;

namespace DesktopPet.Models.Gltf;

public sealed class GltfModelLoader
{
    private const uint GlbMagic = 0x46546C67;
    private const uint JsonChunkType = 0x4E4F534A;
    private const uint BinChunkType = 0x004E4942;

    public GltfModel Load(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);

        var fullPath = Path.GetFullPath(path);
        var extension = Path.GetExtension(fullPath);
        if (string.Equals(extension, ".glb", StringComparison.OrdinalIgnoreCase))
        {
            return LoadGlb(fullPath);
        }

        if (string.Equals(extension, ".gltf", StringComparison.OrdinalIgnoreCase))
        {
            return LoadGltf(fullPath);
        }

        throw new NotSupportedException($"Unsupported glTF file extension: {extension}");
    }

    public IReadOnlyList<string> FindModelFiles(string modelsDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(modelsDirectory);

        if (!Directory.Exists(modelsDirectory))
        {
            return [];
        }

        return Directory
            .EnumerateFiles(modelsDirectory, "*.*", SearchOption.AllDirectories)
            .Where(path => path.EndsWith(".glb", StringComparison.OrdinalIgnoreCase)
                || path.EndsWith(".gltf", StringComparison.OrdinalIgnoreCase))
            .Order(StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    private static GltfModel LoadGlb(string fullPath)
    {
        var bytes = File.ReadAllBytes(fullPath);
        if (bytes.Length < 12)
        {
            throw new InvalidDataException("GLB file is too small.");
        }

        var magic = BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(0, 4));
        var version = BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(4, 4));
        var declaredLength = BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(8, 4));

        if (magic != GlbMagic)
        {
            throw new InvalidDataException("Invalid GLB magic.");
        }

        if (version != 2)
        {
            throw new NotSupportedException($"Unsupported GLB version: {version}");
        }

        if (declaredLength != bytes.Length)
        {
            throw new InvalidDataException("GLB declared length does not match file length.");
        }

        string? json = null;
        byte[] binaryChunk = [];
        var offset = 12;
        while (offset < bytes.Length)
        {
            if (offset + 8 > bytes.Length)
            {
                throw new InvalidDataException("GLB chunk header is incomplete.");
            }

            var chunkLength = checked((int)BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(offset, 4)));
            var chunkType = BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(offset + 4, 4));
            offset += 8;

            if (offset + chunkLength > bytes.Length)
            {
                throw new InvalidDataException("GLB chunk length exceeds file length.");
            }

            var chunk = bytes.AsSpan(offset, chunkLength);
            if (chunkType == JsonChunkType)
            {
                json = Encoding.UTF8.GetString(chunk).TrimEnd('\0', ' ', '\r', '\n', '\t');
            }
            else if (chunkType == BinChunkType)
            {
                binaryChunk = chunk.ToArray();
            }

            offset += chunkLength;
        }

        if (string.IsNullOrWhiteSpace(json))
        {
            throw new InvalidDataException("GLB JSON chunk is missing.");
        }

        return CreateModel(fullPath, json, binaryChunk);
    }

    private static GltfModel LoadGltf(string fullPath)
    {
        var json = File.ReadAllText(fullPath);
        var binaryChunk = TryLoadCompanionBin(fullPath, json);
        return CreateModel(fullPath, json, binaryChunk);
    }

    private static byte[] TryLoadCompanionBin(string gltfPath, string json)
    {
        var manifest = JsonSerializer.Deserialize(json, GltfJsonContext.Default.GltfManifest);
        var buffers = manifest?.Buffers;
        if (buffers is null || buffers.Count == 0)
        {
            return [];
        }

        var results = new List<byte[]>(buffers.Count);
        foreach (var buffer in buffers)
        {
            if (!string.IsNullOrWhiteSpace(buffer.Uri))
            {
                var uriPath = Path.Combine(Path.GetDirectoryName(gltfPath) ?? string.Empty, buffer.Uri);
                results.Add(File.Exists(uriPath) ? File.ReadAllBytes(uriPath) : []);
                continue;
            }

            var defaultBinPath = Path.ChangeExtension(gltfPath, ".bin");
            results.Add(File.Exists(defaultBinPath) ? File.ReadAllBytes(defaultBinPath) : []);
        }

        if (results.Count == 1)
        {
            return results[0];
        }

        var totalLength = results.Sum(r => r.Length);
        var combined = new byte[totalLength];
        var offset = 0;
        foreach (var chunk in results)
        {
            chunk.CopyTo(combined, offset);
            offset += chunk.Length;
        }

        return combined;
    }

    private static GltfModel CreateModel(string fullPath, string json, byte[] binaryChunk)
    {
        var manifest = JsonSerializer.Deserialize(json, GltfJsonContext.Default.GltfManifest)
            ?? throw new InvalidDataException($"Failed to parse glTF manifest: {fullPath}");

        if (manifest.Asset?.Version is null)
        {
            throw new InvalidDataException("glTF asset version is missing.");
        }

        if (!manifest.Asset.Version.StartsWith("2.", StringComparison.Ordinal))
        {
            throw new NotSupportedException($"Unsupported glTF asset version: {manifest.Asset.Version}");
        }

        var usedExtensions = manifest.ExtensionsUsed ?? [];
        var requiredExtensions = manifest.ExtensionsRequired ?? [];
        var hasVrmExtension = usedExtensions.Any(IsVrmExtension)
            || requiredExtensions.Any(IsVrmExtension)
            || (manifest.Extensions?.Keys.Any(IsVrmExtension) ?? false);

        var summary = new GltfModelSummary(
            Path.GetFileNameWithoutExtension(fullPath),
            fullPath,
            manifest.Scenes?.Count ?? 0,
            manifest.Nodes?.Count ?? 0,
            manifest.Meshes?.Count ?? 0,
            manifest.Materials?.Count ?? 0,
            manifest.Textures?.Count ?? 0,
            manifest.Images?.Count ?? 0,
            manifest.Animations?.Count ?? 0,
            manifest.BufferViews?.Count ?? 0,
            manifest.Accessors?.Count ?? 0,
            binaryChunk.Length > 0,
            hasVrmExtension,
            usedExtensions,
            requiredExtensions);

        return new GltfModel(manifest, summary, json, binaryChunk);
    }

    private static bool IsVrmExtension(string extension)
    {
        return string.Equals(extension, "VRM", StringComparison.Ordinal)
            || string.Equals(extension, "VRMC_vrm", StringComparison.Ordinal);
    }
}
