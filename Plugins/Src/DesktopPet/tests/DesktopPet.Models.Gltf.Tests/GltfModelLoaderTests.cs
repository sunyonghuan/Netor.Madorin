using DesktopPet.Models.Gltf;
using System.Buffers.Binary;
using System.Text;

namespace DesktopPet.Models.Gltf.Tests;

[TestClass]
public sealed class GltfModelLoaderTests
{
    [TestMethod]
    public void Load_BundledTriangleGltf_ReturnsSummary()
    {
        var modelPath = Path.Combine(
            AppContext.BaseDirectory,
            "assets",
            "gltf",
            "models",
            "Triangle",
            "Triangle.gltf");

        var model = new GltfModelLoader().Load(modelPath);

        Assert.AreEqual("Triangle", model.Summary.Name);
        Assert.AreEqual(1, model.Summary.SceneCount);
        Assert.AreEqual(1, model.Summary.NodeCount);
        Assert.AreEqual(1, model.Summary.MeshCount);
        Assert.AreEqual(1, model.Summary.MaterialCount);
        Assert.AreEqual(1, model.Summary.AnimationCount);
        Assert.AreEqual(2, model.Summary.BufferViewCount);
        Assert.AreEqual(2, model.Summary.AccessorCount);
        Assert.IsTrue(model.Summary.HasVrmExtension);
        Assert.IsTrue(model.Summary.HasBinaryChunk);
    }

    [TestMethod]
    public void FindModelFiles_ReturnsBundledModels()
    {
        var modelsDirectory = Path.Combine(AppContext.BaseDirectory, "assets", "gltf", "models");

        var modelFiles = new GltfModelLoader().FindModelFiles(modelsDirectory);

        Assert.IsTrue(modelFiles.Any(path => string.Equals(Path.GetFileName(path), "Triangle.gltf", StringComparison.OrdinalIgnoreCase)));
    }

    [TestMethod]
    public void Load_GeneratedGlb_ReturnsBinaryChunk()
    {
        var path = Path.Combine(Path.GetTempPath(), "DesktopPetTests", Guid.NewGuid().ToString("N"), "Triangle.glb");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);

