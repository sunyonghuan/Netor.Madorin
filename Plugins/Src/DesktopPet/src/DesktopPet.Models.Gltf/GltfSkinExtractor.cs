using System.Buffers.Binary;
using System.Numerics;

namespace DesktopPet.Models.Gltf;

/// <summary>Extracts inverse-bind matrices from glTF skin accessors.</summary>
internal static class GltfSkinExtractor
{
    private const int ComponentTypeFloat = 5126;

    /// <summary>
    /// Returns an array of inverse-bind matrices for all joints in the given skin.
    /// If the skin has no inverseBindMatrices accessor, returns identity matrices.
    /// </summary>
    public static Matrix4x4[] ExtractInverseBindMatrices(GltfManifest manifest, byte[] binaryChunk, int skinIndex)
    {
        var skins = manifest.Skins;
        if (skins is null || skinIndex < 0 || skinIndex >= skins.Count)
            return [];

        var skin = skins[skinIndex];
        var jointCount = skin.Joints?.Count ?? 0;
        if (jointCount == 0)
            return [];

        // If no accessor, use identity (glTF spec: defaults to identity per joint)
        if (!skin.InverseBindMatrices.HasValue)
        {
            var identities = new Matrix4x4[jointCount];
            for (var i = 0; i < jointCount; i++)
                identities[i] = Matrix4x4.Identity;
            return identities;
        }

        var accessors = manifest.Accessors;
        var bufferViews = manifest.BufferViews;
        if (accessors is null || bufferViews is null)
            return [];

        var accessorIndex = skin.InverseBindMatrices.Value;
        if (accessorIndex < 0 || accessorIndex >= accessors.Count)
            return [];

        var accessor = accessors[accessorIndex];
        // inverseBindMatrices must be MAT4 float
        if (accessor.ComponentType != ComponentTypeFloat || accessor.Type != "MAT4")
            return [];

        if (!accessor.BufferView.HasValue)
            return [];

        var bvIndex = accessor.BufferView.Value;
        if (bvIndex < 0 || bvIndex >= bufferViews.Count)
            return [];

        var bv = bufferViews[bvIndex];
        var byteOffset = (bv.ByteOffset ?? 0) + (accessor.ByteOffset ?? 0);
        var byteLength = bv.ByteLength;

        if (byteOffset + byteLength > binaryChunk.Length)
            return [];

        var span = binaryChunk.AsSpan(byteOffset, byteLength);

        // Each MAT4 is 16 floats = 64 bytes (column-major in glTF)
        const int mat4Size = 64;
        var stride = bv.ByteStride ?? mat4Size;
        var count = Math.Min(accessor.Count, jointCount);
        var result = new Matrix4x4[jointCount];

        // Fill any joints beyond accessor.Count with identity
        for (var i = count; i < jointCount; i++)
            result[i] = Matrix4x4.Identity;

        for (var i = 0; i < count; i++)
        {
            var off = i * stride;
            // glTF MAT4 is column-major: 16 floats, columns first.
            // System.Numerics.Matrix4x4 constructor takes rows: (M11,M12,M13,M14, M21...).
            // glTF col[c][r] = data[c*4+r]  →  .NET M[row+1][col+1] = data[col*4+row]
            var f0  = BinaryPrimitives.ReadSingleLittleEndian(span.Slice(off +  0, 4));
            var f1  = BinaryPrimitives.ReadSingleLittleEndian(span.Slice(off +  4, 4));
            var f2  = BinaryPrimitives.ReadSingleLittleEndian(span.Slice(off +  8, 4));
            var f3  = BinaryPrimitives.ReadSingleLittleEndian(span.Slice(off + 12, 4));
            var f4  = BinaryPrimitives.ReadSingleLittleEndian(span.Slice(off + 16, 4));
            var f5  = BinaryPrimitives.ReadSingleLittleEndian(span.Slice(off + 20, 4));
            var f6  = BinaryPrimitives.ReadSingleLittleEndian(span.Slice(off + 24, 4));
            var f7  = BinaryPrimitives.ReadSingleLittleEndian(span.Slice(off + 28, 4));
            var f8  = BinaryPrimitives.ReadSingleLittleEndian(span.Slice(off + 32, 4));
            var f9  = BinaryPrimitives.ReadSingleLittleEndian(span.Slice(off + 36, 4));
            var f10 = BinaryPrimitives.ReadSingleLittleEndian(span.Slice(off + 40, 4));
            var f11 = BinaryPrimitives.ReadSingleLittleEndian(span.Slice(off + 44, 4));
            var f12 = BinaryPrimitives.ReadSingleLittleEndian(span.Slice(off + 48, 4));
            var f13 = BinaryPrimitives.ReadSingleLittleEndian(span.Slice(off + 52, 4));
            var f14 = BinaryPrimitives.ReadSingleLittleEndian(span.Slice(off + 56, 4));
            var f15 = BinaryPrimitives.ReadSingleLittleEndian(span.Slice(off + 60, 4));

            // Columns of glTF → rows of .NET (transpose the column-major layout)
            result[i] = new Matrix4x4(
                f0,  f4,  f8,  f12,   // row 0
                f1,  f5,  f9,  f13,   // row 1
                f2,  f6,  f10, f14,   // row 2
                f3,  f7,  f11, f15);  // row 3
        }

        return result;
    }
}
