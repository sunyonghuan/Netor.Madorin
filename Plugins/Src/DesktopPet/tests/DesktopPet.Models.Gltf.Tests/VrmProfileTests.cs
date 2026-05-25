using DesktopPet.Models.Gltf;
using System.Buffers.Binary;
using System.Text;

namespace DesktopPet.Models.Gltf.Tests;

[TestClass]
public sealed class VrmProfileTests
{
    [TestMethod]
    public void TryGetVrmProfile_EmptyVrmc_ReturnsProfileWithNoBones()
    {
        var modelPath = Path.Combine(
            AppContext.BaseDirectory,
            "assets", "gltf", "models", "Triangle", "Triangle.gltf");

        var model = new GltfModelLoader().Load(modelPath);
        var profile = model.TryGetVrmProfile();

        Assert.IsNotNull(profile);
        Assert.AreEqual("1.0", profile.SpecVersion);
        Assert.IsEmpty(profile.HumanoidBoneNodeIndices);
    }

    [TestMethod]
    public void TryGetVrmProfile_Vrm1Humanoid_ParsesBoneNodeIndices()
    {
        var model = LoadGlbWithVrm1Humanoid();
        var profile = model.TryGetVrmProfile();

        Assert.IsNotNull(profile);
        Assert.AreEqual("1.0", profile.SpecVersion);
        Assert.AreEqual(3, profile.GetBoneNodeIndex(VrmHumanoidBone.Hips));
        Assert.AreEqual(4, profile.GetBoneNodeIndex(VrmHumanoidBone.Spine));
        Assert.AreEqual(10, profile.GetBoneNodeIndex(VrmHumanoidBone.Head));
        Assert.IsNull(profile.GetBoneNodeIndex("nonexistent"));
    }

    [TestMethod]
    public void TryGetVrmProfile_Vrm0Humanoid_ParsesBoneNodeIndices()
    {
        var model = LoadGlbWithVrm0Humanoid();
        var profile = model.TryGetVrmProfile();

        Assert.IsNotNull(profile);
        Assert.IsTrue(profile.SpecVersion.StartsWith("0", StringComparison.Ordinal));
        Assert.AreEqual(5, profile.GetBoneNodeIndex(VrmHumanoidBone.Hips));
        Assert.AreEqual(6, profile.GetBoneNodeIndex(VrmHumanoidBone.Head));
    }

    [TestMethod]
    public void TryGetVrmProfile_Vrm1WithExpressions_ParsesPresets()
    {
        var model = LoadGlbWithVrm1Expressions();
        var profile = model.TryGetVrmProfile();

        Assert.IsNotNull(profile);
        Assert.IsTrue(profile.ExpressionPresets.Any(p => p.Name == "happy"));
        Assert.IsTrue(profile.ExpressionPresets.Any(p => p.Name == "angry"));
    }

    [TestMethod]
    public void TryGetVrmProfile_NoVrmExtension_ReturnsNull()
    {
        var model = LoadGlbWithoutVrm();
        var profile = model.TryGetVrmProfile();

        Assert.IsNull(profile);
    }

    private static GltfModel LoadGlbWithVrm1Humanoid()
        => LoadInlineGlb("""
            {
              "asset": { "version": "2.0" },
              "extensionsUsed": ["VRMC_vrm"],
              "extensions": {
                "VRMC_vrm": {
                  "specVersion": "1.0",
                  "humanoid": {
                    "humanBones": {
                      "hips":  { "node": 3 },
                      "spine": { "node": 4 },
                      "head":  { "node": 10 }
                    }
                  }
                }
              }
            }
            """);

    private static GltfModel LoadGlbWithVrm0Humanoid()
        => LoadInlineGlb("""
            {
              "asset": { "version": "2.0" },
              "extensionsUsed": ["VRM"],
              "extensions": {
                "VRM": {
                  "specVersion": "0.0",
                  "humanoid": {
                    "humanBones": [
                      { "bone": "hips", "node": 5 },
                      { "bone": "head", "node": 6 }
                    ]
                  }
                }
              }
            }
            """);

