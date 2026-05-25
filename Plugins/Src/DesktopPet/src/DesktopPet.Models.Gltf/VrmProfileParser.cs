using System.Text.Json;

namespace DesktopPet.Models.Gltf;

internal static class VrmProfileParser
{
    private const string ExtensionVrm0 = "VRM";
    private const string ExtensionVrm1 = "VRMC_vrm";
    private const string ExtensionSpringBone1 = "VRMC_springBone";

    public static VrmProfile? TryParse(GltfManifest manifest)
    {
        var extensions = manifest.Extensions;
        if (extensions is null)
        {
            return null;
        }

        if (extensions.TryGetValue(ExtensionVrm1, out var vrm1Element))
        {
            return TryParseVrm1(vrm1Element, extensions, manifest);
        }

        if (extensions.TryGetValue(ExtensionVrm0, out var vrm0Element))
        {
            return TryParseVrm0(vrm0Element, manifest);
        }

        return null;
    }

    private static VrmProfile? TryParseVrm1(
        JsonElement vrm1,
        IReadOnlyDictionary<string, JsonElement> allExtensions,
        GltfManifest manifest)
    {
        var boneMap = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

        if (vrm1.TryGetProperty("humanoid", out var humanoid)
            && humanoid.TryGetProperty("humanBones", out var humanBones)
            && humanBones.ValueKind == JsonValueKind.Object)
        {
            foreach (var bone in humanBones.EnumerateObject())
            {
                if (bone.Value.TryGetProperty("node", out var nodeEl)
                    && nodeEl.TryGetInt32(out var nodeIndex))
                {
                    boneMap[bone.Name] = nodeIndex;
                }
            }
        }

        var expressions = ParseExpressions1(vrm1, manifest);
        var lookAt = ParseLookAt1(vrm1);
        var springBone = ParseSpringBone1(allExtensions);

        var specVersion = vrm1.TryGetProperty("specVersion", out var sv)
            ? sv.GetString() ?? "1.0"
            : "1.0";

        return new VrmProfile(specVersion, boneMap, expressions, lookAt, springBone);
    }

    private static VrmProfile? TryParseVrm0(JsonElement vrm0, GltfManifest manifest)
    {
        var boneMap = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

        if (vrm0.TryGetProperty("humanoid", out var humanoid)
            && humanoid.TryGetProperty("humanBones", out var humanBones)
            && humanBones.ValueKind == JsonValueKind.Array)
        {
            foreach (var bone in humanBones.EnumerateArray())
            {
                if (bone.TryGetProperty("bone", out var boneNameEl)
                    && bone.TryGetProperty("node", out var nodeEl)
                    && nodeEl.TryGetInt32(out var nodeIndex))
                {
                    var boneName = boneNameEl.GetString();
                    if (!string.IsNullOrWhiteSpace(boneName))
                    {
                        boneMap[boneName] = nodeIndex;
                    }
                }
            }
        }

        var expressions = ParseBlendShapes0(vrm0, manifest);
        var lookAt = ParseFirstPerson0(vrm0);
        var springBone = ParseSecondaryAnimation0(vrm0);

        var specVersion = vrm0.TryGetProperty("specVersion", out var sv)
            ? sv.GetString() ?? "0.0"
            : "0.0";

        return new VrmProfile(specVersion, boneMap, expressions, lookAt, springBone);
    }

    private static List<VrmExpressionPreset> ParseExpressions1(JsonElement vrm1, GltfManifest manifest)
    {
        var result = new List<VrmExpressionPreset>();
        if (!vrm1.TryGetProperty("expressions", out var expressions))
        {
            return result;
        }

        if (expressions.TryGetProperty("preset", out var preset)
            && preset.ValueKind == JsonValueKind.Object)
        {
            foreach (var entry in preset.EnumerateObject())
            {
                var isBinary = entry.Value.TryGetProperty("isBinary", out var ib)
                    && ib.ValueKind == JsonValueKind.True;
                var binds = ParseMorphBinds1(entry.Value, manifest);
                result.Add(new VrmExpressionPreset(entry.Name, isBinary, binds));
            }
        }

        return result;
    }

    private static IReadOnlyList<VrmMorphTargetBind> ParseMorphBinds1(JsonElement expressionEl, GltfManifest manifest)
    {
        var result = new List<VrmMorphTargetBind>();
        if (!expressionEl.TryGetProperty("morphTargetBinds", out var bindsEl)
            || bindsEl.ValueKind != JsonValueKind.Array)
        {
            return result;
        }

        var nodes = manifest.Nodes;
        var meshes = manifest.Meshes;

        foreach (var bind in bindsEl.EnumerateArray())
        {
            if (!bind.TryGetProperty("node", out var nodeEl) || !nodeEl.TryGetInt32(out var nodeIndex)) continue;
            if (!bind.TryGetProperty("index", out var idxEl) || !idxEl.TryGetInt32(out var morphIdx)) continue;
            if (!bind.TryGetProperty("weight", out var weightEl) || !weightEl.TryGetSingle(out var weight)) continue;

            if (nodes is null || nodeIndex < 0 || nodeIndex >= nodes.Count) continue;
            var meshIdx = nodes[nodeIndex].Mesh;
            if (!meshIdx.HasValue) continue;

            if (meshes is null || meshIdx.Value < 0 || meshIdx.Value >= meshes.Count) continue;
            var meshName = meshes[meshIdx.Value].Name ?? $"Mesh{meshIdx.Value}";

            result.Add(new VrmMorphTargetBind(meshName, morphIdx, weight));
        }

        return result;
    }

