using DesktopPet.Models.Live2D;
using DesktopPet.Rendering.D3D11;

internal static class Live2DRenderItemMapper
{
    public static D3D11RenderItem[] ToRenderItems(
        IReadOnlyList<Live2DDrawableSnapshot> snapshots,
        IReadOnlyList<string> texturePaths)
    {
        var items = new D3D11RenderItem[snapshots.Count];
        for (var i = 0; i < snapshots.Count; i++)
        {
            var snapshot = snapshots[i];
            items[i] = new D3D11RenderItem(
                snapshot.Index,
                snapshot.Id,
                snapshot.TextureIndex,
                snapshot.TextureIndex >= 0 && snapshot.TextureIndex < texturePaths.Count
                    ? texturePaths[snapshot.TextureIndex]
                    : null,
                snapshot.DrawOrder,
                snapshot.RenderOrder,
                snapshot.BlendMode,
                snapshot.IsDoubleSided,
                snapshot.IsInvertedMask,
                snapshot.IsVisible,
                snapshot.Opacity,
                snapshot.VertexPositions,
                snapshot.VertexUvs,
                snapshot.Indices,
                snapshot.MaskIndices);
        }

        return items;
    }
}