    private static GltfModel LoadGlbWithVrm1Expressions()
        => LoadInlineGlb("""
            {
              "asset": { "version": "2.0" },
              "extensionsUsed": ["VRMC_vrm"],
              "extensions": {
                "VRMC_vrm": {
                  "specVersion": "1.0",
                  "humanoid": { "humanBones": {} },
                  "expressions": {
                    "preset": {
                      "happy": { "isBinary": false },
                      "angry": { "isBinary": true }
                    }
                  }
                }
              }
            }
            """);

    [TestMethod]
    public void TryGetVrmProfile_Vrm1MorphTargetBinds_ParsedCorrectly()
    {
        var model = LoadInlineGlb("""
            {
              "asset": { "version": "2.0" },
              "extensionsUsed": ["VRMC_vrm"],
              "nodes": [{ "name": "HeadNode", "mesh": 0 }],
              "meshes": [{ "name": "Face", "primitives": [] }],
              "extensions": {
                "VRMC_vrm": {
                  "specVersion": "1.0",
                  "humanoid": { "humanBones": {} },
                  "expressions": {
                    "preset": {
                      "happy": {
                        "isBinary": false,
                        "morphTargetBinds": [
                          { "node": 0, "index": 2, "weight": 0.8 }
                        ]
                      }
                    }
                  }
                }
              }
            }
            """);

        var profile = model.TryGetVrmProfile();
        Assert.IsNotNull(profile);

        var happy = profile.ExpressionPresets.FirstOrDefault(p => p.Name == "happy");
        Assert.IsNotNull(happy);
        Assert.HasCount(1, happy.MorphTargetBinds);

        var bind = happy.MorphTargetBinds[0];
        Assert.AreEqual("Face", bind.MeshName);
        Assert.AreEqual(2, bind.MorphTargetIndex);
        Assert.AreEqual(0.8f, bind.NormalizedWeight, 0.001f);
    }

    [TestMethod]
    public void TryGetVrmProfile_Vrm0MorphBinds_NormalizedWeight()
    {
        var model = LoadInlineGlb("""
            {
              "asset": { "version": "2.0" },
              "extensionsUsed": ["VRM"],
              "meshes": [{ "name": "Body", "primitives": [] }],
              "extensions": {
                "VRM": {
                  "specVersion": "0.0",
                  "humanoid": { "humanBones": [] },
                  "blendShapeMaster": {
                    "blendShapeGroups": [
                      {
                        "presetName": "joy",
                        "binds": [
                          { "mesh": 0, "index": 1, "weight": 50 }
                        ]
                      }
                    ]
                  }
                }
              }
            }
            """);

        var profile = model.TryGetVrmProfile();
        Assert.IsNotNull(profile);

        var joy = profile.ExpressionPresets.FirstOrDefault(p => p.Name == "joy");
        Assert.IsNotNull(joy);
        Assert.HasCount(1, joy.MorphTargetBinds);

        var bind = joy.MorphTargetBinds[0];
        Assert.AreEqual("Body", bind.MeshName);
        Assert.AreEqual(1, bind.MorphTargetIndex);
        Assert.AreEqual(0.5f, bind.NormalizedWeight, 0.001f);
    }

    private static GltfModel LoadGlbWithoutVrm()
        => LoadInlineGlb("""
            {
              "asset": { "version": "2.0" }
            }
            """);

    private static GltfModel LoadInlineGlb(string json)
    {
        var path = Path.Combine(Path.GetTempPath(), "DesktopPetVrmTests",
            Guid.NewGuid().ToString("N"), "test.glb");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        try
        {
            WriteMinimalGlb(path, json);
            return new GltfModelLoader().Load(path);
        }
        finally
        {
            var dir = Path.GetDirectoryName(path);
            if (dir is not null && Directory.Exists(dir))
            {
                Directory.Delete(dir, recursive: true);
            }
        }
    }

    private static void WriteMinimalGlb(string path, string json)
    {
        var jsonBytes = Pad(Encoding.UTF8.GetBytes(json), 0x20);
        var totalLength = 12 + 8 + jsonBytes.Length;

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