    private static VrmLookAt? ParseLookAt1(JsonElement vrm1)
    {
        if (!vrm1.TryGetProperty("lookAt", out var lookAt))
        {
            return null;
        }

        var type = lookAt.TryGetProperty("type", out var typeEl)
            ? typeEl.GetString() ?? "bone"
            : "bone";

        return new VrmLookAt(type);
    }

    private static VrmSpringBoneInfo ParseSpringBone1(IReadOnlyDictionary<string, JsonElement> extensions)
    {
        if (!extensions.TryGetValue(ExtensionSpringBone1, out var sb))
        {
            return new VrmSpringBoneInfo(0, 0);
        }

        var joints = 0;
        var colliders = 0;

        if (sb.TryGetProperty("springs", out var springs) && springs.ValueKind == JsonValueKind.Array)
        {
            foreach (var spring in springs.EnumerateArray())
            {
                if (spring.TryGetProperty("joints", out var jArr) && jArr.ValueKind == JsonValueKind.Array)
                {
                    joints += jArr.GetArrayLength();
                }
            }
        }

        if (sb.TryGetProperty("colliders", out var colArr) && colArr.ValueKind == JsonValueKind.Array)
        {
            colliders = colArr.GetArrayLength();
        }

        return new VrmSpringBoneInfo(joints, colliders);
    }

    private static List<VrmExpressionPreset> ParseBlendShapes0(JsonElement vrm0, GltfManifest manifest)
    {
        var result = new List<VrmExpressionPreset>();
        if (!vrm0.TryGetProperty("blendShapeMaster", out var master))
        {
            return result;
        }

        if (master.TryGetProperty("blendShapeGroups", out var groups)
            && groups.ValueKind == JsonValueKind.Array)
        {
            foreach (var group in groups.EnumerateArray())
            {
                var name = group.TryGetProperty("presetName", out var pn)
                    ? pn.GetString()
                    : group.TryGetProperty("name", out var n)
                        ? n.GetString()
                        : null;

                if (string.IsNullOrWhiteSpace(name)) continue;

                var binds = ParseMorphBinds0(group, manifest);
                result.Add(new VrmExpressionPreset(name!, false, binds));
            }
        }

        return result;
    }

    private static IReadOnlyList<VrmMorphTargetBind> ParseMorphBinds0(JsonElement groupEl, GltfManifest manifest)
    {
        var result = new List<VrmMorphTargetBind>();
        if (!groupEl.TryGetProperty("binds", out var bindsEl)
            || bindsEl.ValueKind != JsonValueKind.Array)
        {
            return result;
        }

        var meshes = manifest.Meshes;

        foreach (var bind in bindsEl.EnumerateArray())
        {
            if (!bind.TryGetProperty("mesh", out var meshIdxEl) || !meshIdxEl.TryGetInt32(out var meshIdx)) continue;
            if (!bind.TryGetProperty("index", out var idxEl) || !idxEl.TryGetInt32(out var morphIdx)) continue;
            if (!bind.TryGetProperty("weight", out var weightEl) || !weightEl.TryGetSingle(out var weight)) continue;

            if (meshes is null || meshIdx < 0 || meshIdx >= meshes.Count) continue;
            var meshName = meshes[meshIdx].Name ?? $"Mesh{meshIdx}";

            // VRM 0.x weight is 0-100; normalize to 0-1
            result.Add(new VrmMorphTargetBind(meshName, morphIdx, weight / 100f));
        }

        return result;
    }

    private static VrmLookAt? ParseFirstPerson0(JsonElement vrm0)
    {
        if (!vrm0.TryGetProperty("firstPerson", out _))
        {
            return null;
        }

        return new VrmLookAt("bone");
    }

    private static VrmSpringBoneInfo ParseSecondaryAnimation0(JsonElement vrm0)
    {
        if (!vrm0.TryGetProperty("secondaryAnimation", out var sec))
        {
            return new VrmSpringBoneInfo(0, 0);
        }

        var joints = 0;
        var colliders = 0;

        if (sec.TryGetProperty("boneGroups", out var bg) && bg.ValueKind == JsonValueKind.Array)
        {
            foreach (var group in bg.EnumerateArray())
            {
                if (group.TryGetProperty("bones", out var bones) && bones.ValueKind == JsonValueKind.Array)
                {
                    joints += bones.GetArrayLength();
                }
            }
        }

        if (sec.TryGetProperty("colliderGroups", out var cg) && cg.ValueKind == JsonValueKind.Array)
        {
            foreach (var group in cg.EnumerateArray())
            {
                if (group.TryGetProperty("colliders", out var col) && col.ValueKind == JsonValueKind.Array)
                {
                    colliders += col.GetArrayLength();
                }
            }
        }

        return new VrmSpringBoneInfo(joints, colliders);
    }
}