        try
        {
            WriteTriangleGlb(path);

            var model = new GltfModelLoader().Load(path);

            Assert.AreEqual("Triangle", model.Summary.Name);
            Assert.IsTrue(model.Summary.HasBinaryChunk);
            Assert.IsTrue(model.Summary.HasVrmExtension);
            Assert.HasCount(44, model.BinaryChunk);
        }
        finally
        {
            var directory = Path.GetDirectoryName(path);
            if (!string.IsNullOrWhiteSpace(directory) && Directory.Exists(directory))
            {
                Directory.Delete(directory, recursive: true);
            }
        }
    }

    private static void WriteTriangleGlb(string path)
    {
        var json = """
            {
              "asset": { "version": "2.0", "generator": "DesktopPet test asset" },
              "extensionsUsed": ["VRMC_vrm"],
              "extensions": { "VRMC_vrm": {} },
              "scenes": [{ "nodes": [0], "name": "Scene" }],
              "nodes": [{ "mesh": 0, "name": "TriangleNode" }],
              "meshes": [{ "name": "Triangle", "primitives": [{ "attributes": { "POSITION": 0 }, "indices": 1, "material": 0 }] }],
              "materials": [{ "name": "Default" }],
              "animations": [{ "name": "Idle" }],
              "buffers": [{ "byteLength": 44 }],
              "bufferViews": [
                { "buffer": 0, "byteOffset": 0, "byteLength": 36 },
                { "buffer": 0, "byteOffset": 36, "byteLength": 6 }
              ],
              "accessors": [
                { "bufferView": 0, "componentType": 5126, "count": 3, "type": "VEC3" },
                { "bufferView": 1, "componentType": 5123, "count": 3, "type": "SCALAR" }
              ]
            }
            """;

        var jsonBytes = Pad(Encoding.UTF8.GetBytes(json), 0x20);
        var binBytes = CreateTriangleBinaryChunk();
        var totalLength = 12 + 8 + jsonBytes.Length + 8 + binBytes.Length;

        using var file = File.Create(path);
        Span<byte> header = stackalloc byte[12];
        BinaryPrimitives.WriteUInt32LittleEndian(header[0..4], 0x46546C67);
        BinaryPrimitives.WriteUInt32LittleEndian(header[4..8], 2);
        BinaryPrimitives.WriteUInt32LittleEndian(header[8..12], (uint)totalLength);
        file.Write(header);

        Span<byte> chunkHeader = stackalloc byte[8];
        BinaryPrimitives.WriteUInt32LittleEndian(chunkHeader[0..4], (uint)jsonBytes.Length);
        BinaryPrimitives.WriteUInt32LittleEndian(chunkHeader[4..8], 0x4E4F534A);
        file.Write(chunkHeader);
        file.Write(jsonBytes);

        BinaryPrimitives.WriteUInt32LittleEndian(chunkHeader[0..4], (uint)binBytes.Length);
        BinaryPrimitives.WriteUInt32LittleEndian(chunkHeader[4..8], 0x004E4942);
        file.Write(chunkHeader);
        file.Write(binBytes);
    }

    private static byte[] CreateTriangleBinaryChunk()
    {
        using var stream = new MemoryStream();
        using var writer = new BinaryWriter(stream, Encoding.UTF8, leaveOpen: true);

        writer.Write(0.0f);
        writer.Write(0.0f);
        writer.Write(0.0f);
        writer.Write(1.0f);
        writer.Write(0.0f);
        writer.Write(0.0f);
        writer.Write(0.0f);
        writer.Write(1.0f);
        writer.Write(0.0f);
        writer.Write((ushort)0);
        writer.Write((ushort)1);
        writer.Write((ushort)2);
        writer.Write((ushort)0);

        return stream.ToArray();
    }

    [TestMethod]
    public void ExtractMeshPrimitives_BundledTriangleGltf_ReturnsOnePrimitive()
    {
        var modelPath = Path.Combine(
            AppContext.BaseDirectory,
            "assets",
            "gltf",
            "models",
            "Triangle",
            "Triangle.gltf");

        var model = new GltfModelLoader().Load(modelPath);
        var primitives = model.ExtractMeshPrimitives();

        Assert.HasCount(1, primitives);
        Assert.AreEqual("Triangle", primitives[0].MeshName);
        Assert.AreEqual(0, primitives[0].PrimitiveIndex);
        Assert.AreEqual(3, primitives[0].VertexCount);
        Assert.HasCount(3, primitives[0].Indices);
    }

    [TestMethod]
    public void ExtractMeshPrimitives_BundledTriangleGltf_PositionsMatchBin()
    {
        var modelPath = Path.Combine(
            AppContext.BaseDirectory,
            "assets",
            "gltf",
            "models",
            "Triangle",
            "Triangle.gltf");

        var model = new GltfModelLoader().Load(modelPath);
        var primitive = model.ExtractMeshPrimitives()[0];

        // Vertex 0: (0, 0, 0)
        Assert.AreEqual(0.0f, primitive.Positions[0], delta: 1e-6f);
        Assert.AreEqual(0.0f, primitive.Positions[1], delta: 1e-6f);
        Assert.AreEqual(0.0f, primitive.Positions[2], delta: 1e-6f);
        // Vertex 1: (1, 0, 0)
        Assert.AreEqual(1.0f, primitive.Positions[3], delta: 1e-6f);
        Assert.AreEqual(0.0f, primitive.Positions[4], delta: 1e-6f);
        Assert.AreEqual(0.0f, primitive.Positions[5], delta: 1e-6f);
        // Vertex 2: (0, 1, 0)
        Assert.AreEqual(0.0f, primitive.Positions[6], delta: 1e-6f);
        Assert.AreEqual(1.0f, primitive.Positions[7], delta: 1e-6f);
        Assert.AreEqual(0.0f, primitive.Positions[8], delta: 1e-6f);

        Assert.AreEqual((ushort)0, primitive.Indices[0]);
        Assert.AreEqual((ushort)1, primitive.Indices[1]);
        Assert.AreEqual((ushort)2, primitive.Indices[2]);
    }

    [TestMethod]
    public void ExtractMeshPrimitives_GeneratedGlb_ReturnsOnePrimitive()
    {
        var path = Path.Combine(Path.GetTempPath(), "DesktopPetTests", Guid.NewGuid().ToString("N"), "Triangle.glb");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);

        try
        {
            WriteTriangleGlb(path);

            var model = new GltfModelLoader().Load(path);
            var primitives = model.ExtractMeshPrimitives();

            Assert.HasCount(1, primitives);
            Assert.AreEqual(3, primitives[0].VertexCount);
            Assert.AreEqual((ushort)0, primitives[0].Indices[0]);
            Assert.AreEqual((ushort)1, primitives[0].Indices[1]);
            Assert.AreEqual((ushort)2, primitives[0].Indices[2]);
        }
        finally
        {
            var directory = Path.GetDirectoryName(path);
            if (!string.IsNullOrWhiteSpace(directory) && Directory.Exists(directory))
            {
                Directory.Delete(directory, recursive: true);
            }
        }
    }

    private static byte[] Pad(byte[] value, byte pad)
    {
        var originalLength = value.Length;
        var paddedLength = (value.Length + 3) & ~3;
        Array.Resize(ref value, paddedLength);

        for (var i = originalLength; i < value.Length; i++)
        {
            value[i] = pad;
        }

        return value;
    }
}
