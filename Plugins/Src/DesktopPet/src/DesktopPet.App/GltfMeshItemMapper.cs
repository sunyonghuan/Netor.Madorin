using System.Numerics;
using DesktopPet.Models.Gltf;
using DesktopPet.Rendering.D3D11;

internal static class GltfMeshItemMapper
{
    public static D3D11MeshItem[] ToMeshItems(GltfModel model, IReadOnlyDictionary<int, Matrix4x4>? worldMatrices = null)
    {
        var primitives = model.ExtractMeshPrimitives();
        var items = new D3D11MeshItem[primitives.Count];
        for (var i = 0; i < primitives.Count; i++)
        {
            items[i] = ToMeshItem(model, primitives[i], worldMatrices, expressionEvaluator: null);
        }
        return items;
    }

    public static (D3D11MeshItem[] Items, int[] NodeIndices) ToBaseItems(
        GltfModel model,
        VrmExpressionEvaluator? expressionEvaluator = null)
    {
        var primitives = model.ExtractMeshPrimitives();
        var items = new D3D11MeshItem[primitives.Count];
        var nodeIndices = new int[primitives.Count];
        for (var i = 0; i < primitives.Count; i++)
        {
            items[i] = ToMeshItem(model, primitives[i], worldMatrices: null, expressionEvaluator);
            nodeIndices[i] = primitives[i].NodeIndex;
        }
        return (items, nodeIndices);
    }

    private static D3D11MeshItem ToMeshItem(
        GltfModel model,
        GltfMeshPrimitive primitive,
        IReadOnlyDictionary<int, Matrix4x4>? worldMatrices,
        VrmExpressionEvaluator? expressionEvaluator)
    {
        var vertexCount = primitive.VertexCount;
        var vertices = new D3D11MeshVertex[vertexCount];
        var normals = primitive.Normals;
        var hasNormals = normals is not null && normals.Length >= vertexCount * 3;
        var texCoords = primitive.TexCoords;
        var hasUvs = texCoords is not null && texCoords.Length >= vertexCount * 2;
        var jointIndices = primitive.JointIndices;
        var weights = primitive.Weights;

        // Apply morph-target blending when an expression evaluator is active
        var positions = primitive.Positions;
        if (expressionEvaluator is not null)
        {
            var blended = expressionEvaluator.BlendPositions(
                primitive.MeshName, primitive.Positions, primitive.MorphPositionDeltas);
            if (blended is not null) positions = blended;
        }

        for (var i = 0; i < vertexCount; i++)
        {
            var x = positions[i * 3];
            var y = positions[i * 3 + 1];
            var z = positions[i * 3 + 2];

            float r, g, b;
            if (hasNormals)
            {
                r = normals![i * 3] * 0.5f + 0.5f;
                g = normals![i * 3 + 1] * 0.5f + 0.5f;
                b = normals![i * 3 + 2] * 0.5f + 0.5f;
            }
            else
            {
                r = g = b = 1.0f;
            }

            var u = hasUvs ? texCoords![i * 2] : 0f;
            var v = hasUvs ? texCoords![i * 2 + 1] : 0f;

            // Skinning data
            ushort j0 = 0, j1 = 0, j2 = 0, j3 = 0;
            float w0 = 1f, w1 = 0f, w2 = 0f, w3 = 0f;
            if (jointIndices is not null && weights is not null
                && jointIndices.Length >= (i + 1) * 4 && weights.Length >= (i + 1) * 4)
            {
                j0 = jointIndices[i * 4 + 0];
                j1 = jointIndices[i * 4 + 1];
                j2 = jointIndices[i * 4 + 2];
                j3 = jointIndices[i * 4 + 3];
                w0 = weights[i * 4 + 0];
                w1 = weights[i * 4 + 1];
                w2 = weights[i * 4 + 2];
                w3 = weights[i * 4 + 3];
            }

            vertices[i] = new D3D11MeshVertex(x, y, z, r, g, b, 1.0f, u, v, j0, j1, j2, j3, w0, w1, w2, w3);
        }

        // Include morph version in the ID so GPU buffer cache is invalidated when expressions change
        var id = $"{primitive.MeshName}[{primitive.PrimitiveIndex}]";
        if (expressionEvaluator is not null
            && expressionEvaluator.HasActiveMorphs(primitive.MeshName))
        {
            id = $"{id}#m{expressionEvaluator.Version}";
        }

        var texture = TryCreateTexture(model, primitive.MaterialIndex, id);
        var colorFactor = model.GetBaseColorFactor(primitive.MaterialIndex);
        var baseColorFactor = new Vector4(colorFactor.R, colorFactor.G, colorFactor.B, colorFactor.A);

        Matrix4x4 world = Matrix4x4.Identity;
        worldMatrices?.TryGetValue(primitive.NodeIndex, out world);

        return new D3D11MeshItem(id, vertices, primitive.Indices, world, texture, baseColorFactor);
    }

    private static PngImage? DecodeImage(byte[] bytes)
    {
        if (bytes.Length < 2) return null;
        if (bytes[0] == 0x89 && bytes[1] == 0x50) return PngRgbaDecoder.Decode(bytes);  // PNG (\x89P)
        if (bytes[0] == 0xFF && bytes[1] == 0xD8) return JpegRgbaDecoder.Decode(bytes); // JPEG SOI
        return null;
    }

    private static D3D11MeshTexture? TryCreateTexture(GltfModel model, int? materialIndex, string meshId)
    {
        var imageIndex = model.GetBaseColorImageIndex(materialIndex);
        if (imageIndex is null)
        {
            return null;
        }

        var imageBytes = model.ExtractImageBytes(imageIndex.Value);
        if (imageBytes is null || imageBytes.Length == 0)
        {
            return null;
        }

        try
        {
            var img = DecodeImage(imageBytes);
            if (img is null) return null;
            var cacheKey = $"{model.Summary.SourcePath}:img{imageIndex.Value}:{meshId}";
            var colorFactor = model.GetBaseColorFactor(materialIndex);
            return new D3D11MeshTexture(
                cacheKey,
                img.Width,
                img.Height,
                img.RgbaPixels,
                new Vector4(colorFactor.R, colorFactor.G, colorFactor.B, colorFactor.A));
        }
        catch
        {
            return null;
        }
    }
}
